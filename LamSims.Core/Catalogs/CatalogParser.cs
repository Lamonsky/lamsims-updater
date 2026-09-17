using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;

namespace LamSims.Core.Catalogs;

/// <summary>The catalog as a whole is unusable: bad JSON, or a version this build cannot read.</summary>
public sealed class CatalogFormatException : Exception
{
    public CatalogFormatException(string message) : base(message) { }
}

public static class CatalogParser
{
    /// <summary>
    /// Reads a catalog, keeping every entry that validates and naming every entry that does
    /// not. A single bad entry must not cost the user the other hundred.
    /// </summary>
    public static CatalogLoadResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new CatalogFormatException($"The catalog is not valid JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CatalogFormatException("The catalog's root must be a JSON object.");

            if (!root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion))
            {
                throw new CatalogFormatException("The catalog has no numeric 'schemaVersion'.");
            }

            if (schemaVersion != Catalog.SupportedSchemaVersion)
                throw new CatalogFormatException(
                    $"The catalog declares schemaVersion {schemaVersion}; this build reads " +
                    $"version {Catalog.SupportedSchemaVersion}.");

            if (!root.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Array)
                throw new CatalogFormatException("The catalog has no 'packs' array.");

            var entries = new List<PackEntry>();
            var rejected = new List<RejectedEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;

            foreach (var element in packs.EnumerateArray())
            {
                position++;
                var description = Describe(element, position);

                try
                {
                    var entry = ReadEntry(element);

                    if (!seen.Add(entry.Code))
                    {
                        rejected.Add(new RejectedEntry(description, $"Code '{entry.Code}' appears more than once."));
                        continue;
                    }

                    entries.Add(entry);
                }
                catch (InvalidEntryException e)
                {
                    rejected.Add(new RejectedEntry(description, e.Message));
                }
            }

            return new CatalogLoadResult(new Catalog(schemaVersion, ReadUpdated(root), entries), rejected);
        }
    }

    private static DateTimeOffset? ReadUpdated(JsonElement root) =>
        root.TryGetProperty("updatedUtc", out var element)
        && element.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>A rejection message has to identify the entry, and a broken entry may have no code.</summary>
    private static string Describe(JsonElement element, int position) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(code.GetString())
            ? $"entry {position} ('{code.GetString()}')"
            : $"entry {position}";

    private static PackEntry ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidEntryException("The entry is not a JSON object.");

        var code = RequiredString(element, "code");

        // The two separator checks are redundant TODAY: HasInvalidCharacter rejects '/' and '\\'
        // on every platform, not only the running one. They are kept because they are the
        // traversal guard stated where a reader looks for it, and because the cost of the
        // redundancy is two comparisons against a short string while the cost of their absence,
        // if that helper is ever narrowed to the platform's own separator, is a write outside the
        // download directory.
        if (FileNameRules.HasInvalidCharacter(code)
            || code.Contains(Path.DirectorySeparatorChar)
            || code.Contains(Path.AltDirectorySeparatorChar)
            || code is "." or ".."
            || FileNameRules.IsReservedDeviceName(code)
            || FileNameRules.HasTrailingDotOrSpace(code))
        {
            // The code names files on disk: path characters allow traversal outside the
            // download directory, a reserved device name redirects the write to hardware
            // instead of a file, and a trailing dot or space collides with the same code
            // stripped of it once Windows normalizes the path.
            throw new InvalidEntryException($"The 'code' value '{code}' is not a usable file name.");
        }

        var name = RequiredString(element, "name");
        var sha256 = RequiredString(element, "sha256");

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidEntryException("The 'sha256' value is not a 64-character hexadecimal digest.");

        if (!element.TryGetProperty("size", out var sizeElement)
            || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size)
            || size <= 0)
        {
            throw new InvalidEntryException("The 'size' value is not a positive number of bytes.");
        }

        long? installedSize = null;
        if (element.TryGetProperty("installedSize", out var installedElement))
        {
            if (installedElement.ValueKind != JsonValueKind.Number
                || !installedElement.TryGetInt64(out var value) || value <= 0)
            {
                throw new InvalidEntryException("The 'installedSize' value is not a positive number of bytes.");
            }

            installedSize = value;
        }

        return new PackEntry(code, name, ReadType(element), size, installedSize, sha256,
            ReadUrls(element), ReadInstallDirs(element, code));
    }

    private static PackType ReadType(JsonElement element) =>
        element.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && Enum.TryParse<PackType>(type.GetString(), ignoreCase: true, out var parsed)
        // TryParse also accepts the numeric form of an enum, so "7" would otherwise cross the
        // public API as a PackType no switch in the application has a case for.
        && Enum.IsDefined(parsed)
            ? parsed
            : PackType.Unknown;

    private static IReadOnlyList<Uri> ReadUrls(JsonElement element)
    {
        if (!element.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            throw new InvalidEntryException("The entry has no 'urls' array.");

        var mirrors = new List<Uri>();

        foreach (var candidate in urls.EnumerateArray())
        {
            var text = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidEntryException($"The url '{text}' is not an absolute http or https address.");
            }

            mirrors.Add(uri);
        }

        if (mirrors.Count == 0)
            throw new InvalidEntryException("The entry lists no url.");

        return mirrors;
    }

    private static IReadOnlyList<string> ReadInstallDirs(JsonElement element, string code)
    {
        if (!element.TryGetProperty("installDirs", out var dirs) || dirs.ValueKind != JsonValueKind.Array)
            return new[] { code };

        var names = new List<string>();

        foreach (var candidate in dirs.EnumerateArray())
        {
            var name = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || FileNameRules.HasInvalidCharacter(name)
                || name is "." or ".."
                || FileNameRules.IsReservedDeviceName(name)
                || FileNameRules.HasTrailingDotOrSpace(name))
            {
                throw new InvalidEntryException($"The installDirs value '{name}' is not a plain directory name.");
            }

            names.Add(name);
        }

        return names.Count == 0 ? new[] { code } : names.ToArray();
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidEntryException($"The entry has no '{property}' value.");
        }

        return value.GetString()!;
    }

    /// <summary>Carries one entry's rejection reason to the loop that records it.</summary>
    private sealed class InvalidEntryException : Exception
    {
        public InvalidEntryException(string message) : base(message) { }
    }
}
