// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using System;

namespace Nova.Common
{
    /// <summary>
    /// Turns a game's master seed into the seeded Random the engine should use,
    /// instead of the unseeded new Random() the desktop game used (design A.4).
    ///
    /// Lives in Common so both the engine (TurnGenerator, BattleEngine, and the
    /// map generators) and the cloud host share one stable rule.
    /// </summary>
    public static class NovaRandom
    {
        /// <summary>The engine's main per-turn RNG.</summary>
        public static Random ForTurn(long masterSeed, int turnYear)
        {
            return new Random(SeedDerivation.ForTurn(masterSeed, turnYear));
        }

        /// <summary>A per-empire RNG, for a seat's own randomness.</summary>
        public static Random ForSeat(long masterSeed, int turnYear, int empireId)
        {
            return new Random(SeedDerivation.ForSeat(masterSeed, turnYear, empireId));
        }

        /// <summary>A per-subsystem RNG (battles, minefields, map generation).</summary>
        public static Random ForSubsystem(long masterSeed, int turnYear, string subsystem)
        {
            return new Random(SeedDerivation.ForSubsystem(masterSeed, turnYear, subsystem));
        }
    }
}
