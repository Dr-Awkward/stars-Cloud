// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using Nova.Common;
using Nova.Server;                 // TurnGenerator
using Nova.Server.Host.Storage;

namespace Nova.Server.Host.Engine
{
    /// <summary>
    /// Generates exactly one turn for one game, headless. This is the container's
    /// whole job in M0: load a game's state and orders from the store, advance the
    /// universe one year with the existing engine, write the new state and each
    /// empire's intel back, and stop.
    ///
    /// M0 uses a working-directory shim: the engine still reads and writes files
    /// through its GameFolder seam, but that folder is a scratch directory we
    /// hydrate from the store and dehydrate back afterward. It touches no engine
    /// internals. The M1 target (Section A.5) refactors OrderReader and IntelWriter
    /// onto streams so no scratch directory exists; this class is where that swap
    /// lands, behind the same GenerateTurnAsync signature.
    /// </summary>
    public sealed class TurnService
    {
        private readonly IGameStore store;
        private readonly ILogger<TurnService> log;
        private readonly string scratchRoot;

        public TurnService(IGameStore store, ILogger<TurnService> log, string scratchRoot)
        {
            this.store = store;
            this.log = log;
            this.scratchRoot = scratchRoot;
        }

        /// <summary>
        /// The outcome galaxies-api needs to commit a turn (design Sections B.2,
        /// B.4): the new authoritative state path, who is in the game (for the
        /// turn-generated event), and whether the game ended.
        /// </summary>
        public sealed record GenerationOutcome(
            int TurnYear,
            string NewStatePath,
            int[] EmpireIds,
            int[] AiEmpireIds,
            bool GameEnded);

        /// <summary>
        /// Advance one game by one turn. Returns the new (advanced) turn year and
        /// the metadata the API commits. Callers hold the per-game generation lock
        /// (Section B.2) so this runs at most once per (gameId, turnYear).
        /// </summary>
        public async Task<GenerationOutcome> GenerateTurnAsync(string gameId, CancellationToken ct = default)
        {
            string workingDir = Path.Combine(scratchRoot, "gen-" + gameId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDir);
            try
            {
                await store.DownloadStateAsync(gameId, workingDir, ct);

                // Load the universe. Restore reads from StatePathName and rebuilds
                // the object graph (LinkServerStateReferences). We override the
                // stale GameFolder baked into the save with our scratch directory,
                // so every file seam (ReadOrders, WriteIntel, BackupTurn,
                // CleanupOrders) operates inside workingDir.
                ServerData serverState = new ServerData();
                serverState.StatePathName = Path.Combine(workingDir, gameId + Global.ServerStateExtension);
                serverState.Restore();
                serverState.GameFolder = workingDir;
                serverState.StatePathName = Path.Combine(workingDir, gameId + Global.ServerStateExtension);

                int yearBefore = serverState.TurnYear;

                // Orders are stored per empire id and read by the engine per race
                // name, so the roster has to come from the loaded universe. That is
                // why the state and the orders are fetched in two steps: neither the
                // turn year nor this map exists before Restore.
                IReadOnlyDictionary<int, string> seats = serverState.AllEmpires
                    .ToDictionary(e => e.Key, e => e.Value.Race.Name);

                int staged = await store.DownloadOrdersAsync(gameId, workingDir, yearBefore, seats, ct);
                log.LogInformation(
                    "Staged {Staged} of {Seats} orders files for game {GameId}, year {Year}",
                    staged, seats.Count, gameId, yearBefore);

                // Determinism (Section A.4). The engine's RNG becomes seeded from
                // serverState.MasterSeed during the port; the seed rule lives in
                // SeedDerivation so the host and the engine agree. Once the engine
                // edit lands, this whole generation is reproducible.
                log.LogInformation("Generating turn for game {GameId}, year {Year}", gameId, yearBefore);

                new TurnGenerator(serverState).Generate();

                int yearAfter = serverState.TurnYear;   // engine increments TurnYear
                serverState.Save();                       // writes to StatePathName (no dialog: it is set)

                await store.UploadResultsAsync(gameId, workingDir, yearAfter, seats, ct);

                // Report what the API needs to commit the turn and fan the result
                // out. The new state lands at the store's per-turn path; empire ids
                // come from the generated universe; AI seats arrive in M3.
                int[] empireIds = serverState.AllEmpires.Keys.ToArray();
                bool gameEnded = !serverState.GameInProgress;
                string newStatePath = GamePaths.StateForTurn(gameId, yearAfter);

                log.LogInformation("Generated turn for game {GameId}: {Before} to {After}", gameId, yearBefore, yearAfter);
                return new GenerationOutcome(yearAfter, newStatePath, empireIds, System.Array.Empty<int>(), gameEnded);
            }
            finally
            {
                TryCleanup(workingDir);
            }
        }

        private void TryCleanup(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not remove scratch directory {Dir}", dir);
            }
        }
    }
}
