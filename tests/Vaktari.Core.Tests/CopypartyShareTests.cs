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
