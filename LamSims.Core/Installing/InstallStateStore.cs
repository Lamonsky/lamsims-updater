using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LamSims.Core.Installing;

public enum InstallMarkerStatus { Installing, Installed }

/// <summary>One pack's journal entry. <c>Installing</c> is written durably before the first
/// mutation and rewritten only after the last entry lands, so a marker still reading it is
/// positive evidence of an interruption, not an inference from what is missing.</summary>
public sealed record InstallMarker(
    int SchemaVersion,
    string Code,
    string GameDirectory,
    string ArchiveSha256,
    InstallMarkerStatus Status,
    DateTimeOffset UpdatedUtc)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One JSON file per pack code, grouped by game directory. Writes are atomic and durable; reads
/// are total — an absent, torn, unparseable or wrong-code marker is null, so a corrupt one degrades
/// a pack to "installed, unverified" and can never accuse a healthy install of being interrupted.
///
/// Grouped by game directory so pointing the setting at a second drive does not demote the first.
/// Nothing reaps a group: the only available rules would delete the state of a second drive or an
/// unmounted share, and a group costs a few hundred bytes.
/// </summary>
public sealed class InstallStateStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Every field nullable, so an absent one is distinguishable from a defaulted one:
    /// deserializing straight into <see cref="InstallMarker"/> reads a missing <c>status</c> as
    /// Installing, and a truncated write would report a complete install as interrupted.</summary>
    private sealed record MarkerDocument(
        int? SchemaVersion,
        string? Code,
        string? GameDirectory,
        string? ArchiveSha256,
        InstallMarkerStatus? Status,
        DateTimeOffset? UpdatedUtc);

    public string Root { get; }

    public InstallStateStore(string directory) => Root = directory;

    public string MarkerFile(string gameDirectory, string code) =>
        Path.Combine(GroupDirectory(gameDirectory), ValidateCode(code).ToLowerInvariant() + ".json");

    public InstallMarker? TryLoad(string gameDirectory, string code)
    {
        try
        {
            return Read(MarkerFile(gameDirectory, code));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every marker for one game directory, keyed case-insensitively by its own
    /// <see cref="InstallMarker.Code"/> as <see cref="Catalogs.CatalogParser"/> does. Duplicates
    /// resolve to the greater UpdatedUtc, so the winner does not depend on listing order.</summary>
    public IReadOnlyDictionary<string, InstallMarker> LoadAll(string gameDirectory)
    {
        var markers = new Dictionary<string, InstallMarker>(StringComparer.OrdinalIgnoreCase);

        string group;
        try
        {
            group = GroupDirectory(gameDirectory);
        }
        catch (ArgumentException)
        {
            return markers;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(group))
            {
                // Filtered by suffix rather than by a search pattern: AtomicFile leaves
                // '<name>.json.<random>.tmp' beside a marker when a write is killed, and
                // parsing that would count one pack twice.
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var marker = Read(file);
                if (marker is null) continue;

                if (!markers.TryGetValue(marker.Code, out var existing) || marker.UpdatedUtc > existing.UpdatedUtc)
                    markers[marker.Code] = marker;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A missing group directory is the ordinary case for a game directory nothing has
            // been installed into. A fault partway through enumeration keeps whatever markers
            // were already read, which can only leave a pack unverified rather than accuse a
            // healthy one of being interrupted.
        }

        return markers;
    }

    /// <summary>Writes one marker. Throws on I/O failure, because the two callers want opposite
    /// things: a failed intent write must fail the install, a failed completion write must
    /// not.</summary>
    public async Task SaveAsync(InstallMarker marker, CancellationToken ct)
    {
        var path = MarkerFile(marker.GameDirectory, marker.Code);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(marker, Format), ct);
    }

    private string GroupDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new ArgumentException("Game directory must not be blank.", nameof(gameDirectory));

        return Path.Combine(Root, PathIdentity.DirectoryKey(gameDirectory));
    }

    private static InstallMarker? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var document = JsonSerializer.Deserialize<MarkerDocument>(File.ReadAllText(path), Format);

            if (document is null
                || document.SchemaVersion != InstallMarker.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(document.Code)
                || string.IsNullOrWhiteSpace(document.GameDirectory)
                || !Hex.IsSha256(document.ArchiveSha256)
                || document.Status is null
                || document.UpdatedUtc is null)
            {
                return null;
            }

            return new InstallMarker(
                document.SchemaVersion.Value, document.Code, document.GameDirectory,
                document.ArchiveSha256!, document.Status.Value, document.UpdatedUtc.Value);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            // JsonException also covers an unrecognised 'status' string: the converter rejects
            // it rather than picking a case, which is what keeps an unknown value from
            // becoming a claim about the install.
            return null;
        }
    }

    private static string ValidateCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Pack code must not be blank.", nameof(code));
        if (code.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Pack code '{code}' contains path characters.", nameof(code));

        return code;
    }
}
