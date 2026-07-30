// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Text;
using System.Xml;
using Galaxies.Api.Auth;
using Galaxies.Api.Generation;
using Galaxies.Api.Storage;
using Galaxies.Api.Wire;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Eventing;
using Galaxies.ControlPlane.Model;
using Galaxies.ControlPlane.Scheduling;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Nova.Common.Commands;

namespace Galaxies.Api;

/// <summary>
/// Tunables and feature flags for the orchestrators. Everything new ships dark:
/// the defaults here are the OFF state, so a deploy that forgets to set a flag is
/// a deploy that changes nothing.
/// </summary>
public sealed class OrchestratorOptions
{
    public int GenerationLeaseSeconds { get; init; } = 300;

    /// <summary>API_AI_ORDERS_ENABLED: the internal AI orders route (M3).</summary>
    public bool AiOrdersEnabled { get; init; }

    /// <summary>
    /// API_AI_TAKEOVER_ENABLED: whether the missed-turn ladder actually flips a seat
    /// to AI. While this is off the handoff is still computed and published on the
    /// turn-generated event, so the behaviour can be watched in production before it
    /// is armed.
    /// </summary>
    public bool AiTakeoverEnabled { get; init; }

    /// <summary>API_LAUNCH_GATE_ENABLED: the M4 browser, invites, moderation, export.</summary>
    public bool LaunchGateEnabled { get; init; }

    /// <summary>GALAXIES_MAX_GAMES_PER_ACCOUNT.</summary>
    public int MaxGamesPerAccount { get; init; } = 20;

    /// <summary>GALAXIES_MAX_AI_SEATS_PER_GAME.</summary>
    public int MaxAiSeatsPerGame { get; init; } = 15;

    /// <summary>Largest page the game browser will serve, whatever the caller asks for.</summary>
    public int MaxPageSize { get; init; } = 100;

    public string DefaultAiParticipantId { get; init; } = "galaxies.default-ai";
    public string DefaultAiDifficulty { get; init; } = "normal";
}

/// <summary>
/// The application service behind the endpoints. It composes the control plane
/// (games, membership, submission, the exactly-once lock), the GCS order/intel
/// stores, the turngen worker, the deadline scheduler, and the turn-generated
/// publisher into the game lifecycle and the turn clock (design Sections D, E).
/// Endpoints stay thin; every rule and every state change lives here.
/// </summary>
public sealed class GameOrchestrator(
    IControlPlane control,
    IOrdersStore orders,
    IIntelStore intel,
    ITurnGenerationInvoker turngen,
    IDeadlineScheduler scheduler,
    ITurnEventPublisher events,
    OrchestratorOptions options,
    ILogger<GameOrchestrator> log)
{
    // ---- Lobby and lifecycle (M1) -------------------------------------------

    public async Task<GameMeta> CreateGameAsync(SessionPrincipal caller, CreateGameRequest req, CancellationToken ct)
    {
        if (req.NumberOfPlayers is < 1 or > 16)
        {
            throw ApiProblem.BadRequest("A game has 1 to 16 players.");
        }

        // Per-account quota (design gap "per-account and global resource quotas").
        // Free Google-only signup with no cap lets one account create thousands of
        // games and move the bill; finished games do not count, so the cap limits
        // concurrent load, not lifetime play.
        int live = await control.CountLiveGamesHostedByAsync(caller.GoogleSub, ct);
        if (live >= options.MaxGamesPerAccount)
        {
            throw ApiProblem.TooManyRequests(
                $"You already host {live} unfinished games; the limit is {options.MaxGamesPerAccount} per account. " +
                "Finish, cancel, or delete one first.");
        }

        string gameId = Guid.NewGuid().ToString("N")[..12];
        var game = new GameMeta
        {
            GameId = gameId,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Untitled" : req.Name.Trim(),
            HostAccountId = caller.GoogleSub,
            Lifecycle = GameLifecycle.Lobby,
            Generation = GenerationState.Idle,
            TurnYear = 2100,
            MaxTimeBetweenTurnsSeconds = req.MaxTimeBetweenTurnsSeconds,
            IsPublic = req.IsPublic,
            ActivePlayerCount = req.NumberOfPlayers,
            MissedTurnPolicy = ParsePolicy(req.MissedTurnPolicy),
            CreatedAt = Timestamp.GetCurrentTimestamp(),
        };
        await control.CreateGameAsync(game, ct);

        // Seed one open slot per player. The host takes empire 1; the rest are open.
        for (int empireId = 1; empireId <= req.NumberOfPlayers; empireId++)
        {
            var member = new Member
            {
                EmpireId = empireId,
                AccountId = empireId == 1 ? caller.GoogleSub : null,
                Kind = PlayerKind.Human,
                Race = "",
            };
            await control.AddMemberAsync(gameId, member, ct);
        }
        return game;
    }

    public Task<GameMeta?> GetGameAsync(string gameId, CancellationToken ct) => control.GetGameAsync(gameId, ct);

    public Task<IReadOnlyList<Member>> GetPlayersAsync(string gameId, CancellationToken ct) => control.GetMembersAsync(gameId, ct);

    public async Task<Member> JoinAsync(SessionPrincipal caller, string gameId, JoinGameRequest req, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        if (game.Lifecycle != GameLifecycle.Lobby)
        {
            throw ApiProblem.Conflict("This game is not open to join.");
        }
        await RequireInvitedIfPrivateAsync(game, caller, ct);
        Member? joined = await control.JoinOpenSlotAsync(gameId, req.EmpireId, caller.GoogleSub, req.Race, ct);
        return joined ?? throw ApiProblem.Conflict("That seat is taken.");
    }

    public async Task<GameMeta> StartAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);

        // Lock the lobby and start the clock. Server-side map/empire initialisation
        // (turn 2100 intel) is dispatched to turngen; here we transition and arm the
        // first deadline. The initial-generation dispatch mirrors EvaluateGeneration.
        GameMeta active = await control.TransitionLifecycleAsync(gameId, GameLifecycle.Active, ct);

        Timestamp now = Timestamp.GetCurrentTimestamp();
        active.LastGenerationAt = now;
        active.DeadlineAt = Cadence.DeadlineFor(now, active.MaxTimeBetweenTurnsSeconds);
        await control.CreateGameAsync(active, ct); // upsert with the new clock anchor

        await scheduler.ScheduleDeadlineAsync(gameId, active.TurnYear, active.DeadlineAt?.ToDateTimeOffset(), ct);
        log.LogInformation("Game {GameId} started at turn {Turn}", gameId, active.TurnYear);
        return active;
    }

    public async Task<GameMeta> HostTransitionAsync(SessionPrincipal caller, string gameId, GameLifecycle to, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        GameMeta updated = await control.TransitionLifecycleAsync(gameId, to, ct);

        // Pausing cancels the pending deadline task; resuming re-arms it.
        if (to == GameLifecycle.Paused)
        {
            await scheduler.CancelDeadlineAsync(gameId, updated.TurnYear, ct);
        }
        else if (to == GameLifecycle.Active)
        {
            await scheduler.ScheduleDeadlineAsync(gameId, updated.TurnYear, updated.DeadlineAt?.ToDateTimeOffset(), ct);
        }
        return updated;
    }

    public async Task<GameMeta> ExtendDeadlineAsync(SessionPrincipal caller, string gameId, long addSeconds, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        DateTimeOffset baseTime = game.DeadlineAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        game.DeadlineAt = Timestamp.FromDateTimeOffset(baseTime.AddSeconds(addSeconds));
        await control.CreateGameAsync(game, ct);
        await scheduler.CancelDeadlineAsync(gameId, game.TurnYear, ct);
        await scheduler.ScheduleDeadlineAsync(gameId, game.TurnYear, game.DeadlineAt?.ToDateTimeOffset(), ct);
        return game;
    }

    // ---- Orders (M1) --------------------------------------------------------

    public async Task PutOrdersAsync(SessionPrincipal caller, string gameId, OrdersRequest req, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        Authorization.RequireOpenTurn(game, req.TurnYear);

        ValidateOrdersStructure(req.OrdersXml, mine.EmpireId, req.TurnYear);

        await orders.PutAsync(gameId, req.TurnYear, mine.EmpireId, Encoding.UTF8.GetBytes(req.OrdersXml), ct);
        // A fresh draft clears any prior "submitted" flag for this turn.
        await control.RecordSubmissionAsync(gameId, mine.EmpireId, req.TurnYear, submitted: false, ct);
    }

    public async Task<OrdersResponse?> GetOrdersAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        byte[]? xml = await orders.GetAsync(gameId, game.TurnYear, mine.EmpireId, ct);
        return xml is null ? null : new OrdersResponse(game.TurnYear, Encoding.UTF8.GetString(xml), mine.TurnSubmitted);
    }

    public async Task<GameMeta> SubmitOrdersAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        Authorization.RequireOpenTurn(game, game.TurnYear);

        // Must have a draft to submit.
        if (await orders.GetAsync(gameId, game.TurnYear, mine.EmpireId, ct) is null)
        {
            throw ApiProblem.BadRequest("No draft orders to submit.");
        }

        GameMeta after = await control.RecordSubmissionAsync(gameId, mine.EmpireId, game.TurnYear, submitted: true, ct);

        // Trigger 1 (design B.2): everyone submitted, generate now without waiting.
        if (Cadence.EveryoneSubmitted(after))
        {
            await EvaluateGenerationAsync(gameId, after.TurnYear, deadlineReached: false, ct);
        }
        return after;
    }

    public async Task UnsubmitAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        Authorization.RequireOpenTurn(game, game.TurnYear);
        await control.RecordSubmissionAsync(gameId, mine.EmpireId, game.TurnYear, submitted: false, ct);
    }

    // ---- AI orders (M3, galaxies-ai spec Section 7.3) ------------------------

    /// <summary>
    /// The internal route galaxies-ai submits an AI seat's orders through. The whole
    /// point is that this is NOT a second orders pipeline: it runs the same
    /// structural validation and writes the same object to the same bucket as a
    /// human PUT, so turngen's OrderReader and CommandRegistry see one kind of
    /// orders file and the engine cannot tell an AI turn from a human one.
    ///
    /// The two things that differ are both about identity, not content: the caller
    /// is a service (Cloud Run IAM OIDC, no session), and the empire is named
    /// explicitly in the route rather than derived from a session. That is safe only
    /// because the seat itself is checked to be AI driven; a human seat is never
    /// submittable this way, so the R3 rule that a caller cannot act as another
    /// empire still holds.
    /// </summary>
    public async Task<AiOrdersResponse> SubmitAiOrdersAsync(string gameId, int empireId, AiOrdersRequest req, CancellationToken ct)
    {
        if (!options.AiOrdersEnabled)
        {
            throw ApiProblem.Forbidden("AI orders are disabled (API_AI_ORDERS_ENABLED).");
        }

        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member seat = members.FirstOrDefault(m => m.EmpireId == empireId)
            ?? throw ApiProblem.NotFound($"No seat {empireId} in game {gameId}.");

        Authorization.RequireAiSeat(seat);
        Authorization.RequireOpenTurn(game, req.TurnYear);

        bool wrote = false;
        if (!req.Held)
        {
            if (string.IsNullOrWhiteSpace(req.OrdersXml))
            {
                throw ApiProblem.BadRequest("ordersXml is required unless held is true.");
            }
            ValidateOrdersStructure(req.OrdersXml, empireId, req.TurnYear);
            await orders.PutAsync(gameId, req.TurnYear, empireId, Encoding.UTF8.GetBytes(req.OrdersXml), ct);
            wrote = true;
        }
        // held == true: the participant timed out, failed, or returned nothing
        // usable. Mark the seat submitted so the turn is not blocked, but leave its
        // existing orders object alone; the engine tolerates a seat with no orders
        // and simply does not change that empire's plans.

        GameMeta after = await control.RecordSubmissionAsync(gameId, empireId, req.TurnYear, submitted: true, ct);

        // The same early-generation trigger the human submit path uses.
        if (Cadence.EveryoneSubmitted(after))
        {
            await EvaluateGenerationAsync(gameId, after.TurnYear, deadlineReached: false, ct);
        }

        log.LogInformation("AI seat {EmpireId} submitted {GameId}@{Turn} (held={Held}).",
            empireId, gameId, req.TurnYear, req.Held);
        return new AiOrdersResponse(req.TurnYear, empireId, req.Held, wrote);
    }

    // ---- Intel and status (M1) ----------------------------------------------

    public async Task<IntelResponse> GetIntelAsync(SessionPrincipal caller, string gameId, int? turnYear, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        Member mine = Authorization.ResolveOwnEmpire(caller, members);
        int year = turnYear ?? game.TurnYear;

        byte[]? xml = await intel.GetAsync(gameId, year, mine.EmpireId, ct);
        if (xml is null)
        {
            throw ApiProblem.NotFound($"No intel for turn {year}.");
        }
        return new IntelResponse(year, Encoding.UTF8.GetString(xml));
    }

    public async Task<StatusResponse> GetStatusAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
        // Membership is required to see status (R2), but any member may.
        Authorization.ResolveOwnEmpire(caller, members);
        int[] submitted = members.Where(m => m.TurnSubmitted).Select(m => m.EmpireId).ToArray();
        return new StatusResponse(
            game.TurnYear, game.Lifecycle.ToString(), game.Generation.ToString(),
            game.DeadlineAt?.ToDateTimeOffset(), game.ActivePlayerCount, game.SubmittedCount, submitted);
    }

    // ---- The clock: exactly-once generation (M2) ----------------------------

    /// <summary>
    /// Evaluate whether to generate the given turn, and if so do it exactly once
    /// (design Section B.2). Called by three triggers: everyone submitted, the
    /// deadline fired, or the host forced it. The claim/commit around the turngen
    /// call is the exactly-once guarantee, keyed on (gameId, turnYear).
    /// </summary>
    public async Task EvaluateGenerationAsync(string gameId, int turnYear, bool deadlineReached, CancellationToken ct)
    {
        string workerToken = Guid.NewGuid().ToString("N");
        GenerationClaim claim = await control.TryClaimGenerationAsync(gameId, turnYear, workerToken, options.GenerationLeaseSeconds, ct);
        if (!claim.Won)
        {
            log.LogInformation("Generation of {GameId}@{Turn} not claimed ({Outcome}).", gameId, turnYear, claim.Outcome);
            return; // stale/duplicate trigger, or a live lock owns it; drop.
        }

        try
        {
            // The missed-turn ladder runs BEFORE the engine, so a seat handed to AI
            // this turn is already an AI seat when turngen reads the roster and when
            // galaxies-ai reads the turn-generated event.
            IReadOnlyList<Member> members = await control.GetMembersAsync(gameId, ct);
            int[] handoffs = await ApplyMissedTurnLadderAsync(gameId, claim.Game, members, ct);

            // turngen loads state + orders from GCS, runs the engine, writes the new
            // state and per-empire intel back, and reports the result.
            TurnGenerationResult result = await turngen.GenerateAsync(gameId, turnYear, ct);

            bool committed = await control.CommitGenerationAsync(gameId, new GenerationCommit
            {
                TurnYear = turnYear,
                WorkerToken = workerToken,
                NewStatePath = result.NewStatePath,
                GameEnded = result.GameEnded,
                WinnerEmpireId = result.WinnerEmpireId,
            }, ct);

            if (!committed)
            {
                // Our lease expired and someone else took over; discard our results.
                log.LogWarning("Generation of {GameId}@{Turn} lost the commit race.", gameId, turnYear);
                return;
            }

            await control.OpenNextTurnAsync(gameId, ct);

            // Arm the next deadline and fan the result out.
            GameMeta? updated = await control.GetGameAsync(gameId, ct);
            if (updated is not null && updated.Lifecycle == GameLifecycle.Active)
            {
                await scheduler.ScheduleDeadlineAsync(gameId, updated.TurnYear, updated.DeadlineAt?.ToDateTimeOffset(), ct);
            }
            // Every AI seat, so galaxies-ai knows who to dispatch next turn, plus the
            // seats handed over this turn so it can act on the change specifically.
            // The control plane is authoritative for the roster; turngen's view is
            // unioned in rather than trusted alone, since it may know of AI seats
            // added inside the engine.
            int[] aiSeats = members
                .Where(m => m.IsActive && (m.Kind == PlayerKind.Ai || handoffs.Contains(m.EmpireId)))
                .Select(m => m.EmpireId)
                .Union(result.AiEmpireIds)
                .OrderBy(id => id)
                .ToArray();

            await events.PublishTurnGeneratedAsync(new TurnGeneratedEvent
            {
                GameId = gameId,
                TurnYear = turnYear,
                EmpireIds = result.EmpireIds,
                AiEmpireIds = aiSeats,
                GameEnded = result.GameEnded,
                Handoffs = handoffs,
            }, ct);
        }
        catch (Exception e)
        {
            // Leave the lock to expire; the sweep will retry. Do not commit.
            log.LogError(e, "Generation of {GameId}@{Turn} failed.", gameId, turnYear);
            throw;
        }
    }

    /// <summary>Host force-generate: apply the missed-turn policy and generate now.</summary>
    public async Task ForceGenerateAsync(SessionPrincipal caller, string gameId, CancellationToken ct)
    {
        GameMeta game = await Require(gameId, ct);
        Authorization.RequireHost(caller, game);
        await EvaluateGenerationAsync(gameId, game.TurnYear, deadlineReached: true, ct);
    }

    /// <summary>Cloud Tasks deadline callback (design B.2 trigger 2).</summary>
    public Task OnDeadlineFireAsync(string gameId, int turnYear, CancellationToken ct)
        => EvaluateGenerationAsync(gameId, turnYear, deadlineReached: true, ct);

    /// <summary>Cloud Scheduler backstop sweep: re-arm any overdue game a lost task missed.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        IReadOnlyList<GameMeta> overdue = await control.ListOverdueGamesAsync(ct);
        foreach (GameMeta g in overdue)
        {
            try
            {
                await EvaluateGenerationAsync(g.GameId, g.TurnYear, deadlineReached: true, ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Sweep failed for {GameId}.", g.GameId);
            }
        }
    }

    /// <summary>
    /// Apply the missed-turn ladder (design Section D.2) to every active seat just
    /// before a turn generates, and return the seats handed to AI.
    ///
    /// Two deliberate choices. First, a handoff KEEPS the absent human on
    /// Member.AccountId and only flips Kind, so the seat is still theirs: they can
    /// still read their own intel, and handing it back is one field. Second, the
    /// flip itself is gated on API_AI_TAKEOVER_ENABLED while the handoff list is
    /// computed and published either way, so the ladder's decisions are observable
    /// in the turn-generated event before anyone's game is actually altered.
    ///
    /// Honest limit: if generation fails after this point and the sweep retries the
    /// same turn, a seat can be counted as missing twice. The counter drives a
    /// coarse ladder measured in whole turns, so an occasional double count moves a
    /// takeover earlier by one turn rather than corrupting anything.
    /// </summary>
    private async Task<int[]> ApplyMissedTurnLadderAsync(
        string gameId, GameMeta? game, IReadOnlyList<Member> members, CancellationToken ct)
    {
        if (game is null)
        {
            return Array.Empty<int>();
        }

        var handoffs = new List<int>();
        foreach (Member seat in members)
        {
            // Open slots and resigned seats cannot miss a turn they were never on.
            if (!seat.IsActive)
            {
                continue;
            }

            MissedTurnAction action = MissedTurn.Decide(game, seat, seat.TurnSubmitted);
            if (action == MissedTurnAction.UseSubmittedOrders)
            {
                continue;
            }

            // A seat that is already AI cannot be handed to AI; it is on held orders
            // already, which is the same outcome the ladder would produce.
            bool handOff = action == MissedTurnAction.HandToAi && seat.Kind == PlayerKind.Human;
            if (handOff)
            {
                handoffs.Add(seat.EmpireId);
            }

            bool armed = handOff && options.AiTakeoverEnabled;
            await control.UpdateMemberAsync(gameId, seat.EmpireId, m =>
            {
                m.ConsecutiveMisses += 1;
                if (armed)
                {
                    m.Kind = PlayerKind.Ai;
                    m.AiTakeover = true;
                    m.AiParticipantId ??= options.DefaultAiParticipantId;
                    m.AiDifficulty ??= options.DefaultAiDifficulty;
                }
            }, ct);

            if (handOff)
            {
                log.LogWarning("Seat {EmpireId} in {GameId} handed to AI (armed={Armed}).",
                    seat.EmpireId, gameId, armed);
            }
        }
        return handoffs.ToArray();
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<GameMeta> Require(string gameId, CancellationToken ct)
        => await control.GetGameAsync(gameId, ct) ?? throw ApiProblem.NotFound($"No game {gameId}.");

    /// <summary>
    /// A private game is invite only (design Section 9, Visibility). The host always
    /// gets in; everyone else needs an invite addressed to the email on their
    /// verified session, which is the same check the accept path makes.
    /// </summary>
    private async Task RequireInvitedIfPrivateAsync(GameMeta game, SessionPrincipal caller, CancellationToken ct)
    {
        if (game.IsPublic || game.HostAccountId == caller.GoogleSub)
        {
            return;
        }
        IReadOnlyList<Invite> invites = await control.ListInvitesForGameAsync(game.GameId, ct);
        bool invited = invites.Any(i =>
            i.Status != InviteStatus.Revoked &&
            (string.Equals(i.InvitedEmail, caller.Email, StringComparison.OrdinalIgnoreCase)
             || i.AcceptedByAccountId == caller.GoogleSub));
        if (!invited)
        {
            throw ApiProblem.Forbidden("This game is private; you need an invite to join it.");
        }
    }

    private static MissedTurnPolicy ParsePolicy(string? s)
        => Enum.TryParse<MissedTurnPolicy>(s, ignoreCase: true, out var p) ? p : MissedTurnPolicy.HoldOrders;

    /// <summary>
    /// Structural order validation (design Section E.2): the envelope is well-formed,
    /// targets the caller's empire and the open turn, and every command carries a
    /// type the CommandRegistry knows. Deep per-empire ICommand.IsValid runs in the
    /// engine at generation time (ParseCommands), which re-checks against the live
    /// universe and skips anything invalid.
    /// </summary>
    private static void ValidateOrdersStructure(string ordersXml, int empireId, int turnYear)
    {
        XmlDocument doc = new();
        try
        {
            doc.LoadXml(ordersXml);
        }
        catch (XmlException e)
        {
            throw ApiProblem.BadRequest("Orders XML is not well formed: " + e.Message);
        }

        XmlNode? turnNode = doc.SelectSingleNode("ROOT/Turn");
        XmlNode? idNode = doc.SelectSingleNode("ROOT/Id");
        if (turnNode is null || idNode is null)
        {
            throw ApiProblem.BadRequest("Orders must carry ROOT/Turn and ROOT/Id.");
        }
        if (!int.TryParse(turnNode.InnerText, out int ordersTurn) || ordersTurn != turnYear)
        {
            throw ApiProblem.BadRequest($"Orders ROOT/Turn must equal {turnYear}.");
        }
        // R3: the body's empire id is advisory; the server already resolved the real
        // one from the session. Reject a mismatch so a confused client is told.
        if (int.TryParse(idNode.InnerText, out int bodyEmpire) && bodyEmpire != empireId)
        {
            throw ApiProblem.Forbidden("Orders name an empire that is not yours.");
        }

        XmlNode? ordersNode = doc.SelectSingleNode("ROOT/Orders");
        if (ordersNode is not null)
        {
            for (XmlNode? cmd = ordersNode.FirstChild; cmd is not null; cmd = cmd.NextSibling)
            {
                string? type = cmd.Attributes?["Type"]?.Value;
                if (type is null || !CommandRegistry.Instance.IsRegistered(type))
                {
                    throw ApiProblem.BadRequest($"Unknown command type '{type}'.");
                }
            }
        }
    }
}
