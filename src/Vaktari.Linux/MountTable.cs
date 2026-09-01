namespace Vaktari.Linux;

/// <summary>
/// Reading /proc/mounts, as pure text.
///
/// **Split out of <see cref="LinuxPlacesProvider"/> so the rules can be tested
/// at all.** The parse — escapes, network filesystems, snap images, btrfs
/// subvolumes, optical media — is the subtlest hundred lines in the Linux
/// assembly, and every one of them was unreachable from a test, because the
/// provider reads the literal path /proc/mounts. Vaktari.Linux.Tests
/// deliberately targets plain net10.0 so this logic can be checked on a Windows
/// desktop before CI ever sees it; that only pays off if the logic takes its
/// input as an argument.
///
/// Text only, and that is a safety property rather than a tidiness one: the
/// watcher asks for a signature every second, and anything that touched the
/// mounts themselves — <c>new DriveInfo(mountPoint)</c> is a statfs — would
/// block on a stick that was physically yanked while its mount lingers. That is
/// the Linux twin of the SMB freeze the sidebar already carries a scar from.
/// </summary>
internal static class MountTable
{
    /// <summary>
    /// The kernel escapes space, tab, newline and backslash in both the device
    /// and the mount point.
    ///
    /// **One left-to-right scan, never chained Replace calls.** Unescaping
    /// \134 first turns every remaining escape into a literal backslash
    /// followed by digits, so "\1340 40" and a real "\040" become
    /// indistinguishable — a folder named with a backslash would silently
    /// resolve to a different path than the one mounted. Left to right, each
    /// escape is consumed once and its output is never re-examined.
    /// </summary>
    internal static string Unescape(string field)
    {
        if (field.IndexOf('\\') < 0) return field;

        var built = new System.Text.StringBuilder(field.Length);

        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length)
            {
                var code = field.AsSpan(i + 1, 3);

                var replacement = code switch
                {
                    "040" => ' ',
                    "011" => '\t',
                    "012" => '\n',
                    "134" => '\\',
                    _ => '\0',
                };

                if (replacement != '\0')
                {
                    built.Append(replacement);
                    i += 3;
                    continue;
                }
            }

            built.Append(field[i]);
        }

        return built.ToString();
    }

    /// <summary>
    /// Whether this line describes a volume a person would recognise as a drive.
    ///
    /// Snap and flatpak mount squashfs images through loop devices, which live
    /// under /dev/ and so pass a naive "is it a block device" test — that is how
    /// a sidebar ends up listing a dozen entries named after revision numbers,
    /// all reporting zero bytes free.
    ///
    /// **iso9660 is NOT excluded, and used to be.** That exclusion was aimed at
    /// snap images, which are squashfs and were already caught twice over by the
    /// loop-device and fstype rules — while iso9660 is what an ordinary data CD
    /// carries. The result was that a real disc in a real drive never appeared
    /// in the sidebar on Linux, on the one platform where the code goes to the
    /// trouble of detecting optical media a few lines later. The dead branch
    /// naming iso9660 as optical is the evidence it was never meant to filter.
    /// </summary>
    internal static bool IsRealVolume(string source, string mountPoint, string fsType)
    {
        if (!source.StartsWith("/dev/", StringComparison.Ordinal)) return false;
        if (source.StartsWith("/dev/loop", StringComparison.Ordinal)) return false;
        if (source.StartsWith("/dev/zram", StringComparison.Ordinal)) return false;

        if (fsType is "squashfs" or "overlay" or "tmpfs" or "devtmpfs") return false;

        if (mountPoint.StartsWith("/boot", StringComparison.Ordinal)) return false;
        if (mountPoint.StartsWith("/snap", StringComparison.Ordinal)) return false;
        if (mountPoint.StartsWith("/var/lib/docker", StringComparison.Ordinal)) return false;

        return true;
    }

    internal static bool IsNetworkFs(string fsType) => fsType
        is "cifs" or "smb3" or "nfs" or "nfs4" or "fuse.sshfs" or "fuse.kio" or "fuse.gvfsd-fuse";

    /// <summary>
    /// Which mounts exist, as one comparable string.
    ///
    /// **Filtered before it is signed, which is not an optimisation.** snapd and
    /// flatpak mount and unmount squashfs loops continuously on an ordinary
    /// desktop; an unfiltered signature would change several times a minute and
    /// rebuild the sidebar each time, for entries the listing then throws away.
    /// The watcher would be a busy loop that never showed anything.
    /// </summary>
    internal static string Signature(IEnumerable<string> lines)
    {
        var kept = new List<string>();

        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            var source = Unescape(parts[0]);
            var mountPoint = Unescape(parts[1]);
            var fsType = parts[2];

            // Network mounts are in the sidebar too, so a share appearing has
            // to count — but gvfs control mounts are not somewhere anyone
            // navigates, and they churn.
            if (IsNetworkFs(fsType))
            {
                if (mountPoint.Contains("/gvfs", StringComparison.Ordinal)) continue;
            }
            else if (!IsRealVolume(source, mountPoint, fsType))
            {
                continue;
            }

            kept.Add($"{source}|{mountPoint}|{fsType}");
        }

        kept.Sort(StringComparer.Ordinal);

        return string.Join("\n", kept);
    }

    /// <summary>
    /// The device backing a mount point, which is what every eject verb takes.
    ///
    /// Read fresh at eject time rather than remembered on the Place: a device
    /// name on a cross-platform record is platform detail leaking into Core,
    /// and a list built minutes ago can name a device that has since been
    /// renumbered onto different hardware.
    /// </summary>
    internal static string? DeviceFor(IEnumerable<string> lines, string mountPoint)
    {
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;

            if (string.Equals(Unescape(parts[1]), mountPoint, StringComparison.Ordinal))
                return Unescape(parts[0]);
        }

        return null;
    }

    /// <summary>The real signature, for the watcher. A ~4 KB text read.</summary>
    internal static string Snapshot()
        => File.Exists("/proc/mounts") ? Signature(File.ReadLines("/proc/mounts")) : "";
}
