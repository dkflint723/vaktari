using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// <c>--self-test-archives &lt;dir&gt;</c>: the switch CI runs against the
/// published binary on both platforms, so a decoder the trimmer dropped fails
/// the build instead of the first person to open such an archive.
/// </summary>
public sealed class SelfTestSwitchTests
{
    [Fact]
    public void The_switch_runs_the_self_test_over_the_folder_it_names()
    {
        var fixtures = RepoSource.At("tests", "Fixtures", "Archives");

        Assert.Equal(0, Program.SelfTestExitCode([Program.SelfTestArchivesFlag, fixtures]));
    }

    [Fact]
    public void The_switch_without_a_folder_is_a_usage_error()
        => Assert.Equal(2, Program.SelfTestExitCode([Program.SelfTestArchivesFlag]));

    [Fact]
    public void An_ordinary_launch_is_not_a_self_test()
        => Assert.Null(Program.SelfTestExitCode([Path.GetTempPath()]));

    /// <summary>
    /// Answered after --version and before the instance mutex: it runs in CI
    /// with no display, and it is not a running file manager, so it must not
    /// claim the mutex the installer watches for. Read off the source, because
    /// the order of statements in Main is not something a unit can be asked.
    /// </summary>
    [Fact]
    public void The_switch_is_answered_after_version_and_before_the_mutex()
    {
        var source = RepoSource.Ui("Program.cs");
        var main = source.IndexOf("public static void Main(string[] args)", StringComparison.Ordinal);

        var version = source.IndexOf("if (args.Any(a => a is \"--version\" or \"-V\"))", main, StringComparison.Ordinal);
        var selfTest = source.IndexOf("if (SelfTestExitCode(args) is { } selfTest)", main, StringComparison.Ordinal);
        var mutex = source.IndexOf("ClaimInstanceMutex();", main, StringComparison.Ordinal);

        Assert.True(version > 0 && selfTest > version && mutex > selfTest,
            $"--version at {version}, self-test at {selfTest}, mutex at {mutex}");
    }
}
