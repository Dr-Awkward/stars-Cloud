// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Firestore;

using System.Text;

using Google.Cloud.Firestore;

/// <summary>
/// Which kind of controller is playing a seat (spec Section 6, ai_seats.controller).
///
/// A note on spelling. The spec writes these tokens lower case and snake case
/// (ai, human, ai_takeover). This codebase stores enums through
/// FirestoreEnumNameConverter, which writes the C# member name verbatim, so what
/// actually lands in Firestore is Ai, Human, AiTakeover. That is the convention
/// every other control-plane enum already follows (see ControlPlane/Model/Enums.cs),
/// and one service inventing a second convention would be worse than the case
/// mismatch. Anything grepping the raw document should match case-insensitively.
/// </summary>
public enum SeatController
{
    Human,      // a person holds this seat; galaxies-ai does not dispatch it
    Ai,         // an AI seat from game creation
    AiTakeover, // a human seat the missed-turn ladder handed to the AI
}

/// <summary>Outcome of one seat-turn dispatch (spec Section 6, ai_runs.status).</summary>
public enum RunStatus
{
    Running,   // a worker holds the run lock and is mid-dispatch
    Submitted, // orders reached the API and the seat is marked submitted
    Held,      // no orders submitted; the seat keeps its previous orders
    Failed,    // the dispatch errored out after its retries; also held orders
}

/// <summary>
/// The per-seat participant pin, Firestore <c>games/{gameId}/ai_seats/{empireId}</c>
/// (spec Section 6).
///
/// This is ADDITIVE metadata keyed by the same (gameId, empireId) as the canonical
/// roster in games/{gameId}/members/{empireId}. It is not a parallel roster and
/// must never be treated as one: whether a seat exists at all is the members
/// subcollection's answer, and this document only says which participant plays it.
/// </summary>
[FirestoreData]
public sealed class AiSeat
{
    [FirestoreProperty("empire_id")] public int EmpireId { get; set; }

    [FirestoreProperty("participant_id")] public string ParticipantId { get; set; } = "";

    /// <summary>Pinned at game creation. A running game never changes it.</summary>
    [FirestoreProperty("version")] public string Version { get; set; } = "";

    [FirestoreProperty("difficulty")] public string Difficulty { get; set; } = "normal";

    [FirestoreProperty("controller", ConverterType = typeof(FirestoreEnumNameConverter<SeatController>))]
    public SeatController Controller { get; set; } = SeatController.Human;

    [FirestoreProperty("pinned_at")] public Timestamp? PinnedAt { get; set; }

    /// <summary>True when this seat is one galaxies-ai is allowed to play.</summary>
    public bool IsAiControlled
        => Controller == SeatController.Ai || Controller == SeatController.AiTakeover;
}

/// <summary>
/// Per-game state galaxies-ai needs but that is not per seat, stored as one
/// reserved document inside the ai_seats subcollection.
///
/// The reserved id starts with an underscore so it can never collide with a seat
/// document, whose id is always the decimal empire id.
/// </summary>
[FirestoreData]
public sealed class AiGameInfo
{
    /// <summary>
    /// A mirror of ServerData.MasterSeed, written by whoever creates the game.
    ///
    /// galaxies-ai does not link the turn engine and never opens the persisted
    /// ServerData, so it cannot derive the seat seed unless someone puts the
    /// master seed here. When this document is absent the worker falls back to
    /// zero AND records seed_authoritative=false on the run, because a seed that
    /// silently defaults is a determinism claim that is quietly false, and a
    /// golden replay built on it would be worthless.
    /// </summary>
    [FirestoreProperty("master_seed")] public long MasterSeed { get; set; }

    [FirestoreProperty("recorded_at")] public Timestamp? RecordedAt { get; set; }
}

/// <summary>
/// One seat-turn dispatch record, Firestore
/// <c>ai_runs/{gameId}_{turnYear}_{empireId}</c> (spec Section 6). Doubles as the
/// idempotency lock; see <see cref="Dispatch.RunLock"/>.
/// </summary>
[FirestoreData]
public sealed class AiRun
{
    [FirestoreProperty("game_id")] public string GameId { get; set; } = "";

    [FirestoreProperty("turn_year")] public int TurnYear { get; set; }

    [FirestoreProperty("empire_id")] public int EmpireId { get; set; }

    [FirestoreProperty("status", ConverterType = typeof(FirestoreEnumNameConverter<RunStatus>))]
    public RunStatus Status { get; set; } = RunStatus.Running;

    [FirestoreProperty("attempts")] public int Attempts { get; set; }

    [FirestoreProperty("participant_id")] public string ParticipantId { get; set; } = "";

    [FirestoreProperty("version")] public string Version { get; set; } = "";

    [FirestoreProperty("started_at")] public Timestamp? StartedAt { get; set; }

    [FirestoreProperty("finished_at")] public Timestamp? FinishedAt { get; set; }

    [FirestoreProperty("orders_count")] public int OrdersCount { get; set; }

    [FirestoreProperty("orders_dropped")] public int OrdersDropped { get; set; }

    /// <summary>gs:// uri of the durable request copy under STATE_BUCKET.</summary>
    [FirestoreProperty("request_uri")] public string RequestUri { get; set; } = "";

    [FirestoreProperty("response_uri")] public string ResponseUri { get; set; } = "";

    [FirestoreProperty("error")] public string Error { get; set; } = "";

    /// <summary>
    /// False when the seat seed was derived from a fallback master seed rather
    /// than the game's real one. Not in the spec's field list; added because the
    /// alternative is a run record that implies a determinism guarantee this
    /// service could not actually make.
    /// </summary>
    [FirestoreProperty("seed_authoritative")] public bool SeedAuthoritative { get; set; }

    /// <summary>
    /// When the current Running claim expires. Also not in the spec's field list:
    /// without a lease, a worker killed mid-dispatch (Cloud Run can and does
    /// evict instances) leaves the seat pinned Running forever and no retry can
    /// ever touch it again.
    /// </summary>
    [FirestoreProperty("lease_until")] public Timestamp? LeaseUntil { get; set; }
}

/// <summary>The registry head. Minimal in M3; the real registry is M6.</summary>
[FirestoreData]
public sealed class AiParticipant
{
    [FirestoreProperty("name")] public string Name { get; set; } = "";

    [FirestoreProperty("author")] public string Author { get; set; } = "";

    [FirestoreProperty("description")] public string Description { get; set; } = "";

    [FirestoreProperty("latest_version")] public string LatestVersion { get; set; } = "";

    [FirestoreProperty("visibility")] public string Visibility { get; set; } = "public";

    [FirestoreProperty("difficulty")] public List<string> Difficulty { get; set; } = new();
}

/// <summary>
/// One immutable participant version, <c>ai_participants/{id}/versions/{version}</c>.
///
/// M3 stores only the handful of fields the dispatch worker actually reads. The
/// full manifest (resources, determinism, network, allowed_hosts, cost_class,
/// service_account, lifecycle, the llm block) is M6 and is deliberately absent
/// rather than half-populated: a field that exists but is never written is a
/// field someone will later trust.
/// </summary>
[FirestoreData]
public sealed class AiParticipantVersion
{
    [FirestoreProperty("kind")] public string Kind { get; set; } = "container";

    [FirestoreProperty("image")] public string Image { get; set; } = "";

    /// <summary>Route on the participant service. Always /v1/act in M3.</summary>
    [FirestoreProperty("endpoint")] public string Endpoint { get; set; } = "/v1/act";

    /// <summary>
    /// Base URL of the deployed participant service. The M6 manifest derives this
    /// from the image and the Cloud Run service; M3 stores it directly so there
    /// is exactly one place a dispatch looks up where to call.
    /// </summary>
    [FirestoreProperty("service_url")] public string ServiceUrl { get; set; } = "";

    [FirestoreProperty("contract_versions")] public List<string> ContractVersions { get; set; } = new();

    /// <summary>Wall-clock cap for this participant. Zero means use the default.</summary>
    [FirestoreProperty("timeout_s")] public int TimeoutSeconds { get; set; }

    /// <summary>
    /// True for images Galaxies itself builds. Only a first-party participant is
    /// sent the engine-native intel payload, because only a participant sharing
    /// the engine assembly can read it; a community image would get a blob it
    /// cannot parse and no extra information.
    /// </summary>
    [FirestoreProperty("first_party")] public bool FirstParty { get; set; }

    [FirestoreProperty("lifecycle")] public string Lifecycle { get; set; } = "active";
}

/// <summary>
/// Typed access to the four Firestore collections galaxies-ai owns (spec Section
/// 6). Nothing else in this service touches Firestore directly.
/// </summary>
public sealed class AiStore
{
    public const string GamesCollection = "games";
    public const string AiSeatsCollection = "ai_seats";
    public const string AiRunsCollection = "ai_runs";
    public const string AiParticipantsCollection = "ai_participants";
    public const string VersionsCollection = "versions";

    /// <summary>Reserved ai_seats document id for the per-game (not per-seat) fields.</summary>
    public const string GameInfoDocId = "_game";

    private readonly FirestoreDb db;

    public AiStore(FirestoreDb db)
    {
        this.db = db;
    }

    public FirestoreDb Db => db;

    // ---- Document id sanitizing ---------------------------------------------

    /// <summary>
    /// Make a fragment safe to concatenate into a flat Firestore document id.
    ///
    /// Game ids look like "roybot:game:8f21". A colon is legal in a Firestore id
    /// but reads badly in a composite key and is awkward in a shell, and the
    /// spec's own smoke script does ${GID//:/_}, so the rule here is chosen to
    /// agree with it: every character outside [A-Za-z0-9_-] becomes an
    /// underscore. For the ids Galaxies actually mints that is exactly the
    /// colon-to-underscore substitution the runbook performs.
    ///
    /// This is one-way. It is a key, not an encoding, so two game ids that differ
    /// only in punctuation would collide. Galaxies mints game ids as
    /// "roybot:game:{hex}", where the hex is unique on its own, so that cannot
    /// happen today. Saying so here rather than pretending the function is
    /// injective.
    /// </summary>
    public static string SanitizeIdPart(string part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return "_";
        }

        StringBuilder builder = new(part.Length);
        foreach (char c in part)
        {
            bool safe = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_';
            builder.Append(safe ? c : '_');
        }

        return builder.ToString();
    }

    /// <summary>The flat ai_runs document id for one seat-turn.</summary>
    public static string RunId(string gameId, int turnYear, int empireId)
        => $"{SanitizeIdPart(gameId)}_{turnYear}_{empireId}";

    // ---- References ---------------------------------------------------------

    // Subcollection paths use the RAW game id, not the sanitized one, because
    // they hang off the same games/{gameId} document the canonical members
    // roster does. Sanitizing here would silently fork the two apart.
    public DocumentReference GameDoc(string gameId) => db.Collection(GamesCollection).Document(gameId);

    public CollectionReference SeatCol(string gameId) => GameDoc(gameId).Collection(AiSeatsCollection);

    public DocumentReference SeatDoc(string gameId, int empireId)
        => SeatCol(gameId).Document(empireId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public DocumentReference GameInfoDoc(string gameId) => SeatCol(gameId).Document(GameInfoDocId);

    public DocumentReference RunDoc(string gameId, int turnYear, int empireId)
        => db.Collection(AiRunsCollection).Document(RunId(gameId, turnYear, empireId));

    public DocumentReference ParticipantDoc(string participantId)
        => db.Collection(AiParticipantsCollection).Document(SanitizeIdPart(participantId));

    public DocumentReference ParticipantVersionDoc(string participantId, string version)
        => ParticipantDoc(participantId).Collection(VersionsCollection).Document(SanitizeIdPart(version));

    // ---- Seats --------------------------------------------------------------

    public async Task<AiSeat?> GetSeatAsync(string gameId, int empireId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await SeatDoc(gameId, empireId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AiSeat>() : null;
    }

    /// <summary>Every AI-controlled seat in a game, ordered by empire id.</summary>
    public async Task<IReadOnlyList<AiSeat>> ListAiSeatsAsync(string gameId, CancellationToken ct = default)
    {
        QuerySnapshot snap = await SeatCol(gameId).GetSnapshotAsync(ct);
        List<AiSeat> seats = new();
        foreach (DocumentSnapshot doc in snap.Documents)
        {
            // Skip the reserved per-game document; it is not a seat.
            if (doc.Id == GameInfoDocId)
            {
                continue;
            }

            AiSeat seat = doc.ConvertTo<AiSeat>();
            if (seat.IsAiControlled)
            {
                seats.Add(seat);
            }
        }

        seats.Sort((a, b) => a.EmpireId.CompareTo(b.EmpireId));
        return seats;
    }

    public async Task UpsertSeatAsync(string gameId, AiSeat seat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seat);
        seat.PinnedAt ??= Timestamp.GetCurrentTimestamp();
        await SeatDoc(gameId, seat.EmpireId).SetAsync(seat, SetOptions.MergeAll, ct);
    }

    /// <summary>
    /// Flip a seat's controller, used by the abandoned-seat takeover path. Merges
    /// so a takeover never clobbers the pinned participant and version, which is
    /// what makes a takeover reversible.
    /// </summary>
    public async Task SetControllerAsync(
        string gameId, int empireId, SeatController controller, CancellationToken ct = default)
    {
        await SeatDoc(gameId, empireId).SetAsync(
            new Dictionary<string, object>
            {
                ["controller"] = controller.ToString(),
                ["empire_id"] = empireId,
            },
            SetOptions.MergeAll,
            ct);
    }

    // ---- Per-game info ------------------------------------------------------

    public async Task<AiGameInfo?> GetGameInfoAsync(string gameId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await GameInfoDoc(gameId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AiGameInfo>() : null;
    }

    public async Task PutGameInfoAsync(string gameId, AiGameInfo info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.RecordedAt ??= Timestamp.GetCurrentTimestamp();
        await GameInfoDoc(gameId).SetAsync(info, SetOptions.MergeAll, ct);
    }

    // ---- Runs ---------------------------------------------------------------

    public async Task<AiRun?> GetRunAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await RunDoc(gameId, turnYear, empireId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AiRun>() : null;
    }

    public async Task PutRunAsync(AiRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await RunDoc(run.GameId, run.TurnYear, run.EmpireId).SetAsync(run, SetOptions.MergeAll, ct);
    }

    // ---- Participants (minimal in M3) ---------------------------------------

    public async Task<AiParticipantVersion?> GetParticipantVersionAsync(
        string participantId, string version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(participantId) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        DocumentSnapshot snap = await ParticipantVersionDoc(participantId, version).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AiParticipantVersion>() : null;
    }

    public async Task<AiParticipant?> GetParticipantAsync(string participantId, CancellationToken ct = default)
    {
        DocumentSnapshot snap = await ParticipantDoc(participantId).GetSnapshotAsync(ct);
        return snap.Exists ? snap.ConvertTo<AiParticipant>() : null;
    }

    /// <summary>
    /// Write the one participant M3 knows about, if it is not already there.
    ///
    /// Spec Section 6: the galaxies.default-ai document exists from first deploy
    /// even with the registry flag off, because it is the only participant the
    /// lobby can offer. Merge semantics, so re-running this on a deployed service
    /// never overwrites a manifest an operator edited.
    /// </summary>
    public async Task SeedDefaultParticipantAsync(
        string participantId, string serviceUrl, CancellationToken ct = default)
    {
        AiParticipant head = new()
        {
            Name = "Nova Default AI",
            Author = "Galaxies core team",
            Description = "The classic Stars! Nova AI: expands, scouts, colonizes.",
            LatestVersion = "1.0.0",
            Visibility = "public",
            Difficulty = new List<string> { "easy", "normal", "hard" },
        };
        await ParticipantDoc(participantId).SetAsync(head, SetOptions.MergeAll, ct);

        AiParticipantVersion version = new()
        {
            Kind = "container",
            Image = "us-central1-docker.pkg.dev/roybot/roybot-galaxies/ai-nova-default:1.0.0",
            Endpoint = "/v1/act",
            ServiceUrl = serviceUrl,
            ContractVersions = new List<string> { AiContract.ContractVersions.Current },
            TimeoutSeconds = 60,
            FirstParty = true,
            Lifecycle = "active",
        };
        await ParticipantVersionDoc(participantId, "1.0.0").SetAsync(version, SetOptions.MergeAll, ct);
    }

    /// <summary>Cheap Firestore reachability probe for /readyz.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await db.Collection(AiRunsCollection).Limit(1).GetSnapshotAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
