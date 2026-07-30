// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService;

/// <summary>
/// Every environment variable and feature flag galaxies-ai reads (spec Section
/// 3.2), resolved once at startup.
///
/// Every flag defaults to FALSE. That is not a stylistic choice: this service can
/// submit orders into live games, so the state it reaches when a deployment
/// forgets a substitution has to be the state where it does nothing. A missing
/// env var must never read as "on".
/// </summary>
public sealed class AiServiceConfig
{
    // ---- Identity and placement (spec Section 3.2) --------------------------

    public string ProjectId { get; init; } = "roybot";

    public string Region { get; init; } = "us-central1";

    /// <summary>Order-submit target, and the OIDC audience for that call.</summary>
    public string GalaxiesApiUrl { get; init; } = "";

    /// <summary>Seat-state reads on the deadline backstop. Unused in M3 paths.</summary>
    public string GalaxiesTurngenUrl { get; init; } = "";

    // ---- Buckets ------------------------------------------------------------

    public string IntelBucket { get; init; } = "roybot-galaxies-intel";

    /// <summary>
    /// Where submitted AI orders land. galaxies-ai never writes here itself; the
    /// API does, on its behalf, so AI and human orders travel one code path. The
    /// name is carried only so operators can see the target in the config dump.
    /// </summary>
    public string OrdersBucket { get; init; } = "roybot-galaxies-orders";

    public string StateBucket { get; init; } = "roybot-galaxies-state";

    // ---- Scheduling ---------------------------------------------------------

    /// <summary>
    /// The Cloud Tasks queue that already carries turngen's gen-{gameId}-{turnYear}
    /// deadline task. Per-seat AI tasks ride the same queue; no new topic and no
    /// new queue (spec Section 2.3).
    /// </summary>
    public string TasksQueue { get; init; } = "galaxies-deadlines";

    /// <summary>
    /// This service's own public URL. It is the Cloud Tasks target and the OIDC
    /// audience the task's identity token is minted for.
    /// </summary>
    public string AiServiceUrl { get; init; } = "";

    /// <summary>Service account Cloud Tasks mints the dispatch OIDC token as.</summary>
    public string InvokerServiceAccount { get; init; } = "galaxies-ai-sa@roybot.iam.gserviceaccount.com";

    /// <summary>
    /// How far AHEAD of the human deadline an AI seat is dispatched, in seconds.
    ///
    /// Spec Section 8.1 requires AI seats to run early so a slow AI never eats the
    /// human window. "Early" needs a number, and a number nobody can change is a
    /// number that will be wrong for some cadence, so it is configurable here
    /// rather than buried in the enqueuer. One hour suits the day-scale default
    /// cadence; a fast test game should shorten it.
    /// </summary>
    public int DispatchLeadSeconds { get; init; } = 3600;

    // ---- Contract and limits ------------------------------------------------

    public string ContractVersion { get; init; } = AiContract.ContractVersions.Current;

    /// <summary>Wall-clock cap on a participant call when its manifest omits one.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;

    /// <summary>Retries after the first attempt before the run is marked failed.</summary>
    public int DispatchRetry { get; init; } = 1;

    public int MaxOrdersPerTurn { get; init; } = AiContract.OrderMapper.DefaultMaxOrders;

    // ---- The single M3 participant -----------------------------------------

    /// <summary>
    /// The one participant M3 can dispatch. The open registry is M6; until then a
    /// seat with no pinned participant falls back to this id.
    /// </summary>
    public string DefaultParticipantId { get; init; } = "galaxies.default-ai";

    /// <summary>
    /// Base URL of the built-in Nova AI worker. Not in spec Section 3.2, which
    /// assumes the M6 registry supplies every endpoint; in M3 the registry is off,
    /// so the one first-party endpoint has to come from somewhere and this is the
    /// honest place for it. A registry manifest, once it exists, wins over this.
    /// </summary>
    public string DefaultParticipantUrl { get; init; } = "";

    // ---- Flags (spec Section 3.1). All default false. ------------------------

    /// <summary>Master switch. Off: reads return disabled, mutations 403.</summary>
    public bool GalaxiesAiEnabled { get; init; }

    /// <summary>
    /// The kill switch. Off: the dispatch worker records held and submits
    /// nothing, so every AI seat runs on held orders and turns still generate.
    /// </summary>
    public bool AiDispatchEnabled { get; init; }

    /// <summary>Off: abandoned human seats are not auto-filled by an AI.</summary>
    public bool AiTakeoverEnabled { get; init; }

    /// <summary>Read the whole configuration out of the environment.</summary>
    public static AiServiceConfig FromConfiguration(IConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        return new AiServiceConfig
        {
            ProjectId = Str(cfg, "GOOGLE_CLOUD_PROJECT", "roybot"),
            Region = Str(cfg, "GALAXIES_REGION", "us-central1"),
            GalaxiesApiUrl = Str(cfg, "GALAXIES_API_URL", ""),
            GalaxiesTurngenUrl = Str(cfg, "GALAXIES_TURNGEN_URL", ""),

            IntelBucket = Str(cfg, "INTEL_BUCKET", "roybot-galaxies-intel"),
            OrdersBucket = Str(cfg, "ORDERS_BUCKET", "roybot-galaxies-orders"),
            StateBucket = Str(cfg, "STATE_BUCKET", "roybot-galaxies-state"),

            TasksQueue = Str(cfg, "TASKS_QUEUE", "galaxies-deadlines"),
            AiServiceUrl = Str(cfg, "GALAXIES_AI_URL", ""),
            InvokerServiceAccount = Str(cfg, "GALAXIES_INVOKER_SA", "galaxies-ai-sa@roybot.iam.gserviceaccount.com"),
            DispatchLeadSeconds = Num(cfg, "AI_DISPATCH_LEAD_S", 3600),

            ContractVersion = Str(cfg, "CONTRACT_VERSION", AiContract.ContractVersions.Current),
            DefaultTimeoutSeconds = Num(cfg, "DEFAULT_TIMEOUT_S", 60),
            DispatchRetry = Num(cfg, "DISPATCH_RETRY", 1),
            MaxOrdersPerTurn = Num(cfg, "MAX_ORDERS_PER_TURN", AiContract.OrderMapper.DefaultMaxOrders),

            DefaultParticipantId = Str(cfg, "DEFAULT_PARTICIPANT_ID", "galaxies.default-ai"),
            DefaultParticipantUrl = Str(cfg, "DEFAULT_PARTICIPANT_URL", ""),

            GalaxiesAiEnabled = Flag(cfg, "GALAXIES_AI_ENABLED"),
            AiDispatchEnabled = Flag(cfg, "AI_DISPATCH_ENABLED"),
            AiTakeoverEnabled = Flag(cfg, "AI_TAKEOVER_ENABLED"),
        };
    }

    private static string Str(IConfiguration cfg, string key, string fallback)
    {
        string? value = cfg[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int Num(IConfiguration cfg, string key, int fallback)
        => int.TryParse(cfg[key], out int value) && value >= 0 ? value : fallback;

    /// <summary>
    /// Only the literal string "true" (case-insensitive) turns a flag on. Not "1",
    /// not "yes", not "TRUE " with a stray space that some other parser would have
    /// accepted. A typo in a substitution has to land on off, not on.
    /// </summary>
    private static bool Flag(IConfiguration cfg, string key)
        => string.Equals(cfg[key]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
