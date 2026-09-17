using System.Threading.Tasks;

namespace LamSims.App.Services;

/// <summary>
/// The other service that needs a window handle, alongside <see cref="IPickerService"/>:
/// Avalonia's clipboard hangs off TopLevel, which is why a view model takes this rather than
/// reaching for a static clipboard of its own.
/// </summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}
