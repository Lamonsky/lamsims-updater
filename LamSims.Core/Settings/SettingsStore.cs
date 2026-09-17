using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace LamSims.Core.Settings;

public sealed record SettingsLoad(AppSettings Settings, string? Error);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;

    public SettingsStore(AppPaths paths) => _paths = paths;

    /// <summary>
    /// Reads the settings, falling back to defaults when the file is absent or damaged. A
    /// damaged file is reported and left where it is, since it is the user's and they may be
    /// halfway through editing it.
    /// </summary>
    public SettingsLoad Load()
    {
        if (!File.Exists(_paths.SettingsFile))
            return new SettingsLoad(new AppSettings(), null);

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_paths.SettingsFile), Format);

            return settings is null
                ? new SettingsLoad(new AppSettings(), $"'{_paths.SettingsFile}' holds no settings object.")
                : new SettingsLoad(settings, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoad(
                new AppSettings(), $"'{_paths.SettingsFile}' could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// Writes to a temporary file and renames it over the target, so a crash mid-write
    /// leaves the previous settings intact rather than a half-written file.
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        _paths.EnsureCreated();
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, JsonSerializer.Serialize(settings, Format), ct);
    }
}
