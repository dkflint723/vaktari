using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Vaktari.Ui;

/// <summary>
/// Asking the platform where a copy or a move should land.
///
/// The shell decides there is a destination to ask for; only a window can put
/// up a picker, so it raises and this answers. One handler, and the helper
/// that works out where the picker should start.
///
/// **Suggested comes along, and the reason is the opposite of the one that
/// kept it behind last time.** It has four callers: this handler, and three in
/// the settings file. When the settings dialog left, this became its only
/// caller in the window's own file — so leaving it there would have made it a
/// member of a file that nothing in that file calls, with its live callers all
/// in a sibling. Its own doc says it stopped being a local function BECAUSE
/// something outside the settings dialog needed the same answer, and that
/// sentence is true wherever it sits; a settings file holding it would be
/// holding an argument against its own name.
///
/// **Two usings, and one of them is not obvious.** Avalonia.Controls is for
/// the Window parameter; Avalonia.Platform.Storage is for
/// TryGetFolderFromPathAsync and TryGetLocalPath, which are EXTENSION methods
/// on IStorageProvider and IStorageItem. Fully qualifying the types would not
/// have imported them. The same move takes that using out of the file this
/// came from, where these two were its only consumers.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// A folder for a picker to open at, or null where there is nothing there
    /// yet and the picker should choose for itself.
    ///
    /// A member rather than the local function it began as, because the
    /// transfer submenus' "Choose a folder…" opens a picker from outside the
    /// settings dialog and wants the same answer to the same question.
    /// </summary>
    private static async Task<Avalonia.Platform.Storage.IStorageFolder?> Suggested(
        Window window, string path)
    {
        try
        {
            return Directory.Exists(path)
                ? await window.StorageProvider.TryGetFolderFromPathAsync(path)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Choose a folder…" at the foot of Copy to and Move to.
    ///
    /// The picker belongs to the window for the reason the settings dialog's
    /// does: a view model that opened one could not be constructed in a test.
    /// The shell is already holding the files and the direction, so the only
    /// thing that travels back is the folder — and a dismissed picker returns
    /// nothing and does nothing.
    /// </summary>
    private async void OnTransferBrowseRequested(
        object? sender, ViewModels.TransferBrowseRequest request)
    {
        // Starting where the files are: a destination is more often beside the
        // source than at home, and it is the folder the user can see.
        var start = await Suggested(this, request.StartAt);

        var picked = await StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = request.Move ? "Choose a folder to move to" : "Choose a folder to copy to",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;

        request.Chose(folder);
    }
}
