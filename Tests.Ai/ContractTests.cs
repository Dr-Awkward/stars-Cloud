// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System;
using System.Linq;

using NUnit.Framework;

using Galaxies.AiContract;

using Nova.Common;

namespace Galaxies.Tests.Ai
{
    /// <summary>
    /// The envelope and the seat seed: the two pieces of the contract that carry
    /// state between the host and a participant.
    /// </summary>
    [TestFixture]
    public class EnvelopeTests
    {
        [Test]
        public void GzipBase64RoundTripsExactly()
        {
            string original = "<ROOT><Turn>2101</Turn><Id>7</Id></ROOT>";
            string packed = Envelope.Pack(original);

            Assert.That(packed, Is.Not.EqualTo(original), "Packing must actually encode.");
            Assert.That(Envelope.Unpack(packed), Is.EqualTo(original));
        }

        [Test]
        public void IntelSurvivesTheNativeRoundTrip()
        {
            // This is the path the first-party participant depends on. If intel
            // does not survive it losslessly, the built-in AI plays a different
            // game than the turn generator wrote, and golden replay is worthless.
            Intel original = Fixtures.BuildPlayableSeat();

            NativePayload payload = Envelope.PackIntel(Envelope.WriteIntel(original));
            Intel restored = Envelope.ReadIntel(payload);

            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.EmpireState, Is.Not.Null);
            Assert.That(restored.EmpireState.Id, Is.EqualTo(original.EmpireState.Id),
                "The seat must survive the round trip.");
            Assert.That(restored.EmpireState.TurnYear, Is.EqualTo(original.EmpireState.TurnYear));
            Assert.That(restored.EmpireState.OwnedStars.Count,
                Is.EqualTo(original.EmpireState.OwnedStars.Count),
                "Owned stars must survive the round trip.");
            Assert.That(restored.EmpireState.OwnedFleets.Count,
                Is.EqualTo(original.EmpireState.OwnedFleets.Count),
                "Owned fleets must survive the round trip.");

            // Designs are the round trip's weak point and the one that matters
            // most for the AI. A server container has no graphics folder, so a
            // design written headless carries an empty icon element; the loader
            // used to dereference that empty element, fail, and drop the design.
            // The AI would then wake up with no ships it could build and quietly
            // play a much worse game, with nothing failing loudly.
            Assert.That(restored.EmpireState.Designs.Count,
                Is.EqualTo(original.EmpireState.Designs.Count),
                "Ship designs must survive the round trip, icons or no icons.");

            foreach (var design in restored.EmpireState.Designs.Values)
            {
                Assert.That(design.Blueprint, Is.Not.Null,
                    "A restored design must keep its hull, or it cannot be built.");
            }
        }

        [Test]
        public void ADesignWithNoIconStillRoundTripsBothWays()
        {
            // Pins the headless icon fix directly. Icon is null for every design
            // the AI creates on a server, because the identifier comes from a
            // graphics database that is not deployed there.
            Nova.Common.EmpireData empire = Fixtures.BuildEmpire();
            Fixtures.AddScoutFleet(empire);

            foreach (var design in empire.Designs.Values)
            {
                Assert.That(design.Icon, Is.Null,
                    "The fixture reproduces the server condition: no icon.");
            }

            Intel intel = Fixtures.BuildIntel(empire);
            Intel restored = Envelope.ReadIntel(Envelope.PackIntel(Envelope.WriteIntel(intel)));

            Assert.That(restored.EmpireState.Designs.Count, Is.EqualTo(empire.Designs.Count),
                "An icon-less design must survive both the write and the load.");
        }

        [Test]
        public void AnUnknownEncodingIsRefusedRatherThanGuessed()
        {
            NativePayload payload = new NativePayload
            {
                ContentType = Envelope.IntelContentType,
                Encoding = "rot13",
                Body = "nonsense",
            };

            Assert.Throws<NotSupportedException>(() => Envelope.Decode(payload),
                "Silently misreading a seat's intel is worse than failing the dispatch.");
        }

        [Test]
        public void IdentityEncodingPassesThrough()
        {
            NativePayload payload = new NativePayload
            {
                Encoding = Envelope.Identity,
                Body = "<ROOT/>",
            };

            Assert.That(Envelope.Decode(payload), Is.EqualTo("<ROOT/>"));
        }
    }

    /// <summary>
    /// The per-seat seed. A participant declaring seeded determinism uses only
    /// this, so it must be stable across processes and distinct per seat.
    /// </summary>
    [TestFixture]
    public class SeatSeedTests
    {
        [Test]
        public void TheSameInputsAlwaysGiveTheSameSeed()
        {
            string a = SeatSeed.For(123456789L, 2101, 7);
            string b = SeatSeed.For(123456789L, 2101, 7);

            Assert.That(a, Is.EqualTo(b),
                "Seat seeds must be reproducible, or golden replay means nothing.");
        }

        [Test]
        public void DifferentSeatsAndTurnsGetDifferentSeeds()
        {
            string seat7 = SeatSeed.For(123456789L, 2101, 7);
            string seat8 = SeatSeed.For(123456789L, 2101, 8);
            string nextYear = SeatSeed.For(123456789L, 2102, 7);

            Assert.That(seat7, Is.Not.EqualTo(seat8),
                "Two seats in the same turn must not share a random stream.");
            Assert.That(seat7, Is.Not.EqualTo(nextYear),
                "The same seat must not replay the same stream every turn.");
        }

        [Test]
        public void TheSeedRoundTripsThroughItsWireForm()
        {
            int derived = SeedDerivation.ForSeat(987654321L, 2105, 3);
            string formatted = SeatSeed.Format(derived);

            Assert.That(SeatSeed.Parse(formatted), Is.EqualTo(derived));
        }

        [Test]
        public void AMalformedSeedDegradesRatherThanThrowing()
        {
            // A participant handed a broken seed should still play, just not
            // reproducibly. Throwing here would turn a cosmetic problem into a
            // lost turn.
            Assert.That(SeatSeed.Parse("not-a-seed"), Is.EqualTo(0));
            Assert.That(SeatSeed.Parse(null), Is.EqualTo(0));
        }
    }

    /// <summary>
    /// The empire_view projection, including the fog-of-war guarantee that is the
    /// single most important property of the whole contract.
    /// </summary>
    [TestFixture]
    public class TranscoderTests
    {
        [Test]
        public void TheProjectionCarriesOwnedStateAndScannedReportsSeparately()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            EmpireView view = EmpireViewTranscoder.Project(intel);

            Assert.That(view.OwnedStars.Count, Is.EqualTo(1), "The seat owns one world.");
            Assert.That(view.OwnedStars[0].Name, Is.EqualTo("Alpha"));
            Assert.That(view.OwnedStars[0].Population, Is.GreaterThan(0),
                "An owned world reports its population; a scanned one may not.");

            Assert.That(view.StarReports.Any(s => s.Name == "Beta"), Is.True,
                "A scanned neighbour must appear as a report.");
            Assert.That(view.StarReports.Any(s => s.Name == "Alpha"), Is.False,
                "An owned world must not also appear as a stale report.");
        }

        [Test]
        public void ANeverScannedWorldKeepsTheEnginesUnsetYear()
        {
            // Projecting an unseen star as year zero would read as "scanned in
            // year zero" rather than "never seen", and a participant would trust
            // stale-looking data that never existed.
            Intel intel = Fixtures.BuildPlayableSeat();
            EmpireView view = EmpireViewTranscoder.Project(intel);

            StarReportView unseen = view.StarReports.First(s => s.Name == "Gamma");
            Assert.That(unseen.Year, Is.EqualTo(Global.Unset));
        }

        [Test]
        public void SixtyFourBitKeysTravelAsDecimalStrings()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            EmpireView view = EmpireViewTranscoder.Project(intel);

            Assert.That(view.OwnedFleets.Count, Is.EqualTo(1));
            string key = view.OwnedFleets[0].Key;

            long parsed;
            Assert.That(EmpireViewTranscoder.TryParseKey(key, out parsed), Is.True,
                "Fleet keys must parse back as decimal.");
            Assert.That(parsed, Is.EqualTo(intel.EmpireState.OwnedFleets.Values.First().Key),
                "The projected key must be the real engine key.");
        }

        [Test]
        public void AllSixResearchFieldsAreProjected()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            EmpireView view = EmpireViewTranscoder.Project(intel);

            Assert.That(view.Research.Levels.Count, Is.EqualTo(6),
                "TechLevel has six fields and all six must reach the participant.");
            Assert.That(view.Research.Levels.ContainsKey("Weapons"), Is.True);
            Assert.That(view.Research.Levels.ContainsKey("Biotechnology"), Is.True);
            Assert.That(view.Research.Budget, Is.EqualTo(intel.EmpireState.ResearchBudget));
        }

        [Test]
        public void TheProjectionNeverCarriesAnotherEmpiresOwnedState()
        {
            // The structural guarantee: the transcoder is handed exactly one
            // intel and has no reference through which another empire's owned
            // stars or fleets could arrive. This test pins the property so a
            // future change that adds such a reference fails loudly.
            Intel intel = Fixtures.BuildPlayableSeat();
            EmpireView view = EmpireViewTranscoder.Project(intel);

            foreach (StarView star in view.OwnedStars)
            {
                Assert.That(star.Owner, Is.EqualTo(Fixtures.EmpireId),
                    "Every owned star must belong to this seat.");
            }

            foreach (FleetView fleet in view.OwnedFleets)
            {
                Assert.That(fleet.Owner, Is.EqualTo(Fixtures.EmpireId),
                    "Every owned fleet must belong to this seat.");
            }

            foreach (OtherEmpireView other in view.OtherEmpires)
            {
                Assert.That(other.Id, Is.Not.EqualTo(Fixtures.EmpireId));
            }
        }

        [Test]
        public void TheRequestStampsTheSeatAndTheSeed()
        {
            Intel intel = Fixtures.BuildPlayableSeat();

            ActRequest request = EmpireViewTranscoder.BuildRequest(
                intel,
                gameId: "roybot:game:test",
                turnYear: Fixtures.TurnYear,
                seatSeed: SeatSeed.For(42L, Fixtures.TurnYear, Fixtures.EmpireId),
                difficulty: "hard",
                settings: null,
                deadline: DateTimeOffset.UtcNow.AddMinutes(5),
                includeNativeIntel: true);

            Assert.That(request.Seat.EmpireId, Is.EqualTo(Fixtures.EmpireId));
            Assert.That(request.Game.TurnYear, Is.EqualTo(Fixtures.TurnYear));
            Assert.That(request.Game.SeatSeed, Is.Not.Empty);
            Assert.That(request.ContractVersion, Is.EqualTo(ContractVersions.Current));
            Assert.That(request.RequestId, Is.Not.Empty);
            Assert.That(request.IntelNative, Is.Not.Null,
                "A first-party dispatch carries the native intel.");

            // And it must actually be readable, not just present.
            Intel restored = Envelope.ReadIntel(request.IntelNative);
            Assert.That(restored.EmpireState.Id, Is.EqualTo(Fixtures.EmpireId));
        }

        [Test]
        public void NativeIntelIsOmittedWhenNotRequested()
        {
            Intel intel = Fixtures.BuildPlayableSeat();

            ActRequest request = EmpireViewTranscoder.BuildRequest(
                intel, "g", Fixtures.TurnYear, "abc", "normal", null,
                DateTimeOffset.UtcNow.AddMinutes(5), includeNativeIntel: false);

            Assert.That(request.IntelNative, Is.Null,
                "A community participant gets the projection only.");
            Assert.That(request.EmpireView.OwnedStars.Count, Is.EqualTo(1),
                "It still gets a usable view.");
        }
    }
}
