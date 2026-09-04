using System.ComponentModel;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the shell says when it will not open something, and what is left of it
/// by the time a person could have seen it.
///
/// **It was thrown away.** Open returned void and every exception went to
/// Quiet.Swallowed, which prints nothing at all unless VAKTARI_QUIET_DEBUG is
/// set — so double-clicking a row whose file had been deleted since the listing
/// was drawn did nothing, said nothing, and looked exactly like a click that
/// had missed.
///
/// **Only the failures are exercised here.** A launch that works starts a real
/// application on the machine running the suite, which is not a thing a test
/// may do; what the shell does after it accepts the request is out of reach
/// anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LaunchFailureTests
{
    /// <summary>
    /// Code 2 through the pure function, so the translation is pinned without
    /// the shell in the way.
    /// </summary>
    [Fact]
    public void The_missing_file_code_is_translated()
    {
        var said = WindowsLauncher.Refusal(new Win32Exception(2), @"C:\work\notes.txt");

        Assert.Equal(
            "that file is not there any more",
            Failures.Describe(Assert.IsType<FileNotFoundException>(said), "open that file"));
    }

    /// <summary>
    /// 1223 is ERROR_CANCELLED. A plain double-click can raise the consent
    /// dialog on its own — the shell elevates a program whose manifest asks it
    /// to — and OpenElevated in the same file already treats a decline as an
    /// answer rather than an error. Saying nothing is that same decision.
    ///
    /// Only reachable as a pure call: producing it for real needs somebody to
    /// press No on a dialog.
    /// </summary>
    [Fact]
    public void Saying_no_to_the_consent_dialog_says_nothing()
    {
        Assert.Null(WindowsLauncher.Refusal(new Win32Exception(1223), @"C:\work\setup.exe"));
    }

    /// <summary>Everything else is handed back as it arrived, so an unforeseen
    /// refusal still reaches the status bar in its own words.</summary>
    [Fact]
    public void An_unforeseen_refusal_is_passed_straight_through()
    {
        var raised = new InvalidOperationException("no file name");

        Assert.Same(raised, WindowsLauncher.Refusal(raised, @"C:\work\notes.txt"));
    }

    private static string Gone() => Path.Combine(
        Path.GetTempPath(), "vaktari-no-such-file-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>
    /// The case the finding was about. Measured on Windows 11: ShellExecute of
    /// a path that is not there raises Win32Exception with NativeErrorCode 2,
    /// and the launcher turns that into the type the rest of the application
    /// already has a sentence for.
    /// </summary>
    [WindowsFact]
    public void A_file_that_is_gone_comes_back_as_a_missing_file()
    {
        var path = Gone();

        var failure = new WindowsLauncher().Open(path);

        Assert.Equal(path, Assert.IsType<FileNotFoundException>(failure).FileName);
    }

    /// <summary>
    /// The half that matters to a person: the launcher's answer, put through
    /// the same describer the copy engine and the listing use, is the sentence
    /// they already know.
    /// </summary>
    [WindowsFact]
    public void The_missing_file_is_described_in_the_words_used_everywhere_else()
    {
        var failure = new WindowsLauncher().Open(Gone());

        Assert.Equal("that file is not there any more",
                     Failures.Describe(failure!, "open that file"));
    }

    /// <summary>
    /// Not only the missing-file case comes back. An empty path never reaches
    /// here from a row, but it is the one refusal that can be provoked without
    /// starting anything: measured on Windows 11, Process.Start raises
    /// InvalidOperationException, "Cannot start process because a file name has
    /// not been provided." — which used to be swallowed like all the rest.
    /// </summary>
    [WindowsFact]
    public void Any_other_refusal_comes_back_too()
    {
        var failure = new WindowsLauncher().Open("");

        Assert.NotNull(failure);
    }
}
