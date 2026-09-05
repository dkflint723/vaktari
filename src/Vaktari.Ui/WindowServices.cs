using System.IO;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
#if VAKTARI_LINUX
using Vaktari.Linux;
#elif VAKTARI_WINDOWS
using Vaktari.Windows;
#endif
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;

namespace Vaktari.Ui;

/// <summary>
/// Everything a second window must NOT build a second of.
///
/// **MainWindow's constructor did two jobs and only one of them is per
/// window.** It built the platform, the settings store, the session store, the
/// folder-view store, the recents store, the drive-link store, the icon-theme
/// install and the hourly trash sweep, and then wired a window to them. A
/// second `new MainWindow()` would therefore have been two writers of
/// session.json; two JsonFolderViewStores on one directory, where the first
/// window's Flush() on close writes its stale snapshot over the second's; two
/// JsonRecentStores with the same fault; a second platform, with a second set
/// of device watches; and a second hourly sweep deleting the same expired
/// files.
///
/// So the first window builds this and LENDS it to every window it opens.
/// Explicit hand-off rather than a process-wide lazy singleton, and that
/// rejection is measured rather than tasteful: <c>JsonSessionStore.Directory-
/// Override</c> is set per test CLASS by TestState, whose own doc records why —
/// "one directory per test CLASS, not one per run… a single directory for the
/// whole suite leaves the tests poisoning each other exactly as they poisoned
/// the developer". A lazy singleton would capture the FIRST test class's
/// directory for the whole assembly and hand it to every later class that
/// builds a window. Ref-counting it back to zero does not save it either:
/// OnClosing is async void and awaits before the window really goes, so
/// whether the next class got a fresh one would depend on dispatcher timing.
/// Lending rather than reaching keeps every existing single-window test
/// getting its own of everything, byte for byte as before.
/// </summary>
internal sealed class WindowServices
{
    /// <summary>
    /// The live windows, in CREATION order — never activation order. Window i
    /// on disk must come back as window i on screen, which is why
    /// <see cref="Active"/> is tracked separately rather than by moving
    /// entries around in here.
    /// </summary>
    private readonly List<MainWindow> _windows = [];

    private DispatcherTimer? _trashTimer;

    /// <summary>Let go of with the timer, and for the same reason.</summary>
    private Settings.BinPolicyWatch? _binPolicy;

    private WindowServices(
        IPlatform platform,
        JsonSettingsStore settingsStore,
        SettingsState settings,
        JsonSessionStore session,
        JsonFolderViewStore folderViews,
        JsonRecentStore recents,
        Vaktari.Core.Sharing.ProtonDriveLinks driveLinks,
        JsonDriveLinkStore driveLinkStore)
    {
        Platform = platform;
        SettingsStore = settingsStore;
        Settings = settings;
        Session = session;
        FolderViews = folderViews;
        Recents = recents;
        DriveLinks = driveLinks;
        DriveLinkStore = driveLinkStore;
    }

    internal IPlatform Platform { get; }
    internal JsonSettingsStore SettingsStore { get; }
    internal SettingsState Settings { get; }
    internal JsonSessionStore Session { get; }
    internal JsonFolderViewStore FolderViews { get; }
    internal JsonRecentStore Recents { get; }
    internal Vaktari.Core.Sharing.ProtonDriveLinks DriveLinks { get; }
    internal JsonDriveLinkStore DriveLinkStore { get; }

    /// <summary>
    /// The desktop's own request channel, held here rather than on the window
    /// that happened to subscribe: the settings dialog reads it, and a
    /// secondary window's dialog would otherwise be handed a null and show
    /// something different from the founder's.
    /// </summary>
    internal IFileManagerService? FileManager { get; set; }

    internal ITrashMaintenance? TrashMaintenance { get; private set; }

    /// <summary>
    /// session.json as the FOUNDER read it, kept so a window restored from it
    /// reads the same state rather than re-reading a file the founder may
    /// already have written over.
    /// </summary>
    internal SessionState? Restored { get; set; }

    /// <summary>
    /// The window the desktop last gave focus to, or null when none has been
    /// focused yet — this is assigned from <c>Activated</c>, which is a focus
    /// event, so a window launched into the background has never raised it.
    /// Read <see cref="ForDesktopRequest"/> rather than this.
    /// </summary>
    internal MainWindow? Active { get; set; }

    /// <summary>
    /// Which window a folder handed over by the desktop belongs to.
    ///
    /// **The fallback is not defensive.** <see cref="Active"/> is null until
    /// something is focused and again after the focused window closes, and a
    /// null there would silently discard the folder — which as a default file
    /// manager is the whole job.
    /// </summary>
    internal MainWindow? ForDesktopRequest => Active ?? _windows.FirstOrDefault();

    /// <summary>The live windows, for the tests that need to see the family.</summary>
    internal IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>
    /// Whether the window asking is the only one left. Asked by the share
    /// teardown, which may only run on the way out of the PROCESS — the same
    /// question <see cref="ReleaseAsync"/> asks, exposed rather than computed
    /// twice.
    /// </summary>
    internal bool IsLastWindow => _windows.Count <= 1;

    /// <summary>
    /// Everything still running anywhere in the application.
    ///
    /// The eject veto asks this rather than one window's list: a per-window
    /// answer would let window A "safely remove" a stick that window B was
    /// still filling, which is the one failure in this area that costs files
    /// rather than a confusing sentence.
    /// </summary>
    internal IEnumerable<IOperationHandle> Running
        => _windows.SelectMany(w => w.Shell.Running);

    internal void Adopt(MainWindow window) => _windows.Add(window);

    /// <summary>
    /// The whole application's session: one entry per live window, in creation
    /// order.
    /// </summary>
    internal SessionState Compose() => new()
    {
        Version = SessionState.CurrentVersion,
        Windows = [.. _windows.Select(w => w.Shell.ToWindowSession())],
    };

    /// <summary>
    /// Wraps the launcher-name reader so a REMOTE listing never pays for it.
    ///
    /// **A launcher name is the only thing in the row pipeline that opens a
    /// file, and it does it on the UI thread.** Measured here: 97 µs for the
    /// first ask about one launcher, and 0.1 µs for every ask after it — the
    /// same as a row that is not a launcher at all. That is a fair price on a
    /// local disk and not a price at all over a wire, where one open-read-close
    /// is a round trip and a screenful of them is a stalled window. So a
    /// launcher on an sshfs or gvfs mount keeps its file name, which is exactly
    /// what the row showed before any of this.
    ///
    /// Asked of <see cref="Thumbnails.ThumbnailLoader.IsRemote"/> rather than
    /// of a second list, for the reason that method's own doc gives: asking the
    /// question twice from two lists is how they come to disagree. That is also
    /// why the wrap lives here instead of in the platform assembly that fills
    /// the seam in — the remote roots are discovered by the sidebar, which the
    /// platform assemblies cannot see.
    ///
    /// A no-op on a build whose platform sets no reader, which is the Windows
    /// one.
    /// </summary>
    internal static void KeepLauncherNamesOffTheWire()
    {
        if (FileKind.LauncherName is not { } read) return;

        FileKind.LauncherName = path => Thumbnails.ThumbnailLoader.IsRemote(path) ? null : read(path);
    }

    /// <summary>
    /// Builds the half of the old MainWindow constructor that is per
    /// APPLICATION.
    ///
    /// The seven instance fields the moved block also assigned —
    /// _defaultFileManager, _properties, _accessEditor, _launcher, _platform,
    /// _virtualDrop and _shortcuts — stay in the constructor and read back off
    /// <see cref="Platform"/>; they are per-window handles onto shared objects,
    /// not shared objects themselves.
    /// </summary>
    internal static WindowServices Create()
    {
        // The one and only place a platform type is named, and the one guard
        // the analyser needs — each platform assembly is annotated for a single
        // OS, so everything inside them is free of per-call checks.
        //
        // The #if is not belt-and-braces around the runtime check. Only one
        // platform assembly is referenced per build (see Vaktari.Ui.csproj), so
        // the branch for the other OS would not compile at all. The runtime
        // check still earns its place: it is what the analyser reads to allow
        // the constructor call.
        const string Unsupported =
            "No platform implementation for this operating system yet.";

        IPlatform platform;

        // Each branch carries its own else rather than sharing one after the
        // #endif. Sharing it compiled, but left the #else arm as a dangling
        // `else` — so a build with neither symbol reported five cascading syntax
        // errors and buried the #error that explains what actually went wrong.
#if VAKTARI_LINUX
        if (OperatingSystem.IsLinux())
            platform = new LinuxPlatform(JsonSessionStore.DefaultDirectory());
        else
            throw new PlatformNotSupportedException(Unsupported);
#elif VAKTARI_WINDOWS
        if (OperatingSystem.IsWindows())
            platform = new WindowsPlatform(JsonSessionStore.DefaultDirectory());
        else
            throw new PlatformNotSupportedException(Unsupported);
#else
#error Vaktari.Ui references no platform assembly. One is selected from the build machine's OS, or by -p:VaktariPlatform=Linux|Windows; see Vaktari.Ui.csproj and WINDOWS.md §2.
        platform = null!;
#endif

        // Before anything builds a label. The view models below read these, and
        // so does every prompt sentence — adopting it here, beside the one place
        // a platform is chosen, is what keeps the window from naming the bin
        // two different ways.
        Naming.Adopt(platform);

        KeepLauncherNamesOffTheWire();

        // Clears a folder-handler registration left by a previous name of this
        // application pointing at a binary that no longer exists. Upgrading
        // from Heimdall removes that install and leaves its verb behind, after
        // which every double-clicked folder fails — the same wound the
        // uninstaller avoids, reached by a different route. Acts only when the
        // command is genuinely dead, so a Heimdall someone still runs is left
        // alone.
        platform.DefaultFileManager?.HealPreviousName();

        Thumbnails.ThumbnailLoader.Provider = platform.Thumbnails;
        Thumbnails.RowMetadata.Provider = platform.Metadata;
        // The desktop's own per-file icons, used only if the setting asks for
        // them. Installed regardless so the choice can be changed without a
        // restart.
        Thumbnails.IconLoader.Files = platform.FileIcons;

        if (platform.Icons is { } icons)
        {
            var probe = icons.Resolve(["inode-directory", "folder"], 32);
            Console.Error.WriteLine($"[vaktari] folder icon resolved to: {probe ?? "NOTHING"}");
        }

        // Settings BEFORE the theme, and this ordering is load-bearing rather
        // than tidy. ThemeApplier reads AppSettings.Current to decide whether a
        // configured font beats Plasma's — so loading settings afterwards meant
        // it read empty defaults and the font setting did nothing at all, even
        // across a restart. Settings are the first thing this does for the same
        // reason they precede the session load below.
        var settingsStore = new JsonSettingsStore(JsonSessionStore.DefaultDirectory());
        var settings = settingsStore.Load();
        settingsStore.EnsureFileExists(settings);

        AppSettings.Apply(settings);

        // **After Apply, and that ordering is the whole of it.** This was
        // written thirty lines above, where AppSettings.Current is still the
        // default record and the chosen folder is therefore always empty — so
        // FromFolder returned null on every launch and the imported theme was
        // never used by anybody, on either platform, while settings.json
        // faithfully recorded the choice and the dialog showed it as set. The
        // comment below about settings preceding the theme is about this exact
        // hazard; the font setting was caught by it once already.
        InstallIconTheme(platform);

        // Per-folder view overrides. A static for the same reason AppSettings is
        // one: panes are created by the shell, not injected here.
        var folderViews = new JsonFolderViewStore(JsonSessionStore.DefaultDirectory());
        ViewModels.PaneViewModel.FolderViews = folderViews;

        var recents = new JsonRecentStore(JsonSessionStore.DefaultDirectory());
        ViewModels.PaneViewModel.Recents = recents;

        // Platform-neutral: it drives the `git` binary, which behaves the same
        // on both targets, so it is constructed here rather than coming from
        // IPlatform like the trash and the icon theme do.
        ViewModels.PaneViewModel.Vcs = new Vaktari.Core.Vcs.GitVersionControl();

        // Same reasoning, different binary: Proton's own CLI, which behaves the
        // same on both targets and carries the encryption itself.
        // The setting always wins; the guess covers the machine where the
        // Proton Drive app is installed and nobody has told Vaktari where —
        // which should cost a click, not a treasure hunt through settings.
        var driveLinks = new Vaktari.Core.Sharing.ProtonDriveLinks
        {
            LocalRoot = AppSettings.Current.General.ProtonDriveFolder is { Length: > 0 } chosen
                ? chosen
                : Vaktari.Core.Sharing.ProtonDriveLinks.GuessLocalRoot() ?? "",
        };
        var driveLinkStore = new JsonDriveLinkStore(JsonSessionStore.DefaultDirectory());

        // From the platform, unlike the one above: what a desktop puts on a
        // context menu is entirely a platform fact, and on Linux the answer is
        // that there is no such thing.
        ViewModels.PaneViewModel.ShellMenu = platform.ShellMenu;
        ViewModels.PaneViewModel.DiskImages = platform.DiskImages;
        ViewModels.PaneViewModel.Shortcuts = platform.Shortcuts;
        ViewModels.PaneViewModel.Places = platform.Places;
        ViewModels.PaneViewModel.Search = platform.Search;

        // Logged at startup, not when the settings dialog opens. The count only
        // appeared on opening the dialog, which made "no line printed" mean two
        // different things and cost a diagnostic round trip. Compare with:
        //   fc-list : family | tr ',' '\n' | sort -u | wc -l
        Console.Error.WriteLine(
            $"[vaktari] fontlist: {Avalonia.Media.FontManager.Current.SystemFonts.Count} "
            + "families visible to Avalonia");

        var session = new JsonSessionStore(JsonSessionStore.DefaultDirectory());

        return new WindowServices(
            platform, settingsStore, settings, session, folderViews, recents,
            driveLinks, driveLinkStore);
    }

    /// <summary>
    /// The chosen theme, applied when it is ready rather than before the window
    /// is allowed to open. <see cref="Thumbnails.IconThemeInstall"/> carries the
    /// measurements and what changes on screen as a result.
    ///
    /// **An imported theme outranks the platform's own set**: it is the most
    /// deliberate of the three sources — somebody found a theme, downloaded it
    /// and pointed at it. A folder that has since been moved or deleted is
    /// ignored rather than honoured into a listing with no icons at all.
    ///
    /// Called after the settings are applied, and again when they are saved.
    /// </summary>
    internal static void InstallIconTheme(IPlatform platform)
    {
        // Before anything reads a theme. A null folder disables caching, which
        // means paying the build on every launch rather than only the first.
        Vaktari.Core.FileSystem.FreedesktopIconTheme.IndexCacheFolder =
            Path.Combine(JsonSessionStore.DefaultDirectory(), "icon-index");

        Thumbnails.IconThemeInstall.Begin(
            AppSettings.Current.General.IconThemeFolder,
            platform.Icons,
            folder => Vaktari.Core.FileSystem.FreedesktopIconTheme.FromCache(folder),
            folder => Vaktari.Core.FileSystem.FreedesktopIconTheme.FromFolder(folder),
            UseIconProvider);

        // Housekeeping, once per launch, off the thread. An index is sixteen
        // megabytes for a big theme and nothing else ever removes one whose
        // theme was deleted; the active theme's entry records a directory that
        // exists, so the prune never touches it.
        _ = Task.Run(Vaktari.Core.FileSystem.FreedesktopIconTheme.PruneIndexCache);
    }

    /// <summary>
    /// Marshals to the UI thread itself rather than asking the caller to. The
    /// first call arrives on it and the second does not, and a Post from the UI
    /// thread would defer the platform's icons past first paint for no reason.
    /// </summary>
    private static void UseIconProvider(Vaktari.Core.FileSystem.IIconThemeProvider? provider)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UseIconProvider(provider));
            return;
        }

        Thumbnails.IconLoader.Provider = provider;

        // Resolved paths and drawables belong to whatever was in place before.
        // On the first call there are none; on the swap they are the shell's,
        // and every one of them is now the wrong picture.
        Thumbnails.IconLoader.Invalidate();
    }

    /// <summary>
    /// Trash expiry, at startup and then hourly — once for the application, not
    /// once per window. Two windows each running their own hourly sweep is two
    /// of them deleting the same expired files, and a restore that opens both
    /// starts the two first sweeps together.
    ///
    /// Hourly rather than on a shorter tick because nothing here is urgent —
    /// a trash that is one hour over its age limit is not a problem — and
    /// because each sweep walks the trash to size it, which is real work to be
    /// doing behind someone's back.
    /// </summary>
    internal void StartTrashMaintenance(ITrashMaintenance? maintenance)
    {
        if (maintenance is null || _trashTimer is not null) return;

        TrashMaintenance = maintenance;

        // Assigned HERE, not beside the other providers: this was null until
        // the sweep was wired, so an earlier assignment handed the pane a null
        // and the Trash listing would have been silently empty forever. Same
        // shape as the font setting, which read its value before the settings
        // load and so never applied one.
        ViewModels.PaneViewModel.Trash = maintenance;

        _ = SweepAsync();

        // And again whenever the policy itself changes, or a new one waited up
        // to an hour to be acted on with nothing on screen saying so. See
        // BinPolicyWatch for why this hangs on Apply rather than on the dialog.
        _binPolicy = new Settings.BinPolicyWatch(() => _ = SweepAsync());

        _trashTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _trashTimer.Tick += (_, _) => _ = SweepAsync();
        _trashTimer.Start();
    }

    /// <summary>
    /// One sweep, reported into the oldest live window's status line — the
    /// sweep belongs to the application, so it says what it did wherever the
    /// application still has a window.
    /// </summary>
    internal async Task SweepAsync()
    {
        if (TrashMaintenance is not { } maintenance) return;

        try
        {
            var policy = AppSettings.Current.Trash;

            var result = await maintenance.SweepAsync(policy, CancellationToken.None);

            // ALWAYS logged, including when it did nothing.
            //
            // It used to speak only when it removed something, so silence meant
            // three different things — the feature is off, it ran and matched
            // nothing, or it never ran at all. For the one feature that deletes
            // files unattended, "I ran and did nothing" is exactly as important
            // as "I removed four", and being unable to tell them apart cost a
            // test round trip.
            var state = !policy.DeleteOldFiles && !policy.LimitSize
                ? "disabled"
                : $"age={(policy.DeleteOldFiles ? $"{policy.DeleteAfterDays}d" : "off")} "
                  + $"size={(policy.LimitSize ? $"{policy.MaximumPercentOfDisk}%" : "off")} "
                  // The field that decides whether it DELETES. Leaving it out
                  // made "removed 0 · OVER LIMIT" ambiguous between "set to warn"
                  // and "set to delete and failing to", which is the whole
                  // question this line exists to answer.
                  + $"when={policy.WhenLimitReached}";

            var freed = ByteSize.Format(result.BytesFreed);

            Console.Error.WriteLine(
                $"[vaktari] trash: {state} · removed {result.Removed} · "
                + $"freed {freed} · skipped {result.Skipped} undated"
                + (result.OverLimit ? " · OVER LIMIT" : ""));

            if (_windows.FirstOrDefault()?.Shell is not { } shell) return;

            if (result.Removed > 0)
                shell.OperationStatus = $"{Naming.BinName}: removed {result.Removed} item(s), freed {freed}";
            else if (result.OverLimit)
                shell.OperationStatus = $"{Naming.BinTitle} is over its size limit";
        }
        catch (Exception ex)
        {
            // A failed sweep must never take the window with it.
            Console.Error.WriteLine($"[vaktari] trash sweep failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One window leaves. Writes what the application looks like WITHOUT it,
    /// and lets go of the shared stores only when it was the last one.
    ///
    /// **The asymmetry is the whole restore contract.** A departing window is
    /// taken out of the list before the session is composed, so what is written
    /// no longer holds it — except for the LAST one out, which stays in.
    /// Dropping that one first would write a session with no windows in it,
    /// which on the next launch is indistinguishable from having forgotten
    /// everything.
    /// </summary>
    internal async Task ReleaseAsync(MainWindow window)
    {
        var last = IsLastWindow;

        if (!last) _windows.Remove(window);

        // A window that has gone cannot answer a desktop request, and Active is
        // only ever reassigned by a FOCUS event — which the survivor may not
        // receive for some time, or at all if it is not on screen.
        if (ReferenceEquals(Active, window)) Active = null;

        Session.NotifyChanged(Compose());

        // Two independent concerns, so two try blocks. They were one, sequenced
        // shares-first: a throw from the share teardown then skipped the flush
        // AND the dispose, and the single catch still printed "session flush
        // failed" for a flush that had never been attempted.
        try
        {
            if (last)
            {
                // Per application, so only the last one out may write them: an
                // earlier Flush from a closing window would put its own stale
                // snapshot over what the windows still open have since changed.
                FolderViews.Flush();
                Recents.Flush();

                _trashTimer?.Stop();
                _trashTimer = null;

                _binPolicy?.Dispose();
                _binPolicy = null;
            }

            await Session.FlushAsync(CancellationToken.None);

            if (last)
            {
                await Session.DisposeAsync();
                _windows.Remove(window);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] session flush failed: {ex.Message}");
        }
    }
}
