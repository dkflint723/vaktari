using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The once-a-day question to the releases page, from the setting to the
/// line on screen. The question itself — newer by number, once a day,
/// silence offline — is pinned in Core; these pin that it is asked only when
/// the box is ticked, and that the answer reaches the two places a person
/// looks.
/// </summary>
public sealed class UpdateCheckTests : OwnedViewModels
{
    private MainWindow? _window;
    private readonly SettingsState _before = AppSettings.Current;

    public override void Dispose()
    {
        _window?.Close();
        AppSettings.Apply(_before);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Answer(string tag)
        => $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/dkflint723/vaktari/releases/tag/{{tag}}"}""";

    // ---- the setting ---------------------------------------------------------

    /// <summary>Off by default — a request on somebody's network is not a
    /// default — and kept when turned on.</summary>
    [AvaloniaFact]
    public void The_check_is_off_by_default_and_the_box_reaches_the_file()
    {
        var vm = new SettingsViewModel(new SettingsState());

        Assert.False(vm.CheckForUpdates);

        vm.CheckForUpdates = true;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.General.CheckForUpdates);
    }

    [AvaloniaFact]
    public void The_version_line_names_a_newer_release_when_one_is_known()
    {
        Assert.Equal($"Vaktari {Program.Version}", new SettingsViewModel(new SettingsState()).VersionLine);

        Assert.Equal(
            $"Vaktari {Program.Version} — 0.11.0 is available",
            new SettingsViewModel(new SettingsState(), updateAvailable: "0.11.0").VersionLine);
    }

    // ---- the window ----------------------------------------------------------

    /// <summary>**Off means nothing is asked**, not asked and discarded.</summary>
    [AvaloniaFact]
    public async Task With_the_box_clear_nothing_is_asked()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow();
        var asked = 0;

        window.Services.Updates.FetchOverride = _ => { asked++; return Task.FromResult(Answer("v99.0.0")); };

        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { CheckForUpdates = false },
        });

        await window.CheckForUpdatesAsync("0.10.2");

        Assert.Equal(0, asked);
        Assert.Null(window.Services.UpdateAvailable);
    }

    /// <summary>
    /// With the box ticked, a newer release is said on the operation bar — the
    /// line that stays until dismissed — and kept for the settings footer.
    /// The version is handed in: the test host reports 0.0.0, and a
    /// development build never asks.
    /// </summary>
    [AvaloniaFact]
    public async Task With_the_box_ticked_a_newer_release_is_said_where_it_stays()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow();
        var asked = 0;

        window.Services.Updates.FetchOverride = _ => { asked++; return Task.FromResult(Answer("v99.0.0")); };

        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { CheckForUpdates = true },
        });

        await window.CheckForUpdatesAsync("0.10.2");

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        Assert.Equal(1, asked);
        Assert.Contains("99.0.0 is available", shell.OperationStatus);
        Assert.Contains("What is new", shell.OperationStatus);
        Assert.Equal("99.0.0", window.Services.UpdateAvailable?.Version);
    }
}
