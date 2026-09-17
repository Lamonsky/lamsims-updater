using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace LamSims.Core.Downloading;

/// <summary>
/// Persists the resume sidecar. Writes go to a temporary file and are renamed into place,
/// so a kill mid-write cannot leave torn JSON where the state belongs. Concurrent workers
/// report completions, so writes are serialized through one semaphore. An unreadable sidecar
/// means "no resume state" rather than an exception.
/// </summary>
public sealed class PartStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PartStateStore(string stateFilePath) => _path = stateFilePath;

    public PartState? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<PartState>(File.ReadAllText(_path), SerializerOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(PartState state, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(state, SerializerOptions), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Delete()
    {
        try { File.Delete(_path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // AtomicFile writes through a temp name unique to each call and removes it itself, so
        // one survives only a kill mid-save. Sweeping them here is what keeps that from
        // accumulating across a download's lifetime: nothing else matches the name.
        try
        {
            foreach (var stale in Directory.EnumerateFiles(
                         Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".*.tmp"))
            {
                try { File.Delete(stale); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
