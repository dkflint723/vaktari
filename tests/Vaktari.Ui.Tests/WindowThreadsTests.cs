using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A window that closed left three threads behind it, for good.** Every
/// window that built its own services built a WindowsThemeProvider, which
/// started a thread per registry key it watches, each blocked in a wait
/// nothing can call off. Measured with this test's own loop: 14 threads, then
/// 60, 90 and 120 after each ten windows opened and closed — three a window —
/// and a full Ui run ended near 740 (0.11.1 QA). The watchers are one set for
/// the process now; the same loop stays flat.
///
/// By the operating system's count of the process's threads, which nothing in
/// the application keeps, so nothing it does can satisfy this by accident.
/// Two windows first, so whatever starts once per process has started; the
/// margin is for the thread pool, which grows on its own. Windows only: the
/// threads were the Windows provider's.
/// </summary>
public sealed class WindowThreadsTests : OwnedViewModels
{
    private static int Threads()
    {
        using var me = Process.GetCurrentProcess();
        return me.Threads.Count;
    }

    private static void OpenAndClose(int windows)
    {
        for (var i = 0; i < windows; i++)
        {
            var window = new MainWindow();

            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public void Opening_and_closing_windows_does_not_grow_the_thread_count()
    {
        UseSearch(PaneViewModel.Search);

        OpenAndClose(2);

        var before = Threads();

        OpenAndClose(10);

        var grown = Threads() - before;

        Assert.True(grown < 15, $"ten windows opened and closed left {grown} more threads than before them");
    }
}
