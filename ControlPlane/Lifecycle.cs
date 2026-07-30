// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.ControlPlane.Model;

namespace Galaxies.ControlPlane;

/// <summary>
/// The game lifecycle state machine (design Section D.4). One place that knows
/// which transitions are legal, so host controls (start/pause/resume/cancel) and
/// the generation path cannot drive a game into an impossible state.
/// </summary>
public static class Lifecycle
{
    private static readonly Dictionary<GameLifecycle, GameLifecycle[]> Allowed = new()
    {
        [GameLifecycle.Draft]     = new[] { GameLifecycle.Lobby, GameLifecycle.Cancelled },
        [GameLifecycle.Lobby]     = new[] { GameLifecycle.Active, GameLifecycle.Cancelled },
        [GameLifecycle.Active]    = new[] { GameLifecycle.Paused, GameLifecycle.Finished, GameLifecycle.Cancelled },
        [GameLifecycle.Paused]    = new[] { GameLifecycle.Active, GameLifecycle.Cancelled },
        [GameLifecycle.Finished]  = new[] { GameLifecycle.Archived },
        [GameLifecycle.Cancelled] = new[] { GameLifecycle.Archived },
        [GameLifecycle.Archived]  = Array.Empty<GameLifecycle>(),
    };

    public static bool CanTransition(GameLifecycle from, GameLifecycle to)
        => from == to || (Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0);

    /// <summary>
    /// Throw if the transition is illegal. Callers map the thrown exception to a
    /// 409 (conflict with current state).
    /// </summary>
    public static void EnsureTransition(GameLifecycle from, GameLifecycle to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidLifecycleTransitionException(from, to);
        }
    }

    /// <summary>Turns generate only while the game is Active.</summary>
    public static bool GeneratesTurns(GameLifecycle lifecycle) => lifecycle == GameLifecycle.Active;
}

public sealed class InvalidLifecycleTransitionException : Exception
{
    public GameLifecycle From { get; }
    public GameLifecycle To { get; }

    public InvalidLifecycleTransitionException(GameLifecycle from, GameLifecycle to)
        : base($"Illegal game lifecycle transition {from} -> {to}.")
    {
        From = from;
        To = to;
    }
}
