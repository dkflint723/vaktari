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

    /// <summary>
    /// Plain text, which is what an address is.
    ///
    /// **A pane could put files on the clipboard and not one character of
    /// text.** Every text copy in the application went out through the shell's
    /// CopyTextRequested event to the window, which reaches the clipboard by
    /// way of ShellViewModel.ActiveTab. The address bar is a pane's own, one
    /// per split half and bound to its group's ActiveTab — so the pane that
    /// owns the bar writes through the clipboard it already owns, rather than
    /// asking the window which pane it thinks is current.
    /// </summary>
    Task<bool> SetTextAsync(string text);
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

    /// <summary>
    /// The one clipboard write in the application that is not a file list.
    ///
    /// **False when there is no TopLevel and false for empty text** — the same
    /// two answers <see cref="SetFilesAsync"/> gives to its own versions of
    /// these conditions, so a caller that could not write is never told that it
    /// did. A window being torn down has no TopLevel and so no clipboard, and
    /// the return value is all a menu row has to report from.
    ///
    /// Empty text is refused rather than written: the caller that has nothing
    /// to say wants "nothing was copied" reported, and a clipboard emptied by
    /// a menu row nobody could see the effect of is worse than one left alone.
    /// </summary>
    public async Task<bool> SetTextAsync(string text)
    {
        var top = resolve();

        if (top?.Clipboard is not { } clipboard || text.Length == 0) return false;

        await clipboard.SetTextAsync(text).ConfigureAwait(false);

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
