using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Settings;

namespace LamSims.Core.Unlocking;

/// <param name="ClientPath">
/// In full, so a hashed-key collision cannot make a record vouch for a different install.
/// </param>
/// <param name="OwnsAutostartBackup">Whether this install captured the autostart value. One
/// machine-wide entry, one backup file, so exactly one record may claim it and only that record's
/// removal restores it.</param>
public sealed record UnlockerInstallRecord(string ClientPath, ClientKind Client,
                                          bool OwnsAutostartBackup);

/// <summary>One record per installed client, grouped by scope — the caller's
/// <see cref="UnlockerPaths.ConfigDirectory"/>, which is why nothing here is global. It exists
/// because the unlocker's configuration directory is shared by both clients and cannot be split,
/// so removal has to know whether anyone else still needs it.</summary>
public sealed class UnlockerInstallRecordStore(AppPaths paths)
{
    /// <summary>Known limitation: keyed per <see cref="ClientKind"/>, so two installs of the SAME
    /// kind in one scope share a record file and the second overwrites the first. The caller's
    /// by-path sibling-DLL check, not this store, is what covers that case.</summary>
    private string FileFor(string scope, ClientKind client) =>
        Path.Combine(paths.UnlockerInstallDirectory, $"{Key(scope)}-{Name(client)}.json");

    private static string Key(string scope) => PathIdentity.DirectoryKey(scope);

    private static string Name(ClientKind client) => client.ToString().ToLowerInvariant();

    public UnlockerInstallRecord? Read(string scope, ClientKind client,
                                      string? expectedClientPath = null)
    {
        var record = ReadFile(FileFor(scope, client));
        if (record is null) return null;

        // A hashed key is not proof. When the caller knows which install it is asking about, a
        // record naming a different one is treated as absent rather than trusted.
        if (expectedClientPath is not null
            && !string.Equals(PathIdentity.Canonical(record.ClientPath),
                              PathIdentity.Canonical(expectedClientPath),
                              StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return record;
    }

    public async Task WriteAsync(string scope, UnlockerInstallRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.UnlockerInstallDirectory);
        await AtomicFile.WriteAllTextAsync(FileFor(scope, record.Client),
            JsonSerializer.Serialize(record), ct);
    }

    public void Delete(string scope, ClientKind client)
    {
        try
        {
            var file = FileFor(scope, client);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be deleted leaves a stale claim, which the next removal
            // reports. It must not fail the removal that has already put the machine right.
        }
    }

    /// <summary>
    /// The scope's other installed clients, driving the one decision that cannot be made per
    /// client: whether removal may delete the shared configuration directory. <c>Complete</c> is
    /// false when the answer could not be established — "no other client" and "cannot tell" must
    /// not collapse, because the caller deletes a directory on the strength of it. An absent
    /// directory is a complete answer of none.
    /// </summary>
    public (IReadOnlyList<UnlockerInstallRecord> Records, bool Complete) Others(
        string scope, ClientKind client)
    {
        var results = new List<UnlockerInstallRecord>();
        var complete = true;
        var prefix = Key(scope);

        if (!Directory.Exists(paths.UnlockerInstallDirectory)) return (results, true);

        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         paths.UnlockerInstallDirectory, $"{prefix}-*.json"))
            {
                var record = ReadFile(file);
                if (record is null) { complete = false; continue; }
                if (record.Client != client) results.Add(record);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            complete = false;
        }

        return (results, complete);
    }

    private static UnlockerInstallRecord? ReadFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var record = JsonSerializer.Deserialize<UnlockerInstallRecord>(File.ReadAllText(file));
            return string.IsNullOrWhiteSpace(record?.ClientPath) ? null : record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
