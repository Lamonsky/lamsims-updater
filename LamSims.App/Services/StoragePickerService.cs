using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace LamSims.App.Services;

/// <summary>
/// The one service that needs a window handle. Avalonia's picker is asynchronous and hangs off
/// TopLevel, which is why the view model takes this rather than calling a dialog itself.
/// </summary>
public sealed class StoragePickerService(TopLevel topLevel) : IPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? startAt)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAt(startAt),
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string title, string? startAt)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAt(startAt),
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<IStorageFolder?> StartAt(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : await topLevel.StorageProvider.TryGetFolderFromPathAsync(path);
}
