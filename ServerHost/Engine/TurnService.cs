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
        /// <param name="GameEnded">
        /// Whether the ENGINE considers the universe finished. This is not the same
        /// as a game having a winner, and it is normally false even after a victory.
        /// Galaxies declares a victor and lets play continue, matching the original
        /// Stars!, so closing a game is a lifecycle decision the control plane makes
        /// when the host closes it or everyone has left.
        /// </param>
        /// <param name="WinnerEmpireId">
        /// The empire that has met a victory condition, or <see cref="Global.Nobody"/>
        /// while none has. Once set it stays set: the first victor is the victor.
        /// This is what the API commits and what the game-over summary reads.
        /// </param>
        public sealed record GenerationOutcome(
            int TurnYear,
            string NewStatePath,
            int[] EmpireIds,
            int[] AiEmpireIds,
            bool GameEnded,
            int WinnerEmpireId);

        /// <summary>
        /// Advance one game by one turn. Returns the new (advanced) turn year and
        /// the metadata the API commits. Callers hold the per-game generation lock
        /// (Section B.2) so this runs at most once per (gameId, turnYear).
        /// </summary>
        public async Task<GenerationOutcome> GenerateTurnAsync(string gameId, CancellationToken ct = default)
        {
            // The working directory name is DERIVED, not random.
            //
            // ServerData serializes GameFolder and StatePathName into the saved game
            // (upstream behaviour, and ServerStateTest depends on it), so whatever
            // this directory is called ends up in the bytes of every turn generated
            // in it. With a GUID here, generating the same turn twice produced two
            // different files. M0 exit criterion 4 compares a generated turn against
            // a committed golden BYTE FOR BYTE across .NET Framework 4.8 on Windows
            // and net10.0 on Linux, and a random path in the payload defeats that
            // before the engine is even considered, then invites a re-baseline that
            // would mask the real cross-architecture divergence the check exists to
            // find.
            //
            // Uniqueness is not lost. The name is unique per (game, turn), and the
            // exactly-once generation lock (IControlPlane.TryClaimGenerationAsync)
            // already guarantees a single worker per (gameId, turnYear), which is a
            // stronger guarantee than a GUID gave. The gameId is sanitized because it
            // is caller-supplied and contains colons in the roybot:game:hex form.
            string safeGameId = string.Concat(gameId.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-'));
            string workingDir = Path.Combine(scratchRoot, "gen-" + safeGameId);

            // A deterministic name can collide with debris from a previous attempt at
            // the same turn that died before its cleanup ran. Stale intel there would
            // otherwise be uploaded as this turn's output, so start from empty.
            if (Directory.Exists(workingDir))
            {
                Directory.Delete(workingDir, recursive: true);
            }
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

                if (serverState.WinnerEmpireId != Global.Nobody)
                {
                    // Logged every turn once a victor stands, not only on the turn it
                    // happened, because the control plane may be catching up and the
                    // declaration is a property of the game rather than of one turn.
                    log.LogInformation(
                        "Game {GameId} has a victor: empire {Winner}. Play continues until the game is closed.",
                        gameId, serverState.WinnerEmpireId);
                }

                log.LogInformation("Generated turn for game {GameId}: {Before} to {After}", gameId, yearBefore, yearAfter);
                return new GenerationOutcome(
                    yearAfter, newStatePath, empireIds, System.Array.Empty<int>(),
                    gameEnded, serverState.WinnerEmpireId);
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
