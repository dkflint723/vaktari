using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Vcs;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Draws a one-character version-control marker on a row.
///
/// Same viewport-driven attached-property shape as thumbnails, metadata and
/// thumbnails: the list virtualizes, so attaching to the realized control keeps the
/// work proportional to what is on screen and the collection holds nothing
/// extra. **`FileEntry` is never widened for this.**
///
/// **A STATIC store, like every other row decorator here.** That looks wrong
/// for a split view until you notice the states are keyed by ABSOLUTE PATH —
/// two panes in two different repositories cannot collide, because no path is
/// in both folders.
/// </summary>
public static class RowVcs
{
    /// <summary>
    /// Latest snapshot per folder. Bounded, because a session that walks a
    /// large tree would otherwise accumulate one entry per folder visited for
    /// the life of the process.
    /// </summary>
    private const int MaxFolders = 32;

    private static readonly Dictionary<string, IReadOnlyDictionary<string, VcsState>> Folders =
        new(StringComparer.Ordinal);

    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();

    /// <summary>
    /// Raised when a snapshot lands. Status is fetched AFTER the listing is on
    /// screen — deliberately, since it can take seconds on a large repository —
    /// so rows are already realized by the time an answer arrives and must be
    /// told to look again.
    /// </summary>
    public static event EventHandler? Changed;

    public static void Publish(string folder, IReadOnlyDictionary<string, VcsState>? states)
    {
        lock (Gate)
        {
            // A null snapshot means the query failed or the folder is not in a
            // repository. Either way the previous answer for this folder is no
            // longer trustworthy, so it is REMOVED rather than left standing —
            // a failed query must draw nothing, never "everything is clean".
            if (states is null || states.Count == 0)
            {
                Folders.Remove(folder);
            }
            else
            {
                if (!Folders.ContainsKey(folder)) Order.Enqueue(folder);
                Folders[folder] = states;

                while (Order.Count > MaxFolders && Order.TryDequeue(out var oldest))
                    if (!string.Equals(oldest, folder, StringComparison.Ordinal))
                        Folders.Remove(oldest);
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static VcsState StateFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return VcsState.None;

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return VcsState.None;

        lock (Gate)
        {
            return Folders.TryGetValue(folder, out var states)
                   && states.TryGetValue(path, out var state)
                ? state
                : VcsState.None;
        }
    }

    /// <summary>
    /// **The letter carries the meaning; the colour is decoration.** That is the
    /// standing rule here — hue never carries meaning alone, because a
    /// colourblind reader would lose it entirely, and these are the same
    /// Okabe-Ito palette, chosen to stay distinguishable to colour-blind readers.
    ///
    /// `Unmodified` draws NOTHING. Marking every clean file in a repository
    /// would be noise in exactly the folders where the feature matters most.
    /// </summary>
    private static (string Glyph, string Colour) Mark(VcsState state)
    {
        // **The palette was chosen for a dark listing and used on both.** These
        // are the Okabe-Ito colourblind-safe hues, which is why they were
        // picked — but on a white listing M measured 2.20:1, ? 2.25 and ! 2.99,
        // all far under the 4.5:1 a single letter at 11px needs. The letter is
        // the signal here and the colour is the decoration, so an illegible
        // letter is the whole mark gone.
        //
        // Same hues, walked down for the light ground rather than replaced:
        // the point of this palette is that the hues stay distinguishable to a
        // colourblind reader, and swapping them would give that up to fix
        // contrast.
        var dark = IsDarkListing;

        return state switch
        {
            VcsState.Modified   => ("M", dark ? "#E69F00" : "#8A6100"),
            VcsState.Added      => ("A", dark ? "#009E73" : "#006147"),
            VcsState.Deleted    => ("D", dark ? "#D55E00" : "#8C3E00"),
            VcsState.Untracked  => ("?", dark ? "#56B4E9" : "#1F6F9E"),
            VcsState.Conflicted => ("!", dark ? "#CC79A7" : "#8E4571"),
            _                   => ("", ""),
        };
    }

    /// <summary>
    /// Whether the listing is dark, so a mark can be drawn to be read on it.
    ///
    /// Asked of the resolved theme rather than of a setting: the palette can
    /// come from the desktop, and the only honest question is what colour the
    /// surface actually ended up.
    /// </summary>
    private static bool IsDarkListing
    {
        get
        {
            if (Avalonia.Application.Current?.Resources
                    .TryGetResource("ViewBackground", null, out var value) == true
                && value is Avalonia.Media.ISolidColorBrush brush)
            {
                // Rec. 601 luma is enough to answer "is this dark"; nothing here
                // needs the full relative-luminance ramp.
                var c = brush.Color;

                return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            }

            return true;
        }
    }

    /// <summary>
    /// For a container that must DISAPPEAR when there is no mark, rather than
    /// merely holding an empty string.
    ///
    /// Details and compact reserve a fixed-width slot so names stay aligned, and
    /// there the empty marker is the point. A grid tile has no column to keep
    /// aligned — an empty chip sitting on every clean thumbnail would be litter.
    /// Two properties rather than one guessing from its parent, following
    /// `RowMetadata`, which carries `Entry` and `Access` for the same reason.
    /// </summary>
    public static readonly AttachedProperty<FileEntry?> BadgeProperty =
        AvaloniaProperty.RegisterAttached<Control, FileEntry?>("Badge", typeof(RowVcs));

    public static void SetBadge(Control target, FileEntry? value)
        => target.SetValue(BadgeProperty, value);

    public static FileEntry? GetBadge(Control target) => target.GetValue(BadgeProperty);

    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Entry", typeof(RowVcs));

    public static void SetEntry(TextBlock target, FileEntry? value)
        => target.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(TextBlock target) => target.GetValue(EntryProperty);

    static RowVcs()
    {
        EntryProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
        {
            Attach(text, () => Apply(text, GetEntry(text)));
            Apply(text, e.NewValue as FileEntry?);
        });

        BadgeProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            Attach(control, () => ApplyBadge(control, GetBadge(control)));
            ApplyBadge(control, e.NewValue as FileEntry?);
        });
    }

    /// <summary>Set once per control, so a recycled row does not stack handlers.</summary>
    private static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Wired", typeof(RowVcs));

    private static void Attach(Control control, Action reapply)
    {
        if (control.GetValue(WiredProperty)) return;

        control.SetValue(WiredProperty, true);

        void OnChanged(object? _, EventArgs __) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(reapply);

        Changed += OnChanged;

        // A static event holding a strong reference to a control would keep
        // every row ever realized alive for the life of the process. Detaching
        // is what makes this safe, and it is why the handler is a named local
        // rather than a lambda written twice.
        control.DetachedFromVisualTree += (_, _) =>
        {
            Changed -= OnChanged;
            control.SetValue(WiredProperty, false);
        };
    }

    private static void ApplyBadge(Control control, FileEntry? entry)
        => control.IsVisible = entry is { } e && Mark(StateFor(e.FullPath)).Glyph.Length > 0;

    private static void Apply(TextBlock text, FileEntry? entry)
    {
        var (glyph, colour) = entry is { } e
            ? Mark(StateFor(e.FullPath))
            : ("", "");

        // Text only. Whether the column exists at all is the pane's decision
        // (`IsRepository`); a clean file inside a repository draws an empty
        // marker rather than collapsing and shifting every name beside it.
        text.Text = glyph;

        if (glyph.Length > 0) text.Foreground = new SolidColorBrush(Color.Parse(colour));
    }
}
