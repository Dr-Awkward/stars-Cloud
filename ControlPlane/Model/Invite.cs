// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>Where an invite is in its short life.</summary>
public enum InviteStatus
{
    Pending,   // sent, not yet accepted
    Accepted,  // the invited email signed in and took a seat
    Revoked,   // the host withdrew it
}

/// <summary>
/// An invitation to a private game (Firestore <c>invites/{inviteId}</c>, design
/// Section G.1 "invite friends by Gmail address"). The invite is keyed to an email
/// address because that is all the host knows; it binds to a Google subject only
/// when the invited person actually signs in and accepts, which is the moment the
/// identity becomes real.
///
/// The token is an opaque random string, not a signed claim. It is a capability:
/// whoever holds it can present it, but accepting still requires a session whose
/// verified email matches <see cref="InvitedEmail"/>, so a leaked link alone does
/// not let a stranger into a private game.
/// </summary>
[FirestoreData]
public sealed class Invite
{
    /// <summary>The document id.</summary>
    [FirestoreProperty] public string InviteId { get; set; } = "";

    [FirestoreProperty] public string GameId { get; set; } = "";

    /// <summary>The invited Gmail address, lower cased for comparison.</summary>
    [FirestoreProperty] public string InvitedEmail { get; set; } = "";

    /// <summary>Opaque random capability string carried in the accept URL.</summary>
    [FirestoreProperty] public string Token { get; set; } = "";

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<InviteStatus>))]
    public InviteStatus Status { get; set; } = InviteStatus.Pending;

    /// <summary>The host account that issued this invite.</summary>
    [FirestoreProperty] public string CreatedByAccountId { get; set; } = "";

    /// <summary>The Google subject that accepted, once someone has.</summary>
    [FirestoreProperty] public string? AcceptedByAccountId { get; set; }

    /// <summary>The seat the acceptance bound, once bound.</summary>
    [FirestoreProperty] public int? EmpireId { get; set; }

    [FirestoreProperty] public Timestamp? CreatedAt { get; set; }
    [FirestoreProperty] public Timestamp? AcceptedAt { get; set; }
    [FirestoreProperty] public Timestamp? RevokedAt { get; set; }
}
