// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

using Nova.Common;
using Nova.Common.Waypoints;
using Nova.Common.Components;
using Nova.Common.DataStructures;
using Nova.Server;

namespace Nova.Tests.UnitTests
{
    /// <summary>
    /// Determinism regression (design Section A.4). With the RNG seeded from
    /// ServerData.MasterSeed and iteration order made deterministic, generating a
    /// turn twice from identical inputs must produce an identical result. This is
    /// the golden-turn idea expressed self-consistently, so it runs on the Linux
    /// target with no .NET Framework baseline to capture: it catches unseeded
    /// randomness and dictionary-order divergence introduced by the port.
    ///
    /// The engine still holds process-wide singletons (GameSettings, AllComponents),
    /// which is why cloud turn generation runs one game per instance
    /// (concurrency = 1). Within one process, identical inputs plus a fixed seed
    /// must reproduce exactly, and that is what this asserts.
    /// </summary>
    [TestFixture]
    public class DeterminismTest
    {
        // Build the same small game every time, mirroring the known-good
        // TurnGeneratorTest scenario (a fleet with a scrap task, a star, one
        // empire), under a fixed master seed so the whole turn pipeline runs.
        private static ServerData BuildGame(long seed)
        {
            ServerData state = new SimpleServerData();
            state.MasterSeed = seed;

            Fleet fleet = new Fleet(1);
            fleet.Owner = 1;
            ShipToken token = new ShipToken(new ShipDesign(1), 1);
            fleet.Composition.Add(token.Key, token);
            Waypoint waypoint = new Waypoint();
            waypoint.Task = new ScrapTask();
            waypoint.Destination = "Star1";
            fleet.Waypoints.Add(waypoint);

            Star star = new Star();
            star.Name = "Star1";
            state.AllStars.Add(star.Key, star);

            EmpireData empire = new SimpleEmpireData();
            empire.Id = 1;
            empire.OwnedFleets.Add(fleet);
            state.AllEmpires.Add(empire.Id, empire);
            return state;
        }

        // A stable signature of the post-turn universe, independent of any file path.
        private static string Signature(ServerData s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Year=").Append(s.TurnYear).Append(';');
            foreach (Fleet f in s.IterateAllFleets())
            {
                sb.Append(f.Key).Append('@')
                  .Append(f.Position.X).Append(',').Append(f.Position.Y)
                  .Append("/b=").Append(f.Bearing).Append(';');
            }
            foreach (Message m in s.AllMessages)
            {
                sb.Append("msg:").Append(m.Text).Append('|');
            }
            return sb.ToString();
        }

        [Test]
        public void SameSeedProducesIdenticalTurn()
        {
            ServerData a = BuildGame(1234567);
            new SimpleTurnGenerator(a).Generate();

            ServerData b = BuildGame(1234567);
            new SimpleTurnGenerator(b).Generate();

            Assert.AreEqual(Signature(a), Signature(b),
                "A turn generated twice from identical inputs and seed must be identical.");
        }

        [Test]
        public void MasterSeedRoundTripsThroughXml()
        {
            // A bare state (no empires) exercises the ServerState-root persistence
            // of MasterSeed and FormatVersion without needing a fully initialised
            // empire graph.
            ServerData state = new SimpleServerData();
            state.MasterSeed = 987654321;
            state.TurnYear = 2110;
            state.GameFolder = "n/a"; // the load ctor reads element text directly

            string path = Path.Combine(Path.GetTempPath(), "galaxies-determinism-seed.sstate");
            state.StatePathName = path;
            state.Save();

            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.Load(path);
            ServerData reloaded = new ServerData(doc);

            Assert.AreEqual(987654321L, reloaded.MasterSeed, "MasterSeed must survive the state XML round trip.");
            Assert.AreEqual(ServerData.CurrentFormatVersion, reloaded.FormatVersion, "FormatVersion must be written and read back.");
            Assert.AreEqual(2110, reloaded.TurnYear, "TurnYear must survive the round trip.");
        }
    }
}
