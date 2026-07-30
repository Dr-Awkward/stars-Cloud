// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using Google.Cloud.Storage.V1;
using Nova.Common;

namespace Nova.Server.Host.Storage
{
    /// <summary>
    /// A Google Cloud Storage backed store (design Section B.3). Objects are
    /// private; nothing here is ever served to a client directly. The API service is
    /// the only reader of intel, and it authorizes per empire before handing a view
    /// out.
    ///
    /// The object layout is the canonical one in <see cref="GamePaths"/>, the same
    /// paths <see cref="LocalGameStore"/> uses and the same paths galaxies-api reads
    /// and writes. Buckets come from configuration.
    /// </summary>
    public sealed class GcsGameStore : IGameStore
    {
        private readonly StorageClient storage;
        private readonly string stateBucket;
        private readonly string ordersBucket;
        private readonly string intelBucket;

        public GcsGameStore(StorageClient storage, string stateBucket, string ordersBucket, string intelBucket)
        {
            this.storage = storage;
            this.stateBucket = stateBucket;
            this.ordersBucket = ordersBucket;
            this.intelBucket = intelBucket;
        }

        public async Task DownloadStateAsync(string gameId, string workingDir, CancellationToken ct = default)
        {
            Directory.CreateDirectory(workingDir);

            // The current object points at the turn we are about to advance from.
            string statePath = Path.Combine(workingDir, gameId + Global.ServerStateExtension);
            try
            {
                await using FileStream fs = File.Create(statePath);
                await storage.DownloadObjectAsync(
                    stateBucket, GamePaths.CurrentState(gameId), fs, cancellationToken: ct);
            }
            catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Leave no truncated file behind for the engine to try to parse.
                File.Delete(statePath);
                throw new FileNotFoundException(
                    $"No current state for game {gameId} at gs://{stateBucket}/{GamePaths.CurrentState(gameId)}", e);
            }
        }

        public async Task<int> DownloadOrdersAsync(
            string gameId,
            string workingDir,
            int turnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(workingDir);

            // One listing over the turn's orders folder rather than a probe per seat:
            // a game can hold up to sixteen empires and most turns have far fewer
            // submissions than seats, so listing is both fewer round trips and
            // tolerant of a seat that never submitted.
            int staged = 0;
            await foreach (var obj in storage
                .ListObjectsAsync(ordersBucket, GamePaths.OrdersPrefix(gameId, turnYear))
                .WithCancellation(ct))
            {
                if (!obj.Name.EndsWith(Global.OrdersExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string leaf = Path.GetFileNameWithoutExtension(obj.Name);
                if (!int.TryParse(leaf, out int empireId) || !seats.TryGetValue(empireId, out string? raceName))
                {
                    // An object that is not named for a seat in this game. Skipping is
                    // the safe read: staging it could hand one empire's orders to
                    // another, which is the boundary we care most about.
                    continue;
                }

                string local = Path.Combine(workingDir, raceName + Global.OrdersExtension);
                await using FileStream fs = File.Create(local);
                await storage.DownloadObjectAsync(ordersBucket, obj.Name, fs, cancellationToken: ct);
                staged++;
            }

            return staged;
        }

        public async Task UploadResultsAsync(
            string gameId,
            string workingDir,
            int newTurnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default)
        {
            // New authoritative state, written both under its turn year (history) and
            // to the current pointer. Object generations give free history and
            // optimistic concurrency (the caller's turn lock enforces
            // ifGenerationMatch, design Section B.2).
            string newState = Path.Combine(workingDir, gameId + Global.ServerStateExtension);
            if (File.Exists(newState))
            {
                await UploadFileAsync(stateBucket, GamePaths.StateForTurn(gameId, newTurnYear), newState, ct);
                await UploadFileAsync(stateBucket, GamePaths.CurrentState(gameId), newState, ct);
            }

            // Per-empire intel for the turn just generated, renamed from the engine's
            // race-name files back to the canonical empire-id objects.
            foreach (KeyValuePair<int, string> seat in seats)
            {
                string src = Path.Combine(workingDir, seat.Value + Global.IntelExtension);
                if (File.Exists(src))
                {
                    await UploadFileAsync(intelBucket, GamePaths.Intel(gameId, newTurnYear, seat.Key), src, ct);
                }
            }

            // The engine's per-year backup subfolder (BackupTurn) is the prior turn's
            // snapshot; keep it as point-in-time recovery.
            int backedUpYear = newTurnYear - 1;
            string engineBackup = Path.Combine(workingDir, backedUpYear.ToString());
            if (Directory.Exists(engineBackup))
            {
                string prefix = GamePaths.BackupPrefix(gameId, backedUpYear);
                foreach (string f in Directory.GetFiles(engineBackup))
                {
                    await UploadFileAsync(stateBucket, prefix + Path.GetFileName(f), f, ct);
                }
            }
        }

        private async Task UploadFileAsync(string bucket, string objectName, string path, CancellationToken ct)
        {
            await using FileStream fs = File.OpenRead(path);
            await storage.UploadObjectAsync(bucket, objectName, "application/xml", fs, cancellationToken: ct);
        }
    }
}
