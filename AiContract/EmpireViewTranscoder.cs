// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.Globalization;

using Nova.Common;
using Nova.Common.Components;
using Nova.Common.Waypoints;

/// <summary>
/// Projects one seat's own intel into the language-neutral empire_view
/// (galaxies-ai spec Section 4.2).
///
/// This transcodes, it does not re-project. The turn generator already ran its
/// scan step and wrote one fog-of-war-correct intel per empire, so everything
/// here reads from a single EmpireData that by construction contains only that
/// empire's owned data plus what it has scanned. There is no code path in this
/// file that can widen the view, because there is nothing wider in scope: it is
/// handed one Intel and it never sees another.
///
/// Where the projection is deliberately lossy, it is lossy in the safe
/// direction (dropping detail a participant does not need), never in the
/// direction of exposing more. The first-party AI does not depend on this
/// projection's completeness; it reads the native intel payload instead, which
/// is why a gap here degrades a community participant's information rather than
/// silently weakening the built-in opponent.
/// </summary>
public static class EmpireViewTranscoder
{
    /// <summary>
    /// Build the full act request for one seat-turn.
    /// </summary>
    /// <param name="intel">The seat's own intel, as the turn generator wrote it.</param>
    /// <param name="gameId">The game this dispatch belongs to.</param>
    /// <param name="turnYear">The turn being played.</param>
    /// <param name="seatSeed">Formatted per-seat deterministic seed.</param>
    /// <param name="difficulty">The tier this seat was pinned at.</param>
    /// <param name="settings">Game settings, or null to send defaults.</param>
    /// <param name="deadline">When held orders take over.</param>
    /// <param name="includeNativeIntel">
    /// True to also carry the engine-native intel XML, for first-party C#
    /// participants that reconstruct a real Intel from it.
    /// </param>
    public static ActRequest BuildRequest(
        Intel intel,
        string gameId,
        int turnYear,
        string seatSeed,
        string difficulty,
        GameSettings? settings,
        DateTimeOffset deadline,
        bool includeNativeIntel)
    {
        ArgumentNullException.ThrowIfNull(intel);

        EmpireData empire = intel.EmpireState
            ?? throw new ArgumentException("Intel carries no empire state.", nameof(intel));

        ActRequest request = new()
        {
            ContractVersion = ContractVersions.Current,
            RequestId = Guid.NewGuid().ToString("N"),
            IssuedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DeadlineUnixMs = deadline.ToUnixTimeMilliseconds(),
            Game = new GameContext
            {
                GameId = gameId,
                TurnYear = turnYear,
                SeatSeed = seatSeed,
                Settings = Settings(settings),
            },
            Seat = new SeatContext
            {
                EmpireId = empire.Id,
                RaceName = empire.Race?.Name ?? string.Empty,
                Difficulty = difficulty,
            },
            EmpireView = Project(intel),
        };

        if (includeNativeIntel)
        {
            request.IntelNative = Envelope.PackIntel(Envelope.WriteIntel(intel));
        }

        return request;
    }

    /// <summary>Project one intel into the contract's empire_view.</summary>
    public static EmpireView Project(Intel intel)
    {
        ArgumentNullException.ThrowIfNull(intel);

        EmpireData empire = intel.EmpireState
            ?? throw new ArgumentException("Intel carries no empire state.", nameof(intel));

        EmpireView view = new()
        {
            TurnYear = empire.TurnYear,
            Research = ProjectResearch(empire),
        };

        if (empire.AvailableComponents is not null)
        {
            foreach (Component component in empire.AvailableComponents.Values)
            {
                view.AvailableComponents.Add(ProjectComponent(component));
            }
        }

        foreach (ShipDesign design in empire.Designs.Values)
        {
            view.Designs.Add(ProjectDesign(design));
        }

        foreach (Star star in empire.OwnedStars.Values)
        {
            view.OwnedStars.Add(ProjectStar(star, empire.Race));
        }

        foreach (StarIntel report in empire.StarReports.Values)
        {
            // A star this empire owns is already in owned_stars with full detail;
            // repeating it as a stale report would only invite a participant to
            // act on the older copy.
            if (empire.OwnedStars.ContainsKey(report.Name))
            {
                continue;
            }

            view.StarReports.Add(ProjectStarReport(report, empire.Race));
        }

        foreach (Fleet fleet in empire.OwnedFleets.Values)
        {
            view.OwnedFleets.Add(ProjectFleet(fleet));
        }

        foreach (FleetIntel report in empire.FleetReports.Values)
        {
            if (empire.OwnedFleets.ContainsKey(report.Key))
            {
                continue;
            }

            view.FleetReports.Add(ProjectFleetReport(report));
        }

        foreach (EmpireIntel other in empire.EmpireReports.Values)
        {
            view.OtherEmpires.Add(ProjectOtherEmpire(other));
        }

        if (intel.AllMinefields is not null)
        {
            foreach (Minefield minefield in intel.AllMinefields.Values)
            {
                view.Minefields.Add(ProjectMinefield(minefield));
            }
        }

        if (intel.Messages is not null)
        {
            foreach (Message message in intel.Messages)
            {
                view.Messages.Add(new MessageView
                {
                    // Message.Event is an arbitrary object used by the desktop GUI
                    // for its goto button. It is not part of the contract and is
                    // deliberately dropped rather than guessed at.
                    Type = message.Type ?? string.Empty,
                    Audience = message.Audience,
                    Text = message.Text ?? string.Empty,
                });
            }
        }

        if (intel.AllScores is not null)
        {
            foreach (ScoreRecord score in intel.AllScores)
            {
                view.Scores.Add(new ScoreView
                {
                    EmpireId = score.EmpireId,
                    Rank = score.Rank,
                    Score = score.Score,
                    Planets = score.Planets,
                    TechLevel = score.TechLevel,
                    Resources = score.Resources,
                    CapitalShips = score.CapitalShips,
                });
            }
        }

        foreach (Nova.Common.DataStructures.BattleReport battle in empire.BattleReports)
        {
            view.BattleReports.Add(new BattleReportView
            {
                Location = battle.Location ?? string.Empty,
                Year = battle.Year,
            });
        }

        return view;
    }

    private static ResearchView ProjectResearch(EmpireData empire)
    {
        ResearchView research = new() { Budget = empire.ResearchBudget };

        // TechLevel exposes no per-field properties, only an indexer over the
        // ResearchField enum, so this loop is the sanctioned way to read all six.
        for (TechLevel.ResearchField field = TechLevel.FirstField;
             field <= TechLevel.LastField;
             field++)
        {
            string name = field.ToString();
            if (empire.ResearchLevels is not null)
            {
                research.Levels[name] = empire.ResearchLevels[field];
            }

            if (empire.ResearchTopics is not null)
            {
                research.Topics[name] = empire.ResearchTopics[field];
            }

            if (empire.ResearchResources is not null)
            {
                research.Resources[name] = empire.ResearchResources[field];
            }
        }

        return research;
    }

    private static ComponentView ProjectComponent(Component component)
    {
        ComponentView view = new()
        {
            Name = component.Name ?? string.Empty,
            Type = component.Type.ToString(),
            Mass = component.Mass,
        };

        if (component.RequiredTech is not null)
        {
            for (TechLevel.ResearchField field = TechLevel.FirstField;
                 field <= TechLevel.LastField;
                 field++)
            {
                view.Tech[field.ToString()] = component.RequiredTech[field];
            }
        }

        return view;
    }

    private static DesignView ProjectDesign(ShipDesign design)
    {
        DesignView view = new()
        {
            Key = Key(design.Key),
            Name = design.Name ?? string.Empty,
            Type = design.Type.ToString(),
            Hull = design.Blueprint?.Name ?? string.Empty,
        };

        // Mass, Armor, and the module list all read through Blueprint and the
        // hull property bag, which throw on a partially built design. A design
        // we cannot summarize is still worth listing by name and key, so the
        // detail is best effort.
        try
        {
            view.Mass = design.Mass;
            view.Armor = design.Armor;
        }
        catch (Exception)
        {
            // Leave the numeric summary at zero.
        }

        try
        {
            Hull? hull = design.Hull;
            if (hull?.Modules is not null)
            {
                int slot = 0;
                foreach (HullModule module in hull.Modules)
                {
                    view.Modules.Add(new ModuleView
                    {
                        Slot = slot++,
                        ComponentType = module.ComponentType ?? string.Empty,
                        Component = module.AllocatedComponent?.Name,
                        Count = module.ComponentCount,
                    });
                }
            }
        }
        catch (Exception)
        {
            // A design whose hull is not resolvable contributes no modules.
        }

        return view;
    }

    private static StarView ProjectStar(Star star, Race? race)
    {
        StarView view = new()
        {
            // Star.Key is a string that shadows Item.Key and returns Name. Using
            // Name directly avoids depending on which static type is in hand.
            Key = star.Name ?? string.Empty,
            Name = star.Name ?? string.Empty,
            X = star.Position.X,
            Y = star.Position.Y,
            Owner = star.Owner,
            Population = star.Colonists,
            Factories = star.Factories,
            Mines = star.Mines,
            Defenses = star.Defenses,
            Minerals = Minerals(star.ResourcesOnHand),
            MineralConcentration = Minerals(star.MineralConcentration),
            StarbaseKey = star.Starbase is null ? null : Key(star.Starbase.Key),
        };

        if (race is not null)
        {
            try
            {
                view.Habitability = race.HabValue(star);
            }
            catch (Exception)
            {
                view.Habitability = 0;
            }
        }

        if (star.ManufacturingQueue?.Queue is not null)
        {
            int index = 0;
            foreach (ProductionOrder order in star.ManufacturingQueue.Queue)
            {
                view.ProductionQueue.Add(new ProductionQueueItemView
                {
                    Unit = order.Name ?? string.Empty,
                    Index = index++,
                    Quantity = order.Quantity,
                    RemainingCost = Minerals(SafeNeededResources(order)),
                });
            }
        }

        return view;
    }

    private static StarReportView ProjectStarReport(StarIntel report, Race? race)
    {
        StarReportView view = new()
        {
            Key = report.Name ?? string.Empty,
            Name = report.Name ?? string.Empty,
            X = report.Position.X,
            Y = report.Position.Y,
            Owner = report.Owner,
            Year = report.Year,
            Population = report.Colonists,
            Minerals = Minerals(report.MineralConcentration),
        };

        if (race is not null && report.Year != Global.Unset)
        {
            try
            {
                view.Habitability = race.HabitalValue(report);
            }
            catch (Exception)
            {
                view.Habitability = 0;
            }
        }

        return view;
    }

    private static FleetView ProjectFleet(Fleet fleet)
    {
        FleetView view = new()
        {
            Key = Key(fleet.Key),
            Name = fleet.Name ?? string.Empty,
            X = fleet.Position.X,
            Y = fleet.Position.Y,
            Owner = fleet.Owner,
            Fuel = fleet.FuelAvailable,
            // Fleet.InOrbit is the Mappable being orbited, not a flag.
            InOrbit = fleet.InOrbit?.Name,
        };

        try
        {
            view.CanColonize = fleet.CanColonize;
        }
        catch (Exception)
        {
            view.CanColonize = false;
        }

        if (fleet.Cargo is not null)
        {
            view.Cargo = new MineralsView
            {
                Ironium = fleet.Cargo.Ironium,
                Boranium = fleet.Cargo.Boranium,
                Germanium = fleet.Cargo.Germanium,
            };
            view.Colonists = fleet.Cargo.ColonistsInKilotons;
        }

        foreach (Waypoint waypoint in fleet.Waypoints)
        {
            view.Waypoints.Add(ProjectWaypoint(waypoint));
        }

        try
        {
            foreach (KeyValuePair<long, ShipToken> token in fleet.Composition)
            {
                view.Composition.Add(new CompositionView
                {
                    DesignKey = Key(token.Key),
                    Quantity = token.Value?.Quantity ?? 0,
                });
            }
        }
        catch (Exception)
        {
            // A fleet with an unresolvable composition still has a position and
            // waypoints worth acting on.
        }

        return view;
    }

    private static FleetReportView ProjectFleetReport(FleetIntel report) => new()
    {
        Key = Key(report.Key),
        Name = report.Name ?? string.Empty,
        X = report.Position.X,
        Y = report.Position.Y,
        Owner = report.Owner,
        Year = report.Year,
    };

    private static OtherEmpireView ProjectOtherEmpire(EmpireIntel other)
    {
        OtherEmpireView view = new()
        {
            Id = other.Id,
            Relation = other.Relation.ToString(),
            RaceName = other.RaceName,
        };

        foreach (ShipDesign design in other.Designs.Values)
        {
            view.Designs.Add(ProjectDesign(design));
        }

        return view;
    }

    private static MinefieldView ProjectMinefield(Minefield minefield) => new()
    {
        Key = Key(minefield.Key),
        X = minefield.Position.X,
        Y = minefield.Position.Y,
        Owner = minefield.Owner,
        Mines = minefield.NumberOfMines,
        Radius = minefield.Radius,
    };

    /// <summary>
    /// Project a waypoint, including enough task detail that a participant can
    /// see what a fleet is already committed to doing.
    /// </summary>
    public static WaypointView ProjectWaypoint(Waypoint waypoint)
    {
        WaypointView view = new()
        {
            X = waypoint.Position?.X ?? 0,
            Y = waypoint.Position?.Y ?? 0,
            Warp = waypoint.WarpFactor,
            Destination = waypoint.Destination,
            Task = waypoint.Task?.Name ?? WaypointTasks.None,
        };

        if (waypoint.Task is CargoTask cargo)
        {
            view.CargoMode = cargo.Mode.ToString();
            if (cargo.Amount is not null)
            {
                view.Cargo = new MineralsView
                {
                    Ironium = cargo.Amount.Ironium,
                    Boranium = cargo.Amount.Boranium,
                    Germanium = cargo.Amount.Germanium,
                };
                view.Colonists = cargo.Amount.ColonistsInKilotons;
            }
        }

        return view;
    }

    private static GameSettingsView Settings(GameSettings? settings)
    {
        if (settings is null)
        {
            return new GameSettingsView();
        }

        return new GameSettingsView
        {
            AcceleratedStart = settings.AcceleratedStart,
            Map = new MapView
            {
                Width = settings.MapWidth,
                Height = settings.MapHeight,
                NumberOfStars = settings.NumberOfStars,
            },
            Victory = new VictoryView
            {
                PlanetsOwned = Enabled(settings.PlanetsOwned),
                TechLevels = Enabled(settings.TechLevels),
                NumberOfFields = Enabled(settings.NumberOfFields),
                TotalScore = Enabled(settings.TotalScore),
                SecondPlaceScore = Enabled(settings.SecondPlaceScore),
                ProductionCapacity = Enabled(settings.ProductionCapacity),
                CapitalShips = Enabled(settings.CapitalShips),
                HighestScore = Enabled(settings.HighestScore),
                TargetsToMeet = settings.TargetsToMeet,
                MinimumGameTime = settings.MinimumGameTime,
            },
        };
    }

    private static EnabledValueView Enabled(EnabledValue? value) => value is null
        ? new EnabledValueView()
        : new EnabledValueView { Enabled = value.IsChecked, Value = value.NumericValue };

    private static MineralsView Minerals(Resources? resources) => resources is null
        ? new MineralsView()
        : new MineralsView
        {
            Ironium = resources.Ironium,
            Boranium = resources.Boranium,
            Germanium = resources.Germanium,
            Energy = resources.Energy,
        };

    private static Resources? SafeNeededResources(ProductionOrder order)
    {
        try
        {
            return order.NeededResources();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Format a 64-bit key as a decimal string. Fleet and design keys pack the
    /// empire id into their high bits and exceed the range JSON numbers can
    /// carry safely, so they travel as strings.
    /// </summary>
    public static string Key(long key) => key.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parse a key back from its decimal string form.</summary>
    public static bool TryParseKey(string? key, out long value)
        => long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// The waypoint task names the engine uses on the wire. These are the exact
/// strings IWaypointTask.Name returns, including the British spelling of
/// Colonise and the space in Lay Mines, because the engine's own waypoint
/// loader dispatches on them.
/// </summary>
public static class WaypointTasks
{
    public const string None = "None";
    public const string Colonise = "Colonise";
    public const string LoadCargo = "Load Cargo";
    public const string UnloadCargo = "Unload Cargo";
    public const string Invade = "Invade";
    public const string LayMines = "Lay Mines";
    public const string Scrap = "Scrap";
    public const string SplitFleet = "Split Fleet";
}
