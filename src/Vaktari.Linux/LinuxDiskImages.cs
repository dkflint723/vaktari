using System.ComponentModel;
using System.Diagnostics;
using Vaktari.Core;
using Vaktari.Core.Places;

namespace Vaktari.Linux;

/// <summary>
/// Mounting an .iso by handing it to udisks2 as a loop device.
///
/// **No privileges of Vaktari's own, and in the ordinary case no prompt.** The
/// root work happens inside the udisks2 daemon, which applies polkit per call;
/// the shipped rules give loop-setup and filesystem-mount to an active local
/// session, and loop-delete of a device the same user created. losetup and
/// `mount -o loop` are rejected precisely because they need real root.
///
/// Driven as a child process for the same reason as the ejector beside it:
/// speaking D-Bus by hand is hundreds of lines of protocol to reach a daemon
/// that ships a supported command line.
/// </summary>
internal sealed class LinuxDiskImages : IDiskImages
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliResult>>? RunOverride { get; init; }
    internal Func<string, bool>? HaveToolOverride { get; init; }
    internal Func<IEnumerable<string>>? MountLines { get; init; }

    internal readonly record struct CliResult(int ExitCode, string StdOut, string StdErr);

    public bool IsAvailable => HaveTool("udisksctl");

    public string? UnavailableReason => IsAvailable
        ? null
        : "udisks2 is not installed, so this desktop has no way to mount a disk image";

    /// <summary>
    /// What the loop driver can actually present.
    ///
    /// .iso and raw images only. Not .vhd or .vhdx — those are container
    /// formats with no kernel driver, so only a fixed-size VHD would mount, and
    /// then by luck. Not .qcow2, which needs qemu-nbd and a root-only device.
    /// Not .dmg, which is usually compressed UDIF the kernel cannot read at all.
    /// Every one of those would be a menu entry that fails on most of the files
    /// it appears for.
    /// </summary>
    private static readonly HashSet<string> Mountable =
        new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".raw" };

    public bool CanMount(string path)
        => !string.IsNullOrEmpty(path)
           && Mountable.Contains(Path.GetExtension(path))
           && !Directory.Exists(path);

    /// <summary>What file a loop device is backed by. Seam for tests, which
    /// have no /sys to read.</summary>
    internal Func<string, string?>? BackingFileOf { get; init; }

    /// <summary>
    /// Where this image is mounted — **asked of the kernel, not remembered.**
    ///
    /// A loop device survives Vaktari, so a process-local map says "not
    /// mounted" about an image that plainly is after any restart, and acting on
    /// that answer attaches the same file a second time. The kernel already
    /// records the answer: every loop device names its backing file under
    /// /sys/block/loopN/loop/backing_file, so the mount table plus that file is
    /// the whole lookup, and it sees images mounted by anything.
    /// </summary>
    public MountedImage? MountOf(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);

        foreach (var line in MountLines is { } fake ? fake() : ReadMountLines())
        {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;

            var source = MountTable.Unescape(parts[0]);

            if (!source.StartsWith("/dev/loop", StringComparison.Ordinal)) continue;

            var backing = BackingFileOf is { } ask ? ask(source) : ReadBackingFile(source);

            if (backing is null) continue;

            if (string.Equals(backing, full, StringComparison.Ordinal))
                return new MountedImage(full, MountTable.Unescape(parts[1]));
        }

        return null;
    }

    /// <summary>
    /// The file behind /dev/loopN, from the kernel's own record.
    ///
    /// A deleted backing file is reported with a " (deleted)" suffix, which is
    /// not part of the path and would stop a perfectly ordinary image matching
    /// itself.
    /// </summary>
    internal static string? ReadBackingFile(string loopDevice)
    {
        try
        {
            var name = Path.GetFileName(loopDevice);
            var file = $"/sys/block/{name}/loop/backing_file";

            return File.Exists(file) ? CleanBackingFile(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path out of what /sys reports — separated from the read so it can be
    /// tested on a machine with no /sys, which is every machine this project is
    /// developed on.
    /// </summary>
    internal static string? CleanBackingFile(string raw)
    {
        var backing = raw.Trim();

        const string deleted = " (deleted)";

        if (backing.EndsWith(deleted, StringComparison.Ordinal))
            backing = backing[..^deleted.Length];

        return backing.Length > 0 ? backing : null;
    }

    public async Task<MountedImage> MountAsync(string imagePath, CancellationToken ct)
    {
        var full = Path.GetFullPath(imagePath);

        if (!File.Exists(full)) throw new FileNotFoundException("that image is not there", full);

        if (!IsAvailable) throw new IOException(UnavailableReason);

        var setup = await RunAsync(["loop-setup", "--no-user-interaction", "-r", "-f", full], ct)
            .ConfigureAwait(false);

        if (setup.ExitCode != 0) throw new IOException(Tidy(setup.StdErr));

        var loop = LoopDeviceIn(setup.StdOut)
            ?? throw new IOException("udisks2 attached the image but did not say where");

        var mount = await RunAsync(["mount", "--no-user-interaction", "-b", loop], ct)
            .ConfigureAwait(false);

        // Already mounted is a success: udisks2 auto-mounts on some desktops,
        // and racing its own automount must not read as a failure.
        var mountPath = MountPointIn(mount.StdOut)
            ?? MountOf(full)?.MountPath;

        if (mount.ExitCode != 0 && mountPath is null)
        {
            // Leave nothing attached behind a failure: an orphaned loop device
            // is invisible and outlives the application.
            await RunAsync(["loop-delete", "--no-user-interaction", "-b", loop], ct)
                .ConfigureAwait(false);

            throw new IOException(Tidy(mount.StdErr));
        }

        return new MountedImage(full, mountPath
            ?? throw new IOException("the image mounted but udisks2 did not say where"));
    }

    public async Task UnmountAsync(string imagePath, CancellationToken ct)
    {
        var full = Path.GetFullPath(imagePath);

        // Found the same way MountOf finds it — through the kernel — so an
        // image mounted before this session started can still be put away.
        var loop = LoopFor(full);

        if (loop is null) return;

        var unmount = await RunAsync(["unmount", "--no-user-interaction", "-b", loop], ct)
            .ConfigureAwait(false);

        // "not mounted" is the goal, not a failure — but anything else stops
        // the teardown, because detaching a loop device whose filesystem is
        // still mounted is how data goes missing.
        if (unmount.ExitCode != 0
            && !unmount.StdErr.Contains("not mounted", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Tidy(unmount.StdErr));

        var delete = await RunAsync(["loop-delete", "--no-user-interaction", "-b", loop], ct)
            .ConfigureAwait(false);

        if (delete.ExitCode != 0) throw new IOException(Tidy(delete.StdErr));
    }

    /// <summary>The loop device carrying this image, or null when nothing is.</summary>
    private string? LoopFor(string fullImagePath)
    {
        foreach (var line in MountLines is { } fake ? fake() : ReadMountLines())
        {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;

            var source = MountTable.Unescape(parts[0]);

            if (!source.StartsWith("/dev/loop", StringComparison.Ordinal)) continue;

            var backing = BackingFileOf is { } ask ? ask(source) : ReadBackingFile(source);

            if (string.Equals(backing, fullImagePath, StringComparison.Ordinal)) return source;
        }

        return null;
    }

    /// <summary>
    /// The loop device out of "Mapped file … as /dev/loop3." — the CLI's own
    /// sentence, which is the only place it reports the device it chose.
    /// </summary>
    internal static string? LoopDeviceIn(string output)
    {
        var at = output.IndexOf("/dev/loop", StringComparison.Ordinal);
        if (at < 0) return null;

        var end = at;
        while (end < output.Length && (char.IsLetterOrDigit(output[end]) || output[end] == '/')) end++;

        return output[at..end];
    }

    /// <summary>The mount point out of "Mounted /dev/loop3 at /run/media/…".</summary>
    internal static string? MountPointIn(string output)
    {
        const string marker = " at ";

        var at = output.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var rest = output[(at + marker.Length)..].Trim();

        // The sentence ends in a full stop that is not part of the path.
        return rest.TrimEnd('.', '\n', '\r') is { Length: > 0 } path ? path : null;
    }

    private async Task<CliResult> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (RunOverride is { } fake) return await fake(argv, ct).ConfigureAwait(false);

        var info = new ProcessStartInfo("udisksctl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in argv) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);

            if (process is null) return new CliResult(-1, "", "could not start udisksctl");

            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(Patience);

            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { Quiet.Swallowed("places", ex); }

                return new CliResult(-1, "", "udisksctl did not finish");
            }

            return new CliResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return new CliResult(-1, "", "udisksctl is not installed");
        }
    }

    private bool HaveTool(string name)
    {
        if (HaveToolOverride is { } fake) return fake(name);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return true;
            }
            catch (Exception e) when (e is ArgumentException or IOException) { }
        }

        return false;
    }

    private static IEnumerable<string> ReadMountLines()
        => File.Exists("/proc/mounts") ? File.ReadLines("/proc/mounts") : [];

    /// <summary>The tool's last clause — the same trim the ejector uses, and
    /// for the same D-Bus-shaped complaints.</summary>
    internal static string Tidy(string stderr)
    {
        var line = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return "the image could not be mounted";

        var at = line.LastIndexOf(": ", StringComparison.Ordinal);

        return at >= 0 && at + 2 < line.Length ? line[(at + 2)..] : line;
    }
}
