using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.App;

/// <summary>
/// Everything the shell needs, assembled once.
/// </summary>
public sealed record AppServices(
    AppPaths Paths,
    SettingsStore Settings,
    AppSettings Current,
    string? SettingsError,
    CatalogLoader Catalog,
    InstallStateStore InstallState,
    IQueueController Queue,
    IUiDispatcher Dispatcher,
    IPickerService Pickers,
    IClipboardService Clipboard,
    IClock Clock,
    string? CommandLineCatalog,
    UnlockerService Unlocker,
    IUnlockerHost UnlockerHost,
    IUnlockerAssetSource UnlockerAssets,
    UnlockerNotes UnlockerNotes,
    DownloadPaths DownloadPaths,
    DownloadOptions DownloadOptions,
    OrphanCleaner Orphans,
    LogRelay Log);
