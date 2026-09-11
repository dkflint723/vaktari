using Vaktari.Core.Vcs;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What git is handed, argument by argument.
///
/// **The status call built its arguments as one quoted string.** .NET splits
/// that string into argv with quote rules on every platform, so a folder whose
/// name held a `"` — legal on Linux — closed the quote early and the rest of
/// the name arrived as git options. <c>-c core.fsmonitor=&lt;command&gt;</c> is
/// honoured by <c>git status</c>, which made browsing into a folder unpacked
/// from somebody else's archive a way to run their command.
///
/// Proved with a recording git that writes each argument it receives on a
/// line of its own, so the test reads what git would have read. It is pointed
/// at through <see cref="GitVersionControl.ExecutableOverride"/> rather than
/// PATH: a bare "git" is resolved by CreateProcess as git.exe alone and walks
/// straight past a git.cmd to the real one — measured, as a test that swore
/// git was never called while the real git was answering "not a repository".
///
/// The quote case is Posix-only because Windows will not create such a name;
/// the shape — a list, literal pathspecs first, the folder last and whole — is
/// pinned on both.
/// </summary>
public sealed class GitArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-gitargs-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _recorded;

    public GitArgumentsTests()
    {
        Directory.CreateDirectory(_root);
        _recorded = Path.Combine(_root, "argv.txt");

        // A repository is a folder with a .git marker; FindRoot walks up to it
        // without asking git, so nothing here needs a real one.
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));

        GitVersionControl.ExecutableOverride = WriteRecordingGit();
        Environment.SetEnvironmentVariable("VAKTARI_GIT_ARGS", _recorded);

        // The availability probe is cached across the process; a run that
        // probed before the override was in place would not be answered by it.
        GitVersionControl.ForgetProbe();
    }

    public void Dispose()
    {
        GitVersionControl.ExecutableOverride = null;
        Environment.SetEnvironmentVariable("VAKTARI_GIT_ARGS", null);
        GitVersionControl.ForgetProbe();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Answers `--version` with an exit code of zero, and records every other
    /// call's arguments one per line. Prints no status output, which the caller
    /// reads as "nothing decorated" and is all this test needs.
    /// </summary>
    private string WriteRecordingGit()
    {
        if (OperatingSystem.IsWindows())
        {
            // %~a strips the quotes ArgumentList put around an argument with a
            // space, so a line is the argument as git would have seen it.
            var script = Path.Combine(_root, "git.cmd");
            File.WriteAllText(script,
                "@echo off\r\n"
                + "if \"%~1\"==\"--version\" (echo git version 2.0.0-fake & exit /b 0)\r\n"
                + "for %%a in (%*) do echo %%~a>>\"%VAKTARI_GIT_ARGS%\"\r\n"
                + "exit /b 0\r\n");
            return script;
        }
        else
        {
            var script = Path.Combine(_root, "git");
            File.WriteAllText(script,
                "#!/bin/sh\n"
                + "if [ \"$1\" = \"--version\" ]; then echo git version 2.0.0-fake; exit 0; fi\n"
                + "for a in \"$@\"; do printf '%s\\n' \"$a\" >> \"$VAKTARI_GIT_ARGS\"; done\n"
                + "exit 0\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return script;
        }
    }

    private async Task<string[]> ArgumentsFor(string folderName)
    {
        var folder = Path.Combine(_root, "repo", folderName);
        Directory.CreateDirectory(folder);

        var git = new GitVersionControl();
        Assert.True(git.IsAvailable, "the recording git was not accepted by the probe");

        await git.StatusAsync(folder, CancellationToken.None);

        Assert.True(File.Exists(_recorded), "git was never called");
        return File.ReadAllLines(_recorded);
    }

    /// <summary>The vulnerability itself, where the name can exist.</summary>
    [PosixFact]
    public async Task A_quote_in_a_folder_name_stays_inside_the_folder_argument()
    {
        const string name = "docs\" -c core.fsmonitor=touch${IFS}/tmp/pwned \"x";

        var argv = await ArgumentsFor(name);

        // One argument, verbatim: the whole name, quote and all, is the last
        // thing git receives — after the `--` that ends the options.
        Assert.Equal(Path.Combine(_root, "repo", name), argv[^1]);
        Assert.Equal("--", argv[^2]);
        Assert.DoesNotContain(argv, a => a.StartsWith("-c", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("fsmonitor", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shape, on both platforms: literal pathspecs before anything else,
    /// and a name with a space arriving as one argument at the end.
    /// </summary>
    [Fact]
    public async Task The_folder_arrives_whole_and_pathspecs_are_literal()
    {
        var argv = await ArgumentsFor("has space");

        Assert.Equal("--literal-pathspecs", argv[0]);
        Assert.Equal("-C", argv[1]);
        Assert.Equal(Path.Combine(_root, "repo"), argv[2]);
        Assert.Equal("--", argv[^2]);
        Assert.Equal(Path.Combine(_root, "repo", "has space"), argv[^1]);
    }
}
