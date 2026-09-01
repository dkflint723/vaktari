using System.ComponentModel;
using System.Diagnostics;
using Vaktari.Core;
using Vaktari.Core.Places;

namespace Vaktari.Linux;

/// <summary>
/// Safely removing a volume on Linux, by driving udisks2's own command line.
///
/// **udisksctl is the supported interface, not a workaround.** The alternative
/// is speaking D-Bus by hand — SASL EXTERNAL over a unix socket, then an
/// ObjectManager walk — which is several hundred lines of protocol to reach a
/// daemon that ships a first-class CLI, in an assembly that takes no
/// dependencies. Driving a tool and reporting its own words back is already how
/// this project talks to git, copyparty, gio and the Proton CLI.
///
/// It also puts the privilege question where it belongs: udisks2 asks polkit,
/// polkit knows this is the user's own session and their own removable device,
/// and Vaktari never holds a right of its own.
/// </summary>
internal sealed class UdisksEjector : IEjector
{
    /// <summary>
    /// **Two minutes, not twenty seconds.** Unmounting is where a stick's
    /// write-back is flushed, and after a large copy to a slow device that
    /// genuinely takes minutes. Killing it mid-flush is the one outcome worse
    /// than making someone wait.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliResult>>? RunOverride { get; init; }
    internal Func<string, bool>? HaveToolOverride { get; init; }
    internal Func<IEnumerable<string>>? MountLines { get; init; }

    internal readonly record struct CliResult(int ExitCode, string StdOut, string StdErr);

    public async Task<EjectResult> EjectAsync(string path, CancellationToken ct)
    {
        // **Read the mount table now, not when the sidebar was built.** A device
        // name captured minutes ago can have been renumbered onto other
        // hardware, and ejecting the wrong device is not a recoverable mistake.
        var lines = (MountLines is { } fake ? fake() : ReadMountLines()).ToList();

        var device = MountTable.DeviceFor(lines, path);

        if (device is null)
        {
            // Nothing is mounted there any more. Eject is idempotent, and the
            // person's goal — that volume not being mounted — is already true.
            return EjectResult.Ejected("that drive is already gone");
        }

        if (!HaveTool("udisksctl"))
            return EjectResult.NoTool(
                "udisks2 is not installed, so this desktop has no way to eject a drive safely");

        var fsType = FsTypeFor(lines, path);
        var optical = fsType is "iso9660" or "udf"
                   || device.StartsWith("/dev/sr", StringComparison.Ordinal);

        var unmount = await RunAsync(
            ["unmount", "--no-user-interaction", "-b", device], ct).ConfigureAwait(false);

        if (unmount.ExitCode != 0)
        {
            var complaint = Tidy(unmount.StdErr);

            if (complaint.Contains("busy", StringComparison.OrdinalIgnoreCase))
                return EjectResult.InUse(
                    $"something still has a file open on {Name(path)} — close it and try again");

            // A tool that is installed but cannot see the object is a case for
            // the plain fallbacks; anything else is reported in its own words.
            if (complaint.Contains("looking up object", StringComparison.OrdinalIgnoreCase))
                return await FallbackAsync(path, fsType, ct).ConfigureAwait(false);

            return EjectResult.Failed(complaint);
        }

        if (optical)
        {
            // udisksctl has no eject verb; util-linux's does the tray. A missing
            // binary is not a failure — the filesystem is already flushed and
            // unmounted, which is the part that matters.
            var tray = await RunAsync(["__eject__", device], ct).ConfigureAwait(false);

            return tray.ExitCode == 0
                ? EjectResult.Ejected($"ejected the disc in {Name(path)}")
                : EjectResult.Ejected($"{Name(path)} is unmounted — the tray did not open");
        }

        var off = await RunAsync(
            ["power-off", "--no-user-interaction", "-b", device], ct).ConfigureAwait(false);

        if (off.ExitCode == 0)
            return EjectResult.Ejected($"{Name(path)} is safe to unplug");

        // A drive that cannot be powered off — a card reader, an internal bay —
        // is not an error worth showing: udisks knows whether the hardware
        // supports it, and the filesystem is flushed either way.
        //
        // **Matched against the RAW stderr, not the tidied sentence.** The
        // reliable marker is the D-Bus error TYPE, `…UDisks2.Error.NotSupported`,
        // which Tidy strips by design — and the prose it leaves behind says
        // "does not support", so a match on "not supported" quietly never fires
        // and every card reader reports a half-eject.
        if (off.StdErr.Contains("NotSupported", StringComparison.Ordinal)
            || off.StdErr.Contains("not support", StringComparison.OrdinalIgnoreCase))
            return EjectResult.Ejected($"{Name(path)} is safe to unplug");

        return EjectResult.Dismounted(
            $"{Name(path)} is written out and safe to unplug — but the system would not power it down");
    }

    /// <summary>
    /// When udisks2 is present but does not know the device — a plain
    /// mount(8) mount, or a fuse filesystem — fall back to the ordinary tools.
    ///
    /// **Never entered after a "busy" refusal.** Retrying a busy volume with a
    /// blunter tool is how a lazy unmount gets reached for.
    /// </summary>
    private async Task<EjectResult> FallbackAsync(string path, string? fsType, CancellationToken ct)
    {
        var fuse = fsType is not null && fsType.StartsWith("fuse", StringComparison.Ordinal);

        var result = fuse
            ? await RunAsync(["__fusermount__", "-u", path], ct).ConfigureAwait(false)
            : await RunAsync(["__umount__", path], ct).ConfigureAwait(false);

        if (result.ExitCode == 0) return EjectResult.Ejected($"{Name(path)} is unmounted");

        var complaint = Tidy(result.StdErr);

        return complaint.Contains("busy", StringComparison.OrdinalIgnoreCase)
            ? EjectResult.InUse(
                $"something still has a file open on {Name(path)} — close it and try again")
            : EjectResult.Failed(complaint);
    }

    /// <summary>
    /// Runs udisksctl — or, for the two argv forms that name another tool, that
    /// tool instead.
    ///
    /// **--force is never spoken, and neither is umount -l.** A lazy unmount
    /// (MNT_DETACH) returns success with write-back still pending: it detaches
    /// the tree and leaves the kernel writing. A button whose entire promise is
    /// "safe to remove" must never make that promise on the strength of one.
    /// It is a five-character edit that reads like a robustness improvement to
    /// anyone who has not read this comment, so it is pinned by a test rather
    /// than trusted to review.
    /// </summary>
    private async Task<CliResult> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (RunOverride is { } fake) return await fake(argv, ct).ConfigureAwait(false);

        var (file, arguments) = argv[0] switch
        {
            "__eject__" => ("eject", argv.Skip(1).ToList()),
            "__umount__" => ("umount", argv.Skip(1).ToList()),
            "__fusermount__" => ("fusermount", argv.Skip(1).ToList()),
            _ => ("udisksctl", argv.ToList()),
        };

        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);

            if (process is null) return new CliResult(-1, "", "could not start " + file);

            // Closed immediately: an interactive polkit prompt would otherwise
            // block forever against a terminal that does not exist.
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

                return new CliResult(-1, "", $"{file} did not finish");
            }

            return new CliResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            // The house signal for "that binary is not installed".
            return new CliResult(-1, "", $"{file} is not installed");
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

    private static string? FsTypeFor(IEnumerable<string> lines, string mountPoint)
    {
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            if (string.Equals(MountTable.Unescape(parts[1]), mountPoint, StringComparison.Ordinal))
                return parts[2];
        }

        return null;
    }

    private static string Name(string mountPoint)
    {
        var name = Path.GetFileName(mountPoint.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? mountPoint : name;
    }

    /// <summary>
    /// The tool's last clause, which is the readable part.
    ///
    /// udisks answers through D-Bus, so its refusals arrive as
    /// "GDBus.Error:org.freedesktop.UDisks2.Error.DeviceBusy: … target is
    /// busy" — the same shape LinuxRemoteMounts already trims by keeping what
    /// follows the final ": ".
    /// </summary>
    internal static string Tidy(string stderr)
    {
        var line = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return "the drive could not be ejected";

        var at = line.LastIndexOf(": ", StringComparison.Ordinal);

        return at >= 0 && at + 2 < line.Length ? line[(at + 2)..] : line;
    }
}
