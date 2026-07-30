// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Dispatch;

using System.Text.Json.Serialization;

using Galaxies.AiService.Firestore;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Eventing;
using Galaxies.ControlPlane.Model;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The game-created event.
///
/// Only the game id is trusted from the payload. The seat roster is read back
/// from the canonical members subcollection rather than taken from the event,
/// because the event is a trigger and Firestore is the authority; an event that
/// arrives late, twice, or with a stale roster then costs nothing.
/// </summary>
public sealed record GameCreatedEvent
{
    [JsonPropertyName("gameId")] public string GameId { get; init; } = "";
}

/// <summary>
/// The deadline-approaching event (ANALYTICS-AND-KPIS.md Section on event
/// sources: gameId, turnYear, hoursRemaining, unsubmitted empireIds).
/// </summary>
public sealed record DeadlineApproachingEvent
{
    [JsonPropertyName("gameId")] public string GameId { get; init; } = "";

    [JsonPropertyName("turnYear")] public int TurnYear { get; init; }

    [JsonPropertyName("empireIds")] public int[] EmpireIds { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The three Pub/Sub push handlers (spec Section 7.1).
///
/// None of them does the work inline. game-created records pins; turn-generated
/// and deadline-approaching enqueue Cloud Tasks. Fanning out through the queue
/// rather than dispatching in the push handler is what keeps one slow AI from
/// holding a Pub/Sub ack open and what gives every seat its own retry budget.
/// </summary>
public sealed class EventHandlers
{
    private readonly AiStore store;
    private readonly TaskEnqueuer enqueuer;
    private readonly IControlPlane control;
    private readonly AiServiceConfig config;
    private readonly ILogger<EventHandlers> log;

    public EventHandlers(
        AiStore store,
        TaskEnqueuer enqueuer,
        IControlPlane control,
        AiServiceConfig config,
        ILogger<EventHandlers>? log = null)
    {
        this.store = store;
        this.enqueuer = enqueuer;
        this.control = control;
        this.config = config;
        this.log = log ?? NullLogger<EventHandlers>.Instance;
    }

    // ---- game-created -------------------------------------------------------

    /// <summary>
    /// Record an ai_seats pin for every roster member whose Kind is Ai.
    ///
    /// M3 pins the one participant that exists. When the M6 registry lands, the
    /// lobby's per-seat selection is what supplies participant_id, version and
    /// difficulty here instead of the defaults.
    /// </summary>
    public async Task<int> OnGameCreatedAsync(GameCreatedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(evt.GameId))
        {
            return 0;
        }

        IReadOnlyList<Member> members = await control.GetMembersAsync(evt.GameId, ct);
        int pinned = 0;

        foreach (Member member in members)
        {
            if (member.Kind != PlayerKind.Ai)
            {
                continue;
            }

            await store.UpsertSeatAsync(evt.GameId, new AiSeat
            {
                EmpireId = member.EmpireId,

                // A member's AccountId holds the participant id for an AI seat.
                // Falling back to the default keeps a seat playable rather than
                // silently un-dispatchable if the lobby left it blank.
                ParticipantId = string.IsNullOrWhiteSpace(member.AccountId)
                    ? config.DefaultParticipantId
                    : member.AccountId,
                Version = "1.0.0",
                Difficulty = "normal",
                Controller = SeatController.Ai,
            }, ct);

            pinned++;
        }

        log.LogInformation("Pinned {Count} AI seat(s) for {Game}.", pinned, evt.GameId);
        return pinned;
    }

    // ---- turn-generated -----------------------------------------------------

    /// <summary>
    /// Enqueue this year's AI seat tasks, and act on any handoffs.
    ///
    /// The event's AiEmpireIds is the seat list. Handoffs are seats the
    /// missed-turn ladder just gave to an AI; flipping their controller is gated
    /// by AI_TAKEOVER_ENABLED, and when that flag is off the flip simply does not
    /// happen, so the seats stay human and are not dispatched.
    /// </summary>
    public async Task<int> OnTurnGeneratedAsync(TurnGeneratedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(evt.GameId) || evt.GameEnded)
        {
            // A finished game has no next turn to play.
            return 0;
        }

        // Abandoned-seat takeover (spec Section 8.3).
        HashSet<int> seats = new(evt.AiEmpireIds ?? Array.Empty<int>());
        foreach (int empireId in evt.Handoffs ?? Array.Empty<int>())
        {
            if (!config.AiTakeoverEnabled)
            {
                log.LogInformation(
                    "AI_TAKEOVER_ENABLED is false; leaving handed-off seat {Empire} in {Game} unfilled.",
                    empireId, evt.GameId);
                continue;
            }

            await store.UpsertSeatAsync(evt.GameId, new AiSeat
            {
                EmpireId = empireId,
                ParticipantId = config.DefaultParticipantId,
                Version = "1.0.0",
                Difficulty = "normal",
                Controller = SeatController.AiTakeover,
            }, ct);

            seats.Add(empireId);
            log.LogInformation("Seat {Empire} in {Game} handed to {Participant}.",
                empireId, evt.GameId, config.DefaultParticipantId);
        }

        if (seats.Count == 0)
        {
            return 0;
        }

        DateTimeOffset? deadline = await DeadlineAsync(evt.GameId, ct);
        foreach (int empireId in seats.Order())
        {
            await enqueuer.EnqueueSeatAsync(evt.GameId, evt.TurnYear, empireId, deadline, ct);
        }

        return seats.Count;
    }

    // ---- deadline-approaching ----------------------------------------------

    /// <summary>
    /// The backstop: dispatch any AI seat that still has not submitted.
    ///
    /// This exists because a Cloud Tasks entry can be lost, a push can be dropped,
    /// and a dispatch can have failed early enough to leave a seat with no orders
    /// and time still on the clock. Re-enqueueing is safe: the task name dedupes,
    /// and the run lock refuses a seat whose orders already landed.
    /// </summary>
    public async Task<int> OnDeadlineApproachingAsync(
        DeadlineApproachingEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(evt.GameId))
        {
            return 0;
        }

        IReadOnlyList<AiSeat> aiSeats = await store.ListAiSeatsAsync(evt.GameId, ct);
        if (aiSeats.Count == 0)
        {
            return 0;
        }

        // Which of those seats are still outstanding, per the canonical roster.
        HashSet<int> unsubmitted = new();
        try
        {
            IReadOnlyList<Member> members = await control.GetMembersAsync(evt.GameId, ct);
            foreach (Member member in members)
            {
                if (!member.TurnSubmitted)
                {
                    unsubmitted.Add(member.EmpireId);
                }
            }
        }
        catch (Exception e)
        {
            // Without the roster, fall back to the event's own list. Less
            // trustworthy, but a backstop that refuses to run when its preferred
            // source is unavailable is not a backstop.
            log.LogWarning(e, "Could not read the roster for {Game}; using the event's empire list.", evt.GameId);
            unsubmitted = new HashSet<int>(evt.EmpireIds ?? Array.Empty<int>());
        }

        int dispatched = 0;
        foreach (AiSeat seat in aiSeats)
        {
            if (!unsubmitted.Contains(seat.EmpireId))
            {
                continue;
            }

            if (seat.Controller == SeatController.AiTakeover && !config.AiTakeoverEnabled)
            {
                continue;
            }

            // Schedule with no deadline, meaning now: the clock has already run
            // down, so the lead time no longer applies.
            await enqueuer.EnqueueSeatAsync(evt.GameId, evt.TurnYear, seat.EmpireId, null, ct);
            dispatched++;
        }

        log.LogInformation("Deadline backstop enqueued {Count} AI seat(s) for {Game} year {Year}.",
            dispatched, evt.GameId, evt.TurnYear);
        return dispatched;
    }

    private async Task<DateTimeOffset?> DeadlineAsync(string gameId, CancellationToken ct)
    {
        try
        {
            GameMeta? game = await control.GetGameAsync(gameId, ct);
            return game?.DeadlineAt?.ToDateTimeOffset();
        }
        catch (Exception e)
        {
            log.LogDebug(e, "No deadline available for {Game}; AI seats will run immediately.", gameId);
            return null;
        }
    }
}
