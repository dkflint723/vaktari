using System.Diagnostics;
using System.Text.Json;
using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Sharing;

/// <summary>
/// Public links through Proton Drive, by driving Proton's own CLI.
///
/// **The CLI is the only sane door.** Proton Drive is end-to-end encrypted:
/// every operation involves their key hierarchy, and their SDK — though it now
/// exists, in C# even — ships without third-party authentication and with a
/// cryptographic model change announced for the end of 2026 that will break
/// pre-change clients. The official CLI carries all of that itself: the user
/// signs in ONCE in their browser (`auth login`), the session lives in the
/// operating system's credential store, and Vaktari never sees a password —
/// it only ever runs the tool. The same reasoning as copyparty and git,
/// applied to cryptography instead of HTTP.
///
/// In Core rather than a platform assembly for the same reason as
/// <see cref="Vcs.GitVersionControl"/>: it drives a binary that behaves the
/// same on both targets; only WHERE the binary sits differs, and that is an
/// argument.
/// </summary>
public sealed class ProtonDriveLinks : ILinkSharing
{
    /// <summary>
    /// Where the user's Proton Drive sync folder is on this machine — the
    /// local half of every mapping. Set from settings; empty disables mapping,
    /// and with it the whole feature's menu presence.
    /// </summary>
    public string LocalRoot { get; set; } = "";

    private readonly Lazy<string?> _binary;

    public ProtonDriveLinks(string? binaryOverride = null)
        => _binary = new(() => binaryOverride ?? Locate());

    public bool IsAvailable => _binary.Value is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "the Proton Drive CLI is not installed — download it from proton.me/drive/download";

    /// <summary>
    /// PATH first — a deliberate install outranks a guess — then the places a
    /// downloaded single binary plausibly lands.
    /// </summary>
    private static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "proton-drive.exe" : "proton-drive";

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception e) when (e is ArgumentException or IOException) { }
        }

        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vaktari", "tools", name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "proton-drive", name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", name),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? MapToRemote(string localPath)
    {
        if (LocalRoot.Length == 0) return null;

        var root = PathRules.Normalise(LocalRoot);
        var full = PathRules.Normalise(localPath);

        if (PathRules.Same(root, full))
            return Grammar.RemoteRoot;

        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathRules.Comparison)) return null;

        // The drive speaks forward slashes whatever the platform does.
        var relative = full[prefix.Length..]
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return Grammar.RemoteRoot + "/" + relative;
    }

    public async Task<bool> IsSignedInAsync(CancellationToken ct)
    {
        var result = await RunAsync(Grammar.SignedInProbe, ct).ConfigureAwait(false);

        return Grammar.ReadSignedIn(result);
    }

    public async Task<DriveLink> CreateLinkAsync(string localPath, CancellationToken ct)
    {
        var remote = MapToRemote(localPath)
            ?? throw new IOException(
                "that is not inside the Proton Drive folder, so there is nothing there to link to");

        var result = await RunAsync(Grammar.CreateLink(remote), ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new IOException(Grammar.Complaint(result, "create the link"));

        var url = Grammar.ReadUrl(result)
            ?? throw new IOException("the CLI made the link but did not say where it is");

        return new DriveLink(PathRules.Normalise(localPath), remote, url);
    }

    public async Task RevokeAsync(DriveLink link, CancellationToken ct)
    {
        var result = await RunAsync(Grammar.RevokeLink(link.RemotePath), ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new IOException(Grammar.Complaint(result, "remove the link"));
    }

    // ---- the tool itself ---------------------------------------------------

    public readonly record struct CliResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Stands in for the binary. The states worth testing — signed out, a link
    /// created, a refusal — cannot be arranged on a build machine that has no
    /// Proton account, and must never depend on one.
    /// </summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliResult>>? RunOverride
    { get; set; }

    private async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (RunOverride is { } fake) return await fake(arguments, ct).ConfigureAwait(false);

        var binary = _binary.Value
            ?? throw new IOException(UnavailableReason ?? "no CLI");

        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new IOException("the Proton Drive CLI would not start");

        // Both concurrently — a sequential drain deadlocks if the child fills
        // the pipe we are not reading yet. Same shape as LinuxRemoteMounts.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Every literal the CLI is spoken to with, in one place.
    ///
    /// **Three of these are documented and two are conjecture**, and the
    /// distinction is marked on each. Until the conjectured ones are pinned
    /// against the real binary's own --help, this feature must not be
    /// described as working — the seam exists precisely so the pinning
    /// changes five strings and nothing else.
    /// </summary>
    internal static class Grammar
    {
        /// <summary>The drive's own name for the root, per the CLI docs'
        /// examples ("/my-files/Documents").</summary>
        internal const string RemoteRoot = "/my-files";

        /// <summary>DOCUMENTED: "sharing set-url" creates the public link;
        /// --json is the CLI's machine-readable switch.</summary>
        internal static string[] CreateLink(string remote)
            => ["sharing", "set-url", remote, "--json"];

        /// <summary>CONJECTURE — pin against `sharing --help` before trusting.
        /// The announcement names set-url and invite; the removal verb is not
        /// in any public text found.</summary>
        internal static string[] RevokeLink(string remote)
            => ["sharing", "delete-url", remote, "--json"];

        /// <summary>CONJECTURE — pin against `auth --help` before trusting.</summary>
        internal static string[] SignedInProbe
            => ["auth", "status", "--json"];

        /// <summary>
        /// The URL out of a create's output: the --json object's "url", with a
        /// bare https line as the fallback for a tool that prints plainly.
        /// </summary>
        internal static string? ReadUrl(CliResult result)
        {
            foreach (var text in new[] { result.StdOut, result.StdErr })
            {
                if (TryJsonString(text, "url") is { } fromJson) return fromJson;

                foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
                    if (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return line;
            }

            return null;
        }

        internal static bool ReadSignedIn(CliResult result)
            => result.ExitCode == 0;

        /// <summary>The tool's own sentence when it has one, tidied of its
        /// name — the person just clicked a menu, not a terminal.</summary>
        internal static string Complaint(CliResult result, string doing)
        {
            var line = (result.StdErr + "\n" + result.StdOut)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !l.StartsWith('{'));

            return string.IsNullOrEmpty(line)
                ? $"could not {doing} (exit {result.ExitCode})"
                : line;
        }

        private static string? TryJsonString(string text, string property)
        {
            var start = text.IndexOf('{');
            if (start < 0) return null;

            try
            {
                using var parsed = JsonDocument.Parse(text[start..]);

                return parsed.RootElement.TryGetProperty(property, out var value)
                       && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
