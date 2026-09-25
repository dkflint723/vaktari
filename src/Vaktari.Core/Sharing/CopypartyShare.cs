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

        SweepLeftovers();
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

        var file = Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}{ConfigExtension}");

        WritePrivate(file, config);

        return file;
    }

    private static void WritePrivate(string file, string text)
    {
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var writer = new StreamWriter(new FileStream(file, create));
        writer.Write(text);
    }

    private const string FilePrefix = "vaktari-share-";
    private const string ConfigExtension = ".conf";
    private const string RecordExtension = ".pid";

    /// <summary>The record that names a share's server and the Vaktari that
    /// started it, beside the config it was started with.</summary>
    internal static string RecordPathFor(string configPath) => Path.ChangeExtension(configPath, RecordExtension);

    /// <summary>
    /// The Vaktari a share's record names as its owner: this process, as a
    /// process id and its <see cref="StartMark"/>. Tests name one that is not
    /// running, which is what a crashed Vaktari looks like to the next one.
    /// </summary>
    internal (int Pid, long Started) Owner { get; init; } = (Environment.ProcessId, StartMark(Environment.ProcessId) ?? 0);

    /// <summary>
    /// When a process started, in a form another process can compare exactly:
    /// on Linux the kernel's own count of clock ticks since boot, and
    /// elsewhere the start time's UTC ticks. Null when the process is not
    /// there to ask.
    ///
    /// **On Linux the start time moved between processes.** .NET builds
    /// Process.StartTime as a boot time plus the kernel's count, and works the
    /// boot time out once per process from the wall clock — so a record
    /// written by one Vaktari and read by the next differed by every step the
    /// clock had taken in between. Past the two seconds of slack, a sweep after
    /// a resume that NTP corrected by five missed the orphan it was looking
    /// at, and then deleted the only record of it. The kernel's count is the
    /// number both processes read from; it does not move and needs no slack.
    /// Field 22 of /proc/[pid]/stat, counted after the last ')', because the
    /// name before it may hold spaces and parentheses of its own.
    /// </summary>
    internal static long? StartMark(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');

                // Field 3 (state) is the first after the name, so 22 is 19 on.
                return long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
            }

            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // Not running, or another account's — either way nothing to name.
            return null;
        }
    }

    /// <summary>
    /// Whether two <see cref="StartMark"/>s name the same start. Exact on
    /// Linux, where both are the kernel's count; two seconds of slack
    /// elsewhere, for a start time that passes through local time and back.
    /// </summary>
    private static bool SameStart(long recorded, long now)
        => OperatingSystem.IsLinux()
            ? recorded == now
            : Math.Abs(recorded - now) < TimeSpan.TicksPerSecond * 2;

    /// <summary>
    /// Writes down which process serves this config and which Vaktari started
    /// it, so a later start can tell an orphan from somebody's live share.
    ///
    /// **The config named no process,** so after a crash there was nothing to
    /// look for: the next start could not list the share, stop it or even say
    /// it was there. A process id alone is not enough either — ids come round
    /// again, and killing whatever holds one now is worse than the orphan —
    /// so each id goes down with the moment its process started, and only a
    /// process that matches both is taken to be the one meant.
    ///
    /// Written under another name and renamed into place, so a sweep in
    /// another copy of Vaktari never reads half a record and takes a live
    /// share for a broken one.
    /// </summary>
    private void Remember(string configPath, Process process)
    {
        try
        {
            var record = RecordPathFor(configPath);
            var staging = record + ".new";

            WritePrivate(staging,
                $"{process.Id}\n{StartMark(process.Id)}\n{Owner.Pid}\n{Owner.Started}\n");

            File.Move(staging, record, overwrite: true);
        }
        catch (Exception ex)
        {
            // Without a record the share still works; only a crash would
            // leave it for the next start to miss.
            Quiet.Swallowed("sharing", ex);
        }
    }

    /// <summary>
    /// Stops what a Vaktari that is no longer running left serving, and
    /// clears its files. Runs once, as the provider is built.
    ///
    /// **A share kept serving after Vaktari crashed or was killed,** and the
    /// next start had no way to find it: which servers were running lived
    /// only in memory. On Windows the job object in the backend ends the
    /// server with the process; on Linux nothing does, because the
    /// parent-death signal fires when the *thread* that forked exits, and
    /// the fork happens on a pool thread that retires while the share is
    /// still wanted. So every platform also looks here.
    ///
    /// **Another copy's live share is left alone.** A portable copy and an
    /// installed one each hold a lock of their own and can run at once, and
    /// the private folder is per user rather than per copy; a record whose
    /// owner is still running is somebody's share in use. A config with no
    /// record is either one being started this instant or one left from
    /// before records existed, so only an old one is cleared — it carries a
    /// password, and nothing will ever read it again.
    /// </summary>
    internal static void SweepLeftovers()
    {
        try
        {
            var directory = ConfigDirectory;

            if (!Directory.Exists(directory)) return;

            // **One file that could not be read stopped the whole sweep.** The
            // only catch was around the loop, and a record another copy
            // deleted between the listing and the read — its share stopped,
            // as they are — threw out of it and skipped every file after,
            // orphans and old passwords included, until the next start. Each
            // file now fails alone.
            foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "*").ToList())
            {
                try
                {
                    SweepFile(file);
                }
                catch (Exception ex)
                {
                    Quiet.Swallowed("sharing", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("sharing", ex);
        }
    }

    private static void SweepFile(string file)
    {
        var extension = Path.GetExtension(file);

        if (extension == RecordExtension)
            SweepRecord(file);
        else if (extension == ConfigExtension
                 && !File.Exists(RecordPathFor(file))
                 && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(1))
            Forget(file);
    }

    private static void SweepRecord(string record)
    {
        var lines = File.ReadAllLines(record);

        if (lines.Length >= 4
            && int.TryParse(lines[0], out var childPid)
            && long.TryParse(lines[1], out var childStarted)
            && int.TryParse(lines[2], out var ownerPid)
            && long.TryParse(lines[3], out var ownerStarted))
        {
            if (Find(ownerPid, ownerStarted) is { } owner)
            {
                owner.Dispose();
                return;
            }

            if (Find(childPid, childStarted) is { } orphan)
            {
                Diagnostics.Log.Warn("sharing", "stopped a share left serving by a Vaktari that is no longer running");
                Kill(orphan);
            }

            // **Forgotten only once nothing holds the id.** A server that
            // survived its kill, or a live process whose start did not match
            // the record's, is not one this can prove is gone — and when the
            // match was what went wrong, deleting the record lost the only
            // way anything would ever find a server still serving with its
            // password. A leftover record costs one small private file; the
            // next start looks again.
            if (StartMark(childPid) is not null) return;
        }

        // An unreadable record was never renamed into place by this code, so
        // no running copy is about to finish it.
        Forget(Path.ChangeExtension(record, ConfigExtension));
    }

    /// <summary>
    /// The running process with this id, if it is the one that started at
    /// the moment its <see cref="StartMark"/> says; an id that has come round
    /// to another process since is not the one meant.
    /// </summary>
    private static Process? Find(int pid, long started)
    {
        if (StartMark(pid) is not { } now || !SameStart(started, now)) return null;

        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            // Gone between the two questions.
            return null;
        }
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

        // Before anything else can go wrong: from here on the server is one
        // that dies with this process where the platform allows it, and one
        // the next start can find where it does not.
        _backend.Contain(process);
        Remember(configPath, process);

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

    /// <summary>Deletes a share's config and the record beside it.</summary>
    private static void Forget(string configPath)
    {
        foreach (var file in new[] { configPath, RecordPathFor(configPath) })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* a leftover temp file is not worth surfacing */ }
        }
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
