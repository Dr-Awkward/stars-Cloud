// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Galaxies.Client;

/// <summary>
/// The cloud implementation of <see cref="ITurnTransport"/> (design Section E.4):
/// it calls galaxies-api over HTTPS, carrying the first-party session token as a
/// bearer credential. The engine XML rides inside the small JSON envelope the API
/// expects, so the desktop client's serialization is unchanged.
/// </summary>
public sealed class HttpTurnTransport : ITurnTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly Func<string?> accessTokenProvider;

    /// <param name="http">A client whose BaseAddress is the /v1 root of galaxies-api.</param>
    /// <param name="accessTokenProvider">Supplies the current session access token.</param>
    public HttpTurnTransport(HttpClient http, Func<string?> accessTokenProvider)
    {
        this.http = http;
        this.accessTokenProvider = accessTokenProvider;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        string? token = accessTokenProvider();
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    public async Task<string> GetIntelAsync(string gameId, int? turnYear = null, CancellationToken ct = default)
    {
        string path = turnYear is { } y
            ? $"games/{gameId}/intel/{y}"
            : $"games/{gameId}/intel";
        using HttpResponseMessage r = await http.SendAsync(Request(HttpMethod.Get, path), ct);
        await EnsureOk(r);
        IntelEnvelope? env = await r.Content.ReadFromJsonAsync<IntelEnvelope>(Json, ct);
        return env?.IntelXml ?? throw new TransportException("Empty intel response.");
    }

    public async Task PutOrdersAsync(string gameId, int turnYear, string ordersXml, CancellationToken ct = default)
    {
        HttpRequestMessage req = Request(HttpMethod.Put, $"games/{gameId}/orders");
        req.Content = JsonContent.Create(new OrdersEnvelope(turnYear, ordersXml), options: Json);
        using HttpResponseMessage r = await http.SendAsync(req, ct);
        await EnsureOk(r);
    }

    public async Task<string?> GetOrdersAsync(string gameId, CancellationToken ct = default)
    {
        using HttpResponseMessage r = await http.SendAsync(Request(HttpMethod.Get, $"games/{gameId}/orders"), ct);
        if (r.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureOk(r);
        OrdersEnvelope? env = await r.Content.ReadFromJsonAsync<OrdersEnvelope>(Json, ct);
        return env?.OrdersXml;
    }

    public async Task SubmitOrdersAsync(string gameId, CancellationToken ct = default)
    {
        using HttpResponseMessage r = await http.SendAsync(Request(HttpMethod.Post, $"games/{gameId}/orders/submit"), ct);
        await EnsureOk(r);
    }

    public async Task<TurnStatus> GetStatusAsync(string gameId, CancellationToken ct = default)
    {
        using HttpResponseMessage r = await http.SendAsync(Request(HttpMethod.Get, $"games/{gameId}/status"), ct);
        await EnsureOk(r);
        StatusEnvelope? s = await r.Content.ReadFromJsonAsync<StatusEnvelope>(Json, ct);
        if (s is null) throw new TransportException("Empty status response.");
        return new TurnStatus(s.TurnYear, s.Lifecycle, s.Generation, false, s.SubmittedCount, s.ActivePlayerCount);
    }

    private static async Task EnsureOk(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return;
        string body = await r.Content.ReadAsStringAsync();
        throw new TransportException($"galaxies-api returned {(int)r.StatusCode}: {body}", r.StatusCode);
    }

    // Envelope shapes mirror the API's Wire DTOs. They are duplicated here rather
    // than shared so the client library does not depend on the server assembly.
    private sealed record IntelEnvelope(int TurnYear, string IntelXml, string ProtocolVersion = "1");
    private sealed record OrdersEnvelope(int TurnYear, string OrdersXml, string ProtocolVersion = "1");
    private sealed record StatusEnvelope(int TurnYear, string Lifecycle, string Generation, int ActivePlayerCount, int SubmittedCount);
}

public sealed class TransportException : Exception
{
    public HttpStatusCode? Status { get; }
    public TransportException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
}
