namespace Vaktari.Linux.Tests;

/// <summary>
/// The process's inotify instances and watches, by the kernel's own account.
/// Nothing in the application keeps these counts, so nothing it does can
/// satisfy them by accident.
/// </summary>
internal static class InotifyCount
{
    /// <summary>Each instance is a descriptor whose link reads
    /// "anon_inode:inotify".</summary>
    public static int Instances()
    {
        var count = 0;

        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "anon_inode:inotify") count++;
            }
            catch (IOException)
            {
                // The descriptor the listing itself used, closed since.
            }
        }

        return count;
    }

    /// <summary>The watches the kernel holds on instance
    /// <paramref name="descriptor"/>: one "inotify wd:" line each in its
    /// fdinfo.</summary>
    public static int Watches(int descriptor)
        => File.ReadAllLines($"/proc/self/fdinfo/{descriptor}").Count(l => l.StartsWith("inotify wd:", StringComparison.Ordinal));
}
