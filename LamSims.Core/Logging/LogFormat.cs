using System.Globalization;

namespace LamSims.Core.Logging;

/// <summary>
/// Human-readable byte counts for log lines. Same 1000-based units and formatting as
/// LamSims.App/ViewModels/PackRowViewModel.cs's private FormatBytes, kept as a separate copy
/// because Core must not reference App, and Core's own callers need it public: Core's
/// InternalsVisibleTo names only LamSims.Core.Tests, and LamSims.App calls this too.
/// </summary>
public static class LogFormat
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = value;
        var unit = 0;

        while (scaled >= 1000 && unit < units.Length - 1)
        {
            scaled /= 1000;
            unit++;
        }

        // InvariantCulture: on a machine whose culture uses a comma decimal separator, the
        // ambient culture would format this differently from what a test asserts literally.
        return unit == 0
            ? $"{value} B"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", scaled, units[unit]);
    }
}
