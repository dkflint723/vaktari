using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which window a name-clash question lands on, asked through the real
/// <see cref="PaneViewModel.AskConflict"/> a window installs.
///
/// **The only test of this was a reading of the source.** The question is a
/// process-wide static every window assigns, and it used to capture the window
/// that assigned it: after that window closed, the next clash was asked of a
/// window that was gone. It is now built with no window to capture and finds
/// its owner when it is asked — the focused window, or any that is open — and
/// with none open it answers Cancel, as dismissing the dialog would, since the
/// last window closing cancels what was running. Both halves are driven here.
/// </summary>
public sealed class ConflictOwnerTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    private readonly Func<FileConflict, ValueTask<ConflictAnswer>>? _askBefore = PaneViewModel.AskConflict;

    public override void Dispose()
    {
        PaneViewModel.AskConflict = _askBefore;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done, string why)
    {
        var deadline = DateTime.UtcNow + Ceiling;

        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, why);

            Settle();
            await Task.Delay(5);
        }

        Settle();
    }

    private static async Task CloseAndWaitAsync(Window window)
    {
        var closed = false;

        window.Closed += (_, _) => closed = true;
        window.Close();

        await Until(() => closed, "the window never closed");
    }

    private static void CloseAll(WindowServices services)
    {
        foreach (var window in services.Windows.ToList().AsEnumerable().Reverse())
        {
            try { window.Close(); }
            catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (services.Windows.Count > 0 && DateTime.UtcNow < deadline)
        {
            Settle();
            Thread.Sleep(1);
        }

        Settle();
    }

    /// <summary>Two real files with the same question between them.</summary>
    private static (FileConflict Clash, string Folder) Clash()
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-conflict-owner").FullName;
        var from = Path.Combine(folder, "from.txt");
        var to = Path.Combine(folder, "to.txt");

        File.WriteAllText(from, "new");
        File.WriteAllText(to, "old");

        return (new FileConflict(from, to), folder);
    }

    /// <summary>
    /// **After the window built last — and focused last — has closed, the
    /// question goes to one that is still open.** That window is the one the
    /// old static had captured; asked there, the dialog had a closed owner.
    /// </summary>
    [AvaloniaFact]
    public async Task A_clash_after_the_focused_window_closed_is_asked_in_one_still_open()
    {
        UseSearch(PaneViewModel.Search);

        var (clash, folder) = Clash();
        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var services = founder.Services;
            var peer = services.Windows.Single(w => !ReferenceEquals(w, founder));

            // The premise: the window about to close is the focused one and the
            // one built last, so neither route to an owner can pick it by luck.
            Assert.Same(peer, services.Active);

            await CloseAndWaitAsync(peer);
            await Until(() => services.Windows.Count == 1, "the closed window is still in the family");

            Assert.Null(services.Active);

            var answer = PaneViewModel.AskConflict!(clash).AsTask();

            await Until(() => founder.OwnedWindows.OfType<ConflictWindow>().Any() || answer.IsCompleted,
                        "the question never appeared");

            Assert.False(answer.IsFaulted, $"the question failed: {answer.Exception?.InnerException?.Message}");

            var dialog = Assert.Single(founder.OwnedWindows.OfType<ConflictWindow>());

            Assert.False(answer.IsCompleted, "answered before anybody was asked");

            // Dismissed, which answers Cancel.
            dialog.Close();

            await Until(() => answer.IsCompleted, "closing the dialog did not answer it");

            Assert.Equal(ConflictResolution.Cancel, answer.Result.Resolution);
        }
        finally
        {
            CloseAll(founder.Services);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// **With no window left, the answer is Cancel and nothing is shown.** A
    /// transfer can still be between its cancel and its next clash when the
    /// last window goes; closing that window has already cancelled it, and
    /// there is nowhere left to put a dialog.
    /// </summary>
    [AvaloniaFact]
    public async Task A_clash_with_no_window_left_is_answered_cancel()
    {
        UseSearch(PaneViewModel.Search);

        var (clash, folder) = Clash();
        var only = new MainWindow();

        try
        {
            only.Show();
            Settle();

            await CloseAndWaitAsync(only);
            await Until(() => only.Services.Windows.Count == 0, "the last window never left the family");

            var answer = PaneViewModel.AskConflict!(clash).AsTask();

            await Until(() => answer.IsCompleted, "a clash with no window left was never answered");

            Assert.False(answer.IsFaulted, $"the question failed: {answer.Exception?.InnerException?.Message}");
            Assert.Equal(ConflictResolution.Cancel, answer.Result.Resolution);
            Assert.False(answer.Result.ApplyToRest);
            Assert.Empty(only.OwnedWindows);
        }
        finally
        {
            CloseAll(only.Services);
            Directory.Delete(folder, recursive: true);
        }
    }
}
