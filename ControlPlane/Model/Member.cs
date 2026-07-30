// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>
/// One seat in a game (Firestore <c>games/{gameId}/members/{empireId}</c>, the
/// LOCKED seat model from the specs reconciliation). This is what the roster
/// resolver, the AI dispatcher, and the submission counter all read; the API is
/// the sole writer. Keyed by empireId (a stable int, distinct from the account).
/// </summary>
[FirestoreData]
public sealed class Member
{
    /// <summary>The engine's empire id (EmpireData.Id), the document key.</summary>
    [FirestoreProperty] public int EmpireId { get; set; }

    /// <summary>
    /// The account holding this seat: users/{google_sub} for a human, or the AI
    /// participant id for an AI seat. Null for an open (unclaimed) lobby slot.
    /// </summary>
    [FirestoreProperty] public string? AccountId { get; set; }

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<PlayerKind>))]
    public PlayerKind Kind { get; set; } = PlayerKind.Human;

    /// <summary>The race file/name chosen for this seat.</summary>
    [FirestoreProperty] public string Race { get; set; } = "";

    /// <summary>True once this seat has submitted final orders for the open turn.</summary>
    [FirestoreProperty] public bool TurnSubmitted { get; set; }

    /// <summary>The last turn year for which this seat submitted (for HoldOrders reuse).</summary>
    [FirestoreProperty] public int LastSubmittedTurn { get; set; } = -1;

    /// <summary>
    /// Consecutive missed deadlines, for the missed-turn ladder (design Section
    /// D.2). Reset to 0 on a real submission; escalates the policy as it climbs.
    /// </summary>
    [FirestoreProperty] public int ConsecutiveMisses { get; set; }

    /// <summary>True if the seat resigned or was kicked and is now open/AI-run.</summary>
    [FirestoreProperty] public bool Resigned { get; set; }

    /// <summary>Vacation days spent (out-of-quorum, never counted as a miss).</summary>
    [FirestoreProperty] public int VacationDaysUsed { get; set; }

    [FirestoreProperty] public Timestamp? JoinedAt { get; set; }

    /// <summary>
    /// Which AI participant drives this seat while <see cref="Kind"/> is Ai, for
    /// example "galaxies.default-ai". Null on a human seat. On an original AI seat
    /// this equals <see cref="AccountId"/>; on a takeover it does not, because a
    /// takeover keeps the human's account on the seat (see <see cref="AiTakeover"/>).
    /// </summary>
    [FirestoreProperty] public string? AiParticipantId { get; set; }

    /// <summary>The difficulty band handed to the participant ("normal" by default).</summary>
    [FirestoreProperty] public string? AiDifficulty { get; set; }

    /// <summary>
    /// True when the missed-turn ladder handed a HUMAN seat to an AI, as opposed to
    /// a seat that was an AI from the lobby. The distinction is load bearing: a
    /// takeover keeps AccountId pointing at the absent human so they still own the
    /// seat, can still read their own intel, and can be handed back; an original AI
    /// seat has the participant id in AccountId and no human behind it.
    /// </summary>
    [FirestoreProperty] public bool AiTakeover { get; set; }

    public bool IsOpenSlot => AccountId == null && !Resigned;
    public bool IsActive => !Resigned && AccountId != null;
}
