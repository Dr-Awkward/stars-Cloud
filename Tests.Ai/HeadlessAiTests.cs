// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System;
using System.Collections.Generic;

using NUnit.Framework;

using Nova.Ai;
using Nova.Client;
using Nova.Common;
using Nova.Common.Commands;

namespace Galaxies.Tests.Ai
{
    /// <summary>
    /// The M3 foundation test: the Stars! Nova AI actually runs with no UI.
    ///
    /// Compiling headless is not the same as running headless. The AI used to
    /// live inside the WinForms project, and its client state opened a file
    /// dialog to find a game folder and a race dialog to pick a race. If any of
    /// that survived the extraction, the code would still compile against the
    /// ported engine and then throw the first time a container tried to play a
    /// turn. These tests are what makes the difference visible.
    /// </summary>
    [TestFixture]
    public class HeadlessAiTests
    {
        [Test]
        public void DefaultAiPlaysATurnWithNoUserInterface()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);

            // The whole point: this call is the AI playing a turn. On the desktop
            // it was reached only after a dialog picked a race and a file was read
            // off a shared folder. Here it runs from an in-memory intel.
            Assert.DoesNotThrow(() => ai.DoMove(),
                "The headless AI must play a turn without a UI, a game folder, or a lock file.");

            Assert.That(ai.ClientState, Is.Not.Null, "The AI must expose the state it played.");
            Assert.That(ai.ClientState.EmpireState, Is.Not.Null);
            Assert.That(ai.ClientState.EmpireState.Id, Is.EqualTo(Fixtures.EmpireId),
                "The AI must play the seat it was handed, not some other empire.");
        }

        [Test]
        public void DefaultAiProducesOrdersForAFreshEmpire()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            DefaultAi ai = new DefaultAi();
            ai.Initialize(state);
            ai.DoMove();

            Stack<ICommand> commands = ai.ClientState.Commands;
            Assert.That(commands, Is.Not.Null);

            // An empire with a home world, a scout, an unexplored neighbour, and a
            // research budget has something to do. If the AI emits nothing at all,
            // the extraction has quietly broken its decision path even though it
            // did not throw.
            Assert.That(commands.Count, Is.GreaterThan(0),
                "A fresh empire with somewhere to go should produce at least one order.");

            foreach (ICommand command in commands)
            {
                Assert.That(command, Is.Not.Null, "No order may be null.");
            }
        }

        [Test]
        public void HeadlessInitializeRejectsANullState()
        {
            DefaultAi ai = new DefaultAi();
            Assert.Throws<ArgumentNullException>(() => ai.Initialize((ClientData)null));
        }

        [Test]
        public void TheDesktopFileAndDialogPathIsRetiredNotSilentlyBroken()
        {
            // The cloud client state deliberately keeps the old signature but
            // refuses it, so a caller that still expects the shared-folder path
            // gets a clear message instead of a mysterious null reference.
            ClientData state = new ClientData();
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                () => state.Initialize(new string[] { "--ai", "-r", "Gestalti" }));

            Assert.That(error.Message, Does.Contain("dispatch request"),
                "The refusal must point the caller at the cloud seam.");
        }

        [Test]
        public void ClientDataFromIntelCarriesTheSeatThrough()
        {
            Intel intel = Fixtures.BuildPlayableSeat();
            ClientData state = ClientData.FromIntel(intel);

            Assert.That(state.EmpireState, Is.SameAs(intel.EmpireState),
                "The client state must play the empire the intel describes.");
            Assert.That(state.InputTurn, Is.SameAs(intel));
            Assert.That(state.Messages, Is.SameAs(intel.Messages));
            Assert.That(state.Commands, Is.Not.Null);
            Assert.That(state.Commands.Count, Is.EqualTo(0),
                "A seat starts a turn with no orders.");
        }

        [Test]
        public void TwoAiRunsOverTheSameStateDoNotInterfere()
        {
            // N seats run as N concurrent invocations in the cloud, where the
            // desktop runner could only ever run one behind a lock file. Nothing
            // in the AI may hold cross-instance state.
            Intel first = Fixtures.BuildPlayableSeat();
            Intel second = Fixtures.BuildPlayableSeat();

            DefaultAi a = new DefaultAi();
            a.Initialize(ClientData.FromIntel(first));
            a.DoMove();

            DefaultAi b = new DefaultAi();
            b.Initialize(ClientData.FromIntel(second));
            b.DoMove();

            Assert.That(a.ClientState.Commands.Count, Is.EqualTo(b.ClientState.Commands.Count),
                "Two AI instances given identical states must reach the same number of orders.");
        }
    }
}
