using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.Diagnostics;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a bug report gets, and what the window says after a crash.
///
/// **A crash left nothing on disk and the dialog had nothing to hand over.**
/// Two facts: the diagnostics text carries what a report needs with its paths
/// already hidden, and a window that starts after a crash says so where it
/// stays said.
///
/// The log is a static; each test points it at its own folder and resets it,
/// so nothing here reaches the developer's real log directory.
/// </summary>
public sealed class DiagnosticsTests : OwnedViewModels
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vaktari-diag-" + Guid.NewGuid().ToString("N")[..8]);

    private MainWindow? _window;

    public DiagnosticsTests() => Log.Configure(_dir, includePaths: false);

    public override void Dispose()
    {
        _window?.Close();
        Log.Reset();

        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    // ---- the bundle ---------------------------------------------------------

    [AvaloniaFact]
    public void The_diagnostics_carry_the_version_the_log_and_no_whole_paths()
    {
        Log.Warn("test", @"could not read C:\Users\somebody\Secret\plans.txt");

        var model = new SettingsViewModel(
            new SettingsState(),
            settingsFile: @"C:\Users\somebody\AppData\Local\vaktari\settings.json");

        string? received = null;
        model.DiagnosticsRequested += (_, text) => received = text;

        model.CopyDiagnosticsCommand.Execute(null);

        Assert.NotNull(received);

        // What a report needs.
        Assert.StartsWith("vaktari " + Program.Version, received, StringComparison.Ordinal);
        Assert.Contains("running from", received, StringComparison.Ordinal);
        Assert.Contains("--- last log lines ---", received, StringComparison.Ordinal);
        Assert.Contains("plans.txt", received, StringComparison.Ordinal);

        // And not what a report must not carry: the directory halves of both
        // the settings path and the logged path are gone, the leaves are kept.
        Assert.DoesNotContain(@"C:\Users\somebody", received, StringComparison.Ordinal);
        Assert.Contains("settings.json", received, StringComparison.Ordinal);
    }

    // ---- the notice ---------------------------------------------------------

    /// <summary>
    /// A marker from the previous run puts one line on the operation bar —
    /// the one place a line stays until it is dismissed — and takes the marker
    /// with it, so the second window of the same run does not say it again.
    /// </summary>
    [AvaloniaFact]
    public void A_window_after_a_crash_says_so_once()
    {
        UseSearch(PaneViewModel.Search);

        Log.Fatal("process", "System.InvalidOperationException: it fell over\n   at Somewhere");

        var window = _window = new MainWindow();
        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        Assert.Contains("closed unexpectedly", shell.OperationStatus, StringComparison.Ordinal);
        Assert.Contains("Copy diagnostics", shell.OperationStatus, StringComparison.Ordinal);

        // Taken: nothing left for a second window to find.
        Assert.Null(Log.TakeCrashMarker());
    }

    [AvaloniaFact]
    public void A_window_after_a_clean_run_says_nothing()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow();
        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        Assert.DoesNotContain("closed unexpectedly", shell.OperationStatus, StringComparison.Ordinal);
    }
}
