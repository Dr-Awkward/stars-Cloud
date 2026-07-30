// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Text.Json;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;

namespace Galaxies.ControlPlane.Eventing;

/// <summary>
/// The turn-generated event (design Section B.4). Pub/Sub carries results outward
/// from generation to the AI dispatcher, the notifier, and analytics.
///
/// Field names are LOCKED by the specs reconciliation: the year field is turnYear
/// (NOT newTurnYear), and the shape is {gameId, turnYear, empireIds, aiEmpireIds,
/// gameEnded, handoffs}.
/// </summary>
public sealed record TurnGeneratedEvent
{
    public required string GameId { get; init; }
    public required int TurnYear { get; init; }
    public int[] EmpireIds { get; init; } = Array.Empty<int>();
    public int[] AiEmpireIds { get; init; } = Array.Empty<int>();
    public bool GameEnded { get; init; }
    /// <summary>Seats handed to AI this turn by the missed-turn ladder.</summary>
    public int[] Handoffs { get; init; } = Array.Empty<int>();
}

public interface ITurnEventPublisher
{
    Task PublishTurnGeneratedAsync(TurnGeneratedEvent evt, CancellationToken ct = default);
}

public sealed class PubSubTurnEventPublisher : ITurnEventPublisher, IAsyncDisposable
{
    public const string TurnGeneratedTopic = "turn-generated";

    private readonly PublisherClient publisher;

    public PubSubTurnEventPublisher(PublisherClient publisher)
    {
        this.publisher = publisher;
    }

    /// <summary>Build the publisher for the turn-generated topic on a project.</summary>
    public static async Task<PubSubTurnEventPublisher> CreateAsync(string projectId)
    {
        TopicName topic = new(projectId, TurnGeneratedTopic);
        PublisherClient client = await PublisherClient.CreateAsync(topic);
        return new PubSubTurnEventPublisher(client);
    }

    public async Task PublishTurnGeneratedAsync(TurnGeneratedEvent evt, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(evt);
        var message = new PubsubMessage
        {
            Data = ByteString.CopyFromUtf8(json),
            Attributes =
            {
                ["gameId"] = evt.GameId,
                ["turnYear"] = evt.TurnYear.ToString(),
                ["gameEnded"] = evt.GameEnded ? "true" : "false",
            },
        };
        await publisher.PublishAsync(message);
    }

    public async ValueTask DisposeAsync()
    {
        await publisher.ShutdownAsync(TimeSpan.FromSeconds(5));
    }
}
