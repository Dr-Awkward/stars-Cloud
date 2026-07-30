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
    using System.Collections;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;

    using Nova.Common.Components;

    /// <summary>
    /// A singleton containing all the useable ship icons.
    /// This module defines the class AllShipIcons which is used to load and
    /// store the game wide list of ship icon images, as a collection of <see cref="ShipIcon"/>s.
    /// </summary>
    public sealed class AllShipIcons
    {
        private static readonly object Padlock = new object();
        private static AllShipIcons instance;
        public List<ShipIcon> IconList = new List<ShipIcon>();
        public Dictionary<string, Dictionary<int, ShipIcon>> Hulls = new Dictionary<string, Dictionary<int, ShipIcon>>();

        /// <summary>
        /// Private constructor to prevent anyone else creating instances of this class.
        /// </summary>
        private AllShipIcons()
        {
        }

        /// <summary>
        /// Provide a mechanism of accessing the single instance of this class that we
        /// will create locally. Creation of the data is thread-safe.
        /// </summary>
        public static AllShipIcons Data
        {
            get
            {
                if (instance == null)
                {
                    lock (Padlock)
                    {
                        if (instance == null)
                        {
                            instance = new AllShipIcons();
                        }
                        Restore();
                    }
                }
                return instance;
            }

            // ----------------------------------------------------------------------------

            set
            {
                instance = value;
            }
        }

        /// <summary>
        /// Load the race images.
        /// </summary>
        /// <returns>true if the race icons were successfully loaded.</returns>
        public static bool Restore()
        {
            if (Data.IconList.Count < 1)
            {
                try
                {
                    using (Config conf = new Config())
                    {
                        // load the icons
                        DirectoryInfo info = new DirectoryInfo(Path.Combine(conf[Global.GraphicsFolderKey], "Ship"));
                        foreach (FileInfo fi in info.GetFiles())
                        {
                            LoadIcon(fi);
                        }
                        info = new DirectoryInfo(Path.Combine(conf[Global.GraphicsFolderKey], "Base"));
                        foreach (FileInfo fi in info.GetFiles())
                        {
                            LoadIcon(fi);
                        }
                    }
                }
                catch
                {
                    Report.Error("RaceIcon: Restore() - Failed to load ship icons.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Load an icon file into the list of all ship icons.
        /// </summary>
        /// <param name="fi">The file to load.</param>
        private static void LoadIcon(FileInfo fi)
        {
            // Headless: no Bitmap is loaded. The icon carries only its Source
            // name; the client presentation layer resolves the image (design A.2).
            ShipIcon icon = new ShipIcon(fi.Name);
            Data.IconList.Add(icon);

            // fi.Name format is <baseHull><iconNumber>.png where the length of <Number> in characters is defined by Global.ShipIconNumberingLength.
            int extensionSeperatorIndex = fi.Name.IndexOf('.'); // position of the '.' in the file name
            // get the base hull of this icon
            string baseHull = fi.Name.Substring(0, extensionSeperatorIndex - Global.ShipIconNumberingLength);
            // Report.Information(baseHull);

            // get the hull number of this icon
            int iconNumber = int.Parse(fi.Name.Substring(extensionSeperatorIndex - Global.ShipIconNumberingLength, Global.ShipIconNumberingLength), System.Globalization.CultureInfo.InvariantCulture);


            // add to the list of hulls
            if (Data.Hulls.ContainsKey(baseHull))
            {
                Data.Hulls[baseHull].Add(iconNumber, icon);
            }
            else
            {
                Dictionary<int, ShipIcon> iconList = new Dictionary<int, ShipIcon>();
                iconList.Add(iconNumber, icon);
                Data.Hulls.Add(baseHull, iconList);
            }
        }

        /// <summary>
        /// Find an icon in the list of all ship icons from the file name.
        /// </summary>
        /// <param name="iconSource">The path and file name of the icon.</param>
        /// <returns>A <see cref="ShipIcon"/> that matches the iconSource or a default icon.</returns>
        public ShipIcon GetIconBySource(string iconSource)
        {
            // fi.Name format is <baseHull><iconNumber>.png where the length of <Number> in characters is defined by Global.ShipIconNumberingLength.
            //
            // Headless port (design Section A.2). This used to end at
            // Data.IconList[0] whenever the hull was not in the database, on the
            // assumption that there is always at least one icon to fall back to.
            // On a server there is not: the graphics folder does not ship in the
            // container, so Hulls and IconList are both empty and indexing IconList
            // threw. FleetIntel(XmlNode) calls this while restoring a fleet and
            // turns any exception into a fatal error, so a saved game containing a
            // fleet could not be loaded at all, and every game has fleets from the
            // first turn.
            //
            // A ShipIcon built from the source is a complete identifier without an
            // image, which is exactly what ShipDesign(XmlNode) already does for the
            // same reason. The client, which has the graphics, still resolves the
            // shared instance from the database on the branch below.
            if (string.IsNullOrEmpty(iconSource))
            {
                return null;
            }

            string[] fileParts = iconSource.Split(System.IO.Path.DirectorySeparatorChar);
            string iconFileName = fileParts[fileParts.Length - 1];
            int extensionSeperatorIndex = iconFileName.IndexOf('.');

            // A name we cannot decompose is still a usable identifier; do not try to
            // index the database with a hull we failed to parse out of it.
            if (extensionSeperatorIndex < Global.ShipIconNumberingLength)
            {
                return new ShipIcon(iconSource);
            }

            string baseHull = iconFileName.Substring(0, extensionSeperatorIndex - Global.ShipIconNumberingLength);
            if (!int.TryParse(
                    iconFileName.Substring(extensionSeperatorIndex - Global.ShipIconNumberingLength, Global.ShipIconNumberingLength),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int iconIndex))
            {
                return new ShipIcon(iconSource);
            }

            if (Data.Hulls.TryGetValue(baseHull, out Dictionary<int, ShipIcon> hullIcons)
                && hullIcons.TryGetValue(iconIndex, out ShipIcon found))
            {
                return found;
            }

            return new ShipIcon(iconSource);
        }
    }
}
