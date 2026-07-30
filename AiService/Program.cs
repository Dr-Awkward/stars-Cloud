// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.
//
// galaxies-ai: the AI participant runner (spec Documentation/Cloud/specs/galaxies-ai.md).
// It runs one AI seat's turn: read that seat's own fog-of-war intel, ask a
// participant for orders, validate them, and submit them through the same
// galaxies-api pipeline a human client uses. On any failure the seat falls back
// to held orders so the turn generates on schedule regardless.
//
// Port 8082. Every flag ships off; with AI_DISPATCH_ENABLED false the worker
// records held and submits nothing, which is the kill switch.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Galaxies.AiService;
using Galaxies.AiService.ApiClient;
using Galaxies.AiService.Dispatch;
using Galaxies.AiService.Firestore;
using Galaxies.AiService.Harness;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Eventing;
using Galaxies.ControlPlane.Firestore;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using Google.Cloud.Tasks.V2;

// Unqualified Task is the framework task type; the Cloud Tasks message type is
// not used here by its short name. Same pattern as the deadline scheduler.
using Task = System.Threading.Tasks.Task;

var builder = WebApplication.CreateBuilder(args);
IConfiguration cfg = builder.Configuration;

AiServiceConfig config = AiServiceConfig.FromConfiguration(cfg);
builder.Services.AddSingleton(config);

// ---- GCP clients (lazy: constructed on first resolve, not at startup, so
// ---- /healthz answers on a box with no credentials) --------------------------
builder.Services.AddSingleton(sp => FirestoreDb.Create(config.ProjectId));
builder.Services.AddSingleton(sp => StorageClient.Create());
builder.Services.AddSingleton(sp => CloudTasksClient.Create());

builder.Services.AddSingleton<IOidcTokenSource, GoogleOidcTokenSource>();
builder.Services.AddSingleton(sp => new AiStore(sp.GetRequiredService<FirestoreDb>()));
builder.Services.AddSingleton(sp => new RunLock(sp.GetRequiredService<AiStore>()));
builder.Services.AddSingleton<IControlPlane>(sp => new FirestoreControlPlane(
    sp.GetRequiredService<FirestoreDb>(),
    sp.GetRequiredService<ILogger<FirestoreControlPlane>>()));

// One HttpClient per outbound peer. Participant calls get no ambient timeout,
// because ParticipantClient enforces the manifest's wall-clock cap itself and two
// competing timeouts would make the failure mode depend on which fired first.
builder.Services.AddHttpClient("participant", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("api", c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton(sp => new ParticipantClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("participant"),
    sp.GetRequiredService<IOidcTokenSource>(),
    sp.GetRequiredService<ILogger<ParticipantClient>>()));
builder.Services.AddSingleton(sp => new OrderSubmitClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"),
    sp.GetRequiredService<IOidcTokenSource>(),
    config,
    sp.GetRequiredService<ILogger<OrderSubmitClient>>()));

builder.Services.AddSingleton(sp => new TaskEnqueuer(
    sp.GetRequiredService<CloudTasksClient>(), config, sp.GetRequiredService<ILogger<TaskEnqueuer>>()));
builder.Services.AddSingleton(sp => new DispatchWorker(
    sp.GetRequiredService<AiStore>(),
    sp.GetRequiredService<RunLock>(),
    sp.GetRequiredService<StorageClient>(),
    sp.GetRequiredService<ParticipantClient>(),
    sp.GetRequiredService<OrderSubmitClient>(),
    sp.GetRequiredService<IControlPlane>(),
    config,
    sp.GetRequiredService<ILogger<DispatchWorker>>()));
builder.Services.AddSingleton(sp => new EventHandlers(
    sp.GetRequiredService<AiStore>(),
    sp.GetRequiredService<TaskEnqueuer>(),
    sp.GetRequiredService<IControlPlane>(),
    config,
    sp.GetRequiredService<ILogger<EventHandlers>>()));
builder.Services.AddSingleton(sp => new ReplayEndpoint(
    sp.GetRequiredService<AiStore>(),
    sp.GetRequiredService<ParticipantClient>(),
    sp.GetRequiredService<StorageClient>(),
    config,
    sp.GetRequiredService<ILogger<ReplayEndpoint>>()));

var app = builder.Build();

// ============================ Health ==========================================
// Liveness needs no dependency; readiness reports whether Firestore answers,
// because a runner that cannot reach Firestore cannot take a run lock and would
// silently replay seats.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (HttpContext ctx, CancellationToken ct) =>
{
    // Any failure is "not ready", never a 500. Resolving AiStore constructs the
    // Firestore client, and that throws outright when application default
    // credentials are absent, so the unguarded version returned 500 for the exact
    // condition this probe exists to report. The distinction matters to the
    // platform: 503 means not ready, keep the revision and retry, while 500 reads
    // as a broken handler.
    try
    {
        bool firestore = await ctx.RequestServices.GetRequiredService<AiStore>().PingAsync(ct);
        return firestore
            ? Results.Ok(new { status = "ready", firestore = true })
            : Results.Json(new { status = "degraded", firestore = false }, statusCode: 503);
    }
    catch (Exception e)
    {
        app.Logger.LogWarning(e, "Readiness check failed to reach Firestore.");
        return Results.Json(new { status = "degraded", firestore = false }, statusCode: 503);
    }
});

// ============================ Internal (OIDC via Cloud Run IAM) ===============
// Every route below is private: Cloud Run is deployed --ingress=internal and
// --allow-unauthenticated=false, so the platform requires a valid OIDC identity
// token from Cloud Tasks or Pub/Sub at the edge and no in-app bearer check is
// needed. This mirrors galaxies-api's /internal routes exactly (Api/Program.cs,
// design Section B.5). Do not add a shared secret header here.

// ---- POST /v1/dispatch: run one seat-turn -----------------------------------
app.MapPost("/v1/dispatch", async (DispatchBody body, HttpContext ctx, CancellationToken ct) =>
{
    if (!config.GalaxiesAiEnabled)
    {
        return Disabled();
    }

    if (string.IsNullOrWhiteSpace(body.GameId) || body.EmpireId <= 0)
    {
        return Results.BadRequest(new { code = "bad_request", message = "game_id and empire_id are required." });
    }

    DispatchWorker worker = ctx.RequestServices.GetRequiredService<DispatchWorker>();
    DispatchResult result = await worker.RunAsync(
        new DispatchRecord(body.GameId, body.TurnYear, body.EmpireId), ct);

    // Always 200. A held or failed seat is a handled outcome, not a transport
    // error, and returning 5xx would make Cloud Tasks retry a decision this
    // worker already made deliberately.
    return Results.Ok(result);
});

// ---- Pub/Sub push events ----------------------------------------------------
app.MapPost("/events/game-created", async (HttpContext ctx, CancellationToken ct) =>
{
    if (!config.GalaxiesAiEnabled)
    {
        return Ack();
    }

    GameCreatedEvent? evt = await ReadPushAsync<GameCreatedEvent>(ctx, ct);
    if (evt is null)
    {
        return Ack();
    }

    int pinned = await ctx.RequestServices.GetRequiredService<EventHandlers>().OnGameCreatedAsync(evt, ct);
    return Results.Ok(new { pinned });
});

app.MapPost("/events/turn-generated", async (HttpContext ctx, CancellationToken ct) =>
{
    if (!config.GalaxiesAiEnabled)
    {
        return Ack();
    }

    TurnGeneratedEvent? evt = await ReadPushAsync<TurnGeneratedEvent>(ctx, ct);
    if (evt is null)
    {
        return Ack();
    }

    int enqueued = await ctx.RequestServices.GetRequiredService<EventHandlers>().OnTurnGeneratedAsync(evt, ct);
    return Results.Ok(new { enqueued });
});

app.MapPost("/events/deadline-approaching", async (HttpContext ctx, CancellationToken ct) =>
{
    if (!config.GalaxiesAiEnabled)
    {
        return Ack();
    }

    DeadlineApproachingEvent? evt = await ReadPushAsync<DeadlineApproachingEvent>(ctx, ct);
    if (evt is null)
    {
        return Ack();
    }

    int enqueued = await ctx.RequestServices.GetRequiredService<EventHandlers>()
        .OnDeadlineApproachingAsync(evt, ct);
    return Results.Ok(new { enqueued });
});

// ---- POST /v1/replay: the harness -------------------------------------------
// Always on, even with dispatch dark (spec Section 7.2). It submits nothing.
app.MapPost("/v1/replay", async (ReplayRequest req, HttpContext ctx, CancellationToken ct) =>
{
    ReplayResponse result = await ctx.RequestServices.GetRequiredService<ReplayEndpoint>()
        .ReplayAsync(req, ct);
    return Results.Ok(result);
});

// ---- Admin: seed the one M3 participant --------------------------------------
// The registry proper is M6; this writes the single galaxies.default-ai document
// the lobby needs from first deploy (spec Section 6).
app.MapPost("/internal/seed-default-participant", async (HttpContext ctx, CancellationToken ct) =>
{
    if (!config.GalaxiesAiEnabled)
    {
        return Disabled();
    }

    await ctx.RequestServices.GetRequiredService<AiStore>()
        .SeedDefaultParticipantAsync(config.DefaultParticipantId, config.DefaultParticipantUrl, ct);
    return Results.Ok(new { seeded = config.DefaultParticipantId });
});

app.Run();

// ---- Local helpers -----------------------------------------------------------

/// <summary>
/// The off state for a read: spec Section 3.1 says reads return {"disabled":true}
/// and mutations 403. Both are true of this one result, which is why it is a 403
/// carrying the disabled body rather than one or the other.
/// </summary>
static IResult Disabled() => Results.Json(new { disabled = true }, statusCode: 403);

/// <summary>
/// Acknowledge a push without acting on it. A 200 tells Pub/Sub the message is
/// handled; a non-2xx would have it redelivered forever against a service that is
/// switched off on purpose.
/// </summary>
static IResult Ack() => Results.Ok(new { disabled = true });

/// <summary>
/// Decode a Pub/Sub push body.
///
/// Push delivery does not post the event. It posts an envelope,
/// {"message":{"data":"&lt;base64 of the event JSON&gt;","attributes":{...},
/// "messageId":"..."},"subscription":"..."}, and reading the outer object as if
/// it were the event yields an all-default event that quietly does nothing. So
/// unwrap properly, and only fall back to treating the body as a raw event when
/// there is no envelope at all, which is what a hand-run curl during a smoke
/// sends.
/// </summary>
static async Task<T?> ReadPushAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
{
    string raw;
    using (StreamReader reader = new(ctx.Request.Body, Encoding.UTF8))
    {
        raw = await reader.ReadToEndAsync(ct);
    }

    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    try
    {
        PubSubPush? push = JsonSerializer.Deserialize<PubSubPush>(raw, options);
        if (push?.Message?.Data is { Length: > 0 } data)
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            return JsonSerializer.Deserialize<T>(json, options);
        }

        // No envelope: a direct post of the event itself.
        return JsonSerializer.Deserialize<T>(raw, options);
    }
    catch (Exception)
    {
        // A malformed push is dropped, not retried. Redelivering a body that
        // will never parse just burns the subscription's retry budget.
        return null;
    }
}

/// <summary>The body Cloud Tasks posts to /v1/dispatch. snake_case, per spec Section 7.1.</summary>
public sealed record DispatchBody
{
    [JsonPropertyName("game_id")] public string GameId { get; init; } = "";

    [JsonPropertyName("turn_year")] public int TurnYear { get; init; }

    [JsonPropertyName("empire_id")] public int EmpireId { get; init; }
}

/// <summary>The Pub/Sub push envelope.</summary>
public sealed record PubSubPush
{
    [JsonPropertyName("message")] public PubSubMessage? Message { get; init; }

    [JsonPropertyName("subscription")] public string? Subscription { get; init; }
}

public sealed record PubSubMessage
{
    /// <summary>Base64 of the event JSON.</summary>
    [JsonPropertyName("data")] public string? Data { get; init; }

    [JsonPropertyName("attributes")] public Dictionary<string, string>? Attributes { get; init; }

    [JsonPropertyName("messageId")] public string? MessageId { get; init; }
}
