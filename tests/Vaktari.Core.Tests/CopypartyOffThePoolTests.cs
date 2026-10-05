using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The two of 9488d50's four moves off the pool that no test saw (QA,
/// qa/poolfix): the startup sweep and an install's pip run. Putting either
/// back on Task.Run left the whole Core suite green.
///
/// - **The sweep** returns the task it runs on, so its creation options say
///   whether it was given a thread of its own.
/// - **The install** narrates between attempts, and that narration runs as
///   the continuation of the attempt's own task: on the attempt's thread when
///   it had one, on a pool worker when it ran on the pool. Two attempts that
///   fail at once make the second line's thread observable. A progress that
///   reports synchronously, so the thread seen is the caller's.
///
/// Shares CopypartyShareTests' collection: the share's config directory is
/// process-wide, and the sweep reads it.
/// </summary>
[Collection(CopypartyConfig.Name)]
public sealed class CopypartyOffThePoolTests
{
    private sealed class Failing : CopypartyBackend
    {
        public override (string? Command, string[] Prefix) Locate() => (null, []);

        public override IReadOnlyList<InstallAttempt> InstallAttempts() =>
        [
            Quick("first"),
            Quick("second"),
        ];

        private static InstallAttempt Quick(string name)
            => OperatingSystem.IsWindows()
                ? new InstallAttempt("cmd.exe", ["/c", "exit 1"], name)
                : new InstallAttempt("/bin/sh", ["-c", "exit 1"], name);

        public override string NotInstalledHint => "not installed";
        public override string NoInstallerHint => "no installer";
        public override string InstalledButNotFoundHint => "installed, not found";
        public override string InstallFailedHint => "install failed";
    }

    /// <summary>Reports on the thread that calls it, and remembers whether
    /// that was a pool worker.</summary>
    private sealed class Seen : IProgress<string>
    {
        public readonly List<(string Line, bool OnThePool)> Lines = [];

        public void Report(string value)
        {
            lock (Lines) Lines.Add((value, Thread.CurrentThread.IsThreadPoolThread));
        }
    }

    [Fact]
    public async Task The_sweep_runs_on_a_thread_of_its_own()
    {
        // Never the user's own runtime folder: the sweep stops what it finds.
        var configs = Directory.CreateTempSubdirectory("vaktari-qa-sweep").FullName;
        CopypartyShare.ConfigDirectoryOverride = configs;

        try
        {
            var sweeping = new CopypartyShare(new Failing()).SweepAsync();

            Assert.True(sweeping.CreationOptions.HasFlag(TaskCreationOptions.LongRunning),
                "the startup sweep runs on a pool worker, where killing and waiting for servers holds it");

            await sweeping.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            CopypartyShare.ConfigDirectoryOverride = null;
            Directory.Delete(configs, recursive: true);
        }
    }

    [Fact]
    public async Task An_install_attempt_holds_no_pool_worker()
    {
        var share = new CopypartyShare(new Failing());
        var progress = new Seen();

        var installed = await share.InstallAsync(progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(installed);

        List<(string Line, bool OnThePool)> lines;
        lock (progress.Lines) lines = [.. progress.Lines];

        // "running first…", then — after the first attempt's task — "first
        // failed, trying another way…" and "running second…", then the hint.
        var after = lines.SkipWhile(l => l.Line != "first failed, trying another way…").ToList();

        Assert.NotEmpty(after);
        Assert.All(after, l => Assert.False(l.OnThePool, $"\"{l.Line}\" was said on a pool worker: the attempt ran on the pool"));
    }
}
