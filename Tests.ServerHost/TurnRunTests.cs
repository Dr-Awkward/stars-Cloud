// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Common;
using Nova.Common.Commands;
using Nova.Server;
using Nova.Server.Host.Engine;
using Nova.Server.Host.Storage;
using NUnit.Framework;

namespace Galaxies.Tests.ServerHost
{
    /// <summary>
    /// M0 exit criterion 2, as a test rather than a manual command: the committed
    /// two-empire fixture advances one year and one intel file per empire comes out,
    /// with no cloud and no credentials.
    ///
    /// This is the first test in the repository that runs the whole turn pipe. Its
    /// absence is why three separate silent failures survived: orders staged at a
    /// path the engine never read, intel written to a folder the store never looked
    /// in, and a fleet that could not be loaded back at all on a machine without a
    /// graphics folder.
    /// </summary>
    [TestFixture]
    public class TurnRunTests
    {
        private const string GameId = "fixture-2p";

        private string root = string.Empty;

        [SetUp]
        public void StageFixture()
        {
            root = Path.Combine(Path.GetTempPath(), "galaxies-turn-" + Path.GetRandomFileName());

            // Work on a copy. A turn run mutates the store, and the deployed fixture
            // is a build artifact shared by every test in this assembly.
            string source = FixturePaths.DeployedFixtureRoot();
            Assert.IsTrue(
                Directory.Exists(source),
                $"The fixture was not deployed to {source}. Check the None/LinkBase item in Tests.ServerHost.csproj.");
            CopyTree(source, root);
        }

        [TearDown]
        public void RemoveStagedFixture()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>
        /// The fixture is a real two-empire game, not a hand-assembled stub. Asserted
        /// here so a regenerated fixture that quietly lost its content is caught
        /// before it is blamed on the turn generator.
        /// </summary>
        [Test]
        public void TheFixtureIsARealTwoEmpireGame()
        {
            ServerData state = LoadFixtureState();

            Assert.AreEqual(2, state.AllEmpires.Count, "Expected two empires.");
            Assert.AreEqual(Global.StartingYear, state.TurnYear, "Expected the fixture to sit at the starting year.");

            TestContext.WriteLine($"stars   {state.AllStars.Count}");
            TestContext.WriteLine($"empires {state.AllEmpires.Count}");
            foreach (KeyValuePair<int, EmpireData> seat in state.AllEmpires)
            {
                TestContext.WriteLine(
                    $"  empire {seat.Key} = {seat.Value.Race.Name}, "
                    + $"{seat.Value.OwnedFleets.Count} fleets, {seat.Value.Designs.Count} designs");
            }

            Assert.IsTrue(state.AllStars.Count >= 2, "A two-empire game needs at least a home star each.");
            foreach (EmpireData empire in state.AllEmpires.Values)
            {
                Assert.IsTrue(
                    empire.OwnedFleets.Count > 0,
                    $"Empire {empire.Id} ({empire.Race.Name}) has no starting fleets, so the fixture is not a real game.");
            }
        }

        /// <summary>
        /// The whole pipe: state in, one turn generated, new state and per-empire
        /// intel out, at the canonical paths galaxies-api reads.
        /// </summary>
        [Test]
        public async Task OneTurnAdvancesTheFixtureAndWritesIntelPerEmpire()
        {
            ServerData before = LoadFixtureState();
            int yearBefore = before.TurnYear;
            Dictionary<int, string> seats = before.AllEmpires.ToDictionary(e => e.Key, e => e.Value.Race.Name);

            var store = new LocalGameStore(root);
            var service = new TurnService(store, NullLogger<TurnService>.Instance, ScratchRoot());

            TurnService.GenerationOutcome outcome = await service.GenerateTurnAsync(GameId);

            Assert.AreEqual(yearBefore + 1, outcome.TurnYear, "The turn year did not advance by exactly one.");
            Assert.AreEqual(2, outcome.EmpireIds.Length, "Both empires should be reported to the API.");
            Assert.IsFalse(outcome.GameEnded, "A first turn should not end the game.");

            // One intel object per empire, keyed by empire id, where the API reads it.
            foreach (KeyValuePair<int, string> seat in seats)
            {
                string intel = Path.Combine(
                    root,
                    $"games/{GameId}/intel/{outcome.TurnYear}/{seat.Key}.intel"
                        .Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(
                    File.Exists(intel),
                    $"No intel for empire {seat.Key} ({seat.Value}) at {intel}.");
                Assert.IsTrue(new FileInfo(intel).Length > 0, $"Intel for empire {seat.Key} is empty.");
            }

            // The new state is committed both as history and as the current pointer.
            Assert.IsTrue(
                File.Exists(Path.Combine(root, $"games/{GameId}/state/{outcome.TurnYear}.sstate"
                    .Replace('/', Path.DirectorySeparatorChar))),
                "No per-turn state history was written.");
            Assert.AreEqual(
                GamePaths.StateForTurn(GameId, outcome.TurnYear),
                outcome.NewStatePath,
                "The reported state path does not match the canonical layout.");
        }

        /// <summary>
        /// Two turns in a row. The second turn has to pick up what the first
        /// committed, which is what proves the current pointer is actually load
        /// bearing rather than write-only.
        /// </summary>
        [Test]
        public async Task TurnsChainThroughTheCurrentPointer()
        {
            var store = new LocalGameStore(root);
            var service = new TurnService(store, NullLogger<TurnService>.Instance, ScratchRoot());

            TurnService.GenerationOutcome first = await service.GenerateTurnAsync(GameId);
            TurnService.GenerationOutcome second = await service.GenerateTurnAsync(GameId);

            Assert.AreEqual(first.TurnYear + 1, second.TurnYear, "The second turn did not build on the first.");
        }

        /// <summary>
        /// The whole point of the exercise: an order written where galaxies-api
        /// writes it must change the generated turn.
        ///
        /// This is the end-to-end version of the defect that started all of this.
        /// The API wrote orders to games/{id}/orders/{turnYear}/{empireId}.orders,
        /// the game store read games/{id}/orders/current/, and the engine opened
        /// {raceName}.orders. Three conventions, no overlap, no exception: turns
        /// generated normally and every submitted order was discarded. Staging is
        /// covered by GameStoreLayoutTests; this asserts the consequence, which is
        /// that the order actually lands in the next state.
        ///
        /// A fleet rename is used because its effect is unambiguous and depends on
        /// no game mechanics. It also exercises the hex fleet key: the engine writes
        /// FleetKey as hexadecimal, and reading it back as decimal would silently
        /// address the wrong fleet.
        /// </summary>
        [Test]
        public async Task ASubmittedOrderChangesTheGeneratedTurn()
        {
            const string NewFleetName = "Renamed By The Seam Test";

            ServerData before = LoadFixtureState();
            EmpireData empire = before.AllEmpires.Values.First();
            Fleet fleet = empire.OwnedFleets.Values.First();
            long fleetKey = fleet.Key;
            string originalName = fleet.Name;

            Assert.AreNotEqual(NewFleetName, originalName, "The fixture already carries the test name.");

            WriteOrders(
                empireId: empire.Id,
                turnYear: before.TurnYear,
                command: new RenameFleetCommand(fleet, NewFleetName));

            var store = new LocalGameStore(root);
            var service = new TurnService(store, NullLogger<TurnService>.Instance, ScratchRoot());
            TurnService.GenerationOutcome outcome = await service.GenerateTurnAsync(GameId);

            ServerData after = LoadFixtureState();
            Assert.AreEqual(outcome.TurnYear, after.TurnYear, "The committed state is not the generated turn.");

            EmpireData reloaded = after.AllEmpires[empire.Id];
            Assert.IsTrue(
                reloaded.OwnedFleets.ContainsKey(fleetKey),
                $"Fleet {fleetKey:X} is gone from empire {empire.Id} after the turn.");
            Assert.AreEqual(
                NewFleetName,
                reloaded.OwnedFleets[fleetKey].Name,
                "The submitted rename did not reach the generated turn. The orders file was staged but never applied.");
        }

        /// <summary>
        /// Orders belonging to another empire must not take effect, even when they
        /// are staged. OrderReader checks the empire id inside the file, which is the
        /// backstop behind the store's own roster filter.
        /// </summary>
        [Test]
        public async Task AnOrderTaggedForAnotherEmpireIsRejected()
        {
            const string NewFleetName = "Should Never Apply";

            ServerData before = LoadFixtureState();
            EmpireData victim = before.AllEmpires.Values.First();
            EmpireData other = before.AllEmpires.Values.Last();
            Assert.AreNotEqual(victim.Id, other.Id, "The fixture needs two distinct empires.");

            Fleet fleet = victim.OwnedFleets.Values.First();
            long fleetKey = fleet.Key;
            string originalName = fleet.Name;

            // Written to the OTHER empire's orders path, but tagged inside for the
            // victim. The store stages it as the other empire's file; OrderReader
            // must then refuse it on the id mismatch.
            WriteOrders(
                empireId: other.Id,
                turnYear: before.TurnYear,
                command: new RenameFleetCommand(fleet, NewFleetName),
                taggedEmpireId: victim.Id);

            var store = new LocalGameStore(root);
            var service = new TurnService(store, NullLogger<TurnService>.Instance, ScratchRoot());
            await service.GenerateTurnAsync(GameId);

            ServerData after = LoadFixtureState();
            Assert.AreEqual(
                originalName,
                after.AllEmpires[victim.Id].OwnedFleets[fleetKey].Name,
                "An order tagged for another empire was applied.");
        }

        /// <summary>
        /// The same turn, generated twice from the same input, must produce the same
        /// bytes. This is the precondition for M0 exit criterion 4: a golden turn
        /// comparison is meaningless if a single machine cannot reproduce itself.
        ///
        /// Two things broke this and neither was the game logic. The scratch
        /// directory was named with a GUID and ServerData serializes GameFolder and
        /// StatePathName into the save, so every run embedded a different path. And
        /// RacialTraits enumerated a Hashtable, whose order .NET randomizes per
        /// process, so the same lesser traits serialized in a different order.
        ///
        /// Both runs deliberately share one scratch root, which is how the service
        /// actually runs, and which also exercises the stale-directory cleanup that a
        /// derived directory name makes necessary.
        /// </summary>
        [Test]
        public async Task TheSameTurnGeneratedTwiceIsByteIdentical()
        {
            string scratch = ScratchRoot();
            string secondRoot = Path.Combine(Path.GetTempPath(), "galaxies-det-" + Path.GetRandomFileName());

            try
            {
                CopyTree(FixturePaths.DeployedFixtureRoot(), secondRoot);

                var first = new TurnService(new LocalGameStore(root), NullLogger<TurnService>.Instance, scratch);
                TurnService.GenerationOutcome a = await first.GenerateTurnAsync(GameId);

                var second = new TurnService(new LocalGameStore(secondRoot), NullLogger<TurnService>.Instance, scratch);
                TurnService.GenerationOutcome b = await second.GenerateTurnAsync(GameId);

                Assert.AreEqual(a.TurnYear, b.TurnYear, "The two runs did not generate the same turn.");

                AssertSameBytes(
                    Path.Combine(root, RelativeState(a.TurnYear)),
                    Path.Combine(secondRoot, RelativeState(b.TurnYear)),
                    "The generated state differs between two runs of the same turn.");

                foreach (int empireId in a.EmpireIds)
                {
                    AssertSameBytes(
                        Path.Combine(root, RelativeIntel(a.TurnYear, empireId)),
                        Path.Combine(secondRoot, RelativeIntel(b.TurnYear, empireId)),
                        $"Intel for empire {empireId} differs between two runs of the same turn.");
                }
            }
            finally
            {
                foreach (string dir in new[] { secondRoot, scratch })
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            }
        }

        private static string RelativeState(int turnYear)
            => $"games/{GameId}/state/{turnYear}.sstate".Replace('/', Path.DirectorySeparatorChar);

        private static string RelativeIntel(int turnYear, int empireId)
            => $"games/{GameId}/intel/{turnYear}/{empireId}.intel".Replace('/', Path.DirectorySeparatorChar);

        private static void AssertSameBytes(string left, string right, string message)
        {
            Assert.IsTrue(File.Exists(left), $"Missing {left}");
            Assert.IsTrue(File.Exists(right), $"Missing {right}");

            byte[] a = File.ReadAllBytes(left);
            byte[] b = File.ReadAllBytes(right);

            // Compare lengths first so a size mismatch reports as a size mismatch
            // rather than as an offset somewhere in the middle.
            Assert.AreEqual(a.Length, b.Length, message + " (different lengths)");
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    Assert.Fail($"{message} First difference at byte {i}.");
                }
            }
        }

        /// <summary>
        /// Write an orders file in the engine's envelope, at the canonical path
        /// galaxies-api writes to. The path is a literal, copied from
        /// Api/Storage/ObjectStores.cs, for the reason given in GameStoreLayoutTests.
        /// </summary>
        /// <param name="empireId">Whose orders slot the file occupies.</param>
        /// <param name="taggedEmpireId">
        /// The empire id written INSIDE the file. Defaults to the slot owner; the
        /// negative test sets it to somebody else.
        /// </param>
        private void WriteOrders(int empireId, int turnYear, ICommand command, int? taggedEmpireId = null)
        {
            XmlDocument xmldoc = new XmlDocument();
            XmlElement root = Global.InitializeXmlDocument(xmldoc);

            Global.SaveData(xmldoc, root, "Turn", turnYear.ToString());
            Global.SaveData(xmldoc, root, "Id", (taggedEmpireId ?? empireId).ToString());

            XmlElement orders = xmldoc.CreateElement("Orders");
            orders.AppendChild(command.ToXml(xmldoc));
            root.AppendChild(orders);

            string path = Path.Combine(
                this.root,
                $"games/{GameId}/orders/{turnYear}/{empireId}.orders"
                    .Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            xmldoc.Save(path);
        }

        private ServerData LoadFixtureState()
        {
            string path = Path.Combine(
                root, GamePaths.CurrentState(GameId).Replace('/', Path.DirectorySeparatorChar));
            ServerData state = new ServerData { StatePathName = path };
            state.Restore();
            return state;
        }

        private static string ScratchRoot()
            => Path.Combine(Path.GetTempPath(), "galaxies-scratch-" + Path.GetRandomFileName());

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, destination));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, destination), overwrite: true);
            }
        }
    }
}
