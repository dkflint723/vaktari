using System.Diagnostics;
using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>The classes that set CopypartyShare.ConfigDirectoryOverride,
/// which is one static for the process: they take turns.</summary>
[CollectionDefinition(Name)]
public sealed class CopypartyConfig
{
    public const string Name = "copyparty config directory";
}

/// <summary>
/// **Building the sharing provider looked for copyparty, on the UI thread.**
/// The constructor ran the backend's Locate — on Windows up to three Python
/// launches, each allowed ten seconds; measured 16-135 ms on a warm machine —
/// and the leftover sweep, inside the first window's constructor, for a
/// feature most sessions never use. The look is now asked for (EnsureKnownAsync,
/// started by the application after startup and awaited by a share), and the
/// newest look is the only one whose answer counts.
///
/// **One collection with CopypartyShareTests**, because both point the
/// process-wide ConfigDirectoryOverride at a folder of their own: run side by
/// side, one class's Dispose put it back to the real private folder under
/// the other's sweep (measured: the full suite failed the sweep test that
/// way, while each class passed alone).
/// </summary>
[Collection(CopypartyConfig.Name)]
public sealed class CopypartyLookTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-look-" + Guid.NewGuid().ToString("N")[..12]);

    public CopypartyLookTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        CopypartyShare.ConfigDirectoryOverride = _root;
    }

    public void Dispose()
    {
        CopypartyShare.ConfigDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Answers from a queue of gates, one per look, so a test decides
    /// when each look finishes and what it found.</summary>
    private sealed class Gated : CopypartyBackend
    {
        public int Looks;
        public int LooksOnThePool;
        public readonly List<(ManualResetEventSlim Gate, string? Command)> Answers = [];

        public override (string? Command, string[] Prefix) Locate()
        {
            var n = Interlocked.Increment(ref Looks) - 1;

            if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref LooksOnThePool);

            (ManualResetEventSlim gate, string? command) answer;
            lock (Answers) answer = n < Answers.Count ? Answers[n] : (new ManualResetEventSlim(true), "copyparty-stub");

            answer.gate.Wait(TimeSpan.FromSeconds(30));
            return (answer.command, []);
        }

        public override IReadOnlyList<InstallAttempt> InstallAttempts() => [];
        public override string NotInstalledHint => "not installed";
        public override string NoInstallerHint => "";
        public override string InstalledButNotFoundHint => "";
        public override string InstallFailedHint => "";
    }

    [Fact]
    public void Building_the_provider_does_not_look_for_copyparty()
    {
        var backend = new Gated();

        var share = new CopypartyShare(backend);

        Assert.Equal(0, backend.Looks);
        Assert.False(share.IsKnown);
        Assert.False(share.IsAvailable);
    }

    [Fact]
    public async Task Asking_twice_looks_once_and_a_share_waits_for_the_look()
    {
        var backend = new Gated();
        var gate = new ManualResetEventSlim(false);
        backend.Answers.Add((gate, "copyparty-stub"));

        var share = new CopypartyShare(backend) { LaunchOverride = _ => Process.Start(Quick()) };

        var first = share.EnsureKnownAsync();
        var second = share.EnsureKnownAsync();
        Assert.Same(first, second);

        var starting = share.StartAsync(Path.Combine(_root, "photos"), new ShareOptions(false), CancellationToken.None);

        await Task.Delay(100);
        Assert.False(starting.IsCompleted, "the share started before the look had answered");
        Assert.False(share.IsKnown);

        gate.Set();

        var session = await starting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(share.IsKnown);
        Assert.True(share.IsAvailable);
        Assert.Equal(1, backend.Looks);

        await share.StopAsync(session);
    }

    /// <summary>
    /// **The startup look finishing after an install's must not have the last
    /// word** (review H3). The install re-looks; the slow first look that
    /// still says "not installed" lands afterwards and is ignored.
    /// </summary>
    [Fact]
    public async Task The_newest_look_wins()
    {
        var backend = new Gated();
        var slowFirst = new ManualResetEventSlim(false);
        backend.Answers.Add((slowFirst, null));
        backend.Answers.Add((new ManualResetEventSlim(true), "copyparty-stub"));

        var share = new CopypartyShare(backend);
        var announced = 0;
        share.Changed += (_, _) => Interlocked.Increment(ref announced);

        var first = share.EnsureKnownAsync();

        // What an install does once its command succeeds.
        await share.RescanAsync();

        Assert.True(share.IsAvailable);

        slowFirst.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.True(share.IsAvailable, "the older look's answer replaced the newer one");
        Assert.Equal(1, Volatile.Read(ref announced));
    }

    /// <summary>
    /// **The look held a pool worker for as long as Python took to answer**
    /// — three launches, ten seconds each — and it starts at startup, beside
    /// the listing and every store's WriteBehind. CI run 37256500633: with
    /// this class's gates holding workers on a busy four-core runner, a
    /// trivial pool continuation waited more than ten seconds. Both the
    /// startup look and an install's re-look must run off the pool.
    /// </summary>
    [Fact]
    public async Task A_look_holds_no_pool_worker()
    {
        var backend = new Gated();
        var share = new CopypartyShare(backend);

        await share.EnsureKnownAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await share.RescanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, backend.Looks);
        Assert.Equal(0, Volatile.Read(ref backend.LooksOnThePool));
    }

    private static ProcessStartInfo Quick()
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("true") { UseShellExecute = false };
}
