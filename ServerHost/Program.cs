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

static string ScratchRoot(IConfiguration cfg)
    => cfg["GALAXIES_SCRATCH_ROOT"] ?? Path.Combine(Path.GetTempPath(), "galaxies");

static IGameStore BuildStore(IConfiguration cfg)
{
    // Local development: point at a folder, no cloud needed.
    string? localRoot = cfg["GALAXIES_LOCAL_ROOT"];
    if (!string.IsNullOrEmpty(localRoot))
    {
        return new LocalGameStore(localRoot);
    }

    // Cloud: three GCS buckets from configuration (design Section B.3).
    string state = cfg["GALAXIES_STATE_BUCKET"] ?? throw new InvalidOperationException("GALAXIES_STATE_BUCKET is required");
    string orders = cfg["GALAXIES_ORDERS_BUCKET"] ?? throw new InvalidOperationException("GALAXIES_ORDERS_BUCKET is required");
    string intel = cfg["GALAXIES_INTEL_BUCKET"] ?? throw new InvalidOperationException("GALAXIES_INTEL_BUCKET is required");
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
