using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What <see cref="IApplicationLauncher.Run"/> does on a platform that has not
/// been taught to run anything.
///
/// **The fallback is the whole reason the member has a default body**, and
/// nothing exercised it: the Linux launcher overrides Run, and every fake in
/// the UI tests overrides it too, so replacing the body with "=> null" left all
/// four suites green while quietly deleting the behaviour the remarks promise.
/// The Windows launcher is the one production type that inherits it — it
/// answers false to CanRunFile, so the question never reaches it in the
/// application — and it cannot be the subject here, because its Open is a real
/// ShellExecute and a test that called it would start whatever it was handed.
///
/// So the subject is a launcher with nothing but the required members, which is
/// exactly the shape the default exists for.
/// </summary>
public sealed class LauncherRunDefaultTests
{
    /// <summary>Implements what the interface demands and not one member more,
    /// so every default body is the one under test.</summary>
    private sealed class BareLauncher : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public Exception? Open(string path)
        {
            Opened.Add(path);
            return null;
        }

        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    /// <summary>
    /// **Handed to the shell rather than dropped.** A platform reached anyway —
    /// through a route that did not ask CanRunFile first, or one written later
    /// — must still do something with the file; silence here is the fault this
    /// whole change is about, arriving by a different door.
    /// </summary>
    [Fact]
    public void A_platform_that_cannot_run_a_file_opens_it_instead()
    {
        var launcher = new BareLauncher();

        Assert.Null(((IApplicationLauncher)launcher).Run("/home/me/install.sh"));

        Assert.Equal(["/home/me/install.sh"], launcher.Opened);
    }

    /// <summary>
    /// And the refusal comes back with it, rather than being swallowed on the
    /// way through — the contract Run shares with Open, and the reason either
    /// of them returns anything at all. A fallback that reported success for a
    /// file that never went anywhere is the swallowed failure this change spent
    /// its other half removing.
    /// </summary>
    [Fact]
    public void A_platform_that_cannot_run_a_file_still_reports_the_refusal()
    {
        var refusing = new RefusingLauncher();

        var failure = ((IApplicationLauncher)refusing).Run("/home/me/gone.sh");

        Assert.IsType<FileNotFoundException>(failure);
    }

    /// <summary>The same bare shape, with an Open that will not go.</summary>
    private sealed class RefusingLauncher : IApplicationLauncher
    {
        public Exception? Open(string path) => new FileNotFoundException(path);
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    /// <summary>
    /// The other half of the same default, and the reason the fallback above is
    /// ever reached: a platform that has not been taught the difference says it
    /// cannot run anything, so the question is never put to somebody on it.
    ///
    /// **A default of true would have asked "Run this?" on every desktop that
    /// inherits it**, including the Windows one, which already runs a
    /// double-clicked executable itself — two consecutive questions and then
    /// the program started twice. That is what the Windows suite's
    /// The_shell_runs_what_it_is_handed_so_nothing_is_asked_here says of the
    /// launcher; this says it of the interface.
    /// </summary>
    [Fact]
    public void A_platform_that_was_never_taught_says_it_cannot_run_anything()
        => Assert.False(((IApplicationLauncher)new BareLauncher()).CanRunFile("/home/me/install.sh"));
}
