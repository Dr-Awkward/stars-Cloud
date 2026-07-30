// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Harness;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Galaxies.AiContract;
using Galaxies.AiService.ApiClient;
using Galaxies.AiService.Firestore;

using Google.Cloud.Storage.V1;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nova.Common;

/// <summary>
/// The replay request (spec Section 12.1). Three ways to name what to replay,
/// in descending order of fidelity.
/// </summary>
public sealed record ReplayRequest
{
    /// <summary>Which participant to ask. Defaults to the built-in AI.</summary>
    [JsonPropertyName("participant_id")] public string? ParticipantId { get; init; }

    [JsonPropertyName("version")] public string? Version { get; init; }

    /// <summary>
    /// Explicit participant URL, overriding the registry. Lets an operator point
    /// a replay at a locally-deployed candidate image without publishing it.
    /// </summary>
    [JsonPropertyName("service_url")] public string? ServiceUrl { get; init; }

    /// <summary>
    /// A whole saved act request, as captured under
    /// STATE_BUCKET/ai-runs/{gameId}/{turnYear}/{empireId}.req.json. This is the
    /// highest-fidelity form: it carries the native intel, so the local IsValid
    /// dry run is possible.
    /// </summary>
    [JsonPropertyName("request")] public JsonElement? Request { get; init; }

    /// <summary>
    /// gs:// uri or bare object path of a saved request in STATE_BUCKET. Fetched
    /// and treated exactly as <see cref="Request"/>.
    /// </summary>
    [JsonPropertyName("request_uri")] public string? RequestUri { get; init; }

    /// <summary>
    /// A bare empire_view, for hand-built or trimmed fixtures. The participant is
    /// still called, but see the note on <see cref="ReplayResponse.DryRun"/>.
    /// </summary>
    [JsonPropertyName("empire_view")] public JsonElement? EmpireView { get; init; }

    [JsonPropertyName("game_id")] public string? GameId { get; init; }

    [JsonPropertyName("turn_year")] public int? TurnYear { get; init; }

    [JsonPropertyName("empire_id")] public int? EmpireId { get; init; }

    [JsonPropertyName("difficulty")] public string? Difficulty { get; init; }

    [JsonPropertyName("seat_seed")] public string? SeatSeed { get; init; }
}

public sealed record ReplayDropped
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

public sealed record ReplayResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }

    [JsonPropertyName("participant_id")] public string ParticipantId { get; init; } = "";

    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; init; }

    /// <summary>How many orders the participant returned, before validation.</summary>
    [JsonPropertyName("orders")] public int Orders { get; init; }

    /// <summary>How many passed the local IsValid dry run.</summary>
    [JsonPropertyName("accepted")] public int Accepted { get; init; }

    [JsonPropertyName("dropped")] public List<ReplayDropped> Dropped { get; init; } = new();

    /// <summary>
    /// Whether the IsValid dry run actually ran. It needs a real EmpireData, which
    /// only the native intel payload provides, so a replay of a bare empire_view
    /// reports the participant's orders with this false rather than pretending
    /// every order passed a check that never happened.
    /// </summary>
    [JsonPropertyName("dry_run")] public bool DryRun { get; init; }

    [JsonPropertyName("dry_run_skipped_because")] public string? DryRunSkippedBecause { get; init; }

    /// <summary>The raw orders the participant returned, echoed for diffing.</summary>
    [JsonPropertyName("returned_orders")] public List<OrderDto> ReturnedOrders { get; init; } = new();

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// The cloud form of the old command-line AI replay (spec Section 12.1).
///
/// The desktop path was <c>Nova --ai -r race -t turn -i intel</c> against a shared
/// folder with a lock file. There is no folder and no lock here: a replay is one
/// HTTP call to a participant with a saved body, so it runs against production
/// images, concurrently, from CI.
///
/// It is deliberately NOT gated by AI_DISPATCH_ENABLED. Replay never submits
/// orders and never touches a live game, so it must work while dispatch is dark;
/// that is exactly when an operator most needs to ask what the AI would do.
/// </summary>
public sealed class ReplayEndpoint
{
    private readonly AiStore store;
    private readonly ParticipantClient participants;
    private readonly StorageClient storage;
    private readonly AiServiceConfig config;
    private readonly ILogger<ReplayEndpoint> log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ReplayEndpoint(
        AiStore store,
        ParticipantClient participants,
        StorageClient storage,
        AiServiceConfig config,
        ILogger<ReplayEndpoint>? log = null)
    {
        this.store = store;
        this.participants = participants;
        this.storage = storage;
        this.config = config;
        this.log = log ?? NullLogger<ReplayEndpoint>.Instance;
    }

    public async Task<ReplayResponse> ReplayAsync(ReplayRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        string participantId = string.IsNullOrWhiteSpace(req.ParticipantId)
            ? config.DefaultParticipantId
            : req.ParticipantId;

        ActRequest? act;
        string? buildError;
        try
        {
            act = await BuildRequestAsync(req, ct);
            buildError = act is null ? "Nothing to replay: supply request, request_uri, or empire_view." : null;
        }
        catch (Exception e)
        {
            act = null;
            buildError = "Could not build the replay request: " + e.Message;
        }

        if (act is null)
        {
            return new ReplayResponse { Ok = false, ParticipantId = participantId, Error = buildError };
        }

        // Where to call.
        AiParticipantVersion? manifest =
            await store.GetParticipantVersionAsync(participantId, req.Version ?? "1.0.0", ct);
        string serviceUrl = !string.IsNullOrWhiteSpace(req.ServiceUrl)
            ? req.ServiceUrl!
            : !string.IsNullOrWhiteSpace(manifest?.ServiceUrl)
                ? manifest!.ServiceUrl
                : config.DefaultParticipantUrl;

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return new ReplayResponse
            {
                Ok = false,
                ParticipantId = participantId,
                Error = $"No endpoint known for participant '{participantId}'.",
            };
        }

        int timeout = manifest?.TimeoutSeconds > 0 ? manifest.TimeoutSeconds : config.DefaultTimeoutSeconds;
        ActResult result = await participants.ActAsync(
            serviceUrl, manifest?.Endpoint ?? "/v1/act", act, timeout, ct);

        if (!result.Ok || result.Response is null)
        {
            return new ReplayResponse
            {
                Ok = false,
                ParticipantId = participantId,
                ElapsedMs = result.ElapsedMs,
                Error = result.Error ?? "Participant returned nothing.",
            };
        }

        ActResponse response = result.Response;
        List<OrderDto> returned = response.Orders ?? new List<OrderDto>();

        // The dry run needs a real EmpireData, which only the native intel gives.
        EmpireData? empire = TryRebuildEmpire(act, out string? why);
        if (empire is null)
        {
            return new ReplayResponse
            {
                Ok = true,
                ParticipantId = participantId,
                ElapsedMs = result.ElapsedMs,
                Orders = returned.Count,
                Accepted = 0,
                DryRun = false,
                DryRunSkippedBecause = why,
                ReturnedOrders = returned,
            };
        }

        MappedOrders mapped = OrderMapper.Map(
            response, empire, act.Game.TurnYear, act.Seat.EmpireId, config.MaxOrdersPerTurn);

        return new ReplayResponse
        {
            Ok = true,
            ParticipantId = participantId,
            ElapsedMs = result.ElapsedMs,
            Orders = returned.Count,
            Accepted = mapped.AcceptedCount,
            Dropped = mapped.Dropped
                .Select(d => new ReplayDropped { Index = d.Index, Type = d.Type, Reason = d.Reason })
                .ToList(),
            DryRun = true,
            ReturnedOrders = returned,
        };
    }

    /// <summary>Resolve the three input forms into one act request.</summary>
    private async Task<ActRequest?> BuildRequestAsync(ReplayRequest req, CancellationToken ct)
    {
        if (req.Request is { } inline)
        {
            return inline.Deserialize<ActRequest>(Json);
        }

        if (!string.IsNullOrWhiteSpace(req.RequestUri))
        {
            string? saved = await ReadSavedAsync(req.RequestUri!, ct);
            return saved is null ? null : JsonSerializer.Deserialize<ActRequest>(saved, Json);
        }

        if (req.EmpireView is { } view)
        {
            // A bare view. Enough to exercise a participant end to end; not
            // enough to validate what it returns, which the response says.
            return new ActRequest
            {
                ContractVersion = config.ContractVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                IssuedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(config.DefaultTimeoutSeconds)
                    .ToUnixTimeMilliseconds(),
                Game = new GameContext
                {
                    GameId = req.GameId ?? "replay",
                    TurnYear = req.TurnYear ?? 0,
                    SeatSeed = req.SeatSeed ?? SeatSeed.Format(0),
                },
                Seat = new SeatContext
                {
                    EmpireId = req.EmpireId ?? 0,
                    Difficulty = req.Difficulty ?? "normal",
                },
                EmpireView = view.Deserialize<EmpireView>(Json) ?? new EmpireView(),
            };
        }

        return null;
    }

    private static EmpireData? TryRebuildEmpire(ActRequest act, out string? why)
    {
        why = null;

        if (act.IntelNative is null)
        {
            why = "The replayed request carries no native intel, so there is no EmpireData "
                + "to validate against. Replay a saved .req.json to get the dry run.";
            return null;
        }

        try
        {
            Intel intel = Envelope.ReadIntel(act.IntelNative);
            EmpireData? empire = intel.EmpireState;
            if (empire is null)
            {
                why = "The native intel carries no empire state.";
            }

            return empire;
        }
        catch (Exception e)
        {
            why = "The native intel did not parse: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// Fetch a saved request. Accepts a gs:// uri or a bare object path, but only
    /// ever from STATE_BUCKET: the harness reads back its own artifacts and has
    /// no business reaching into the intel or orders buckets.
    /// </summary>
    private async Task<string?> ReadSavedAsync(string uri, CancellationToken ct)
    {
        string path = uri;
        if (path.StartsWith("gs://", StringComparison.Ordinal))
        {
            string rest = path[5..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            string bucket = rest[..slash];
            if (!string.Equals(bucket, config.StateBucket, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replay reads only gs://{config.StateBucket}; refused '{bucket}'.");
            }

            path = rest[(slash + 1)..];
        }

        try
        {
            using MemoryStream buffer = new();
            await storage.DownloadObjectAsync(config.StateBucket, path, buffer, cancellationToken: ct);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the saved request gs://{Bucket}/{Path}.", config.StateBucket, path);
            return null;
        }
    }
}
