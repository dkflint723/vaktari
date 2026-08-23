using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The preview pane: one selected file, shown as a picture or as its first few
/// hundred characters.
///
/// Split out of PaneViewModel, which had grown to 2,592 lines under ten region
/// banners — one of which, "preview", covered a thousand lines containing
/// navigation, view modes, the clipboard, renames and the session. This is the
/// part of it that genuinely was preview.
///
/// Still the same class: a pane's preview reads SelectedEntry and writes
/// Status, and prising those apart would mean threading state through a
/// constructor to gain nothing but a smaller file.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- preview -------------------------------------------------------

    [ObservableProperty] private bool _isPreviewVisible;

    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _previewImage;

    [ObservableProperty] private string _previewText = "";

    public bool HasPreviewImage => PreviewImage is not null;

    public bool HasPreviewText => PreviewText.Length > 0;

    [RelayCommand]
    public void TogglePreview()
    {
        IsPreviewVisible = !IsPreviewVisible;
        if (IsPreviewVisible) _ = RefreshPreviewAsync();
    }

    partial void OnPreviewImageChanged(Avalonia.Media.Imaging.Bitmap? value)
        => OnPropertyChanged(nameof(HasPreviewImage));

    partial void OnPreviewTextChanged(string value)
        => OnPropertyChanged(nameof(HasPreviewText));

    private async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        PreviewImage = null;
        PreviewText = "";

        if (SelectedEntry is not { } entry)
        {
            PreviewTitle = "";
            PreviewDetail = "nothing selected";
            return;
        }

        PreviewTitle = entry.Name;
        PreviewDetail = entry.IsDirectory
            ? "folder"
            : $"{entry.Length:N0} bytes · {entry.LastWriteTime:yyyy-MM-dd HH:mm}";

        if (entry.IsDirectory) return;

        try
        {
            var bitmap = await Thumbnails.ThumbnailLoader
                .LoadAsync(entry.FullPath, 512, ct).ConfigureAwait(false);

            // **Re-checked after every await, and again inside the dispatch.**
            // Arrowing down a folder of photos starts a load per row and
            // cancels the one before, but a load already past its own last
            // cancellation check still returned a bitmap — and InvokeAsync only
            // QUEUES the assignment, so a stale image could be posted after the
            // current one had already been shown. The picture on screen then
            // belonged to a file the selection had moved off.
            ct.ThrowIfCancellationRequested();

            if (bitmap is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested) PreviewImage = bitmap;
                });
                return;
            }

            // Not an image — show the head of the file if it looks like text.
            // Capped hard: previewing a gigabyte log should cost the same as
            // previewing a config file.
            if (entry.Length is > 0 and < 8_000_000 && LooksTextual(entry.Name))
            {
                var text = await ReadHeadAsync(entry.FullPath, 4000, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested) PreviewText = text;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A failure for a file nobody is looking at any more is not
                // worth putting on screen over the one they are.
                if (!ct.IsCancellationRequested)
                    PreviewDetail = Core.FileSystem.Failures.Describe(ex, "preview that");
            });
        }
    }

    private static bool LooksTextual(string name)
    {
        var ext = Path.GetExtension(name);

        if (ext.Length == 0) return true;

        return ext.ToLowerInvariant() is
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yaml" or ".yml" or
            ".cs" or ".py" or ".sh" or ".ps1" or ".c" or ".h" or ".cpp" or ".rs" or
            ".go" or ".js" or ".ts" or ".html" or ".css" or ".ini" or ".conf" or
            ".toml" or ".csv" or ".sql" or ".axaml" or ".xaml" or ".csproj" or ".props";
    }

    private static async Task<string> ReadHeadAsync(string path, int chars, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[chars];
        var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }
}
