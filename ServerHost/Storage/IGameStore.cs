// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

namespace Nova.Server.Host.Storage
{
    /// <summary>
    /// Where a game's files live. This is the seam that replaces the shared folder
    /// from the desktop game.
    ///
    /// One canonical object layout, shared by every implementation and by
    /// galaxies-api (see Api/Storage/ObjectStores.cs). Paths are relative to a
    /// bucket or a local root:
    ///
    ///   games/{gameId}/state/{turnYear}.sstate
    ///   games/{gameId}/state/current.sstate
    ///   games/{gameId}/orders/{turnYear}/{empireId}.orders
    ///   games/{gameId}/intel/{turnYear}/{empireId}.intel
    ///   games/{gameId}/backup/{turnYear}/
    ///
    /// Objects are keyed by EMPIRE ID, never by race name. Race names are
    /// player-supplied strings: they can carry spaces and punctuation, they are not
    /// guaranteed unique, and a player can change one. Empire ids are stable
    /// integers and are what the API authorizes on, so they are the only safe key.
    /// The engine, by contrast, reads and writes {raceName}.orders and
    /// {raceName}.intel inside its game folder, because that is what OrderReader
    /// and IntelWriter have always done and the golden turn depends on it.
    ///
    /// Translating between the two is this seam's job, which is why the download
    /// is two calls rather than one: the empire-id-to-race-name roster only exists
    /// once the state file has been loaded, and the orders path needs the turn year
    /// that comes from the same place.
    ///
    /// The M1 target (design Section A.5) is a stream-based store so the engine
    /// never touches a filesystem at all: OpenState / CreateIntel / OpenOrders /
    /// ArchiveTurn / DeleteOrders. That refactor lands when OrderReader and
    /// IntelWriter come off GameFolder, and it is intentionally not part of M0.
    /// </summary>
    public interface IGameStore
    {
        /// <summary>
        /// Copy the game's current authoritative state into
        /// <paramref name="workingDir"/> as a single &lt;gameId&gt;.sstate, which is
        /// where the engine's StatePathName points.
        /// </summary>
        /// <exception cref="FileNotFoundException">
        /// The game has no current state. This is a real failure, not an empty
        /// result: a turn cannot be generated for a game that was never created.
        /// </exception>
        Task DownloadStateAsync(string gameId, string workingDir, CancellationToken ct = default);

        /// <summary>
        /// Copy every orders file submitted for <paramref name="turnYear"/> into
        /// <paramref name="workingDir"/>, renaming each from its canonical
        /// {empireId}.orders to the {raceName}.orders that OrderReader opens.
        /// </summary>
        /// <param name="seats">
        /// Empire id to race name, from the loaded universe. An orders object for an
        /// empire that is not in this map is skipped rather than staged: it belongs
        /// to a seat this game does not have.
        /// </param>
        /// <returns>How many orders files were staged. Zero is legitimate; a turn
        /// can generate with nobody having submitted, and every empire then holds.
        /// </returns>
        Task<int> DownloadOrdersAsync(
            string gameId,
            string workingDir,
            int turnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default);

        /// <summary>
        /// Push the results of a generated turn back to durable storage: the new
        /// state file, every per-empire intel the engine wrote (renamed from
        /// {raceName}.intel back to the canonical {empireId}.intel), and the
        /// per-turn backup the engine snapshotted. Consumed orders are already
        /// deleted from <paramref name="workingDir"/> by the engine's CleanupOrders.
        /// </summary>
        /// <param name="seats">Empire id to race name, the same map used to stage
        /// the orders, used here in reverse.</param>
        Task UploadResultsAsync(
            string gameId,
            string workingDir,
            int newTurnYear,
            IReadOnlyDictionary<int, string> seats,
            CancellationToken ct = default);
    }

    /// <summary>
    /// The one place the canonical object layout is spelled out. Both stores build
    /// their paths here so the two can never drift apart again, which is exactly
    /// what happened when each wrote its own scheme: the API wrote orders to
    /// games/{gameId}/orders/{turnYear}/{empireId}.orders while the game store read
    /// games/{gameId}/orders/current/, so every submitted order was silently
    /// ignored and turns generated as though nobody had moved.
    /// </summary>
    public static class GamePaths
    {
        public static string StateForTurn(string gameId, int turnYear)
            => $"games/{gameId}/state/{turnYear}{Nova.Common.Global.ServerStateExtension}";

        public static string CurrentState(string gameId)
            => $"games/{gameId}/state/current{Nova.Common.Global.ServerStateExtension}";

        public static string OrdersPrefix(string gameId, int turnYear)
            => $"games/{gameId}/orders/{turnYear}/";

        public static string Orders(string gameId, int turnYear, int empireId)
            => $"{OrdersPrefix(gameId, turnYear)}{empireId}{Nova.Common.Global.OrdersExtension}";

        public static string IntelPrefix(string gameId, int turnYear)
            => $"games/{gameId}/intel/{turnYear}/";

        public static string Intel(string gameId, int turnYear, int empireId)
            => $"{IntelPrefix(gameId, turnYear)}{empireId}{Nova.Common.Global.IntelExtension}";

        public static string BackupPrefix(string gameId, int turnYear)
            => $"games/{gameId}/backup/{turnYear}/";
    }
}
