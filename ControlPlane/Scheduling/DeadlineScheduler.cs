// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Tasks.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Disambiguate: unqualified Task is the framework task type; the Cloud Tasks
// message type is written fully qualified as Google.Cloud.Tasks.V2.Task.
using Task = System.Threading.Tasks.Task;

namespace Galaxies.ControlPlane.Scheduling;

/// <summary>
/// Arms and cancels a game's per-turn deadline (design Section B.2). Cloud Tasks
/// is the timed, per-entity, cancellable, deduped trigger: one task per
/// (gameId, turnYear), named so a second enqueue collides and no-ops, firing at
/// the deadline into galaxies-api's /internal/deadline-fire with an OIDC token.
/// </summary>
public interface IDeadlineScheduler
{
    /// <summary>
    /// Schedule (or, by the deterministic task name, dedupe) the deadline task for
    /// the turn the game will advance to. No-op if <paramref name="deadline"/> is
    /// null (a game with no clock).
    /// </summary>
    Task ScheduleDeadlineAsync(string gameId, int turnYear, DateTimeOffset? deadline, CancellationToken ct = default);

    /// <summary>Cancel a pending deadline task (host pause, early generation).</summary>
    Task CancelDeadlineAsync(string gameId, int turnYear, CancellationToken ct = default);
}

/// <summary>Configuration for the Cloud Tasks deadline queue.</summary>
public sealed class DeadlineSchedulerOptions
{
    public required string ProjectId { get; init; }
    public required string LocationId { get; init; }        // e.g. us-central1
    public required string QueueId { get; init; }           // e.g. galaxies-deadlines
    public required string DeadlineFireUrl { get; init; }   // galaxies-api /internal/deadline-fire
    public required string InvokerServiceAccount { get; init; } // OIDC identity to mint
}

public sealed class CloudTasksDeadlineScheduler : IDeadlineScheduler
{
    private readonly CloudTasksClient client;
    private readonly DeadlineSchedulerOptions options;
    private readonly ILogger<CloudTasksDeadlineScheduler> log;

    public CloudTasksDeadlineScheduler(
        CloudTasksClient client,
        DeadlineSchedulerOptions options,
        ILogger<CloudTasksDeadlineScheduler>? log = null)
    {
        this.client = client;
        this.options = options;
        this.log = log ?? NullLogger<CloudTasksDeadlineScheduler>.Instance;
    }

    // The deterministic task name is the dedup key: a second create with the same
    // (gameId, turnYear) fails AlreadyExists, which we treat as success.
    private string TaskId(string gameId, int turnYear) => $"gen-{gameId}-{turnYear}";

    public async Task ScheduleDeadlineAsync(string gameId, int turnYear, DateTimeOffset? deadline, CancellationToken ct = default)
    {
        if (deadline is not { } when)
        {
            return; // no clock on this game
        }

        // Refuse to arm a deadline nobody can receive.
        //
        // GALAXIES_API_BASE_URL was set by nothing, so this URL silently became
        // http://localhost/internal/deadline-fire. Cloud Tasks accepted the task,
        // reported success, and then delivered every deadline to the loopback
        // address of whichever machine ran the API. Turn generation on a deadline
        // simply never happened, and no error appeared anywhere: the failure was a
        // valid string in a required field.
        //
        // Loopback is legitimate when running the whole stack on one machine, so it
        // is a warning there and an error only when a real queue is involved.
        // The scheme check is not redundant. On Linux, Uri.TryCreate accepts a
        // rooted path such as "/internal/deadline-fire" as an ABSOLUTE uri, parsing
        // it as file:///internal/deadline-fire. That is precisely the string an
        // unset GALAXIES_API_BASE_URL produces, and the services run on Linux, so an
        // absolute-only check would have passed the exact case it exists to catch.
        if (!Uri.TryCreate(options.DeadlineFireUrl, UriKind.Absolute, out Uri? fireUrl)
            || (fireUrl.Scheme != Uri.UriSchemeHttp && fireUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "GALAXIES_API_BASE_URL is not set to an absolute http or https URL, so the deadline target "
                + $"resolved to '{options.DeadlineFireUrl}'. Cloud Tasks would accept the task and deliver it "
                + "nowhere. Set it to the value of `terraform output api_url` and apply again.");
        }

        if (fireUrl.IsLoopback)
        {
            log.LogWarning(
                "Deadline target {Url} is a loopback address. Cloud Tasks cannot reach it. "
                + "This is only correct when the queue is local.", options.DeadlineFireUrl);
        }

        QueueName queue = new(options.ProjectId, options.LocationId, options.QueueId);
        var task = new Google.Cloud.Tasks.V2.Task
        {
            Name = new TaskName(options.ProjectId, options.LocationId, options.QueueId, TaskId(gameId, turnYear)).ToString(),
            ScheduleTime = Timestamp.FromDateTimeOffset(when.ToUniversalTime()),
            HttpRequest = new HttpRequest
            {
                Url = options.DeadlineFireUrl,
                HttpMethod = Google.Cloud.Tasks.V2.HttpMethod.Post,
                Headers = { ["Content-Type"] = "application/json" },
                Body = ByteString.CopyFromUtf8(
                    $"{{\"gameId\":\"{gameId}\",\"turnYear\":{turnYear}}}"),
                OidcToken = new OidcToken { ServiceAccountEmail = options.InvokerServiceAccount },
            },
        };

        try
        {
            await client.CreateTaskAsync(new CreateTaskRequest { Parent = queue.ToString(), Task = task }, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
            // The deadline for this turn is already armed. Dedup by design.
            log.LogDebug("Deadline task {Task} already exists; leaving it.", TaskId(gameId, turnYear));
        }
    }

    public async Task CancelDeadlineAsync(string gameId, int turnYear, CancellationToken ct = default)
    {
        var name = new TaskName(options.ProjectId, options.LocationId, options.QueueId, TaskId(gameId, turnYear));
        try
        {
            await client.DeleteTaskAsync(name, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            // Already fired or never armed; nothing to cancel.
        }
    }
}
