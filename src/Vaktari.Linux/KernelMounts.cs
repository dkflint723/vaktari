namespace Vaktari.Linux;

/// <summary>
/// Where the kernel's own filesystems are mounted — /proc, /sys, autofs
/// trigger points and the rest of <see cref="MountTable.IsKernelFs"/> — for the
/// walks that must not go inside them.
///
/// Read from /proc/mounts and kept for a few seconds: a walk asks about every
/// folder it meets, and the table changes rarely.
/// </summary>
internal static class KernelMounts
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    private static readonly Lock Gate = new();
    private static HashSet<string> _points = new(StringComparer.Ordinal);
    private static DateTime _read = DateTime.MinValue;

    /// <summary>Whether a folder is where a kernel filesystem is mounted.</summary>
    public static bool Contains(string path)
    {
        HashSet<string> points;

        lock (Gate)
        {
            if (DateTime.UtcNow - _read > Fresh)
            {
                _points = PointsIn(File.Exists("/proc/mounts") ? ReadLines() : []);
                _read = DateTime.UtcNow;
            }

            points = _points;
        }

        return points.Contains(path.Length > 1 ? path.TrimEnd('/') : path);
    }

    private static List<string> ReadLines()
    {
        try
        {
            return [.. File.ReadLines("/proc/mounts")];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("mounts", e);
            return [];
        }
    }

    /// <summary>
    /// **The mount on top decides.** An automount point is autofs until it is
    /// used, and then a real filesystem is mounted over it at the same place,
    /// later in the table — /boot, or a NAS in fstab with x-systemd.automount.
    /// Read by the first line, the whole volume was skipped.
    /// </summary>
    internal static HashSet<string> PointsIn(IEnumerable<string> lines)
    {
        var top = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            top[MountTable.Unescape(parts[1])] = parts[2];
        }

        return top.Where(p => MountTable.IsKernelFs(p.Value)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }
}
