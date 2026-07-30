// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Collections.Concurrent;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Model;
using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Tests;

/// <summary>
/// An in-process IControlPlane for tests. It stands in for Firestore, using a
/// per-game monitor to give the claim/commit/submission operations the same
/// atomicity the Firestore transactions provide. That is exactly what makes the
/// exactly-once guarantee testable without a database: two concurrent claims
/// serialize on the game's lock, so only one can win.
/// </summary>
public sealed class InMemoryControlPlane : IControlPlane
{
    private readonly ConcurrentDictionary<string, GameMeta> games = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, Member>> members = new();
    private readonly ConcurrentDictionary<string, UserAccount> users = new();
    private readonly ConcurrentDictionary<string, object> locks = new();
    private readonly ConcurrentDictionary<string, Invite> invites = new();
    private readonly ConcurrentDictionary<string, AbuseReport> reports = new();
    private readonly ConcurrentDictionary<string, Ban> bans = new();

    private object GameLock(string gameId) => locks.GetOrAdd(gameId, _ => new object());
    private static GameMeta Clone(GameMeta g) => (GameMeta)g.MemberwiseCloneShallow();
    private ConcurrentDictionary<int, Member> MemberMap(string gameId) => members.GetOrAdd(gameId, _ => new());

    // ---- Accounts -----------------------------------------------------------
    public Task<UserAccount?> GetUserAsync(string googleSub, CancellationToken ct = default)
        => Task.FromResult(users.TryGetValue(googleSub, out var u) ? u : null);
    public Task<UserAccount> UpsertUserAsync(UserAccount user, CancellationToken ct = default)
    { users[user.GoogleSub] = user; return Task.FromResult(user); }
    public Task SoftDeleteUserAsync(string googleSub, CancellationToken ct = default)
    { if (users.TryGetValue(googleSub, out var u)) { u.DeletedAt = Timestamp.GetCurrentTimestamp(); u.RefreshChainId = null; } return Task.CompletedTask; }

    // ---- Games --------------------------------------------------------------
    public Task<GameMeta> CreateGameAsync(GameMeta game, CancellationToken ct = default)
    { games[game.GameId] = game; return Task.FromResult(game); }
    public Task<GameMeta?> GetGameAsync(string gameId, CancellationToken ct = default)
        => Task.FromResult(games.TryGetValue(gameId, out var g) ? Clone(g) : null);
    public Task<IReadOnlyList<GameMeta>> ListGamesForUserAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GameMeta>>(games.Values.ToList());
    public Task<IReadOnlyList<GameMeta>> ListOpenGamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GameMeta>>(games.Values.Where(g => g.Lifecycle == GameLifecycle.Lobby && g.IsPublic).ToList());
    public Task<IReadOnlyList<GameMeta>> ListGamesAsync(GameBrowseQuery query, CancellationToken ct = default)
    {
        IEnumerable<GameMeta> q = games.Values;
        if (query.Lifecycle is { } lifecycle) q = q.Where(g => g.Lifecycle == lifecycle);
        if (query.PublicOnly) q = q.Where(g => g.IsPublic);
        var list = q.OrderByDescending(g => g.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, query.Limit)).Select(Clone).ToList();
        return Task.FromResult<IReadOnlyList<GameMeta>>(list);
    }
    public Task<int> CountLiveGamesHostedByAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult(games.Values.Count(g => g.HostAccountId == accountId
            && g.Lifecycle is GameLifecycle.Draft or GameLifecycle.Lobby or GameLifecycle.Active or GameLifecycle.Paused));
    public Task DeleteGameAsync(string gameId, CancellationToken ct = default)
    { games.TryRemove(gameId, out _); members.TryRemove(gameId, out _); return Task.CompletedTask; }
    public Task<IReadOnlyList<GameMeta>> ListOverdueGamesAsync(CancellationToken ct = default)
    {
        Timestamp now = Timestamp.GetCurrentTimestamp();
        var list = games.Values.Where(g => g.Lifecycle == GameLifecycle.Active
            && g.Generation == GenerationState.Idle && Cadence.DeadlineReached(g, now)).ToList();
        return Task.FromResult<IReadOnlyList<GameMeta>>(list);
    }
    public Task<GameMeta> TransitionLifecycleAsync(string gameId, GameLifecycle to, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            GameMeta g = games[gameId];
            Lifecycle.EnsureTransition(g.Lifecycle, to);
            g.Lifecycle = to;
            if (to is GameLifecycle.Finished or GameLifecycle.Cancelled)
                g.FinishedAt ??= Timestamp.GetCurrentTimestamp();
            return Task.FromResult(Clone(g));
        }
    }

    // ---- Membership ---------------------------------------------------------
    public Task<IReadOnlyList<Member>> GetMembersAsync(string gameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Member>>(MemberMap(gameId).Values.ToList());
    public Task<Member> AddMemberAsync(string gameId, Member member, CancellationToken ct = default)
    { MemberMap(gameId)[member.EmpireId] = member; return Task.FromResult(member); }
    public Task RemoveMemberAsync(string gameId, int empireId, CancellationToken ct = default)
    { MemberMap(gameId).TryRemove(empireId, out _); return Task.CompletedTask; }
    public Task<Member?> JoinOpenSlotAsync(string gameId, int empireId, string accountId, string race, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            var map = MemberMap(gameId);
            if (!map.TryGetValue(empireId, out var m) || !m.IsOpenSlot) return Task.FromResult<Member?>(null);
            m.AccountId = accountId; m.Race = race; m.Kind = PlayerKind.Human;
            return Task.FromResult<Member?>(m);
        }
    }
    public Task<Member?> ClaimSlotForAiAsync(string gameId, int empireId, string participantId, string difficulty, string race, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            var map = MemberMap(gameId);
            if (!map.TryGetValue(empireId, out var m) || !m.IsOpenSlot) return Task.FromResult<Member?>(null);
            m.AccountId = participantId; m.Kind = PlayerKind.Ai; m.AiParticipantId = participantId;
            m.AiDifficulty = difficulty; m.AiTakeover = false; m.Race = race;
            m.JoinedAt = Timestamp.GetCurrentTimestamp();
            return Task.FromResult<Member?>(m);
        }
    }
    public Task<Member?> ReleaseSlotAsync(string gameId, int empireId, string? expectedAccountId, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            var map = MemberMap(gameId);
            if (!map.TryGetValue(empireId, out var m)) return Task.FromResult<Member?>(null);
            if (expectedAccountId is not null && m.AccountId != expectedAccountId) return Task.FromResult<Member?>(null);
            m.AccountId = null; m.Kind = PlayerKind.Human; m.AiParticipantId = null; m.AiDifficulty = null;
            m.AiTakeover = false; m.Race = ""; m.TurnSubmitted = false; m.LastSubmittedTurn = -1;
            m.ConsecutiveMisses = 0; m.Resigned = false; m.JoinedAt = null;
            return Task.FromResult<Member?>(m);
        }
    }
    public Task<Member?> UpdateMemberAsync(string gameId, int empireId, Action<Member> mutate, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            if (!MemberMap(gameId).TryGetValue(empireId, out var m)) return Task.FromResult<Member?>(null);
            mutate(m);
            return Task.FromResult<Member?>(m);
        }
    }

    // ---- Submission ---------------------------------------------------------
    public Task<GameMeta> RecordSubmissionAsync(string gameId, int empireId, int turnYear, bool submitted, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            GameMeta g = games[gameId];
            Member m = MemberMap(gameId)[empireId];
            if (g.TurnYear != turnYear) return Task.FromResult(Clone(g));
            if (submitted && !m.TurnSubmitted)
            { m.TurnSubmitted = true; m.LastSubmittedTurn = turnYear; m.ConsecutiveMisses = 0; g.SubmittedCount++; }
            else if (!submitted && m.TurnSubmitted)
            { m.TurnSubmitted = false; g.SubmittedCount = Math.Max(0, g.SubmittedCount - 1); }
            return Task.FromResult(Clone(g));
        }
    }

    // ---- Exactly-once lock --------------------------------------------------
    public Task<GenerationClaim> TryClaimGenerationAsync(string gameId, int turnYear, string workerToken, int leaseSeconds, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            if (!games.TryGetValue(gameId, out var g)) return Task.FromResult(new GenerationClaim(ClaimOutcome.NotGenerable, null));
            if (g.Lifecycle != GameLifecycle.Active) return Task.FromResult(new GenerationClaim(ClaimOutcome.NotGenerable, Clone(g)));
            if (g.TurnYear != turnYear) return Task.FromResult(new GenerationClaim(ClaimOutcome.AlreadyAdvanced, Clone(g)));

            Timestamp now = Timestamp.GetCurrentTimestamp();
            bool liveLock = g.Generation == GenerationState.Generating
                && g.Lock is { } l && l.LeaseUntil.ToDateTimeOffset() > now.ToDateTimeOffset();
            if (liveLock) return Task.FromResult(new GenerationClaim(ClaimOutcome.HeldByOther, Clone(g)));

            g.Generation = GenerationState.Generating;
            g.Lock = new GenerationLock { Token = workerToken, LeaseUntil = Timestamp.FromDateTimeOffset(now.ToDateTimeOffset().AddSeconds(leaseSeconds)) };
            return Task.FromResult(new GenerationClaim(ClaimOutcome.Won, Clone(g)));
        }
    }

    public Task<bool> CommitGenerationAsync(string gameId, GenerationCommit commit, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            GameMeta g = games[gameId];
            bool ours = g.TurnYear == commit.TurnYear && g.Generation == GenerationState.Generating && g.Lock?.Token == commit.WorkerToken;
            if (!ours) return Task.FromResult(false);
            Timestamp now = Timestamp.GetCurrentTimestamp();
            g.TurnYear = commit.TurnYear + 1;
            g.Generation = GenerationState.Idle;
            g.Lock = null;
            g.CurrentStatePath = commit.NewStatePath;
            g.LastGenerationAt = now;
            g.SubmittedCount = 0;
            g.DeadlineAt = Cadence.DeadlineFor(now, g.MaxTimeBetweenTurnsSeconds);
            if (commit.WinnerEmpireId is { } winner) g.WinnerEmpireId = winner;
            if (commit.GameEnded) { g.Lifecycle = GameLifecycle.Finished; g.FinishedAt ??= now; }
            return Task.FromResult(true);
        }
    }

    public Task OpenNextTurnAsync(string gameId, CancellationToken ct = default)
    {
        lock (GameLock(gameId))
        {
            foreach (var m in MemberMap(gameId).Values) m.TurnSubmitted = false;
            return Task.CompletedTask;
        }
    }

    // ---- Invites ------------------------------------------------------------
    public Task<Invite> CreateInviteAsync(Invite invite, CancellationToken ct = default)
    { invite.CreatedAt ??= Timestamp.GetCurrentTimestamp(); invites[invite.InviteId] = invite; return Task.FromResult(invite); }
    public Task<Invite?> GetInviteAsync(string inviteId, CancellationToken ct = default)
        => Task.FromResult(invites.TryGetValue(inviteId, out var i) ? i : null);
    public Task<Invite?> GetInviteByTokenAsync(string token, CancellationToken ct = default)
        => Task.FromResult(invites.Values.FirstOrDefault(i => i.Token == token));
    public Task<IReadOnlyList<Invite>> ListInvitesForGameAsync(string gameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Invite>>(invites.Values.Where(i => i.GameId == gameId).ToList());
    public Task<Invite> UpdateInviteAsync(Invite invite, CancellationToken ct = default)
    { invites[invite.InviteId] = invite; return Task.FromResult(invite); }

    // ---- Moderation ---------------------------------------------------------
    public Task<AbuseReport> CreateReportAsync(AbuseReport report, CancellationToken ct = default)
    { report.CreatedAt ??= Timestamp.GetCurrentTimestamp(); reports[report.ReportId] = report; return Task.FromResult(report); }
    public Task<AbuseReport?> GetReportAsync(string reportId, CancellationToken ct = default)
        => Task.FromResult(reports.TryGetValue(reportId, out var r) ? r : null);
    public Task<IReadOnlyList<AbuseReport>> ListReportsAsync(ReportStatus? status, int limit, CancellationToken ct = default)
    {
        IEnumerable<AbuseReport> q = reports.Values;
        if (status is { } s) q = q.Where(r => r.Status == s);
        var list = q.OrderByDescending(r => r.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, limit)).ToList();
        return Task.FromResult<IReadOnlyList<AbuseReport>>(list);
    }
    public Task<AbuseReport> UpdateReportAsync(AbuseReport report, CancellationToken ct = default)
    { reports[report.ReportId] = report; return Task.FromResult(report); }
    public Task<Ban?> GetBanAsync(string googleSub, CancellationToken ct = default)
        => Task.FromResult(bans.TryGetValue(googleSub, out var b) ? b : null);
    public Task SetBanAsync(Ban ban, CancellationToken ct = default)
    { ban.CreatedAt ??= Timestamp.GetCurrentTimestamp(); bans[ban.GoogleSub] = ban; return Task.CompletedTask; }
    public Task RemoveBanAsync(string googleSub, CancellationToken ct = default)
    { bans.TryRemove(googleSub, out _); return Task.CompletedTask; }
}

/// <summary>Tiny shallow-clone helper so tests read snapshots, not live objects.</summary>
internal static class CloneExtensions
{
    public static object MemberwiseCloneShallow(this GameMeta g)
    {
        return new GameMeta
        {
            GameId = g.GameId, Name = g.Name, HostAccountId = g.HostAccountId,
            Lifecycle = g.Lifecycle, Generation = g.Generation, TurnYear = g.TurnYear,
            MaxTimeBetweenTurnsSeconds = g.MaxTimeBetweenTurnsSeconds, DeadlineAt = g.DeadlineAt,
            LastGenerationAt = g.LastGenerationAt, CurrentStatePath = g.CurrentStatePath,
            Lock = g.Lock is null ? null : new GenerationLock { Token = g.Lock.Token, LeaseUntil = g.Lock.LeaseUntil },
            MissedTurnPolicy = g.MissedTurnPolicy, AiTakeoverAfterMisses = g.AiTakeoverAfterMisses,
            ActivePlayerCount = g.ActivePlayerCount, SubmittedCount = g.SubmittedCount,
            IsPublic = g.IsPublic, CreatedAt = g.CreatedAt,
            Options = g.Options.Copy(), WinnerEmpireId = g.WinnerEmpireId, FinishedAt = g.FinishedAt,
        };
    }
}
