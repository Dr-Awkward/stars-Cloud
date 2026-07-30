// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.ApiClient;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The body of the galaxies-api internal orders route (spec Section 7.3).
///
/// Note what is NOT here: the empire id. It travels in the path, stamped by this
/// worker from its own dispatch record, so there is no field a participant's
/// response could ever influence into naming a different seat.
/// </summary>
public sealed record SubmitOrdersBody
{
    [JsonPropertyName("turnYear")] public int TurnYear { get; init; }

    /// <summary>
    /// The OrderWriter wire shape (ROOT/Turn, ROOT/Id, ROOT/Orders/Command[]),
    /// byte-identical to what a desktop client submits. Empty when held.
    /// </summary>
    [JsonPropertyName("ordersXml")] public string OrdersXml { get; init; } = "";

    /// <summary>
    /// True to mark the seat submitted WITHOUT replacing its orders (spec Section
    /// 8.2). This is the whole held-orders mechanism: turngen stops waiting on the
    /// seat, and the seat keeps whatever it last submitted.
    /// </summary>
    [JsonPropertyName("held")] public bool Held { get; init; }
}

public readonly record struct SubmitResult(bool Ok, int StatusCode, string? Error)
{
    public static SubmitResult Success(int status) => new(true, status, null);
}

/// <summary>
/// Submits a seat's orders through galaxies-api, which writes them to the orders
/// bucket exactly as a human submission does and marks the seat TurnSubmitted.
///
/// galaxies-ai deliberately does not write the orders object itself. One writer
/// means one validation path and one submission-counting path; the turn engine
/// genuinely cannot tell an AI's orders from a person's, which is the property
/// the whole design rests on.
/// </summary>
public sealed class OrderSubmitClient
{
    private readonly HttpClient http;
    private readonly IOidcTokenSource tokens;
    private readonly AiServiceConfig config;
    private readonly ILogger<OrderSubmitClient> log;

    public OrderSubmitClient(
        HttpClient http,
        IOidcTokenSource tokens,
        AiServiceConfig config,
        ILogger<OrderSubmitClient>? log = null)
    {
        this.http = http;
        this.tokens = tokens;
        this.config = config;
        this.log = log ?? NullLogger<OrderSubmitClient>.Instance;
    }

    public async Task<SubmitResult> SubmitAsync(
        string gameId,
        int empireId,
        int turnYear,
        string ordersXml,
        bool held,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.GalaxiesApiUrl))
        {
            return new SubmitResult(false, 0, "GALAXIES_API_URL is not configured.");
        }

        string baseUrl = config.GalaxiesApiUrl.TrimEnd('/');
        string url = $"{baseUrl}/internal/v1/games/{Uri.EscapeDataString(gameId)}/seats/{empireId}/orders";

        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new SubmitOrdersBody
            {
                TurnYear = turnYear,
                OrdersXml = ordersXml ?? "",
                Held = held,
            }),
        };

        // The audience is the API's base URL, not the full route: Cloud Run
        // issues one identity per service.
        string? token = await tokens.GetTokenAsync(baseUrl, ct);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return SubmitResult.Success((int)response.StatusCode);
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            log.LogWarning("Order submit for {Game}/{Empire} failed {Status}: {Body}",
                gameId, empireId, (int)response.StatusCode, Truncate(body));
            return new SubmitResult(false, (int)response.StatusCode, Truncate(body));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Order submit for {Game}/{Empire} threw.", gameId, empireId);
            return new SubmitResult(false, 0, e.Message);
        }
    }

    // Error bodies end up in a Firestore run record and in logs; cap them so one
    // verbose 500 page cannot bloat every failed run document.
    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "...";
}
