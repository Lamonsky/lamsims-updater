using System.Threading.Tasks;

namespace LamSims.App.Services;

public interface IPickerService
{
    /// <summary>Null when the user cancelled, which every call site treats as "no change".</summary>
    Task<string?> PickFolderAsync(string title, string? startAt);

    Task<string?> PickFileAsync(string title, string? startAt);
}
