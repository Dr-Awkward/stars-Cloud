// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

namespace Galaxies.Api.Wire;

// The wire payload (design "Resolved key decisions" + Section E.3): the existing
// engine XML travels inside a small JSON envelope for the desktop client (zero
// domain-model drift). A native JSON projection for AI and the web client is a
// later convergence. Here, orders and intel carry their XML as a string field.

public sealed record GoogleLoginRequest(string IdToken);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);

public sealed record MeResponse(string GoogleSub, string Email, string DisplayName, string[] Roles);

public sealed record CreateGameRequest(
    string Name,
    bool IsPublic,
    long MaxTimeBetweenTurnsSeconds,
    int NumberOfPlayers,
    string MissedTurnPolicy);

public sealed record GameSummaryResponse(
    string GameId,
    string Name,
    string Lifecycle,
    string Generation,
    int TurnYear,
    DateTimeOffset? DeadlineAt,
    int ActivePlayerCount,
    int SubmittedCount,
    bool IsPublic);

public sealed record PlayerResponse(int EmpireId, string? AccountId, string Kind, string Race, bool TurnSubmitted);

public sealed record JoinGameRequest(int EmpireId, string Race);

// Orders: the OrderWriter XML (ROOT/Turn, ROOT/Id, ROOT/Orders/Command[]) inside a
// JSON envelope. The server derives the empire from the session (R3), so the body
// never names it authoritatively.
public sealed record OrdersRequest(int TurnYear, string OrdersXml, string ProtocolVersion = "1");
public sealed record OrdersResponse(int TurnYear, string OrdersXml, bool Submitted);

// Intel: the per-empire .intel Xml inside a JSON envelope, fog-of-war correct.
public sealed record IntelResponse(int TurnYear, string IntelXml, string ProtocolVersion = "1");

public sealed record StatusResponse(
    int TurnYear,
    string Lifecycle,
    string Generation,
    DateTimeOffset? DeadlineAt,
    int ActivePlayerCount,
    int SubmittedCount,
    IReadOnlyList<int> SubmittedEmpireIds);

public sealed record ExtendDeadlineRequest(long AddSeconds);
public sealed record VersionResponse(string ApiVersion, string ProtocolVersion, string MinClientVersion);
public sealed record DeadlineFireRequest(string GameId, int TurnYear);

// ---- M3: AI seats ----------------------------------------------------------

// The internal AI submission (galaxies-ai spec Section 7.3). The empire id is a
// route parameter, not a body field, and the seat must already be AI driven; the
// body carries only the same OrderWriter XML a human client would send. Held is
// the fallback path: mark the seat submitted so the turn is not blocked, but do
// not overwrite whatever orders the seat already has.
public sealed record AiOrdersRequest(int TurnYear, string? OrdersXml, bool Held = false);
public sealed record AiOrdersResponse(int TurnYear, int EmpireId, bool Held, bool OrdersWritten);

// Host adds an AI opponent to an open lobby slot.
public sealed record AddAiPlayerRequest(int EmpireId, string? ParticipantId, string? Difficulty, string? Race);

// ---- M4: the game browser --------------------------------------------------

public sealed record GameListResponse(
    IReadOnlyList<GameSummaryResponse> Games,
    string Scope,
    int Limit,
    int Offset,
    bool HasMore);

// ---- M4: settings ----------------------------------------------------------

public sealed record VictoryConditionDto(bool Enabled, int Value);

public sealed record GameSettingsResponse(
    string GameId,
    string Lifecycle,
    bool Editable,
    long MaxTimeBetweenTurnsSeconds,
    int MapWidth,
    int MapHeight,
    int NumberOfStars,
    int StarSeparation,
    int StarDensity,
    int StarUniformity,
    VictoryConditionDto PlanetsOwned,
    VictoryConditionDto TechLevels,
    VictoryConditionDto NumberOfFields,
    VictoryConditionDto TotalScore,
    VictoryConditionDto SecondPlaceScore,
    VictoryConditionDto ProductionCapacity,
    VictoryConditionDto CapitalShips,
    VictoryConditionDto HighestScore,
    int TargetsToMeet,
    int MinimumGameTime);

// Every field is optional: a PATCH names only what changes. Null means "leave it".
public sealed record UpdateGameSettingsRequest(
    long? MaxTimeBetweenTurnsSeconds = null,
    int? MapWidth = null,
    int? MapHeight = null,
    int? NumberOfStars = null,
    int? StarSeparation = null,
    int? StarDensity = null,
    int? StarUniformity = null,
    VictoryConditionDto? PlanetsOwned = null,
    VictoryConditionDto? TechLevels = null,
    VictoryConditionDto? NumberOfFields = null,
    VictoryConditionDto? TotalScore = null,
    VictoryConditionDto? SecondPlaceScore = null,
    VictoryConditionDto? ProductionCapacity = null,
    VictoryConditionDto? CapitalShips = null,
    VictoryConditionDto? HighestScore = null,
    int? TargetsToMeet = null,
    int? MinimumGameTime = null);

// ---- M4: invites -----------------------------------------------------------

public sealed record CreateInviteRequest(string Email);

// Token is returned only to the host, on create and list, because the host is the
// one who has to send the link. It is never in any player-facing projection.
public sealed record InviteResponse(
    string InviteId,
    string GameId,
    string InvitedEmail,
    string Status,
    string? Token,
    int? EmpireId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? AcceptedAt);

public sealed record AcceptInviteResponse(string GameId, PlayerResponse Player);

// ---- M4: game-over summary -------------------------------------------------

public sealed record StandingResponse(
    int EmpireId,
    string? AccountId,
    string Kind,
    string Race,
    bool Resigned,
    bool AiTakeover,
    int LastSubmittedTurn);

public sealed record GameOverSummaryResponse(
    string GameId,
    string Name,
    string Lifecycle,
    int StartYear,
    int FinalTurnYear,
    int YearsPlayed,
    int? WinnerEmpireId,
    DateTimeOffset? FinishedAt,
    string FinalStatePath,
    IReadOnlyList<StandingResponse> Standings,
    string ScoreNote);

// ---- M4: DSAR export -------------------------------------------------------

public sealed record AccountExportMembership(
    string GameId,
    string GameName,
    string Lifecycle,
    int TurnYear,
    int EmpireId,
    string Kind,
    string Race,
    bool Resigned,
    int OrdersObjectCount,
    IReadOnlyList<string> OrdersObjects,
    int IntelObjectCount,
    IReadOnlyList<string> IntelObjects);

public sealed record AccountExportResponse(
    string GoogleSub,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string[] Roles,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<AccountExportMembership> Memberships,
    IReadOnlyList<InviteResponse> Invites,
    DateTimeOffset GeneratedAt,
    string BlobNote);

// ---- M4: moderation --------------------------------------------------------

public sealed record CreateReportRequest(string TargetType, string TargetId, string Reason);

public sealed record ReportResponse(
    string ReportId,
    string ReporterAccountId,
    string TargetType,
    string TargetId,
    string Reason,
    string Status,
    string? Resolution,
    string? ResolvedByAccountId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ResolveReportRequest(string? Resolution);

public sealed record BanRequest(string? Reason);

public sealed record BanResponse(string GoogleSub, string Reason, string BannedByAccountId, DateTimeOffset? CreatedAt);
