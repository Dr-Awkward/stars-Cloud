// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>
/// The host-editable slice of a game's settings, held on the game document so the
/// lobby can show and edit it without loading the engine's ServerData blob (design
/// Section 9, "Games CRUD and lobby"). These mirror the fields of
/// Nova.Common.GameSettings that a host actually chooses before the map is built:
/// the map dimensions and star counts, and the victory conditions.
///
/// This is deliberately a snapshot, not the engine type. ControlPlane does not
/// reference the engine, and the engine's GameSettings carries file paths and
/// runtime state that have no meaning in Firestore. galaxies-turngen reads these
/// values once, at map initialisation, and builds the real GameSettings from them.
/// After a game starts they are frozen; the API refuses edits outside the lobby.
/// </summary>
[FirestoreData]
public sealed class GameOptions
{
    // ---- Map size (Nova.Common.GameSettings map fields) ----------------------

    [FirestoreProperty] public int MapWidth { get; set; } = 400;
    [FirestoreProperty] public int MapHeight { get; set; } = 400;
    [FirestoreProperty] public int NumberOfStars { get; set; } = 50;
    [FirestoreProperty] public int StarSeparation { get; set; } = 10;
    [FirestoreProperty] public int StarDensity { get; set; } = 40;
    [FirestoreProperty] public int StarUniformity { get; set; } = 60;

    // ---- Victory conditions (the EnabledValue pairs the engine's VictoryCheck
    // ---- evaluates). Enabled plus a target; TargetsToMeet says how many of the
    // ---- enabled ones an empire must hold to win.

    [FirestoreProperty] public VictoryCondition PlanetsOwned { get; set; } = new(true, 60);
    [FirestoreProperty] public VictoryCondition TechLevels { get; set; } = new(false, 22);
    [FirestoreProperty] public VictoryCondition NumberOfFields { get; set; } = new(false, 4);
    [FirestoreProperty] public VictoryCondition TotalScore { get; set; } = new(false, 1000);
    [FirestoreProperty] public VictoryCondition SecondPlaceScore { get; set; } = new(false, 0);
    [FirestoreProperty] public VictoryCondition ProductionCapacity { get; set; } = new(false, 1000);
    [FirestoreProperty] public VictoryCondition CapitalShips { get; set; } = new(false, 100);
    [FirestoreProperty] public VictoryCondition HighestScore { get; set; } = new(false, 100);

    /// <summary>How many enabled conditions an empire must meet to win.</summary>
    [FirestoreProperty] public int TargetsToMeet { get; set; } = 1;

    /// <summary>Years that must pass before any victory is declared.</summary>
    [FirestoreProperty] public int MinimumGameTime { get; set; } = 50;

    public GameOptions Copy() => new()
    {
        MapWidth = MapWidth,
        MapHeight = MapHeight,
        NumberOfStars = NumberOfStars,
        StarSeparation = StarSeparation,
        StarDensity = StarDensity,
        StarUniformity = StarUniformity,
        PlanetsOwned = PlanetsOwned.Copy(),
        TechLevels = TechLevels.Copy(),
        NumberOfFields = NumberOfFields.Copy(),
        TotalScore = TotalScore.Copy(),
        SecondPlaceScore = SecondPlaceScore.Copy(),
        ProductionCapacity = ProductionCapacity.Copy(),
        CapitalShips = CapitalShips.Copy(),
        HighestScore = HighestScore.Copy(),
        TargetsToMeet = TargetsToMeet,
        MinimumGameTime = MinimumGameTime,
    };
}

/// <summary>
/// One victory condition: whether it counts, and the number to reach. The control
/// plane's stand-in for the engine's EnabledValue, which is not a Firestore type.
/// </summary>
[FirestoreData]
public sealed class VictoryCondition
{
    public VictoryCondition()
    {
    }

    public VictoryCondition(bool enabled, int value)
    {
        Enabled = enabled;
        Value = value;
    }

    [FirestoreProperty] public bool Enabled { get; set; }
    [FirestoreProperty] public int Value { get; set; }

    public VictoryCondition Copy() => new(Enabled, Value);
}
