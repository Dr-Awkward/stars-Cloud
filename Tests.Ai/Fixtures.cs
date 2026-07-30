// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using System;
using System.IO;

using NUnit.Framework;

using Nova.Common;
using Nova.Common.Components;
using Nova.Common.DataStructures;
using Nova.Common.Waypoints;

namespace Galaxies.Tests.Ai
{
    /// <summary>
    /// Points the headless component loader at the components.xml copied beside
    /// the test assembly. Without this the component database cannot be located
    /// in headless mode and every design decision the AI makes fails.
    /// </summary>
    [SetUpFixture]
    public class GlobalTestSetup
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            string components = Path.Combine(AppContext.BaseDirectory, "components.xml");
            if (File.Exists(components))
            {
                AllComponents.ComponentFilePathOverride = components;
            }
        }
    }

    /// <summary>
    /// Builds a small but genuinely playable one-empire universe.
    ///
    /// The fixture is built fresh for every test on purpose. GameSettings.Data
    /// and AllComponents are process-wide statics, and sharing a Star or a Race
    /// instance between test methods contaminated later tests during the M0 port.
    /// Fresh instances every time is the cheap fix, and it is why nothing here is
    /// cached in a static field.
    /// </summary>
    public static class Fixtures
    {
        public const int EmpireId = 7;
        public const int TurnYear = 2100;

        /// <summary>A race with tolerances wide enough to find worlds habitable.</summary>
        public static Race BuildRace(string name = "Gestalti")
        {
            Race race = new Race();
            race.Name = name;
            race.PluralName = name;

            race.GravityTolerance = new GravityTolerance();
            race.RadiationTolerance = new RadiationTolerance();
            race.TemperatureTolerance = new TemperatureTolerance();

            race.GravityTolerance.MinimumValue = 15;
            race.GravityTolerance.MaximumValue = 85;
            race.RadiationTolerance.MinimumValue = 15;
            race.RadiationTolerance.MaximumValue = 85;
            race.TemperatureTolerance.MinimumValue = 15;
            race.TemperatureTolerance.MaximumValue = 85;

            race.GrowthRate = 15;
            race.FactoryBuildCost = 10;
            race.ColonistsPerResource = 1000;
            race.FactoryProduction = 10;
            race.OperableFactories = 10;
            race.MineBuildCost = 5;
            race.MineProductionRate = 10;
            race.OperableMines = 10;

            race.ResearchCosts = new TechLevel(1, 1, 1, 1, 1, 1);
            race.Traits = new RacialTraits();
            race.Traits.SetPrimary("HE");

            return race;
        }

        /// <summary>A habitable, populated home world with a production queue.</summary>
        public static Star BuildStar(string name, int x, int y, ushort owner, Race race)
        {
            Star star = new Star();
            star.Name = name;
            star.Position = new NovaPoint { X = x, Y = y };
            star.Owner = owner;
            star.ThisRace = owner == 0 ? null : race;

            star.Gravity = 50;
            star.Radiation = 50;
            star.Temperature = 50;
            star.OriginalGravity = 50;
            star.OriginalRadiation = 50;
            star.OriginalTemperature = 50;

            star.Colonists = owner == 0 ? 0 : 25000;
            star.Factories = owner == 0 ? 0 : 10;
            star.Mines = owner == 0 ? 0 : 10;
            star.Defenses = 0;
            star.ScanRange = 100;

            star.MineralConcentration = new Resources(50, 50, 50, 0);
            star.ResourcesOnHand = new Resources(500, 500, 500, 500);
            star.ManufacturingQueue = new ProductionQueue();

            return star;
        }

        /// <summary>An empire that owns one world and has components unlocked.</summary>
        public static EmpireData BuildEmpire(ushort id = EmpireId, int turnYear = TurnYear)
        {
            Race race = BuildRace();

            EmpireData empire = new EmpireData();
            empire.Id = id;
            empire.Race = race;
            empire.TurnYear = turnYear;
            empire.ResearchBudget = 10;
            empire.ResearchLevels = new TechLevel(1, 1, 1, 1, 1, 1);
            empire.ResearchResources = new TechLevel(0);
            empire.ResearchTopics = new TechLevel(0, 0, 0, 0, 1, 0);

            // Unlock the components this race and tech level can build. This is
            // what lets the AI actually design a scout and a colonizer.
            empire.AvailableComponents = new RaceComponents();
            try
            {
                empire.AvailableComponents.DetermineRaceComponents(race, empire.ResearchLevels);
            }
            catch (Exception)
            {
                // A missing component database is reported by the test that needs
                // it, not by silently producing an empire that cannot build.
            }

            Star home = BuildStar("Alpha", 210, 160, id, race);
            empire.OwnedStars.Add(home);

            // A neighbouring world this empire has seen but does not own, so the
            // scouting and colonizing paths have somewhere to go.
            StarIntel report = new StarIntel();
            report.Name = "Beta";
            report.Position = new NovaPoint { X = 250, Y = 140 };
            report.Owner = 0;
            report.Year = turnYear - 1;
            report.Gravity = 50;
            report.Radiation = 50;
            report.Temperature = 50;
            report.MineralConcentration = new Resources(40, 40, 40, 0);
            empire.StarReports[report.Name] = report;

            // A never-scanned world, which must project with the engine's unset
            // year rather than a plausible-looking zero.
            StarIntel unseen = new StarIntel();
            unseen.Name = "Gamma";
            unseen.Position = new NovaPoint { X = 300, Y = 300 };
            unseen.Owner = 0;
            unseen.Year = Global.Unset;
            unseen.MineralConcentration = new Resources(0, 0, 0, 0);
            empire.StarReports[unseen.Name] = unseen;

            return empire;
        }

        /// <summary>Give the empire a design and a fleet built from it.</summary>
        public static Fleet AddScoutFleet(EmpireData empire, string name = "Scout #1")
        {
            ShipDesign design = new ShipDesign(empire.GetNextDesignKey());
            design.Name = "Scout";
            design.Owner = empire.Id;
            design.Type = ItemType.Ship;

            Component hull;
            if (empire.AvailableComponents != null
                && empire.AvailableComponents.TryGetValue("Scout", out hull))
            {
                design.Blueprint = hull;
                try
                {
                    design.Update();
                }
                catch (Exception)
                {
                    // A design we cannot fully summarize is still usable as a key
                    // for the mapper tests.
                }
            }

            empire.Designs[design.Key] = design;

            Fleet fleet = new Fleet(empire.GetNextFleetKey());
            fleet.Name = name;
            fleet.Owner = empire.Id;
            fleet.Position = new NovaPoint { X = 210, Y = 160 };
            fleet.FuelAvailable = 300;
            fleet.Cargo = new Cargo();

            ShipToken token = new ShipToken(design, 1);
            fleet.Composition.Add(token.Key, token);

            Waypoint start = new Waypoint();
            start.Position = new NovaPoint { X = 210, Y = 160 };
            start.WarpFactor = 0;
            start.Destination = "Alpha";
            start.Task = new NoTask();
            fleet.Waypoints.Add(start);

            empire.OwnedFleets.Add(fleet);
            return fleet;
        }

        /// <summary>Wrap an empire in the intel envelope the contract transcodes.</summary>
        public static Intel BuildIntel(EmpireData empire)
        {
            Intel intel = new Intel();
            intel.EmpireState = empire;

            intel.Messages.Add(new Message(
                empire.Id, "Your race has advanced to tech level 2.", "TechAdvance", null));
            intel.Messages.Add(new Message(
                Global.Everyone, "The galaxy stirs.", "Info", null));

            ScoreRecord score = new ScoreRecord();
            score.EmpireId = empire.Id;
            score.Rank = 1;
            score.Score = 640;
            score.Planets = 1;
            intel.AllScores.Add(score);

            Minefield field = new Minefield();
            field.Owner = 3;
            field.Position = new NovaPoint { X = 240, Y = 150 };
            field.NumberOfMines = 900;
            intel.AllMinefields[field.Key] = field;

            return intel;
        }

        /// <summary>The whole fixture in one call: a playable seat.</summary>
        public static Intel BuildPlayableSeat()
        {
            EmpireData empire = BuildEmpire();
            AddScoutFleet(empire);
            return BuildIntel(empire);
        }
    }
}
