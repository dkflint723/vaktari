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
    /// Human-readable size. Folders get an em dash rather than "0", which is
    /// actively misleading: a folder is not empty just because its own inode
    /// has no length.
    /// </summary>
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

    public static readonly IValueConverter Size =
        new FuncValueConverter<FileEntry, string>(entry =>
        {
            if (entry.FullPath is null) return "";
            if (entry.IsDirectory) return "—";

            // The sixth and last copy of this. It was the only one already
            // using binary unit names, which is why the Size column and the
            // status bar beside it disagreed about the same file.
            return ByteSize.Format(entry.Length);
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
