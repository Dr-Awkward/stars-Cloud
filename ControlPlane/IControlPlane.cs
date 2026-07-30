// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.ControlPlane.Model;

namespace Galaxies.ControlPlane;

/// <summary>
/// The transactional control plane (design Sections B.2, C.2, D). Accounts,
/// games, membership, submission tracking, and the exactly-once generation lock.
/// Firestore-backed in production (see FirestoreControlPlane); an in-memory
/// implementation drives the unit tests for the lock and the scheduling logic.
/// </summary>
public interface IControlPlane
{
    // ---- Accounts (users/{google_sub}) --------------------------------------
    Task<UserAccount?> GetUserAsync(string googleSub, CancellationToken ct = default);
    Task<UserAccount> UpsertUserAsync(UserAccount user, CancellationToken ct = default);
    Task SoftDeleteUserAsync(string googleSub, CancellationToken ct = default);

    // ---- Games (games/{gameId}) ---------------------------------------------
    Task<GameMeta> CreateGameAsync(GameMeta game, CancellationToken ct = default);
    Task<GameMeta?> GetGameAsync(string gameId, CancellationToken ct = default);
    Task<IReadOnlyList<GameMeta>> ListGamesForUserAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<GameMeta>> ListOpenGamesAsync(CancellationToken ct = default);

    /// <summary>
    /// The game browser's general query (design Section 6.2 <c>GET /games</c>).
    /// Filters on lifecycle and visibility and caps the result, which is what the
    /// public scopes need and what ListOpenGamesAsync is too narrow for.
    /// </summary>
    Task<IReadOnlyList<GameMeta>> ListGamesAsync(GameBrowseQuery query, CancellationToken ct = default);

    /// <summary>
    /// How many games this account hosts that are not yet over, for the per-account
    /// quota (design gap "per-account and global resource quotas"). Finished,
    /// cancelled, and archived games do not count against the cap, because they cost
    /// nothing to keep and capping them would punish people for playing.
    /// </summary>
    Task<int> CountLiveGamesHostedByAsync(string accountId, CancellationToken ct = default);

    /// <summary>Hard delete a game document and its members. Pre-start games only.</summary>
    Task DeleteGameAsync(string gameId, CancellationToken ct = default);

    /// <summary>
    /// Games whose deadline has passed and that are still open. The Cloud Scheduler
    /// sweep uses this to re-arm any generation a lost Cloud Tasks entry missed
    /// (design Section B.2 backstop).
    /// </summary>
    Task<IReadOnlyList<GameMeta>> ListOverdueGamesAsync(CancellationToken ct = default);

    /// <summary>Change a game's lifecycle, enforcing the state machine.</summary>
    Task<GameMeta> TransitionLifecycleAsync(string gameId, GameLifecycle to, CancellationToken ct = default);

    // ---- Membership (games/{gameId}/members/{empireId}) ---------------------
    Task<IReadOnlyList<Member>> GetMembersAsync(string gameId, CancellationToken ct = default);
    Task<Member> AddMemberAsync(string gameId, Member member, CancellationToken ct = default);
    Task RemoveMemberAsync(string gameId, int empireId, CancellationToken ct = default);

    /// <summary>
    /// Claim an open lobby slot for an account, transactionally, so two players
    /// cannot take the same seat. Returns the claimed member, or null if the slot
    /// is gone.
    /// </summary>
    Task<Member?> JoinOpenSlotAsync(string gameId, int empireId, string accountId, string race, CancellationToken ct = default);

    /// <summary>
    /// Claim an OPEN lobby slot for an AI participant, transactionally. Returns null
    /// if the slot is gone or already held, so adding an AI can never displace a
    /// human who joined a moment earlier.
    /// </summary>
    Task<Member?> ClaimSlotForAiAsync(string gameId, int empireId, string participantId, string difficulty, string race, CancellationToken ct = default);

    /// <summary>
    /// Return a seat to the open-slot state (leave, or a host removing a player).
    /// When <paramref name="expectedAccountId"/> is given the release only happens if
    /// the seat still belongs to that account, so a stale request cannot evict
    /// whoever took the seat since.
    /// </summary>
    Task<Member?> ReleaseSlotAsync(string gameId, int empireId, string? expectedAccountId, CancellationToken ct = default);

    /// <summary>
    /// Read-modify-write one seat inside a transaction. The mutation runs against
    /// the live document, so concurrent writers cannot lose each other's changes.
    /// Returns the updated seat, or null if it does not exist.
    /// </summary>
    Task<Member?> UpdateMemberAsync(string gameId, int empireId, Action<Member> mutate, CancellationToken ct = default);

    // ---- Submission tracking ------------------------------------------------
    /// <summary>
    /// Record that a seat submitted final orders for <paramref name="turnYear"/>,
    /// transactionally bumping the game's SubmittedCount. Idempotent per
    /// (gameId, empireId, turnYear). Returns the post-submit game snapshot so the
    /// caller can fire early generation when everyone is in.
    /// </summary>
    Task<GameMeta> RecordSubmissionAsync(string gameId, int empireId, int turnYear, bool submitted, CancellationToken ct = default);

    // ---- Exactly-once generation lock (design Section B.2) -------------------
    /// <summary>
    /// Try to claim generation of <paramref name="turnYear"/> for a worker. Wins
    /// only if the game is on that turnYear and no live lock is held. Sets the
    /// lock's token and a lease. This is transaction A of the exactly-once guard.
    /// </summary>
    Task<GenerationClaim> TryClaimGenerationAsync(string gameId, int turnYear, string workerToken, int leaseSeconds, CancellationToken ct = default);

    /// <summary>
    /// Commit a generated turn: assert the turnYear is unchanged and the lock token
    /// still matches, then advance turnYear, set Generation=Idle, clear the lock,
    /// point CurrentStatePath at the new blob, and set the next deadline. This is
    /// transaction B. Returns false if the assertion fails (the lock was stolen);
    /// the caller discards the just-written results.
    /// </summary>
    Task<bool> CommitGenerationAsync(string gameId, GenerationCommit commit, CancellationToken ct = default);

    /// <summary>
    /// Open the next turn after a commit: clear every member's TurnSubmitted and
    /// reset SubmittedCount, so submission tracking starts fresh.
    /// </summary>
    Task OpenNextTurnAsync(string gameId, CancellationToken ct = default);

    // ---- Invites (invites/{inviteId}) ---------------------------------------
    Task<Invite> CreateInviteAsync(Invite invite, CancellationToken ct = default);
    Task<Invite?> GetInviteAsync(string inviteId, CancellationToken ct = default);

    /// <summary>Look an invite up by its opaque token, for the accept path.</summary>
    Task<Invite?> GetInviteByTokenAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<Invite>> ListInvitesForGameAsync(string gameId, CancellationToken ct = default);
    Task<Invite> UpdateInviteAsync(Invite invite, CancellationToken ct = default);

    // ---- Moderation (reports/{reportId}, bans/{google_sub}) -----------------
    Task<AbuseReport> CreateReportAsync(AbuseReport report, CancellationToken ct = default);
    Task<AbuseReport?> GetReportAsync(string reportId, CancellationToken ct = default);
    Task<IReadOnlyList<AbuseReport>> ListReportsAsync(ReportStatus? status, int limit, CancellationToken ct = default);
    Task<AbuseReport> UpdateReportAsync(AbuseReport report, CancellationToken ct = default);

    /// <summary>The ban record for an account, or null if it is not banned.</summary>
    Task<Ban?> GetBanAsync(string googleSub, CancellationToken ct = default);
    Task SetBanAsync(Ban ban, CancellationToken ct = default);
    Task RemoveBanAsync(string googleSub, CancellationToken ct = default);
}

/// <summary>
/// The game browser's filter (design Section 6.2). Null <see cref="Lifecycle"/>
/// means any lifecycle. <see cref="Limit"/> is a hard cap the caller must set to
/// something sane; the API clamps it before this is ever constructed.
/// </summary>
public sealed record GameBrowseQuery
{
    public GameLifecycle? Lifecycle { get; init; }

    /// <summary>Restrict to games flagged public, which is what a stranger may browse.</summary>
    public bool PublicOnly { get; init; }

    public int Limit { get; init; } = 50;
}

/// <summary>Outcome of a generation claim (transaction A).</summary>
public enum ClaimOutcome
{
    Won,             // this worker holds the lock; proceed to generate
    AlreadyAdvanced, // turnYear already moved on; this trigger is stale, drop it
    HeldByOther,     // another live lock owns it; drop this trigger
    NotGenerable,    // game is not Active, or does not exist
}

public readonly record struct GenerationClaim(ClaimOutcome Outcome, GameMeta? Game)
{
    public bool Won => Outcome == ClaimOutcome.Won;
}

/// <summary>Everything a commit needs to advance a game by one turn.</summary>
public sealed record GenerationCommit
{
    public required int TurnYear { get; init; }        // the turn that was generated
    public required string WorkerToken { get; init; }  // must still match the lock
    public required string NewStatePath { get; init; } // GCS uri of the new ServerData
    public bool GameEnded { get; init; }               // a victory condition was met
    public int? WinnerEmpireId { get; init; }          // the victor, when the worker named one
}
