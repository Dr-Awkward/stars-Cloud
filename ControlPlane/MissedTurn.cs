// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.ControlPlane.Model;

namespace Galaxies.ControlPlane;

/// <summary>
/// What to do with a seat that has not submitted when a turn generates
/// (design Section D.2). The default is HoldOrders: reuse the last submitted
/// orders so an absent player's empire keeps doing something sensible. Repeated
/// misses escalate; under AiTakeover a long-absent seat is handed to an AI.
/// </summary>
public enum MissedTurnAction
{
    UseSubmittedOrders,  // the seat submitted this turn; nothing to do
    HoldOrders,          // reuse last turn's orders
    NoOrders,            // generate with no orders for this seat
    HandToAi,            // convert the seat to AI from now on
}

public static class MissedTurn
{
    /// <summary>
    /// Decide the action for one seat at generation time, given the game's policy
    /// and the seat's miss history. <paramref name="submittedThisTurn"/> short
    /// circuits to UseSubmittedOrders.
    /// </summary>
    public static MissedTurnAction Decide(GameMeta game, Member member, bool submittedThisTurn)
    {
        if (submittedThisTurn)
        {
            return MissedTurnAction.UseSubmittedOrders;
        }

        // A seat that has never submitted has no orders to hold.
        bool hasPriorOrders = member.LastSubmittedTurn >= 0;

        switch (game.MissedTurnPolicy)
        {
            case MissedTurnPolicy.DoNothing:
                return MissedTurnAction.NoOrders;

            case MissedTurnPolicy.AiTakeover:
                // Escalate: hold while under the threshold, hand to AI once the
                // consecutive misses reach it. member.ConsecutiveMisses counts
                // misses BEFORE this turn, so this miss makes it +1.
                if (member.ConsecutiveMisses + 1 >= Math.Max(1, game.AiTakeoverAfterMisses))
                {
                    return MissedTurnAction.HandToAi;
                }
                return hasPriorOrders ? MissedTurnAction.HoldOrders : MissedTurnAction.NoOrders;

            case MissedTurnPolicy.HoldOrders:
            default:
                return hasPriorOrders ? MissedTurnAction.HoldOrders : MissedTurnAction.NoOrders;
        }
    }
}
