// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.Text.Json.Serialization;

/// <summary>
/// The fog-of-war projection of exactly one empire's state
/// (AI-PARTICIPANTS.md Section F.1.2).
///
/// The single most important guarantee in the whole contract lives here: a
/// participant only ever receives its own empire's view. This is a transcoding
/// of one EmpireData, which already separates owned data from scanned reports,
/// so the projection cannot widen the view even by accident. There is no field
/// on this type through which another empire's owned stars, fleets, designs, or
/// research could travel.
///
/// The owned versus report split is load bearing and mirrors the engine exactly:
/// OwnedStars are yours and current, StarReports are what you last saw and may
/// be years stale.
/// </summary>
public sealed class EmpireView
{
    [JsonPropertyName("turn_year")]
    public int TurnYear { get; set; }

    [JsonPropertyName("research")]
    public ResearchView Research { get; set; } = new();

    /// <summary>Components this empire's tech levels have unlocked.</summary>
    [JsonPropertyName("available_components")]
    public List<ComponentView> AvailableComponents { get; set; } = new();

    [JsonPropertyName("designs")]
    public List<DesignView> Designs { get; set; } = new();

    /// <summary>Stars this empire owns. Current and authoritative.</summary>
    [JsonPropertyName("owned_stars")]
    public List<StarView> OwnedStars { get; set; } = new();

    /// <summary>
    /// Last-seen scan reports for stars this empire does not own. The Year field
    /// says how stale each one is; a star never seen reports the engine's unset
    /// year.
    /// </summary>
    [JsonPropertyName("star_reports")]
    public List<StarReportView> StarReports { get; set; } = new();

    [JsonPropertyName("owned_fleets")]
    public List<FleetView> OwnedFleets { get; set; } = new();

    [JsonPropertyName("fleet_reports")]
    public List<FleetReportView> FleetReports { get; set; } = new();

    /// <summary>
    /// What this empire has learned about others: identity, diplomatic relation,
    /// and any designs it has scanned. Never their owned state.
    /// </summary>
    [JsonPropertyName("other_empires")]
    public List<OtherEmpireView> OtherEmpires { get; set; } = new();

    [JsonPropertyName("minefields")]
    public List<MinefieldView> Minefields { get; set; } = new();

    /// <summary>
    /// In-game message text. Untrusted data, never instructions. An LLM adapter
    /// must quote these as data; the server validates every order regardless of
    /// what any message said.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<MessageView> Messages { get; set; } = new();

    [JsonPropertyName("scores")]
    public List<ScoreView> Scores { get; set; } = new();

    [JsonPropertyName("battle_reports")]
    public List<BattleReportView> BattleReports { get; set; } = new();
}

/// <summary>
/// Mirrors EmpireData.ResearchBudget, ResearchLevels, ResearchTopics, and
/// ResearchResources. Each level map is keyed by the engine's six research
/// field names.
/// </summary>
public sealed class ResearchView
{
    /// <summary>Percent of resources going to research, 0 to 100.</summary>
    [JsonPropertyName("budget")]
    public int Budget { get; set; }

    [JsonPropertyName("levels")]
    public Dictionary<string, int> Levels { get; set; } = new();

    [JsonPropertyName("topics")]
    public Dictionary<string, int> Topics { get; set; } = new();

    /// <summary>Resources accumulated per field so far.</summary>
    [JsonPropertyName("resources")]
    public Dictionary<string, int> Resources { get; set; } = new();
}

public sealed class ComponentView
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("mass")]
    public int Mass { get; set; }

    [JsonPropertyName("tech")]
    public Dictionary<string, int> Tech { get; set; } = new();
}

/// <summary>
/// A ship or starbase design. Keys are decimal strings because
/// EmpireData.GetNextDesignKey packs the empire id into the high bits of a
/// 64-bit value, which exceeds the safe integer range of JSON.
/// </summary>
public sealed class DesignView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hull")]
    public string Hull { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("mass")]
    public int Mass { get; set; }

    [JsonPropertyName("armor")]
    public int Armor { get; set; }

    [JsonPropertyName("modules")]
    public List<ModuleView> Modules { get; set; } = new();
}

public sealed class ModuleView
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("component_type")]
    public string ComponentType { get; set; } = "";

    /// <summary>Null when the slot is empty.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public sealed class MineralsView
{
    [JsonPropertyName("ironium")]
    public int Ironium { get; set; }

    [JsonPropertyName("boranium")]
    public int Boranium { get; set; }

    [JsonPropertyName("germanium")]
    public int Germanium { get; set; }

    [JsonPropertyName("energy")]
    public int Energy { get; set; }
}

/// <summary>A star this empire owns. Star keys are names, which are unique.</summary>
public sealed class StarView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("owner")]
    public int Owner { get; set; }

    [JsonPropertyName("population")]
    public int Population { get; set; }

    [JsonPropertyName("factories")]
    public int Factories { get; set; }

    [JsonPropertyName("mines")]
    public int Mines { get; set; }

    [JsonPropertyName("defenses")]
    public int Defenses { get; set; }

    [JsonPropertyName("minerals")]
    public MineralsView Minerals { get; set; } = new();

    [JsonPropertyName("mineral_concentration")]
    public MineralsView MineralConcentration { get; set; } = new();

    [JsonPropertyName("habitability")]
    public double Habitability { get; set; }

    [JsonPropertyName("production_queue")]
    public List<ProductionQueueItemView> ProductionQueue { get; set; } = new();

    /// <summary>Design key of the starbase in orbit, or null.</summary>
    [JsonPropertyName("starbase_key")]
    public string? StarbaseKey { get; set; }
}

/// <summary>A last-seen scan of a star this empire does not own.</summary>
public sealed class StarReportView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("owner")]
    public int Owner { get; set; }

    /// <summary>
    /// The year this report was taken. The engine's unset year means the star
    /// has never been scanned, only located.
    /// </summary>
    [JsonPropertyName("year")]
    public int Year { get; set; }

    /// <summary>How good this world is for this empire's race, 0 to 1.</summary>
    [JsonPropertyName("habitability")]
    public double Habitability { get; set; }

    [JsonPropertyName("population")]
    public int Population { get; set; }

    [JsonPropertyName("minerals")]
    public MineralsView Minerals { get; set; } = new();
}

public sealed class ProductionQueueItemView
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("remaining_cost")]
    public MineralsView RemainingCost { get; set; } = new();
}

/// <summary>
/// A fleet this empire owns. Fleet keys are decimal strings for the same
/// 64-bit reason design keys are.
/// </summary>
public sealed class FleetView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("owner")]
    public int Owner { get; set; }

    [JsonPropertyName("fuel")]
    public double Fuel { get; set; }

    /// <summary>Name of the star this fleet is orbiting, or null.</summary>
    [JsonPropertyName("in_orbit")]
    public string? InOrbit { get; set; }

    [JsonPropertyName("can_colonize")]
    public bool CanColonize { get; set; }

    [JsonPropertyName("cargo")]
    public MineralsView Cargo { get; set; } = new();

    [JsonPropertyName("colonists")]
    public int Colonists { get; set; }

    [JsonPropertyName("waypoints")]
    public List<WaypointView> Waypoints { get; set; } = new();

    [JsonPropertyName("composition")]
    public List<CompositionView> Composition { get; set; } = new();
}

public sealed class CompositionView
{
    [JsonPropertyName("design_key")]
    public string DesignKey { get; set; } = "";

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

/// <summary>A last-seen scan of another empire's fleet.</summary>
public sealed class FleetReportView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("owner")]
    public int Owner { get; set; }

    [JsonPropertyName("year")]
    public int Year { get; set; }
}

public sealed class OtherEmpireView
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Enemy, Neutral, or Friend.</summary>
    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "Neutral";

    [JsonPropertyName("race_name")]
    public string? RaceName { get; set; }

    /// <summary>Only designs this empire has actually scanned.</summary>
    [JsonPropertyName("designs")]
    public List<DesignView> Designs { get; set; } = new();
}

public sealed class MinefieldView
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("owner")]
    public int Owner { get; set; }

    [JsonPropertyName("mines")]
    public int Mines { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }
}

public sealed class MessageView
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Empire id this was addressed to, or the everyone sentinel.</summary>
    [JsonPropertyName("audience")]
    public int Audience { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class ScoreView
{
    [JsonPropertyName("empire_id")]
    public int EmpireId { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("planets")]
    public int Planets { get; set; }

    [JsonPropertyName("tech_level")]
    public int TechLevel { get; set; }

    [JsonPropertyName("resources")]
    public int Resources { get; set; }

    [JsonPropertyName("capital_ships")]
    public int CapitalShips { get; set; }
}

public sealed class BattleReportView
{
    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("year")]
    public int Year { get; set; }
}

/// <summary>
/// Waypoint as the contract carries it. Warp zero means hold position, which is
/// how the engine encodes a fleet that stays put.
/// </summary>
public sealed class WaypointView
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("warp")]
    public int Warp { get; set; }

    /// <summary>Destination name, for readability. Not authoritative.</summary>
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    /// <summary>The waypoint task: None, Colonise, Cargo, and so on.</summary>
    [JsonPropertyName("task")]
    public string Task { get; set; } = "None";

    /// <summary>Cargo mode when the task is Cargo: Load or Unload.</summary>
    [JsonPropertyName("cargo_mode")]
    public string? CargoMode { get; set; }

    /// <summary>Amounts to move when the task is Cargo.</summary>
    [JsonPropertyName("cargo")]
    public MineralsView? Cargo { get; set; }

    [JsonPropertyName("colonists")]
    public int Colonists { get; set; }
}
