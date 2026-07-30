// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System.Security.Cryptography;
using Galaxies.Api.Auth;
using Galaxies.Api.Storage;
using Galaxies.Api.Wire;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Model;
using Galaxies.ControlPlane.Scheduling;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;

namespace Galaxies.Api;

/// <summary>
/// The M4 public-launch surface: the game browser, pre-start settings and roster
/// management, invites by Gmail address, the game-over summary, the DSAR export,
/// moderation, and the per-account quotas (design Section G.1 M0 plus the
/// "per-account and global resource quotas" gap).
///
/// This is a sibling of GameOrchestrator, not a replacement: the turn clock, the
/// exactly-once guard, and the orders pipeline stay there, and everything here
/// reuses the same control plane and the same Authorization rules. It exists so
/// that GameOrchestrator stays about turns.
/// </summary>
public sealed class LaunchOrchestrator(
    IControlPlane control,
    IOrdersStore orders,
    IIntelStore intel,
    IDeadlineScheduler scheduler,
    OrchestratorOptions options,
    ILogger<LaunchOrchestrator> log)
{
    /// <summary>The first turn every Galaxies game opens on.</summary>
    private const int StartYear = 2100;

    // ================= B1: the game browser ==================================

    /// <summary>
    /// List games for the lobby and browser. Scopes: "mine" (games the caller is a
    /// member of, any state), "open" (public games still in the lobby), "public"
    /// (every public game, running or not), "finished" (public games that are over,
    /// for their summary pages).
    ///
    /// Paging is offset based over a capped page. That is honest about its limit: an
    /// offset walk re-reads the pages it skips, so it is fine for the first few
    /// pages a human will actually click through and wrong for a crawler. A cursor
    /// (Firestore StartAfter on CreatedAt) is the real answer when the browser grows
    /// past that.
    /// </summary>
    public async Task<GameListResponse> ListGamesAsync(
        SessionPrincipal caller, string? scope, int limit, int offset, CancellationToken ct)
    {
        string wanted = string.IsNullOrWhiteSpace(scope) ? "mine" : scope.Trim().ToLowerInvariant();
        int page = Math.Clamp(limit <= 0 ? 25 : limit, 1, options.MaxPageSize);
        int skip = Math.Max(0, offset);

        // Fetch one past the page so HasMore is a fact, not a guess.
        int fetch = skip + page + 1;

        IReadOnlyList<GameMeta> found = wanted switch
        {
            "mine" => await control.ListGamesForUserAsync(caller.GoogleSub, ct),
            "open" => await control.ListGamesAsync(
                new GameBrowseQuery { Lifecycle = GameLifecycle.Lobby, PublicOnly = true, Limit = fetch }, ct),
            "public" => await control.ListGamesAsync(
                new GameBrowseQuery { PublicOnly = true, Limit = fetch }, ct),
            "finished" => await control.ListGamesAsync(
                new GameBrowseQuery { Lifecycle = GameLifecycle.Finished, PublicOnly = true, Limit = fetch }, ct),
            _ => throw ApiProblem.BadRequest("scope must be one of mine, open, public, finished."),
        };

        List<GameMeta> ordered = found
            .OrderByDescending(g => g.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .ToList();
        bool hasMore = ordered.Count > skip + page;
        var window = ordered.Skip(skip).Take(page).Select(ToSummary).ToList();
        return new GameListResponse(window, wanted, page, skip, hasMore);
    }

    // ================= B2: settings ==========================================

    public async Task<GameSettingsResponse> GetSettingsAsync(string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        return ToSettings(game);
    }

    /// <summary>
    /// Host edits map size, victory conditions, and the turn cadence before the game
    /// starts. Lobby only: once the map is built from these values, changing them
    /// would describe a universe that does not exist.
    /// </summary>
    public async Task<GameSettingsResponse> UpdateSettingsAsync(
        SessionPrincipal caller, string gameId, UpdateGameSettingsRequest req, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        RequireLobby(game, "Settings can only be changed before the game starts.");

        GameOptions o = game.Options;
        bool cadenceChanged = false;

        if (req.MaxTimeBetweenTurnsSeconds is { } cadence)
        {
            if (cadence < 0)
            {
                throw ApiProblem.BadRequest("maxTimeBetweenTurnsSeconds cannot be negative (0 means no clock).");
            }
            cadenceChanged = cadence != game.MaxTimeBetweenTurnsSeconds;
            game.MaxTimeBetweenTurnsSeconds = cadence;
        }

        if (req.MapWidth is { } w) o.MapWidth = Positive(w, "mapWidth");
        if (req.MapHeight is { } h) o.MapHeight = Positive(h, "mapHeight");
        if (req.NumberOfStars is { } stars) o.NumberOfStars = Positive(stars, "numberOfStars");
        if (req.StarSeparation is { } sep) o.StarSeparation = Positive(sep, "starSeparation");
        if (req.StarDensity is { } density) o.StarDensity = Positive(density, "starDensity");
        if (req.StarUniformity is { } uniformity) o.StarUniformity = Positive(uniformity, "starUniformity");

        if (req.PlanetsOwned is { } pv) o.PlanetsOwned = Victory(pv);
        if (req.TechLevels is { } tv) o.TechLevels = Victory(tv);
        if (req.NumberOfFields is { } nv) o.NumberOfFields = Victory(nv);
        if (req.TotalScore is { } sv) o.TotalScore = Victory(sv);
        if (req.SecondPlaceScore is { } s2) o.SecondPlaceScore = Victory(s2);
        if (req.ProductionCapacity is { } pc) o.ProductionCapacity = Victory(pc);
        if (req.CapitalShips is { } cs) o.CapitalShips = Victory(cs);
        if (req.HighestScore is { } hs) o.HighestScore = Victory(hs);

        if (req.TargetsToMeet is { } targets) o.TargetsToMeet = Positive(targets, "targetsToMeet");
        if (req.MinimumGameTime is { } minTime) o.MinimumGameTime = Math.Max(0, minTime);

        // At least one victory condition must be reachable, or the game can never end.
        int enabled = new[]
        {
            o.PlanetsOwned, o.TechLevels, o.NumberOfFields, o.TotalScore,
            o.SecondPlaceScore, o.ProductionCapacity, o.CapitalShips, o.HighestScore,
        }.Count(v => v.Enabled);
        if (enabled == 0)
        {
            throw ApiProblem.BadRequest("At least one victory condition must be enabled.");
        }
        if (o.TargetsToMeet > enabled)
        {
            throw ApiProblem.BadRequest($"targetsToMeet is {o.TargetsToMeet} but only {enabled} conditions are enabled.");
        }

        if (cadenceChanged)
        {
            // The deadline is derived from the cadence, so it has to move with it.
            // In the lobby there is no anchor yet and this is null, which is correct:
            // the first deadline is armed at start. Recomputing here rather than only
            // at start means the rule holds by construction if the lobby-only
            // restriction is ever relaxed.
            game.DeadlineAt = game.LastGenerationAt is { } last
                ? Cadence.DeadlineFor(last, game.MaxTimeBetweenTurnsSeconds)
                : null;
        }

        await control.CreateGameAsync(game, ct); // upsert

        if (cadenceChanged)
        {
            // Replace any task armed against the old cadence. Both calls tolerate
            // "nothing there", which is the lobby case.
            await scheduler.CancelDeadlineAsync(gameId, game.TurnYear, ct);
            await scheduler.ScheduleDeadlineAsync(gameId, game.TurnYear, game.DeadlineAt?.ToDateTimeOffset(), ct);
        }
        return ToSettings(game);
    }

    // ================= A2/A3/B3: roster before the start ======================

    /// <summary>
    /// Host adds an AI opponent to an OPEN slot. It can never displace a human: the
    /// claim is transactional and fails if the seat is held, so a player who joined
    /// a moment earlier keeps their seat and the host gets a 409.
    /// </summary>
    public async Task<Member> AddAiPlayerAsync(
        SessionPrincipal caller, string gameId, AddAiPlayerRequest req, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        RequireLobby(game, "AI players can only be added before the game starts.");

        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        int aiSeats = members.Count(m => m.Kind == PlayerKind.Ai);
        if (aiSeats >= options.MaxAiSeatsPerGame)
        {
            throw ApiProblem.TooManyRequests(
                $"This game already has {aiSeats} AI seats; the limit is {options.MaxAiSeatsPerGame} per game.");
        }

        Member seat = members.FirstOrDefault(m => m.EmpireId == req.EmpireId)
            ?? throw ApiProblem.NotFound($"No seat {req.EmpireId} in game {gameId}.");
        if (!seat.IsOpenSlot)
        {
            throw ApiProblem.Conflict($"Seat {req.EmpireId} is already taken.");
        }

        string participant = Blank(req.ParticipantId) ? options.DefaultAiParticipantId : req.ParticipantId!.Trim();
        string difficulty = Blank(req.Difficulty) ? options.DefaultAiDifficulty : req.Difficulty!.Trim();
        string race = req.Race?.Trim() ?? "";

        Member? claimed = await control.ClaimSlotForAiAsync(gameId, req.EmpireId, participant, difficulty, race, ct);
        if (claimed is null)
        {
            throw ApiProblem.Conflict($"Seat {req.EmpireId} was taken while the AI was being added.");
        }
        log.LogInformation("AI {Participant} took seat {EmpireId} in {GameId}.", participant, req.EmpireId, gameId);
        return claimed;
    }

    /// <summary>Host removes a player or an AI before the game starts.</summary>
    public async Task RemovePlayerAsync(SessionPrincipal caller, string gameId, int empireId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        RequireLobby(game, "Players can only be removed before the game starts.");

        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member seat = members.FirstOrDefault(m => m.EmpireId == empireId)
            ?? throw ApiProblem.NotFound($"No seat {empireId} in game {gameId}.");
        if (seat.AccountId == game.HostAccountId && seat.Kind == PlayerKind.Human)
        {
            throw ApiProblem.Conflict("The host cannot be removed; delete the game instead.");
        }
        await control.ReleaseSlotAsync(gameId, empireId, expectedAccountId: null, ct);
    }

    /// <summary>
    /// Leave before the start. The seat goes back to being an open slot so someone
    /// else can take it. After the start this is resignation, which is a different
    /// thing with different consequences for the running universe, and is not this.
    /// </summary>
    public async Task LeaveAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        RequireLobby(game, "You cannot leave a game that has started; resign instead.");

        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        if (game.HostAccountId == caller.GoogleSub)
        {
            throw ApiProblem.Conflict("The host cannot leave; delete the game or transfer the host first.");
        }
        await control.ReleaseSlotAsync(gameId, mine.EmpireId, caller.GoogleSub, ct);
    }

    // ================= B4: invites ===========================================

    public async Task<InviteResponse> CreateInviteAsync(
        SessionPrincipal caller, string gameId, CreateInviteRequest req, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        RequireLobby(game, "Invites can only be sent while the game is in the lobby.");

        string email = (req.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0 || !email.Contains('@'))
        {
            throw ApiProblem.BadRequest("A valid email address is required.");
        }

        // One live invite per address per game; re-inviting returns the existing one
        // so a host clicking twice does not scatter tokens.
        IReadOnlyList<Invite> existing = await control.ListInvitesForGameAsync(gameId, ct);
        Invite? already = existing.FirstOrDefault(i =>
            i.Status == InviteStatus.Pending &&
            string.Equals(i.InvitedEmail, email, StringComparison.OrdinalIgnoreCase));
        if (already is not null)
        {
            return ToInvite(already, includeToken: true);
        }

        var invite = new Invite
        {
            InviteId = Guid.NewGuid().ToString("N"),
            GameId = gameId,
            InvitedEmail = email,
            Token = NewToken(),
            Status = InviteStatus.Pending,
            CreatedByAccountId = caller.GoogleSub,
            CreatedAt = Timestamp.GetCurrentTimestamp(),
        };
        await control.CreateInviteAsync(invite, ct);
        log.LogInformation("Invite {InviteId} created for {GameId}.", invite.InviteId, gameId);
        return ToInvite(invite, includeToken: true);
    }

    public async Task<IReadOnlyList<InviteResponse>> ListInvitesAsync(
        SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        IReadOnlyList<Invite> invites = await control.ListInvitesForGameAsync(gameId, ct);
        return invites
            .OrderByDescending(i => i.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .Select(i => ToInvite(i, includeToken: true))
            .ToList();
    }

    public async Task RevokeInviteAsync(SessionPrincipal caller, string gameId, string inviteId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);

        Invite invite = await control.GetInviteAsync(inviteId, ct)
            ?? throw ApiProblem.NotFound($"No invite {inviteId}.");
        if (invite.GameId != gameId)
        {
            throw ApiProblem.NotFound($"Invite {inviteId} does not belong to game {gameId}.");
        }
        if (invite.Status == InviteStatus.Accepted)
        {
            throw ApiProblem.Conflict("That invite has already been accepted; remove the player instead.");
        }
        invite.Status = InviteStatus.Revoked;
        invite.RevokedAt = Timestamp.GetCurrentTimestamp();
        await control.UpdateInviteAsync(invite, ct);
    }

    /// <summary>
    /// Accept an invite and take a seat. Holding the token is not enough: the
    /// session's verified email must be the invited address, so a forwarded or
    /// leaked link cannot let a stranger into a private game. This is the moment an
    /// invite that was addressed to an email becomes bound to a Google subject.
    /// </summary>
    public async Task<AcceptInviteResponse> AcceptInviteAsync(SessionPrincipal caller, string token, CancellationToken ct)
    {
        Invite invite = await control.GetInviteByTokenAsync(token, ct)
            ?? throw ApiProblem.NotFound("That invite link is not valid.");

        if (invite.Status == InviteStatus.Revoked)
        {
            throw ApiProblem.Conflict("That invite was withdrawn.");
        }
        if (!string.Equals(invite.InvitedEmail, caller.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw ApiProblem.Forbidden($"That invite was sent to a different address ({invite.InvitedEmail}).");
        }

        GameMeta game = await Require(invite.GameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(invite.GameId, ct);

        // Idempotent: accepting twice returns the seat already bound.
        if (invite.Status == InviteStatus.Accepted && invite.AcceptedByAccountId == caller.GoogleSub)
        {
            Member? bound = members.FirstOrDefault(m => m.AccountId == caller.GoogleSub);
            if (bound is not null)
            {
                return new AcceptInviteResponse(invite.GameId, ToPlayer(bound));
            }
        }
        if (invite.Status == InviteStatus.Accepted)
        {
            throw ApiProblem.Conflict("That invite has already been used.");
        }
        RequireLobby(game, "That game has already started.");

        Member? seat = members.FirstOrDefault(m => m.AccountId == caller.GoogleSub);
        if (seat is null)
        {
            int? wanted = invite.EmpireId;
            IEnumerable<Member> candidates = wanted is { } id
                ? members.Where(m => m.EmpireId == id)
                : members.Where(m => m.IsOpenSlot).OrderBy(m => m.EmpireId);

            foreach (Member open in candidates)
            {
                seat = await control.JoinOpenSlotAsync(invite.GameId, open.EmpireId, caller.GoogleSub, race: "", ct);
                if (seat is not null) break;
            }
        }
        if (seat is null)
        {
            throw ApiProblem.Conflict("That game is full.");
        }

        invite.Status = InviteStatus.Accepted;
        invite.AcceptedByAccountId = caller.GoogleSub;
        invite.EmpireId = seat.EmpireId;
        invite.AcceptedAt = Timestamp.GetCurrentTimestamp();
        await control.UpdateInviteAsync(invite, ct);

        return new AcceptInviteResponse(invite.GameId, ToPlayer(seat));
    }

    // ================= B5: the game-over summary =============================

    /// <summary>
    /// The game-over summary (design Section G.1, "game-over summary").
    ///
    /// Honest about its limits. What the control plane knows is exactly what is
    /// returned: the final turn year, how long the game ran, the roster with each
    /// seat's kind and race, and the winner IF a turn generation reported one. What
    /// it does NOT know is the score table, and it cannot: score rows live in the
    /// final ServerData blob in GCS, and producing real standings means downloading
    /// a multi-megabyte universe and running the engine's Scores over it. That is a
    /// batch job, not a synchronous read on a public page, so this route returns the
    /// blob's path and lets a summary builder do it out of band. Standings here are
    /// therefore a roster in empire order, not a ranking, and say so.
    /// </summary>
    public async Task<GameOverSummaryResponse> GetSummaryAsync(string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        if (game.Lifecycle != GameLifecycle.Finished)
        {
            throw ApiProblem.Conflict($"Game {gameId} is {game.Lifecycle}; a summary exists only once it is Finished.");
        }

        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        var standings = members
            .Where(m => m.AccountId is not null)
            .OrderBy(m => m.EmpireId)
            .Select(m => new StandingResponse(
                m.EmpireId, m.AccountId, m.Kind.ToString(), m.Race, m.Resigned, m.AiTakeover, m.LastSubmittedTurn))
            .ToList();

        // TurnYear is the turn the game WOULD be on; the last one actually played is
        // the one before it.
        int finalYear = Math.Max(StartYear, game.TurnYear - 1);

        return new GameOverSummaryResponse(
            game.GameId,
            game.Name,
            game.Lifecycle.ToString(),
            StartYear,
            finalYear,
            finalYear - StartYear + 1,
            game.WinnerEmpireId,
            game.FinishedAt?.ToDateTimeOffset(),
            game.CurrentStatePath,
            standings,
            "Standings list the final roster in empire order, not a ranking. Score rows, resources, " +
            "and tech levels live in the final state blob at finalStatePath and are not computed here.");
    }

    // ================= B6: the DSAR export ===================================

    /// <summary>
    /// The data-subject-access export (design Section C.5, a launch-gate legal
    /// requirement). It returns everything the service holds ABOUT the caller: their
    /// account record, every seat they hold or held, and the invites addressed to
    /// them.
    ///
    /// The orders and intel objects are returned as gs:// references with counts,
    /// not inlined. That is a deliberate design choice, not an omission: a long game
    /// holds tens of megabytes of orders and intel per empire, and a synchronous
    /// JSON response carrying all of it for every game would time out, cost more to
    /// serve than it informs, and be unusable to the person who asked. The
    /// references are complete, so a follow-up job can fetch the bodies; the legal
    /// duty is to disclose what is held and give access to it, which this does.
    /// It never contains another player's private view.
    /// </summary>
    public async Task<AccountExportResponse> ExportAccountAsync(SessionPrincipal caller, CancellationToken ct)
    {
        UserAccount user = await control.GetUserAsync(caller.GoogleSub, ct)
            ?? throw ApiProblem.NotFound("No account.");

        IReadOnlyList<GameMeta> games = await control.ListGamesForUserAsync(caller.GoogleSub, ct);
        var memberships = new List<AccountExportMembership>();
        var invites = new List<InviteResponse>();

        foreach (GameMeta game in games.DistinctBy(g => g.GameId))
        {
            IReadOnlyList<Member> members = await control.GetMembersAsync(game.GameId, ct);
            Member? mine = members.FirstOrDefault(m => m.AccountId == caller.GoogleSub);
            if (mine is null)
            {
                continue;
            }

            IReadOnlyList<string> orderObjects = await orders.ListForEmpireAsync(game.GameId, mine.EmpireId, ct);
            IReadOnlyList<string> intelObjects = await intel.ListForEmpireAsync(game.GameId, mine.EmpireId, ct);

            memberships.Add(new AccountExportMembership(
                game.GameId, game.Name, game.Lifecycle.ToString(), game.TurnYear,
                mine.EmpireId, mine.Kind.ToString(), mine.Race, mine.Resigned,
                orderObjects.Count, orderObjects,
                intelObjects.Count, intelObjects));

            // Invites addressed to this person are held about them, so they belong
            // in the bundle. The token is withheld: it is a capability, and a
            // download is not the place to hand one out again.
            IReadOnlyList<Invite> gameInvites = await control.ListInvitesForGameAsync(game.GameId, ct);
            invites.AddRange(gameInvites
                .Where(i => string.Equals(i.InvitedEmail, user.Email, StringComparison.OrdinalIgnoreCase)
                            || i.AcceptedByAccountId == caller.GoogleSub)
                .Select(i => ToInvite(i, includeToken: false)));
        }

        return new AccountExportResponse(
            user.GoogleSub, user.Email, user.DisplayName, user.AvatarUrl, user.Roles.ToArray(),
            user.CreatedAt?.ToDateTimeOffset(), user.DeletedAt?.ToDateTimeOffset(),
            memberships, invites, DateTimeOffset.UtcNow,
            "Orders and intel are listed as gs:// references with counts rather than inlined, because a " +
            "full game's blobs are too large to return in one synchronous response. Every object you own " +
            "is referenced here.");
    }

    // ================= B7: moderation and admin ==============================

    /// <summary>Any authenticated player may report abuse; only a moderator reads the queue.</summary>
    public async Task<ReportResponse> CreateReportAsync(SessionPrincipal caller, CreateReportRequest req, CancellationToken ct)
    {
        string type = (req.TargetType ?? "").Trim().ToLowerInvariant();
        if (type is not ("user" or "game" or "message"))
        {
            throw ApiProblem.BadRequest("targetType must be one of user, game, message.");
        }
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            throw ApiProblem.BadRequest("targetId is required.");
        }
        if (string.IsNullOrWhiteSpace(req.Reason))
        {
            throw ApiProblem.BadRequest("reason is required.");
        }

        var report = new AbuseReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            ReporterAccountId = caller.GoogleSub,
            TargetType = type,
            TargetId = req.TargetId.Trim(),
            Reason = req.Reason.Trim(),
            Status = ReportStatus.Open,
            CreatedAt = Timestamp.GetCurrentTimestamp(),
        };
        await control.CreateReportAsync(report, ct);
        log.LogInformation("Abuse report {ReportId} filed against {TargetType} {TargetId}.",
            report.ReportId, report.TargetType, report.TargetId);
        return ToReport(report);
    }

    public async Task<IReadOnlyList<ReportResponse>> ListReportsAsync(
        SessionPrincipal caller, string? status, int limit, CancellationToken ct)
    {
        Authorization.RequireAnyRole(caller, "moderator", "admin");
        ReportStatus? filter = string.IsNullOrWhiteSpace(status)
            ? null
            : Enum.TryParse<ReportStatus>(status, ignoreCase: true, out ReportStatus s)
                ? s
                : throw ApiProblem.BadRequest("status must be Open or Resolved.");

        int page = Math.Clamp(limit <= 0 ? 50 : limit, 1, options.MaxPageSize);
        IReadOnlyList<AbuseReport> found = await control.ListReportsAsync(filter, page, ct);
        return found.Select(ToReport).ToList();
    }

    public async Task<ReportResponse> ResolveReportAsync(
        SessionPrincipal caller, string reportId, ResolveReportRequest req, CancellationToken ct)
    {
        Authorization.RequireAnyRole(caller, "moderator", "admin");
        AbuseReport report = await control.GetReportAsync(reportId, ct)
            ?? throw ApiProblem.NotFound($"No report {reportId}.");

        report.Status = ReportStatus.Resolved;
        report.Resolution = req.Resolution?.Trim();
        report.ResolvedByAccountId = caller.GoogleSub;
        report.ResolvedAt = Timestamp.GetCurrentTimestamp();
        await control.UpdateReportAsync(report, ct);
        return ToReport(report);
    }

    /// <summary>
    /// Ban an account. The ban document's existence is the ban, and the session path
    /// checks for it on every authenticated request, so the next call a banned user
    /// makes fails even though their JWT is still cryptographically valid.
    /// </summary>
    public async Task<BanResponse> BanAsync(SessionPrincipal caller, string googleSub, BanRequest req, CancellationToken ct)
    {
        Authorization.RequireAnyRole(caller, "moderator", "admin");
        if (string.Equals(googleSub, caller.GoogleSub, StringComparison.Ordinal))
        {
            throw ApiProblem.BadRequest("You cannot ban yourself.");
        }

        var ban = new Ban
        {
            GoogleSub = googleSub,
            Reason = req.Reason?.Trim() ?? "",
            BannedByAccountId = caller.GoogleSub,
            CreatedAt = Timestamp.GetCurrentTimestamp(),
        };
        await control.SetBanAsync(ban, ct);
        log.LogWarning("Account {GoogleSub} banned by {Moderator}.", googleSub, caller.GoogleSub);
        return new BanResponse(ban.GoogleSub, ban.Reason, ban.BannedByAccountId, ban.CreatedAt?.ToDateTimeOffset());
    }

    public async Task UnbanAsync(SessionPrincipal caller, string googleSub, CancellationToken ct)
    {
        Authorization.RequireAnyRole(caller, "moderator", "admin");
        await control.RemoveBanAsync(googleSub, ct);
        log.LogWarning("Account {GoogleSub} unbanned by {Moderator}.", googleSub, caller.GoogleSub);
    }

    /// <summary>
    /// Host or admin abandons a game. A game that never started is deleted outright,
    /// because nothing happened in it worth keeping. A game that has run is
    /// Cancelled instead: other people played those turns, and the record of them
    /// belongs to those players as much as to the host.
    /// </summary>
    public async Task DeleteGameAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);

        await scheduler.CancelDeadlineAsync(gameId, game.TurnYear, ct);

        if (game.Lifecycle is GameLifecycle.Draft or GameLifecycle.Lobby)
        {
            await control.DeleteGameAsync(gameId, ct);
            log.LogInformation("Pre-start game {GameId} deleted by {Account}.", gameId, caller.GoogleSub);
            return;
        }
        // Throws InvalidLifecycleTransitionException (mapped to 409) if the game is
        // already over, which is the right answer to "abandon a finished game".
        await control.TransitionLifecycleAsync(gameId, GameLifecycle.Cancelled, ct);
        log.LogInformation("Game {GameId} cancelled by {Account}.", gameId, caller.GoogleSub);
    }

    // ================= Helpers ===============================================

    private async Task<GameMeta> Require(string gameId, CancellationToken ct)
        => await control.GetGameAsync(gameId, ct) ?? throw ApiProblem.NotFound($"No game {gameId}.");

    private static void RequireLobby(GameMeta game, string message)
    {
        if (game.Lifecycle != GameLifecycle.Lobby)
        {
            throw ApiProblem.Conflict(message);
        }
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static int Positive(int value, string field)
        => value > 0 ? value : throw ApiProblem.BadRequest($"{field} must be greater than zero.");

    private static VictoryCondition Victory(VictoryConditionDto dto)
        => new(dto.Enabled, Math.Max(0, dto.Value));

    /// <summary>
    /// 256 bits from the OS CSPRNG, url-safe. The token is a capability, so guessing
    /// it must be hopeless even though accepting also requires a matching email.
    /// </summary>
    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static GameSummaryResponse ToSummary(GameMeta g) => new(
        g.GameId, g.Name, g.Lifecycle.ToString(), g.Generation.ToString(), g.TurnYear,
        g.DeadlineAt?.ToDateTimeOffset(), g.ActivePlayerCount, g.SubmittedCount, g.IsPublic);

    private static PlayerResponse ToPlayer(Member m)
        => new(m.EmpireId, m.AccountId, m.Kind.ToString(), m.Race, m.TurnSubmitted);

    private static InviteResponse ToInvite(Invite i, bool includeToken) => new(
        i.InviteId, i.GameId, i.InvitedEmail, i.Status.ToString(),
        includeToken ? i.Token : null, i.EmpireId,
        i.CreatedAt?.ToDateTimeOffset(), i.AcceptedAt?.ToDateTimeOffset());

    private static ReportResponse ToReport(AbuseReport r) => new(
        r.ReportId, r.ReporterAccountId, r.TargetType, r.TargetId, r.Reason,
        r.Status.ToString(), r.Resolution, r.ResolvedByAccountId,
        r.CreatedAt?.ToDateTimeOffset(), r.ResolvedAt?.ToDateTimeOffset());

    private static GameSettingsResponse ToSettings(GameMeta g)
    {
        GameOptions o = g.Options;
        return new GameSettingsResponse(
            g.GameId, g.Lifecycle.ToString(), g.Lifecycle == GameLifecycle.Lobby,
            g.MaxTimeBetweenTurnsSeconds,
            o.MapWidth, o.MapHeight, o.NumberOfStars, o.StarSeparation, o.StarDensity, o.StarUniformity,
            V(o.PlanetsOwned), V(o.TechLevels), V(o.NumberOfFields), V(o.TotalScore),
            V(o.SecondPlaceScore), V(o.ProductionCapacity), V(o.CapitalShips), V(o.HighestScore),
            o.TargetsToMeet, o.MinimumGameTime);

        static VictoryConditionDto V(VictoryCondition c) => new(c.Enabled, c.Value);
    }
}
