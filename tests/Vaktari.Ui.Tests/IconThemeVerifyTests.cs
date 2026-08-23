using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Opening Settings must not wait to find out whether the chosen theme still
/// reads.
///
/// **Reading a theme is seconds, not milliseconds.** It enumerates the theme
/// and everything it inherits: 2.8–3.1 seconds for Papirus-Dark on the machine
/// that reported it. Asking that question in the constructor meant the dialog
/// did not appear until it was answered — a freeze with nothing on screen to
/// explain it.
///
/// What the check is actually for — a folder moved, renamed or deleted — is two
/// existence tests and free. Only the deeper question goes behind the dialog.
/// </summary>
[Collection("settings theme verification")]
public class IconThemeVerifyTests : IDisposable
{
    private readonly Func<string, bool> _real = SettingsViewModel.ReadsAsTheme;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "vaktari-verify-" + Guid.NewGuid().ToString("N"));

    public IconThemeVerifyTests()
    {
        // Structurally a theme, so the cheap checks pass and the slow one is
        // the only thing left to decide it.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "index.theme"), "[Icon Theme]\nName=Test\n");
    }

    public void Dispose()
    {
        SettingsViewModel.ReadsAsTheme = _real;

        // Only what this test built, under its own temporary folder.
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private SettingsState With(string folder)
        => new() { General = new GeneralSettings { IconThemeFolder = folder } };

    /// <summary>
    /// The one that would have caught it: the reader is held open, and the
    /// constructor has to come back anyway. A synchronous implementation blocks
    /// here until the gate times out instead of passing.
    /// </summary>
    [AvaloniaFact]
    public async Task The_dialog_opens_without_waiting_for_the_theme_to_be_read()
    {
        using var reading = new ManualResetEventSlim(false);

        SettingsViewModel.ReadsAsTheme = _ =>
        {
            reading.Wait(TimeSpan.FromSeconds(10));
            return false;
        };

        var model = new SettingsViewModel(With(_folder));

        // Still reading, and the dialog is already built and complaint-free.
        Assert.False(model.ThemeVerification.IsCompleted);
        Assert.Equal("", model.IconThemeProblem);

        reading.Set();
        await model.ThemeVerification;

        // The answer arrives on the UI thread, so it needs one turn of the loop.
        Dispatcher.UIThread.RunJobs();

        Assert.True(model.HasIconThemeProblem);
        Assert.Contains("no longer reads as an icon theme", model.IconThemeProblem);
    }

    /// <summary>A theme that still reads leaves the dialog saying nothing,
    /// which is the case every ordinary open takes.</summary>
    [AvaloniaFact]
    public async Task A_theme_that_still_reads_is_not_complained_about()
    {
        SettingsViewModel.ReadsAsTheme = _ => true;

        var model = new SettingsViewModel(With(_folder));

        await model.ThemeVerification;
        Dispatcher.UIThread.RunJobs();

        Assert.False(model.HasIconThemeProblem);
    }

    /// <summary>
    /// **A folder that is gone is answered immediately**, because that answer
    /// costs two existence checks. Waiting on a thread to say what a missing
    /// directory already says would be the same mistake in the other direction.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_that_has_been_deleted_is_reported_at_once()
    {
        var reads = 0;
        SettingsViewModel.ReadsAsTheme = _ => { reads++; return true; };

        var model = new SettingsViewModel(With(Path.Combine(_folder, "not-here")));

        Assert.True(model.HasIconThemeProblem);
        Assert.Contains("moved or deleted", model.IconThemeProblem);
        Assert.True(model.ThemeVerification.IsCompleted);
        Assert.Equal(0, reads);
    }

    /// <summary>
    /// A folder still present but with its index.theme gone is the same
    /// verdict, and just as cheap: that file is what makes it a theme.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_without_an_index_theme_is_reported_at_once()
    {
        File.Delete(Path.Combine(_folder, "index.theme"));

        var reads = 0;
        SettingsViewModel.ReadsAsTheme = _ => { reads++; return true; };

        var model = new SettingsViewModel(With(_folder));

        Assert.Contains("moved or deleted", model.IconThemeProblem);
        Assert.Equal(0, reads);
    }

    /// <summary>No theme chosen asks nothing of the disk at all.</summary>
    [AvaloniaFact]
    public void With_no_theme_chosen_nothing_is_read()
    {
        var reads = 0;
        SettingsViewModel.ReadsAsTheme = _ => { reads++; return true; };

        var model = new SettingsViewModel(With(""));

        Assert.False(model.HasIconThemeProblem);
        Assert.True(model.ThemeVerification.IsCompleted);
        Assert.Equal(0, reads);
    }
}
