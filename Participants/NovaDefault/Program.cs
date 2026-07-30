// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using Galaxies.AiContract;
using Galaxies.Participant.NovaDefault;

// The built-in Nova AI participant worker (galaxies-ai spec Section 3).
//
// PORT. The Galaxies services take consecutive ports so a full local stack can
// run side by side: galaxies-api 8080, galaxies-turngen 8081, galaxies-ai 8082,
// galaxies-notifier 8083. This participant takes the next free one, 8084. Cloud
// Run injects PORT and overrides all of that, as it should.
//
// ROUTES.
//   GET  /healthz  liveness. Says nothing about whether the AI can play.
//   GET  /readyz   readiness. Reports whether the component definitions loaded,
//                  because an AI with no component database cannot cost a
//                  factory or validate a design and must not take traffic.
//   GET  /version  participant id, version, contract versions spoken.
//   POST /v1/act   the contract endpoint.
//
// EVERYTHING SHIPS DARK. PARTICIPANT_ENABLED defaults to false. Off, the read
// routes return {"disabled":true} and /v1/act returns 403. Turning this on is a
// deliberate act, not a side effect of a deploy.
//
// CONCURRENCY. Nova.Common.Components.AllComponents and GameSettings.Data are
// process-wide statics. AllComponents is written once at startup and only read
// afterwards, so it is safe under concurrent readers; the risk is the engine
// code paths that lazily touch or mutate shared statics on a request thread, and
// we have not proven the AI's whole call graph is free of those. The safe
// deployment setting is therefore Cloud Run concurrency 1, and to make that hold
// regardless of how the service is configured, this process also serializes
// /v1/act on the gate below. The manifest's max_concurrency of 20 is a fan-out
// budget across instances, not a promise that one instance handles 20 at once.
// The AI runs in milliseconds against a 60 second timeout, so serializing costs
// nothing worth having.

var builder = WebApplication.CreateBuilder(args);

// Only take the default port when nothing else has spoken. Cloud Run sets PORT;
// the Dockerfile sets ASPNETCORE_URLS; either wins over the 8084 default.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    string port = Environment.GetEnvironmentVariable("PORT") ?? "8084";
    builder.WebHost.UseUrls("http://0.0.0.0:" + port);
}

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NovaDefault");

bool enabled = Flag("PARTICIPANT_ENABLED");
int maxOrders = Positive("PARTICIPANT_MAX_ORDERS", OrderMapper.DefaultMaxOrders);

// Load the component definitions before the listener opens, so a container that
// cannot play fails its readiness check rather than failing a seat's turn. Load
// them even when the participant is disabled: readiness should tell the truth
// about the image, and finding out at flip time that components.xml never made
// it into the layer is the exact surprise this avoids.
ComponentDefinitions.Load();

if (ComponentDefinitions.Loaded)
{
    log.LogInformation(
        "Loaded {Count} component definitions from {Path}. Participant enabled: {Enabled}.",
        ComponentDefinitions.Count, ComponentDefinitions.Path, enabled);
}
else
{
    log.LogError(
        "Component definitions did not load: {Failure}. /v1/act will return no orders.",
        ComponentDefinitions.Failure);
}

// Serializes /v1/act. See the concurrency note above.
using SemaphoreSlim gate = new(1, 1);

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/readyz", () =>
{
    if (!enabled)
    {
        return Results.Ok(new { disabled = true });
    }

    if (!ComponentDefinitions.Loaded)
    {
        // 503, not 200 with a sad field. A participant that cannot cost a hull
        // should not be handed seats.
        return Results.Json(
            new
            {
                ready = false,
                components_loaded = false,
                error = ComponentDefinitions.Failure,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        ready = true,
        components_loaded = true,
        component_count = ComponentDefinitions.Count,
        component_file = ComponentDefinitions.Path,
    });
});

app.MapGet("/version", () =>
{
    if (!enabled)
    {
        return Results.Ok(new { disabled = true });
    }

    return Results.Ok(new
    {
        id = NovaDefaultParticipant.ParticipantId,
        version = NovaDefaultParticipant.ParticipantVersion,
        contract_versions = ContractVersions.Supported,
        kind = "container",
        // Honest, and it matches manifest.json. The property holds (identical
        // intel replays to identical orders); the mechanism is that the AI has
        // no randomness at all, not that it seeds any. See
        // NovaDefaultParticipant.SeedNote.
        determinism = "seeded",
        seat_seed_consumed = false,
    });
});

app.MapPost("/v1/act", async (ActRequest? request, CancellationToken ct) =>
{
    if (!enabled)
    {
        return Results.Json(
            new { error = "participant_disabled", message = "PARTICIPANT_ENABLED is false." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    await gate.WaitAsync(ct);
    try
    {
        ActOutcome outcome = NovaDefaultParticipant.Act(request, maxOrders, log);

        if (outcome.StatusCode != StatusCodes.Status200OK || outcome.Response is null)
        {
            return Results.Json(
                new { error = "bad_request", message = outcome.Error },
                statusCode: outcome.StatusCode);
        }

        return Results.Ok(outcome.Response);
    }
    catch (Exception e)
    {
        // NovaDefaultParticipant.Act already catches everything the AI can throw
        // and degrades to an empty orders list. This is the belt to that pair of
        // braces: if the adapter itself ever fails, the seat still gets a
        // well-formed empty answer and falls back to held orders rather than
        // seeing a 500 and losing the turn outright.
        log.LogError(e, "Unhandled failure in /v1/act.");

        return Results.Ok(new ActResponse
        {
            ContractVersion = ContractVersions.Current,
            RequestId = request?.RequestId ?? string.Empty,
            EmpireId = request?.Seat?.EmpireId ?? 0,
            TurnYear = request?.Game?.TurnYear ?? 0,
            Orders = new List<OrderDto>(),
            Diagnostics = new DiagnosticsDto
            {
                Notes = "The participant failed and returned no orders: " + e.Message,
                SeedUsed = request?.Game?.SeatSeed,
                ElapsedMs = 0,
            },
        });
    }
    finally
    {
        gate.Release();
    }
});

app.Run();

// ---- helpers ---------------------------------------------------------------

// A flag is on only when it says so. Anything else, including an unset variable
// and a typo, is off. That asymmetry is the point of shipping dark.
static bool Flag(string name)
    => string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

static int Positive(string name, int fallback)
{
    string? raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out int value) && value > 0
        ? value
        : fallback;
}
