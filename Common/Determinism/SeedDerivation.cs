// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

namespace Nova.Common
{
    /// <summary>
    /// Deterministic seed derivation for reproducible turns (design Section A.4).
    ///
    /// The desktop engine newed up an unseeded Random() in several places, so a
    /// turn could not be reproduced, audited, or covered by golden-turn tests. One
    /// master seed is stored per game (ServerData.MasterSeed); every stochastic
    /// subsystem derives its stream from that seed plus the turn year, and, for
    /// per-seat randomness, the empire id. Two different subsystems get different
    /// sub-seeds so they never share or reorder a sequence.
    ///
    /// The hash is a plain FNV-1a over the inputs. It does NOT use
    /// string.GetHashCode, which is randomized per process on modern .NET and would
    /// break reproducibility across runs. This function must stay stable forever,
    /// because changing it re-rolls every game's randomness.
    ///
    /// This lives in Common (not the host) so the engine can seed itself directly.
    /// </summary>
    public static class SeedDerivation
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>Seed for the whole turn, shared by the engine's main RNG.</summary>
        public static int ForTurn(long masterSeed, int turnYear)
        {
            return ToSeed(Mix(FnvOffset, unchecked((ulong)masterSeed), unchecked((ulong)turnYear)));
        }

        /// <summary>Seed for one empire's per-turn randomness (for example an AI seat).</summary>
        public static int ForSeat(long masterSeed, int turnYear, int empireId)
        {
            return ToSeed(Mix(FnvOffset, unchecked((ulong)masterSeed), unchecked((ulong)turnYear), unchecked((ulong)empireId)));
        }

        /// <summary>
        /// Seed for a named subsystem within a turn (battles, minefields, map
        /// generation), so subsystems do not draw from one shared sequence.
        /// </summary>
        public static int ForSubsystem(long masterSeed, int turnYear, string subsystem)
        {
            ulong h = Mix(FnvOffset, unchecked((ulong)masterSeed), unchecked((ulong)turnYear));
            foreach (char c in subsystem)
            {
                h = (h ^ c) * FnvPrime;
            }
            return ToSeed(h);
        }

        private static ulong Mix(ulong seed, params ulong[] values)
        {
            ulong h = seed;
            foreach (ulong v in values)
            {
                for (int i = 0; i < 8; i++)
                {
                    h = (h ^ ((v >> (i * 8)) & 0xFF)) * FnvPrime;
                }
            }
            return h;
        }

        // Fold the 64-bit hash into a non-negative 31-bit seed for System.Random.
        private static int ToSeed(ulong h)
        {
            uint folded = (uint)(h ^ (h >> 32));
            return (int)(folded & 0x7FFFFFFF);
        }
    }
}
