// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.IO.Compression;
using System.Text;
using System.Xml;

using Nova.Common;

/// <summary>
/// Carries engine-native XML inside the JSON contract, using the same envelope
/// convention the player-facing API already uses for intel and orders (design
/// decision 8).
///
/// Intel and orders XML compress well (they are repetitive markup over a large
/// object graph), so gzip plus base64 keeps dispatch bodies small enough to sit
/// inline in a Cloud Tasks payload rather than forcing every dispatch through a
/// second storage round trip.
/// </summary>
public static class Envelope
{
    public const string IntelContentType = "application/vnd.nova.intel+xml";
    public const string OrdersContentType = "application/vnd.nova.orders+xml";
    public const string GzipBase64 = "gzip+base64";
    public const string Identity = "identity";

    /// <summary>Compress and base64 a UTF-8 string.</summary>
    public static string Pack(string xml)
    {
        byte[] raw = Encoding.UTF8.GetBytes(xml);
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Reverse of <see cref="Pack"/>.</summary>
    public static string Unpack(string base64)
    {
        byte[] compressed = Convert.FromBase64String(base64);
        using MemoryStream input = new(compressed);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// Decode a payload according to its declared encoding. Unknown encodings
    /// throw rather than guess, because silently misreading a seat's intel is
    /// worse than failing the dispatch and falling back to held orders.
    /// </summary>
    public static string Decode(NativePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload.Encoding switch
        {
            GzipBase64 => Unpack(payload.Body),
            Identity => payload.Body,
            _ => throw new NotSupportedException(
                $"Unsupported payload encoding '{payload.Encoding}'."),
        };
    }

    /// <summary>Wrap raw intel XML for transport.</summary>
    public static NativePayload PackIntel(string intelXml) => new()
    {
        ContentType = IntelContentType,
        Encoding = GzipBase64,
        Body = Pack(intelXml),
    };

    /// <summary>
    /// Rebuild a real <see cref="Intel"/> from a native payload.
    ///
    /// This is the lossless path a first-party C# participant takes. It reuses
    /// the engine's own Intel(XmlDocument) constructor, so the object graph the
    /// AI plays against is byte-identical to what the turn generator wrote, with
    /// no hand-written projection sitting in the middle quietly dropping fields.
    /// </summary>
    public static Intel ReadIntel(NativePayload payload)
    {
        string xml = Decode(payload);
        XmlDocument doc = new();
        doc.LoadXml(xml);
        return new Intel(doc);
    }

    /// <summary>Serialize an <see cref="Intel"/> back to its XML form.</summary>
    public static string WriteIntel(Intel intel)
    {
        ArgumentNullException.ThrowIfNull(intel);

        XmlDocument doc = new();
        XmlDeclaration declaration = doc.CreateXmlDeclaration("1.0", "utf-8", null);
        doc.AppendChild(declaration);
        doc.AppendChild(intel.ToXml(doc));

        using StringWriter writer = new();
        using (XmlTextWriter xmlWriter = new(writer))
        {
            xmlWriter.Formatting = Formatting.Indented;
            doc.WriteTo(xmlWriter);
        }

        return writer.ToString();
    }
}

/// <summary>
/// Formats the per-seat deterministic seed the contract carries.
///
/// The numeric seed comes from the engine's own FNV-1a derivation over
/// (MasterSeed, turnYear, empireId); this only decides how it is written on the
/// wire. Hex, lower case, no prefix, so it round trips exactly and reads the way
/// the contract examples show it.
/// </summary>
public static class SeatSeed
{
    /// <summary>Derive and format the seat seed for a dispatch.</summary>
    public static string For(long masterSeed, int turnYear, int empireId)
        => Format(SeedDerivation.ForSeat(masterSeed, turnYear, empireId));

    /// <summary>Format an already-derived seed.</summary>
    public static string Format(int seed) => seed.ToString("x8");

    /// <summary>
    /// Parse a seat seed back to the integer a participant seeds its RNG with.
    /// An unparseable seed yields zero rather than throwing: a participant that
    /// was handed a malformed seed should still play, just not reproducibly.
    /// </summary>
    public static int Parse(string? seed)
        => int.TryParse(seed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
}
