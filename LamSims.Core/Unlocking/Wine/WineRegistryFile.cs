using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="Text">The decoded string, or null for a value that is not text. Non-null for
/// str(2) and str(7) too — a ClientPath stored as REG_EXPAND_SZ read as "not a string" makes
/// detection find nothing, silently.</param>
/// <param name="Expandable">REG_EXPAND_SZ, whose %VAR% references the caller must expand.</param>
public sealed record WineRegistryValue(string? Text, bool Expandable);

/// <param name="PriorValue">
/// What was there before, so removal can put it back verbatim rather than guessing.
/// </param>
public sealed record WineRegistryWrite(bool CreatedBlock, string? PriorValue);

/// <summary>Wine's .reg format, read fully and written surgically. Line-oriented, never
/// parse-and-regenerate: this owns one value in one key, and rewriting 38 000 lines to change it
/// would put every other value at risk of a round-trip bug.</summary>
public static class WineRegistryFile
{
    public static WineRegistryValue? ReadValue(string path, string key, string name) =>
        ReadKey(path, key) is { } values && values.TryGetValue(name, out var value) ? value : null;

    /// <summary>Every value in the key, or null when it is absent. EVERY block with that name is
    /// merged, because duplicate blocks merge in Wine too (measured) — stopping at the first
    /// misses a value written the way <see cref="SetValueAsync"/> writes one.</summary>
    public static IReadOnlyDictionary<string, WineRegistryValue>? ReadKey(string path, string key)
    {
        try
        {
            return ReadKeyFromLines(File.ReadLines(path), key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>As <see cref="ReadKey"/>, from enumerable lines.</summary>
    public static IReadOnlyDictionary<string, WineRegistryValue>? ReadKeyFromLines(
        IEnumerable<string> lines, string key)
    {
        Dictionary<string, WineRegistryValue>? found = null;
        var wanted = UnescapeKey(key);
        var inKey = false;
        var continuing = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (continuing)
            {
                continuing = line.EndsWith('\\');
                continue;
            }

            if (line.Length > 0 && line[0] == '[')
            {
                var close = line.LastIndexOf(']');
                inKey = close > 1
                        && string.Equals(UnescapeKey(line[1..close]), wanted,
                                         StringComparison.OrdinalIgnoreCase);
                if (inKey)
                {
                    found ??= new Dictionary<string, WineRegistryValue>(
                        StringComparer.OrdinalIgnoreCase);
                }

                continue;
            }

            // `@=` is the key's DEFAULT value, and there are 29 773 of them. Read as a named
            // value it becomes an entry called "", which reports overrides nobody set.
            if (!inKey || line.Length == 0 || line[0] != '"')
            {
                continue;
            }

            var end = ClosingQuote(line, 1);
            if (end < 0 || end + 1 >= line.Length || line[end + 1] != '=') continue;

            var payload = line[(end + 2)..];
            found![Unescape(line[1..end])] = Parse(payload);
            continuing = payload.EndsWith('\\');
        }

        return found;
    }

    private static WineRegistryValue Parse(string payload)
    {
        if (payload.StartsWith('"'))
        {
            var end = ClosingQuote(payload, 1);
            return new WineRegistryValue(end < 0 ? null : Unescape(payload[1..end]), false);
        }

        foreach (var (prefix, expandable) in new[] { ("str(2):", true), ("str(7):", false) })
        {
            if (!payload.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = payload[prefix.Length..];
            if (!rest.StartsWith('"')) return new WineRegistryValue(null, expandable);

            var end = ClosingQuote(rest, 1);
            return new WineRegistryValue(end < 0 ? null : Unescape(rest[1..end]), expandable);
        }

        // dword:, hex:, hex(N): — present, but not text. The caller needs to know it exists (an
        // AppDefaults conflict is a conflict whatever its type) without being told it is a string.
        return new WineRegistryValue(null, false);
    }

    /// <summary>The closing quote, skipping escaped ones: a value containing \" would otherwise
    /// truncate at the escape.</summary>
    private static int ClosingQuote(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"') return i;
        }

        return -1;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { builder.Append(value[i]); continue; }

            switch (value[++i])
            {
                case 'x' when i + 4 < value.Length
                              && ushort.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    i += 4;
                    break;
                case '0': builder.Append('\0'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                default: builder.Append(value[i]); break;
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Key text carries only the doubled backslash, so it needs no character decoding.</summary>
    private static string UnescapeKey(string key) =>
        key.Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string EscapeKey(string key) =>
        key.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>The Windows-side variables a REG_EXPAND_SZ ClientPath can carry. The result is
    /// still a WINDOWS path. An unknown variable is left standing rather than blanked, so the
    /// resolver reports a path that does not exist rather than one resolving somewhere wrong.</summary>
    public static string Expand(string value, string windowsUserName)
    {
        if (!value.Contains('%')) return value;

        var profile = $@"C:\users\{windowsUserName}";

        var table = new (string Name, string Value)[]
        {
            ("%ProgramFiles(x86)%", @"C:\Program Files (x86)"),
            ("%ProgramFiles%", @"C:\Program Files"),
            ("%ProgramW6432%", @"C:\Program Files"),
            ("%ProgramData%", @"C:\ProgramData"),
            ("%CommonProgramFiles%", @"C:\Program Files\Common Files"),
            ("%SystemRoot%", @"C:\windows"),
            ("%windir%", @"C:\windows"),
            ("%SystemDrive%", "C:"),
            ("%LOCALAPPDATA%", $@"{profile}\AppData\Local"),
            ("%APPDATA%", $@"{profile}\AppData\Roaming"),
            ("%USERPROFILE%", profile),
        };

        foreach (var (name, replacement) in table)
            value = value.Replace(name, replacement, StringComparison.OrdinalIgnoreCase);

        return value;
    }

    /// <summary>Sets one value in one key, leaving every other byte as it was. Block present is
    /// the NORMAL case — a prefix from wineboot -i already carries an empty
    /// <c>[Software\\Wine\\DllOverrides]</c> block, so an append-only implementation works on no
    /// real prefix.</summary>
    public static async Task<WineRegistryWrite> SetValueAsync(string path, string key, string name,
                                                              string value, CancellationToken ct)
    {
        var (lines, tail) = Load(path);
        var wanted = UnescapeKey(key);
        var line = $"\"{Escape(name)}\"=\"{Escape(value)}\"";
        var prefix = $"\"{Escape(name)}\"=";

        var (keyIndex, blockEnd) = FindBlock(lines, wanted);

        if (keyIndex < 0)
        {
            lines.Add("");
            lines.Add($"[{EscapeKey(wanted)}] 0");
            lines.Add(line);

            // A file that ended without a newline gets one, because the value line is the last line
            // and Wine's own writer always terminates it.
            await AtomicFile.WriteAllTextAsync(path, Join(lines, tail.Length == 0 ? "\n" : tail), ct);
            return new WineRegistryWrite(CreatedBlock: true, PriorValue: null);
        }

        for (var i = keyIndex + 1; i < blockEnd; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var prior = Parse(trimmed[prefix.Length..]).Text;
            var endsWithBackslash = trimmed.EndsWith('\\');
            lines[i] = line;

            if (endsWithBackslash)
            {
                var j = i + 1;
                bool continueRemoving = true;
                while (j < blockEnd && continueRemoving)
                {
                    var continuationLine = lines[j].TrimEnd('\r');
                    continueRemoving = continuationLine.EndsWith('\\');
                    lines.RemoveAt(j);
                    blockEnd--;
                }
            }

            await AtomicFile.WriteAllTextAsync(path, Join(lines, tail), ct);
            return new WineRegistryWrite(CreatedBlock: false, PriorValue: prior);
        }

        // Immediately after #time=, which is where Wine itself puts the first value of a block. A
        // later Wine save is then a no-op on our line rather than moving it.
        var at = keyIndex + 1;
        if (at < blockEnd && lines[at].TrimEnd('\r').StartsWith("#time=", StringComparison.Ordinal))
            at++;

        lines.Insert(at, line);
        await AtomicFile.WriteAllTextAsync(path, Join(lines, tail), ct);
        return new WineRegistryWrite(CreatedBlock: false, PriorValue: null);
    }

    /// <param name="removeBlockIfEmpty">
    /// True only when this application created the block. Removing a block it did not create would
    /// delete a key Wine or the user established.
    /// </param>
    public static async Task RemoveValueAsync(string path, string key, string name,
                                              bool removeBlockIfEmpty, CancellationToken ct)
    {
        var (lines, tail) = Load(path);
        var wanted = UnescapeKey(key);
        var prefix = $"\"{Escape(name)}\"=";

        var (keyIndex, blockEnd) = FindBlock(lines, wanted);
        if (keyIndex < 0) return;

        var removed = false;
        for (var i = blockEnd - 1; i > keyIndex; i--)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var endsWithBackslash = trimmed.EndsWith('\\');
            lines.RemoveAt(i);
            blockEnd--;
            removed = true;

            if (endsWithBackslash)
            {
                bool continueRemoving = true;
                while (i < blockEnd && continueRemoving)
                {
                    var continuationLine = lines[i].TrimEnd('\r');
                    continueRemoving = continuationLine.EndsWith('\\');
                    lines.RemoveAt(i);
                    blockEnd--;
                }
            }
        }

        if (!removed) return;

        if (removeBlockIfEmpty && !HasValue(lines, keyIndex, blockEnd))
        {
            lines.RemoveRange(keyIndex, blockEnd - keyIndex);

            // The blank line this application wrote before the block it created goes with it.
            if (keyIndex > 0 && lines[keyIndex - 1].Length == 0) lines.RemoveAt(keyIndex - 1);
        }

        await AtomicFile.WriteAllTextAsync(path, Join(lines, tail), ct);
    }

    private static bool HasValue(List<string> lines, int keyIndex, int blockEnd)
    {
        for (var i = keyIndex + 1; i < blockEnd; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '"' || line.StartsWith("@=", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>The first block with this name, and where it ends. The FIRST, because Wine merges
    /// duplicates into one and that is where the canonical form keeps our line.</summary>
    private static (int KeyIndex, int BlockEnd) FindBlock(List<string> lines, string wanted)
    {
        var keyIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line[0] != '[') continue;

            var close = line.LastIndexOf(']');
            if (close < 1) continue;

            if (keyIndex >= 0) return (keyIndex, i);

            if (string.Equals(UnescapeKey(line[1..close]), wanted, StringComparison.OrdinalIgnoreCase))
                keyIndex = i;
        }

        return (keyIndex, lines.Count);
    }

    /// <summary>Lines plus the exact trailing newlines, so a file we only inserted into comes back
    /// byte-identical when the insertion is undone.</summary>
    private static (List<string> Lines, string Tail) Load(string path)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var body = text.TrimEnd('\n');

        return (body.Length == 0 ? [] : new List<string>(body.Split('\n')), text[body.Length..]);
    }

    private static string Join(List<string> lines, string tail) => string.Join("\n", lines) + tail;
}
