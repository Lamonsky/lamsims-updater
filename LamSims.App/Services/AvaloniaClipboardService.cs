using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace LamSims.App.Services;

/// <summary>
/// The window's real clipboard, following <see cref="StoragePickerService"/>'s pattern exactly:
/// built with the window's own TopLevel, once it exists.
/// </summary>
public sealed class AvaloniaClipboardService(TopLevel topLevel) : IClipboardService
{
    /// <summary>
    /// Clipboard is null on a TopLevel with no clipboard backend (a headless test host without
    /// one wired up), in which case the copy is a no-op rather than a throw: losing a bug report
    /// aid is not worth crashing the log view over. The clipboard that IS there can throw too —
    /// see <see cref="SetTextAsync(IClipboard?, string)"/> — for the exact same reason.
    /// </summary>
    public Task SetTextAsync(string text) => SetTextAsync(topLevel.Clipboard, text);

    /// <summary>
    /// The logic above, taking the clipboard as a parameter rather than reading
    /// <c>topLevel.Clipboard</c> itself so a test can drive it with a clipboard that throws —
    /// Avalonia's <see cref="IClipboard"/> can only be reached live behind a real windowing
    /// platform, which a unit test has no access to.
    ///
    /// Windows is where this actually fires: <c>Avalonia.Win32.ClipboardImpl</c> retries the
    /// native open/set calls a fixed number of times against a clipboard manager, RDP session or
    /// Office holding the clipboard, and then throws — a bare <see cref="TimeoutException"/> for
    /// the open, and whatever <see cref="Marshal.ThrowExceptionForHR(int)"/> produces for the set,
    /// which for the OLE clipboard HRESULTs involved is a <see cref="COMException"/>. Both are
    /// swallowed here, same reasoning as the null-clipboard case above: this command exists so a
    /// user can get the log out to report a bug, and a failed copy must not be how they lose the
    /// log they were trying to save. <see cref="Trace.WriteLine(string?)"/>, not
    /// <c>Debug.WriteLine</c>, so the failure still lands somewhere in a Release build the way
    /// <c>LogViewModel.Drain</c>'s own catch does.
    /// </summary>
    internal static async Task SetTextAsync(IClipboard? clipboard, string text)
    {
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is TimeoutException or COMException)
        {
            Trace.WriteLine($"AvaloniaClipboardService.SetTextAsync: clipboard threw: {ex}");
        }
    }
}
