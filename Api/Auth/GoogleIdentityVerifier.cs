// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Apis.Auth;

namespace Galaxies.Api.Auth;

/// <summary>A verified Google identity (design Section C.1/C.2).</summary>
public sealed record GoogleIdentity(string Sub, string Email, string Name, string? Picture);

public interface IGoogleIdentityVerifier
{
    Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Verifies a Google/Firebase-brokered ID token and extracts the canonical
/// identity. Google is the only identity provider; the subject claim becomes the
/// users/{google_sub} document id (design Section C.2). Bad tokens throw
/// <see cref="InvalidGoogleTokenException"/>, which the API maps to 401.
/// </summary>
public sealed class GoogleIdentityVerifier : IGoogleIdentityVerifier
{
    private readonly string audience; // the OAuth client id we accept tokens for

    public GoogleIdentityVerifier(string audience)
    {
        this.audience = audience;
    }

    public async Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { audience },
            };
            GoogleJsonWebSignature.Payload payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

            if (string.IsNullOrEmpty(payload.Subject))
            {
                throw new InvalidGoogleTokenException("Google token carried no subject.");
            }
            return new GoogleIdentity(
                payload.Subject,
                payload.Email ?? "",
                payload.Name ?? payload.Email ?? payload.Subject,
                payload.Picture);
        }
        catch (InvalidJwtException e)
        {
            throw new InvalidGoogleTokenException(e.Message, e);
        }
    }
}

public sealed class InvalidGoogleTokenException : Exception
{
    public InvalidGoogleTokenException(string message, Exception? inner = null) : base(message, inner) { }
}
