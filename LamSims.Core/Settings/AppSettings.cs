using System;
using LamSims.Core.Downloading;

namespace LamSims.Core.Settings;

public sealed class AppSettings
{
    /// <summary>A path or URL the user chose. Second in the catalog resolution order.</summary>
    public string? CatalogSource { get; set; }

    public string? GameDirectory { get; set; }

    /// <summary>
    /// A Wine prefix the user chose, scanned in addition to the ones each launcher's configuration
    /// names. Read live at scan time rather than captured, so changing it re-runs detection without
    /// rebuilding the object graph.
    /// </summary>
    public string? WinePrefix { get; set; }

    /// <summary>Overrides where partial downloads and archives live; null keeps the default.</summary>
    public string? DownloadDirectory { get; set; }

    public int Connections { get; set; } = 8;

    /// <summary>
    /// The settings file is hand-editable, so its values are clamped rather than validated:
    /// a nonsensical connection count should not stop the application from starting.
    /// </summary>
    public DownloadOptions ToDownloadOptions() => new() { Connections = Math.Clamp(Connections, 1, 16) };
}
