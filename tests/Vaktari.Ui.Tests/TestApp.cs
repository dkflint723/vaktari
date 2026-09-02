using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Vaktari.Ui.Tests.TestApp))]

// **One application and one UI thread, so one test at a time.**
//
// xunit runs test CLASSES in parallel by default, and that was never sound
// here: every headless test in this assembly shares a single Application, and
// ThemeApplierTests reads back Application.Current.RequestedThemeVariant and
// its resources — application-wide state another class can be writing at the
// same moment. It survived only because nothing yielded the UI thread
// mid-test; the first test that pumped the dispatcher turned it into a race
// that failed twice and then would not reproduce, which is the worst kind of
// test failure to be handed.
//
// The suite runs in about half a second, so serialising it costs nothing worth
// weighing against that.
//
// **CollectionPerAssembly is the half that was missing, and it is what the
// Linux flake was.** Turning parallelism off makes collections run one after
// another; it does not make them one collection. xunit's default is a
// collection PER TEST CLASS, and Avalonia.Headless keeps its session — the
// dedicated thread that owns Dispatcher.UIThread — for the life of a
// collection. So the suite was starting and disposing a session per class,
// perhaps forty times a run, and disposal does not finish synchronously:
// whenever the next session began setting up its Application before the
// previous thread had let go of Dispatcher.UIThread, AppBuilder.SetupUnsafe
// built a Compositor, DefaultRenderLoop.Add called VerifyAccess, and the run
// failed with "the calling thread cannot access this object" — attributed to
// whichever test happened to be next, which is why it never named the same one
// twice and never reproduced on a fast Windows agent.
//
// One collection for the assembly means one session for the run, so there is
// no second thread to lose the race to.
[assembly: Xunit.CollectionBehavior(
    Xunit.CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]

// **And this is what the Linux flake actually was.**
//
// Avalonia.Headless defaults to PerTest isolation: every single test tears the
// Application down and builds a new one, and building one constructs a
// Compositor, whose ServerCompositor registers with the render loop and calls
// Dispatcher.VerifyAccess. Four hundred and seventy-four tests meant four
// hundred and seventy-four of those, each one a chance for the session's
// dispatch loop — which is a Task on the thread pool, not a thread of its own —
// to have resumed somewhere other than the thread Dispatcher.UIThread was bound
// to. When it did, VerifyAccess threw "the calling thread cannot access this
// object", the runner reported it as a CLEANUP failure of whichever test was
// next, and it never named the same test twice. It reproduced on roughly one
// Linux run in five and almost never on a fast Windows agent, which is exactly
// the shape of a thread-pool race.
//
// PerAssembly builds the Application once for the whole run, so there is one
// Compositor and one VerifyAccess instead of hundreds. It is also what this
// assembly already assumed: the note above explains that these tests share a
// single Application and read Application.Current between them, which is only
// coherent if the Application is not being replaced underneath them.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Vaktari.Ui.Tests;

/// <summary>
/// The application the headless tests run inside.
///
/// **Deliberately not Vaktari.Ui's own App.** That one builds a platform, a
/// session store and a shell view model on startup — it wants a real machine
/// with real folders, and a test that needs all of it can only ever be an
/// end-to-end test. What these tests exercise is narrower and more valuable:
/// the markup and the theme rules, which is where this project's bugs have
/// actually been.
///
/// FluentTheme is loaded because it is half the subject. Two of the faults
/// these tests pin came from Fluent styling a control's TEMPLATE, where a local
/// value on the control cannot reach — a test against a bare Avalonia would
/// have passed while the shipped application was wrong.
/// </summary>
public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
