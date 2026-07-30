// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This project is
// likewise distributed under the GNU General Public License version 2; see
// LICENSE.txt at the repository root.

namespace Nova.Client
{
    using System;
    using System.Collections.Generic;

    using Nova.Common;
    using Nova.Common.Commands;

    /// <summary>
    /// The headless client state an AI participant plays against (Galaxies M3).
    ///
    /// The desktop ClientData (Nova/Client/ClientData.cs) is a WinForms type: its
    /// Initialize() path opens an OpenFileDialog to find a game folder and a
    /// SelectRaceDialog to pick a race, then reads the seat's .intel off a shared
    /// folder. None of that survives in the cloud, where a participant is handed
    /// exactly one empire's view in a request body and hands back orders.
    ///
    /// So this is the same shape with the file and dialog paths cut away: the four
    /// members the AI actually reads and writes (EmpireState, InputTurn, Messages,
    /// Commands) and nothing else. The desktop file stays on disk untouched for the
    /// eventual GUI port, exactly as the M0 engine port left the WinForms files in
    /// place (design Section A.2).
    ///
    /// Note the AI never seeds the engine RNG. ServerData.MasterSeed and the derived
    /// per-turn and per-seat seeds are server side only; a participant receives its
    /// per-seat seed in the dispatch and uses it for its own randomness.
    /// </summary>
    [Serializable]
    public sealed class ClientData
    {
        /// <summary>
        /// This empire's authoritative state: owned stars and fleets, plus the
        /// reports of everything it has scanned. Already fog-of-war correct when it
        /// arrives, because IntelWriter projected it per empire server side.
        /// </summary>
        public EmpireData EmpireState = new EmpireData();

        /// <summary>
        /// The orders this AI is composing. DefaultAi pushes ICommands here and the
        /// dispatch worker serializes the stack to the orders wire shape.
        /// </summary>
        public Stack<ICommand> Commands = new Stack<ICommand>();

        /// <summary>Messages addressed to this empire (or to everyone) this turn.</summary>
        public List<Message> Messages = new List<Message>();

        /// <summary>The intel this turn was built from.</summary>
        public Intel InputTurn = null;

        /// <summary>True on the first turn of a game, when there is no prior state.</summary>
        public bool FirstTurn = true;

        /// <summary>
        /// Build a client state from one seat's intel. This is the whole cloud
        /// ingest path: no folder, no lock file, no dialog.
        /// </summary>
        /// <param name="intel">The seat's own intel, as IntelWriter produced it.</param>
        /// <returns>A client state the AI can play.</returns>
        public static ClientData FromIntel(Intel intel)
        {
            if (intel == null)
            {
                throw new ArgumentNullException("intel");
            }

            ClientData state = new ClientData();
            state.InputTurn = intel;
            state.EmpireState = intel.EmpireState;
            state.Messages = intel.Messages ?? new List<Message>();

            // The engine's own convention: the opening year is the first turn.
            state.FirstTurn = intel.EmpireState != null
                && intel.EmpireState.TurnYear <= Global.StartingYear;

            return state;
        }

        /// <summary>
        /// The desktop command line initialization path, which read the seat's
        /// .intel from a shared folder and prompted for a race. There is no such
        /// path in the cloud, so this fails loudly rather than pretending.
        /// AbstractAI.Initialize(ClientData) is the cloud seam.
        /// </summary>
        public void Initialize(string[] argArray)
        {
            throw new NotSupportedException(
                "A cloud AI participant receives its empire view in the dispatch request, "
                + "not from a shared folder. Use AbstractAI.Initialize(ClientData) with "
                + "ClientData.FromIntel(intel).");
        }
    }
}
