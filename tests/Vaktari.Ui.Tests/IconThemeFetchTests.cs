using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Fetching a theme from Settings.
///
/// The download and the unpacking are tested where they live — the archive
/// rules, including the ones that keep a hostile archive inside its folder, are
/// in Vaktari.Core.Tests. What is pinned here is the part somebody actually
/// sees: that a successful fetch puts the theme to use without asking anything
/// further, and that a failed one says so instead of leaving a button that
/// looks broken.
/// </summary>
public sealed class IconThemeFetchTests : IDisposable
{
    private static readonly IconThemeSource Source =
        new("Papirus", "…", "https://example.invalid/papirus.tar.gz", 110, "GPL-3.0", new string('0', 64));

    public void Dispose()
    {
        SettingsViewModel.Installer = IconThemeInstaller.InstallAsync;
        SettingsViewModel.FileInstaller = IconThemeInstaller.InstallFromFileAsync;
    }

    private static SettingsViewModel Model() => new(new Vaktari.Core.Settings.SettingsState());

    [AvaloniaFact]
    public async Task A_fetched_theme_is_put_to_use_without_asking_again()
    {
        SettingsViewModel.Installer = (_, progress, _) =>
        {
            progress?.Report(new FetchProgress(55, 110));

            return Task.FromResult(new IconThemeArchive.Installed(
                [@"C:\icons\Papirus", @"C:\icons\Papirus-Dark"], 4000, 40000, 1024));
        };

        var model = Model();

        await model.FetchIconThemeCommand.ExecuteAsync(Source);

        // Chosen by name, so the archive's other variants do not win by
        // ordering — Papirus-Dark sorts first.
        Assert.Equal(@"C:\icons\Papirus", model.IconThemeFolder);
        Assert.True(model.HasIconTheme);
        Assert.Empty(model.IconThemeProblem);

        // And the variants that came with it are mentioned, since nothing else
        // would tell anybody they are there.
        Assert.Contains("1 more variant", model.IconThemeStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A download that fails must not look like one that worked.** The
    /// button re-enables, the spinner stops, and the reason appears beside the
    /// control rather than in a dialog on top of a dialog.
    /// </summary>
    [AvaloniaFact]
    public async Task A_download_that_fails_says_so_and_leaves_the_setting_alone()
    {
        SettingsViewModel.Installer = (_, _, _) =>
            throw new HttpRequestException("no such host is known");

        var model = Model();

        await model.FetchIconThemeCommand.ExecuteAsync(Source);

        Assert.Empty(model.IconThemeFolder);
        Assert.Contains("could not be downloaded", model.IconThemeProblem, StringComparison.Ordinal);
        Assert.Empty(model.IconThemeStatus);
        Assert.False(model.IsFetchingIconTheme);
        Assert.True(model.FetchIconThemeCommand.CanExecute(Source));
    }

    /// <summary>
    /// An archive that unpacks to nothing a theme reader recognises — the
    /// project reorganised its folders, say. Distinct from a failed download,
    /// and it must not select a folder that has no theme in it.
    /// </summary>
    [AvaloniaFact]
    public async Task An_archive_with_no_theme_in_it_selects_nothing()
    {
        SettingsViewModel.Installer = (_, _, _) =>
            Task.FromResult(new IconThemeArchive.Installed([], 0, 0, 0));

        var model = Model();

        await model.FetchIconThemeCommand.ExecuteAsync(Source);

        Assert.Empty(model.IconThemeFolder);
        Assert.Contains("no icon theme inside it", model.IconThemeProblem, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The list and the setting have to agree in both directions**, and a
    /// pair of properties that set each other is exactly the shape that either
    /// loops forever or silently stops updating. A theme that arrives from
    /// anywhere but the list — installed, or browsed to — must still end up
    /// selected in it.
    /// </summary>
    [AvaloniaFact]
    public async Task An_installed_theme_becomes_the_selected_row()
    {
        SettingsViewModel.Installer = (_, _, _) => Task.FromResult(
            new IconThemeArchive.Installed([@"C:\icons\Papirus"], 1, 0, 1));

        var model = Model();

        // Before: only Vaktari's own icons, and that is what is selected.
        Assert.Equal("", model.SelectedIconTheme!.Folder);

        await model.FetchIconThemeCommand.ExecuteAsync(Source);

        Assert.Equal(@"C:\icons\Papirus", model.IconThemeFolder);
        Assert.Equal(@"C:\icons\Papirus", model.SelectedIconTheme!.Folder);
        Assert.Contains(model.IconThemeChoices, c => c.Folder == @"C:\icons\Papirus");
    }

    /// <summary>
    /// Choosing from the list is the other direction, and choosing Vaktari's
    /// own icons is how the setting is cleared now that there is no Clear
    /// button.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_from_the_list_sets_the_folder()
    {
        var model = Model();

        model.IconThemeFolder = @"C:\somewhere\Tela";

        // Browsed to, so it joins the list rather than leaving it disagreeing
        // with the setting.
        var browsed = Assert.Single(model.IconThemeChoices, c => c.Folder == @"C:\somewhere\Tela");

        Assert.Same(browsed, model.SelectedIconTheme);
        Assert.Contains("chosen folder", browsed.Label, StringComparison.Ordinal);

        model.SelectedIconTheme = model.IconThemeChoices[0];

        Assert.Equal("", model.IconThemeFolder);
        Assert.False(model.HasIconTheme);
    }

    /// <summary>
    /// An archive somebody downloaded themselves goes through the same
    /// unpacking, and lands in the same list.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_can_be_installed_instead_of_fetched()
    {
        string? asked = null;

        SettingsViewModel.FileInstaller = (file, _) =>
        {
            asked = file;

            return Task.FromResult(new IconThemeArchive.Installed([@"C:\icons\Tela\Tela"], 9, 0, 1));
        };

        var model = Model();

        await model.InstallIconThemeFromAsync(@"D:\downloads\Tela.tar.gz");

        Assert.Equal(@"D:\downloads\Tela.tar.gz", asked);
        Assert.Equal(@"C:\icons\Tela\Tela", model.IconThemeFolder);
        Assert.Equal(@"C:\icons\Tela\Tela", model.SelectedIconTheme!.Folder);
    }

    /// <summary>
    /// A file that is not an archive at all, which is what a file picker will
    /// eventually be pointed at.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_that_is_not_an_archive_says_so()
    {
        SettingsViewModel.FileInstaller = (_, _) =>
            throw new InvalidDataException("that is not a .tar.gz or a .zip.");

        var model = Model();

        await model.InstallIconThemeFromAsync(@"D:\downloads\notes.txt");

        Assert.Empty(model.IconThemeFolder);
        Assert.Contains("could not be unpacked", model.IconThemeProblem, StringComparison.Ordinal);
        Assert.False(model.IsFetchingIconTheme);
    }

    /// <summary>Two fetches at once would race for the same folder, and the
    /// button is the only way to start one.</summary>
    [AvaloniaFact]
    public async Task It_cannot_be_started_twice()
    {
        var gate = new TaskCompletionSource();
        var starts = 0;

        SettingsViewModel.Installer = async (_, _, _) =>
        {
            starts++;
            await gate.Task;

            return new IconThemeArchive.Installed([@"C:\icons\Papirus"], 1, 0, 1);
        };

        var model = Model();

        var first = model.FetchIconThemeCommand.ExecuteAsync(Source);

        Assert.True(model.IsFetchingIconTheme);
        Assert.False(model.FetchIconThemeCommand.CanExecute(Source));

        await model.FetchIconThemeCommand.ExecuteAsync(Source);

        gate.SetResult();
        await first;

        Assert.Equal(1, starts);
    }
}
