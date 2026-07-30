// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Net.Http.Json;

namespace Galaxies.Api.Generation;

/// <summary>
/// The result of one turn generation as reported by the turngen worker: where the
/// new authoritative state landed, and who is in the game (for the turn-generated
/// event).
/// </summary>
public sealed record TurnGenerationResult(
    string NewStatePath,
    int[] EmpireIds,
    int[] AiEmpireIds,
    bool GameEnded,
    // Optional, and absent from older workers, which is why it is nullable with a
    // default: a finished game whose generator did not name a victor reports null,
    // and null must be read as "unknown", never as "nobody won".
    int? WinnerEmpireId = null);

/// <summary>
/// Runs one turn on the private turngen service. The API owns the exactly-once
/// lock (claim/commit around this call); turngen is a stateless compute worker
/// that loads state and orders from GCS, runs TurnGenerator.Generate, and writes
/// the new state and intel back. This reconciles the flagged state-field
/// collision by keeping all control-plane writes in the API.
/// </summary>
public interface ITurnGenerationInvoker
{
    Task<TurnGenerationResult> GenerateAsync(string gameId, int turnYear, CancellationToken ct = default);
}

public sealed class HttpTurnGenerationInvoker(HttpClient http, string turngenBaseUrl) : ITurnGenerationInvoker
{
    public async Task<TurnGenerationResult> GenerateAsync(string gameId, int turnYear, CancellationToken ct = default)
    {
        // POST /internal/games/{gameId}/generate with the intended turnYear. The
        // caller has already claimed the lock; turngen returns the new state path.
        var url = $"{turngenBaseUrl.TrimEnd('/')}/internal/games/{gameId}/generate";
        using HttpResponseMessage response = await http.PostAsJsonAsync(url, new { gameId, turnYear }, ct);
        response.EnsureSuccessStatusCode();
        TurnGenerationResult? result = await response.Content.ReadFromJsonAsync<TurnGenerationResult>(ct);
        return result ?? throw new InvalidOperationException("turngen returned an empty result.");
    }
}
