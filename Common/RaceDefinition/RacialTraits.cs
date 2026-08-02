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

namespace Nova.Common
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Text;
    using System.Xml;

    /// <summary>
    /// Racial Traits: 
    /// Defines a colletion of TraitEntry objects representing the singular primary
    /// and zero or more lesser (secondary) racial traits. See PrimaryTraits and 
    /// SecondaryTraits for descriptions of these traits.
    /// </summary>
    /// <remarks>
    /// Inherits from TraitList and adds a PrimaryTrait field.</remarks>
    [Serializable]
    public class RacialTraits : TraitList
    {
        private TraitEntry primaryTrait = AllTraits.Data.Primary["JOAT"]; // Start with some default primary trait

        /// <summary>
        /// Default constructor.
        /// </summary>
        public RacialTraits()
        {
        }

        /// <summary>
        /// Loop through all of a races traits, starting with the primary trait.
        /// </summary>
        /// <returns>Each of the race's traits, begining with the PrimaryTrait, 
        /// followed by any lesser traits.</returns>
        public new IEnumerator GetEnumerator()
        {
            yield return this.primaryTrait;

            // Lesser traits come out sorted by code, not in hash order.
            //
            // TraitList derives from DictionaryBase, which is backed by a
            // Hashtable, and .NET randomizes string hashing per process. So
            // Dictionary.Values enumerated in a different order on every run. That
            // is invisible during play, because a race either has a trait or it does
            // not, but it changes the byte layout of everything written from this
            // loop: generating the same turn twice produced saved games whose <LRT>
            // elements appeared in different positions.
            //
            // That matters for M0 exit criterion 4, which compares a generated turn
            // against a committed golden BYTE FOR BYTE across .NET Framework 4.8 on
            // Windows and net10.0 on Linux. An unstable order fails that comparison
            // for a reason that has nothing to do with the engine, and the tempting
            // response is to re-baseline the golden, which would mask exactly the
            // cross-architecture divergence the check exists to catch.
            //
            // Sorting here rather than at each call site keeps every consumer
            // order-stable, which is what design Section A.4 asks for. The list is
            // at most a handful of entries, so the cost is not worth measuring.
            List<TraitEntry> lesserTraits = new List<TraitEntry>();
            foreach (TraitEntry trait in Dictionary.Values)
            {
                lesserTraits.Add(trait);
            }

            lesserTraits.Sort((left, right) => string.CompareOrdinal(left.Code, right.Code));

            foreach (TraitEntry trait in lesserTraits)
            {
                yield return trait;
            }
        }

        /// <summary>
        /// Check if the racial traits contains a particular trait.
        /// </summary>
        /// <param name="trait"></param>
        /// <returns>True if trait is the race's PrimaryTrait or a lesser trait.</returns>
        public new bool Contains(string trait)
        {
            if (this.primaryTrait.Code == trait)
            {
                return true;
            }
            if (Dictionary == null)
            {
                Report.Error("RacialTraits: Contains() - Dictionary is null.");
                return false; // FIXME (priority 4) - should never be null. Should be fixed now that this is based on DictionaryBase (requires testing) ---Dan 16 Oct 09
            }
            return Dictionary.Contains(trait);
        }

        /// <summary>
        /// Control access to the primary trait. It can be read as a public property. 
        /// It can be set using the SetPrimary() accessor function passing either a 
        /// TraitEntry or a String containing one of the primary trait codes.
        /// </summary>
        /// <remarks>Did not use a set method as I needed to overload depending on 
        /// whether a TraitEntry or a String is used to set the Primary racial trait.</remarks>
        public TraitEntry Primary
        {
            get
            {
                return this.primaryTrait;
            }
        }

        /// <summary>
        /// Control access to the primary trait. It can be read as a public property. 
        /// It can be set using the SetPrimary() accessor function passing either a 
        /// TraitEntry or a String containing one of the primary trait codes.
        /// </summary>
        /// <param name="primaryTrait">The new primary trait.</param>
        public void SetPrimary(TraitEntry primaryTrait)
        {
            this.primaryTrait = primaryTrait;
        }

        /// <summary>
        /// Control access to the primary trait. It can be read as a public property. 
        /// It can be set using the SetPrimary() accessor function passing either a 
        /// TraitEntry or a String containing one of the primary trait codes.
        /// </summary>
        /// <param name="primaryTrait">The new primary trait.</param>
        public void SetPrimary(string primaryTrait)
        {
            foreach (DictionaryEntry de in AllTraits.Data.Primary)
            {
                TraitEntry trait = de.Value as TraitEntry;
                if (trait.Code == primaryTrait || trait.Name == primaryTrait)
                {
                    this.primaryTrait = trait;
                    return;
                }
            }

            Report.Error("The primaryTrait \"" + primaryTrait + "\" is not recognised. Failed to set primary trait.");
        }
    }
}
