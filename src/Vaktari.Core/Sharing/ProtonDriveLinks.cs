using System.Diagnostics;
using System.Net.Http;
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

    private readonly string? _binaryOverride;
    private string? _binary;
    private bool _located;

    public ProtonDriveLinks(string? binaryOverride = null)
        => _binaryOverride = binaryOverride;

    /// <summary>
    /// Found once and remembered — but re-scannable, unlike the Lazy this
    /// replaced, whose first "not installed" answer stood for the whole run
    /// and made every install need a restart to notice.
    /// </summary>
    /// <summary>Stands in for discovery in tests, which must not depend on
    /// what the machine running them happens to have installed.</summary>
    internal Func<string?>? LocateOverride { get; init; }

    private string? Binary
    {
        get
        {
            if (!_located)
            {
                _binary = _binaryOverride ?? (LocateOverride ?? Locate)();
                _located = true;
            }

            return _binary;
        }
    }

    /// <summary>Re-runs discovery, so an install — Vaktari's own or a copy
    /// dropped in by hand — takes effect without a restart. Same contract as
    /// CopypartyShare's rescan.</summary>
    public void Rescan() => _located = false;

    public bool IsAvailable => Binary is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "the Proton Drive CLI is not installed — the Share menu offers to install it";

    private static string BinaryName
        => OperatingSystem.IsWindows() ? "proton-drive.exe" : "proton-drive";

    /// <summary>Where Vaktari's own install lands. Overridable so the install
    /// tests write into a temp folder instead of the real one.</summary>
    internal string? ToolsDirOverride { get; init; }

    private string ToolsDir => ToolsDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "vaktari", "tools");

    /// <summary>
    /// PATH first — a deliberate install outranks a guess — then the places a
    /// downloaded single binary plausibly lands.
    /// </summary>
    private string? Locate()
    {
        var name = BinaryName;

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
            Path.Combine(ToolsDir, name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "proton-drive", name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", name),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Stands in for the download in tests, which must never touch
    /// the network. Takes the URL and the file to write.</summary>
    internal Func<string, string, CancellationToken, Task>? FetchOverride { get; set; }

    /// <summary>
    /// Downloads the CLI into Vaktari's tools folder — the same "install is a
    /// menu click" promise copyparty makes, kept the way a single static
    /// binary allows: fetch, stage, rename into place.
    ///
    /// Staged under a .part name and renamed at the end, so a transfer that
    /// dies halfway leaves nothing that <see cref="Locate"/> would mistake
    /// for a working tool. The binary is ~120 MB, hence streamed to disk and
    /// never held in memory.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (IsAvailable) return true;

        if (Grammar.DownloadUrl() is not { } url)
        {
            progress.Report("Proton does not publish its CLI for this platform");
            return false;
        }

        progress.Report("downloading the Proton Drive CLI…");

        var final = Path.Combine(ToolsDir, BinaryName);
        var staged = final + ".part";

        try
        {
            Directory.CreateDirectory(ToolsDir);

            await (FetchOverride ?? FetchAsync)(url, staged, ct).ConfigureAwait(false);

            // The download is not a program until it can run.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            File.Move(staged, final, overwrite: true);
            Rescan();

            progress.Report("Proton Drive sharing is ready");
            return IsAvailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Report($"could not install the Proton Drive CLI: {ex.Message}");

            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (Exception e) { Quiet.Swallowed("proton", e); }

            return false;
        }
    }

    private static async Task FetchAsync(string url, string destination, CancellationToken ct)
    {
        // Same shape and the same patience as the icon-theme fetch: large
        // file, unknowable connection, and a stall should not hang forever.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vaktari");

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);

        await network.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the Proton Drive app most likely syncs to, for when the setting
    /// is empty — the app puts "My files" either directly under
    /// "~/Proton Drive" or one account folder down.
    ///
    /// A guess is offered only when it is unambiguous: with two account
    /// folders there is no right answer, and a wrong drive mapping would make
    /// links to the wrong account's files. Null sends the person to the
    /// setting, which always wins over this.
    /// </summary>
    public static string? GuessLocalRoot()
        => GuessLocalRoot(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Proton Drive"));

    internal static string? GuessLocalRoot(string protonBase)
    {
        try
        {
            if (!Directory.Exists(protonBase)) return null;

            var direct = Path.Combine(protonBase, "My files");
            if (Directory.Exists(direct)) return direct;

            var accounts = Directory.EnumerateDirectories(protonBase)
                .Where(account => Directory.Exists(Path.Combine(account, "My files")))
                .Take(2)
                .ToList();

            return accounts.Count == 1 ? Path.Combine(accounts[0], "My files") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    public async Task<bool> SignInAsync(Action<string> openUrl, CancellationToken ct)
    {
        // Streamed rather than buffered: the process does not exit until the
        // person has finished in the browser, and the link they need is
        // somewhere in the middle of that output.
        var exit = await RunStreamingAsync(
            Grammar.SignIn,
            line =>
            {
                if (Grammar.ExtractUrl(line) is { } url) openUrl(url);
            },
            ct).ConfigureAwait(false);

        return exit == 0;
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

    /// <summary>Whether a refusal reads as "not signed in" — the one failure
    /// the caller can fix by running <see cref="SignInAsync"/> and retrying.</summary>
    public static bool LooksSignedOut(string complaint) => Grammar.LooksSignedOut(complaint);

    // ---- the tool itself ---------------------------------------------------

    public readonly record struct CliResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Stands in for the binary. The states worth testing — signed out, a link
    /// created, a refusal — cannot be arranged on a build machine that has no
    /// Proton account, and must never depend on one.
    /// </summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliResult>>? RunOverride
    { get; set; }

    /// <summary>The streamed twin, for the sign-in flow's tests.</summary>
    internal Func<IReadOnlyList<string>, Action<string>, CancellationToken, Task<int>>?
        StreamOverride { get; set; }

    private async Task<int> RunStreamingAsync(
        IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
    {
        if (StreamOverride is { } fake)
            return await fake(arguments, onLine, ct).ConfigureAwait(false);

        var binary = Binary ?? throw new IOException(UnavailableReason ?? "no CLI");

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

        // A kill on cancel, or an abandoned sign-in leaves a child waiting on a
        // browser nobody is coming back to.
        using var cancellation = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception e) { Quiet.Swallowed("proton", e); }
        });

        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                onLine(line);
        }

        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        return process.ExitCode;
    }

    private async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (RunOverride is { } fake) return await fake(arguments, ct).ConfigureAwait(false);

        var binary = Binary
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

        /// <summary>
        /// Pinned, not "latest": a version that was tested is a version that
        /// keeps working, and Proton's download paths carry the number.
        /// </summary>
        internal const string CliVersion = "0.8.0";

        /// <summary>
        /// VERIFIED for both platforms — each URL answered 200 with the
        /// binary's length on 2026-08-29. Null where Proton publishes no CLI.
        /// </summary>
        internal static string? DownloadUrl()
            => OperatingSystem.IsWindows()
                ? $"https://proton.me/download/drive/cli/{CliVersion}/windows-x64/proton-drive.exe"
                : OperatingSystem.IsLinux()
                    ? $"https://proton.me/download/drive/cli/{CliVersion}/linux-x64/proton-drive"
                    : null;

        /// <summary>DOCUMENTED: "sharing set-url" creates the public link;
        /// --json is the CLI's machine-readable switch.</summary>
        internal static string[] CreateLink(string remote)
            => ["sharing", "set-url", remote, "--json"];

        /// <summary>CONJECTURE — pin against `sharing --help` before trusting.
        /// The announcement names set-url and invite; the removal verb is not
        /// in any public text found.</summary>
        internal static string[] RevokeLink(string remote)
            => ["sharing", "delete-url", remote, "--json"];

        /// <summary>DOCUMENTED: "auth login" runs the browser sign-in and
        /// exits when it completes.</summary>
        internal static string[] SignIn => ["auth", "login"];

        /// <summary>
        /// A URL out of one line of sign-in chatter. The tool may open the
        /// browser itself, print a link, or both; a line is a link when it
        /// carries one, whatever else it says around it.
        /// </summary>
        internal static string? ExtractUrl(string line)
        {
            var at = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            var url = line[at..];
            var end = url.IndexOfAny([' ', '\t', '"', '\'']);

            return end > 0 ? url[..end] : url;
        }

        /// <summary>
        /// Whether a refusal reads as "you are not signed in" — the one
        /// failure Vaktari can fix by itself, by running the sign-in and
        /// retrying. Matched loosely on purpose: the exact sentence is the
        /// tool's to change, and a miss only means the user sees the message
        /// instead of the browser.
        /// </summary>
        internal static bool LooksSignedOut(string complaint)
            => complaint.Contains("sign", StringComparison.OrdinalIgnoreCase)
               || complaint.Contains("log", StringComparison.OrdinalIgnoreCase)
               || complaint.Contains("auth", StringComparison.OrdinalIgnoreCase)
               || complaint.Contains("session", StringComparison.OrdinalIgnoreCase);

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
