using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LamSims.App;

/// <summary>
/// Multiplies a bound length by a constant. Exists for one binding: the settings region's share of
/// the window height.
///
/// A literal MaxHeight cannot do that job. The region needs about 429px with a section open, so any
/// literal is either below that - clipping a section at sizes where there is ample room - or above
/// it, which is no cap at all at the window's 560px minimum. The cap has to move with the window.
/// </summary>
public sealed class Ratio : IValueConverter
{
    /// <summary>The share of the window the settings region may occupy.</summary>
    public static readonly Ratio Of = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double length || double.IsNaN(length) || double.IsInfinity(length))
            return double.PositiveInfinity;

        if (parameter is not string text
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
        {
            return double.PositiveInfinity;
        }

        // Never a cap of zero: a window measured before its first layout reports 0, and a zero
        // MaxHeight would collapse the region rather than leave it uncapped for that pass.
        var capped = length * share;

        return capped > 0 ? capped : double.PositiveInfinity;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Ratio is one-way: nothing writes a window height back.");
}
