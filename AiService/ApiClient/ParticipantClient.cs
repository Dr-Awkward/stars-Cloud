// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.ApiClient;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Galaxies.AiContract;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// One call to a participant's <c>POST /v1/act</c>.
/// </summary>
/// <param name="Response">What came back, or null on any failure.</param>
/// <param name="Error">Why it failed. Null on success.</param>
/// <param name="TimedOut">True when the wall-clock cap fired.</param>
/// <param name="ElapsedMs">Round trip, for the run record.</param>
/// <param name="RawBody">The raw response text, for the durable artifact copy.</param>
public readonly record struct ActResult(
    ActResponse? Response,
    string? Error,
    bool TimedOut,
    long ElapsedMs,
    string? RawBody)
{
    public bool Ok => Response is not null && Error is null;
}

/// <summary>
/// Calls a participant's act route with an OIDC token and a hard wall-clock cap.
///
/// The cap is the point. A participant is untrusted code on the far side of an
/// HTTP call, and the one failure a play-by-email game cannot absorb is a seat
/// that never answers, so the timeout is enforced here by this worker rather than
/// trusted to the participant's own good behaviour. When it fires the seat falls
/// back to held orders and the turn generates on schedule.
/// </summary>
public sealed class ParticipantClient
{
    private readonly HttpClient http;
    private readonly IOidcTokenSource tokens;
    private readonly ILogger<ParticipantClient> log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ParticipantClient(
        HttpClient http,
        IOidcTokenSource tokens,
        ILogger<ParticipantClient>? log = null)
    {
        this.http = http;
        this.tokens = tokens;
        this.log = log ?? NullLogger<ParticipantClient>.Instance;
    }

    /// <summary>
    /// Post one act request.
    /// </summary>
    /// <param name="serviceUrl">Base URL of the participant service; also the OIDC audience.</param>
    /// <param name="endpoint">Route on that service, normally /v1/act.</param>
    /// <param name="request">The seat's act request.</param>
    /// <param name="timeoutSeconds">Hard wall-clock cap.</param>
    public async Task<ActResult> ActAsync(
        string serviceUrl,
        string endpoint,
        ActRequest request,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return new ActResult(null, "Participant has no service URL.", false, 0, null);
        }

        string baseUrl = serviceUrl.TrimEnd('/');
        string route = string.IsNullOrWhiteSpace(endpoint) ? "/v1/act" : endpoint;
        if (!route.StartsWith('/'))
        {
            route = "/" + route;
        }

        Stopwatch clock = Stopwatch.StartNew();

        // Linked so an inbound request cancellation still aborts the call, and so
        // the timeout is ours rather than HttpClient's ambient one.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        try
        {
            using HttpRequestMessage message = new(HttpMethod.Post, baseUrl + route)
            {
                Content = JsonContent.Create(request),
            };

            string? token = await tokens.GetTokenAsync(baseUrl, timeout.Token);
            if (token is not null)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage response = await http.SendAsync(message, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            clock.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ActResult(null,
                    $"Participant returned {(int)response.StatusCode}.",
                    false, clock.ElapsedMilliseconds, body);
            }

            ActResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ActResponse>(body, Json);
            }
            catch (JsonException e)
            {
                return new ActResult(null, "Participant returned unparseable JSON: " + e.Message,
                    false, clock.ElapsedMilliseconds, body);
            }

            if (parsed is null)
            {
                return new ActResult(null, "Participant returned an empty body.",
                    false, clock.ElapsedMilliseconds, body);
            }

            return new ActResult(parsed, null, false, clock.ElapsedMilliseconds, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            clock.Stop();
            log.LogWarning("Participant at {Url} exceeded its {Timeout}s cap.", baseUrl, timeoutSeconds);
            return new ActResult(null, $"Participant exceeded its {timeoutSeconds}s wall-clock cap.",
                true, clock.ElapsedMilliseconds, null);
        }
        catch (Exception e)
        {
            clock.Stop();
            log.LogWarning(e, "Participant call to {Url} threw.", baseUrl);
            return new ActResult(null, e.Message, false, clock.ElapsedMilliseconds, null);
        }
    }
}
