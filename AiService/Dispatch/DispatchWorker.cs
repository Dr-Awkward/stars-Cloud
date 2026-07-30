// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Dispatch;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Galaxies.AiContract;
using Galaxies.AiService.ApiClient;
using Galaxies.AiService.Firestore;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Model;

using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nova.Common;

/// <summary>
/// The dispatch record: the ONLY source of which seat is being played.
///
/// Every empire id in this file comes from here. Nothing downstream, and in
/// particular nothing a participant returns, can redirect the work to another
/// seat.
/// </summary>
/// <param name="GameId">The game.</param>
/// <param name="TurnYear">The turn being played.</param>
/// <param name="EmpireId">The seat. Authoritative.</param>
public readonly record struct DispatchRecord(string GameId, int TurnYear, int EmpireId);

/// <summary>
/// What one dispatch did, as returned on the wire and stored on the run.
///
/// Field names are snake_case to match the ai_runs document and the smoke script
/// in spec Section 14, which greps the dispatch response for orders_count. A
/// response whose field names differ from the record they describe would make
/// every runbook line a guess.
/// </summary>
public sealed record DispatchResult
{
    /// <summary>submitted, held, failed, noop, or disabled.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("orders_count")] public int OrdersCount { get; init; }

    [JsonPropertyName("orders_dropped")] public int OrdersDropped { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("request_uri")] public string? RequestUri { get; init; }

    [JsonPropertyName("response_uri")] public string? ResponseUri { get; init; }

    /// <summary>False when the seat seed fell back to a non-authoritative master seed.</summary>
    [JsonPropertyName("seed_authoritative")] public bool SeedAuthoritative { get; init; }

    [JsonPropertyName("dropped")]
    public IReadOnlyList<DroppedOrder> Dropped { get; init; } = Array.Empty<DroppedOrder>();
}

/// <summary>
/// Runs one AI seat's turn (spec Section 2.2).
///
/// Two invariants govern everything below.
///
/// ISOLATION (spec Section 10.1). A participant may only ever see the seat it was
/// handed. This is made structurally true rather than merely intended: the intel
/// object path is built by one private method that takes the empire id as a
/// parameter, and that method is called from exactly one place with exactly one
/// value, the dispatch record's EmpireId. There is no list of seats in scope
/// here, no loop over empires, and no way to widen the read, because there is
/// nothing wider to read from.
///
/// LIVENESS (spec Section 8.2). A turn generates on schedule whether or not the
/// AI answered. Every failure path below ends in held orders, never in an
/// exception that escapes and never in an empty order set submitted as if it
/// were a deliberate turn.
/// </summary>
public sealed class DispatchWorker
{
    private readonly AiStore store;
    private readonly RunLock runLock;
    private readonly StorageClient storage;
    private readonly ParticipantClient participants;
    private readonly OrderSubmitClient orders;
    private readonly IControlPlane control;
    private readonly AiServiceConfig config;
    private readonly ILogger<DispatchWorker> log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public DispatchWorker(
        AiStore store,
        RunLock runLock,
        StorageClient storage,
        ParticipantClient participants,
        OrderSubmitClient orders,
        IControlPlane control,
        AiServiceConfig config,
        ILogger<DispatchWorker>? log = null)
    {
        this.store = store;
        this.runLock = runLock;
        this.storage = storage;
        this.participants = participants;
        this.orders = orders;
        this.control = control;
        this.config = config;
        this.log = log ?? NullLogger<DispatchWorker>.Instance;
    }

    // ---- Object paths -------------------------------------------------------

    /// <summary>
    /// The seat's own intel object, matching the canonical layout in
    /// Api/Storage/ObjectStores.cs. The empireId parameter is the isolation
    /// boundary: see the single call site in <see cref="RunAsync"/>.
    /// </summary>
    private static string IntelPath(string gameId, int turnYear, int empireId)
        => $"games/{gameId}/intel/{turnYear}/{empireId}.intel";

    private static string RequestArtifactPath(string gameId, int turnYear, int empireId)
        => $"ai-runs/{gameId}/{turnYear}/{empireId}.req.json";

    private static string ResponseArtifactPath(string gameId, int turnYear, int empireId)
        => $"ai-runs/{gameId}/{turnYear}/{empireId}.resp.json";

    // ---- The dispatch -------------------------------------------------------

    public async Task<DispatchResult> RunAsync(DispatchRecord record, CancellationToken ct = default)
    {
        if (!config.GalaxiesAiEnabled)
        {
            // Master switch off. Not even the run record is written: a dark
            // service should leave no trace in a live game's data.
            return new DispatchResult { Status = "disabled" };
        }

        // (a) Take the run lock. A duplicate Cloud Tasks delivery stops here.
        RunClaim claim;
        try
        {
            claim = await runLock.TryClaimAsync(
                record.GameId, record.TurnYear, record.EmpireId,
                LeaseSeconds(), ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not take the run lock for {Game}/{Year}/{Empire}.",
                record.GameId, record.TurnYear, record.EmpireId);
            return new DispatchResult { Status = "failed", Error = "Run lock unavailable: " + e.Message };
        }

        if (!claim.Won)
        {
            return new DispatchResult
            {
                Status = "noop",
                Error = claim.Outcome == RunClaimOutcome.AlreadySubmitted
                    ? "This seat-turn was already submitted."
                    : "Another worker holds this seat-turn.",
                OrdersCount = claim.Existing?.OrdersCount ?? 0,
                OrdersDropped = claim.Existing?.OrdersDropped ?? 0,
            };
        }

        AiRun run = new()
        {
            GameId = record.GameId,
            TurnYear = record.TurnYear,
            EmpireId = record.EmpireId,
            Status = RunStatus.Running,
            Attempts = claim.Attempts,
            StartedAt = Timestamp.GetCurrentTimestamp(),
        };

        try
        {
            return await RunClaimedAsync(record, run, ct);
        }
        catch (Exception e)
        {
            // A dispatch must never take the service down or leave a seat stuck
            // Running. Any escape lands here and becomes held orders.
            log.LogError(e, "Dispatch for {Game}/{Year}/{Empire} threw.",
                record.GameId, record.TurnYear, record.EmpireId);
            return await FinishAsync(run, RunStatus.Failed, "Dispatch threw: " + e.Message, ct);
        }
    }

    private async Task<DispatchResult> RunClaimedAsync(
        DispatchRecord record, AiRun run, CancellationToken ct)
    {
        // The kill switch (spec Section 3.1). Records held, submits nothing.
        if (!config.AiDispatchEnabled)
        {
            return await FinishAsync(run, RunStatus.Held,
                "AI_DISPATCH_ENABLED is false; the seat runs on held orders.", ct);
        }

        // ---- Who is playing this seat --------------------------------------
        AiSeat? seat = await store.GetSeatAsync(record.GameId, record.EmpireId, ct);
        if (!await IsAiSeatAsync(record, seat, ct))
        {
            return await FinishAsync(run, RunStatus.Held,
                "Seat is not AI controlled; nothing dispatched.", ct);
        }

        string participantId = string.IsNullOrWhiteSpace(seat?.ParticipantId)
            ? config.DefaultParticipantId
            : seat!.ParticipantId;
        string difficulty = string.IsNullOrWhiteSpace(seat?.Difficulty) ? "normal" : seat!.Difficulty;
        AiParticipantVersion? manifest =
            await store.GetParticipantVersionAsync(participantId, seat?.Version ?? "", ct);

        run.ParticipantId = participantId;
        run.Version = seat?.Version ?? "";

        string serviceUrl = !string.IsNullOrWhiteSpace(manifest?.ServiceUrl)
            ? manifest!.ServiceUrl
            : config.DefaultParticipantUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return await FinishAsync(run, RunStatus.Held,
                $"No endpoint known for participant '{participantId}'.", ct);
        }

        // Only a first-party image shares the engine assembly, so only it can
        // read the native intel payload. An unregistered participant is assumed
        // first-party in M3 because galaxies.default-ai is the only one that
        // exists; that assumption dies the moment the M6 registry turns on.
        bool firstParty = manifest?.FirstParty ?? true;

        // ---- (b) Load ONLY this seat's intel -------------------------------
        // The isolation guarantee, spec Section 10.1. record.EmpireId is the only
        // value ever interpolated into this path, and this is its only call site.
        string intelPath = IntelPath(record.GameId, record.TurnYear, record.EmpireId);
        byte[]? intelBytes = await ReadObjectAsync(config.IntelBucket, intelPath, ct);
        if (intelBytes is null)
        {
            return await FinishAsync(run, RunStatus.Held,
                $"No intel object at gs://{config.IntelBucket}/{intelPath}.", ct);
        }

        // ---- (c) Rebuild Intel and compute the seat seed -------------------
        Intel intel;
        try
        {
            // IntelWriter saves plain, uncompressed XML (xmldoc.Save), so the
            // object is normally identity-encoded. The gzip magic-number check
            // costs two bytes of inspection and means that if the intel pipeline
            // ever starts compressing, this reads it instead of failing every AI
            // seat in every game with an unhelpful parse error.
            bool gzipped = intelBytes.Length > 1 && intelBytes[0] == 0x1F && intelBytes[1] == 0x8B;
            intel = Envelope.ReadIntel(new NativePayload
            {
                ContentType = Envelope.IntelContentType,
                Encoding = gzipped ? Envelope.GzipBase64 : Envelope.Identity,
                Body = gzipped
                    ? Convert.ToBase64String(intelBytes)
                    : Encoding.UTF8.GetString(intelBytes),
            });
        }
        catch (Exception e)
        {
            return await FinishAsync(run, RunStatus.Failed, "Intel did not parse: " + e.Message, ct);
        }

        EmpireData? empire = intel.EmpireState;
        if (empire is null)
        {
            return await FinishAsync(run, RunStatus.Failed, "Intel carries no empire state.", ct);
        }

        // Belt and braces on isolation: if the object at this seat's path is not
        // this seat's data, something upstream mislaid an object, and playing it
        // would hand one empire another's view. Refuse, loudly.
        if (empire.Id != record.EmpireId)
        {
            log.LogError("Intel at {Path} carries empire {Found}, expected {Expected}.",
                intelPath, empire.Id, record.EmpireId);
            return await FinishAsync(run, RunStatus.Failed,
                $"Intel object carries empire {empire.Id}, expected {record.EmpireId}.", ct);
        }

        AiGameInfo? gameInfo = await store.GetGameInfoAsync(record.GameId, ct);
        bool seedAuthoritative = gameInfo is not null;
        long masterSeed = gameInfo?.MasterSeed ?? 0L;
        string seatSeed = SeatSeed.For(masterSeed, record.TurnYear, record.EmpireId);
        run.SeedAuthoritative = seedAuthoritative;

        if (!seedAuthoritative)
        {
            // Do not fabricate determinism. The seed still exists and the
            // participant still plays; it is simply not reproducible against the
            // real game, and the run record says so rather than implying it is.
            log.LogWarning(
                "No master seed recorded for {Game}; seat seed derived from 0 and marked non-authoritative.",
                record.GameId);
        }

        // ---- (d) Build the act request -------------------------------------
        DateTimeOffset deadline = await SeatDeadlineAsync(record, manifest, ct);
        ActRequest request = EmpireViewTranscoder.BuildRequest(
            intel,
            record.GameId,
            record.TurnYear,
            seatSeed,
            difficulty,
            settings: null, // GameSettings live on ServerData, which a client never opens.
            deadline,
            includeNativeIntel: firstParty);

        string requestUri = await WriteArtifactAsync(
            RequestArtifactPath(record.GameId, record.TurnYear, record.EmpireId),
            JsonSerializer.Serialize(request, Json), ct);
        run.RequestUri = requestUri;

        // ---- (e) Call the participant, with retries -------------------------
        int timeout = manifest?.TimeoutSeconds > 0 ? manifest.TimeoutSeconds : config.DefaultTimeoutSeconds;
        ActResult act = default;
        int attempts = Math.Max(1, config.DispatchRetry + 1);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            act = await participants.ActAsync(serviceUrl, manifest?.Endpoint ?? "/v1/act", request, timeout, ct);
            if (act.Ok)
            {
                break;
            }

            log.LogWarning("Participant {Participant} attempt {Attempt}/{Total} failed for {Game}/{Year}/{Empire}: {Error}",
                participantId, attempt, attempts, record.GameId, record.TurnYear, record.EmpireId, act.Error);
        }

        string? responseUri = null;
        if (act.RawBody is not null)
        {
            responseUri = await WriteArtifactAsync(
                ResponseArtifactPath(record.GameId, record.TurnYear, record.EmpireId), act.RawBody, ct);
            run.ResponseUri = responseUri;
        }

        // ---- (g) Held orders on timeout, error, or nothing ------------------
        if (!act.Ok)
        {
            RunStatus status = act.TimedOut ? RunStatus.Held : RunStatus.Failed;
            return await FinishAsync(run, status,
                act.Error ?? "Participant returned nothing.", ct, requestUri, responseUri);
        }

        // ---- (f) Map the response ------------------------------------------
        // expectedTurnYear and expectedEmpireId come from the DISPATCH RECORD,
        // never from the response. The response's own values are compared for
        // equality inside the mapper and otherwise ignored.
        MappedOrders mapped = OrderMapper.Map(
            act.Response,
            empire,
            record.TurnYear,
            record.EmpireId,
            config.MaxOrdersPerTurn);

        run.OrdersCount = mapped.AcceptedCount;
        run.OrdersDropped = mapped.DroppedCount;

        if (mapped.AllInvalid || mapped.AcceptedCount == 0)
        {
            // An empty accepted set is not a deliberate pass. Submitting it would
            // look like the AI chose to do nothing, when in fact it failed.
            return await FinishAsync(run, RunStatus.Held,
                mapped.AllInvalid
                    ? "Every order the participant returned was invalid."
                    : "The participant returned no usable orders.",
                ct, requestUri, responseUri, mapped.Dropped);
        }

        // ---- (h) Submit through galaxies-api --------------------------------
        string ordersXml = OrderMapper.BuildOrdersXml(record.TurnYear, record.EmpireId, mapped.Accepted);
        SubmitResult submit = await orders.SubmitAsync(
            record.GameId, record.EmpireId, record.TurnYear, ordersXml, held: false, ct);

        if (!submit.Ok)
        {
            return await FinishAsync(run, RunStatus.Failed,
                $"Order submit failed ({submit.StatusCode}): {submit.Error}",
                ct, requestUri, responseUri, mapped.Dropped);
        }

        // ---- (i) Finish the run record --------------------------------------
        return await FinishAsync(run, RunStatus.Submitted, null, ct, requestUri, responseUri, mapped.Dropped);
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>
    /// A seat is dispatchable when its ai_seats pin says AI, or, when there is no
    /// pin yet, when the canonical roster says the member is an AI.
    ///
    /// The roster is the authority on what a seat IS (the locked seat model);
    /// ai_seats is additive metadata about which participant plays it. Checking
    /// both is what stops a missing or stale pin from ever letting this worker
    /// submit orders for a person's seat.
    /// </summary>
    private async Task<bool> IsAiSeatAsync(DispatchRecord record, AiSeat? seat, CancellationToken ct)
    {
        if (seat is not null)
        {
            if (seat.Controller == SeatController.AiTakeover && !config.AiTakeoverEnabled)
            {
                return false;
            }

            return seat.IsAiControlled;
        }

        try
        {
            IReadOnlyList<Member> members = await control.GetMembersAsync(record.GameId, ct);
            foreach (Member member in members)
            {
                if (member.EmpireId == record.EmpireId)
                {
                    return member.Kind == PlayerKind.Ai;
                }
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the roster for {Game}; treating the seat as not AI.", record.GameId);
        }

        return false;
    }

    /// <summary>
    /// When held orders take over for this seat. The participant is told, so a
    /// well-behaved one can return nothing rather than answer late.
    /// </summary>
    private async Task<DateTimeOffset> SeatDeadlineAsync(
        DispatchRecord record, AiParticipantVersion? manifest, CancellationToken ct)
    {
        int timeout = manifest?.TimeoutSeconds > 0 ? manifest.TimeoutSeconds : config.DefaultTimeoutSeconds;
        DateTimeOffset cap = DateTimeOffset.UtcNow.AddSeconds(timeout);

        try
        {
            GameMeta? game = await control.GetGameAsync(record.GameId, ct);
            if (game?.DeadlineAt is { } deadline)
            {
                DateTimeOffset human = deadline.ToDateTimeOffset();

                // The seat's effective deadline is whichever comes first: our own
                // wall-clock cap, or the game's human deadline. Never later than
                // the game deadline, or the answer would arrive after the turn.
                return human < cap ? human : cap;
            }
        }
        catch (Exception e)
        {
            log.LogDebug(e, "No game deadline available for {Game}; using the timeout cap.", record.GameId);
        }

        return cap;
    }

    /// <summary>Lease long enough to cover the full retry budget plus overhead.</summary>
    private int LeaseSeconds()
        => (config.DefaultTimeoutSeconds * Math.Max(1, config.DispatchRetry + 1)) + 60;

    private async Task<byte[]?> ReadObjectAsync(string bucket, string path, CancellationToken ct)
    {
        try
        {
            using MemoryStream buffer = new();
            await storage.DownloadObjectAsync(bucket, path, buffer, cancellationToken: ct);
            return buffer.ToArray();
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read gs://{Bucket}/{Path}.", bucket, path);
            return null;
        }
    }

    /// <summary>
    /// Write a durable copy of the request or response under STATE_BUCKET.
    ///
    /// Capture is free here and priceless later: every dispatch is replayable
    /// (spec Section 12.1) purely because these two objects exist. An artifact
    /// write that fails must not fail the dispatch, so this returns an empty uri
    /// and lets the turn proceed.
    /// </summary>
    private async Task<string> WriteArtifactAsync(string path, string content, CancellationToken ct)
    {
        try
        {
            using MemoryStream body = new(Encoding.UTF8.GetBytes(content));
            await storage.UploadObjectAsync(
                config.StateBucket, path, "application/json", body, cancellationToken: ct);
            return $"gs://{config.StateBucket}/{path}";
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not write the run artifact gs://{Bucket}/{Path}.", config.StateBucket, path);
            return "";
        }
    }

    /// <summary>
    /// Close out the run: write the record, and on any non-submitted outcome mark
    /// the seat submitted-with-held so turngen stops waiting on it.
    /// </summary>
    private async Task<DispatchResult> FinishAsync(
        AiRun run,
        RunStatus status,
        string? error,
        CancellationToken ct,
        string? requestUri = null,
        string? responseUri = null,
        IReadOnlyList<DroppedOrder>? dropped = null)
    {
        run.Status = status;
        run.FinishedAt = Timestamp.GetCurrentTimestamp();
        run.Error = error ?? "";
        run.LeaseUntil = null;
        if (requestUri is not null) run.RequestUri = requestUri;
        if (responseUri is not null) run.ResponseUri = responseUri;

        if (status is RunStatus.Held or RunStatus.Failed)
        {
            run.OrdersCount = 0;

            // Held orders, spec Section 8.2. The seat keeps whatever it last
            // submitted (or nothing, on turn one) and is marked submitted so the
            // turn generates on schedule. This call carries held=true and an
            // empty body precisely so the API does NOT overwrite the seat's
            // existing orders object.
            //
            // The guard matters. With the kill switch off this worker must touch
            // no live game state at all, not even to mark a seat held, so it
            // records the run and stops. The seat then simply misses its window
            // and turngen's own deadline path generates the turn, which is the
            // behaviour a game had before galaxies-ai existed.
            if (config.AiDispatchEnabled)
            {
                SubmitResult hold = await orders.SubmitAsync(
                    run.GameId, run.EmpireId, run.TurnYear, "", held: true, ct);
                if (!hold.Ok)
                {
                    log.LogWarning("Could not mark {Game}/{Empire} held: {Error}",
                        run.GameId, run.EmpireId, hold.Error);
                }
            }
        }

        try
        {
            await store.PutRunAsync(run, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not write the run record for {Game}/{Year}/{Empire}.",
                run.GameId, run.TurnYear, run.EmpireId);
        }

        return new DispatchResult
        {
            Status = status.ToString().ToLowerInvariant(),
            OrdersCount = run.OrdersCount,
            OrdersDropped = run.OrdersDropped,
            Error = string.IsNullOrEmpty(run.Error) ? null : run.Error,
            RequestUri = string.IsNullOrEmpty(run.RequestUri) ? null : run.RequestUri,
            ResponseUri = string.IsNullOrEmpty(run.ResponseUri) ? null : run.ResponseUri,
            SeedAuthoritative = run.SeedAuthoritative,
            Dropped = dropped ?? Array.Empty<DroppedOrder>(),
        };
    }
}
