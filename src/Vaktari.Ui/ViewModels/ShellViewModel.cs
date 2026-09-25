using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One item a batch could not do, as the details list shows it.
///
/// **The bar named one of them and counted the rest.** "report.docx and 2 more
/// were left behind" is the right length for a line that has to share a bar
/// with a progress fraction, and it was the whole of what anybody was ever
/// told — the other two could be found only by comparing the source folder
/// against the destination by hand, which is the work that carrying on past a
/// locked file is supposed to save.
///
/// The full path travels with the row because the leaf name stops being an
/// answer the moment a copy has subfolders in it: three "index.html" left
/// behind name none of the three.
/// </summary>
public sealed record ProblemRow(string Name, string Path, string Reason);

/// <summary>
/// What "Choose a folder…" at the foot of Copy to and Move to asks the window
/// for: a folder picker, opened at <see cref="StartAt"/>, whose answer goes
/// back through <see cref="Chose"/>.
///
/// **The files are captured when the row is picked, not read back when the
/// picker closes.** The shell holds them in the callback, so a listing that
/// refreshes itself while the dialog is up — a watcher firing on the folder
/// being copied out of — cannot leave the transfer with a shorter selection
/// than the one the user chose the row for.
///
/// A callback rather than a path property the window assigns, because a
/// dismissed picker must do NOTHING: the window simply does not call it.
/// </summary>
public sealed class TransferBrowseRequest(bool move, string startAt, Action<string> chose)
{
    /// <summary>True when the folder picked is to receive a move rather than a
    /// copy. The picker's title is the only thing that still says which.</summary>
    public bool Move { get; } = move;

    /// <summary>Where the picker should open — the folder the files are in,
    /// since a destination is more often near the source than at home.</summary>
    public string StartAt { get; } = startAt;

    /// <summary>Hands back the folder the user picked, which starts the
    /// transfer. Not called at all when the picker was dismissed.</summary>
    public void Chose(string folder) => chose(folder);
}

/// <summary>
/// Owns one or two pane groups. Deliberately thin — it decides which side is
/// active and nothing else; all the behaviour lives in PaneViewModel, which is
/// what made split view an addition rather than a rewrite.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;

    /// <summary>
    /// Stands in for the platform's file operations in every window built
    /// while it is set — for a test that drives a real window to the point of
    /// sending something to the bin, and must not send it to the real one.
    /// Null in the application.
    /// </summary>
    internal static Func<IFileOperations?, IFileOperations?>? OperationsOverride { get; set; }
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly IScriptRunner? _scripts;
    private readonly ITemplateProvider? _templates;
    private readonly IFileSharing? _sharing;
    private readonly ISessionStore? _store;
    private bool _restoring;
    private bool _started;

    /// <summary>The two subscriptions to sources that outlive this shell, kept
    /// so <see cref="Dispose"/> has something to hand back.</summary>
    private readonly EventHandler _onCutMarks;
    private readonly EventHandler? _onSharingChanged;

    /// <summary>
    /// The right side as it was when last closed. Reopening restores it, so
    /// toggling the split off is not a way to silently lose where you were.
    /// </summary>
    private PaneState? _rememberedRight;

    public ShellViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        ISessionStore? store = null,
        IPlacesProvider? places = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null,
        IScriptRunner? scripts = null,
        ITemplateProvider? templates = null,
        IFileSharing? sharing = null)
    {
        _sharing = sharing;

        // **Held so they can be let go.** Both sources outlive this shell:
        // CutMarks is static and IFileSharing is one object shared by every
        // window. Until windows could close, a subscription for the life of the
        // process was the life of the window; now a closed window that is still
        // subscribed keeps its whole visual tree alive AND goes on reacting —
        // measured: after Dispose(), CutMarks.Mark still set this shell's
        // CutPaths, because Dispose only stopped the rate timer and the panes.
        //
        // Marks are raised by a pane and shown by every listing, so the shell
        // mirrors them rather than owning them.
        _onCutMarks = (_, _) =>
            Dispatcher.UIThread.Post(() => CutPaths = CutMarks.Paths);

        CutMarks.Changed += _onCutMarks;

        if (sharing is not null)
        {
            _onSharingChanged = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                RefreshShares();

                // Discovery re-runs after an install, so availability can change
                // while the app is open.
                OnPropertyChanged(nameof(CanShare));
                OnPropertyChanged(nameof(CanInstallSharing));
            });

            sharing.Changed += _onSharingChanged;
        }

        _scripts = scripts;
        _templates = templates;
        _fs = fs;
        _ops = OperationsOverride is { } swap ? swap(ops) : ops;
        _store = store;
        _launcher = launcher;
        _clipboard = clipboard;

        // The same provider the panes enumerate through, so the tree's idea of
        // what is in a folder cannot differ from the listing's.
        Sidebar = new SidebarViewModel(places, () => PaneViewModel.Trash, fs: fs);

        // A chosen result navigates the active tab to its folder and selects it,
        // rather than opening the file — search is for finding, not launching.
        // Attached here rather than in MainWindow, because this is the only
        // place that knows which pane is active.
        Sidebar.AttachNavigation(path => _ = ActiveTab?.NavigateAsync(path));

        Left = CreateGroup();
        ActiveGroup = Left;

        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.Rail)
                               or nameof(SidebarViewModel.Width)
                               or nameof(SidebarViewModel.CollapsedSections))
                MarkDirty();
        };
    }

    public SidebarViewModel Sidebar { get; }

    public PaneGroupViewModel Left { get; private set; }

    /// <summary>Null unless split. The XAML binds its column's visibility to this.</summary>
    [ObservableProperty] private PaneGroupViewModel? _right;

    [ObservableProperty] private PaneGroupViewModel _activeGroup = null!;

    [ObservableProperty] private double _splitRatio = 0.5;

    /// <summary>
    /// Multiplies the whole type scale and every metric derived from it. Exists
    /// as a user control rather than a constant because "the text is too small"
    /// is an accessibility problem, and the right size depends on the display
    /// and the person, not on a value picked at build time.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>Set by the view so scale changes can re-write the resources.</summary>
    public Action<double, double>? ScaleApplier { get; set; }

    partial void OnFontScaleChanged(double value) => ApplyScales();
    partial void OnIconScaleChanged(double value) => ApplyScales();

    private void ApplyScales()
    {
        // Application-level defaults only: the sidebar, status bar and
        // properties window. Panes carry their own scale and set their own
        // TextScale, so nothing here reaches into them.
        ScaleApplier?.Invoke(FontScale, IconScale);
        MarkDirty();
    }

    // The font axis has one range for every layout; the icon axis has one per
    // layout, and asks PaneScale for it.
    private const double MinFontScale = 0.7;
    private const double MaxFontScale = 2.5;

    /// <summary>
    /// One notch on either axis.
    ///
    /// **A fixed ADDITIVE delta is not one notch — it is a different notch at
    /// every size.** Adding 0.15 to 0.7 is a 21% jump and adding it to 3.5 is
    /// 4%, so the same wheel click lurched at the bottom of the range and
    /// crawled at the top. That was invisible while every axis was clamped to
    /// 0.7–2.5; widening the grid to 256px made it the difference between ten
    /// clicks from 100% to the top of the range and eighteen.
    ///
    /// The delta is read as a PROPORTION of the current scale, so callers keep
    /// the numbers they already pass and every notch is the same perceived
    /// step. Down is the reciprocal of up rather than <c>1 - delta</c>: a notch
    /// in followed by a notch out has to land back where it started, and
    /// x1.15 then x0.85 lands at 0.98.
    ///
    /// Rounded to three places, and BEFORE the clamp rather than after. The
    /// ends of a layout's icon range are a pixel count over a base — 256/72 is
    /// 3.5555… — so rounding the CLAMPED value hands back a scale a hair past
    /// the limit it was just held to. Three places rather than two because a
    /// notch is now a ratio and coarse rounding eats it: measured at ONE place,
    /// the same 15% notch came out x1.20 from a scale of 1.0 and x1.13 from a
    /// scale of 3.0 — the unevenness this method exists to remove, put back by
    /// the rounding.
    /// </summary>
    private static double Step(double value, double delta, double min, double max)
    {
        var scaled = delta >= 0 ? value * (1 + delta) : value / (1 - delta);

        return Math.Clamp(Math.Round(scaled, 3), min, max);
    }

    /// <summary>
    /// One notch on the font axis, and nothing at all when the caller is moving
    /// only icons. Its own method because two callers need it: the plain scale
    /// below, and the ladder step in <see cref="ZoomPane"/>, which cannot go
    /// through the plain scale without also moving the icons a second time.
    /// </summary>
    private static void StepFont(PaneViewModel pane, double delta)
    {
        if (delta == 0) return;

        pane.FontScale = Step(pane.FontScale, delta, MinFontScale, MaxFontScale);
    }

    /// <summary>
    /// Scaling applies to ONE pane — whichever is active, or whichever the
    /// pointer is over for the wheel. A reference listing beside a working one
    /// wants different sizes, which is the point of having two.
    /// </summary>
    public void ScalePane(PaneViewModel? pane, double fontDelta, double iconDelta)
    {
        if (pane is null) return;

        StepFont(pane, fontDelta);

        if (iconDelta != 0)
        {
            // The icon range belongs to the LAYOUT, not to the application: the
            // grid draws a 72px tile and the details row an 18px icon, so one
            // shared 0.7–2.5 gave the grid 50–180 and the row 13–45 — three
            // ranges nobody picked. See PaneScale.IconPixelRange.
            var (min, max) = pane.IconLimits;

            pane.IconScale = Step(pane.IconScale, iconDelta, min, max);
        }

        MarkDirty();
    }

    /// <summary>
    /// The zoom gesture: Ctrl+wheel, Ctrl+plus, Ctrl+minus.
    ///
    /// **It scaled inside the layout on screen and then stopped dead.** Every
    /// file manager's Ctrl+wheel walks a ladder — Explorer's runs from a list
    /// of names to a wall of extra-large tiles — and this one could only
    /// stretch whichever of the three layouts you happened to be in. Reaching
    /// large thumbnails meant leaving the wheel, opening a menu and choosing a
    /// layout by name, which is exactly the work the gesture exists to save.
    ///
    /// So: scale within the layout until the icon axis is at the end of that
    /// layout's stretch, and let the NEXT notch move to the neighbouring layout
    /// instead, carrying the sizes across. At the ends of the ladder there is
    /// no neighbour and it goes back to scaling in place, which is what makes
    /// the outermost notch do nothing rather than wrap around.
    ///
    /// Separate from <see cref="ScalePane"/>, which the flyout's own buttons
    /// use: those sit beside a layout chooser and must not move it.
    /// </summary>
    public void ZoomPane(PaneViewModel? pane, double fontDelta, double iconDelta)
    {
        if (pane is null) return;

        // The icon delta is what walks the ladder, so a caller moving only the
        // font must not be read as "step down": AtIconLimit takes a direction,
        // and a delta of 0 is not one.
        if (iconDelta != 0
            && pane.AtIconLimit(iconDelta > 0)
            && pane.StepLayout(iconDelta > 0))
        {
            // **The step used to return here and drop the font half of the
            // notch**, so the one click in every run that crossed a boundary
            // left the text frozen while the icons carried across — measured on
            // the build before this: a plain Ctrl+wheel at the details ceiling
            // moved neither axis' number. A step consumes the ICON half of the
            // notch, not the whole gesture.
            StepFont(pane, fontDelta);

            // And no MarkDirty, which is measured rather than an omission. **A
            // step always moves the icon SCALE**, because the arriving layout
            // draws a different base and the same pixel size therefore cannot
            // be the same multiplier — and every scale change raises
            // ScaleChanged, which CreatePane already wires to MarkDirty. A
            // MarkDirty() stood here until the test that guards it — see
            // ZoomLadderTests.A_ladder_step_is_worth_saving — went on passing
            // with it deleted, which is what proved the second call redundant.
            return;
        }

        ScalePane(pane, fontDelta, iconDelta);
    }

    // ---- remote mounts ---------------------------------------------------

    private IRemoteMounts? _remotes;

    public void UseRemotes(IRemoteMounts? remotes)
    {
        _remotes = remotes;
        Sidebar.UseRemotes(remotes);
    }

    public bool CanConnect => _remotes?.IsAvailable == true;

    /// <summary>
    /// What the connect prompt should offer, from whatever is doing the
    /// mounting. Hard-coding "smb://" here put a Linux URI in front of Windows
    /// users, whose redirector wants `\\server\share` and cannot mount sftp at
    /// all.
    /// </summary>
    public string ConnectPrefill => _remotes?.AddressPrefill ?? "";

    public string ConnectHint => _remotes?.AddressHint ?? "";

    /// <summary>The view owns the clipboard, so commands just ask.</summary>
    public event EventHandler<string>? CopyTextRequested;

    /// <summary>
    /// F11 toggles the panel on the side that has focus, so the shortcut and
    /// the per-side buttons do the same thing to the same place.
    /// </summary>
    [RelayCommand]
    private void ToggleInfo() => ActiveGroup?.ToggleInfoCommand.Execute(null);

    /// <summary>Hands the provider to both sides; each builds its own panel.</summary>
    public void UseProperties(IPropertiesProvider? properties)
    {
        Left?.UseProperties(properties);
        Right?.UseProperties(properties);
        _properties = properties;
    }

    private IPropertiesProvider? _properties;

    public bool IsSplit => Right is not null;

    /// <summary>
    /// What this desktop calls the bin, for labels that name it.
    ///
    /// Exposed on the view model rather than reached from markup with x:Static
    /// because <c>InitializeComponent</c> runs before the platform is chosen —
    /// an x:Static reference is resolved as the XAML is parsed and would bake in
    /// the default. A binding is evaluated when the DataContext arrives, which
    /// is after.
    /// </summary>
    public string BinName => Core.Naming.BinName;

    /// <summary>The same inside a sentence: "the Recycle Bin", "the trash".</summary>
    public string TheBin => Core.Naming.TheBin;

    /// <summary>The same starting a label: "Recycle Bin", "Trash".</summary>
    public string BinTitle => Core.Naming.BinTitle;

    /// <summary>
    /// "Empty Recycle Bin…" / "Empty trash…". The ellipsis is a promise that it
    /// asks first, and it stays on both platforms.
    /// </summary>
    public string BinEmptyLabel => $"Empty {BinName}…";

    /// <summary>
    /// "Move to the Recycle Bin" / "Move to the trash".
    ///
    /// The one bin label that was still hardcoded, so the context menu said
    /// "trash" on Windows while the prompt beside it, the settings page and the
    /// sidebar all said Recycle Bin.
    /// </summary>
    public string BinMoveLabel => $"Move to {TheBin}";

    public string BinEmptyHint => $"permanently delete everything in {TheBin}";

    // Hiding a control does not give its column back — an invisible pane in a
    // "*" column still reserves half the window. The definitions themselves
    // have to collapse, so they are driven from here.
    public GridLength LeftColumnWidth
        => new(IsSplit ? Math.Clamp(SplitRatio, 0.1, 0.9) : 1, GridUnitType.Star);

    public GridLength RightColumnWidth
        => IsSplit ? new GridLength(1 - Math.Clamp(SplitRatio, 0.1, 0.9), GridUnitType.Star)
                   : new GridLength(0);

    /// <summary>
    /// The fraction of the content width this group receives. 1 when not split.
    ///
    /// Clamped exactly as the column definitions clamp, so the answer matches
    /// what the layout will actually do rather than what SplitRatio says.
    /// </summary>
    private double ShareOf(object? group)
    {
        if (!IsSplit) return 1.0;

        var ratio = Math.Clamp(SplitRatio, 0.1, 0.9);

        return ReferenceEquals(group, Left) ? ratio : 1 - ratio;
    }

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(LeftColumnWidth));
        OnPropertyChanged(nameof(RightColumnWidth));
    }

    /// <summary>
    /// The active tab of the active side. Everything outside this class —
    /// toolbar, key bindings, context menu — binds through here and never needs
    /// to know whether the window is split.
    /// </summary>
    public PaneViewModel? ActiveTab => ActiveGroup?.ActiveTab;

    /// <summary>Item and selection counts, separate from the transient status
    /// so a passing message never hides them.</summary>
    public string ActiveSummary => ActiveTab?.Summary ?? "";

    /// <summary>
    /// The status line, prefixed in split view with the side it belongs to.
    ///
    /// **Only when there IS a status.** Status is empty almost all the time —
    /// it carries transient messages and is cleared the moment a listing
    /// finishes — so the split branch printed the folder name, an em dash and
    /// nothing after it, permanently, on every split window. The bar read
    /// "8 items · qa —" and had done since split view was built.
    /// </summary>
    public string ActiveStatus
    {
        get
        {
            if (ActiveTab is not { } pane || pane.Status.Length == 0) return "";

            return IsSplit ? $"{pane.Title} — {pane.Status}" : pane.Status;
        }
    }

    /// <summary>The other side, when split. Where "copy to other pane" sends things.</summary>
    public PaneGroupViewModel? OtherGroup
        => Right is null ? null : ReferenceEquals(ActiveGroup, Left) ? Right : Left;

    public event EventHandler<PaneViewModel>? PaneCreated;

    /// <summary>The view owns window creation, so the command just asks.</summary>
    public event EventHandler? PropertiesRequested;

    public event EventHandler? BatchRenameRequested;

    [RelayCommand]
    private void BatchRename() => BatchRenameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// F2 on more than one row asks for the batch dialog rather than renaming
    /// the focused one and ignoring the rest.
    /// </summary>
    private void WireBatchRename(PaneViewModel pane)
    {
        pane.BatchRenameRequested -= OnPaneAskedForBatchRename;
        pane.BatchRenameRequested += OnPaneAskedForBatchRename;
    }

    private void OnPaneAskedForBatchRename(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab)) BatchRenameRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowProperties()
    {
        // **Not in the bin or in Recent.** Both hold rows naming where a file
        // USED to be, so the sheet describes a path that is not there: on
        // Windows that reads as "modified 1601-01-01", size 0. Every neighbouring
        // entry carries this gate; this one was written two hundred lines from
        // the comment explaining why.
        if (ActiveTab is { IsTrashListing: true } or { IsRecentListing: true }) return;

        // **This PC and a search had nothing to fall back on.** With no row
        // picked the window describes the FOLDER you are looking at, and
        // neither of those is one: it was handed "vaktari:computer", or a whole
        // search path, found no such thing on disk, and printed the internal
        // scheme straight back at the person — "vaktari:computer is no longer
        // there". Path.GetFileName leaves the scheme whole, because 'v' is not
        // a drive letter, so the leak was the entire message. A misleading
        // refusal, where the honest answer is that there is nothing here to
        // describe.
        //
        // Here as well as on the row's visibility because Alt+Enter reaches
        // this command directly, which is exactly how the bin and Recent were
        // got round before.
        if (ActiveTab is null or { HasSelection: false, IsRealFolder: false }) return;

        PropertiesRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether the Properties entry applies here at all.
    ///
    /// **It stayed on the menu where there was nothing for it to describe.**
    /// The bin and Recent were excluded; This PC and a search were not, so
    /// right-clicking the background of either offered a row that could only
    /// fail. A picked drive or a picked result is a real path, so the row is
    /// gated on having something to ask about rather than on which listing
    /// this is — which also keeps it where it has always been, in an ordinary
    /// folder with nothing selected, describing the folder itself.
    /// </summary>
    public bool CanShowProperties
        => ActiveTab is not null
           && !ActiveTab.IsTrashListing
           && !ActiveTab.IsRecentListing
           && (ActiveTab.HasSelection || ActiveTab.IsRealFolder);

    /// <summary>Widen the window by this many pixels, to make room for a panel
    /// that would not otherwise fit.</summary>
    public event EventHandler<double>? GrowRequested;

    /// <summary>Put the window back to the width it had before it was grown.</summary>
    public event EventHandler? ReleaseRequested;

    /// <summary>
    /// Asks the window to bin the selection, so the confirmation setting is
    /// honoured.
    ///
    /// **The context menu used to call TrashSelectedCommand directly**, which
    /// skipped the prompt entirely: with "ask before moving files to the bin"
    /// turned on, the Delete key asked and the identical menu entry did not.
    /// The one path where somebody has explicitly requested a safety net is the
    /// worst place to have two routes with different behaviour.
    ///
    /// An event rather than a command that prompts, because the prompt bar
    /// belongs to the window — the same shape EmptyTrashRequested already uses.
    /// </summary>
    public event EventHandler? TrashSelectionRequested;

    [RelayCommand]
    private void TrashSelection() => TrashSelectionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Emptying the trash goes through the window, not straight to the store,
    /// because it needs the confirm bar — and the prompt lives in the window,
    /// which is the only thing that owns real buttons. Same arrangement as
    /// properties and settings.
    /// </summary>
    public event EventHandler? EmptyTrashRequested;

    [RelayCommand]
    private void EmptyTrash() => EmptyTrashRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>"Empty the Recycle Bin" or "Empty the bin", named the way the
    /// platform names it, as every other reference to it here is.</summary>
    public string EmptyBinLabel => $"Empty {Core.Naming.TheBin}";

    /// <summary>
    /// Status bar visibility, straight off the preferences. Re-raised rather
    /// than stored, so there is one source of truth and no copy to fall out of
    /// step with the file.
    /// </summary>
    public bool ShowStatusBar => Settings.AppSettings.Current.General.ShowStatusBar;

    public bool ShowFreeSpace => Settings.AppSettings.Current.General.ShowFreeSpace;

    // ---- column widths --------------------------------------------------------

    /// <summary>
    /// Raised once a dragged column width is in force and the drag has ended,
    /// so the window can write it out through the settings store it owns — the
    /// same shape as <see cref="DefaultViewChanged"/>, for the same reason:
    /// the shell has no store of its own.
    ///
    /// At the END of the drag and not on every move: a drag is a hundred
    /// events, and each one written to disk is a hundred atomic rewrites of
    /// settings.json for one gesture.
    /// </summary>
    public event EventHandler<Core.Settings.SettingsState>? ColumnWidthsChanged;

    /// <summary>
    /// Moves one details column's width by how far its grip was dragged.
    ///
    /// **The four metadata columns were fixed widths, in a file manager.**
    /// Every table a person has used lets a heading's edge be dragged, and a
    /// date column that cannot be widened is a date column that trims the year
    /// off at every size the designed width did not anticipate — a long
    /// relative date, a mono font with wide digits, a size in a language with
    /// a longer unit.
    ///
    /// The grip reports pixels on screen; the width is kept at 100%, so the
    /// pixels are divided by what the column was drawn at — the pane's own
    /// zoom times the interface size, exactly the product
    /// <see cref="PaneScale.Compute"/> multiplies the width by on the way out.
    /// Skipping the division would move a column at 200% half as far as the
    /// pointer, and the grip would slide out from under it.
    ///
    /// Every pane takes the width at once, through the same re-apply a zoom
    /// notch runs, because the width is one preference and not a property of
    /// the pane that was dragged. The file is written when the drag ends: see
    /// <see cref="ColumnWidthsChanged"/>.
    /// </summary>
    public void ResizeColumn(Core.Settings.DetailsColumn column, double deltaPixels, double paneFontScale)
    {
        var scale = paneFontScale * InterfaceText.Scale;
        var details = Settings.AppSettings.Current.Views.Details;
        var current = PaneScale.ColumnWidth(details, column);

        var next = Math.Clamp(
            Math.Round(current + deltaPixels / scale, 1),
            PaneScale.ColumnMin, PaneScale.ColumnMax);

        SetColumnWidths(details.WithWidth(column, next));
    }

    /// <summary>The drag has ended; what it left is written out.</summary>
    public void CommitColumnWidths()
        => ColumnWidthsChanged?.Invoke(this, Settings.AppSettings.Current);

    /// <summary>
    /// Every column back to its designed width, and written out at once — the
    /// only way back after a drag went too far, short of restoring every
    /// setting in the dialog.
    /// </summary>
    [RelayCommand]
    private void ResetColumnWidths()
    {
        SetColumnWidths(Settings.AppSettings.Current.Views.Details with
        {
            TypeColumn = 0, SizeColumn = 0, ModifiedColumn = 0, CreatedColumn = 0,
        });

        CommitColumnWidths();
    }

    private void SetColumnWidths(Core.Settings.DetailsViewSettings details)
    {
        Settings.AppSettings.Apply(Settings.AppSettings.Current with
        {
            Views = Settings.AppSettings.Current.Views with { Details = details },
        });

        RefreshPaneScales();
    }

    /// <summary>
    /// Re-lists every open pane.
    ///
    /// **A listing rather than a repaint, because RowIcon resolves an icon when
    /// its entry is set and never again** — there is no cheaper handle on "draw
    /// that row again". Called when the icons change SOURCE, which the caches
    /// alone cannot express: dropping them leaves whatever is already drawn
    /// exactly as it is.
    /// </summary>
    public void RefreshPaneListings()
    {
        // Left and Right, not a Groups collection — see RefreshPaneScales.
        foreach (var group in new[] { Left, Right })
            if (group is not null)
                foreach (var tab in group.Tabs)
                    tab.RefreshCommand.Execute(null);
    }

    public Func<WindowSession>? GeometryProvider { get; set; }

    // ---- construction --------------------------------------------------

    private PaneGroupViewModel CreateGroup()
    {
        var group = new PaneGroupViewModel(NewPane);

        group.LocationChanged += (_, _) => SyncSidebarLocation();

        // The details panel's width is persisted, and until it had a working
        // handle nothing could ever change it — so nothing marked the session
        // dirty for it either, and a drag would have been forgotten by the next
        // launch.
        group.LayoutChanged += (_, _) => MarkDirty();

        // Forwarded rather than handled: only the window can change its own
        // width, and the group has no business knowing a window exists.
        //
        // But the ARITHMETIC belongs here, because only the shell knows the
        // window is split. The columns are STAR lengths driven by SplitRatio, so
        // growing the window by the group's shortfall hands that side only its
        // SHARE of the extra — which is why the window grew and the panel still
        // did not appear. Dividing by the share makes one resize enough.
        group.GrowRequested += (sender, needed) =>
            GrowRequested?.Invoke(this, needed / ShareOf(sender));

        // Only give the width back when NEITHER side still needs it. In a split
        // both panels can have grown the window, and restoring on the first close
        // would pull the room out from under the one still open.
        group.ReleaseRequested += (_, _) =>
        {
            if (Left.GrewForPanel || Right is { GrewForPanel: true })
            {
                PaneGroupViewModel.PanelDebug("[vaktari] panel: holding the width — the other side's panel "
                    + "is still open");
                return;
            }

            ReleaseRequested?.Invoke(this, EventArgs.Empty);
        };

        // A split created later must get the provider too, or its panel would
        // silently have nothing to show.
        group.UseProperties(_properties);
        group.PropertyChanged += OnGroupChanged;
        return group;
    }

    private PaneViewModel NewPane()
    {
        // A new tab inherits the sizes of the one it was opened from, rather
        // than snapping back to default mid-session.
        var pane = new PaneViewModel(_fs, _ops, _launcher, _clipboard, _scripts, _templates)
        {
            FontScale = ActiveTab?.FontScale ?? FontScale,
            IconScale = ActiveTab?.IconScale ?? IconScale,
        };

        pane.ScaleChanged += (_, _) =>
        {
            MarkDirty();

            // The boxes in the menu follow the wheel as well as their own
            // typing, or they sit showing a size that is no longer true.
            NotifyTargetSizes();
        };
        pane.OperationStarted += OnOperationStarted;
        pane.PropertyChanged += OnPaneChanged;
        WireBatchRename(pane);
        PaneCreated?.Invoke(this, pane);
        return pane;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneGroupViewModel.ActiveTab)) return;

        // A side showing another tab is showing another folder.
        if (IsComparing) Recompare();

        if (ReferenceEquals(sender, ActiveGroup))
        {
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ActiveStatus));
            OnPropertyChanged(nameof(ActiveSummary));
            NotifySelectionMenu();
        }

        MarkDirty();
    }

    partial void OnActiveGroupChanged(PaneGroupViewModel? oldValue, PaneGroupViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActiveGroup = false;
        newValue.IsActiveGroup = true;

        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(OtherGroup));
        OnPropertyChanged(nameof(CompareSummary));
        NotifySelectionMenu();

        MarkDirty();
    }

    partial void OnRightChanged(PaneGroupViewModel? value)
    {
        // Comparing is between two sides; with one there is nothing to compare.
        if (value is null) IsComparing = false;

        // Closing the split leaves the size chooser pointing at a pane that is
        // no longer there, showing a number that belongs to nothing.
        NotifyTargetSizes();

        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(OtherGroup));
        NotifySelectionMenu();
        SyncWindowControls();
        NotifyColumns();
        MarkDirty();
    }

    /// <summary>
    /// The window's own controls live on one side only: the right, when split,
    /// and the only side otherwise.
    ///
    /// **Three controls, and the plural is deliberate**: the split toggle, the
    /// details-panel toggle and the view-options menu. All three sit on one
    /// side because that is what was asked for — "only show the settings, split
    /// view, and detail panel buttons on the right most window when in split
    /// mode. They are not needed on the left split at all."
    ///
    /// An audit once read the panel toggle's own tooltip — "for this side" —
    /// and concluded the left half was being cut off from the feature, so the
    /// gate came off. It was wrong: F11 toggles whichever side is active, so
    /// the left half keeps the panel and simply does not carry a second copy of
    /// the button. Restored 14 August 2026.
    ///
    /// Driven from here rather than computed in each group, because a group has
    /// no idea whether it is the left or the right of anything — that is the
    /// shell's knowledge, and OnRightChanged is the single point every split
    /// change passes through, including a session restored at startup.
    /// </summary>
    private void SyncWindowControls()
    {
        Left.ShowsWindowControls = Right is null;
        if (Right is { } right) right.ShowsWindowControls = true;
    }

    partial void OnSplitRatioChanged(double value)
    {
        NotifyColumns();
        MarkDirty();
    }

}
