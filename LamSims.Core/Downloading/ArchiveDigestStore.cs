using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace LamSims.Core.Downloading;

/// <summary>
/// What a verified archive looked like at the moment its digest was checked. The
/// <see cref="Length"/>/<see cref="LastWriteTimeUtc"/> pair binds the record to a file
/// <em>snapshot</em> rather than to a name, so a same-name file swapped in underneath forces a
/// re-hash instead of inheriting the verification. A tool that replaces the content while
/// preserving both defeats the check; the threat model here is accident, not an adversary who
/// already holds the user's own write permissions.
///
/// FAT and exFAT store timestamps in local time rather than UTC, so a daylight-saving
/// transition shifts every file's reported <see cref="LastWriteTimeUtc"/> on such a volume and
/// invalidates every record's mtime match at once. That costs one re-hash per pack, once,
/// after the transition.
/// </summary>
public sealed record ArchiveDigest(
    int SchemaVersion,
    string Code,
    string Sha256,
    long Length,
    DateTimeOffset LastWriteTimeUtc)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Persists one digest record per archive, beside the archive. Writes are atomic and durable.
/// Reads are total: absent, torn, unparseable, future-versioned or naming another pack all
/// read as null, and null costs one re-hash rather than a re-download.
/// </summary>
public sealed class ArchiveDigestStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Every field nullable, so an absent one is distinguishable from a present one holding
    /// the default. A record with no <c>length</c> would otherwise claim length zero and match
    /// nothing, or match an empty file.
    /// </summary>
    private sealed record DigestDocument(
        int? SchemaVersion,
        string? Code,
        string? Sha256,
        long? Length,
        DateTimeOffset? LastWriteTimeUtc);

    private readonly DownloadPaths _paths;

    public ArchiveDigestStore(DownloadPaths paths) => _paths = paths;

    public ArchiveDigest? TryLoad(string code)
    {
        try
        {
            var path = _paths.ArchiveDigestFile(code);
            if (!File.Exists(path)) return null;

            var document = JsonSerializer.Deserialize<DigestDocument>(File.ReadAllText(path), Format);

            if (document is null
                || document.SchemaVersion != ArchiveDigest.CurrentSchemaVersion
                || !string.Equals(document.Code, code, StringComparison.OrdinalIgnoreCase)
                || !Hex.IsSha256(document.Sha256)
                || document.Length is null or < 0
                || document.LastWriteTimeUtc is null)
            {
                return null;
            }

            return new ArchiveDigest(
                document.SchemaVersion.Value, document.Code!, document.Sha256!,
                document.Length.Value, document.LastWriteTimeUtc.Value);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ArchiveDigest record, CancellationToken ct)
    {
        _paths.EnsureCreated();
        await AtomicFile.WriteAllTextAsync(
            _paths.ArchiveDigestFile(record.Code), JsonSerializer.Serialize(record, Format), ct);
    }

    public void Delete(string code)
    {
        try
        {
            File.Delete(_paths.ArchiveDigestFile(code));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>
    /// Snapshots an archive just verified and saves the record, swallowing the write's own
    /// failure. Best-effort and uncancellable: the bytes are verified either way, and an
    /// archive with no record costs one re-hash next time rather than a re-download, so a
    /// racing cancel must not lose the record while keeping the archive. Silent no-op when the
    /// file cannot be statted, which the callers already treat as "no record."
    /// </summary>
    public async Task RecordAsync(string code, string archivePath, string sha256)
    {
        var record = Describe(code, archivePath, sha256);
        if (record is null) return;

        try
        {
            await SaveAsync(record, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Snapshots an archive that has just been verified. Null when the file cannot be statted,
    /// which the callers treat as "no record".
    /// </summary>
    public static ArchiveDigest? Describe(string code, string archivePath, string sha256)
    {
        try
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists) return null;

            return new ArchiveDigest(
                ArchiveDigest.CurrentSchemaVersion, code, sha256, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
