// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Nova.Server.Host.Storage;
using NUnit.Framework;

namespace Galaxies.Tests.ServerHost
{
    /// <summary>
    /// The contract between galaxies-api and the turn generator.
    ///
    /// Every path this fixture writes or asserts is spelled out as a LITERAL, copied
    /// from Api/Storage/ObjectStores.cs, and deliberately not built through
    /// GamePaths. Building both sides from the same helper would only prove the
    /// store is self-consistent, which it already was when the bug existed: the API
    /// wrote games/{gameId}/orders/{turnYear}/{empireId}.orders while the store read
    /// games/{gameId}/orders/current/, each perfectly consistent with itself. If
    /// GamePaths is ever changed away from what the API does, these literals are
    /// what fails.
    /// </summary>
    [TestFixture]
    public class GameStoreLayoutTests
    {
        private const string GameId = "roybot:game:2f1a";
        private const int TurnYear = 2100;

        // Two seats. Race names carry a space and punctuation on purpose: they are
        // player-supplied strings, which is exactly why storage keys on empire id.
        private static readonly Dictionary<int, string> Seats = new()
        {
            [1] = "The Antheads",
            [2] = "Ubers",
        };

        private string root = string.Empty;
        private string workingDir = string.Empty;

        [SetUp]
        public void CreateDirs()
        {
            root = Path.Combine(Path.GetTempPath(), "galaxies-store-" + Path.GetRandomFileName());
            workingDir = Path.Combine(Path.GetTempPath(), "galaxies-work-" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(workingDir);
        }

        [TearDown]
        public void RemoveDirs()
        {
            foreach (string dir in new[] { root, workingDir })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }

        /// <summary>
        /// Orders written where the API writes them must be staged under the name the
        /// engine's OrderReader opens, which is the race name.
        ///
        /// This is the test whose absence let the original defect ship. Before the
        /// layout converged it failed twice: the store looked under orders/current/
        /// rather than the turn folder, and it staged files under the empire id
        /// rather than the race name, so OrderReader found nothing either way and
        /// every turn generated as though nobody had moved.
        /// </summary>
        [Test]
        public async Task OrdersWrittenByTheApiAreStagedUnderTheRaceName()
        {
            WriteApiOrders(empireId: 1, body: "<orders empire=\"1\" />");
            WriteApiOrders(empireId: 2, body: "<orders empire=\"2\" />");

            var store = new LocalGameStore(root);
            int staged = await store.DownloadOrdersAsync(GameId, workingDir, TurnYear, Seats);

            Assert.AreEqual(2, staged, "Both submitted orders files should have been staged.");

            string first = Path.Combine(workingDir, "The Antheads.orders");
            string second = Path.Combine(workingDir, "Ubers.orders");

            Assert.IsTrue(File.Exists(first), "Orders for empire 1 were not staged as the race name.");
            Assert.IsTrue(File.Exists(second), "Orders for empire 2 were not staged as the race name.");
            Assert.AreEqual("<orders empire=\"1\" />", File.ReadAllText(first), "Empire 1 got the wrong body.");
            Assert.AreEqual("<orders empire=\"2\" />", File.ReadAllText(second), "Empire 2 got the wrong body.");
        }

        /// <summary>
        /// Intel the engine wrote as {raceName}.intel must land where the API reads
        /// it, which is keyed by empire id.
        /// </summary>
        [Test]
        public async Task IntelIsUploadedWhereTheApiReadsIt()
        {
            // The engine writes intel into its game folder, named for the race.
            File.WriteAllText(Path.Combine(workingDir, "The Antheads.intel"), "<intel empire=\"1\" />");
            File.WriteAllText(Path.Combine(workingDir, "Ubers.intel"), "<intel empire=\"2\" />");

            var store = new LocalGameStore(root);
            await store.UploadResultsAsync(GameId, workingDir, TurnYear, Seats);

            string first = ApiPath($"games/{GameId}/intel/{TurnYear}/1.intel");
            string second = ApiPath($"games/{GameId}/intel/{TurnYear}/2.intel");

            Assert.IsTrue(File.Exists(first), "Empire 1's intel is not where the API reads it.");
            Assert.IsTrue(File.Exists(second), "Empire 2's intel is not where the API reads it.");
            Assert.AreEqual("<intel empire=\"1\" />", File.ReadAllText(first), "Empire 1 got the wrong intel.");
            Assert.AreEqual("<intel empire=\"2\" />", File.ReadAllText(second), "Empire 2 got the wrong intel.");
        }

        /// <summary>
        /// A seat that did not submit is simply absent. The engine treats a missing
        /// orders file as hold, which is the correct outcome, so this must not throw
        /// and must not invent a file.
        /// </summary>
        [Test]
        public async Task ASeatThatDidNotSubmitIsSkipped()
        {
            WriteApiOrders(empireId: 1, body: "<orders empire=\"1\" />");

            var store = new LocalGameStore(root);
            int staged = await store.DownloadOrdersAsync(GameId, workingDir, TurnYear, Seats);

            Assert.AreEqual(1, staged, "Only one seat submitted.");
            Assert.IsTrue(File.Exists(Path.Combine(workingDir, "The Antheads.orders")));
            Assert.IsFalse(File.Exists(Path.Combine(workingDir, "Ubers.orders")));
        }

        /// <summary>
        /// Orders belonging to an empire that is not a seat in this game are never
        /// staged. This is the fog-of-war boundary at the storage layer: staging a
        /// stray object could feed one empire's orders into another's turn.
        /// </summary>
        [Test]
        public async Task OrdersForAnEmpireOutsideTheRosterAreNotStaged()
        {
            WriteApiOrders(empireId: 1, body: "<orders empire=\"1\" />");
            WriteApiOrders(empireId: 99, body: "<orders empire=\"99\" />");

            var store = new LocalGameStore(root);
            int staged = await store.DownloadOrdersAsync(GameId, workingDir, TurnYear, Seats);

            Assert.AreEqual(1, staged, "Only the roster seat should be staged.");
            Assert.AreEqual(
                1,
                Directory.GetFiles(workingDir, "*.orders").Length,
                "An orders file outside the roster reached the working directory.");
        }

        /// <summary>
        /// The state round trip: what UploadResultsAsync writes as current must be
        /// what DownloadStateAsync picks up next generation, and it must also be kept
        /// under its turn year for history.
        /// </summary>
        [Test]
        public async Task StateRoundTripsThroughTheCurrentPointer()
        {
            var store = new LocalGameStore(root);

            File.WriteAllText(Path.Combine(workingDir, GameId + ".sstate"), "<state year=\"2100\" />");
            await store.UploadResultsAsync(GameId, workingDir, TurnYear, Seats);

            Assert.IsTrue(File.Exists(ApiPath($"games/{GameId}/state/current.sstate")), "No current pointer written.");
            Assert.IsTrue(File.Exists(ApiPath($"games/{GameId}/state/{TurnYear}.sstate")), "No per-turn history written.");

            string nextWorkingDir = Path.Combine(Path.GetTempPath(), "galaxies-next-" + Path.GetRandomFileName());
            try
            {
                await store.DownloadStateAsync(GameId, nextWorkingDir);
                Assert.AreEqual(
                    "<state year=\"2100\" />",
                    File.ReadAllText(Path.Combine(nextWorkingDir, GameId + ".sstate")),
                    "The next generation did not pick up the state just committed.");
            }
            finally
            {
                if (Directory.Exists(nextWorkingDir))
                {
                    Directory.Delete(nextWorkingDir, recursive: true);
                }
            }
        }

        /// <summary>
        /// A game with no state is a failure, not an empty turn. Generating against a
        /// game that was never created would otherwise look like a successful turn
        /// with an empty universe.
        /// </summary>
        [Test]
        public void DownloadingStateForAnUnknownGameThrows()
        {
            var store = new LocalGameStore(root);
            Assert.ThrowsAsync<FileNotFoundException>(
                async () => await store.DownloadStateAsync("roybot:game:nope", workingDir));
        }

        /// <summary>
        /// Write an orders object exactly where Api/Storage/ObjectStores.cs:48 puts
        /// it. The path is a literal on purpose; see the fixture summary.
        /// </summary>
        private void WriteApiOrders(int empireId, string body)
        {
            string path = ApiPath($"games/{GameId}/orders/{TurnYear}/{empireId}.orders");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        private string ApiPath(string objectName)
            => Path.Combine(root, objectName.Replace('/', Path.DirectorySeparatorChar));
    }
}
