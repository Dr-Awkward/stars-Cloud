// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Galaxies.Api.Auth;

public sealed class JwtOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    /// <summary>HS256 signing key, from Secret Manager. Never in the image.</summary>
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 60;
}

/// <summary>
/// The session subject carried in a first-party access token: the Google subject
/// (which is the users/{google_sub} document id) plus roles.
/// </summary>
public sealed record SessionPrincipal(string GoogleSub, string Email, string[] Roles)
{
    public bool HasRole(string role) => Array.IndexOf(Roles, role) >= 0;
}

/// <summary>
/// Mints and validates the first-party session JWT (design Section C.1). Google is
/// brokered for identity; the backend then issues its own short-lived access token
/// so every request does not re-hit Google. HS256 with a Secret Manager key.
/// </summary>
public sealed class JwtService
{
    private const string RolesClaim = "roles";
    private readonly JwtOptions options;
    private readonly SymmetricSecurityKey key;

    public JwtService(JwtOptions options)
    {
        this.options = options;
        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }

    /// <summary>
    /// Mint a rotating refresh token (design Section C.1): a longer-lived token
    /// carrying the account and its current refresh-chain id. Rotating the chain id
    /// on the account revokes every older refresh token at once.
    /// </summary>
    public string MintRefreshToken(string googleSub, string chainId)
    {
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, googleSub),
            new Claim("cid", chainId),
            new Claim("typ", "refresh"),
        };
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Validate a refresh token; returns (googleSub, chainId) or null.</summary>
    public (string GoogleSub, string ChainId)? ValidateRefresh(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        try
        {
            ClaimsPrincipal cp = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            if (cp.FindFirstValue("typ") != "refresh") return null;
            string sub = cp.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            string cid = cp.FindFirstValue("cid") ?? "";
            return string.IsNullOrEmpty(sub) ? null : (sub, cid);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    public string MintAccessToken(SessionPrincipal principal)
    {
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principal.GoogleSub),
            new(JwtRegisteredClaimNames.Email, principal.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        foreach (string role in principal.Roles)
        {
            claims.Add(new Claim(RolesClaim, role));
        }

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(options.AccessTokenMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validate an access token and return its principal, or null if it is missing,
    /// expired, or not ours (design Section C.3 rule R1).
    /// </summary>
    public SessionPrincipal? Validate(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            ClaimsPrincipal cp = new JwtSecurityTokenHandler().ValidateToken(bearer, parameters, out _);
            string sub = cp.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? cp.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            string email = cp.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "";
            string[] roles = cp.FindAll(RolesClaim).Select(c => c.Value).ToArray();
            if (roles.Length == 0) roles = new[] { "player" };
            return string.IsNullOrEmpty(sub) ? null : new SessionPrincipal(sub, email, roles);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }
}
