// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using System.Collections.Generic;
using System.Linq;
using Nova.Common;
using Nova.Server;
using Nova.Server.NewGame;
using NUnit.Framework;

namespace Nova.Tests.UnitTests
{
    /// <summary>
    /// The same master seed and the same options must produce the same galaxy.
    ///
    /// This is design Section A.4 item 4, and it was not true: StarMapGenerator,
    /// NameGenerator, and the four draw sites in StarMapinitializer each held their
    /// own unseeded Random, so two games created from identical inputs differed in
    /// star positions, star names, and mineral concentrations. The seed was stored
    /// and then ignored.
    ///
    /// Worth stating what this does NOT claim. Two DIFFERENT seeds are expected to
    /// give different galaxies, and that is asserted separately below, because a
    /// reproducibility fix that accidentally made every galaxy identical would
    /// otherwise pass.
    /// </summary>
    [TestFixture]
    public class GalaxyReproducibilityTest
    {
        private const long Seed = 426470976;

        [SetUp]
        public void FixTheMapOptions()
        {
            GameSettings.Data.MapWidth = 400;
            GameSettings.Data.MapHeight = 400;
            GameSettings.Data.StarSeparation = 10;
            GameSettings.Data.StarDensity = 40;
            GameSettings.Data.StarUniformity = 60;
        }

        [Test]
        public void TheSameSeedProducesTheSameGalaxy()
        {
            List<string> first = GenerateGalaxy(Seed);
            List<string> second = GenerateGalaxy(Seed);

            Assert.IsNotEmpty(first, "The generator produced no stars, so this test proves nothing.");
            CollectionAssert.AreEqual(
                first, second,
                "The same master seed produced a different galaxy. Map generation is not deriving from the seed.");
        }

        [Test]
        public void ADifferentSeedProducesADifferentGalaxy()
        {
            List<string> first = GenerateGalaxy(Seed);
            List<string> other = GenerateGalaxy(Seed + 1);

            Assert.AreNotEqual(
                string.Join("|", first),
                string.Join("|", other),
                "Two different seeds produced an identical galaxy, so the seed is not reaching the generator.");
        }

        /// <summary>
        /// A galaxy reduced to a comparable form: every star's name, position, and
        /// mineral concentrations, ordered by name so dictionary iteration order
        /// cannot affect the comparison. Ordering here is deliberate: this test is
        /// about the seed, and letting hash order leak in would make it flaky for an
        /// unrelated reason.
        /// </summary>
        private static List<string> GenerateGalaxy(long masterSeed)
        {
            ServerData state = new ServerData { MasterSeed = masterSeed };

            // Two players, matching the committed fixture. The count feeds the map
            // generator, so it has to be fixed for the comparison to mean anything.
            state.AllPlayers.Add(new PlayerSettings { PlayerNumber = 1, RaceName = "Humanoid", AiProgram = "Human" });
            state.AllPlayers.Add(new PlayerSettings { PlayerNumber = 2, RaceName = "Insectoid", AiProgram = "Human" });

            new StarMapinitializer(state).GenerateStars();

            return state.AllStars.Values
                .Select(star => string.Join(
                    ",",
                    star.Name,
                    star.Position.X,
                    star.Position.Y,
                    star.MineralConcentration.Ironium,
                    star.MineralConcentration.Boranium,
                    star.MineralConcentration.Germanium,
                    star.Radiation,
                    star.Gravity,
                    star.Temperature))
                .OrderBy(line => line, System.StringComparer.Ordinal)
                .ToList();
        }
    }
}
