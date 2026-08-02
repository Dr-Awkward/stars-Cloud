// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using System.Collections;
using System.Collections.Generic;
using Nova.Common;
using NUnit.Framework;

namespace Nova.Tests.UnitTests
{
    /// <summary>
    /// Racial traits must enumerate in a stable order.
    ///
    /// TraitList derives from DictionaryBase, which is backed by a Hashtable, and
    /// .NET randomizes string hashing PER PROCESS. So enumeration order was stable
    /// within a run and different between runs. Race.ToXml writes one LRT element
    /// per trait straight out of this enumerator, so the same race serialized with
    /// its lesser traits in different positions on different runs, and a saved game
    /// therefore did not reproduce itself.
    ///
    /// That breaks M0 exit criterion 4, which compares a generated turn against a
    /// committed golden BYTE FOR BYTE across .NET Framework 4.8 on Windows and
    /// net10.0 on Linux.
    ///
    /// This is a separate test from the byte-identity check in
    /// Tests.ServerHost/TurnRunTests.cs on purpose. That one generates two turns in
    /// ONE process, so both share a hash seed and it cannot see this defect at all.
    /// Only an explicit assertion on the ordering catches it without spawning a
    /// second process.
    /// </summary>
    [TestFixture]
    public class RacialTraitsOrderTest
    {
        /// <summary>
        /// Codes chosen so that insertion order, alphabetical order, and the order
        /// they appear in AllTraits are all different. Inserting in reverse
        /// alphabetical order means a test that merely preserved insertion order
        /// would fail.
        /// </summary>
        private static readonly string[] Codes = { "TT", "NAS", "IFE", "CE", "ARM" };

        [Test]
        public void LesserTraitsEnumerateInSortedOrder()
        {
            RacialTraits traits = BuildTraits();

            List<string> seen = Enumerate(traits);

            // The primary trait comes first by contract, then the lesser traits.
            Assert.AreEqual(Codes.Length + 1, seen.Count, "Expected the primary trait plus every lesser trait.");

            List<string> lesser = seen.GetRange(1, seen.Count - 1);
            List<string> expected = new List<string>(Codes);
            expected.Sort(System.StringComparer.Ordinal);

            CollectionAssert.AreEqual(expected, lesser, "Lesser traits did not enumerate in sorted order.");
        }

        /// <summary>
        /// Two enumerations of the same collection must agree. This is weaker than
        /// the cross-process property that actually matters, but it is the part that
        /// can be asserted in one process, and it fails immediately if the ordering
        /// is ever made dependent on enumeration state.
        /// </summary>
        [Test]
        public void RepeatedEnumerationIsStable()
        {
            RacialTraits traits = BuildTraits();

            CollectionAssert.AreEqual(
                Enumerate(traits),
                Enumerate(traits),
                "Two enumerations of the same traits disagreed.");
        }

        private static RacialTraits BuildTraits()
        {
            RacialTraits traits = new RacialTraits();
            foreach (string code in Codes)
            {
                traits.Add(AllTraits.Data.All[code]);
            }

            return traits;
        }

        private static List<string> Enumerate(RacialTraits traits)
        {
            List<string> codes = new List<string>();
            IEnumerator enumerator = traits.GetEnumerator();
            while (enumerator.MoveNext())
            {
                codes.Add(((TraitEntry)enumerator.Current).Code);
            }

            return codes;
        }
    }
}
