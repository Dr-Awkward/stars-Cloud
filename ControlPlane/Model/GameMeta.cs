// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>
/// The control-plane document for one game (Firestore <c>games/{gameId}</c>,
/// design Sections B.2, B.3, C.2, D.1, D.4). Small, queryable, transactional
/// metadata: schedulers and the turn trigger read this; the large ServerData
/// universe blob lives in GCS and is never scanned for these fields.
///
/// State-field reconciliation (the specs flagged a collision): lifecycle status
/// and generation status are DISTINCT fields, <see cref="Lifecycle"/> and
/// <see cref="Generation"/>, with the exactly-once guard modelled once in
/// <see cref="Lock"/> ({token, leaseUntil}). turngen's older "state = idle|
/// generating" and "lockOwner/lockExpiry" vocabulary maps onto these.
/// </summary>
[FirestoreData]
public sealed class GameMeta
{
    [FirestoreProperty] public string GameId { get; set; } = "";
    [FirestoreProperty] public string Name { get; set; } = "";

    /// <summary>The Google subject (users/{google_sub}) of the host.</summary>
    [FirestoreProperty] public string HostAccountId { get; set; } = "";

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<GameLifecycle>))]
    public GameLifecycle Lifecycle { get; set; } = GameLifecycle.Draft;

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<GenerationState>))]
    public GenerationState Generation { get; set; } = GenerationState.Idle;

    /// <summary>
    /// The turn year this game is currently ON (open for orders). Increments
    /// monotonically on commit; every generation trigger names the turnYear it
    /// intends to advance, which is what makes generation exactly-once.
    /// </summary>
    [FirestoreProperty] public int TurnYear { get; set; }

    /// <summary>
    /// Maximum wall-clock time between turns, in seconds (design Section D.1). The
    /// deadline for the open turn is lastGenerationAt + this. 0 means no clock
    /// (turns only generate when everyone submits).
    /// </summary>
    [FirestoreProperty] public long MaxTimeBetweenTurnsSeconds { get; set; }

    /// <summary>When the current open turn must resolve. Null when paused or unset.</summary>
    [FirestoreProperty] public Timestamp? DeadlineAt { get; set; }

    /// <summary>When the last turn was generated (the deadline is measured from here).</summary>
    [FirestoreProperty] public Timestamp? LastGenerationAt { get; set; }

    /// <summary>GCS uri of the authoritative ServerData for the current turn.</summary>
    [FirestoreProperty] public string CurrentStatePath { get; set; } = "";

    /// <summary>The exactly-once generation lock (design Section B.2).</summary>
    [FirestoreProperty] public GenerationLock? Lock { get; set; }

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<MissedTurnPolicy>))]
    public MissedTurnPolicy MissedTurnPolicy { get; set; } = MissedTurnPolicy.HoldOrders;

    /// <summary>Consecutive misses that trigger AI takeover under that policy.</summary>
    [FirestoreProperty] public int AiTakeoverAfterMisses { get; set; } = 3;

    /// <summary>Active seats expected to submit this turn (humans plus AIs).</summary>
    [FirestoreProperty] public int ActivePlayerCount { get; set; }

    /// <summary>How many seats have submitted for the open turn.</summary>
    [FirestoreProperty] public int SubmittedCount { get; set; }

    /// <summary>Visibility: true if anyone can find and join in the lobby.</summary>
    [FirestoreProperty] public bool IsPublic { get; set; }

    [FirestoreProperty] public Timestamp? CreatedAt { get; set; }

    /// <summary>
    /// The host-editable settings snapshot (map size, victory conditions). Frozen
    /// once the game leaves the lobby; turngen reads it at map initialisation.
    /// </summary>
    [FirestoreProperty] public GameOptions Options { get; set; } = new();

    /// <summary>
    /// The winning empire, once a turn generation reports one. Null while the game
    /// runs, and also null on a finished game whose generator did not report a
    /// victor, so a reader must treat null as "unknown", not "nobody won".
    /// </summary>
    [FirestoreProperty] public int? WinnerEmpireId { get; set; }

    /// <summary>When the game reached Finished or Cancelled.</summary>
    [FirestoreProperty] public Timestamp? FinishedAt { get; set; }

    /// <summary>
    /// True once the game is open for orders (Active and not mid-generation). The
    /// deadline sweeper queries on Lifecycle and DeadlineAt, not this.
    /// </summary>
    public bool IsOpenForOrders => Lifecycle == GameLifecycle.Active && Generation == GenerationState.Idle;
}

/// <summary>
/// The exactly-once lock fields, modelled once (design Section B.2). A worker
/// claims by setting a fresh token and a lease; it commits only if the token still
/// matches. Replaces turngen's separate lockOwner/lockExpiry scalars.
/// </summary>
[FirestoreData]
public sealed class GenerationLock
{
    /// <summary>Opaque owner token minted by the claiming worker.</summary>
    [FirestoreProperty] public string Token { get; set; } = "";

    /// <summary>When the lease expires; a later worker may steal an expired lock.</summary>
    [FirestoreProperty] public Timestamp LeaseUntil { get; set; }
}
