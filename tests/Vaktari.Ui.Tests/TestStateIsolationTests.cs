using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a window built by a test keeps its state.
///
/// **It kept it in the developer's own.** MainWindow's constructor makes eight
/// stores out of one directory — the session, the settings, the folder views,
/// the recents, the drive links, the icon index and the platform's own — and
/// closing the window flushes them, so every headless test that built one
/// overwrote the open tabs, the window geometry and the back stack of whoever
/// ran the suite. It was not theoretical: a back stack on this machine held
/// about eighty entries named after temp folders a rename test had visited, and
/// a test that left a tab in the bin made the bin the folder the application
/// opened on next launch — which then failed two unrelated tests, because
/// renaming is refused there.
/// </summary>
public sealed class TestStateIsolationTests : OwnedViewModels
{
    /// <summary>
    /// The seam is installed for the whole run, from a module initializer, so
    /// this holds in every test in the assembly rather than only the ones that
    /// remembered to ask.
    /// </summary>
    [Fact]
    public void Every_store_in_this_run_is_pointed_somewhere_disposable()
    {
        Assert.Equal(TestState.Current(), JsonSessionStore.DefaultDirectory());

        // Under the temp directory, which is what makes it disposable — and not
        // the real one, which is the whole point.
        Assert.StartsWith(Path.GetTempPath(), TestState.Current(), PathRulesComparison);

        Assert.NotEqual(RealDirectory(), JsonSessionStore.DefaultDirectory());
    }

    /// <summary>
    /// **And a window really writes there.** The property above could be right
    /// while the constructor still reached the real directory some other way —
    /// it builds its stores one by one, and only a window that has actually
    /// closed proves the flush landed where it was pointed.
    ///
    /// The tab is navigated first, because a flush writes nothing unless
    /// something marked the session dirty — and navigating is precisely what
    /// did the damage: the folder a test visited became the folder the
    /// application opened on next launch.
    /// </summary>
    [AvaloniaFact]
    public async Task A_window_that_opens_and_closes_writes_only_to_it()
    {
        var mine = Path.Combine(TestState.Current(), "session.json");

        if (File.Exists(mine)) File.Delete(mine);

        var folder = Directory.CreateTempSubdirectory("vaktari-isolation").FullName;

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            await shell.ActiveTab!.NavigateAsync(folder);

            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();

            // Closing cancels once, flushes, and closes for real, so the write
            // is still in flight when Close returns.
            for (var i = 0; i < 200 && !File.Exists(mine); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        // The session the window just flushed is in the test directory, and it
        // is this window's: the folder it was told to open is in it.
        Assert.True(File.Exists(mine), "the window wrote no session at all, so this proves nothing");
        Assert.Contains(Path.GetFileName(folder), File.ReadAllText(mine), StringComparison.Ordinal);

        // **What is deliberately NOT asserted here: that the real session file
        // was left alone.** That is the fact this whole seam exists for, and it
        // was checked by hand — the modification times of session.json,
        // settings.json, recents.json and drive-links.json under
        // %LOCALAPPDATA%\\vaktari, taken either side of a full `dotnet test
        // vaktari.slnx`, all unchanged. It is not asserted because a developer
        // machine may be running Vaktari, or another checkout's tests, while
        // this one runs: the assertion would then fail for something this
        // process did not do, which is the kind of test that gets deleted
        // rather than believed. The two positives above say the same thing
        // from the other side — the store points somewhere disposable, and the
        // window really writes there.
    }

    /// <summary>
    /// Where the stores WOULD go — the rule the override stands in front of.
    /// Recomputed here rather than read from the code under test, so that
    /// pointing the override back at the real directory is something this file
    /// can see.
    /// </summary>
    private static string RealDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

            if (string.IsNullOrWhiteSpace(state))
                state = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "state");

            return Path.Combine(state, "vaktari");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vaktari");
    }

    private static StringComparison PathRulesComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
