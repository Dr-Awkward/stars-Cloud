// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using Google.Cloud.Storage.V1;
using Nova.Server.Host.Engine;
using Nova.Server.Host.Storage;

// Two ways to run the M0 host:
//
//   1. As a container service (default): a private HTTP endpoint the internal
//      trigger calls to generate one turn.
//        POST /internal/games/{gameId}/generate  -> { "gameId", "turnYear" }
//        GET  /healthz                            -> 200
//
//   2. As a one-shot CLI, for local development against LocalGameStore:
//        Nova.Server.Host generate <gameId>
//      Runs a single turn against GALAXIES_LOCAL_ROOT and exits.
//
// There is no auth and no scheduler here; that is M1 and M2. M0 only proves that
// the containerized engine can take a game from storage, advance one turn, and
// write per-empire intel back.

if (args.Length >= 2 && args[0] == "generate")
{
    await RunCliOnce(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IGameStore>(sp => BuildStore(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<TurnService>(sp => new TurnService(
    sp.GetRequiredService<IGameStore>(),
    sp.GetRequiredService<ILogger<TurnService>>(),
    ScratchRoot(sp.GetRequiredService<IConfiguration>())));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

// Private endpoint: generate one turn. In M2 this is invoked by Cloud Tasks with
// an OIDC token at the game's deadline; in M0 it is called by hand or a test.
app.MapPost("/internal/games/{gameId}/generate", async (
    string gameId, TurnService turns, CancellationToken ct) =>
{
    try
    {
        // Returns the outcome galaxies-api commits: new state path, empire ids,
        // and whether the game ended (design Sections B.2, B.4).
        var outcome = await turns.GenerateTurnAsync(gameId, ct);
        return Results.Ok(outcome);
    }
    catch (FileNotFoundException e)
    {
        return Results.NotFound(new { gameId, error = e.Message });
    }
});

app.Run();


// ---- helpers ----------------------------------------------------------------

// appsettings.json ships every GALAXIES_* key as an empty string, deliberately, to
// document what has to be supplied at deploy time. That makes `??` the wrong
// operator throughout this file: an empty string is not null, so the fallback never
// fires and the empty value wins.
//
// It cost a turn. ScratchRoot returned "", Path.Combine("", "gen-...") produced a
// RELATIVE path, it resolved against the image's WORKDIR of /app, and creating it
// as uid 10001 failed with UnauthorizedAccessException. Every HTTP generate request
// returned 500, including the one for a game that does not exist, while the CLI path
// kept working because it calls Path.GetTempPath() directly. Treat blank as absent.
static string? Setting(IConfiguration cfg, string key)
{
    string? value = cfg[key];
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static string ScratchRoot(IConfiguration cfg)
{
    string root = Setting(cfg, "GALAXIES_SCRATCH_ROOT") ?? Path.Combine(Path.GetTempPath(), "galaxies");

    // A relative scratch root resolves against the working directory, which in the
    // container is /app and is not writable by the non-root user. Fail loudly here
    // rather than at the first generate request.
    if (!Path.IsPathRooted(root))
    {
        throw new InvalidOperationException(
            $"GALAXIES_SCRATCH_ROOT must be an absolute path; got '{root}'.");
    }

    return root;
}

static IGameStore BuildStore(IConfiguration cfg)
{
    // Local development, or production with game files on a mounted volume: point
    // at a directory, no cloud needed. Note that the directory must be durable. A
    // container filesystem is not: on Cloud Run it is in-memory, per-instance, and
    // destroyed at scale to zero, so this needs a volume mount rather than a plain
    // container path.
    string? localRoot = Setting(cfg, "GALAXIES_LOCAL_ROOT");
    if (localRoot is not null)
    {
        return new LocalGameStore(localRoot);
    }

    // Cloud: three GCS buckets from configuration (design Section B.3).
    string state = Setting(cfg, "GALAXIES_STATE_BUCKET") ?? throw new InvalidOperationException("GALAXIES_STATE_BUCKET is required");
    string orders = Setting(cfg, "GALAXIES_ORDERS_BUCKET") ?? throw new InvalidOperationException("GALAXIES_ORDERS_BUCKET is required");
    string intel = Setting(cfg, "GALAXIES_INTEL_BUCKET") ?? throw new InvalidOperationException("GALAXIES_INTEL_BUCKET is required");
    return new GcsGameStore(StorageClient.Create(), state, orders, intel);
}

static async Task RunCliOnce(string gameId)
{
    string root = Environment.GetEnvironmentVariable("GALAXIES_LOCAL_ROOT")
        ?? throw new InvalidOperationException("Set GALAXIES_LOCAL_ROOT to a local games folder for CLI runs.");
    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    var store = new LocalGameStore(root);
    var svc = new TurnService(store, loggerFactory.CreateLogger<TurnService>(),
        Path.Combine(Path.GetTempPath(), "galaxies"));
    var outcome = await svc.GenerateTurnAsync(gameId);
    Console.WriteLine($"Generated turn for {gameId}: new year {outcome.TurnYear}");
}
