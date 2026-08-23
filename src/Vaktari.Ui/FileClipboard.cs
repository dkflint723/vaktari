using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Vaktari.Ui;

public enum ClipboardAction { Copy, Cut }

public sealed record ClipboardPayload(ClipboardAction Action, IReadOnlyList<string> Paths);

/// <summary>
/// File clipboard using the freedesktop conventions, so copy in Vaktari and paste
/// in Dolphin works — and the other way round.
///
/// The file list itself goes through Avalonia's universal DataFormat.File,
/// which it serialises to text/uri-list on X11. Declaring our own
/// text/uri-list platform format collided with that handler: the target was
/// advertised but failed to convert, so receivers asked for it, got an error,
/// and discarded the whole transfer. Only the copy-versus-cut verb, which has
/// no universal equivalent, is written as a raw platform format.
/// </summary>
public static class FileClipboard
{
    // Despite the name this is what KDE reads too, and it is the only part of
    // the payload that says whether this was a copy or a cut.
    private static readonly DataFormat<byte[]> GnomeFormat =
        DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");

    // KIO does not read the verb out of the gnome format. Dolphin decides
    // copy-versus-cut from this separate target holding "1", and without it a
    // cut pastes as a copy and the source is never removed. Deleting the
    // source is the receiving application's job, not ours.
    private static readonly DataFormat<byte[]> KdeCutFormat =
        DataFormat.CreateBytesPlatformFormat("application/x-kde-cutselection");

    /// <summary>
    /// How Windows says cut, and the reason a cut in Explorer used to paste
    /// here as a copy.
    ///
    /// Two desktop conventions were handled — GNOME's verb line and KDE's
    /// marker — and Windows' own was not, so the file stayed where it was and a
    /// second copy appeared. A DWORD: 2 is DROPEFFECT_MOVE, 1 is
    /// DROPEFFECT_COPY. CreateBytesPlatformFormat resolves to
    /// RegisterClipboardFormatW here, which is the same mechanism
    /// VirtualFileDrop registers its shell formats through.
    /// </summary>
    private static readonly DataFormat<byte[]> WindowsDropEffect =
        DataFormat.CreateBytesPlatformFormat("Preferred DropEffect");

    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    public static async Task SetAsync(
        IClipboard clipboard,
        IStorageProvider storage,
        ClipboardAction action,
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await storage.TryGetFolderFromPathAsync(path).ConfigureAwait(false)
                : await storage.TryGetFileFromPathAsync(path).ConfigureAwait(false);

            if (item is not null) items.Add(item);
        }

        if (items.Count == 0) return;

        var gnome = new StringBuilder();
        gnome.Append(action == ClipboardAction.Cut ? "cut" : "copy");
        foreach (var path in paths) gnome.Append('\n').Append(ToFileUri(path));

        var data = new DataTransfer();

        // The verb rides on the first item; the file formats are what produce
        // text/uri-list, and Avalonia handles that serialisation itself.
        var first = new DataTransferItem();
        first.Set(GnomeFormat, Encoding.UTF8.GetBytes(gnome.ToString()));

        // Written unconditionally, both ways round, so a cut made HERE moves
        // when pasted in Explorer rather than quietly copying there too.
        first.Set(
            WindowsDropEffect,
            BitConverter.GetBytes(action == ClipboardAction.Cut ? DropEffectMove : DropEffectCopy));

        if (action == ClipboardAction.Cut)
            first.Set(KdeCutFormat, "1"u8.ToArray());

        first.SetFile(items[0]);
        first.SetText(string.Join('\n', paths));
        data.Add(first);

        for (var i = 1; i < items.Count; i++)
            data.Add(DataTransferItem.CreateFile(items[i]));

        await clipboard.SetDataAsync(data).ConfigureAwait(false);

        // On X11 values are provided lazily, so without this the payload would
        // be unavailable the moment Vaktari exits.
        await clipboard.FlushAsync().ConfigureAwait(false);
    }

    public static async Task<ClipboardPayload?> GetAsync(IClipboard clipboard)
    {
        using var data = await clipboard.TryGetDataAsync().ConfigureAwait(false);
        if (data is null) return null;

        // Prefer the gnome format: it is the only source for the cut verb.
        if (await data.TryGetValueAsync(GnomeFormat).ConfigureAwait(false) is { } gnome)
        {
            var lines = Encoding.UTF8.GetString(gnome)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length > 1)
            {
                var action = lines[0].Equals("cut", StringComparison.OrdinalIgnoreCase)
                    ? ClipboardAction.Cut
                    : ClipboardAction.Copy;

                var paths = lines.Skip(1).Select(FromFileUri).OfType<string>().ToList();
                if (paths.Count > 0) return new ClipboardPayload(action, paths);
            }
        }

        // Anything else offering files — Dolphin, Nautilus, a browser. Dolphin
        // signals a cut with the KDE target rather than the gnome verb, so
        // check it before defaulting to copy.
        var kdeCut = await data.TryGetValueAsync(KdeCutFormat).ConfigureAwait(false);
        var cut = kdeCut is { Length: > 0 } && kdeCut[0] == (byte)'1';

        // Windows' turn. OR-ed into the same decision rather than branching, so
        // there is still one place that decides cut versus copy.
        var effect = await data.TryGetValueAsync(WindowsDropEffect).ConfigureAwait(false);

        if (effect is { Length: >= 4 }
            && (BitConverter.ToInt32(effect, 0) & DropEffectMove) != 0) cut = true;

        var files = await data.TryGetFilesAsync().ConfigureAwait(false);
        var fromFiles = (files ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        return fromFiles.Count > 0
            ? new ClipboardPayload(cut ? ClipboardAction.Cut : ClipboardAction.Copy, fromFiles)
            : null;
    }

    private static string ToFileUri(string path)
        // **Left splitting on '/' deliberately — a file URI is not a path.**
        // RFC 8089 uses '/' as the separator on every platform, so this is
        // correct here and would be wrong to route through PathRules. The
        // Windows work is a different job: a path there is `C:\x\y`, its URI is
        // `file:///C:/x/y`, and the desktop exchanges files as CF_HDROP rather
        // than text/uri-list anyway.
        => "file://" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static string? FromFileUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.Ordinal)) return null;
        return string.Join("/", uri[7..].Split('/').Select(Uri.UnescapeDataString));
    }
}
