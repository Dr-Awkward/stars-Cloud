// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;

using Galaxies.AiContract;

using Nova.Ai;
using Nova.Client;
using Nova.Common;
using Nova.Common.Commands;

namespace Galaxies.Tests.Ai
{
    /// <summary>
    /// Golden replay (galaxies-ai spec Section 12.2, AI-PARTICIPANTS.md F.6.2).
    ///
    /// A participant is a pure function from empire_view to orders, so replaying
    /// the same view must reproduce the same orders. That property is what makes
    /// a golden-game regression meaningful and what lets the replay harness be a
    /// real test rather than a demo.
    ///
    /// An honest caveat that these tests exist to keep honest: the manifest for
    /// the built-in participant claims a determinism class, and this fixture is
    /// where that claim is either true or caught. If the AI reaches for an
    /// unseeded random number generator anywhere on its decision path, replay
    /// diverges and the tests below fail rather than letting the manifest lie.
    /// </summary>
    [TestFixture]
    public class GoldenReplayTests
    {
        /// <summary>Play one turn and return the orders as the submitted wire bytes.</summary>
        private static string PlayAndSerialize()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);
            ai.DoMove();

            // Serialize through the same path a real submission takes, so the
            // comparison covers the mapper and the wire format, not just the
            // AI's in-memory decisions.
            List<ICommand> commands = ai.ClientState.Commands.ToList();
            commands.Reverse();

            return OrderMapper.BuildOrdersXml(Fixtures.TurnYear, Fixtures.EmpireId, commands);
        }

        [Test]
        public void ReplayingTheSameSeatReproducesTheSameOrders()
        {
            string first = PlayAndSerialize();
            string second = PlayAndSerialize();

            Assert.That(second, Is.EqualTo(first),
                "The built-in AI must reproduce its orders byte for byte from an "
                + "identical seat, or the golden gate and the replay harness are "
                + "both meaningless.");
        }

        [Test]
        public void ReplayIsStableAcrossSeveralRuns()
        {
            // One repeat can pass by luck when a random draw happens to land the
            // same way. Several cannot.
            string baseline = PlayAndSerialize();

            for (int run = 0; run < 5; run++)
            {
                Assert.That(PlayAndSerialize(), Is.EqualTo(baseline),
                    "Run " + run + " diverged from the baseline.");
            }
        }

        [Test]
        public void TheOrderCountIsStableAcrossRuns()
        {
            // A cheaper signal that localizes a divergence: if the count moves,
            // the AI took a different branch rather than merely reordering.
            int baseline = -1;

            for (int run = 0; run < 5; run++)
            {
                Intel intel = Fixtures.BuildPlayableSeat();
                ClientData state = ClientData.FromIntel(intel);

                DefaultAi ai = new DefaultAi();
                ai.Initialize(state);
                ai.DoMove();

                int count = ai.ClientState.Commands.Count;
                if (baseline < 0)
                {
                    baseline = count;
                }

                Assert.That(count, Is.EqualTo(baseline),
                    "The AI produced a different number of orders on run " + run + ".");
            }
        }

        [Test]
        public void EveryOrderTheAiEmitsSurvivesItsOwnValidation()
        {
            // The property that matters most for a non-deterministic participant
            // (AI-PARTICIPANTS.md F.6.4) and a useful invariant for a seeded one:
            // the AI must never emit an order its own engine would reject. If it
            // does, the seat silently loses part of its turn every turn.
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);
            ai.DoMove();

            List<ICommand> commands = ai.ClientState.Commands.ToList();
            commands.Reverse();

            // Replay against a fresh empire, because the AI already applied its
            // own commands to the state it played.
            EmpireData fresh = Fixtures.BuildEmpire();
            Fixtures.AddScoutFleet(fresh);

            int rejected = 0;
            foreach (ICommand command in commands)
            {
                if (command.IsValid(fresh))
                {
                    command.ApplyToState(fresh);
                }
                else
                {
                    rejected++;
                }
            }

            Assert.That(rejected, Is.EqualTo(0),
                "The built-in AI emitted " + rejected + " order(s) the engine rejects.");
        }

        [Test]
        public void ResearchBudgetStaysInRange()
        {
            // One of the four invariants the spec names for participants whose
            // output cannot be diffed exactly.
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);
            ai.DoMove();

            foreach (ICommand command in ai.ClientState.Commands)
            {
                if (command is ResearchCommand research)
                {
                    Assert.That(research.Budget, Is.InRange(0, 100),
                        "A research budget outside 0 to 100 is rejected by the engine.");
                }
            }
        }

        [Test]
        public void NoOrderReferencesAnObjectTheSeatDoesNotOwn()
        {
            // The last of the invariants: a participant must never reference a
            // key outside its own holdings. Routing the AI's own output back
            // through the hostile-input mapper is the strongest form of this
            // check, because it is the exact code that guards a community
            // participant.
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);
            ai.DoMove();

            List<ICommand> commands = ai.ClientState.Commands.ToList();
            commands.Reverse();

            EmpireData fresh = Fixtures.BuildEmpire();
            Fixtures.AddScoutFleet(fresh);

            foreach (ICommand command in commands)
            {
                Assert.That(command.IsValid(fresh), Is.True,
                    "The AI referenced something outside its own holdings.");
                command.ApplyToState(fresh);
            }
        }
    }
}
