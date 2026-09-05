using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The menu behind a crumb: the folders inside that ancestor, so the path bar
/// goes DOWN as well as up.
///
/// **Nothing in the application enumerated a crumb.** Every crumb was one
/// command — navigate to this ancestor — and the separator beside it was a
/// TextBlock. So the shortest move in a file manager, from a folder to the one
/// beside it, cost a click on the parent, a full listing of it, and a hunt down
/// the rows for a name the bar was already showing. Explorer hangs that folder's
/// subfolders off the chevron after each crumb, and Dolphin off the same spot;
/// this is that menu.
///
/// The read is the crumb's own directory read, so what the menu offers and what
/// navigating there would show are one answer: the same hidden-file rule, the
/// same order, the same This PC listing of drives.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// How many folders a crumb menu shows.
    ///
    /// **The read is not what this caps — the popup is.** Reading the folder
    /// costs what navigating into it would cost anyway, and it has to finish
    /// before anything can be sorted; but a menu with a row per subfolder of
    /// C:\Windows\WinSxS is not a menu, it is a listing drawn in a popup with
    /// none of the listing's columns, sorting or selection.
    /// </summary>
    private const int CrumbMenuLimit = 100;

    /// <summary>
    /// The crumb menus with a read already in flight.
    ///
    /// **A second press started a second enumeration.** The read takes
    /// <c>CancellationToken.None</c>, so nothing takes it back once it is
    /// running, and every chevron in the bar is one press away from a share
    /// that is not answering — the same shape <c>ReloadExpandedAsync</c>
    /// measured, where three refreshes against a folder that had not answered
    /// left three un-cancellable enumerations in flight. A press that arrives
    /// while this menu is still filling is dropped: the read already running is
    /// reading the same folder from the same disk, and its answer lands in this
    /// very collection.
    ///
    /// Keyed by the MENU rather than by the folder, which is where this differs
    /// from <c>_opening</c>. A path appears once in a bar, so within one bar
    /// the two are the same guard — but a navigation rebuilds Breadcrumbs, and
    /// keying by folder then blocked the new crumb on a read whose answer goes
    /// into the old one's collection, leaving a menu on "reading…" with nothing
    /// coming. Measured that way round first: see
    /// <c>A_crumb_the_bar_rebuilt_is_not_blocked_by_the_old_ones_read</c>.
    /// </summary>
    private readonly HashSet<ObservableCollection<PathSegment>> _crumbMenusFilling = [];

    /// <summary>
    /// One crumb, with the command that navigates to it and the command that
    /// lists what is inside it.
    ///
    /// The collection is made HERE and closed over rather than reached through
    /// the segment, because the segment does not exist yet while its own
    /// commands are being built — and a record whose Children could be swapped
    /// afterwards would let a crumb fill a menu that is no longer the one on
    /// screen.
    /// </summary>
    private PathSegment Crumb(string name, string target, bool isLast)
    {
        var children = new ObservableCollection<PathSegment>
        {
            // **The popup opens before the read answers.** The press does both
            // in one gesture, and the directory read reaches the menu at least
            // one continuation later. Measured in the real window: a flyout
            // opened on an empty collection laid out 2px wide by 32 high and
            // jumped to 216 by 64 when the two rows landed — a sliver beside
            // the bar that then grew out from under the pointer. With this row
            // it opens at 160 by 37 and settles.
            Note("reading…"),
        };

        return new PathSegment(
            name, target,
            new RelayCommand(() => Detached(NavigateAsync(target), "navigate")),
            isLast)
        {
            Children = children,
            Menu = new RelayCommand(
                () => Detached(FillCrumbMenuAsync(target, children), "crumb-menu")),
        };
    }

    /// <summary>
    /// A row that says something rather than going somewhere.
    ///
    /// Disabled through CanExecute rather than left with an empty command:
    /// **a menu row that lights up under the pointer is a promise**, and
    /// "no folders in here" that highlights and then swallows the click reads
    /// as the application having failed rather than as an answer.
    /// </summary>
    private static PathSegment Note(string text)
        => new(text, "", new RelayCommand(() => { }, () => false), IsLast: false);

    /// <summary>
    /// Refills one crumb's menu.
    ///
    /// Cleared and rebuilt on every press rather than read once and kept: a
    /// crumb lives as long as the path bar shows that path, which is as long as
    /// you stay in the folder, and a menu that answered from the first press
    /// would go on offering a folder somebody has since renamed or deleted.
    /// </summary>
    internal async Task FillCrumbMenuAsync(
        string folder, ObservableCollection<PathSegment> into)
    {
        // One read per menu at a time — see _crumbMenusFilling for what a
        // second press did without this.
        if (!_crumbMenusFilling.Add(into)) return;

        List<FileEntry>? folders;

        try
        {
            folders = await FoldersInAsync(folder).ConfigureAwait(true);
        }
        finally
        {
            // In a finally, so a read that threw its way out does not leave the
            // crumb refusing to answer any later press.
            _crumbMenusFilling.Remove(into);
        }

        into.Clear();

        // **A refusal must not read as an empty folder.** The read answers null
        // for a folder there are no rights to, and reporting that as "no folders
        // in here" would be the application stating, in a menu, something it
        // does not know.
        if (folders is null)
        {
            into.Add(Note("could not read this folder"));
            return;
        }

        if (folders.Count == 0)
        {
            into.Add(Note("no folders in here"));
            return;
        }

        var shown = Math.Min(folders.Count, CrumbMenuLimit);

        for (var i = 0; i < shown; i++)
        {
            var target = folders[i].FullPath;

            into.Add(new PathSegment(
                folders[i].Name, target,
                new RelayCommand(() => Detached(NavigateAsync(target), "navigate")),
                IsLast: false));
        }

        // **The cap was reached in silence**, the fault the search band already
        // carries a line for: a menu that stopped at a hundred looked exactly
        // like a folder that held a hundred, and the rows are alphabetical, so
        // everything past the hundredth name was gone with nothing saying so.
        // Last rather than first, because it is what the rows above ran out
        // into.
        if (folders.Count > shown)
            into.Add(Note($"showing the first {shown:N0} of {folders.Count:N0}"));
    }

    /// <summary>
    /// The folders one crumb holds, in the order the listing would show them.
    ///
    /// Null for a folder that could not be read, which is a message rather than
    /// a crash — a crumb is one press away from a permission error on every
    /// platform, and This PC's own crumb is one press away from a share that is
    /// not answering.
    ///
    /// <c>CancellationToken.None</c>, like <c>ReadChildrenAsync</c>: the pane's
    /// token source is disposed by the next navigation, and this read belongs
    /// to a menu rather than to the listing on screen.
    ///
    /// That method pairs the token with a generation re-check and this one does
    /// not, because there is nothing here for a stale answer to reach. A
    /// navigation rebuilds <c>Breadcrumbs</c> from scratch, so the crumb whose
    /// menu was filling is gone and the collection the fill lands in is one
    /// nothing on screen is bound to any more — measured in
    /// <c>A_fill_that_lands_after_a_navigation_has_nowhere_to_land</c>. What
    /// this does share is the other guard: only one read per menu at a time,
    /// see <see cref="_crumbMenusFilling"/>.
    /// </summary>
    private async Task<List<FileEntry>?> FoldersInAsync(string folder)
    {
        var options = new ListingOptions { IncludeHidden = ShowHidden };
        var found = new List<FileEntry>();

        // The same branch the listing takes, so the machine crumb offers the
        // drives rather than nothing: This PC is not a directory, and handing
        // "vaktari:computer" to the filesystem provider would have thrown and
        // left the menu saying the machine could not be read.
        var source = folder == VirtualPaths.Computer
            ? ComputerListing.EnumerateAsync(Places, CancellationToken.None)
            : _fs.EnumerateAsync(folder, options, CancellationToken.None);

        try
        {
            await foreach (var batch in source.ConfigureAwait(false))
                foreach (var entry in batch)
                {
                    // **Folders only.** The menu is how you go somewhere from
                    // the bar; a file in it would either do nothing or launch
                    // something, and a crumb menu that can launch a program is
                    // not what anybody pressing a separator is asking for.
                    if (entry.IsDirectory) found.Add(entry);
                }
        }
        catch (Exception ex)
        {
            // NO KILLING MUTATION, and it was looked for: replacing this with
            // `_ = ex;` left all twenty-six tests in CrumbMenuTests green,
            // because the only test that reaches a refused folder asserts on
            // the row the menu shows rather than on the log. It stays for the
            // reason every other Swallowed call does — a failure nothing
            // records is a failure nobody can diagnose.
            Vaktari.Core.Quiet.Swallowed("crumb-menu", ex);
            return null;
        }

        // The pane's own within-folder order, so the menu lists what the
        // listing would list in the order the listing would list it — including
        // the direction, and including the natural-sorting and case preferences
        // that only this comparer knows about.
        found.Sort(CompareWithin);

        // Everything, uncapped: the cap belongs to the menu rather than to the
        // read, and it is applied AFTER this sort by the caller. Capping HERE
        // would have handed back whichever hundred the filesystem happened to
        // return first and then sorted those, so the menu would be missing
        // folders from the middle of the alphabet — and the caller would have
        // no count to say how many it was missing.
        return found;
    }
}
