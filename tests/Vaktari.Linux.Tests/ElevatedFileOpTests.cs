using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Doing a piece of our own work as root, which is a different shape from
/// running somebody's program as root.
///
/// **Rank 196 established pkexec as the mechanism and polkit as the consent
/// story, and this follows it exactly** — the same detection, the same refusal
/// to hold rights of our own, the same reading of a dismissed prompt as an
/// answer rather than a fault. What it does NOT follow is the terminal. That
/// exists because pkexec unsets DISPLAY and XAUTHORITY, so a program started
/// through it has no window and nowhere to print why it stopped; this program
/// started with the elevated flag has nothing to show and nothing to say, and a
/// terminal window over a file copy would be a window nobody asked for, whose
/// close button looks like cancelling.
///
/// The argv is the whole of what can be wrong here, and it is pinned rather
/// than run: this machine has no pkexec, and even where there is one no test
/// can answer a polkit prompt.
/// </summary>
public sealed class ElevatedFileOpTests
{
    private static readonly ElevatedRequest Request =
        new(ElevatedVerb.Delete, null, ["/srv/data/report.txt"]);

    /// <summary>
    /// pkexec, then this program, then the request — and nothing else at all.
    /// </summary>
    [Fact]
    public void The_elevated_run_is_pkexec_then_us_then_the_request()
    {
        var argv = LinuxLauncher.ElevatedSelf(
            "/usr/bin/pkexec", "/usr/bin/vaktari", Request.ToArguments());

        Assert.Equal(
            ["/usr/bin/pkexec", "/usr/bin/vaktari", .. Request.ToArguments()],
            argv);
    }

    /// <summary>
    /// **No terminal is wrapped round it**, which is the one place this departs
    /// from the elevated-launch pair beside it. Asserted by the length: a
    /// terminal would put its own command and run flag ahead of pkexec.
    /// </summary>
    [Fact]
    public void No_terminal_is_wrapped_round_it()
    {
        var argv = LinuxLauncher.ElevatedSelf(
            "/usr/bin/pkexec", "/usr/bin/vaktari", Request.ToArguments());

        Assert.Equal("/usr/bin/pkexec", argv[0]);
        Assert.Equal(Request.ToArguments().Count + 2, argv.Count);
    }

    /// <summary>
    /// **Never a shell, and never a line.** The request goes over as an
    /// argument list, so a file called <c>; rm -rf ~</c> is a file called
    /// <c>; rm -rf ~</c> — the same property the Windows side gets from
    /// ArgumentList, and the one thing that must hold on a command line built
    /// for a process about to be given root.
    /// </summary>
    [Fact]
    public void The_request_goes_over_as_an_argument_list_and_not_a_command_line()
    {
        var awkward = new ElevatedRequest(
            ElevatedVerb.Delete, null, ["/srv/; rm -rf ~ \"and\" $HOME.txt"]);

        var argv = LinuxLauncher.ElevatedSelf(
            "/usr/bin/pkexec", "/usr/bin/vaktari", awkward.ToArguments());

        var info = LinuxLauncher.ElevatedStart(argv);

        Assert.False(info.UseShellExecute, "a shell would tear that name apart");
        Assert.Equal("/usr/bin/pkexec", info.FileName);
        Assert.Equal(argv.Skip(1), info.ArgumentList);

        // Nothing was joined into a line behind its back.
        Assert.Equal("", info.Arguments);
    }

    /// <summary>
    /// **A dismissed authentication is a person saying no.** pkexec documents
    /// 126 for that and 127 for being unable to run the program at all; only
    /// the first is an answer, so only the first comes back as null and clears
    /// the bar. 127 travels on as a number the caller will not recognise, which
    /// is how "the elevated run never spoke" stays distinguishable from "it did
    /// the work and left two behind".
    /// </summary>
    [Fact]
    public void A_dismissed_authentication_is_not_a_failure()
    {
        Assert.Null(LinuxLauncher.Outcome(126));
        Assert.Equal(0, LinuxLauncher.Outcome(0));
        Assert.Equal(2, LinuxLauncher.Outcome(2));
        Assert.Equal(127, LinuxLauncher.Outcome(127));
    }

    /// <summary>
    /// A machine with no pkexec offers nothing rather than offering a button
    /// that fails — the rule the two elevated launch entries already follow.
    /// </summary>
    [Fact]
    public async Task A_machine_with_no_pkexec_never_starts_one()
    {
        var launcher = new LinuxLauncher();
        launcher.UsePkexec(null);

        var ran = false;
        launcher.ElevatedRunOverride = (_, _) =>
        {
            ran = true;
            return Task.FromResult(0);
        };

        Assert.Null(await launcher.RunSelfElevatedAsync(Request.ToArguments(), default));
        Assert.False(ran, "something was started on a machine with no way to elevate");
    }

    /// <summary>
    /// **The dismissal is read where the run comes back, not only in the
    /// mapping.** The seam stands in for the process, so it hands over an exit
    /// code and the launcher does what it does with one — which is how the
    /// 126-is-a-decline rule stays pinned at its call site rather than only as
    /// a function nothing reaches.
    /// </summary>
    [Fact]
    public async Task A_prompt_dismissed_by_the_real_run_comes_back_as_no_answer()
    {
        var launcher = new LinuxLauncher();
        launcher.UsePkexec("/usr/bin/pkexec");

        IReadOnlyList<string>? argv = null;
        launcher.ElevatedRunOverride = (seen, _) =>
        {
            argv = seen;
            return Task.FromResult(126);
        };

        Assert.Null(await launcher.RunSelfElevatedAsync(Request.ToArguments(), default));

        // And what it was handed is pkexec in front of the request, so the
        // decline being read is a decline of THIS run.
        Assert.NotNull(argv);
        Assert.Equal("/usr/bin/pkexec", argv[0]);
    }

    /// <summary>
    /// And a count from the same route travels on as a count. Without this the
    /// test above is satisfied by a launcher that answers null to everything.
    /// </summary>
    [Fact]
    public async Task A_count_from_the_real_run_travels_on_as_a_count()
    {
        var launcher = new LinuxLauncher();
        launcher.UsePkexec("/usr/bin/pkexec");
        launcher.ElevatedRunOverride = (_, _) => Task.FromResult(2);

        Assert.Equal(2, await launcher.RunSelfElevatedAsync(Request.ToArguments(), default));
    }

    /// <summary>
    /// The two engines build the administrator offer the same way. Only the
    /// Windows one can be run on the machine most of this is written on — an
    /// access-denied failure needs a real filesystem that refuses — so this is
    /// the floor under the Linux copy: a guard, and labelled as one.
    ///
    /// The same reasoning as every other pair in this repository that says one
    /// thing twice: a rule that only exists on one side is the copy that rots.
    /// </summary>
    [Fact]
    public void Both_engines_record_a_refusal_the_same_way()
    {
        foreach (var source in new[]
                 {
                     RepoSource.Read("src", "Vaktari.Linux", "LinuxFileOperations.cs"),
                     RepoSource.Read("src", "Vaktari.Windows", "WindowsFileOperations.cs"),
                 })
        {
            Assert.Contains(
                "if (ex is UnauthorizedAccessException) denied.Add(item.Source);",
                source, StringComparison.Ordinal);

            Assert.Contains(
                "if (ex is UnauthorizedAccessException) denied.Add(path);",
                source, StringComparison.Ordinal);

            Assert.Contains(
                "ElevatedVerb.Delete, null, worthRetrying, denied",
                source, StringComparison.Ordinal);

            Assert.Contains(
                "move ? ElevatedVerb.Move : ElevatedVerb.Copy",
                source, StringComparison.Ordinal);
        }
    }
}
