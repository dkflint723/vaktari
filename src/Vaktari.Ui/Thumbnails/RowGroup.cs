using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Shows a group header above the first row of each group.
///
/// A header per run rather than a separate item type in the collection: mixing
/// headers into <c>Entries</c> would mean the list no longer holds only
/// <see cref="FileEntry"/>, which breaks selection, the three layouts that share
/// it, and the stat-free struct the enumerator produces. The pane works out
/// which paths start a group; the row just asks.
/// </summary>
public static class RowGroup
{
    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<Control, FileEntry?>("Entry", typeof(RowGroup));

    public static void SetEntry(Control control, FileEntry? value)
        => control.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(Control control) => control.GetValue(EntryProperty);

    static RowGroup()
    {
        EntryProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            // **No header was ever drawn, and this line is why.** A row's
            // bindings are applied while the template's content is still being
            // BUILT, so this handler runs before the new control has a logical
            // parent — and walking up to the pane is how Apply finds the header
            // map. It found nothing, left the control hidden, and nothing ever
            // ran it again: the map is rebuilt per listing, not per row.
            //
            // Measured in the headless harness on a real MainWindow, a real
            // folder and a real layout, grouped by type: all three header
            // controls came back IsVisible=false with no content, while
            // re-setting this same property on those same controls afterwards
            // turned two of them on reading "MD (1)" and "TXT (2)" — which puts
            // the fault in WHEN Apply ran rather than in what it does.
            //
            // Removed-then-added rather than a flag: rows are virtualized, so
            // one control takes this property many times over its life, and a
            // subscription per assignment would pile up. Measured on one
            // control given three entries in a row: with the two removals
            // below, each event carries exactly one of these handlers; with
            // them deleted, the count came back 3.
            control.AttachedToLogicalTree -= Reapply;
            control.AttachedToLogicalTree += Reapply;
            control.DetachedFromLogicalTree -= Forget;
            control.DetachedFromLogicalTree += Forget;

            Apply(control, args.NewValue as FileEntry?);
        });
    }

    /// <summary>Once the row is in the tree, the walk up to the pane can
    /// finish.</summary>
    private static void Reapply(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Apply(control, GetEntry(control));
    }

    /// <summary>
    /// A row on its way out of the listing stops listening.
    ///
    /// The subscription runs pane -> listener -> control, so a row that kept it
    /// would still be reachable from the pane after the listing had finished
    /// with it, and would go on re-reading the map from outside the tree, where
    /// <see cref="Apply"/>'s walk reaches no pane and can only hide. Measured
    /// through the pane's own handler count: one while the row is in, none once
    /// it is out.
    /// </summary>
    private static void Forget(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (sender is Control control && Listeners.TryGetValue(control, out var listener))
            listener.Listen(null);
    }

    /// <summary>
    /// What each realized heading is listening to. Keyed weakly on the control,
    /// so a row the listing has finished with is not kept alive by this table.
    /// </summary>
    private static readonly ConditionalWeakTable<Control, Listener> Listeners = new();

    /// <summary>
    /// Re-reads one control's heading whenever the pane rebuilds the map.
    ///
    /// **The headings went stale on every watcher event, and only once they
    /// were being drawn at all did that show.** The map is rebuilt per listing
    /// AND per watcher burst, but a realized row re-read it only when its own
    /// entry changed or when it entered the tree — and an insert into
    /// <c>Entries</c> is neither for the rows that keep their entry. Measured
    /// on a listing of notes.md/b.txt/c.txt grouped by kind, with a.txt
    /// arriving from the watcher: the pane's map moved the heading to a.txt and
    /// gave b.txt none, while the control over b.txt still read "TXT (2)" — two
    /// headings over one band of three, and the second one wrong. The mirror is
    /// worse: deleting a band's first row left the band with no heading at all,
    /// and therefore nothing to click.
    ///
    /// A handler per control rather than one static handler, because what has
    /// to happen is re-reading the map for THIS control.
    /// </summary>
    private sealed class Listener(Control control)
    {
        private PaneViewModel? _pane;

        /// <summary>Listen to this pane, or — with null — to none.</summary>
        public void Listen(PaneViewModel? pane)
        {
            if (_pane is not null) _pane.GroupingChanged -= OnHeadersChanged;

            _pane = pane;

            if (_pane is not null) _pane.GroupingChanged += OnHeadersChanged;
        }

        private void OnHeadersChanged(object? sender, EventArgs e)
            => Apply(control, GetEntry(control));
    }

    private static void Apply(Control control, FileEntry? entry)
    {
        // Hidden by default: most rows are not the first of a group, and a
        // header that briefly appears on every row while scrolling would be
        // worse than none.
        control.IsVisible = false;

        if (entry is not { } value) return;

        // The pane owns the map. Walking up rather than binding to it because
        // the row template has no path to the pane that survives recycling.
        for (var node = control as Control; node is not null; node = node.Parent as Control)
        {
            if (node.DataContext is not PaneViewModel pane) continue;

            // Before the lookup, not after it: a row with no heading now is
            // exactly the one that gains one when a file above it goes.
            Listeners.GetValue(control, static c => new Listener(c)).Listen(pane);

            if (pane.HeaderFor(value.FullPath) is { } header)
            {
                if (control is ContentControl content) content.Content = header.Text;
                control.IsVisible = true;
            }

            return;
        }
    }
}
