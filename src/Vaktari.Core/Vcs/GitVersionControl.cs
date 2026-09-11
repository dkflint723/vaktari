using System.Diagnostics;

namespace Vaktari.Core.Vcs;

/// <summary>
/// Git status by running `git`, not by linking a library.
///
/// Same reasoning as copyparty and `gio`: the useful thing already exists as a
/// program, and a managed git library would cost the trimmed single-binary story
/// for a decoration most folders never need.
///
/// **ONE SUBPROCESS PER FOLDER OPEN — never per row.** That distinction is the
/// whole safety story here. A per-row subprocess is exactly what made a listing
/// of extensionless files take 44 seconds once, when `xdg-mime` was spawned for
/// every entry and parked the thread pool at 83 threads.
/// </summary>
public sealed class GitVersionControl : IVersionControl
{
    public string Name => "git";

    /// <summary>
    /// Probed once. A machine without git is not an error state — the feature
    /// simply does nothing — but silence would be indistinguishable from the
    /// setting being off, so it says so exactly once.
    /// </summary>
    public bool IsAvailable => _available ??= Probe();

    private static bool? _available;

    /// <summary>
    /// Drops the cached answer to "is git installed", so a test that puts its
    /// own git first on PATH is not answered from a probe that ran before it
    /// did. The probe runs once per process and is cached for good reason —
    /// it is a process spawn — and a test is the only caller that wants it
    /// twice.
    /// </summary>
    internal static void ForgetProbe() => _available = null;

    /// <summary>
    /// The program to run instead of whatever `git` PATH finds. Null in the
    /// application. A test that wants to see what git is HANDED puts a
    /// recording script here — on PATH would not do, because a bare "git" is
    /// resolved by CreateProcess as git.exe alone and walks straight past a
    /// git.cmd to the real one.
    /// </summary>
    internal static string? ExecutableOverride { get; set; }

    private static string Executable => ExecutableOverride ?? "git";

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,

                // See Ask, below. Redirecting the streams is not enough.
                CreateNoWindow = true,
            });

            if (process is null) return false;

            process.WaitForExit(3000);

            var ok = process.HasExited && process.ExitCode == 0;
            if (!ok) Console.Error.WriteLine("[vaktari] vcs: git present but did not answer --version");

            return ok;
        }
        catch
        {
            Console.Error.WriteLine("[vaktari] vcs: git not found — decorations disabled");
            return false;
        }
    }

    /// <summary>
    /// Walks up for a `.git` marker.
    ///
    /// **Tested with `Exists` on BOTH a directory and a file**: a submodule or a
    /// linked worktree has `.git` as a FILE containing a gitdir pointer, and
    /// checking only for a directory would silently skip exactly those.
    /// </summary>
    public string? FindRoot(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;

        try
        {
            var current = new DirectoryInfo(folder);

            while (current is not null)
            {
                var marker = Path.Combine(current.FullName, ".git");

                if (Directory.Exists(marker) || File.Exists(marker)) return current.FullName;

                current = current.Parent;
            }
        }
        catch
        {
            // An unreadable parent on the way up is not a repository as far as
            // anyone can tell from here.
        }

        return null;
    }

    public async Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        if (FindRoot(folder) is not { } root) return null;

        // --porcelain=v1  a stable, documented format; the human one is not.
        // -z              NUL separators and NO quoting. Without it git quotes
        //                 and C-escapes any path with a space, quote or
        //                 non-ASCII byte, and every such filename would need
        //                 unescaping — a whole class of bugs avoided by asking
        //                 for the machine format in the first place.
        // --ignored=no    ignored files are noise in a file manager; build
        //                 output would drown the listing.
        // -- <folder>     scope to what is on screen. A repo-wide status is one
        //                 call either way, but on a large tree it is a slow one,
        //                 and nothing off screen can be decorated.
        // **As a LIST, never as a quoted string.** This was one string with the
        // two paths in double quotes, and .NET splits that string into argv with
        // quote rules on every platform — so a folder whose name held a `"`
        // (legal on Linux) closed the quote early and the rest of the name
        // became git options. `-c core.fsmonitor=<command>` is honoured by
        // `git status`, which made browsing into a folder extracted from
        // somebody else's archive a way to run their command. The other
        // thirty-nine places this tree starts a process already used
        // ArgumentList; this was the one that did not.
        //
        // --literal-pathspecs, so a folder named `*` or `:` is a folder rather
        // than a glob or a magic pathspec.
        string output;

        try
        {
            var info = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,

                // **A black console window flashed on every folder listing.**
                // Vaktari is a GUI-subsystem binary and so owns no console;
                // git.exe is a console-subsystem one. Starting it from here
                // makes Windows allocate a NEW console for the child, and
                // allocating a console shows its window. Redirecting all three
                // streams does not prevent that -- redirection decides where
                // the handles point, not whether a console is created.
                //
                // CreateNoWindow passes CREATE_NO_WINDOW, which is what
                // actually suppresses it. Nothing on Linux, where the flag is
                // ignored and there was never a window to begin with, which is
                // why this survived the port unnoticed: the git probe runs at
                // startup and the status read runs on EVERY listing, so it
                // flashed on more or less every navigation.
                CreateNoWindow = true,
            };

            foreach (var arg in new[]
                     {
                         "--literal-pathspecs",
                         "-C", root,
                         "--no-optional-locks",
                         "status", "--porcelain=v1", "-z", "--ignored=no",
                         "--", folder,
                     })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);

            if (process is null) return null;

            // **Giving up on the wait does not stop the child.** Dispose
            // releases the handle and leaves git running, so the ten-second
            // timeout below — and the token, which fires the moment the user
            // navigates away — would each leak a git.exe. The listing that
            // takes too long is precisely the one people leave, so the two
            // paths that abandon the wait are the two that leak, once per
            // navigation, for as long as they keep trying.
            try
            {
                // Both streams concurrently. Reading one to completion first
                // deadlocks as soon as the child fills the buffer of the other —
                // this codebase has fixed that same bug five times.
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);

                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10), ct)
                    .ConfigureAwait(false);

                if (!process.WaitForExit(2000))
                {
                    Kill(process);
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    Console.Error.WriteLine(
                        $"[vaktari] vcs: git status exited {process.ExitCode} — {(await stderr).Trim()}");

                    return null;
                }

                output = await stdout.ConfigureAwait(false);
            }
            catch
            {
                Kill(process);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] vcs: git status failed — {ex.Message}");
            return null;
        }

        return new VcsSnapshot(root, Parse(output, root, folder));
    }

    /// <summary>
    /// Ends a git that is no longer being waited on. Never throws: it races
    /// with normal exit by definition, and a child that finished on its own
    /// between the check and the call is the good outcome, not an error worth
    /// reporting to anybody.
    /// </summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("vcs", ex);
        }
    }

    /// <summary>
    /// Turns porcelain v1 `-z` output into a state per entry of the listed
    /// folder.
    ///
    /// **Record shape:** two status characters, a space, then the path — and for
    /// a rename or copy, a SECOND NUL-terminated field holding the old path.
    /// Missing that second field desynchronises the whole parse, turning the old
    /// path into a status code, so it is consumed explicitly.
    ///
    /// **Roll-up:** paths are relative to the repository root and may be many
    /// levels below the folder on screen. Only the first segment under that
    /// folder is a row the user can see, so a change anywhere beneath it is
    /// attributed to that row, taking the strongest state — which is what the
    /// enum's ordering is for.
    /// </summary>
    private static Dictionary<string, VcsState> Parse(string output, string root, string folder)
    {
        var states = new Dictionary<string, VcsState>(StringComparer.Ordinal);
        var fields = output.Split('\0');
        var prefix = Path.GetFullPath(folder);

        for (var i = 0; i < fields.Length; i++)
        {
            var record = fields[i];

            // Trailing empty field after the final NUL, and any stray blank.
            if (record.Length < 4) continue;

            var x = record[0];
            var y = record[1];
            var relative = record[3..];

            // R and C carry their source path in the NEXT field. Consume it
            // here so the loop does not read it as a status record.
            if (x is 'R' or 'C') i++;

            var full = Path.GetFullPath(Path.Combine(root, relative));

            // Which visible row does this belong to? The entry itself when it
            // sits directly in the folder, otherwise the subdirectory that
            // contains it.
            if (!full.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = full[prefix.Length..].TrimStart(Path.DirectorySeparatorChar);
            if (rest.Length == 0) continue;

            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            var row = Path.Combine(prefix, separator < 0 ? rest : rest[..separator]);

            var state = StateOf(x, y);

            states[row] = states.TryGetValue(row, out var existing) && existing > state
                ? existing
                : state;
        }

        return states;
    }

    /// <summary>
    /// The two status columns are index and work tree. Conflicts are the pairs
    /// git documents as unmerged, and they are checked FIRST because several of
    /// them would otherwise read as an ordinary add or delete.
    /// </summary>
    private static VcsState StateOf(char x, char y)
    {
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            return VcsState.Conflicted;

        if (x == '?' && y == '?') return VcsState.Untracked;

        // The work tree wins over the index when they disagree: what is on disk
        // now is what the row is showing.
        foreach (var c in new[] { y, x })
        {
            switch (c)
            {
                case 'M' or 'T': return VcsState.Modified;
                case 'A': return VcsState.Added;
                case 'D': return VcsState.Deleted;
                case 'R' or 'C': return VcsState.Modified;
            }
        }

        return VcsState.Unmodified;
    }
}
