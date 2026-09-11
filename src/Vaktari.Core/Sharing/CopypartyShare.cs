using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Sharing;

/// <summary>
/// Serves folders by driving copyparty, which is launched as an ordinary child
/// process and talked to over its command line — no library, no embedding.
///
/// The reason for a process rather than an implementation: copyparty is Python,
/// Vaktari is trimmed NativeAOT C#. Embedding a Python runtime would cost the
/// single-binary story and the startup time for a feature most sessions never
/// use. A subprocess costs nothing until it runs, and the two can be upgraded
/// independently.
///
/// Same reasoning as the scripts menu: the useful thing already exists as a
/// program, so run the program.
///
/// **In Core rather than per-platform**, which is a change from where this
/// started. IFileSharing calls itself platform-specific "because locating and
/// launching a server differs by OS" — locating does, launching does not, and
/// everything between writing the config and reaping the process was one copy
/// of code that would have become two the moment Windows wanted it. What
/// genuinely differs now lives behind <see cref="CopypartyBackend"/>, which is
/// about sixty lines a platform against four hundred here.
/// </summary>
public sealed class CopypartyShare : IFileSharing
{
    private sealed record Running(Process Process, ShareSession Session, string ConfigPath);

    private readonly ConcurrentDictionary<Guid, Running> _running = new();
    private readonly CopypartyBackend _backend;

    private string? _command;
    private string[] _prefixArgs;

    public CopypartyShare(CopypartyBackend backend)
    {
        _backend = backend;
        (_command, _prefixArgs) = _backend.Locate();
    }

    /// <summary>Re-runs discovery, so an install takes effect without a restart.</summary>
    private void Rescan()
    {
        (_command, _prefixArgs) = _backend.Locate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAvailable => _command is not null;

    public string? UnavailableReason => IsAvailable ? null : _backend.NotInstalledHint;

    public IReadOnlyList<ShareSession> Active =>
        _running.Values.Select(r => r.Session).ToList();

    public event EventHandler? Changed;

    /// <summary>
    /// Installs copyparty, trying what the platform offers in the order it
    /// offered it — least disturbing to the system first.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (IsAvailable) return true;

        var attempts = _backend.InstallAttempts();

        if (attempts.Count == 0)
        {
            progress.Report(_backend.NoInstallerHint);
            return false;
        }

        for (var i = 0; i < attempts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var attempt = attempts[i];
            progress.Report($"running {attempt.Describe}…");

            var (code, output) = await Task.Run(() => Capture(attempt.File, attempt.Args, ct), ct)
                                           .ConfigureAwait(false);

            if (code == 0)
            {
                Rescan();

                if (IsAvailable)
                {
                    progress.Report("copyparty installed");
                    return true;
                }

                progress.Report(_backend.InstalledButNotFoundHint);
                return false;
            }

            // Quiet when the next attempt is the targeted answer to THIS
            // failure, noisy when it is another guess. Every attempt runs
            // either way; this only chooses whether to narrate.
            if (i < attempts.Count - 1 && !_backend.NextAttemptAddresses(output))
                progress.Report($"{attempt.Describe} failed, trying another way…");
        }

        progress.Report(_backend.InstallFailedHint);
        return false;
    }

    private static (int Code, string Output) Capture(string file, string[] args, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,

                // Windows would otherwise allocate a console for the child and
                // show its window: this process is GUI-subsystem and owns no
                // console to lend. Same reason GitVersionControl sets it.
                CreateNoWindow = true,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return (-1, "");

            // ct was accepted and then never used, so cancelling an install did
            // nothing at all. Killing the tree is the only thing that actually
            // stops a pip download part-way.
            using var cancellation = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("sharing", ex); }
            });

            // Concurrently, not one after the other. Draining stdout to
            // completion and only then stderr deadlocks the moment the child
            // fills the stderr buffer while we are still blocked on stdout —
            // and the WaitForExit timeout below never applies, because
            // ReadToEnd has no timeout of its own.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            // Generous: a cold pip install of a package this size can take a
            // while on a slow connection.
            if (!process.WaitForExit(300_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("sharing", ex); }
                return (-1, "timed out");
            }

            var output = Task.WaitAll(new Task[] { stdout, stderr }, 5_000)
                ? stdout.Result + stderr.Result
                : "";

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Where the config files go. Null is the session's private
    /// folder; tests point it at a folder of their own.</summary>
    internal static string? ConfigDirectoryOverride { get; set; }

    /// <summary>
    /// **Not /tmp.** The config carries the share's password now, and /tmp is
    /// every account's; the session's private folder is the one the instance
    /// lock lives in.
    /// </summary>
    internal static string ConfigDirectory => ConfigDirectoryOverride ?? PrivateDirectory.Runtime();

    /// <summary>The account every share is served to. Cosmetic to copyparty
    /// — the password is the identity — but it is the name the login box
    /// shows, and one fixed name is easier to say than a generated one.</summary>
    internal const string Account = "vaktari";

    /// <summary>
    /// A password for one share: sixteen characters from the system's random
    /// source, out of an alphabet with no look-alikes — no 0 and O, no 1, l
    /// and I — because it is read off one screen and typed on another.
    /// </summary>
    internal static string NewPassword()
        => System.Security.Cryptography.RandomNumberGenerator.GetString(
            "abcdefghjkmnpqrstuvwxyz23456789", 16);

    /// <summary>The address to hand someone. The password rides along as the
    /// query copyparty reads it from, which also logs the browser in.</summary>
    internal static string UrlFor(string address, int port, string password)
        => $"http://{address}:{port}/?pw={password}";

    /// <summary>
    /// The server config for one share.
    ///
    /// **Anyone who could reach the port could read the folder, the port was
    /// on every interface, and the share announced itself.** The volume
    /// granted `r: *` — or `rw: *` — to the world; no `i:` meant copyparty
    /// bound every address the machine had, a VPN's or a public one's
    /// included; and `z` put the share by name into every mDNS-aware file
    /// manager and Windows Explorer on the network. A folder handed to one
    /// person by its address was offered to the building. Now one account
    /// with a password of its own is the only reader, the server listens on
    /// the one address the URL names, and announcing is the option it should
    /// always have been.
    ///
    /// A config file rather than <c>-v src:dst:perm</c> on the command line,
    /// for two reasons. That syntax is colon-separated, so a folder named
    /// "notes:2026" would parse into something other than what the user picked
    /// — a path is not safe to interpolate into it. And the file states the
    /// whole policy in one place where it can be read back, rather than as a
    /// string of flags whose interaction has to be reasoned about.
    ///
    /// The path goes in with forward slashes on every platform. copyparty is
    /// Python and takes either on Windows, and a backslash is the one character
    /// whose meaning inside a config value is worth not having to reason about
    /// — `C:\temp\new` carries a `\n` to anything that unescapes.
    /// </summary>
    internal static string Config(string path, int port, ShareOptions options, string address, string password)
    {
        var served = path.Replace('\\', '/');

        var global = new List<string> { $"p: {port}", $"i: {address}", "no-thumb", "no-robots", "q" };

        if (options.Announce) global.Add("z");

        return $"""
            # generated by Vaktari; deleted when the share stops

            [global]
              {string.Join("\n  ", global)}

            [accounts]
              {Account}: {password}

            [/]
              {served}
              accs:
                {(options.Writable ? "rw" : "r")}: {Account}
              flags:
                # Nothing outside this folder, ever. xvol ignores symlinks that
                # leave the volume's top directory and xdev refuses to cross
                # into another filesystem; since copyparty 1.7.0 both block
                # access at request time, not just during indexing. Without
                # these, one symlink inside a shared folder exposes wherever it
                # points.
                xvol
                xdev

                # No index database, so an ad-hoc share leaves no .hist folder
                # behind in the user's directory.
                no_idx: .

            """;
    }

    /// <summary>
    /// Writes a config where only this user can read it: created with the
    /// mode rather than chmod'ed after, so there is no moment in which the
    /// password is anybody else's to read. Windows ignores the mode; its
    /// per-user temp folder is the private folder there.
    /// </summary>
    private static string WriteConfig(string config)
    {
        var directory = ConfigDirectory;
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, $"vaktari-share-{Guid.NewGuid():N}.conf");

        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var writer = new StreamWriter(new FileStream(file, create));
        writer.Write(config);

        return file;
    }

    /// <summary>Stands in for <see cref="Process.Start(ProcessStartInfo)"/>
    /// in tests, which read the config the server would have been handed and
    /// start something harmless in its place. Null in production.</summary>
    internal Func<ProcessStartInfo, Process?>? LaunchOverride { get; set; }

    public async Task<ShareSession> StartAsync(string path, ShareOptions options, CancellationToken ct)
    {
        if (_command is null)
            throw new InvalidOperationException(UnavailableReason);

        path = PathRules.Normalise(Path.GetFullPath(path));

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);

        // PathRules rather than a test for "/", which was true on Linux and
        // never true of "C:\". Sharing a whole drive is the same mistake as
        // sharing a whole filesystem.
        if (path.Length == 0 || PathRules.IsRoot(path))
            throw new InvalidOperationException("refusing to share the whole filesystem");

        // What the platform has to say about the network, first and off the
        // caller's thread: on Windows it runs a command, and the caller is a
        // click.
        var warning = await Task.Run(() => _backend.StartWarning(), ct).ConfigureAwait(false);

        var port = FreePort();
        var address = LocalAddress();
        var password = NewPassword();
        var configPath = WriteConfig(Config(path, port, options, address, password));

        var info = new ProcessStartInfo(_command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Deliberately NOT the shared folder: copyparty with no volume
            // defined serves its working directory read-write, so pointing it
            // at the share would make a config failure quietly permissive.
            // Temp is empty and boring.
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach (var arg in _prefixArgs) info.ArgumentList.Add(arg);

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(configPath);

        var launch = LaunchOverride ?? (start => Process.Start(start));

        var process = launch(info)
                      ?? throw new InvalidOperationException("could not start copyparty");

        var session = new ShareSession
        {
            Path = path,
            Url = UrlFor(address, port, password),
            Port = port,
            Writable = options.Writable,
            Password = password,
            Warning = warning,
            Handle = Guid.NewGuid(),
        };

        _running[(Guid)session.Handle] = new Running(process, session, configPath);

        // A server that dies silently would leave a dead entry in the UI
        // promising a URL that answers nothing.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _running.TryRemove((Guid)session.Handle, out _);
            Forget(configPath);
            Changed?.Invoke(this, EventArgs.Empty);
        };

        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    private static void Forget(string configPath)
    {
        try { if (File.Exists(configPath)) File.Delete(configPath); }
        catch { /* a leftover temp file is not worth surfacing */ }
    }

    public Task StopAsync(ShareSession session)
    {
        if (session.Handle is Guid id && _running.TryRemove(id, out var running))
        {
            Kill(running.Process);
            Forget(running.ConfigPath);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        foreach (var id in _running.Keys.ToList())
        {
            if (!_running.TryRemove(id, out var running)) continue;

            Kill(running.Process);
            Forget(running.ConfigPath);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Already gone, or not ours to kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Asks the OS for a free port rather than guessing one.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// The address another machine can actually reach — and, now, the one the
    /// server is bound to. Loopback would be useless in a share URL, which is
    /// the whole point of one.
    /// </summary>
    private static string LocalAddress()
    {
        try
        {
            var candidates = new List<(string Address, bool HasGateway)>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();

                foreach (var address in properties.UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        candidates.Add((address.Address.ToString(), properties.GatewayAddresses.Count > 0));
            }

            return ChooseAddress(candidates);
        }
        catch
        {
            // Fall through to loopback; at least the local machine works.
            return "127.0.0.1";
        }
    }

    /// <summary>
    /// **The first adapter with an address was the one named, and is now the
    /// one bound.** Measured on a machine with VMware and WSL installed: both
    /// virtual adapters are Up and hold an address, and neither has a
    /// gateway; the adapter the LAN is on has one. Whichever order the
    /// runtime lists them in, the one with a route out is the one other
    /// machines can reach — and with the server bound to a single address,
    /// the wrong pick is no longer merely the wrong label but the wrong door.
    /// </summary>
    internal static string ChooseAddress(IEnumerable<(string Address, bool HasGateway)> candidates)
    {
        string? first = null;

        foreach (var (address, hasGateway) in candidates)
        {
            if (hasGateway) return address;

            first ??= address;
        }

        return first ?? "127.0.0.1";
    }
}
