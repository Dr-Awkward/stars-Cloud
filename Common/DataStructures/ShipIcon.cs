#region Copyright Notice
// ============================================================================
// Copyright (C) 2011 The Stars-Nova Project
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
    using System.Xml;

    /// <summary>
    /// This object identifies a ship icon by the image file's path (Source). The
    /// engine needs only the identifier; the live Bitmap for display is loaded by
    /// the client presentation layer from Source (design Section A.2). Headless
    /// Common carries no System.Drawing.
    /// </summary>
    [Serializable]
    public class ShipIcon : ICloneable
    {
        private int index;
        public string Source = string.Empty;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public ShipIcon()
        {
        }

        /// <summary>
        /// Initializing constructor.
        /// </summary>
        /// <param name="source">The path and file name to the icon.</param>
        public ShipIcon(string source)
        {
            Source = source ?? string.Empty;

            // fi.Name format is <baseHull><iconNumber>.png where the length of <Number> in characters is defined by Global.ShipIconNumberingLength.
            int extensionSeperatorIndex = Source.LastIndexOf('.'); // position of the '.' in the file name

            // get the hull number of this icon.
            //
            // Headless port (design Section A.2): the identifier now round trips
            // through the state and intel XML on a server that has no graphics
            // folder, so a source that does not follow the numbering convention
            // (including an empty one) reaches this constructor in normal
            // operation. Parsing it unconditionally used to throw and take the
            // surrounding design load down with it. A source we cannot number is
            // still a usable identifier for the client, so keep it and leave the
            // index at zero; only the icon cycling operators care about the index.
            if (extensionSeperatorIndex >= Global.ShipIconNumberingLength)
            {
                int.TryParse(
                    Source.Substring(extensionSeperatorIndex - Global.ShipIconNumberingLength, Global.ShipIconNumberingLength),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out index);
            }
        }

        /// <summary>
        /// Increment the current icon image.
        /// </summary>
        /// <param name="icon">The currently selected icon.</param>
        /// <returns>The next race icon in the AllRaceIcons collection.</returns>
        static public ShipIcon operator ++(ShipIcon icon)
        {
            if (AllShipIcons.Data.IconList.Count == 0)
            {
                Report.Error("RaceIcon: operator++ - Race Icons failed to load.");
                return icon;
            }

            // icon.Source format is <baseHull><iconNumber>.png where the length of <Number> in characters is defined by Global.ShipIconNumberingLength.
            // need to split this up to get the baseHull 
            // (the number is stored as icon.Index, which is the key in the dictonary of ship icons associated with the baseHull)
            // then find the number of available icons, locate the next one and look up that icon.
            string baseHull = icon.Source.Substring(0, icon.Source.IndexOf('.') - Global.ShipIconNumberingLength);
            int iconCount = AllShipIcons.Data.Hulls[baseHull].Count;
            int nextIconIndex = icon.index + 1;
            if (nextIconIndex > (iconCount - 1))
            {
                nextIconIndex = 0;
            }
            // check for a missing index, this might happen if the hulls are not numbered sequentially
            if (!AllShipIcons.Data.Hulls[baseHull].ContainsKey(nextIconIndex))
            {
                nextIconIndex = 0; // only "safe" option
            }
            return (ShipIcon)AllShipIcons.Data.Hulls[baseHull][nextIconIndex];
        }

        /// <summary>
        /// Decrement the current icon image.
        /// </summary>
        /// <param name="icon">The currently selected icon.</param>
        /// <returns>The previous icon in the AllRaceIcons collection.</returns>
        static public ShipIcon operator --(ShipIcon icon)
        {
            if (AllShipIcons.Data.IconList.Count == 0)
            {
                Report.Error("RaceIcon: operator-- - Race Icons failed to load.");
                return icon;
            }
            string baseHull = icon.Source.Substring(0, icon.Source.IndexOf('.') - Global.ShipIconNumberingLength);
            int iconCount = AllShipIcons.Data.Hulls[baseHull].Count;
            int prevIconIndex = icon.index - 1;
            if (prevIconIndex < 0)
            {
                prevIconIndex = iconCount - 1;
            }
            return (ShipIcon)AllShipIcons.Data.Hulls[baseHull][prevIconIndex];
        }

        /// <summary>
        /// Return a clone of this object.
        /// </summary>
        public object Clone()
        {
            ShipIcon clone = new ShipIcon(Source);
            return clone as object;
        }

        /// <summary>
        /// Load from XML: initializing constructor from an XML node.
        /// </summary>
        /// <param name="xmlnode">An <see cref="XmlNode"/> within 
        /// a Nova game file (xml document).
        /// </param>
        public ShipIcon(XmlNode xmlnode)
        {
            XmlNode subnode = xmlnode.FirstChild;
            while (subnode != null)
            {
                try
                {
                    if (subnode.Name.ToLower() == "shipicon")
                    {
                        Source = subnode.FirstChild.Value;
                    }
                }
                catch (Exception e)
                {
                    Report.FatalError(e.Message + "\n Details: \n" + e);
                }
                subnode = subnode.NextSibling;
            }
        }

        /// <summary>
        /// Save: Serialize this object to an <see cref="XmlElement"/>.
        /// </summary>
        /// <param name="xmldoc">The parent <see cref="XmlDocument"/>.</param>
        /// <returns>An <see cref="XmlElement"/> representation of the ScoreRecord.</returns>
        /// <remarks>FIXME (priority 6) - Currently the icon is saved as the path to the icon. This is broken if the server is saving .intel and the client then loads it with the icons in a different location.</remarks>
        public XmlElement ToXml(XmlDocument xmldoc)
        {
            XmlElement xmlelRaceIcon = xmldoc.CreateElement("ShipIcon");

            // Source;
            Global.SaveData(xmldoc, xmlelRaceIcon, "ShipIcon", Source);

            return xmlelRaceIcon;
        }
    }
}
