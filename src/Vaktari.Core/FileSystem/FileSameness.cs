namespace Vaktari.Core.FileSystem;

/// <summary>What two files' sizes and modification times say about each other.</summary>
public enum Sameness
{
    /// <summary>The same size, changed at the same moment: they look like the same file.</summary>
    Same,

    /// <summary>Changed at the same moment, but not the same size: they differ, and neither is newer.</summary>
    SameTime,

    /// <summary>The first was changed later.</summary>
    FirstNewer,

    /// <summary>The second was changed later.</summary>
    SecondNewer,
}

/// <summary>
/// The one rule for whether two files look the same, and which is newer —
/// shared by the file-clash prompt and the folder comparison, so the prompt
/// can never call a file the same one that the comparison marks as newer.
///
/// **Under two seconds apart is the same moment.** FAT stores a modification
/// time in two-second steps, so a copy on a FAT stick keeps its original's time
/// only to within two seconds; a rule that wanted the times equal would mark
/// every such copy as changed. The file-clash prompt already treated a gap under
/// two seconds as "changed at the same time" — but still called the two files
/// the same only when the times were exactly equal, which is the case a FAT
/// copy never produces.
/// </summary>
public static class FileSameness
{
    /// <summary>How far apart two modification times may be and still be one moment.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public static Sameness Judge(long firstLength, DateTimeOffset firstTime, long secondLength, DateTimeOffset secondTime)
    {
        var difference = firstTime - secondTime;

        if (difference.Duration() < Tolerance)
            return firstLength == secondLength ? Sameness.Same : Sameness.SameTime;

        return difference > TimeSpan.Zero ? Sameness.FirstNewer : Sameness.SecondNewer;
    }
}
