using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The Proton link provider, tested around a stand-in binary.
///
/// A build machine has no Proton account and never will, so everything here
/// runs against the runner seam — which is also what keeps the tests honest
/// about what they prove: the mapping, the argv, and the parsing are pinned;
/// the CLI's real verbs are pinned separately against its own --help.
/// </summary>
public sealed class ProtonDriveLinksTests
{
    private static ProtonDriveLinks Fresh(
        Func<IReadOnlyList<string>, ProtonDriveLinks.CliResult>? answer = null)
    {
        var links = new ProtonDriveLinks(binaryOverride: "/fake/proton-drive")
        {
            LocalRoot = OperatingSystem.IsWindows() ? @"D:\Proton-Drive" : "/home/f/Proton-Drive",
        };

        if (answer is not null)
            links.RunOverride = (args, _) => Task.FromResult(answer(args));

        return links;
    }

    private static string Local(string relative)
        => Path.Combine(
            OperatingSystem.IsWindows() ? @"D:\Proton-Drive" : "/home/f/Proton-Drive",
            relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- mapping -----------------------------------------------------------

    /// <summary>The drive speaks forward slashes whatever this machine does.</summary>
    [Fact]
    public void A_nested_path_maps_under_the_remote_root()
        => Assert.Equal(
            "/my-files/My files/Software/tool.zip",
            Fresh().MapToRemote(Local("My files/Software/tool.zip")));

    [Fact]
    public void The_root_itself_maps_to_the_remote_root()
        => Assert.Equal("/my-files", Fresh().MapToRemote(
            OperatingSystem.IsWindows() ? @"D:\Proton-Drive\" : "/home/f/Proton-Drive/"));

    /// <summary>
    /// **Outside the folder is null, not a guess.** A path with no remote twin
    /// has nothing to link to, and offering the menu item there would promise
    /// something the drive cannot do.
    /// </summary>
    [Fact]
    public void Outside_the_folder_there_is_no_mapping()
    {
        Assert.Null(Fresh().MapToRemote(
            OperatingSystem.IsWindows() ? @"C:\Users\f\notes.txt" : "/home/f/notes.txt"));

        // A sibling whose name merely starts the same is outside too.
        Assert.Null(Fresh().MapToRemote(
            OperatingSystem.IsWindows() ? @"D:\Proton-Drive-old\x" : "/home/f/Proton-Drive-old/x"));
    }

    [Fact]
    public void With_no_root_configured_nothing_maps()
    {
        var links = Fresh();
        links.LocalRoot = "";

        Assert.Null(links.MapToRemote(Local("anything.txt")));
    }

    // ---- the conversation --------------------------------------------------

    /// <summary>Creating a link speaks the documented verb and hands back the
    /// URL from the tool's JSON.</summary>
    [Fact]
    public async Task Creating_a_link_asks_set_url_and_reads_the_answer()
    {
        IReadOnlyList<string>? spoken = null;

        var links = Fresh(args =>
        {
            spoken = args;
            return new(0, """{"url":"https://drive.proton.me/urls/ABC123#key"}""", "");
        });

        var link = await links.CreateLinkAsync(Local("My files/report.pdf"), CancellationToken.None);

        Assert.Equal(["sharing", "set-url", "/my-files/My files/report.pdf", "--json"], spoken);
        Assert.Equal("https://drive.proton.me/urls/ABC123#key", link.Url);
        Assert.Equal("/my-files/My files/report.pdf", link.RemotePath);
    }

    /// <summary>A tool that prints the URL plainly rather than as JSON still
    /// gets read — the fallback the parser promises.</summary>
    [Fact]
    public async Task A_plainly_printed_url_is_read_too()
    {
        var links = Fresh(_ => new(0, "https://drive.proton.me/urls/PLAIN#k\n", ""));

        var link = await links.CreateLinkAsync(Local("a.txt"), CancellationToken.None);

        Assert.Equal("https://drive.proton.me/urls/PLAIN#k", link.Url);
    }

    /// <summary>
    /// **A refusal carries the tool's own sentence.** "exit 1" tells the user
    /// nothing; the CLI's line about not being signed in tells them what to do.
    /// </summary>
    [Fact]
    public async Task A_refusal_speaks_the_tools_own_words()
    {
        var links = Fresh(_ => new(1, "", "You are not signed in. Run auth login first.\n"));

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => links.CreateLinkAsync(Local("a.txt"), CancellationToken.None));

        Assert.Contains("not signed in", thrown.Message);
    }

    [Fact]
    public async Task Sharing_something_outside_the_folder_is_refused_before_the_tool_runs()
    {
        var ran = false;
        var links = Fresh(_ => { ran = true; return new(0, "", ""); });

        await Assert.ThrowsAsync<IOException>(() => links.CreateLinkAsync(
            OperatingSystem.IsWindows() ? @"C:\elsewhere\a.txt" : "/elsewhere/a.txt",
            CancellationToken.None));

        Assert.False(ran, "nothing should be spoken for a path with no remote twin");
    }

    [Fact]
    public async Task Revoking_speaks_the_removal_verb_for_the_remote_path()
    {
        IReadOnlyList<string>? spoken = null;
        var links = Fresh(args => { spoken = args; return new(0, "{}", ""); });

        await links.RevokeAsync(
            new DriveLink(Local("a.txt"), "/my-files/a.txt", "https://x"), CancellationToken.None);

        Assert.NotNull(spoken);
        Assert.Equal("sharing", spoken![0]);
        Assert.Contains("/my-files/a.txt", spoken);
    }

    /// <summary>
    /// The sign-in flow: the tool prints a link mid-stream, the caller gets it
    /// opened, and success is the process finishing cleanly — the same
    /// click-a-link-and-authenticate shape the CLI gives every app.
    /// </summary>
    [Fact]
    public async Task Signing_in_surfaces_the_link_and_reports_completion()
    {
        var links = Fresh();
        var opened = new List<string>();

        links.StreamOverride = (args, onLine, _) =>
        {
            Assert.Equal(["auth", "login"], args);

            onLine("Proton Drive CLI");
            onLine("To continue, open: https://account.proton.me/authorize?code=XYZ in your browser");
            onLine("Waiting for you to finish…");
            onLine("Signed in.");

            return Task.FromResult(0);
        };

        var ok = await links.SignInAsync(url => opened.Add(url), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(["https://account.proton.me/authorize?code=XYZ"], opened);
    }

    [Fact]
    public async Task An_abandoned_sign_in_reports_failure()
    {
        var links = Fresh();
        links.StreamOverride = (_, _, _) => Task.FromResult(1);

        Assert.False(await links.SignInAsync(_ => { }, CancellationToken.None));
    }

    /// <summary>The heuristic that turns a refusal into a sign-in: loose on
    /// purpose, and quiet about everything else.</summary>
    [Theory]
    [InlineData("You are not signed in. Run auth login first.", true)]
    [InlineData("No valid session found", true)]
    [InlineData("Authentication required", true)]
    [InlineData("the network is unreachable", false)]
    [InlineData("no such remote path", false)]
    public void Only_signed_out_refusals_trigger_the_browser(string complaint, bool expected)
        => Assert.Equal(expected, ProtonDriveLinks.LooksSignedOut(complaint));

    /// <summary>No binary, no feature — and a reason the UI can show.</summary>
    [Fact]
    public void Without_the_tool_the_feature_says_what_to_install()
    {
        var links = new ProtonDriveLinks(binaryOverride: null);

        // The machine building this may genuinely have the CLI on PATH; only
        // assert the negative half when it does not.
        if (!links.IsAvailable)
            Assert.Contains("proton.me", links.UnavailableReason);
    }
}
