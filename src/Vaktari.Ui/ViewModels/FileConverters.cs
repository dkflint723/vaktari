using Avalonia.Data.Converters;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Presentation of the raw values in <see cref="FileEntry"/>.
///
/// The entry deliberately carries a raw <c>long</c> and a <c>DateTimeOffset</c>
/// — it is produced by the enumerator and must stay allocation-free — so the
/// formatting belongs here rather than in the model.
/// </summary>
public static class FileConverters
{
    /// <summary>
    /// The folder an entry lives in, with the home directory shown as `~`.
    ///
    /// Only used by the recent listings, where rows span the whole filesystem
    /// and a bare filename identifies nothing. Abbreviating home is what makes
    /// the column narrow enough to be worth having — most rows are under it.
    /// </summary>
    /// <summary>
    /// The full path for the recent listing's Path column, or nothing when the
    /// user has turned row tooltips off.
    ///
    /// **"Show tooltips on rows" only ever suppressed one of the two.** The
    /// setting was read in exactly one place — the modified column's age
    /// description — while this column went on popping the full path over
    /// every hover regardless. A preference that silences some tooltips and
    /// not others is worse than one that does nothing, because the ones that
    /// remain look like a fault rather than a setting.
    ///
    /// Gated in the converter for the same reason the age one is: a null Tip
    /// shows no tooltip, so the preference costs a line and no binding
    /// gymnastics, and reading it live means turning it off takes effect on
    /// the next hover rather than the next launch.
    /// </summary>
    public static readonly IValueConverter PathTip =
        new FuncValueConverter<FileEntry, string?>(entry =>
            Settings.AppSettings.Current.General.ShowTooltips ? entry.FullPath : null);

    /// <summary>
    /// The name as a listing shows it.
    ///
    /// **A shortcut listed as "Chrome.lnk".** Windows marks lnkfile
    /// NeverShowExt, so Desktop and the Start Menu — folders that are nothing
    /// but shortcuts — read here as a wall of ".lnk" while every other window
    /// on the machine showed plain names. The sidebar already agreed with
    /// Explorer, and the shortcut writer's own comment claimed the listing did
    /// too; only the listing did not.
    /// </summary>
    public static readonly IValueConverter DisplayName =
        new FuncValueConverter<FileEntry, string>(FileKind.DisplayName);

    /// <summary>
    /// The whole filename, for a name that did not fit its column.
    ///
    /// **A trimmed name could only be read by renaming it.** The name is the
    /// one thing in a row with no tooltip: the modified column explains its
    /// shading, the look-alike chip explains itself, the recent listing's path
    /// column pops the full path — and the name, which is the only column that
    /// ellipsizes, said nothing. In a narrow split pane, or a grid tile with two
    /// lines, "Q3-forecast-…-final.xlsx" could be read only by pressing F2 to
    /// see the edit box and Escape to get out again.
    ///
    /// The bare name, not name-and-path: the path already has its own tip in
    /// the one listing whose rows span the filesystem, and two tips saying
    /// overlapping things would disagree about width and content.
    ///
    /// Gated in the converter for the same reason PathTip is.
    /// </summary>
    public static readonly IValueConverter NameTip =
        new FuncValueConverter<FileEntry, string?>(entry =>
            Settings.AppSettings.Current.General.ShowTooltips
            && !string.IsNullOrEmpty(entry.Name)
                ? entry.Name
                : null);

    /// <summary>
    /// What one listing row is called, for anything reading the window rather
    /// than looking at it.
    ///
    /// **A row read out as the record's ToString.** With no name of its own and
    /// a template that is not one piece of text, a container falls back to the
    /// item, so every row announced "FileEntry { Name = report.txt, FullPath =
    /// /a/report.txt, Length = 1, LastWriteTime = ..., IsDirectory = False, ... }"
    /// — ten fields, the filename second. Everything AROUND the listing was
    /// named: the breadcrumbs, the four sort headers, the sidebar places and
    /// their group headings. The rows, which are most of the window, were not.
    ///
    /// The DISPLAY name, so what is read matches what is drawn: a Windows
    /// shortcut loses its .lnk in both places or in neither. Deliberately not
    /// NameTip, which is a tooltip and is gated on the ShowTooltips setting —
    /// switching tooltips off is a preference about the mouse and must not take
    /// a row's name away with it.
    ///
    /// Folder and link are said because a row carries neither fact in text: the
    /// folder icon and the corner link emblem are both artwork, and the type
    /// column that would say "folder" is optional and exists in one layout of
    /// the three.
    ///
    /// Null rather than "" when there is no name to give. Measured: a row whose
    /// AutomationProperties.Name is "" reads as nothing at all, while a row with
    /// no name at least falls back to the item. FileKind.DisplayName answers ""
    /// for an entry with no name, so the guard keeps that from silencing a row
    /// outright.
    ///
    /// Not about an unbound container: a null DataContext never reaches this
    /// lambda at all, because FuncValueConverter answers UnsetValue for a null
    /// against a value-type input. Measured too.
    /// </summary>
    public static readonly IValueConverter RowName =
        new FuncValueConverter<FileEntry, string?>(entry =>
        {
            var name = FileKind.DisplayName(entry);

            if (string.IsNullOrEmpty(name)) return null;

            if (entry.IsDirectory) name += ", folder";

            return entry.IsSymlink ? name + ", link" : name;
        });

    public static readonly IValueConverter ParentPath =
        new FuncValueConverter<FileEntry, string>(entry =>
        {
            if (string.IsNullOrEmpty(entry.FullPath)) return "";

            // Normalised first, so the comparisons below see one spelling of the
            // separator. On Windows a path can arrive with either.
            var parent = PathRules.Parent(entry.FullPath);
            if (string.IsNullOrEmpty(parent)) return PathRules.LeafName(entry.FullPath);

            var home = PathRules.Normalise(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (!string.IsNullOrEmpty(home))
            {
                if (PathRules.Same(parent, home)) return "~";

                // The separator is part of the test on purpose: without it,
                // "/home/flintstone" would match a home of "/home/flint".
                // Through the platform's own constant, and case-insensitively on
                // Windows, for the same reasons PathRules.Same exists.
                if (parent.StartsWith(home + Path.DirectorySeparatorChar, PathRules.Comparison))
                    return "~" + parent[home.Length..];
            }

            return parent;
        });

    /// <summary>
    /// What kind of thing the row is. Folders say so, and everything else is
    /// named by its extension, because that is the only answer available
    /// without asking the platform once per file.
    /// </summary>
    public static readonly IValueConverter Kind =
        new FuncValueConverter<FileEntry, string>(entry =>
            entry.FullPath is null ? "" : FileKind.Describe(entry));

    /// <summary>
    /// Local time, and compact. The default rendering of a DateTimeOffset is a
    /// full timestamp with a UTC offset — accurate, unreadable in a column, and
    /// wrong for a person looking at their own files.
    /// </summary>
    public static readonly IValueConverter Modified =
        new FuncValueConverter<DateTimeOffset, string>(value =>
        {
            // **Nothing, rather than the epoch.** A drive has no meaningful
            // modified time, and the This PC listing says so by carrying the
            // epoch — which rendered as "31 Dec 1969" in the column, a date
            // that reads as real and is not. The trash uses MinValue for a
            // deletion date it could not parse, which is the same "no answer".
            if (value <= DateTimeOffset.UnixEpoch) return "";

            var local = value.ToLocalTime();
            var now = DateTimeOffset.Now;

            // Absolute is one fixed shape regardless of when the file is from,
            // which is what you want when comparing dates rather than reading
            // them.
            if (Settings.AppSettings.Current.Views.Details.DateStyle
                == Core.Settings.DateStyle.Absolute)
                return local.ToString("yyyy-MM-dd HH:mm");

            // Relative: today gets a time, this year drops the year, older keeps
            // it. Relative in the sense that matters — it omits what you can
            // infer from today's date — and it earns column width back.
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Year == now.Year) return local.ToString("dd MMM HH:mm");

            return local.ToString("dd MMM yyyy");
        });

    /// <summary>
    /// Upper-cases a label for display only. The sidebar's group headings are
    /// set in small caps with tracking; <c>Place.Label</c> is data read off the
    /// desktop's places list and is never rewritten to suit a heading.
    /// </summary>
    /// <summary>
    /// Ghosts a hidden or system file, the way both references do.
    ///
    /// **With "show hidden files" on, they looked exactly like real content.**
    /// desktop.ini, thumbs.db, .DS_Store and every dotfile sat in the listing
    /// at full strength, indistinguishable from the folder's actual contents —
    /// which is the whole reason the setting is off by default and the whole
    /// reason turning it on is survivable elsewhere.
    ///
    /// Opacity rather than a colour, for the same reason the cut mark uses it:
    /// it survives every theme, reads the same on a selected row as an
    /// unselected one, and does not have to be undone for the icon and each
    /// column separately.
    ///
    /// **And a drive that was not there was drawn like a live one.** This PC
    /// lists an unmounted volume and a disconnected mapped drive in place, on
    /// purpose — a row that vanishes is worse than a row you cannot open — but
    /// nothing said which was which, so the only way to find out was to click
    /// it and wait out the timeout. The same ghosting, because it means the
    /// same thing: this row is here, and it is not ordinary content. The name
    /// stays HiddenFade because it is the one Opacity all three layouts bind.
    /// </summary>
    public static readonly IValueConverter HiddenFade =
        new FuncValueConverter<FileEntry, double>(entry =>
            entry.FullPath is not null
            && (entry.IsHidden || (entry.Flags & EntryFlags.System) != 0
                || entry.IsUnreadable)
                ? 0.55
                : 1.0);

    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string>(s => s?.ToUpperInvariant() ?? "");

    /// <summary>
    /// A wash of the accent behind the open place's row. Nothing but the edge
    /// bar carried "this is where you are", and on a one-line row that bar is a
    /// 2x14px mark — too small to find at a glance.
    /// </summary>
    public static readonly IValueConverter CurrentRowFill =
        new FuncValueConverter<bool, Avalonia.Media.IBrush?>(current =>
            current && Avalonia.Application.Current?.Resources["AccentDim"]
                is Avalonia.Media.ISolidColorBrush accent
                // 7% — the design's own rgba(...,.07). Enough to find the row,
                // not enough to read as a selection.
                ? new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(18, accent.Color.R, accent.Color.G, accent.Color.B))
                : Avalonia.Media.Brushes.Transparent);

    /// <summary>
    /// Dims a row that has been cut and not yet pasted, the way Explorer does.
    ///
    /// Opacity rather than a colour: it survives every theme, reads the same on
    /// a selected row as on an unselected one, and does not have to be undone
    /// for the icon, the name and the size columns separately.
    ///
    /// **Both values matter.** The path says which row this is; the set is what
    /// changes when something is cut, and binding to it is what makes every
    /// visible row re-evaluate at that moment.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter CutFade =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, double>(values =>
        {
            var pair = values.ToList();

            return pair.Count == 2
                   && pair[0] is string path
                   && pair[1] is IReadOnlySet<string> cut
                   && cut.Contains(path)
                ? 0.45
                : 1.0;
        });

    /// <summary>
    /// Whether to mark a row as sharing its look with another.
    ///
    /// Same shape as CutFade and for the same reason: the row supplies its
    /// path, the pane supplies the set, and binding the set is what makes every
    /// visible row re-evaluate when a listing changes.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter Confusable =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, bool>(values =>
        {
            var pair = values.ToList();

            return pair.Count == 2
                   && pair[0] is string path
                   && pair[1] is IReadOnlySet<string> confusable
                   && confusable.Contains(path);
        });

    /// <summary>
    /// How far in a row sits, for a folder opened in place.
    ///
    /// Same shape as CutFade and Confusable: the row supplies its path, the
    /// pane supplies the map, and binding the MAP is what makes every realized
    /// row re-measure the moment a folder is opened or closed.
    ///
    /// Zero for every row of the folder itself — the map holds only the rows
    /// that came from inside something — so an ordinary listing draws a
    /// zero-width spacer and looks exactly as it did.
    ///
    /// The map already holds PIXELS. The step scales with the pane's icon zoom,
    /// and the pane is the only thing that knows its own zoom, so multiplying
    /// here would need that scale bound in as a third value for no gain.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter Indent =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, double>(values =>
        {
            var pair = values.ToList();

            return pair.Count == 2
                   && pair[0] is string path
                   && pair[1] is IReadOnlyDictionary<string, double> indents
                   && indents.TryGetValue(path, out var width)
                ? width
                : 0;
        });

    /// <summary>The triangle a shut folder shows: pointing along the row, at
    /// what opening it would reveal.</summary>
    private static readonly Avalonia.Media.Geometry Shut =
        Avalonia.Media.Geometry.Parse("M 3,1 L 8,5.5 L 3,10 Z");

    /// <summary>And the one an open folder shows, turned a quarter to point at
    /// the rows it has put underneath itself.</summary>
    private static readonly Avalonia.Media.Geometry Open =
        Avalonia.Media.Geometry.Parse("M 1,3 L 10,3 L 5.5,8 Z");

    /// <summary>
    /// Which way a row's triangle points, or nothing at all for a row that is
    /// not a folder.
    ///
    /// **One Path with two shapes rather than two Paths that appear and
    /// disappear.** Decoration that comes and goes under the pointer changes
    /// what the second click of a double-click lands on, and Avalonia's
    /// double-tap gesture requires both clicks on the same element — the rule
    /// MarkupRulesTests states in general. A Path whose Data is null draws
    /// nothing and stays exactly where it was.
    ///
    /// Three values rather than two: the pane's set says which folders are
    /// open, and the row's own flag is what separates "shut" from "not a
    /// folder", which the set alone cannot.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter Twisty =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, Avalonia.Media.Geometry?>(
            values =>
            {
                var parts = values.ToList();

                if (parts.Count != 3 || parts[1] is not true) return null;

                return parts[0] is string path
                       && parts[2] is IReadOnlySet<string> open
                       && open.Contains(path)
                    ? Open
                    : Shut;
            });

    /// <summary>
    /// Whether this row is the folder a drop would land in.
    ///
    /// **Nothing said where a drop was going.** The whole pane took an outline
    /// while a drag was over it, and that is the one thing never in doubt — what
    /// you cannot tell is whether releasing puts the files in the folder under
    /// the pointer or in the folder being listed, and those are different
    /// places. Both references ring the row.
    ///
    /// Same shape as CutFade and Confusable: the row supplies its path, the
    /// pane supplies the target, and binding the pane's property is what makes
    /// every visible row re-evaluate as the pointer moves.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter DropRing =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, bool>(values =>
        {
            var pair = values.ToList();

            return pair.Count == 2
                   && pair[0] is string path
                   && pair[1] is string target
                   && target.Length > 0
                   && Vaktari.Core.FileSystem.PathRules.Same(path, target);
        });

    /// <summary>
    /// The sidebar row's fill, at three strengths of one accent: the place a
    /// drop would land in, the place you are in, and the place you are
    /// somewhere inside.
    ///
    /// **A place gave no sign whatever that it was a target.** Dragging onto
    /// "Downloads" looked exactly like dragging past it, so the only way to
    /// learn where the files went was to release and go and look. The rows are
    /// one line tall and stacked, which is precisely where a target has to say
    /// which one it is.
    ///
    /// **And one folder down, no row was marked at all** — the mark was an
    /// exact path match, so the sidebar went blank the moment you opened a
    /// folder inside Documents. The holding row takes the faintest of the
    /// three; it is a hint about where you came from, not a claim to be the
    /// place, and it must not compete with the row that IS one.
    ///
    /// Read positionally rather than by name because a MultiBinding hands over
    /// an ordered list: index 0 is here, 1 is the drop, 2 is the holder. Each
    /// is read with a bounds check, so the two-value calls that predate the
    /// third still answer.
    ///
    /// A fill rather than a ring: a border appearing on one row would move
    /// every row below it, and the row you are aiming at is the one that must
    /// hold still.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter PlaceRowFill =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, object?>(values =>
        {
            var flags = values.ToList();

            var accent = Avalonia.Application.Current?.Resources["AccentDim"]
                as Avalonia.Media.ISolidColorBrush;

            if (accent is null) return Avalonia.Media.Brushes.Transparent;

            bool Set(int slot) => flags.Count > slot && flags[slot] is true;

            // 28% for a drop target against the current row's 7% and the
            // holder's 3.5%: the target has to read at a glance from the corner
            // of the eye while a pointer carrying files is somewhere else, and
            // the holder must stay quieter than the row it stands in for.
            byte alpha = Set(1) ? (byte)72
                       : Set(0) ? (byte)18
                       : Set(2) ? (byte)9
                       : (byte)0;

            return alpha == 0
                ? Avalonia.Media.Brushes.Transparent
                : new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(alpha, accent.Color.R, accent.Color.G, accent.Color.B));
        });

    /// <summary>
    /// The same answer as <see cref="DropRing"/>, as a brush.
    ///
    /// A brush rather than a visibility so the ring can keep a constant
    /// thickness: a border that appears and disappears reflows the row under
    /// the pointer, which is the one row that must hold still while you aim at
    /// it.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter DropRingBrush =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, object?>(values =>
        {
            var pair = values.ToList();

            var here = pair.Count == 2
                       && pair[0] is string path
                       && pair[1] is string target
                       && target.Length > 0
                       && Vaktari.Core.FileSystem.PathRules.Same(path, target);

            return here
                ? Avalonia.Application.Current?.Resources["AccentColour"]
                : Avalonia.Media.Brushes.Transparent;
        });

    /// <summary>
    /// Whether this row is the one whose name is being edited in place.
    ///
    /// Shaped like <see cref="DropRingBrush"/> above and for the same reason:
    /// the pane holds ONE path and every realized row compares itself against
    /// it, so there is no per-row flag for a refresh to throw away.
    ///
    /// An empty target is nobody, not everybody. "" is what the pane holds when
    /// no rename is open, and <c>PathRules.Same("", "")</c> answers true —
    /// measured by dropping the length test and watching the converter's own
    /// test go red — so without it a row carrying no path yet, which is what a
    /// container bound to a default entry has, would open an edit box while
    /// nothing had asked for one.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter Renaming =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, bool>(Editing);

    /// <summary>
    /// The other half: the drawn name steps aside for the box that replaces it.
    ///
    /// A second converter rather than an inversion at the binding, because
    /// Avalonia's `!` only negates a bound BOOLEAN path and this is a
    /// two-value comparison.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter NotRenaming =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, bool>(values => !Editing(values));

    /// <summary>
    /// The refusal, held open on the ONE row it belongs to.
    ///
    /// **It opened on every realized row.** The reason is a pane-level flag and
    /// the box is stamped once per row, so binding the tip's IsOpen straight to
    /// it put a popup on all of them: measured on a 42-entry folder in details
    /// view, one refused name gave 30 open tooltips, 29 of them over invisible
    /// boxes whose own bounds are zero — a stack of identical popups at the top
    /// of the listing. Grid measured the same. So the tip is gated on the same
    /// row comparison the box's own visibility is.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IMultiValueConverter RefusedHere =
        new Avalonia.Data.Converters.FuncMultiValueConverter<object?, bool>(values =>
        {
            var all = values.ToList();

            return all.Count == 3 && all[2] is true && Editing(all.Take(2));
        });

    private static bool Editing(IEnumerable<object?> values)
    {
        var pair = values.ToList();

        return pair.Count == 2
               && pair[0] is string path
               && pair[1] is string target
               && target.Length > 0
               && Vaktari.Core.FileSystem.PathRules.Same(path, target);
    }

    /// <summary>Accent along the active side's tab bar, transparent on the other.</summary>
    public static readonly IValueConverter ActiveEdge =
        new FuncValueConverter<bool, object?>(active =>
            Avalonia.Application.Current?.Resources[active ? "AccentColour" : "EdgeHighlight"]);

    /// <summary>The current folder is the one you are in; the ancestors are
    /// links. Weight carries that, so it still reads without colour.</summary>
    public static readonly IValueConverter CrumbWeight =
        new FuncValueConverter<bool, Avalonia.Media.FontWeight>(
            isLast => isLast ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    public static readonly IValueConverter CrumbBrush =
        new FuncValueConverter<bool, object?>(isLast =>
            Avalonia.Application.Current?.Resources[isLast ? "ViewText" : "ViewDimText"]);
}
