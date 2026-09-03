using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Vaktari.Ui;

/// <summary>
/// Clipboard access as an injectable service.
///
/// This replaces a command → event → window-handler → clipboard chain that had
/// three places to fail silently. A view model that owns its own clipboard
/// either works or reports why.
/// </summary>
public interface IClipboardService
{
    Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths);
    Task<ClipboardPayload?> GetFilesAsync();

    /// <summary>Whether the clipboard holds files, without reading them.</summary>
    Task<bool> HasFilesAsync();
}

public sealed class ClipboardService(Func<TopLevel?> resolve) : IClipboardService
{
    public static ClipboardService ForWindow(Window window)
        => new(() => TopLevel.GetTopLevel(window));

    public async Task<bool> HasFilesAsync()
    {
        var top = resolve();

        if (top?.Clipboard is not { } clipboard) return false;

        return await FileClipboard.HasFilesAsync(clipboard).ConfigureAwait(false);
    }

    public async Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
    {
        var top = resolve();
        if (top?.Clipboard is not { } clipboard || paths.Count == 0) return false;

        await FileClipboard.SetAsync(clipboard, top.StorageProvider, action, paths)
                           .ConfigureAwait(false);
        return true;
    }

    public async Task<ClipboardPayload?> GetFilesAsync()
    {
        var top = resolve();
        return top?.Clipboard is { } clipboard
            ? await FileClipboard.GetAsync(clipboard).ConfigureAwait(false)
            : null;
    }
}
