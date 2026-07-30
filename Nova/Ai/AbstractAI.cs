#region Copyright Notice
// ============================================================================
// Copyright (C) 2009, 2010 stars-nova
//
// This file is part of Stars-Nova.
// See <http://sourceforge.net/projects/stars-nova/>.
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 2 as
// published by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>
// ===========================================================================
#endregion

namespace Nova.Ai
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Nova.Client;
    using Nova.Common;

    public abstract class AbstractAI
    {
        protected string raceName;
        protected int turnNumber = -1;
        protected ClientData clientState;

        /// <summary>
        /// Desktop initialization: build the client state from command line
        /// arguments, which reads the seat's .intel off a shared folder. Retired on
        /// the cloud path; see the ClientData overload below.
        /// </summary>
        public void Initialize(CommandArguments commandArguments)
        {
            raceName = commandArguments[CommandArguments.Option.RaceName];
            turnNumber = int.Parse(commandArguments[CommandArguments.Option.Turn], System.Globalization.CultureInfo.InvariantCulture);
            clientState = new ClientData();
            clientState.Initialize(commandArguments.ToArray());
        }

        /// <summary>
        /// Headless initialization (Galaxies M3). A cloud participant is handed one
        /// empire's view in a request body; there is no shared folder, no .lock file,
        /// and no file to read. The dispatch worker builds the ClientData from the
        /// seat's own intel and hands it straight in, which is what lets N AI seats
        /// run as N concurrent invocations instead of one at a time.
        /// </summary>
        /// <param name="state">The seat's client state, already populated.</param>
        public void Initialize(ClientData state)
        {
            if (state == null)
            {
                throw new ArgumentNullException("state");
            }

            clientState = state;
            raceName = state.EmpireState != null && state.EmpireState.Race != null
                ? state.EmpireState.Race.Name
                : string.Empty;
            turnNumber = state.EmpireState != null ? state.EmpireState.TurnYear : -1;
        }

        public abstract void DoMove();
        
        public ClientData ClientState
        {
            // This is used in the AI runner.
            get { return clientState; }
        }
    }
}
