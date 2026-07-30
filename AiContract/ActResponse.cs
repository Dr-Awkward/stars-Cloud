// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.Text.Json.Serialization;

/// <summary>
/// The participant to host response (AI-PARTICIPANTS.md Section F.1.3).
///
/// Orders map one to one onto the five existing ICommand implementations, and
/// the Type token is the same one the engine's order reader dispatches on today,
/// so the host adapter is a thin translator rather than a second command model.
/// </summary>
public sealed class ActResponse
{
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = ContractVersions.Current;

    /// <summary>Echo of the request id.</summary>
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = "";

    /// <summary>
    /// Echo of the seat. Checked for equality only. The host stamps the real
    /// empire id from its own dispatch record, never from this field, which is
    /// what makes it impossible for a participant to act on a seat it was not
    /// handed (mirroring the engine's own empire-id guard).
    /// </summary>
    [JsonPropertyName("empire_id")]
    public int EmpireId { get; set; }

    /// <summary>Must equal the request's turn year.</summary>
    [JsonPropertyName("turn_year")]
    public int TurnYear { get; set; }

    [JsonPropertyName("orders")]
    public List<OrderDto> Orders { get; set; } = new();

    /// <summary>
    /// Optional and never affects the turn. It feeds logs and the test harness.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public DiagnosticsDto? Diagnostics { get; set; }
}

/// <summary>
/// One order. This is a union across the five command types, discriminated by
/// Type; fields not relevant to a given type are null and ignored.
///
/// Adding a sixth order type still costs one arm in the engine's command
/// registry plus one arm in the order mapper. The contract localizes that edit
/// to two places; it does not remove it, and claiming otherwise would be
/// dishonest.
/// </summary>
public sealed class OrderDto
{
    /// <summary>Research, Waypoint, Design, Production, or RenameFleet.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Add, Edit, or Delete. Defaults to Add where a mode applies.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    // Research
    /// <summary>Percent of resources to research, 0 to 100.</summary>
    [JsonPropertyName("budget")]
    public int? Budget { get; set; }

    /// <summary>Research priorities keyed by field name.</summary>
    [JsonPropertyName("topics")]
    public Dictionary<string, int>? Topics { get; set; }

    // Waypoint and RenameFleet
    /// <summary>Decimal string, because fleet keys are 64-bit.</summary>
    [JsonPropertyName("fleet_key")]
    public string? FleetKey { get; set; }

    /// <summary>Waypoint index, or production queue index.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("waypoint")]
    public WaypointView? Waypoint { get; set; }

    [JsonPropertyName("new_name")]
    public string? NewName { get; set; }

    // Production
    /// <summary>Star name, which is the star key.</summary>
    [JsonPropertyName("star_key")]
    public string? StarKey { get; set; }

    [JsonPropertyName("order")]
    public ProductionOrderDto? Order { get; set; }

    // Design
    [JsonPropertyName("design")]
    public DesignView? Design { get; set; }
}

public sealed class ProductionOrderDto
{
    /// <summary>
    /// What to build: Factory, Mine, Defenses, or a ship design name.
    /// </summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;

    /// <summary>Design key when the unit is a ship. Decimal string.</summary>
    [JsonPropertyName("design_key")]
    public string? DesignKey { get; set; }
}

public sealed class DiagnosticsDto
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("tokens_used")]
    public int? TokensUsed { get; set; }

    [JsonPropertyName("seed_used")]
    public string? SeedUsed { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long? ElapsedMs { get; set; }
}

/// <summary>The order type tokens the contract recognizes.</summary>
public static class OrderTypes
{
    public const string Research = "Research";
    public const string Waypoint = "Waypoint";
    public const string Design = "Design";
    public const string Production = "Production";
    public const string RenameFleet = "RenameFleet";
}
