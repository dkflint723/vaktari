using Avalonia.Input;
using Avalonia.Platform.Storage;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// What a drag out of a listing carries.
///
/// **A drag out of a plainly opened pane carried the neighbour.** The payload
/// is built through the storage provider, which on Windows is a FileInfo
/// underneath and folds "…\report " to "…\report": a Shift-drop moved
/// "report", which nobody dragged, and a Ctrl-drop copied it — both reported
/// Completed — and the bin row reads the same payload (seventh review round,
/// 7-D). So a drag that would hand on any name Win32 folds is refused whole,
/// in words naming it, before a payload exists: a drag that quietly left one
/// row out would move the rest and look like it moved them all. Inside
/// Vaktari the same rows still go by Ctrl+C and Ctrl+V, whose own list keeps
/// every name exactly. See <see cref="ReachablePath.RefuseHandedOut"/>.
///
/// Apart from the window so a test can build exactly what a drag builds.
/// </summary>
internal static class DragPayload
{
    /// <summary>The payload for these paths, or the sentence refusing it —
    /// or neither, when the storage provider would hand over none of them.</summary>
    public static async Task<(DataTransfer? Data, string? Refusal)> BuildAsync(
        IStorageProvider storage, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
            if (ReachablePath.RefuseHandedOut(path) is { } why)
                return (null, why);

        // DataFormat.File is what other applications actually read; Avalonia
        // serialises it to text/uri-list on X11, the same route the clipboard
        // takes.
        var data = new DataTransfer();

        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await storage.TryGetFolderFromPathAsync(path).ConfigureAwait(true)
                : await storage.TryGetFileFromPathAsync(path).ConfigureAwait(true);

            if (item is not null) data.Add(DataTransferItem.CreateFile(item));
        }

        return data.Items.Count > 0 ? (data, null) : (null, null);
    }
}
