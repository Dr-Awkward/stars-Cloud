// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Nova.Common;
using Nova.Common.Components;
using Nova.Server;
using Nova.Server.Host.Storage;
using Nova.Server.NewGame;
using NUnit.Framework;

namespace Galaxies.Tests.ServerHost
{
    /// <summary>
    /// Builds the committed two-empire fixture that M0 exit criterion 2 needs.
    ///
    /// The M0 and turngen specs both said to build this with the desktop New Game
    /// wizard on .NET Framework. That is not necessary and it is worth not needing:
    /// the wizard is only a front end for Gameinitializer, which lives in
    /// ServerState and runs headless, as NewGameTest has demonstrated for years.
    /// Building it here means the fixture can be regenerated on Linux by anyone,
    /// with no Windows box and no GUI.
    ///
    /// The unseeded map RNG does not matter here, which is the point worth
    /// understanding. Unseeded generation is a problem for REPRODUCING a game, not
    /// for producing one artifact once: this runs a single time, and the result is
    /// committed and never regenerated. Seeding the map generator is still required
    /// before the server can create games on demand, and it remains open.
    ///
    /// This test is [Explicit] because it writes into the source tree and because
    /// Gameinitializer touches nova.conf as a side effect. CI never runs it; CI
    /// consumes the committed output.
    ///
    /// To regenerate:
    ///   dotnet test Tests.ServerHost -c Release --filter "FullyQualifiedName~BuildFixture"
    /// </summary>
    [TestFixture]
    [Explicit("Writes into the source tree. Run deliberately to regenerate the fixture.")]
    public class FixtureBuilder
    {
        private const string FixtureGameId = "fixture-2p";

        [Test]
        public void BuildFixtureTwoPlayer()
        {
            string repoRoot = FixturePaths.RepoRoot();
            string scratch = Path.Combine(Path.GetTempPath(), "galaxies-fixturegen-" + Path.GetRandomFileName());
            Directory.CreateDirectory(scratch);

            try
            {
                // A small, dense map keeps the fixture file modest while still giving
                // each empire somewhere to go. Both empires are human seats: AI seats
                // are M3's concern and would drag the AI assembly into this fixture.
                GameSettings.Data.GameName = FixtureGameId;
                GameSettings.Data.MapWidth = 400;
                GameSettings.Data.MapHeight = 400;
                GameSettings.Data.StarSeparation = 10;
                GameSettings.Data.StarDensity = 40;
                GameSettings.Data.StarUniformity = 60;

                string racesDir = Path.Combine(repoRoot, "DefaultRaces");
                Dictionary<string, Race> knownRaces = new()
                {
                    ["Humanoid"] = new Race(Path.Combine(racesDir, "Humanoid.race")),
                    ["Insectoid"] = new Race(Path.Combine(racesDir, "Insectoid.race")),
                };

                List<PlayerSettings> players = new()
                {
                    new PlayerSettings { PlayerNumber = 1, RaceName = "Humanoid", AiProgram = "Human" },
                    new PlayerSettings { PlayerNumber = 2, RaceName = "Insectoid", AiProgram = "Human" },
                };

                Gameinitializer.Initialize(scratch, players, knownRaces);

                // Gameinitializer names the state file after the game.
                string generated = Path.Combine(scratch, FixtureGameId + Global.ServerStateExtension);
                Assert.IsTrue(File.Exists(generated), $"Gameinitializer wrote no state at {generated}.");

                // Commit it at the canonical current-state path, which is what
                // IGameStore.DownloadStateAsync reads.
                string destination = Path.Combine(
                    FixturePaths.FixtureRoot(repoRoot),
                    GamePaths.CurrentState(FixtureGameId).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(generated, destination, overwrite: true);

                TestContext.WriteLine($"Fixture written to {destination}");
                TestContext.WriteLine($"Size: {new FileInfo(destination).Length / 1024} KB");

                // Prove the artifact is loadable before committing it, so a broken
                // fixture is caught here rather than by whoever runs a turn next.
                ServerData reloaded = new ServerData { StatePathName = destination };
                reloaded.Restore();
                Assert.AreEqual(2, reloaded.AllEmpires.Count, "The fixture should hold exactly two empires.");
                Assert.AreEqual(Global.StartingYear, reloaded.TurnYear, "The fixture should start at the starting year.");
                Assert.IsTrue(reloaded.AllStars.Count > 0, "The fixture has no stars.");

                foreach (KeyValuePair<int, EmpireData> seat in reloaded.AllEmpires)
                {
                    TestContext.WriteLine($"  empire {seat.Key} = {seat.Value.Race.Name}");
                }
            }
            finally
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// Locating the repository and the committed fixture, shared by the builder and
    /// by the tests that consume the fixture.
    /// </summary>
    internal static class FixturePaths
    {
        /// <summary>
        /// Walk up from the test assembly until the repository root appears. Keyed on
        /// two markers rather than a fixed number of parent hops, so this survives a
        /// change of target framework or configuration in the output path.
        /// </summary>
        public static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Galaxies.slnx"))
                    && Directory.Exists(Path.Combine(dir.FullName, "DefaultRaces")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find the repository root above {AppContext.BaseDirectory}.");
        }

        public static string FixtureRoot(string repoRoot)
            => Path.Combine(repoRoot, "Tests", "Fixtures");

        /// <summary>
        /// The fixture as copied next to the test assembly, which is what a test
        /// should read so it never depends on the source tree being present.
        /// </summary>
        public static string DeployedFixtureRoot()
            => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    /// <summary>
    /// Points the headless component loader at the components.xml beside the test
    /// assembly, so nothing needs nova.conf, the registry, or a file dialog. Mirrors
    /// Tests/GlobalTestSetup.cs; the fixture builder cannot cost a ship design
    /// without it.
    /// </summary>
    [SetUpFixture]
    public class ServerHostTestSetup
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            string components = Path.Combine(AppContext.BaseDirectory, "components.xml");
            if (File.Exists(components))
            {
                AllComponents.ComponentFilePathOverride = components;
            }
        }
    }
}
