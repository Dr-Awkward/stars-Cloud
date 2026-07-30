// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Firestore;
using Galaxies.ControlPlane.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galaxies.ControlPlane.Firestore;

/// <summary>
/// Firestore-backed control plane (design Sections B.2, B.3, C.2, D). Firestore is
/// authoritative for the small, queryable, transactional metadata and for the
/// exactly-once generation lock; the large ServerData universe lives in GCS.
/// </summary>
public sealed class FirestoreControlPlane : IControlPlane
{
    private const string Users = "users";
    private const string Games = "games";
    private const string Members = "members";
    private const string Invites = "invites";
    private const string Reports = "reports";
    private const string Bans = "bans";

    private readonly FirestoreDb db;
    private readonly ILogger<FirestoreControlPlane> log;

    public FirestoreControlPlane(FirestoreDb db, ILogger<FirestoreControlPlane>? log = null)
    {
        this.db = db;
        this.log = log ?? NullLogger<FirestoreControlPlane>.Instance;
    }

    private DocumentReference UserDoc(string googleSub) => db.Collection(Users).Document(googleSub);
    private DocumentReference GameDoc(string gameId) => db.Collection(Games).Document(gameId);
    private CollectionReference MemberCol(string gameId) => GameDoc(gameId).Collection(Members);
    private DocumentReference InviteDoc(string inviteId) => db.Collection(Invites).Document(inviteId);
    private DocumentReference ReportDoc(string reportId) => db.Collection(Reports).Document(reportId);
    private DocumentReference BanDoc(string googleSub) => db.Collection(Bans).Document(googleSub);

    // ---- Accounts -----------------------------------------------------------

    public async Task<UserAccount?> GetUserAsync(string googleSub, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await UserDoc(googleSub).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<UserAccount>() : null;
    }

    public async Task<UserAccount> UpsertUserAsync(UserAccount user, CancellationToken ct = default)
    {
        user.CreatedAt ??= Timestamp.GetCurrentTimestamp();
        await UserDoc(user.GoogleSub).SetAsync(user, SetOptions.MergeAll, ct);
        return user;
    }

    public async Task SoftDeleteUserAsync(string googleSub, CancellationToken ct = default)
    {
        await UserDoc(googleSub).UpdateAsync(new Dictionary<string, object>
        {
            ["DeletedAt"] = Timestamp.GetCurrentTimestamp(),
            ["RefreshChainId"] = null!,
        }, cancellationToken: ct);
    }

    // ---- Games --------------------------------------------------------------

    public async Task<GameMeta> CreateGameAsync(GameMeta game, CancellationToken ct = default)
    {
        game.CreatedAt ??= Timestamp.GetCurrentTimestamp();
        await GameDoc(game.GameId).SetAsync(game, cancellationToken: ct);
        return game;
    }

    public async Task<GameMeta?> GetGameAsync(string gameId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await GameDoc(gameId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<GameMeta>() : null;
    }

    public async Task<IReadOnlyList<GameMeta>> ListGamesForUserAsync(string accountId, CancellationToken ct = default)
    {
        // Membership is a subcollection, so this uses a collection-group query on
        // members filtered by AccountId, then loads each parent game.
        Query q = db.CollectionGroup(Members).WhereEqualTo("AccountId", accountId);
        QuerySnapshot memberSnaps = await q.GetSnapshotAsync(ct);
        var games = new List<GameMeta>();
        foreach (DocumentSnapshot m in memberSnaps.Documents)
        {
            DocumentReference? gameRef = m.Reference.Parent.Parent;
            if (gameRef is null) continue;
            DocumentSnapshot g = await gameRef.GetSnapshotAsync(ct);
            if (g.Exists) games.Add(g.ConvertTo<GameMeta>());
        }
        return games;
    }

    public async Task<IReadOnlyList<GameMeta>> ListOpenGamesAsync(CancellationToken ct = default)
    {
        Query q = db.Collection(Games)
            .WhereEqualTo("Lifecycle", nameof(GameLifecycle.Lobby))
            .WhereEqualTo("IsPublic", true);
        QuerySnapshot snap = await q.GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<GameMeta>()).ToList();
    }

    public async Task<IReadOnlyList<GameMeta>> ListGamesAsync(GameBrowseQuery query, CancellationToken ct = default)
    {
        Query q = db.Collection(Games);
        if (query.Lifecycle is { } lifecycle)
        {
            q = q.WhereEqualTo("Lifecycle", lifecycle.ToString());
        }
        if (query.PublicOnly)
        {
            q = q.WhereEqualTo("IsPublic", true);
        }
        // Newest first is the only ordering a browser wants, and CreatedAt is the
        // only monotonic field every game has. A composite index on
        // (Lifecycle, IsPublic, CreatedAt desc) is required in Firestore.
        q = q.OrderByDescending("CreatedAt").Limit(Math.Max(1, query.Limit));
        QuerySnapshot snap = await q.GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<GameMeta>()).ToList();
    }

    public async Task<int> CountLiveGamesHostedByAsync(string accountId, CancellationToken ct = default)
    {
        // Firestore has no cheap "count where not in [...]" so this counts the three
        // live lifecycles explicitly. Three small indexed reads beat one scan.
        int total = 0;
        foreach (GameLifecycle live in new[] { GameLifecycle.Draft, GameLifecycle.Lobby, GameLifecycle.Active, GameLifecycle.Paused })
        {
            Query q = db.Collection(Games)
                .WhereEqualTo("HostAccountId", accountId)
                .WhereEqualTo("Lifecycle", live.ToString());
            AggregateQuerySnapshot count = await q.Count().GetSnapshotAsync(ct);
            total += (int)(count.Count ?? 0);
        }
        return total;
    }

    public async Task DeleteGameAsync(string gameId, CancellationToken ct = default)
    {
        QuerySnapshot members = await MemberCol(gameId).GetSnapshotAsync(ct);
        WriteBatch batch = db.StartBatch();
        foreach (DocumentSnapshot m in members.Documents)
        {
            batch.Delete(m.Reference);
        }
        batch.Delete(GameDoc(gameId));
        await batch.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<GameMeta>> ListOverdueGamesAsync(CancellationToken ct = default)
    {
        Timestamp now = Timestamp.GetCurrentTimestamp();
        Query q = db.Collection(Games)
            .WhereEqualTo("Lifecycle", nameof(GameLifecycle.Active))
            .WhereEqualTo("Generation", nameof(GenerationState.Idle))
            .WhereLessThanOrEqualTo("DeadlineAt", now);
        QuerySnapshot snap = await q.GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<GameMeta>()).ToList();
    }

    public async Task<GameMeta> TransitionLifecycleAsync(string gameId, GameLifecycle to, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(GameDoc(gameId), ct);
            if (!snap.Exists) throw new GameNotFoundException(gameId);
            GameMeta game = snap.ConvertTo<GameMeta>();

            Lifecycle.EnsureTransition(game.Lifecycle, to);
            game.Lifecycle = to;

            // Pausing cancels the clock; the deadline is recomputed on resume.
            if (to == GameLifecycle.Paused) game.DeadlineAt = null;
            if (to == GameLifecycle.Active && game.LastGenerationAt is { } last)
                game.DeadlineAt = Cadence.DeadlineFor(last, game.MaxTimeBetweenTurnsSeconds);
            if (to is GameLifecycle.Finished or GameLifecycle.Cancelled)
                game.FinishedAt ??= Timestamp.GetCurrentTimestamp();

            transaction.Set(GameDoc(gameId), game);
            return game;
        }, cancellationToken: ct);
    }

    // ---- Membership ---------------------------------------------------------

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string gameId, CancellationToken ct = default)
    {
        QuerySnapshot snap = await MemberCol(gameId).GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<Member>()).ToList();
    }

    public async Task<Member> AddMemberAsync(string gameId, Member member, CancellationToken ct = default)
    {
        member.JoinedAt ??= Timestamp.GetCurrentTimestamp();
        await MemberCol(gameId).Document(member.EmpireId.ToString()).SetAsync(member, cancellationToken: ct);
        return member;
    }

    public async Task RemoveMemberAsync(string gameId, int empireId, CancellationToken ct = default)
    {
        await MemberCol(gameId).Document(empireId.ToString()).DeleteAsync(cancellationToken: ct);
    }

    public async Task<Member?> JoinOpenSlotAsync(string gameId, int empireId, string accountId, string race, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentReference memberRef = MemberCol(gameId).Document(empireId.ToString());
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(memberRef, ct);
            if (!snap.Exists) return (Member?)null;
            Member member = snap.ConvertTo<Member>();
            if (!member.IsOpenSlot) return null; // someone already took it

            member.AccountId = accountId;
            member.Kind = PlayerKind.Human;
            member.Race = race;
            member.JoinedAt = Timestamp.GetCurrentTimestamp();
            transaction.Set(memberRef, member);
            return member;
        }, cancellationToken: ct);
    }

    public async Task<Member?> ClaimSlotForAiAsync(string gameId, int empireId, string participantId, string difficulty, string race, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentReference memberRef = MemberCol(gameId).Document(empireId.ToString());
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(memberRef, ct);
            if (!snap.Exists) return (Member?)null;
            Member member = snap.ConvertTo<Member>();
            if (!member.IsOpenSlot) return null; // never displace a human

            member.AccountId = participantId;
            member.Kind = PlayerKind.Ai;
            member.AiParticipantId = participantId;
            member.AiDifficulty = difficulty;
            member.AiTakeover = false;
            member.Race = race;
            member.JoinedAt = Timestamp.GetCurrentTimestamp();
            transaction.Set(memberRef, member);
            return member;
        }, cancellationToken: ct);
    }

    public async Task<Member?> ReleaseSlotAsync(string gameId, int empireId, string? expectedAccountId, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentReference memberRef = MemberCol(gameId).Document(empireId.ToString());
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(memberRef, ct);
            if (!snap.Exists) return (Member?)null;
            Member member = snap.ConvertTo<Member>();
            if (expectedAccountId is not null && member.AccountId != expectedAccountId) return null;

            member.AccountId = null;
            member.Kind = PlayerKind.Human;
            member.AiParticipantId = null;
            member.AiDifficulty = null;
            member.AiTakeover = false;
            member.Race = "";
            member.TurnSubmitted = false;
            member.LastSubmittedTurn = -1;
            member.ConsecutiveMisses = 0;
            member.Resigned = false;
            member.JoinedAt = null;
            transaction.Set(memberRef, member);
            return member;
        }, cancellationToken: ct);
    }

    public async Task<Member?> UpdateMemberAsync(string gameId, int empireId, Action<Member> mutate, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentReference memberRef = MemberCol(gameId).Document(empireId.ToString());
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(memberRef, ct);
            if (!snap.Exists) return (Member?)null;
            Member member = snap.ConvertTo<Member>();
            mutate(member);
            transaction.Set(memberRef, member);
            return member;
        }, cancellationToken: ct);
    }

    // ---- Submission tracking ------------------------------------------------

    public async Task<GameMeta> RecordSubmissionAsync(string gameId, int empireId, int turnYear, bool submitted, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentReference gameRef = GameDoc(gameId);
            DocumentReference memberRef = MemberCol(gameId).Document(empireId.ToString());

            DocumentSnapshot gameSnap = await transaction.GetSnapshotAsync(gameRef, ct);
            DocumentSnapshot memberSnap = await transaction.GetSnapshotAsync(memberRef, ct);
            if (!gameSnap.Exists) throw new GameNotFoundException(gameId);
            if (!memberSnap.Exists) throw new MemberNotFoundException(gameId, empireId);

            GameMeta game = gameSnap.ConvertTo<GameMeta>();
            Member member = memberSnap.ConvertTo<Member>();

            if (game.TurnYear != turnYear)
            {
                // Submitting for a turn that has already generated; no-op.
                return game;
            }

            bool wasSubmitted = member.TurnSubmitted;
            if (submitted && !wasSubmitted)
            {
                member.TurnSubmitted = true;
                member.LastSubmittedTurn = turnYear;
                member.ConsecutiveMisses = 0;
                game.SubmittedCount += 1;
            }
            else if (!submitted && wasSubmitted)
            {
                member.TurnSubmitted = false;
                game.SubmittedCount = Math.Max(0, game.SubmittedCount - 1);
            }

            transaction.Set(memberRef, member);
            transaction.Set(gameRef, game);
            return game;
        }, cancellationToken: ct);
    }

    // ---- Exactly-once generation lock (design Section B.2) ------------------

    public async Task<GenerationClaim> TryClaimGenerationAsync(string gameId, int turnYear, string workerToken, int leaseSeconds, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(GameDoc(gameId), ct);
            if (!snap.Exists) return new GenerationClaim(ClaimOutcome.NotGenerable, null);
            GameMeta game = snap.ConvertTo<GameMeta>();

            if (game.Lifecycle != GameLifecycle.Active)
                return new GenerationClaim(ClaimOutcome.NotGenerable, game);

            // Every trigger names the turn it means to advance. If turnYear moved
            // on, this trigger is a stale duplicate; drop it.
            if (game.TurnYear != turnYear)
                return new GenerationClaim(ClaimOutcome.AlreadyAdvanced, game);

            Timestamp now = Timestamp.GetCurrentTimestamp();
            bool liveLock = game.Generation == GenerationState.Generating
                            && game.Lock is { } l
                            && l.LeaseUntil.ToDateTimeOffset() > now.ToDateTimeOffset();
            if (liveLock)
                return new GenerationClaim(ClaimOutcome.HeldByOther, game);

            // Win the claim: mark generating and take the lock with a fresh lease.
            game.Generation = GenerationState.Generating;
            game.Lock = new GenerationLock
            {
                Token = workerToken,
                LeaseUntil = Timestamp.FromDateTimeOffset(now.ToDateTimeOffset().AddSeconds(leaseSeconds)),
            };
            transaction.Set(GameDoc(gameId), game);
            return new GenerationClaim(ClaimOutcome.Won, game);
        }, cancellationToken: ct);
    }

    public async Task<bool> CommitGenerationAsync(string gameId, GenerationCommit commit, CancellationToken ct = default)
    {
        return await db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(GameDoc(gameId), ct);
            if (!snap.Exists) return false;
            GameMeta game = snap.ConvertTo<GameMeta>();

            // Assert the world did not move under us: same turn, our token still
            // holds the lock. If not, the lock was stolen (our lease expired); the
            // caller discards its just-written results.
            bool ours = game.TurnYear == commit.TurnYear
                        && game.Generation == GenerationState.Generating
                        && game.Lock?.Token == commit.WorkerToken;
            if (!ours) return false;

            Timestamp now = Timestamp.GetCurrentTimestamp();
            game.TurnYear = commit.TurnYear + 1;
            game.Generation = GenerationState.Idle;
            game.Lock = null;
            game.CurrentStatePath = commit.NewStatePath;
            game.LastGenerationAt = now;
            game.SubmittedCount = 0;
            game.DeadlineAt = Cadence.DeadlineFor(now, game.MaxTimeBetweenTurnsSeconds);
            if (commit.WinnerEmpireId is { } winner) game.WinnerEmpireId = winner;
            if (commit.GameEnded)
            {
                game.Lifecycle = GameLifecycle.Finished;
                game.FinishedAt ??= now;
            }

            transaction.Set(GameDoc(gameId), game);
            return true;
        }, cancellationToken: ct);
    }

    public async Task OpenNextTurnAsync(string gameId, CancellationToken ct = default)
    {
        QuerySnapshot members = await MemberCol(gameId).GetSnapshotAsync(ct);
        WriteBatch batch = db.StartBatch();
        foreach (DocumentSnapshot m in members.Documents)
        {
            batch.Update(m.Reference, new Dictionary<string, object> { ["TurnSubmitted"] = false });
        }
        await batch.CommitAsync(ct);
    }

    // ---- Invites ------------------------------------------------------------

    public async Task<Invite> CreateInviteAsync(Invite invite, CancellationToken ct = default)
    {
        invite.CreatedAt ??= Timestamp.GetCurrentTimestamp();
        await InviteDoc(invite.InviteId).SetAsync(invite, cancellationToken: ct);
        return invite;
    }

    public async Task<Invite?> GetInviteAsync(string inviteId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await InviteDoc(inviteId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<Invite>() : null;
    }

    public async Task<Invite?> GetInviteByTokenAsync(string token, CancellationToken ct = default)
    {
        // The token is high entropy and unique, so this is a point lookup wearing a
        // query's clothes. Limit(1) keeps it that way even if one ever collided.
        QuerySnapshot snap = await db.Collection(Invites)
            .WhereEqualTo("Token", token).Limit(1).GetSnapshotAsync(ct);
        return snap.Documents.Count == 0 ? null : snap.Documents[0].ConvertTo<Invite>();
    }

    public async Task<IReadOnlyList<Invite>> ListInvitesForGameAsync(string gameId, CancellationToken ct = default)
    {
        QuerySnapshot snap = await db.Collection(Invites)
            .WhereEqualTo("GameId", gameId).GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<Invite>()).ToList();
    }

    public async Task<Invite> UpdateInviteAsync(Invite invite, CancellationToken ct = default)
    {
        await InviteDoc(invite.InviteId).SetAsync(invite, cancellationToken: ct);
        return invite;
    }

    // ---- Moderation ---------------------------------------------------------

    public async Task<AbuseReport> CreateReportAsync(AbuseReport report, CancellationToken ct = default)
    {
        report.CreatedAt ??= Timestamp.GetCurrentTimestamp();
        await ReportDoc(report.ReportId).SetAsync(report, cancellationToken: ct);
        return report;
    }

    public async Task<AbuseReport?> GetReportAsync(string reportId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await ReportDoc(reportId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AbuseReport>() : null;
    }

    public async Task<IReadOnlyList<AbuseReport>> ListReportsAsync(ReportStatus? status, int limit, CancellationToken ct = default)
    {
        Query q = db.Collection(Reports);
        if (status is { } s)
        {
            q = q.WhereEqualTo("Status", s.ToString());
        }
        q = q.OrderByDescending("CreatedAt").Limit(Math.Max(1, limit));
        QuerySnapshot snap = await q.GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<AbuseReport>()).ToList();
    }

    public async Task<AbuseReport> UpdateReportAsync(AbuseReport report, CancellationToken ct = default)
    {
        await ReportDoc(report.ReportId).SetAsync(report, cancellationToken: ct);
        return report;
    }

    public async Task<Ban?> GetBanAsync(string googleSub, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await BanDoc(googleSub).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<Ban>() : null;
    }

    public async Task SetBanAsync(Ban ban, CancellationToken ct = default)
    {
        ban.CreatedAt ??= Timestamp.GetCurrentTimestamp();
        await BanDoc(ban.GoogleSub).SetAsync(ban, cancellationToken: ct);
    }

    public async Task RemoveBanAsync(string googleSub, CancellationToken ct = default)
    {
        await BanDoc(googleSub).DeleteAsync(cancellationToken: ct);
    }
}

public sealed class GameNotFoundException(string gameId)
    : Exception($"No game with id {gameId}.") { public string GameId { get; } = gameId; }

public sealed class MemberNotFoundException(string gameId, int empireId)
    : Exception($"No member {empireId} in game {gameId}.")
{ public string GameId { get; } = gameId; public int EmpireId { get; } = empireId; }
