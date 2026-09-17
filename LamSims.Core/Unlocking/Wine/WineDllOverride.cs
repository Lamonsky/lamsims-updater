using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Settings;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="PrefixPath">In full, so a hashed-key collision cannot make a record vouch for
/// another prefix.</param>
/// <param name="WroteRegistry">Whether this application wrote the entry. Removal branches on THIS,
/// never on what a launcher config now says.</param>
/// <param name="PriorValue">Written back verbatim on removal, or null to remove the line.</param>
/// <param name="CreatedBlock">Whether the key block itself was appended, and so may be removed.</param>
/// <param name="SkippedBecause">Why nothing was written, when nothing was.</param>
public sealed record WineOverrideRecord(string PrefixPath, bool WroteRegistry, string? PriorValue,
                                        bool CreatedBlock, string? SkippedBecause);

/// <summary>One record per PREFIX, not per client: one <c>"*version"</c> entry in one user.reg
/// serves every client in that prefix.</summary>
public sealed class WineOverrideStore(AppPaths paths)
{
    private string FileFor(string prefixPath) =>
        Path.Combine(paths.WineOverrideDirectory, $"{PathIdentity.DirectoryKey(prefixPath)}.json");

    public WineOverrideRecord? Read(string prefixPath)
    {
        var record = ReadFile(FileFor(prefixPath));

        // A hashed key is not proof. A record naming a different prefix is treated as absent rather
        // than trusted, so a collision cannot make one prefix's undo run against another's.
        return record is not null
               && PathIdentity.Canonical(record.PrefixPath) == PathIdentity.Canonical(prefixPath)
            ? record
            : null;
    }

    /// <summary>Reports rather than throws. A record that cannot be saved after the registry was
    /// written is the one unrecoverable loss: the override is in place with nothing recording what
    /// it replaced.</summary>
    /// <returns>Null on success; the reason otherwise.</returns>
    public async Task<string?> WriteAsync(WineOverrideRecord record, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(paths.WineOverrideDirectory);
            await AtomicFile.WriteAllTextAsync(FileFor(record.PrefixPath),
                                              JsonSerializer.Serialize(record), ct);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    public void Delete(string prefixPath)
    {
        try
        {
            var file = FileFor(prefixPath);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be deleted leaves a stale claim, which the next sweep reports.
            // It must not fail a removal that has already put the prefix right.
        }
    }

    public IReadOnlyList<WineOverrideRecord> All()
    {
        var found = new List<WineOverrideRecord>();

        try
        {
            if (!Directory.Exists(paths.WineOverrideDirectory)) return found;

            foreach (var file in Directory.EnumerateFiles(paths.WineOverrideDirectory, "*.json"))
                if (ReadFile(file) is { } record) found.Add(record);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable store sweeps nothing, which is the safe direction: it leaves records in
            // place rather than deleting ones it could not read.
        }

        return found;
    }

    private static WineOverrideRecord? ReadFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;

            var record = JsonSerializer.Deserialize<WineOverrideRecord>(File.ReadAllText(file));

            return string.IsNullOrWhiteSpace(record?.PrefixPath) ? null : record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The one registry value this application writes, and the record that lets it be undone.
///
/// It must be <c>"*version"</c>, not <c>"version"</c>: the star marks the entry path-independent,
/// and a bare entry matches only the system-directory reduction. Verified against the real
/// EADesktop.exe — starred, Wine logs <c>got standard key n,b</c> and loads the app-directory DLL;
/// unstarred, <c>got hardcoded default</c> and it is never loaded at all.
/// </summary>
public static class WineDllOverride
{
    public const string Key = @"Software\Wine\DllOverrides";
    public const string ValueName = "*version";
    public const string Native = "native,builtin";

    /// <summary>Whether the registry already prefers the native DLL path-independently. A bare
    /// <c>"version"</c> entry does NOT satisfy this — it misses an app-directory DLL.</summary>
    public static bool IsSatisfied(WinePrefix prefix)
    {
        var value = WineRegistryFile.ReadValue(prefix.UserRegFile, Key, ValueName)?.Text;

        return value is { Length: > 0 }
               && LauncherOverrides.Classify($"*version={value}") == OverrideVerdict.SuppliesNative;
    }

    /// <param name="afterWrite">Test seam: wineserver saves on a timer and lingers after its last
    /// client, so a write can be discarded AFTER it succeeded. Production passes none.</param>
    /// <returns>Null on success; the user-facing failure otherwise.</returns>
    public static async Task<string?> ApplyAsync(WinePrefix prefix, WineOverrideStore store,
                                                 OverrideVerdict verdict, CancellationToken ct,
                                                 Action? afterWrite = null)
    {
        var existing = store.Read(prefix.Root);

        if (verdict == OverrideVerdict.SuppliesNative || IsSatisfied(prefix))
        {
            // PriorValue/CreatedBlock are never overwritten: a reinstall would record our own
            // "native,builtin" as PriorValue, and removal would restore the override it just set.
            if (existing is null)
            {
                // The only record whose loss carries no consequence: nothing was written, so it
                // says "there is nothing to undo" and its absence says the same thing.
                await store.WriteAsync(
                    new WineOverrideRecord(prefix.Root, false, null, false,
                                           "the Wine DLL override was already in place"), ct);
            }

            return null;
        }

        // A prefix validates on system.reg and drive_c alone, so user.reg can be absent. A missing
        // file reads as empty, so writing would create one holding only our block, with no header
        // and no #arch — a hive Wine rejects, while the confirming re-read parses it and reports
        // success. UndoAsync refuses the same shape.
        if (!File.Exists(prefix.UserRegFile))
        {
            return $"'{prefix.Root}' does not have a valid user.reg file; the prefix may be "
                   + "damaged. The Wine DLL override was not written.";
        }

        // A repair: the recorded entry is no longer satisfied and nothing external supplies it.
        // The record keeps its OWN PriorValue, not this write's report, so removal restores the
        // user's original value rather than one this repair fabricated.
        WineRegistryWrite write;
        try
        {
            write = await WineRegistryFile.SetValueAsync(prefix.UserRegFile, Key, ValueName,
                                                         Native, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"'{prefix.Root}' cannot be written to: {e.Message}.";
        }

        afterWrite?.Invoke();

        // From the EXISTING record on a repair: this write's report reflects the state after the
        // entry vanished, not the user's original value.
        var priorValue = existing?.PriorValue ?? write.PriorValue;
        var createdBlock = existing?.CreatedBlock ?? write.CreatedBlock;

        // Re-read and confirm: a lingering wineserver owns the registry and rewrites user.reg when
        // it saves. Read the file directly, because a transient read failure and a discarded write
        // need opposite handling — a lost write leaves no record, a misread landing leaves one.
        string? fileText;
        try
        {
            fileText = await File.ReadAllTextAsync(prefix.UserRegFile, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read itself failed. The write landed but we cannot confirm. Record it so removal can
            // undo, but report the confirmation failure.
            var unconfirmed = $"The Wine DLL override was written but could not be confirmed (the "
                              + $"prefix is temporarily unavailable: {e.Message})";

            return await store.WriteAsync(new WineOverrideRecord(prefix.Root, true, priorValue,
                                                                createdBlock, null), ct) is { } save
                ? $"{unconfirmed}, and the record needed to undo it could not be saved ({save}). "
                  + "Removal will not clear it."
                : $"{unconfirmed}; removal will clear it.";
        }

        // Read succeeded. Parse the file text we already have to check if the value is there.
        // Use the lines-based parser to avoid a second disk read.
        var lines = fileText.Split('\n');
        var found = WineRegistryFile.ReadKeyFromLines(lines, Key) is { } values
                    && values.TryGetValue(ValueName, out var value)
                    && value.Text == Native;

        if (found)
        {
            // The record is what makes the write reversible, so a record that cannot be saved must
            // be reported: otherwise removal does nothing, reports success, and the replaced value
            // is gone with no trace.
            if (await store.WriteAsync(new WineOverrideRecord(prefix.Root, true, priorValue,
                                                             createdBlock, null), ct) is { } save)
            {
                return "The Wine DLL override was set, but the record needed to undo it could not "
                       + $"be saved ({save}), so a removal will not restore the previous value.";
            }

            return null;
        }

        // Read succeeded but the value is absent or wrong: the write was genuinely lost to a flush.
        // Do not record it, so a re-run will attempt the write again.
        return "The Wine DLL override was written but did not survive; something is using "
               + "this prefix. Close it and try again.";
    }

    /// <returns>Null on success; the user-facing failure otherwise.</returns>
    public static async Task<string?> UndoAsync(WinePrefix prefix, WineOverrideStore store,
                                                CancellationToken ct)
    {
        if (store.Read(prefix.Root) is not { } record) return null;

        // Recorded as "nothing was written", so there is nothing of ours to undo. Touching the
        // registry here would clear an override the user or a launcher established.
        if (!record.WroteRegistry)
        {
            store.Delete(prefix.Root);
            return null;
        }

        // The prefix is gone: a Proton recreate or a deleted bottle. Only delete the record if the
        // root directory is actually gone. If it exists but user.reg is missing, that is a damaged
        // prefix and we should report it rather than attempt to repair it.
        if (!Directory.Exists(prefix.Root))
        {
            store.Delete(prefix.Root);
            return null;
        }

        if (!File.Exists(prefix.UserRegFile))
        {
            // The prefix root exists but user.reg is missing: the prefix is damaged. Keep the
            // record and report the issue rather than fabricating a new user.reg.
            return $"'{prefix.Root}' does not have a valid user.reg file; the prefix may be "
                   + "damaged. The unlocker override record is kept for manual recovery.";
        }

        try
        {
            if (record.PriorValue is { } prior)
            {
                await WineRegistryFile.SetValueAsync(prefix.UserRegFile, Key, ValueName, prior, ct);
            }
            else
            {
                await WineRegistryFile.RemoveValueAsync(prefix.UserRegFile, Key, ValueName,
                                                        record.CreatedBlock, ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"'{prefix.Root}' cannot be written to: {e.Message}.";
        }

        store.Delete(prefix.Root);
        return null;
    }

    /// <summary>Deletes every record whose prefix root is gone, reporting each. Without it a
    /// record outlives its prefix and the next removal writes a recorded value into a registry
    /// regenerated since. A root that exists but fails validation is reported and kept.</summary>
    public static IReadOnlyList<string> SweepOrphans(WineOverrideStore store, string userName)
    {
        var reported = new List<string>();

        foreach (var record in store.All())
        {
            // Only delete if the prefix root is gone. A root that exists but fails validation is
            // reported but kept, so the undo can try to repair it or the user can investigate.
            if (!Directory.Exists(record.PrefixPath))
            {
                // "Does not exist" is also the answer for an unmounted volume, where a Proton
                // prefix ordinarily lives. The parent separates the two: a deleted prefix leaves
                // its parent, an absent mount takes the whole chain.
                var parent = Path.GetDirectoryName(record.PrefixPath);
                if (parent is null || !Directory.Exists(parent))
                {
                    reported.Add($"'{record.PrefixPath}' cannot be reached, so its unlocker "
                                 + "override record is kept.");
                    continue;
                }

                store.Delete(record.PrefixPath);
                reported.Add($"'{record.PrefixPath}' no longer exists.");
                continue;
            }

            if (WinePrefix.TryOpen(record.PrefixPath,
                                   new TargetEnvironment(EnvironmentSource.Wine, record.PrefixPath),
                                   userName) is null)
            {
                reported.Add($"'{record.PrefixPath}' is not a valid Wine prefix.");
            }
        }

        return reported;
    }
}
