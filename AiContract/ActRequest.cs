// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.Text.Json.Serialization;

/// <summary>
/// The host to participant request for one seat, one turn
/// (AI-PARTICIPANTS.md Section F.1.2).
///
/// One act request per seat per turn. The participant touches no storage, no
/// locks, and no object graph: it receives its view in this body and returns
/// orders in the response body. That is the whole reason N AI seats can run as
/// N concurrent invocations, where the desktop AiRunner could only ever run one
/// at a time behind a shared-folder lock file.
/// </summary>
public sealed class ActRequest
{
    /// <summary>The contract version this request speaks. Currently "1.0".</summary>
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = ContractVersions.Current;

    /// <summary>Correlation id, echoed back in the response.</summary>
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("issued_unix_ms")]
    public long IssuedUnixMs { get; set; }

    /// <summary>
    /// When held orders will be used instead. A participant that cannot answer
    /// by this time should return nothing rather than answer late; the turn
    /// generates on schedule either way.
    /// </summary>
    [JsonPropertyName("deadline_unix_ms")]
    public long DeadlineUnixMs { get; set; }

    [JsonPropertyName("game")]
    public GameContext Game { get; set; } = new();

    [JsonPropertyName("seat")]
    public SeatContext Seat { get; set; } = new();

    /// <summary>
    /// The language-neutral fog-of-war projection of this seat's own state. This
    /// is what community and LLM participants read.
    /// </summary>
    [JsonPropertyName("empire_view")]
    public EmpireView EmpireView { get; set; } = new();

    /// <summary>
    /// Optional: the seat's own intel as the engine's native XML, gzipped and
    /// base64 encoded, in the same envelope shape the player-facing API already
    /// uses for intel and orders (design decision 8, "XML inside a JSON
    /// envelope").
    ///
    /// Why this exists. The empire_view above is a projection: it is complete
    /// enough to play well and it is the contract every participant can rely on,
    /// but it is not a lossless serialization of a roughly 24,000 line domain
    /// model, and pretending otherwise would quietly degrade the built-in AI and
    /// make golden replay meaningless. A first-party C# participant shares the
    /// engine assembly, so it reconstructs a real Intel from this field and
    /// plays against the authoritative object graph. Community and LLM
    /// participants ignore this field entirely and read empire_view.
    ///
    /// This is exactly the "reusing the EmpireData(XmlNode) / ToXml pair" path
    /// the spec sanctions (galaxies-ai spec Section 2.1). It is null when the
    /// dispatch is for a participant that did not declare it speaks native
    /// intel.
    /// </summary>
    [JsonPropertyName("intel_native")]
    public NativePayload? IntelNative { get; set; }
}

/// <summary>Per-game context for the dispatch.</summary>
public sealed class GameContext
{
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = "";

    /// <summary>Maps to ServerData.TurnYear.</summary>
    [JsonPropertyName("turn_year")]
    public int TurnYear { get; set; }

    /// <summary>
    /// The per-seat, per-turn deterministic seed, hash(MasterSeed, turnYear,
    /// empireId).
    ///
    /// Named seat_seed, not seed, on purpose: ServerData.MasterSeed is the one
    /// persisted per-game seed and this value is derived from it. Calling this
    /// "seed" invited the reading that a participant holds a second master value
    /// (specs README reconciliation item 3). A participant declaring
    /// determinism "seeded" must use only this for randomness.
    /// </summary>
    [JsonPropertyName("seat_seed")]
    public string SeatSeed { get; set; } = "";

    [JsonPropertyName("settings")]
    public GameSettingsView Settings { get; set; } = new();
}

/// <summary>A projection of the game's settings that affect play decisions.</summary>
public sealed class GameSettingsView
{
    [JsonPropertyName("accelerated_start")]
    public bool AcceleratedStart { get; set; }

    [JsonPropertyName("map")]
    public MapView Map { get; set; } = new();

    [JsonPropertyName("victory")]
    public VictoryView Victory { get; set; } = new();
}

public sealed class MapView
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("number_of_stars")]
    public int NumberOfStars { get; set; }
}

/// <summary>
/// The victory conditions in play. A participant that knows the game ends on
/// planets owned plays differently from one racing tech levels, so this is not
/// decoration.
/// </summary>
public sealed class VictoryView
{
    [JsonPropertyName("planets_owned")]
    public EnabledValueView PlanetsOwned { get; set; } = new();

    [JsonPropertyName("tech_levels")]
    public EnabledValueView TechLevels { get; set; } = new();

    /// <summary>Number of tech fields at the TechLevels threshold.</summary>
    [JsonPropertyName("number_of_fields")]
    public EnabledValueView NumberOfFields { get; set; } = new();

    [JsonPropertyName("total_score")]
    public EnabledValueView TotalScore { get; set; } = new();

    /// <summary>Win by exceeding second place by this factor.</summary>
    [JsonPropertyName("second_place_score")]
    public EnabledValueView SecondPlaceScore { get; set; } = new();

    [JsonPropertyName("production_capacity")]
    public EnabledValueView ProductionCapacity { get; set; } = new();

    [JsonPropertyName("capital_ships")]
    public EnabledValueView CapitalShips { get; set; } = new();

    [JsonPropertyName("highest_score")]
    public EnabledValueView HighestScore { get; set; } = new();

    /// <summary>How many of the enabled targets must be met to win.</summary>
    [JsonPropertyName("targets_to_meet")]
    public int TargetsToMeet { get; set; }

    /// <summary>No victory is declared before this many years have passed.</summary>
    [JsonPropertyName("minimum_game_time")]
    public int MinimumGameTime { get; set; }
}

/// <summary>Mirrors the engine's EnabledValue: a toggle plus a threshold.</summary>
public sealed class EnabledValueView
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

/// <summary>Which seat this dispatch is for.</summary>
public sealed class SeatContext
{
    /// <summary>EmpireData.Id. Zero is reserved, so a real seat is 1 to 127.</summary>
    [JsonPropertyName("empire_id")]
    public int EmpireId { get; set; }

    [JsonPropertyName("race_name")]
    public string RaceName { get; set; } = "";

    /// <summary>One of the tiers the participant's manifest declares.</summary>
    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = "normal";
}

/// <summary>
/// An engine-native payload carried inside the JSON envelope, matching the
/// shape the player-facing API already uses for intel and orders.
/// </summary>
public sealed class NativePayload
{
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "application/vnd.nova.intel+xml";

    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "gzip+base64";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";
}

/// <summary>The contract versions this build speaks.</summary>
public static class ContractVersions
{
    public const string Current = "1.0";

    public static readonly string[] Supported = { "1.0" };

    public static bool IsSupported(string? version)
        => version is not null && Array.IndexOf(Supported, version) >= 0;
}
