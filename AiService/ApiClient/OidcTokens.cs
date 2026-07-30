// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.ApiClient;

using System.Collections.Concurrent;

using Google.Apis.Auth.OAuth2;

/// <summary>
/// Mints Google OIDC identity tokens for service to service calls.
///
/// Galaxies internal auth is GCP-native OIDC everywhere: galaxies-ai to
/// galaxies-api and galaxies-ai to a participant both carry an identity token
/// whose audience is the callee's URL, and Cloud Run IAM checks it at the edge.
/// There is no shared secret and no internal header; do not add one.
///
/// Locally, and in any test that has no application default credentials, minting
/// fails. That is deliberately not fatal here: the caller gets null and sends no
/// Authorization header, which fails at the callee rather than crashing the
/// worker. A dispatch that cannot authenticate should fall back to held orders
/// like any other failed dispatch, not take the service down.
/// </summary>
public interface IOidcTokenSource
{
    Task<string?> GetTokenAsync(string audience, CancellationToken ct = default);
}

public sealed class GoogleOidcTokenSource : IOidcTokenSource
{
    // One OidcToken per audience. The object refreshes itself, so caching it is
    // what keeps a per-seat dispatch from doing a fresh metadata-server round
    // trip on every single call.
    private readonly ConcurrentDictionary<string, Task<OidcToken>> tokens = new();

    public async Task<string?> GetTokenAsync(string audience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return null;
        }

        try
        {
            Task<OidcToken> token = tokens.GetOrAdd(audience, aud => MintAsync(aud));
            return await (await token).GetAccessTokenAsync(ct);
        }
        catch (Exception)
        {
            // Drop the failed entry so a transient credential problem does not
            // poison this audience for the life of the process.
            tokens.TryRemove(audience, out _);
            return null;
        }
    }

    private static async Task<OidcToken> MintAsync(string audience)
    {
        GoogleCredential credential = await GoogleCredential.GetApplicationDefaultAsync();
        return await credential.GetOidcTokenAsync(OidcTokenOptions.FromTargetAudience(audience));
    }
}
