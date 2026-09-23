using Avalonia.Headless.XUnit;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Settings;
using Vaktari.Core.Vcs;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// That the application's composition root actually hands out what it builds.
///
/// **Eleven service locators were wired by WindowServices.Create() and not one
/// of them was observed by any test.** Deleting an assignment line left the
/// suite green: every test that uses one of these statics installs its own
/// value first, so none was ever reading what Create() put there. The single
/// exception was PaneViewModel.Searches, pinned by a source-TEXT assertion in
/// SearchHistoryTests — a string comparison against WindowServices.cs, which
/// holds whether or not the assignment does anything.
///
/// And every reader degrades in silence. The locators are nullable and each
/// one is read as <c>X?.Something</c> or behind an <c>is not { }</c> guard, so
/// a missing service costs previews, or git decorations, or the shell menu, or
/// the per-folder view memory — and says nothing anywhere. That is the same
/// fail-open shape ReachUpBindingTests was written for on the markup side.
///
/// **Nulled first, deliberately.** This assembly runs serialized, so a static
/// left set by an earlier class would satisfy a naive assertion even with the
/// assignment deleted. The test clears each one before calling Create(), which
/// is what makes a deleted line fail here.
///
/// **And counted, because half of these are legitimately null on one platform
/// or the other.** IPlatform gives ShellMenu and FileIcons a null default and
/// Linux takes it; Windows has no icon theme provider. Assert.Same(null, null)
/// passes whether or not anything was wired, so the count of pairings that
/// were actually MEANINGFUL is asserted too — otherwise this test could
/// quietly become a test of nothing on a platform that returns fewer services.
///
/// Two of Create()'s neighbours are deliberately out of scope. PaneViewModel.Trash
/// is set later, by StartTrashMaintenance, and its own comment records why it
/// must not move next to these. IconLoader.Provider is installed through the
/// icon-theme path, which is asynchronous when the index is cold and depends
/// on a setting another class may have left dirty.
/// </summary>
public sealed class CompositionRootTests : IDisposable
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    private readonly IThumbnailProvider? _thumbnails = ThumbnailLoader.Provider;
    private readonly IFileMetadataProvider? _metadata = RowMetadata.Provider;
    private readonly IFileIconProvider? _fileIcons = IconLoader.Files;
    private readonly IFolderViewStore? _folderViews = PaneViewModel.FolderViews;
    private readonly IRecentStore? _recents = PaneViewModel.Recents;
    private readonly ISearchHistory? _searches = PaneViewModel.Searches;
    private readonly IVersionControl? _vcs = PaneViewModel.Vcs;
    private readonly IShellMenuProvider? _shellMenu = PaneViewModel.ShellMenu;
    private readonly Vaktari.Core.Places.IDiskImages? _diskImages = PaneViewModel.DiskImages;
    private readonly IShortcutMaker? _shortcuts = PaneViewModel.Shortcuts;
    private readonly IPlacesProvider? _places = PaneViewModel.Places;
    private readonly ISearchProvider? _search = PaneViewModel.Search;

    /// <summary>
    /// Every one of them put back, including the ones this test nulls and
    /// Create() then fills with the real platform's. A class that leaves the
    /// live providers installed hands them to whatever runs next — see
    /// TestState for what that costs.
    /// </summary>
    public void Dispose()
    {
        ThumbnailLoader.Provider = _thumbnails;
        RowMetadata.Provider = _metadata;
        IconLoader.Files = _fileIcons;
        PaneViewModel.FolderViews = _folderViews;
        PaneViewModel.Recents = _recents;
        PaneViewModel.Searches = _searches;
        PaneViewModel.Vcs = _vcs;
        PaneViewModel.ShellMenu = _shellMenu;
        PaneViewModel.DiskImages = _diskImages;
        PaneViewModel.Shortcuts = _shortcuts;
        PaneViewModel.Places = _places;
        PaneViewModel.Search = _search;

        AppSettings.Apply(_settingsBefore);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The wiring, asserted by reference identity.
    ///
    /// Identity rather than "not null and looks right": it is the only shape
    /// that distinguishes the object Create() built from one some earlier test
    /// happened to leave lying in the same static.
    /// </summary>
    /// Needs the Avalonia app context: Create() reaches FontManager.Current
    /// while sizing the interface, which throws outside one.
    [AvaloniaFact]
    public async Task Create_hands_every_service_it_builds_to_the_static_that_reads_it()
    {
        ThumbnailLoader.Provider = null;
        RowMetadata.Provider = null;
        IconLoader.Files = null;
        PaneViewModel.FolderViews = null;
        PaneViewModel.Recents = null;
        PaneViewModel.Searches = null;
        PaneViewModel.Vcs = null;
        PaneViewModel.ShellMenu = null;
        PaneViewModel.DiskImages = null;
        PaneViewModel.Shortcuts = null;
        PaneViewModel.Places = null;
        PaneViewModel.Search = null;

        var services = WindowServices.Create();

        try
        {
            var platform = services.Platform;

            // (what Create() was given, what it should have handed over, a name)
            (object? Built, object? Wired, string Name)[] wiring =
            [
                (platform.Thumbnails, ThumbnailLoader.Provider, "ThumbnailLoader.Provider"),
                (platform.Metadata, RowMetadata.Provider, "RowMetadata.Provider"),
                (platform.FileIcons, IconLoader.Files, "IconLoader.Files"),
                (services.FolderViews, PaneViewModel.FolderViews, "PaneViewModel.FolderViews"),
                (services.Recents, PaneViewModel.Recents, "PaneViewModel.Recents"),
                (services.Searches, PaneViewModel.Searches, "PaneViewModel.Searches"),
                (platform.ShellMenu, PaneViewModel.ShellMenu, "PaneViewModel.ShellMenu"),
                (platform.DiskImages, PaneViewModel.DiskImages, "PaneViewModel.DiskImages"),
                (platform.Shortcuts, PaneViewModel.Shortcuts, "PaneViewModel.Shortcuts"),
                (platform.Places, PaneViewModel.Places, "PaneViewModel.Places"),
                (platform.Search, PaneViewModel.Search, "PaneViewModel.Search"),
            ];

            var meaningful = 0;

            foreach (var (built, wired, name) in wiring)
            {
                Assert.True(ReferenceEquals(built, wired),
                            $"{name} does not hold what Create() built. A service the "
                            + "composition root forgets to hand over costs a feature at "
                            + "runtime and nothing at all in this suite: every reader of "
                            + "these is null-tolerant and silent.");

                if (built is not null) meaningful++;
            }

            // Both halves of a null pairing pass by construction, so a platform
            // that returns fewer services would quietly reduce this to a test of
            // nothing. Nine is what the leanest platform supplies: the four
            // non-nullable providers, the two it does implement, and the three
            // stores WindowServices builds itself.
            Assert.True(meaningful >= 9,
                        $"only {meaningful} of the {wiring.Length} pairings were non-null, so "
                        + "most of this test asserted null against null. Either the platform "
                        + "stopped supplying services, or Create() stopped building them.");

            // Not by reference: Create() news this one up and keeps no handle on
            // it, so its own identity is all there is to check.
            Assert.IsType<GitVersionControl>(PaneViewModel.Vcs);
        }
        finally
        {
            await services.ReleaseAsync(null!);
        }
    }
}
