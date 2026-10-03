using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// This window's half of the folder hand-over: every tab it holds, and every
/// path it keeps that names a folder, for <see cref="FolderHandover"/> to let
/// go of before a folder moves and to carry to where it went afterwards.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Every live tab in this window, both halves of a split.
    ///
    /// **A background tab holds its watch exactly as the one on screen does**
    /// — which is why the eject and the disconnect walk every tab too — so the
    /// hand-over has to reach all of them, not the active one.
    /// </summary>
    internal IEnumerable<PaneViewModel> AllTabs
        => new[] { Left, Right }.OfType<PaneGroupViewModel>().SelectMany(g => g.Tabs).ToList();

    /// <summary>
    /// Carries what this window remembers about places that are not open
    /// tabs — tabs closed on either side, the right half of a split that was
    /// closed, the folder tree, and the links shared from a folder — from
    /// <paramref name="from"/> to <paramref name="to"/>.
    ///
    /// **Ctrl+Shift+T put a tab back on a folder that had been renamed in the
    /// same window**, and reopening the split did the same: both are kept as
    /// TabStates, which nothing told. A renamed folder is the same place
    /// under another name, so they follow it the way the open tabs do.
    /// </summary>
    internal void FollowRemembered(string from, string to)
    {
        Left.FollowClosed(from, to);
        Right?.FollowClosed(from, to);

        if (_rememberedRight is { } remembered)
            _rememberedRight = remembered with { Tabs = [.. remembered.Tabs.Select(t => Followed(t, from, to))] };

        Sidebar.Tree?.Follow(from, to);

        FollowDriveLinks(from, to);
    }

    /// <summary>
    /// A cut waiting to be pasted, carried from <paramref name="from"/> to
    /// <paramref name="to"/> — on the clipboard as well as in the marks.
    ///
    /// **The marks alone would have lied** (review finding 18): the rows would
    /// show as cut at their new paths while the clipboard still named the old
    /// ones, and Paste then said they were not there. The clipboard is
    /// rewritten only while it still holds exactly the cut Vaktari made — the
    /// same paths, as a cut — because anything else on it is somebody else's
    /// and not this window's to change. Otherwise the marks go, since the cut
    /// they showed is no longer the one on the clipboard.
    /// </summary>
    internal async Task FollowCutAsync(string from, string to)
    {
        var marked = CutMarks.Paths;

        if (!marked.Any(p => PathRules.Contains(from, p))) return;

        var moved = marked.Select(p => PathRules.Rebase(p, from, to) ?? p).ToList();

        try
        {
            if (_clipboard is { } clipboard
                && await clipboard.GetFilesAsync().ConfigureAwait(true) is { Action: ClipboardAction.Cut } held
                && held.Paths.Count == marked.Count
                && held.Paths.All(marked.Contains)
                && await clipboard.SetFilesAsync(ClipboardAction.Cut, moved).ConfigureAwait(true))
            {
                CutMarks.Mark(moved);
                return;
            }
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("handover", ex);
        }

        CutMarks.Clear();
    }

    /// <summary>
    /// A saved tab, as it reads once <paramref name="from"/> has become
    /// <paramref name="to"/>: where it is and both of its histories.
    /// </summary>
    internal static TabState Followed(TabState tab, string from, string to)
    {
        static string Moved(string path, string from, string to)
            => VirtualPaths.Rebase(path, from, to) ?? path;

        return tab with
        {
            Path = Moved(tab.Path, from, to),

            // Null in a session written before tabs kept a history — see
            // PaneViewModel.RestoreFrom — and left null rather than invented.
            BackStack = ReferenceEquals(tab.BackStack, null)
                ? tab.BackStack!
                : [.. tab.BackStack.Select(p => Moved(p, from, to))],
            ForwardStack = ReferenceEquals(tab.ForwardStack, null)
                ? tab.ForwardStack!
                : [.. tab.ForwardStack.Select(p => Moved(p, from, to))],
        };
    }
}
