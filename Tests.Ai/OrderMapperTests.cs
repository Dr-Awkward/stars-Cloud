// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Xml;

using NUnit.Framework;

using Galaxies.AiContract;

using Nova.Common;
using Nova.Common.Commands;

namespace Galaxies.Tests.Ai
{
    /// <summary>
    /// The trust boundary. Every test here is written from the position that the
    /// participant is hostile, because a community container and an LLM both are
    /// in the sense that matters: neither can be assumed correct.
    /// </summary>
    [TestFixture]
    public class OrderMapperTests
    {
        private EmpireData empire;
        private Fleet fleet;

        [SetUp]
        public void SetUp()
        {
            // Fresh every test. The engine holds process-wide statics and sharing
            // a Star or Race between methods contaminated later tests during the
            // M0 port.
            empire = Fixtures.BuildEmpire();
            fleet = Fixtures.AddScoutFleet(empire);
        }

        private static ActResponse Respond(params OrderDto[] orders) => new ActResponse
        {
            ContractVersion = ContractVersions.Current,
            RequestId = "test",
            EmpireId = Fixtures.EmpireId,
            TurnYear = Fixtures.TurnYear,
            Orders = orders.ToList(),
        };

        [Test]
        public void AResearchOrderMapsAndValidates()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Research,
                Budget = 25,
                Topics = new Dictionary<string, int> { { "Weapons", 1 }, { "Energy", 0 } },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(1), Because(result));
            Assert.That(result.Accepted[0], Is.InstanceOf<ResearchCommand>());
        }

        [Test]
        public void AnOutOfRangeResearchBudgetIsRejected()
        {
            // The engine's own rule: a budget outside 0 to 100 is invalid. We do
            // not re-implement it, we let the command say so.
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Research,
                Budget = 500,
                Topics = new Dictionary<string, int> { { "Weapons", 1 } },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.AllInvalid, Is.True,
                "A response with nothing valid in it must degrade to held orders.");
        }

        [Test]
        public void AWaypointOrderForAnOwnedFleetMaps()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Waypoint,
                Mode = "Add",
                FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                Index = 1,
                Waypoint = new WaypointView { X = 250, Y = 140, Warp = 6, Task = "None" },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(1), Because(result));
            Assert.That(result.Accepted[0], Is.InstanceOf<WaypointCommand>());
        }

        [Test]
        public void AWaypointOrderForAFleetWeDoNotOwnIsDropped()
        {
            // The forgery case. A participant naming someone else's fleet key
            // must not be able to move it.
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Waypoint,
                Mode = "Add",
                FleetKey = "999999999",
                Index = 1,
                Waypoint = new WaypointView { X = 1, Y = 1, Warp = 5, Task = "None" },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.DroppedCount, Is.EqualTo(1));
        }

        [Test]
        public void RenamingAFleetWeDoNotOwnIsDropped()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.RenameFleet,
                FleetKey = "424242",
                NewName = "Not Mine",
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.Dropped[0].Reason, Does.Contain("not owned"));
        }

        [Test]
        public void RenamingAnOwnedFleetToAnEmptyNameIsDropped()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.RenameFleet,
                FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                NewName = "   ",
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            Assert.That(result.AcceptedCount, Is.EqualTo(0));
        }

        [Test]
        public void RenamingAnOwnedFleetWorks()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.RenameFleet,
                FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                NewName = "Trailblazer",
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            Assert.That(result.AcceptedCount, Is.EqualTo(1), Because(result));
        }

        [Test]
        public void ProducingOnAStarWeDoNotOwnIsDropped()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Production,
                Mode = "Add",
                StarKey = "Beta",
                Index = 0,
                Order = new ProductionOrderDto { Unit = "Factory", Quantity = 1 },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.Dropped[0].Reason, Does.Contain("not owned"));
        }

        [Test]
        public void AnUnknownCommandTypeIsRejected()
        {
            ActResponse response = Respond(new OrderDto { Type = "SelfDestructEveryoneElse" });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.Dropped[0].Reason, Does.Contain("Unknown command type"));
        }

        [Test]
        public void AResponseForTheWrongTurnIsRejectedWholesale()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Research,
                Budget = 20,
                Topics = new Dictionary<string, int> { { "Weapons", 1 } },
            });
            response.TurnYear = Fixtures.TurnYear + 5;

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.AllInvalid, Is.True);
        }

        [Test]
        public void AResponseClaimingAnotherSeatIsRejectedWholesale()
        {
            // The single most important rejection in the file. The host stamps
            // the seat from its own dispatch record; a participant claiming a
            // different empire is refused outright rather than partially obeyed.
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Research,
                Budget = 20,
                Topics = new Dictionary<string, int> { { "Weapons", 1 } },
            });
            response.EmpireId = Fixtures.EmpireId + 1;

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.AllInvalid, Is.True);
        }

        [Test]
        public void AnUnsupportedContractVersionIsRejected()
        {
            ActResponse response = Respond(new OrderDto { Type = OrderTypes.Research, Budget = 20 });
            response.ContractVersion = "99.0";

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            Assert.That(result.AllInvalid, Is.True);
        }

        [Test]
        public void OrderSpamIsCapped()
        {
            List<OrderDto> spam = new List<OrderDto>();
            for (int i = 0; i < 50; i++)
            {
                spam.Add(new OrderDto
                {
                    Type = OrderTypes.RenameFleet,
                    FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                    NewName = "Spam " + i,
                });
            }

            ActResponse response = Respond(spam.ToArray());
            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId, maxOrders: 5);

            Assert.That(result.AcceptedCount, Is.LessThanOrEqualTo(5),
                "The cap must actually bound how much a participant can queue.");
            Assert.That(result.Dropped.Any(d => d.Reason.Contains("cap")), Is.True);
        }

        [Test]
        public void ANullResponseYieldsNothingAndIsNotTreatedAsAllInvalid()
        {
            // A participant that never answered is a timeout, which is held
            // orders. It is not the same as one that answered with garbage.
            MappedOrders result = OrderMapper.Map(null, empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AcceptedCount, Is.EqualTo(0));
            Assert.That(result.AllInvalid, Is.False);
        }

        [Test]
        public void AnEmptyOrderListIsNotAllInvalid()
        {
            MappedOrders result = OrderMapper.Map(Respond(), empire, Fixtures.TurnYear, Fixtures.EmpireId);

            Assert.That(result.AllInvalid, Is.False,
                "Deliberately doing nothing is a legitimate turn, not a failure.");
        }

        /// <summary>
        /// The conversion that would be a silent corruption bug. The CONTRACT
        /// carries fleet keys as decimal strings, because they are 64 bit and
        /// exceed the safe integer range of JSON. The ENGINE's command XML writes
        /// them as hexadecimal, with ToString("X"), and parses them back with
        /// NumberStyles.HexNumber. Get the conversion backwards in either
        /// direction and orders quietly apply to the wrong fleet, or to no fleet
        /// at all, with nothing throwing.
        /// </summary>
        [Test]
        public void FleetKeysSurviveDecimalToHexAndBack()
        {
            long realKey = fleet.Key;

            // Contract side: decimal.
            string decimalKey = EmpireViewTranscoder.Key(realKey);
            Assert.That(decimalKey, Is.EqualTo(realKey.ToString()),
                "The contract must carry the key in decimal.");

            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.RenameFleet,
                FleetKey = decimalKey,
                NewName = "Trailblazer",
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            Assert.That(result.AcceptedCount, Is.EqualTo(1), Because(result));

            // Engine side: the serialized command must carry hex, and the engine's
            // own reader must recover the identical key from it.
            string xml = OrderMapper.BuildOrdersXml(Fixtures.TurnYear, Fixtures.EmpireId, result.Accepted);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            XmlNode command = doc.SelectSingleNode("ROOT/Orders/Command");
            Assert.That(command, Is.Not.Null, "The orders document must carry the command.");

            XmlNode keyNode = command.SelectSingleNode("FleetKey");
            Assert.That(keyNode, Is.Not.Null);
            Assert.That(keyNode.InnerText, Is.EqualTo(realKey.ToString("X")),
                "The engine's wire format is hexadecimal.");

            // And the round trip: the engine's own registry must rebuild it.
            ICommand rebuilt = CommandRegistry.Instance.Create(
                command.Attributes["Type"].Value, command);

            Assert.That(rebuilt, Is.InstanceOf<RenameFleetCommand>());
            Assert.That(((RenameFleetCommand)rebuilt).FleetKey, Is.EqualTo(realKey),
                "The key that comes back out must be the key that went in.");
        }

        [Test]
        public void TheOrdersDocumentMatchesTheShapeTheEngineReads()
        {
            ActResponse response = Respond(new OrderDto
            {
                Type = OrderTypes.Research,
                Budget = 30,
                Topics = new Dictionary<string, int> { { "Energy", 1 } },
            });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            string xml = OrderMapper.BuildOrdersXml(2101, Fixtures.EmpireId, result.Accepted);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            // These three selectors are exactly what the engine's order reader
            // uses. If they move, an AI's orders silently stop being read.
            Assert.That(doc.SelectSingleNode("ROOT/Turn"), Is.Not.Null, "ROOT/Turn is required.");
            Assert.That(doc.SelectSingleNode("ROOT/Id"), Is.Not.Null, "ROOT/Id is required.");
            Assert.That(doc.SelectSingleNode("ROOT/Orders"), Is.Not.Null, "ROOT/Orders is required.");

            Assert.That(doc.SelectSingleNode("ROOT/Turn").InnerText, Is.EqualTo("2101"),
                "The turn year is written in decimal, as the reader parses it.");
            Assert.That(doc.SelectSingleNode("ROOT/Id").InnerText,
                Is.EqualTo(Fixtures.EmpireId.ToString()));

            foreach (XmlNode node in doc.SelectSingleNode("ROOT/Orders").ChildNodes)
            {
                Assert.That(node.Attributes["Type"], Is.Not.Null,
                    "Every command element must carry the Type attribute the registry dispatches on.");
                Assert.That(CommandRegistry.Instance.IsRegistered(node.Attributes["Type"].Value), Is.True,
                    "Every emitted type must be one the engine knows.");
            }
        }

        [Test]
        public void CommandsAreWrittenNewestFirst()
        {
            // The engine pushes each command onto a stack as it reads, then pops
            // to apply, so the file has to be newest first for orders to apply in
            // the order they were issued. Reversing this silently reorders play.
            ActResponse response = Respond(
                new OrderDto
                {
                    Type = OrderTypes.RenameFleet,
                    FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                    NewName = "First",
                },
                new OrderDto
                {
                    Type = OrderTypes.RenameFleet,
                    FleetKey = EmpireViewTranscoder.Key(fleet.Key),
                    NewName = "Second",
                });

            MappedOrders result = OrderMapper.Map(response, empire, Fixtures.TurnYear, Fixtures.EmpireId);
            Assert.That(result.AcceptedCount, Is.EqualTo(2), Because(result));

            string xml = OrderMapper.BuildOrdersXml(Fixtures.TurnYear, Fixtures.EmpireId, result.Accepted);
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            XmlNodeList commands = doc.SelectSingleNode("ROOT/Orders").ChildNodes;
            Assert.That(commands[0].SelectSingleNode("NewName").InnerText, Is.EqualTo("Second"),
                "The newest order is written first.");
            Assert.That(commands[1].SelectSingleNode("NewName").InnerText, Is.EqualTo("First"));
        }

        // Surfaces why a mapping failed when an assertion trips, so a failure
        // names the reason instead of just a count.
        private static string Because(MappedOrders result)
            => "dropped: " + string.Join("; ", result.Dropped.Select(d => d.Type + " -> " + d.Reason));
    }
}
