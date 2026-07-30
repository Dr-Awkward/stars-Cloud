// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Dispatch;

using System.Text.Json;

using Galaxies.AiService.Firestore;

using Google.Cloud.Tasks.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Disambiguate: unqualified Task is the framework task type; the Cloud Tasks
// message type is written fully qualified as Google.Cloud.Tasks.V2.Task. This is
// the same pattern ControlPlane/Scheduling/DeadlineScheduler.cs uses.
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Enqueues one Cloud Task per AI seat-turn (spec Section 2.2 step 2).
///
/// The task NAME is the dedup key, exactly as turngen's deadline scheduler uses
/// gen-{gameId}-{turnYear}: a second create with the same name fails
/// AlreadyExists, which is success. That is what makes it safe for both the
/// turn-generated event and the deadline-approaching backstop to enqueue the same
/// seat without the seat being played twice; the run lock then backstops the
/// backstop.
/// </summary>
public sealed class TaskEnqueuer
{
    private readonly CloudTasksClient client;
    private readonly AiServiceConfig config;
    private readonly ILogger<TaskEnqueuer> log;

    public TaskEnqueuer(CloudTasksClient client, AiServiceConfig config, ILogger<TaskEnqueuer>? log = null)
    {
        this.client = client;
        this.config = config;
        this.log = log ?? NullLogger<TaskEnqueuer>.Instance;
    }

    /// <summary>
    /// The per-seat task id. Sanitized because Cloud Tasks allows only letters,
    /// digits, hyphens and underscores in a task name, and Galaxies game ids
    /// contain colons.
    /// </summary>
    public static string TaskId(string gameId, int turnYear, int empireId)
        => $"ai-{AiStore.SanitizeIdPart(gameId)}-{turnYear}-{empireId}";

    /// <summary>
    /// Schedule one AI seat's dispatch.
    /// </summary>
    /// <param name="humanDeadline">
    /// When the game's human seats are due. The AI task is placed
    /// AI_DISPATCH_LEAD_S ahead of it so a slow AI eats its own window and not
    /// the players' (spec Section 8.1). With no deadline (a game with no clock)
    /// the seat runs immediately, because there is nothing to run ahead of.
    /// </param>
    public async Task EnqueueSeatAsync(
        string gameId,
        int turnYear,
        int empireId,
        DateTimeOffset? humanDeadline,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AiServiceUrl))
        {
            log.LogWarning("GALAXIES_AI_URL is unset; cannot enqueue AI seat tasks.");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset when = now;
        if (humanDeadline is { } deadline)
        {
            DateTimeOffset early = deadline.AddSeconds(-Math.Max(0, config.DispatchLeadSeconds));

            // Clamp forward. On a cadence shorter than the lead, "early" is
            // already in the past, and the honest answer is to run now rather
            // than to schedule a task Cloud Tasks would fire immediately anyway.
            when = early > now ? early : now;
        }

        string taskId = TaskId(gameId, turnYear, empireId);
        QueueName queue = new(config.ProjectId, config.Region, config.TasksQueue);

        string body = JsonSerializer.Serialize(new
        {
            game_id = gameId,
            turn_year = turnYear,
            empire_id = empireId,
        });

        var task = new Google.Cloud.Tasks.V2.Task
        {
            Name = new TaskName(config.ProjectId, config.Region, config.TasksQueue, taskId).ToString(),
            ScheduleTime = Timestamp.FromDateTimeOffset(when.ToUniversalTime()),
            HttpRequest = new HttpRequest
            {
                Url = config.AiServiceUrl.TrimEnd('/') + "/v1/dispatch",
                HttpMethod = Google.Cloud.Tasks.V2.HttpMethod.Post,
                Headers = { ["Content-Type"] = "application/json" },
                Body = ByteString.CopyFromUtf8(body),

                // Cloud Run IAM checks this at the edge; the audience is this
                // service's own URL. No shared secret anywhere.
                OidcToken = new OidcToken
                {
                    ServiceAccountEmail = config.InvokerServiceAccount,
                    Audience = config.AiServiceUrl.TrimEnd('/'),
                },
            },
        };

        try
        {
            await client.CreateTaskAsync(new CreateTaskRequest { Parent = queue.ToString(), Task = task }, ct);
            log.LogInformation("Enqueued {Task} for {When:o}.", taskId, when);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
            // This seat-turn is already armed. Dedup by design.
            log.LogDebug("AI seat task {Task} already exists; leaving it.", taskId);
        }
    }

    /// <summary>Cancel a pending AI seat task, for a paused or finished game.</summary>
    public async Task CancelSeatAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default)
    {
        TaskName name = new(config.ProjectId, config.Region, config.TasksQueue,
            TaskId(gameId, turnYear, empireId));
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
