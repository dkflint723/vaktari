namespace Vaktari.Core.FileSystem;

/// <summary>
/// Appending only: persisted as numbers, so reordering would reinterpret
/// every saved session.
/// </summary>
public enum GroupMode { None, Name, Size, Modified, Kind }

/// <summary>
/// The band a file belongs to when the listing is grouped.
///
/// Bands rather than exact values: grouping by every distinct size or timestamp
/// would give one group per file, which is the same as no grouping but with
/// more lines. The bands are the ones people actually reason in — today, this
/// week, small, huge.
/// </summary>
public static class Grouping
{
    public static string Label(FileEntry entry, GroupMode mode, DateTimeOffset now) => mode switch
    {
        GroupMode.Name => NameBand(entry.Name),
        GroupMode.Size => entry.IsDirectory ? "Folders" : SizeBand(entry.Length),
        GroupMode.Modified => DateBand(entry.LastWriteTime, now),
        // **Extension is already dot-free**, so slicing a character off it was
        // slicing off a character of the name: .txt grouped under "XT", .cs
        // under "S". The > 1 guard hid the other half - a one-letter extension
        // such as .c had nothing left after the slice, so every C file grouped
        // as "No extension". CompareGroups and the Kind sort beside it have
        // always read Extension as it really is; only this label disagreed.
        GroupMode.Kind => entry.IsDirectory
            ? "Folders"
            : entry.Extension.Length > 0
                ? entry.Extension.ToString().ToUpperInvariant()
                : "No extension",
        _ => "",
    };

    /// <summary>
    /// Orders two entries by their group, without building the label.
    ///
    /// Sorting is the hot path — a 200k listing is millions of comparisons — so
    /// this compares ranks and spans rather than allocating a band string per
    /// comparison. Grouping has to participate in the sort at all, or bands
    /// interleave and every group holds one file.
    /// </summary>
    public static int CompareGroups(FileEntry a, FileEntry b, GroupMode mode, DateTimeOffset now)
    {
        switch (mode)
        {
            case GroupMode.Name:
                return NameRank(a.Name).CompareTo(NameRank(b.Name));

            case GroupMode.Size:
                return SizeRank(a).CompareTo(SizeRank(b));

            case GroupMode.Modified:
                return DateRank(a.LastWriteTime, now).CompareTo(DateRank(b.LastWriteTime, now));

            case GroupMode.Kind:
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return a.Extension.CompareTo(b.Extension, StringComparison.OrdinalIgnoreCase);

            default:
                return 0;
        }
    }

    private static char NameRank(string name)
    {
        if (name.Length == 0) return '#';

        var first = char.ToUpperInvariant(name[0]);
        return char.IsAsciiLetter(first) ? first : '#';
    }

    private static int SizeRank(FileEntry entry) => entry.IsDirectory ? -1 : entry.Length switch
    {
        < 100L * 1024 => 0,
        < 1024L * 1024 => 1,
        < 100L * 1024 * 1024 => 2,
        < 1024L * 1024 * 1024 => 3,
        _ => 4,
    };

    /// <summary>Lower rank sorts first, so newest-first matches the labels.</summary>
    private static int DateRank(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.Date;
        var today = now.Date;

        if (day > today) return 0;
        if (day == today) return 1;
        if (day == today.AddDays(-1)) return 2;
        if (day > today.AddDays(-7)) return 3;
        if (day > today.AddDays(-31)) return 4;
        if (day.Year == today.Year) return 5;

        return 6 + (today.Year - day.Year);
    }

    private static string NameBand(string name)
    {
        if (name.Length == 0) return "#";

        var first = char.ToUpperInvariant(name[0]);
        return char.IsAsciiLetter(first) ? first.ToString() : "#";
    }

    private static string SizeBand(long bytes) => bytes switch
    {
        < 100L * 1024 => "Tiny — under 100 KiB",
        < 1024L * 1024 => "Small — under 1 MiB",
        < 100L * 1024 * 1024 => "Medium — under 100 MiB",
        < 1024L * 1024 * 1024 => "Large — under 1 GiB",
        _ => "Huge — 1 GiB and over",
    };

    /// <summary>
    /// Calendar-relative, not elapsed-time: "yesterday" should mean yesterday's
    /// date, not twenty-five hours ago.
    /// </summary>
    private static string DateBand(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.Date;
        var today = now.Date;

        if (day > today) return "Later";
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return "This week";
        if (day > today.AddDays(-31)) return "This month";
        if (day.Year == today.Year) return "This year";

        return day.Year.ToString();
    }
}
