// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using Nova.Common;

namespace Nova.Server.Host.Storage
{
    /// <summary>
    /// A filesystem-backed store. It needs no cloud credentials, so a developer can
    /// run a full turn on their machine, and it is also the production store when
    /// game files live on a mounted volume rather than in a bucket directly.
    ///
    /// The layout under <c>root</c> is the canonical one in <see cref="GamePaths"/>,
    /// byte for byte the same as the bucket layout. That is deliberate: when the
    /// same paths serve local development and production, the path handling is
    /// exercised every time anyone runs a turn on a laptop, instead of only being
    /// exercised in the cloud where nobody can see it.
    ///
    /// Note that the directory this points at must be durable. A container
    /// filesystem is not: on Cloud Run it is in-memory, per-instance, and destroyed
    /// when the instance goes away, so pointing this at a plain container path
    /// silently loses every game. It needs a mounted volume.
    /// </summary>
    public sealed class LocalGameStore : IGameStore
    {
        private readonly string root;

        public LocalGameStore(string root)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
        }

        private string Absolute(string relative)
            => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        public Task DownloadStateAsync(string gameId, string workingDir, CancellationToken ct = default)
        {
            Directory.CreateDirectory(workingDir);

            string stateSrc = Absolute(GamePaths.CurrentState(gameId));
            if (!File.Exists(stateSrc))
            {
                throw new FileNotFoundException($"No current state for game {gameId} at {stateSrc}");
            }

            // The engine loads state from StatePathName; name it by game id so the
            // working directory holds exactly one .sstate.
            File.Copy(stateSrc, Path.Combine(workingDir, gameId + Global.ServerStateExtension), overwrite: true);
            return Task.CompletedTask;
        }

        public Task<int> DownloadOrdersAsync(
            string gameId,
            string workingDir,
            int turnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(workingDir);

            int staged = 0;
            foreach (KeyValuePair<int, string> seat in seats)
            {
                string src = Absolute(GamePaths.Orders(gameId, turnYear, seat.Key));
                if (!File.Exists(src))
                {
                    // This seat did not submit. The engine treats a missing orders
                    // file as "hold", which is the correct outcome.
                    continue;
                }

                // Renamed to what OrderReader opens. OrderReader still validates the
                // file's own turn year and empire id, so a stale or mistargeted file
                // is rejected there, not here.
                File.Copy(src, Path.Combine(workingDir, seat.Value + Global.OrdersExtension), overwrite: true);
                staged++;
            }

            return Task.FromResult(staged);
        }

        public Task UploadResultsAsync(
            string gameId,
            string workingDir,
            int newTurnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default)
        {
            // New authoritative state, written under its turn year for history and to
            // the current pointer for the next generation to pick up.
            string newState = Path.Combine(workingDir, gameId + Global.ServerStateExtension);
            if (File.Exists(newState))
            {
                CopyTo(newState, GamePaths.StateForTurn(gameId, newTurnYear));
                CopyTo(newState, GamePaths.CurrentState(gameId));
            }

            // Per-empire intel for the turn just generated, renamed from the engine's
            // race-name files back to the canonical empire-id objects.
            foreach (KeyValuePair<int, string> seat in seats)
            {
                string src = Path.Combine(workingDir, seat.Value + Global.IntelExtension);
                if (File.Exists(src))
                {
                    CopyTo(src, GamePaths.Intel(gameId, newTurnYear, seat.Key));
                }
            }

            // The engine's per-year backup subfolder (BackupTurn) is the prior turn's
            // snapshot; keep it as point-in-time recovery.
            int backedUpYear = newTurnYear - 1;
            string engineBackup = Path.Combine(workingDir, backedUpYear.ToString());
            if (Directory.Exists(engineBackup))
            {
                string dest = Absolute(GamePaths.BackupPrefix(gameId, backedUpYear));
                Directory.CreateDirectory(dest);
                foreach (string f in Directory.GetFiles(engineBackup))
                {
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
                }
            }

            return Task.CompletedTask;
        }

        private void CopyTo(string sourcePath, string relativeDestination)
        {
            string dest = Absolute(relativeDestination);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(sourcePath, dest, overwrite: true);
        }
    }
}
