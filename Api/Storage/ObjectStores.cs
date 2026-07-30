// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Google.Cloud.Storage.V1;

namespace Galaxies.Api.Storage;

/// <summary>
/// The API's view of the private GCS buckets (design Section B.3). Orders are
/// written here by the API and read by turngen; intel is written by turngen and
/// served here, per empire, only after the ownership check (R5). Nothing is ever a
/// public object. Object paths follow the turngen/host scheme
/// games/{gameId}/{orders|intel}/{turnYear}/{empireId}.{ext}, resolving the
/// path drift the specs flagged toward one canonical layout.
/// </summary>
public interface IOrdersStore
{
    Task PutAsync(string gameId, int turnYear, int empireId, byte[] ordersXml, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default);
    Task DeleteAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default);

    /// <summary>
    /// The gs:// uris of every object this empire owns in this game. The DSAR export
    /// returns these as references rather than inlining the bodies (design Section
    /// C.5): a long game's orders history is tens of megabytes, and a synchronous
    /// export endpoint that streams it would time out and cost more than it informs.
    /// </summary>
    Task<IReadOnlyList<string>> ListForEmpireAsync(string gameId, int empireId, CancellationToken ct = default);
}

public interface IIntelStore
{
    Task<byte[]?> GetAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default);

    /// <summary>The gs:// uris of every intel object this empire owns; see IOrdersStore.</summary>
    Task<IReadOnlyList<string>> ListForEmpireAsync(string gameId, int empireId, CancellationToken ct = default);
}

public sealed class GcsBucketOptions
{
    public required string OrdersBucket { get; init; }
    public required string IntelBucket { get; init; }
}

public sealed class GcsOrdersStore(StorageClient storage, GcsBucketOptions options) : IOrdersStore
{
    private static string Path(string gameId, int turnYear, int empireId)
        => $"games/{gameId}/orders/{turnYear}/{empireId}.orders";

    public async Task PutAsync(string gameId, int turnYear, int empireId, byte[] ordersXml, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(ordersXml);
        await storage.UploadObjectAsync(options.OrdersBucket, Path(gameId, turnYear, empireId),
            "application/xml", ms, cancellationToken: ct);
    }

    public async Task<byte[]?> GetAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            await storage.DownloadObjectAsync(options.OrdersBucket, Path(gameId, turnYear, empireId), ms, cancellationToken: ct);
            return ms.ToArray();
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default)
    {
        try
        {
            await storage.DeleteObjectAsync(options.OrdersBucket, Path(gameId, turnYear, empireId), cancellationToken: ct);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public Task<IReadOnlyList<string>> ListForEmpireAsync(string gameId, int empireId, CancellationToken ct = default)
        => ObjectListing.ListAsync(storage, options.OrdersBucket, $"games/{gameId}/orders/", $"/{empireId}.orders", ct);
}

public sealed class GcsIntelStore(StorageClient storage, GcsBucketOptions options) : IIntelStore
{
    private static string Path(string gameId, int turnYear, int empireId)
        => $"games/{gameId}/intel/{turnYear}/{empireId}.intel";

    public async Task<byte[]?> GetAsync(string gameId, int turnYear, int empireId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            await storage.DownloadObjectAsync(options.IntelBucket, Path(gameId, turnYear, empireId), ms, cancellationToken: ct);
            return ms.ToArray();
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<string>> ListForEmpireAsync(string gameId, int empireId, CancellationToken ct = default)
        => ObjectListing.ListAsync(storage, options.IntelBucket, $"games/{gameId}/intel/", $"/{empireId}.intel", ct);
}

/// <summary>
/// Prefix listing shared by both stores. The object layout puts the turn year
/// between the prefix and the empire id, so GCS cannot filter by empire on the
/// server; we list the game's whole folder and filter the suffix here. That is
/// acceptable because the only caller is the DSAR export, which is rare and
/// per account, not a hot path.
/// </summary>
internal static class ObjectListing
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        StorageClient storage, string bucket, string prefix, string suffix, CancellationToken ct)
    {
        var uris = new List<string>();
        await foreach (Google.Apis.Storage.v1.Data.Object o in storage.ListObjectsAsync(bucket, prefix).WithCancellation(ct))
        {
            if (o.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                uris.Add($"gs://{bucket}/{o.Name}");
            }
        }
        uris.Sort(StringComparer.Ordinal);
        return uris;
    }
}
