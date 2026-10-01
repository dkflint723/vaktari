using System.Diagnostics;
using Vaktari.Core.Vcs;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Git in a pane opened through "\\?\" (0.11.1 path-safety check, left for
/// 0.11.2).
///
/// **What git does with such a root was measured first**, with Git for
/// Windows 2.55: "-C \\?\…\repo" and a "\\?\…" pathspec answer exactly as the
/// plain spelling does, so the decorations can work there. Two things stood in
/// the way:
///
/// - git writes "/" between names, and GetFullPath leaves a "\\?\" path as
///   written, so "sub/" became "…\sub/", matched no row, and nothing under a
///   folder was rolled up to it;
/// - git strips the prefix and opens the rest as a plain path, so "-C
///   \\?\…\repo " beside a repository "repo" answered repo's status — the
///   neighbour's decorations. Such a folder is not handed to git at all.
///
/// The recording git of <see cref="GitArgumentsTests"/> answers with a fixed
/// line, so what is parsed is known; one test asks the real git, which the
/// Windows runners carry. Shares a collection with that class, because both
/// set the process-wide executable override.
/// </summary>
[Collection("git executable")]
public sealed class GitExtendedPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-gitext-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _recorded;

    public GitExtendedPathTests()
    {
        Directory.CreateDirectory(_root);
        _recorded = Path.Combine(_root, "argv.txt");
        Environment.SetEnvironmentVariable("VAKTARI_GIT_ARGS", _recorded);
    }

    public void Dispose()
    {
        GitVersionControl.ExecutableOverride = null;
        Environment.SetEnvironmentVariable("VAKTARI_GIT_ARGS", null);
        GitVersionControl.ForgetProbe();

        try
        {
            // Through "\\?\", so "repo " is removed as itself; git marks its
            // object files read-only.
            var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(root, recursive: true);
        }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>A git that answers --version, records its arguments, and
    /// prints one porcelain record: an untracked folder "sub".</summary>
    private void RecordingGit()
    {
        var script = Path.Combine(_root, "git.cmd");
        File.WriteAllText(script,
            "@echo off\r\n"
            + "if \"%~1\"==\"--version\" (echo git version 2.0.0-fake & exit /b 0)\r\n"
            + "for %%a in (%*) do echo %%~a>>\"%VAKTARI_GIT_ARGS%\"\r\n"
            + "<nul set /p \"=?? sub/\"\r\n"
            + "exit /b 0\r\n");

        GitVersionControl.ExecutableOverride = script;
        GitVersionControl.ForgetProbe();
    }

    [WindowsFact]
    public async Task A_folder_git_names_with_a_slash_is_the_row_in_an_extended_pane()
    {
        RecordingGit();

        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "sub"));

        var snapshot = await new GitVersionControl().StatusAsync(@"\\?\" + repo, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(
            new Dictionary<string, VcsState> { [@"\\?\" + Path.Combine(repo, "sub")] = VcsState.Untracked },
            snapshot.States);
    }

    /// <summary>"repo " — a repository of its own — beside "repo": asked about
    /// "repo " itself, git reads the neighbour (measured), so it is never
    /// asked. A folder inside it is refused by the same hand-off rule, which
    /// reads every name on the way and does not guess what git strips.</summary>
    [WindowsTheory]
    [InlineData("")]
    [InlineData("inner")]
    public async Task A_folder_under_a_name_git_would_fold_is_never_handed_to_git(string below)
    {
        RecordingGit();

        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));

        var own = @"\\?\" + Path.Combine(_root, "repo ");
        Directory.CreateDirectory(Path.Combine(own, ".git"));
        Directory.CreateDirectory(Path.Combine(own, "inner"));

        var folder = below.Length == 0 ? own : Path.Combine(own, below);

        Assert.Null(await new GitVersionControl().StatusAsync(folder, CancellationToken.None));
        Assert.False(File.Exists(_recorded), "git was handed a folder it would read as its neighbour");
    }

    /// <summary>
    /// The real git, end to end: a file and a folder in a repository opened
    /// through "\\?\" are decorated on their own rows.
    /// </summary>
    [WindowsFact]
    public async Task The_real_git_decorates_a_pane_opened_through_the_extended_prefix()
    {
        GitVersionControl.ExecutableOverride = null;
        GitVersionControl.ForgetProbe();

        var git = new GitVersionControl();
        Assert.True(git.IsAvailable, "git is not on PATH here, and this test is about what git does");

        var repo = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        Run(repo, "init", "-q");

        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        File.WriteAllText(Path.Combine(repo, "sub", "b.txt"), "b");

        var snapshot = await git.StatusAsync(@"\\?\" + repo, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(
            new Dictionary<string, VcsState>
            {
                [@"\\?\" + Path.Combine(repo, "a.txt")] = VcsState.Untracked,
                [@"\\?\" + Path.Combine(repo, "sub")] = VcsState.Untracked,
            },
            snapshot.States);
    }

    private static void Run(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(30_000), "git init did not finish");
        Assert.Equal(0, process.ExitCode);
    }
}
