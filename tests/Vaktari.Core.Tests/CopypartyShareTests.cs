using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a share is started with.
///
/// **Anyone who could reach the port could read the folder, the port was on
/// every interface, and it announced itself.** The config granted `r: *` —
/// or `rw: *` — to the world, bound copyparty to every address the machine
/// had, turned on zeroconf so the share appeared by name in every file
/// manager on the network, and wrote all of that to /tmp. A folder meant for
/// the person at the next desk was offered to the building.
///
/// Every share now has a password of its own and no anonymous access, the
/// server listens on the one address the URL names, announcing is a choice,
/// and the config — which now carries the password — is written where only
/// this user can read it. The config is a pure function of its inputs, and
/// most of this pins that text; the start itself is pinned through a launch
/// seam that reads the file copyparty would have been handed.
/// </summary>
public sealed class CopypartyShareTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-share-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _photos;

    public CopypartyShareTests()
    {
        _photos = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
        CopypartyShare.ConfigDirectoryOverride = _root;
    }

    public void Dispose()
    {
        CopypartyShare.ConfigDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private static readonly ShareOptions ReadOnly = new(Writable: false);

    private static string Config(ShareOptions? options = null)
        => CopypartyShare.Config("/srv/photos", 8080, options ?? ReadOnly, "192.0.2.7", "hunter2hunter2xx");

    private static List<string> Lines(string config)
        => config.Split('\n').Select(l => l.Trim()).ToList();

    // ---- the config ------------------------------------------------------------

    [Fact]
    public void Every_share_has_a_password_and_nobody_browses_without_it()
    {
        var lines = Lines(Config());

        Assert.Contains("[accounts]", lines);
        Assert.Contains("vaktari: hunter2hunter2xx", lines);
        Assert.Contains("r: vaktari", lines);
        Assert.DoesNotContain(lines, l => l.EndsWith(": *"));
    }

    [Fact]
    public void Uploads_are_granted_to_the_account_and_nobody_else()
    {
        var lines = Lines(Config(new ShareOptions(Writable: true)));

        Assert.Contains("rw: vaktari", lines);
        Assert.DoesNotContain("r: vaktari", lines);
        Assert.DoesNotContain(lines, l => l.EndsWith(": *"));
    }

    [Fact]
    public void The_server_listens_on_the_advertised_address_only()
        => Assert.Contains("i: 192.0.2.7", Lines(Config()));

    [Fact]
    public void A_share_is_not_announced_unless_asked()
    {
        Assert.DoesNotContain("z", Lines(Config()));
        Assert.Contains("z", Lines(Config(new ShareOptions(Writable: false, Announce: true))));
    }

    [Fact]
    public void The_address_carries_the_password()
        => Assert.Equal(
            "http://192.0.2.7:8080/?pw=hunter2hunter2xx",
            CopypartyShare.UrlFor("192.0.2.7", 8080, "hunter2hunter2xx"));

    [Fact]
    public void A_password_is_long_random_and_free_of_look_alikes()
    {
        var one = CopypartyShare.NewPassword();
        var two = CopypartyShare.NewPassword();

        Assert.Matches("^[abcdefghjkmnpqrstuvwxyz23456789]{16}$", one);
        Assert.NotEqual(one, two);
    }

    // ---- which address ---------------------------------------------------------

    /// <summary>
    /// **The first adapter with an address was the one named, and is now the
    /// one bound.** Measured on a machine with VMware and WSL installed: both
    /// virtual adapters are Up and hold an address, and neither has a
    /// gateway; the adapter the LAN is on has one. Whichever the runtime
    /// lists first, the one with a route out is the one other machines reach.
    /// </summary>
    [Fact]
    public void The_adapter_with_a_gateway_is_preferred_whatever_order_they_come_in()
        => Assert.Equal("192.168.4.2", CopypartyShare.ChooseAddress(
            [("172.18.176.1", false), ("192.168.158.1", false), ("192.168.4.2", true)]));

    [Fact]
    public void Without_a_gateway_anywhere_the_first_address_serves()
        => Assert.Equal("172.18.176.1", CopypartyShare.ChooseAddress(
            [("172.18.176.1", false), ("192.168.158.1", false)]));

    [Fact]
    public void With_no_address_at_all_it_is_loopback()
        => Assert.Equal("127.0.0.1", CopypartyShare.ChooseAddress([]));

    // ---- starting one ----------------------------------------------------------

    /// <summary>A backend that names a command and installs nothing.</summary>
    private sealed class Stub(string? warning = null) : CopypartyBackend
    {
        public override (string? Command, string[] Prefix) Locate() => ("copyparty-stub", []);
        public override IReadOnlyList<InstallAttempt> InstallAttempts() => [];
        public override string NotInstalledHint => "";
        public override string NoInstallerHint => "";
        public override string InstalledButNotFoundHint => "";
        public override string InstallFailedHint => "";
        public override string? StartWarning() => warning;
    }

    /// <summary>
    /// Stands in for copyparty: a process that exits at once, so nothing ever
    /// listens on the port. Whatever a test wants from the config it reads
    /// before returning, because the share deletes the file the moment the
    /// process ends.
    /// </summary>
    private static Process Stand(ProcessStartInfo _)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "exit", "0" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "exit 0" } };

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        return Process.Start(info)!;
    }

    private static string ConfigPathOf(ProcessStartInfo info)
        => info.ArgumentList[info.ArgumentList.IndexOf("-c") + 1];

    [Fact]
    public async Task Starting_a_share_writes_a_config_that_agrees_with_the_address_and_the_password()
    {
        string? configPath = null, configText = null;

        var share = new CopypartyShare(new Stub())
        {
            LaunchOverride = info =>
            {
                configPath = ConfigPathOf(info);
                configText = File.ReadAllText(configPath);
                return Stand(info);
            },
        };

        var session = await share.StartAsync(_photos, ReadOnly, CancellationToken.None);

        Assert.NotNull(configPath);
        Assert.Equal(_root, Path.GetDirectoryName(configPath));
        Assert.Matches("^[abcdefghjkmnpqrstuvwxyz23456789]{16}$", session.Password);
        Assert.Contains($"vaktari: {session.Password}", configText);
        Assert.EndsWith($"/?pw={session.Password}", session.Url);
        Assert.Contains($"i: {new Uri(session.Url).Host}", configText);
        Assert.Null(session.Warning);
    }

    /// <summary>The file carries the password, so it is nobody else's to read.</summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public async Task The_config_is_readable_by_this_user_alone()
    {
        UnixFileMode? mode = null;

        var share = new CopypartyShare(new Stub())
        {
            LaunchOverride = info =>
            {
                mode = File.GetUnixFileMode(ConfigPathOf(info));
                return Stand(info);
            },
        };

        await share.StartAsync(_photos, ReadOnly, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task What_the_platform_has_to_say_reaches_the_session()
    {
        var share = new CopypartyShare(new Stub("this machine is on a public network")) { LaunchOverride = Stand };

        var session = await share.StartAsync(_photos, ReadOnly, CancellationToken.None);

        Assert.Equal("this machine is on a public network", session.Warning);
    }

    // ---- after a crash ---------------------------------------------------------

    /// <summary>Stands in for a copyparty that keeps serving: a process that
    /// waits a minute. Every test that starts one kills it on the way out.</summary>
    private static Process Linger()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "60", "127.0.0.1" } }
            : new ProcessStartInfo("sleep") { ArgumentList = { "60" } };

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        return Process.Start(info)!;
    }

    private static void Bury(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <summary>
    /// **A share kept serving after Vaktari crashed or was killed,** and the
    /// next start could not find it: which servers were running lived only
    /// in memory, and the config named no process. The share now writes down
    /// its server and its owner beside the config, and the next provider
    /// built stops a server whose owner is gone. The owner here is this
    /// process with a start time it never had — which is what a Vaktari that
    /// died looks like once its id has gone to somebody else.
    /// </summary>
    [Fact]
    public async Task A_share_left_serving_by_a_vaktari_that_died_is_stopped_at_the_next_start()
    {
        Process? server = null;
        string? configPath = null;

        try
        {
            var crashed = new CopypartyShare(new Stub())
            {
                LaunchOverride = info =>
                {
                    configPath = ConfigPathOf(info);
                    return server = Linger();
                },
                Owner = (Environment.ProcessId, NeverStarted),
            };

            await crashed.StartAsync(_photos, ReadOnly, CancellationToken.None);

            Assert.True(File.Exists(CopypartyShare.RecordPathFor(configPath!)));

            // No StopAllAsync: nothing ran that a crash would have skipped.
            _ = new CopypartyShare(new Stub());

            Assert.True(server!.WaitForExit(5_000), "the orphaned server is still serving");
            Assert.False(File.Exists(configPath), "the config and its password were left behind");
            Assert.False(File.Exists(CopypartyShare.RecordPathFor(configPath!)));
        }
        finally
        {
            Bury(server);
        }
    }

    /// <summary>A portable copy and an installed one can run at once and
    /// share the private folder; the one that starts second must not stop
    /// the other's share.</summary>
    [Fact]
    public async Task A_share_whose_vaktari_is_still_running_is_left_alone()
    {
        Process? server = null;
        string? configPath = null;

        try
        {
            var running = new CopypartyShare(new Stub())
            {
                LaunchOverride = info =>
                {
                    configPath = ConfigPathOf(info);
                    return server = Linger();
                },
            };

            await running.StartAsync(_photos, ReadOnly, CancellationToken.None);

            _ = new CopypartyShare(new Stub());

            Assert.False(server!.WaitForExit(1_000), "another copy's live share was stopped");
            Assert.True(File.Exists(configPath));
            Assert.True(File.Exists(CopypartyShare.RecordPathFor(configPath!)));

            await running.StopAllAsync();
        }
        finally
        {
            Bury(server);
        }
    }

    /// <summary>A start no process ever had, on either platform's reckoning:
    /// this process's id with it is a Vaktari that died and whose id went to
    /// somebody else.</summary>
    private const long NeverStarted = -1;

    /// <summary>Writes a record by hand, as a share would have.</summary>
    private string Record(string name, int childPid, long childStarted, int ownerPid, long ownerStarted)
    {
        var config = Path.Combine(_root, $"vaktari-share-{name}.conf");
        File.WriteAllText(config, "[accounts]\n  vaktari: hunter2hunter2xx\n");
        File.WriteAllText(CopypartyShare.RecordPathFor(config),
            $"{childPid}\n{childStarted}\n{ownerPid}\n{ownerStarted}\n");

        return config;
    }

    /// <summary>
    /// **A live server whose start did not match lost its record.** When the
    /// owner looked dead and the child's start missed — on Linux, by a clock
    /// step between the Vaktari that wrote the record and the one reading it —
    /// the sweep fell through to deleting the config and the record, and the
    /// server went on serving with its password where nothing would ever find
    /// it again. While anything holds the id, the record stays.
    /// </summary>
    [Fact]
    public void A_record_whose_server_is_still_running_but_does_not_match_is_kept()
    {
        var server = Linger();

        try
        {
            var config = Record("0123456789abcdef0123456789abcdef",
                server.Id, NeverStarted, Environment.ProcessId, NeverStarted);

            _ = new CopypartyShare(new Stub());

            Assert.False(server.HasExited, "a server the record could not prove was ours was killed");
            Assert.True(File.Exists(CopypartyShare.RecordPathFor(config)), "the only record of a live server was deleted");
            Assert.True(File.Exists(config));
        }
        finally
        {
            Bury(server);
        }
    }

    /// <summary>
    /// **On Linux the record's start time moved with the clock.** It was
    /// Process.StartTime, which .NET reckons from a boot time each process
    /// works out for itself from the wall clock, so the next Vaktari read a
    /// different number for the same server after any clock step. The record
    /// holds the kernel's own count now — field 22 of /proc/[pid]/stat, read
    /// here independently — which every process reads the same.
    /// </summary>
    [PosixFact]
    public async Task On_linux_a_record_holds_the_kernels_own_start_count()
    {
        Process? server = null;
        string? configPath = null;

        try
        {
            var share = new CopypartyShare(new Stub())
            {
                LaunchOverride = info =>
                {
                    configPath = ConfigPathOf(info);
                    return server = Linger();
                },
            };

            await share.StartAsync(_photos, ReadOnly, CancellationToken.None);

            var stat = File.ReadAllText($"/proc/{server!.Id}/stat");
            var kernel = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19];

            var record = File.ReadAllLines(CopypartyShare.RecordPathFor(configPath!));

            Assert.Equal(kernel, record[1]);

            await share.StopAllAsync();
        }
        finally
        {
            Bury(server);
        }
    }

    /// <summary>
    /// **One unreadable file stopped the whole sweep.** The only catch was
    /// around the loop, so a record that could not be read — deleted by
    /// another copy between the listing and the read, or held by a scanner —
    /// skipped every file after it until the next start, old passwords
    /// included. The files the sweep should still clear are shares whose
    /// server and Vaktari are both long gone.
    ///
    /// **Which files list first is the filesystem's business** — NTFS sorts
    /// by name, tmpfs on the Linux runner listed the newest first and so ran
    /// the unreadable records last, which passed with the fix removed. So the
    /// unreadable ones are named to sort first and written both before and
    /// after the rest: no order of listing puts all ten behind all twenty.
    /// </summary>
    [Fact]
    public void A_record_that_cannot_be_read_does_not_stop_the_sweep()
    {
        var held = new List<FileStream>();

        void Unreadable(int i)
        {
            var record = Path.Combine(_root, $"vaktari-share-0000{i:D28}.pid");
            File.WriteAllText(record, "1\n1\n1\n1\n");

            // Unreadable the way each platform makes a file unreadable.
            if (OperatingSystem.IsWindows())
                held.Add(new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            else
                File.SetUnixFileMode(record, UnixFileMode.None);
        }

        try
        {
            for (var i = 0; i < 5; i++) Unreadable(i);

            // A process id nothing holds, for the server and for its Vaktari.
            const int gone = int.MaxValue - 1;

            var dead = Enumerable.Range(0, 20)
                .Select(i => Record($"ffff{i:D28}", gone, NeverStarted, gone, NeverStarted))
                .ToList();

            for (var i = 5; i < 10; i++) Unreadable(i);

            _ = new CopypartyShare(new Stub());

            Assert.All(dead, config =>
            {
                Assert.False(File.Exists(config), $"{Path.GetFileName(config)} was left behind");
                Assert.False(File.Exists(CopypartyShare.RecordPathFor(config)));
            });
        }
        finally
        {
            foreach (var stream in held) stream.Dispose();
        }
    }

    /// <summary>A config from before records existed names nothing to stop,
    /// but it holds a password: an old one goes, and one written a moment
    /// ago — a share another copy is starting right now — stays.</summary>
    [Fact]
    public void An_old_config_with_no_record_is_cleared_and_a_new_one_is_not()
    {
        var old = Path.Combine(_root, "vaktari-share-0123456789abcdef0123456789abcdef.conf");
        var fresh = Path.Combine(_root, "vaktari-share-fedcba9876543210fedcba9876543210.conf");

        File.WriteAllText(old, "[accounts]\n  vaktari: hunter2hunter2xx\n");
        File.WriteAllText(fresh, "[accounts]\n  vaktari: hunter2hunter2xx\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        _ = new CopypartyShare(new Stub());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>Not /tmp: the session's private folder, the same one the
    /// instance lock lives in. A Linux fact — on Windows the private folder
    /// IS the per-user temp folder, and this could not tell the two apart.</summary>
    [PosixFact]
    public void The_config_folder_is_the_sessions_private_one()
    {
        CopypartyShare.ConfigDirectoryOverride = null;

        Assert.Equal(PrivateDirectory.Runtime(), CopypartyShare.ConfigDirectory);
    }
}
