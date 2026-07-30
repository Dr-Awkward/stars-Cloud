// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>
/// A player account (Firestore <c>users/{google_sub}</c>, the LOCKED account
/// document id, design Section C.2). Google is the only identity provider; the
/// document id is the Google subject claim, so there is one canonical record per
/// Google identity.
/// </summary>
[FirestoreData]
public sealed class UserAccount
{
    /// <summary>The Google subject claim (sub). This is the document id.</summary>
    [FirestoreProperty] public string GoogleSub { get; set; } = "";

    [FirestoreProperty] public string Email { get; set; } = "";

    [FirestoreProperty] public string DisplayName { get; set; } = "";

    [FirestoreProperty] public string? AvatarUrl { get; set; }

    /// <summary>Roles: "player" (default), "moderator", "admin" (design Section C.3/C.6).</summary>
    [FirestoreProperty] public List<string> Roles { get; set; } = new() { "player" };

    [FirestoreProperty] public Timestamp? CreatedAt { get; set; }

    /// <summary>Set when the account is soft-deleted (design Section C.5); null while active.</summary>
    [FirestoreProperty] public Timestamp? DeletedAt { get; set; }

    /// <summary>
    /// The current refresh-token chain id. Rotating the refresh token advances
    /// this; /auth/logout clears it, revoking the chain (design Section C.1).
    /// </summary>
    [FirestoreProperty] public string? RefreshChainId { get; set; }

    public bool IsDeleted => DeletedAt != null;
    public bool HasRole(string role) => Roles.Contains(role);
}
