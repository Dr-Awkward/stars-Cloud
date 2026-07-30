// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

namespace Galaxies.Client;

/// <summary>Where the open turn stands, for the client's status poll.</summary>
public sealed record TurnStatus(int TurnYear, string Lifecycle, string Generation, bool YouSubmitted, int SubmittedCount, int ActivePlayerCount);

/// <summary>
/// The client's turn wire (design Section E.4). It replaces the desktop game's
/// shared-folder file exchange (.orders out, .intel in) with calls to
/// galaxies-api. The WinForms client's IntelReader and OrderWriter are refactored
/// to go through this seam, so the same client works against the cloud without a
/// shared folder, and a future web client implements the same interface in JS.
///
/// The engine XML travels as-is (zero domain-model drift): GetIntel returns the
/// per-empire .intel document; SubmitOrders sends the OrderWriter document. The
/// server derives the caller's empire from the session, so the transport never
/// names it.
/// </summary>
public interface ITurnTransport
{
    /// <summary>Fetch the caller's current-turn intel XML, or a past turn for replay.</summary>
    Task<string> GetIntelAsync(string gameId, int? turnYear = null, CancellationToken ct = default);

    /// <summary>Upload (create or replace) the caller's draft orders XML for the open turn.</summary>
    Task PutOrdersAsync(string gameId, int turnYear, string ordersXml, CancellationToken ct = default);

    /// <summary>Read back the caller's draft orders XML, or null if none saved.</summary>
    Task<string?> GetOrdersAsync(string gameId, CancellationToken ct = default);

    /// <summary>Mark the caller's orders final; may trip early generation.</summary>
    Task SubmitOrdersAsync(string gameId, CancellationToken ct = default);

    /// <summary>Poll the open turn's status.</summary>
    Task<TurnStatus> GetStatusAsync(string gameId, CancellationToken ct = default);
}
