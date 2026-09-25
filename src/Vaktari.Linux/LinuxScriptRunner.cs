using System.Diagnostics;
using System.Text;
using Vaktari.Core;

namespace Vaktari.Linux;

public sealed class LinuxScriptRunner : IScriptRunner
{
    /// <summary>
    /// The scripts folder under the user's data directory, or under
    /// <paramref name="portableRoot"/> for a portable copy.
    ///
    /// **A portable copy made its scripts folder on every machine it ran on**,
    /// and went looking there for the old names' folders to move — writing
    /// outside the one folder it promises to stay in, and finding none of the
    /// scripts it carried. The Windows runner was already built from the state
    /// directory, which is the portable folder when there is one; this is the
    /// same answer, given only to a portable copy so an installed one keeps
    /// the ~/.local/share folder its scripts have always been in.
    /// </summary>
    public LinuxScriptRunner(string? portableRoot = null)
    {
        if (portableRoot is not null)
        {
            ScriptsDirectory = Path.Combine(portableRoot, "scripts");
            EnsureDirectory();
            return;
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");

        ScriptsDirectory = Path.Combine(dataHome, "vaktari", "scripts");

        // Carried over from the old names, once, so scripts written before a
        // rename keep working without being moved by hand.
        //
        // **Both renames, newest first.** This tried rove and not heimdall,
        // which meant the sweep that renamed the project would have silently
        // orphaned every script anybody had written under the previous name —
        // and scripts are user-authored content, not application state.
        Vaktari.Core.PreviousName.Adopt(
            Path.Combine(dataHome, "vaktari"), Path.Combine(dataHome, "heimdall"));
        Vaktari.Core.PreviousName.Adopt(
            Path.Combine(dataHome, "vaktari"), Path.Combine(dataHome, "rove"));
        EnsureDirectory();
    }

    public string ScriptsDirectory { get; }

    /// <summary>
    /// Created eagerly with a README, because a feature whose entire interface
    /// is "put a file in a folder" is invisible until that folder exists.
    /// </summary>
    private void EnsureDirectory()
    {
        try
        {
            if (Directory.Exists(ScriptsDirectory)) return;

            Directory.CreateDirectory(ScriptsDirectory);

            File.WriteAllText(Path.Combine(ScriptsDirectory, "README"),
                """
                Any executable file in this folder appears in Vaktari's context menu.

                It is run with the selected paths as arguments, and with the
                folder you are looking at as its working directory. Anything it
                prints is shown in the status bar.

                  VAKTARI_CWD       the folder being listed
                  VAKTARI_SELECTED  number of selected items

                Make a script executable with: chmod +x <file>
                The menu entry is the filename, underscores shown as spaces.
                """);
        }
        catch
        {
            // A read-only home is not a reason to fail startup.
        }
    }

    public IReadOnlyList<ScriptCommand> Discover()
    {
        try
        {
            if (!Directory.Exists(ScriptsDirectory)) return [];

            return Directory.EnumerateFiles(ScriptsDirectory)
                .Where(IsExecutable)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new ScriptCommand(
                    Path.GetFileNameWithoutExtension(path).Replace('_', ' '),
                    path))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The execute bit is the opt-in — a half-written script sitting in
    /// the folder should not appear in a menu until it is meant to run.</summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);

            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<string> RunAsync(
        ScriptCommand script,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo(script.Path)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var path in paths) info.ArgumentList.Add(path);

        info.Environment["VAKTARI_CWD"] = workingDirectory;
        info.Environment["VAKTARI_SELECTED"] = paths.Count.ToString();

        // **The old names, still set, because scripts are the user's code.**
        //
        // The rename swept these along with everything else, which would have
        // broken every script anybody had already written — silently, since a
        // shell reading an unset variable gets an empty string and carries on.
        // A script that did `cd "$HEIMDALL_CWD"` would have run in the wrong
        // directory rather than failed.
        //
        // Deprecated, not supported: the documented names are the VAKTARI_ ones
        // and these exist so nothing breaks on the day of the rename.
        info.Environment["HEIMDALL_CWD"] = workingDirectory;
        info.Environment["HEIMDALL_SELECTED"] = paths.Count.ToString();

        // The old names are still set. Scripts are the user's own code living
        // outside this repo, and a rename here should not silently break them.
        info.Environment["ROVE_CWD"] = workingDirectory;
        info.Environment["ROVE_SELECTED"] = paths.Count.ToString();

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {script.Name}.");

        // Disposing a Process does not stop the child. Without this, cancelling
        // left the user's script running with nothing showing it and no way to
        // reach it from the application.
        using var cancellation = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("scripts", ex); }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var error = (await stderr.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var message = new StringBuilder($"{script.Name} exited {process.ExitCode}");
            if (error.Length > 0) message.Append(": ").Append(FirstLine(error));
            return message.ToString();
        }

        return output.Length > 0 ? FirstLine(output) : $"{script.Name} finished";
    }

    /// <summary>The status bar is one line; the rest would be truncated anyway.</summary>
    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
