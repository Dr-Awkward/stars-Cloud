// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

namespace Galaxies.ControlPlane.Model;

/// <summary>
/// The game's position in its lifecycle (design Section D.4). This is a distinct
/// axis from <see cref="GenerationState"/>: lifecycle is the human-facing status
/// (are we in the lobby, running, paused, done), generation is the transient "is a
/// turn being computed right now" flag guarded by the exactly-once lock. Keeping
/// them as separate fields resolves the state-field collision the specs flagged.
/// </summary>
public enum GameLifecycle
{
    Draft,      // being configured by the host, not yet open to join
    Lobby,      // open, accepting players, not started
    Active,     // running: turns generate on the clock
    Paused,     // host paused; the deadline task is cancelled
    Finished,   // a victory condition was met
    Cancelled,  // host abandoned before or during play
    Archived,   // cold storage after Finished/Cancelled
}

/// <summary>
/// Whether a turn is being generated right now. Guarded by the Firestore
/// generation lock so exactly one worker advances a given (gameId, turnYear)
/// (design Section B.2).
/// </summary>
public enum GenerationState
{
    Idle,        // no generation in flight; the turn is open for orders
    Generating,  // a worker holds the lock and is computing the turn
}

/// <summary>Who occupies a seat.</summary>
public enum PlayerKind
{
    Human,
    Ai,
}

/// <summary>
/// What happens to an empire that misses a deadline (design Section D.2). The
/// ladder escalates with consecutive misses; see <see cref="MissedTurn"/>.
/// </summary>
public enum MissedTurnPolicy
{
    HoldOrders,   // reuse the last submitted orders (default)
    DoNothing,    // generate with no orders for that empire
    AiTakeover,   // hand the seat to an AI after enough consecutive misses
}
