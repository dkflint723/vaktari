using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// The desktop asking this application to show something.
///
/// **A closed call graph with one way in**, which is what makes it a file:
/// OnShowRequested hands to OpenPaths, OpenPaths asks LocalPath what a URI
/// actually names, and Raise brings the window forward for both. Nothing else
/// in the window calls into any of it. The one entry surface is the founder's
/// block in the constructor, which registers this window as the handler and
/// then forwards whatever arrives.
///
/// It answers org.freedesktop.FileManager1, which is how a desktop says "show
/// me this file in its folder" — a file manager's side of Show Item, not a
/// second way of opening a folder. The distinction matters at the far end:
/// showing an item selects it where it lives, and opening one does not.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// A path, whether the desktop handed over a path or a file:// URI.
    ///
    /// **The installed desktop entry said %U and this did not decode one**, so
    /// on GNOME, Xfce, Cinnamon and plain xdg-open — every desktop that honours
    /// %U literally — "open containing folder" arrived as
    /// "file:///home/me/Documents", Directory.Exists said no, and the path was
    /// dropped without a word. That was the primary Linux install route, and it
    /// could not open a folder at all.
    ///
    /// packaging/install.sh now writes %F, matching brand/vaktari.desktop. This
    /// stays because a portal or a D-Bus caller can still send a URI whatever
    /// the Exec key says, and percent-decoding is the difference between
    /// opening "My Documents" and opening nothing.
    /// </summary>
    /// Moved to Vaktari.Core.FileSystem.FileUri, because there are two callers
    /// now — the command line and the desktop's own request channel — and two
    /// copies of a decoder is how one of them keeps a bug the other has already
    /// fixed. The shared one also refuses what it cannot open rather than
    /// handing the raw string on, which is the same silent drop by a different
    /// route.
    /// </summary>
    private static string? LocalPath(string raw) => FileUri.ToLocalPath(raw);

    /// <summary>
    /// Says which of the four things happened, on the same terminal as the
    /// running-from line. **A file manager that silently does not answer looks
    /// exactly like one that answered and did nothing**, and that is the whole
    /// failure being fixed here — so not answering has to say so.
    ///
    /// async void with a catch, like the other started-and-not-awaited handlers
    /// here: a discard would take the exception with the task.
    /// </summary>
    private static async void AnnounceFileManagerService(IFileManagerService service)
    {
        try
        {
            var state = await service.ReconcileAsync().ConfigureAwait(true);

            Console.Error.WriteLine(
                $"[vaktari] FileManager1: {FileManagerServiceStates.Describe(state)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] FileManager1: {ex.Message}");
        }
    }

    /// <summary>
    /// The three verbs, each routed to something the window already does. There
    /// is no new behaviour here at all — the value of this feature is that these
    /// three now have a name on the bus that other applications already call.
    ///
    /// **Items goes to ShowAsync and not OpenPaths**, which is the entire
    /// difference: OpenPaths opens the folder and selects nothing, and in a
    /// Downloads folder of four hundred files that does not answer "which one
    /// did I just save".
    /// </summary>
    internal async void OnShowRequested(ShowRequest request)
    {
        try
        {
            switch (request.Kind)
            {
                case ShowKind.Items:
                    await _shell.ShowAsync(request.Paths).ConfigureAwait(true);
                    break;

                case ShowKind.Folders:
                    // Raises the window itself, so it returns rather than
                    // falling through to a second Raise.
                    OpenPaths(request.Paths, activate: true);
                    return;

                case ShowKind.ItemProperties:
                    // Already refuses a path that has gone and says so in the
                    // status line, rather than filling a sheet with zeroes.
                    ShowPropertiesFor(request.Paths);
                    break;
            }

            Raise();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] FileManager1 request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the window forward. Split out of OpenPaths so the bus's three
    /// verbs raise it the same way a handed-over launch does — somebody asked to
    /// SEE something, and loading it behind whatever they were doing is not that.
    /// </summary>
    private void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
    }

    /// <summary>
    /// Opens folders in tabs. Files resolve to the folder holding them, because
    /// "open containing folder" is the request the desktop actually sends.
    /// </summary>
    internal void OpenPaths(IReadOnlyList<string> paths, bool activate)
    {
        foreach (var raw in paths)
        {
            // **A URI this process cannot open now says so.** It used to be
            // handed on as its own raw text, fail Directory.Exists and vanish;
            // trash:/// and sftp:// are real things a desktop sends.
            if (LocalPath(raw) is not { } path)
            {
                _shell.OperationStatus = $"cannot open {raw}";
                continue;
            }

            if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                path = parent;

            if (!Directory.Exists(path)) continue;

            _shell.OpenInNewTab(path);
        }

        if (!activate) return;

        // The user asked to see a folder, and silently loading it behind
        // whatever they were doing is not that.
        Raise();
    }
}
