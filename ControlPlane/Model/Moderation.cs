// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

using Google.Cloud.Firestore;

namespace Galaxies.ControlPlane.Model;

/// <summary>Where an abuse report sits in the moderation queue.</summary>
public enum ReportStatus
{
    Open,
    Resolved,
}

/// <summary>
/// A player's abuse report (Firestore <c>reports/{reportId}</c>, design Section
/// G.1 "moderation console"). Any authenticated player may file one; only a
/// moderator may read the queue or resolve an entry. The report records who filed
/// it, because a reporter who files hundreds is itself a signal.
/// </summary>
[FirestoreData]
public sealed class AbuseReport
{
    [FirestoreProperty] public string ReportId { get; set; } = "";

    /// <summary>The Google subject of the reporter.</summary>
    [FirestoreProperty] public string ReporterAccountId { get; set; } = "";

    /// <summary>What is being reported: "user", "game", or "message".</summary>
    [FirestoreProperty] public string TargetType { get; set; } = "";

    /// <summary>The id of the reported thing, in its own namespace.</summary>
    [FirestoreProperty] public string TargetId { get; set; } = "";

    [FirestoreProperty] public string Reason { get; set; } = "";

    [FirestoreProperty(ConverterType = typeof(FirestoreEnumNameConverter<ReportStatus>))]
    public ReportStatus Status { get; set; } = ReportStatus.Open;

    /// <summary>What the moderator decided, free text, written on resolve.</summary>
    [FirestoreProperty] public string? Resolution { get; set; }

    [FirestoreProperty] public string? ResolvedByAccountId { get; set; }

    [FirestoreProperty] public Timestamp? CreatedAt { get; set; }
    [FirestoreProperty] public Timestamp? ResolvedAt { get; set; }
}

/// <summary>
/// A ban (Firestore <c>bans/{google_sub}</c>). The document id is the banned
/// account's Google subject, so the session path can check for it with a single
/// point read. Its existence is the ban; removing the document lifts it.
/// </summary>
[FirestoreData]
public sealed class Ban
{
    [FirestoreProperty] public string GoogleSub { get; set; } = "";
    [FirestoreProperty] public string Reason { get; set; } = "";

    /// <summary>The moderator or admin who imposed it.</summary>
    [FirestoreProperty] public string BannedByAccountId { get; set; } = "";

    [FirestoreProperty] public Timestamp? CreatedAt { get; set; }
}
