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

    /// <summary>No binary, no feature — and a reason the UI can show. The
    /// locate seam makes this true whatever the build machine has installed.</summary>
    [Fact]
    public void Without_the_tool_the_feature_says_what_to_install()
    {
        var links = new ProtonDriveLinks { LocateOverride = () => null };

        Assert.False(links.IsAvailable);
        Assert.Contains("install", links.UnavailableReason);
    }

    // ---- installing the tool ----------------------------------------------

    /// <summary>The URLs are pinned: a version that was tested is a version
    /// that keeps working, and both answered 200 when this was written.</summary>
    [Fact]
    public void The_download_url_is_the_published_build_for_this_platform()
    {
        var url = ProtonDriveLinks.Grammar.DownloadUrl();

        if (OperatingSystem.IsWindows())
            Assert.Equal(
                "https://proton.me/download/drive/cli/0.8.0/windows-x64/proton-drive.exe", url);
        else if (OperatingSystem.IsLinux())
            Assert.Equal(
                "https://proton.me/download/drive/cli/0.8.0/linux-x64/proton-drive", url);
    }

    [Fact]
    public async Task Installing_lands_the_tool_and_the_feature_comes_alive()
    {
        var tools = Directory.CreateTempSubdirectory("vaktari-proton-install").FullName;

        try
        {
            var name = OperatingSystem.IsWindows() ? "proton-drive.exe" : "proton-drive";
            var landed = Path.Combine(tools, name);

            var links = new ProtonDriveLinks
            {
                ToolsDirOverride = tools,

                // Discovery sees only the test's folder — the machine's own
                // installs must not leak in.
                LocateOverride = () => File.Exists(landed) ? landed : null,

                FetchOverride = (url, destination, _) =>
                {
                    Assert.Contains("proton.me", url);
                    return File.WriteAllBytesAsync(destination, [1, 2, 3]);
                },
            };

            Assert.False(links.IsAvailable);

            var lines = new List<string>();
            var done = await links.InstallAsync(
                new Immediate(lines.Add), CancellationToken.None);

            Assert.True(done);
            Assert.True(links.IsAvailable);
            Assert.True(File.Exists(landed));
            Assert.Contains(lines, l => l.Contains("ready"));
        }
        finally
        {
            Directory.Delete(tools, recursive: true);
        }
    }

    /// <summary>A dead download says so and leaves nothing that discovery
    /// would mistake for a working tool.</summary>
    [Fact]
    public async Task A_failed_download_reports_and_leaves_nothing_behind()
    {
        var tools = Directory.CreateTempSubdirectory("vaktari-proton-fail").FullName;

        try
        {
            var links = new ProtonDriveLinks
            {
                ToolsDirOverride = tools,
                LocateOverride = () => null,
                FetchOverride = (_, _, _) => throw new IOException("the network went away"),
            };

            var lines = new List<string>();
            var done = await links.InstallAsync(
                new Immediate(lines.Add), CancellationToken.None);

            Assert.False(done);
            Assert.False(links.IsAvailable);
            Assert.Empty(Directory.GetFiles(tools));
            Assert.Contains(lines, l => l.Contains("the network went away"));
        }
        finally
        {
            Directory.Delete(tools, recursive: true);
        }
    }

    /// <summary>IProgress without a SynchronizationContext detour, so the
    /// lines are there when the assert runs.</summary>
    private sealed class Immediate(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }

    // ---- guessing the sync folder -----------------------------------------

    [Fact]
    public void The_folder_guess_takes_a_direct_my_files()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-guess").FullName;

        try
        {
            var myFiles = Path.Combine(home, "My files");
            Directory.CreateDirectory(myFiles);

            Assert.Equal(myFiles, ProtonDriveLinks.GuessLocalRoot(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void The_folder_guess_descends_into_a_single_account()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-guess").FullName;

        try
        {
            var myFiles = Path.Combine(home, "me@proton.me", "My files");
            Directory.CreateDirectory(myFiles);

            Assert.Equal(myFiles, ProtonDriveLinks.GuessLocalRoot(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Two accounts is no answer at all: a wrong guess links the
    /// wrong account's files, which is worse than asking.</summary>
    [Fact]
    public void Two_accounts_refuse_to_guess()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-guess").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(home, "one@proton.me", "My files"));
            Directory.CreateDirectory(Path.Combine(home, "two@proton.me", "My files"));

            Assert.Null(ProtonDriveLinks.GuessLocalRoot(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void No_proton_folder_no_guess()
        => Assert.Null(ProtonDriveLinks.GuessLocalRoot(
            Path.Combine(Path.GetTempPath(), "vaktari-no-such-folder")));

    // ---- the app's own mapping file ---------------------------------------

    /// <summary>The shape is the REAL file's, copied from a live install —
    /// the app records its sync root under Mappings[].Local.RootFolderPath,
    /// which finds a root moved to another drive that no layout guess would.</summary>
    [Fact]
    public void The_mapping_file_names_the_sync_root()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-map").FullName;

        try
        {
            var root = Path.Combine(home, "Proton-Drive", "My files");
            Directory.CreateDirectory(root);

            var file = Path.Combine(home, "Mappings.json");
            File.WriteAllText(file, $$"""
                {
                  "Mappings": [
                    {
                      "Id": 12,
                      "Type": 1,
                      "SyncMethod": 1,
                      "Status": 1,
                      "Remote": { "VolumeId": "x", "RootFolderName": null, "RootItemType": 1 },
                      "Local": {
                        "VolumeSerialNumber": 250661234,
                        "RootFolderPath": {{System.Text.Json.JsonSerializer.Serialize(root)}},
                        "RootFolderId": 5066549580847718,
                        "InternalVolumeId": 1
                      }
                    }
                  ],
                  "LatestId": 13
                }
                """);

            Assert.Equal(root, ProtonDriveLinks.FromMappings(file));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Among several mappings — other computers' mirrors — the one
    /// named "My files" is the person's own drive.</summary>
    [Fact]
    public void Among_several_mappings_my_files_wins()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-map").FullName;

        try
        {
            var mine = Path.Combine(home, "My files");
            var mirror = Path.Combine(home, "Laptop");
            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(mirror);

            var file = Path.Combine(home, "Mappings.json");
            File.WriteAllText(file, $$"""
                {
                  "Mappings": [
                    { "Local": { "RootFolderPath": {{System.Text.Json.JsonSerializer.Serialize(mirror)}} } },
                    { "Local": { "RootFolderPath": {{System.Text.Json.JsonSerializer.Serialize(mine)}} } }
                  ]
                }
                """);

            Assert.Equal(mine, ProtonDriveLinks.FromMappings(file));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>A mapping whose folder is gone — an unplugged drive — must
    /// not aim the feature at nothing.</summary>
    [Fact]
    public void A_mapping_to_a_missing_folder_does_not_count()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-map").FullName;

        try
        {
            var file = Path.Combine(home, "Mappings.json");
            File.WriteAllText(file, """
                { "Mappings": [ { "Local": { "RootFolderPath": "Q:\\unplugged\\My files" } } ] }
                """);

            Assert.Null(ProtonDriveLinks.FromMappings(file));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_mapping_file_answers_nothing()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-proton-map").FullName;

        try
        {
            var file = Path.Combine(home, "Mappings.json");
            File.WriteAllText(file, "{ not json");

            Assert.Null(ProtonDriveLinks.FromMappings(file));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void No_mapping_file_no_answer()
        => Assert.Null(ProtonDriveLinks.FromMappings(
            Path.Combine(Path.GetTempPath(), "vaktari-no-such", "Mappings.json")));
}
