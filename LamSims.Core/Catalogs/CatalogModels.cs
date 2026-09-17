using System;
using System.Collections.Generic;
using System.Linq;
using LamSims.Core.Downloading;

namespace LamSims.Core.Catalogs;

/// <summary>
/// The pack kinds the catalog names. <see cref="Unknown"/> carries an entry whose
/// <c>type</c> is absent or unrecognised: the field is not among the catalog's required
/// ones, so it must never be the reason a downloadable pack disappears from the list.
/// </summary>
public enum PackType { Unknown, Expansion, Game, Stuff, Kit }

public sealed record PackEntry(
    string Code,
    string Name,
    PackType Type,
    long Size,
    long? InstalledSize,
    string Sha256,
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<string> InstallDirs)
{
    /// <summary>What the game directory must have free, falling back to the archive size.</summary>
    public long RequiredInstallBytes => InstalledSize ?? Size;

    public DownloadRequest ToDownloadRequest() => new(Code, Urls, Size, Sha256);
}

public sealed record Catalog(int SchemaVersion, DateTimeOffset? UpdatedUtc, IReadOnlyList<PackEntry> Packs)
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// Every pack code, compared case-insensitively to match <see cref="CatalogParser"/>'s
    /// own de-duplication. A caller building its own set from <see cref="Packs"/> could pick
    /// an ordinal comparer that disagrees with the parser: an orphan cleanup, for instance,
    /// would then delete a partial download whose on-disk code differs from the catalog only
    /// in case.
    /// </summary>
    public IReadOnlySet<string> KnownCodes => new HashSet<string>(Packs.Select(p => p.Code), StringComparer.OrdinalIgnoreCase);
}

/// <summary>An entry that did not load, named well enough for a user to find it.</summary>
public sealed record RejectedEntry(string Description, string Reason);

public sealed record CatalogLoadResult(Catalog Catalog, IReadOnlyList<RejectedEntry> Rejected);
