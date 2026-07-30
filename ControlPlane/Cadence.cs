// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Firestore;
using Galaxies.ControlPlane.Model;

namespace Galaxies.ControlPlane;

/// <summary>
/// The turn clock (design Section D.1): a game's deadline is its last generation
/// time plus its "maximum time between turns". Pure functions so the API, the
/// scheduler, and the sweeper all agree on when a turn is due.
/// </summary>
public static class Cadence
{
    /// <summary>
    /// The deadline for the turn opened at <paramref name="lastGenerationAt"/>,
    /// or null when the game has no clock (maxTimeBetweenTurns == 0), meaning a
    /// turn only generates once everyone submits.
    /// </summary>
    public static Timestamp? DeadlineFor(Timestamp lastGenerationAt, long maxTimeBetweenTurnsSeconds)
    {
        if (maxTimeBetweenTurnsSeconds <= 0)
        {
            return null;
        }
        return Timestamp.FromDateTimeOffset(
            lastGenerationAt.ToDateTimeOffset().AddSeconds(maxTimeBetweenTurnsSeconds));
    }

    /// <summary>Recompute a game's deadline from its own fields.</summary>
    public static Timestamp? DeadlineFor(GameMeta game)
    {
        Timestamp anchor = game.LastGenerationAt ?? game.CreatedAt ?? game.DeadlineAt ?? default;
        return DeadlineFor(anchor, game.MaxTimeBetweenTurnsSeconds);
    }

    /// <summary>True when the deadline has passed as of <paramref name="now"/>.</summary>
    public static bool DeadlineReached(GameMeta game, Timestamp now)
    {
        return game.DeadlineAt is { } d && now.ToDateTimeOffset() >= d.ToDateTimeOffset();
    }

    /// <summary>
    /// True when every active seat has submitted, so the turn can generate early
    /// without waiting for the deadline (design Section B.2 trigger 1).
    /// </summary>
    public static bool EveryoneSubmitted(GameMeta game)
    {
        return game.ActivePlayerCount > 0 && game.SubmittedCount >= game.ActivePlayerCount;
    }
}
