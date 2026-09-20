using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The properties window, and the two handles only it uses.
///
/// Three members that funnel into one: a no-argument entry for the selection,
/// a single-path entry for a sidebar row, and the list overload both of them
/// end in. The fields come too, and that is the part worth checking rather
/// than assuming — <c>_properties</c> is read in exactly two statements and
/// <c>_accessEditor</c> in one, all three inside the list overload, and no
/// other partial mentions either name or either interface. Left behind they
/// would have sat assigned and never read, which is the fault this class has
/// now shipped twice and caught twice. Their assignments stay in the
/// constructor beside the window's other per-window handles, exactly as the
/// settings file's three already do.
///
/// **Three ways in, narrow and named rather than closed.** Two are shell
/// subscriptions in the constructor. The third is the desktop's
/// org.freedesktop.FileManager1 ItemProperties verb, which reaches the list
/// overload from MainWindow.DesktopRequests.cs — a real cross-file call, and
/// the reason this is not a sealed unit.
///
/// **It opens rather than shows as a dialog, and it is the only thing in the
/// window that does.** Every other place a MainWindow partial puts up a
/// window uses ShowDialog; this one call is the single Show in the class. The
/// properties sheet is non-modal on purpose — it is a thing you leave open
/// beside the file you are looking at — which is the same decision batch
/// rename records taking the other way.
///
/// Back across the boundary it reaches only the shell, twice, and nothing else
/// of the window's.
/// </summary>
public partial class MainWindow
{
    private readonly IPropertiesProvider _properties;
    private readonly IAccessEditor? _accessEditor;

    /// <summary>
    /// Non-modal on purpose: you frequently want to compare two files, and a
    /// modal dialog makes that impossible without closing it first.
    /// </summary>
    private void ShowProperties()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(x => x.FullPath).ToList()
            : pane.SelectedEntry is { } one ? [one.FullPath]
            : new List<string> { pane.CurrentPath };

        if (paths.Count == 0) return;

        ShowPropertiesFor(paths);
    }

    private void ShowPropertiesFor(string path) => ShowPropertiesFor([path]);

    private void ShowPropertiesFor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        // **A sheet for a path that has gone was confidently wrong rather than
        // empty.** Windows answers a query about a file that is not there with
        // a size of zero, 1601-01-01 for every date, and every attribute set —
        // so the window filled itself in and looked authoritative. A row can go
        // between being listed and being asked about, so refusing the bin and
        // Recent is not enough on its own; this is a race as well as a gate.
        var live = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

        if (live.Count == 0)
        {
            if (_shell.ActiveTab is { } gone)
                gone.Status = paths.Count == 1
                    ? $"{PathRules.LeafName(paths[0])} is no longer there"
                    : "those items are no longer there";

            return;
        }

        paths = live;

        // **The desktop's own dialog wins where it has one.** On Windows that
        // sheet carries Security, Details and the Unblock checkbox, and hosts
        // the pages other applications add to the shell — none of which this
        // application can reproduce, and all of which are why somebody opens
        // properties there.
        //
        // One path only. The shell has SHMultiFileProperties for a selection,
        // but it wants an ITEMIDLIST array rather than paths and shows a
        // reduced sheet; a multi-select falls through to Vaktari's window,
        // which handles several items properly already.
        if (paths.Count == 1 && _properties.ShowSystemDialog(paths[0])) return;

        // Theme and metrics are application-scoped, so this inherits them.
        new PropertiesWindow(new PropertiesViewModel(_properties, paths, _accessEditor)).Show(this);
    }
}
