using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace LamSims.Core.Unlocking;

/// <summary>
/// Adds or removes one whole <c>key=value</c> line in an ini file that belongs to somebody else.
/// Reads bytes, keeps the byte-order mark and the dominant newline sequence, edits whole lines, and
/// writes through <see cref="AtomicFile.WriteAllBytesAsync"/>.
///
///
/// Assumes the file EA Desktop writes: UTF-8, and newlines that are either "\n" or "\r\n". None of
/// the consequences below is reachable for machine.ini, but all are real for an arbitrary ini file,
/// so do not reuse this for one without revisiting them:
///   - a file of MIXED endings is normalised to whichever dominates;
///   - a lone "\r" used as a line ending is dropped rather than preserved, because lines are split on
///     "\n" and their trailing "\r" trimmed, and restoring it would need a newline-aware tokenizer
///     rather than a split;
///   - a UTF-16 byte-order mark is not recognised, so such a file would be mis-decoded as UTF-8.
/// </summary>
public static class IniFlagEditor
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static Task<bool> AddFlagAsync(string path, string flag, CancellationToken ct) =>
        EditAsync(path, flag, add: true, ct);

    public static Task<bool> RemoveFlagAsync(string path, string flag, CancellationToken ct) =>
        EditAsync(path, flag, add: false, ct);

    private static async Task<bool> EditAsync(string path, string flag, bool add, CancellationToken ct)
    {
        // A missing file is the normal case on a machine where EA Desktop has never run, and the
        // step is non-fatal.
        if (!File.Exists(path)) return false;

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var hasBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Bom);
        var body = Encoding.UTF8.GetString(hasBom ? bytes.AsSpan(3) : bytes);

        // Whichever ending dominates is the one new lines get, so a CRLF file stays CRLF.
        var crlf = CountOf(body, "\r\n");
        var newline = crlf > 0 && crlf >= CountOf(body, "\n") - crlf ? "\r\n" : "\n";

        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var trailing = body.Length > 0 && (body.EndsWith('\n'));
        if (trailing) lines.RemoveAt(lines.Count - 1);

        var present = lines.Any(l => string.Equals(l.Trim(), flag, StringComparison.Ordinal));

        if (add == present) return false;
        if (add) lines.Add(flag);
        else lines.RemoveAll(l => string.Equals(l.Trim(), flag, StringComparison.Ordinal));

        var rebuilt = string.Join(newline, lines) + (trailing ? newline : "");
        var payload = (hasBom ? Bom : []).Concat(Encoding.UTF8.GetBytes(rebuilt)).ToArray();

        await AtomicFile.WriteAllBytesAsync(path, payload, ct);
        return true;
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
