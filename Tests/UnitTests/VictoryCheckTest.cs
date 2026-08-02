// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using System.Linq;
using Nova.Common;
using Nova.Server;
using NUnit.Framework;

namespace Nova.Tests.UnitTests
{
    /// <summary>
    /// Declaring a victor records who won and does NOT end the game.
    ///
    /// That is a product decision, made 2026-08-01, and it matches the original
    /// Stars!: a winner is announced and play continues for anyone who wants to
    /// finish. Ending a game is a lifecycle decision owned by the control plane,
    /// when the host closes it or everyone has left. Players may leave without
    /// penalty once a victor stands.
    ///
    /// Before this, both win paths did nothing but append a Message, so the fact
    /// that a game had been won existed only as English prose inside a player's turn
    /// report. GenerationOutcome, GenerationCommit.WinnerEmpireId, and the game-over
    /// summary route were all written expecting a value the engine never produced.
    ///
    /// The victory RULES are unchanged and deliberately so: last man standing, plus
    /// a targets-met check gated on MinimumGameTime and TargetsToMeet. Only the
    /// recording of the result is new.
    /// </summary>
    [TestFixture]
    public class VictoryCheckTest
    {
        /// <summary>
        /// Last empire standing. Every owned star belongs to one empire, which is the
        /// condition that applies at any point in the game regardless of elapsed time.
        /// </summary>
        [Test]
        public void TheLastEmpireStandingIsRecordedAsTheWinner()
        {
            ServerData state = TwoEmpireGame();
            GiveAllStarsTo(state, empireId: 1);

            new VictoryCheck(state, new Scores(state)).Victor();

            Assert.AreEqual(1, state.WinnerEmpireId, "The surviving empire should be recorded as the winner.");
        }

        /// <summary>
        /// The decision under test. A victory must not stop the universe.
        /// </summary>
        [Test]
        public void DeclaringAVictorDoesNotEndTheGame()
        {
            ServerData state = TwoEmpireGame();
            GiveAllStarsTo(state, empireId: 1);

            new VictoryCheck(state, new Scores(state)).Victor();

            Assert.IsTrue(
                state.GameInProgress,
                "A declared victory ended the game. Galaxies announces a winner and lets play continue; "
                + "closing a game belongs to the control plane.");
        }

        /// <summary>
        /// Players are told, in words, as well as in the machine-readable field. The
        /// message is what a human actually sees in their turn report, so losing it
        /// while adding the field would be a regression.
        /// </summary>
        [Test]
        public void TheVictoryIsAlsoAnnouncedToEveryone()
        {
            ServerData state = TwoEmpireGame();
            GiveAllStarsTo(state, empireId: 1);

            new VictoryCheck(state, new Scores(state)).Victor();

            Assert.IsTrue(
                state.AllMessages.Any(m => m.Audience == Global.Everyone && m.Text.Contains("won the game")),
                "No victory message was sent to the players.");
        }

        /// <summary>
        /// A game still being contested has no winner. Without this, a test suite
        /// could pass with a VictoryCheck that declared somebody every single turn.
        /// </summary>
        [Test]
        public void AContestedGameHasNoWinner()
        {
            ServerData state = TwoEmpireGame();

            Star a = new Star { Name = "Alpha", Owner = 1 };
            Star b = new Star { Name = "Beta", Owner = 2 };
            state.AllStars[a.Name] = a;
            state.AllStars[b.Name] = b;

            new VictoryCheck(state, new Scores(state)).Victor();

            Assert.AreEqual(
                Global.Nobody, state.WinnerEmpireId,
                "A game with two surviving empires declared a winner.");
            Assert.IsTrue(state.GameInProgress, "A contested game should still be running.");
        }

        private static ServerData TwoEmpireGame()
        {
            ServerData state = new ServerData
            {
                TurnYear = Global.StartingYear,
                GameInProgress = true,
            };

            EmpireData first = new EmpireData();
            first.Id = 1;
            first.Race.Name = "Tom";
            first.Race.PluralName = "Toms";

            EmpireData second = new EmpireData();
            second.Id = 2;
            second.Race.Name = "Dick";
            second.Race.PluralName = "Dicks";

            state.AllEmpires[first.Id] = first;
            state.AllEmpires[second.Id] = second;

            return state;
        }

        private static void GiveAllStarsTo(ServerData state, ushort empireId)
        {
            Star owned = new Star { Name = "Alpha", Owner = empireId };
            Star alsoOwned = new Star { Name = "Beta", Owner = empireId };
            Star unowned = new Star { Name = "Gamma", Owner = Global.Nobody };

            state.AllStars[owned.Name] = owned;
            state.AllStars[alsoOwned.Name] = alsoOwned;
            state.AllStars[unowned.Name] = unowned;
        }
    }
}
