using System.Security;
using System.Security.Principal;
using System.Text;

namespace Vaktari.Windows;

/// <summary>One thing in the Recycle Bin, as its metadata file describes it.</summary>
internal sealed record RecycleEntry(
    string InfoPath,
    string PayloadPath,
    string OriginalPath,
    DateTimeOffset Deleted,
    long Size,
    bool IsDirectory);

/// <summary>
/// The Recycle Bin, read from its own on-disk metadata.
///
/// **The same shape as the freedesktop trash, which is why this works at all.**
/// Both keep the deleted bytes under one name and the memory of where they came
/// from in a sidecar beside it: <c>$IABC123.txt</c> holds the original path,
/// the size and the deletion time, and <c>$RABC123.txt</c> holds the payload.
/// ITrashMaintenance was designed against that shape, so Windows satisfies it
/// almost line for line — see XdgTrash.Restore.
///
/// **Read directly rather than through the shell, and that is a deliberate
/// reversal of the earlier assumption.** COM was measured first: a
/// source-generated IShellItem enumeration of the bin works correctly in a
/// published NativeAOT binary, so the risk WINDOWS.md recorded is not real. The
/// format is still the better tool for this particular job, for reasons that
/// have nothing to do with whether COM works:
///
/// - The interface requires that a restore whose original name has been taken
///   lands ALONGSIDE rather than clobbering, and returns where it landed. The
///   shell's undelete verb decides that for itself and reports nothing back.
/// - Sweep needs a size and a deletion date per entry to apply a policy. Both
///   are plain fields here; through the shell they are property-store lookups.
/// - Nothing here shows UI, prompts, or blocks on a dialog, which matters for
///   an unattended sweep.
///
/// The format is documented and unchanged since Vista. A version this does not
/// recognise is skipped rather than guessed at — see <see cref="TryParse"/>.
/// </summary>
internal static class RecycleBin
{
    /// <summary>
    /// Every per-volume bin belonging to the current user.
    ///
    /// The Recycle Bin is not one folder: each volume carries its own
    /// <c>$Recycle.Bin</c>, and inside it one folder per user SID. Deleting
    /// from D: puts the file in D:'s bin, so a listing that reads only C: is
    /// silently partial.
    /// </summary>
    internal static IEnumerable<string> Directories()
    {
        string sid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null) yield break;
            sid = identity.User.Value;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            yield break;
        }

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { yield break; }

        foreach (var drive in drives)
        {
            bool ready;
            try { ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch (IOException) { continue; }

            if (!ready) continue;

            var path = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);

            var exists = false;
            try { exists = Directory.Exists(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            if (exists) yield return path;
        }
    }

    /// <summary>
    /// Whether any volume's bin holds anything, without reading a single
    /// sidecar or sorting anything.
    ///
    /// The same walk as <see cref="List"/> and then it stops at the first hit:
    /// EnumerateFiles rather than GetFiles, so a bin with ten thousand items
    /// costs one directory entry rather than ten thousand plus a metadata read
    /// each. The sidebar asks this on every rebuild.
    /// </summary>
    internal static bool HasAny()
    {
        foreach (var directory in Directories())
        {
            try
            {
                if (Directory.EnumerateFiles(directory, "$I*").Any()) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A volume that will not answer is not a reason to claim the
                // bin is full, and not a reason to stop asking the others.
            }
        }

        return false;
    }

    /// <summary>Everything currently in the bin, newest first.</summary>
    internal static List<RecycleEntry> List()
    {
        var entries = new List<RecycleEntry>();

        // Over the same walk Names makes, so a volume one of them reaches is a
        // volume the other reaches. The reading is the only difference between
        // them, and it is all of the cost — see Names.
        foreach (var info in Names())
        {
            if (Read(info) is { } entry) entries.Add(entry);
        }

        entries.Sort((a, b) => b.Deleted.CompareTo(a.Deleted));

        return entries;
    }

    /// <summary>
    /// Every metadata path in every bin, without opening one of them.
    ///
    /// **The undo after a recycle wants nothing but these names.** It takes the
    /// difference between the bin before the call and the bin after, and a
    /// trash key IS the <c>$I</c> path — which the directory entry already
    /// carries. Going through <see cref="List"/> for it opened, parsed and
    /// payload-checked every entry in the bin, twice, on every Delete key press,
    /// to hand back a field that cost nothing.
    ///
    /// Measured against a real bin holding 107 entries across two volumes,
    /// warm cache, repeated because the spread is wide: List 9-19 ms a call,
    /// this walk 0.17-0.33 ms. On a synthetic bin the per-entry read costs
    /// 62-81 us warm, so 2000 items are of the order of 150 ms a listing
    /// against a couple of ms a walk.
    ///
    /// **A wider answer than List's, deliberately.** An <c>$I</c> whose payload
    /// is gone, or whose bytes name a format version this does not know, has no
    /// entry — but it still holds its name. That same bin answered 114 names to
    /// 107 entries, so this is not a hypothetical: seven files there are one and
    /// not the other. It is right for a difference, where such a file is in the
    /// before set and the after set alike and cancels, and wrong for a listing,
    /// which is why List still reads every one.
    /// </summary>
    internal static IEnumerable<string> Names()
    {
        foreach (var directory in Directories())
            foreach (var info in InfoFiles(directory))
                yield return info;
    }

    /// <summary>
    /// The metadata files in one volume's bin, and nothing else in there: the
    /// payloads share the folder, and so does the <c>desktop.ini</c> that gives
    /// the bin its name in Explorer.
    ///
    /// A volume that will not answer contributes nothing rather than stopping
    /// the walk, which is the rule <see cref="HasAny"/> follows too — one
    /// unreadable drive must not hide the bins on the others.
    /// </summary>
    internal static string[] InfoFiles(string directory)
    {
        try { return Directory.GetFiles(directory, "$I*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>One entry, or null if its metadata cannot be trusted.</summary>
    internal static RecycleEntry? Read(string infoPath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(infoPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        if (!TryParse(bytes, out var original, out var size, out var deleted)) return null;

        var payload = PayloadFor(infoPath);
        if (payload is null) return null;

        bool isDirectory;
        try
        {
            if (!File.Exists(payload) && !Directory.Exists(payload)) return null;
            isDirectory = Directory.Exists(payload);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        return new RecycleEntry(infoPath, payload, original, deleted, size, isDirectory);
    }

    /// <summary>
    /// The payload beside a metadata file. <c>$I</c> and <c>$R</c> differ by
    /// exactly one character and share everything after it, extension included.
    /// </summary>
    internal static string? PayloadFor(string infoPath)
    {
        var name = Path.GetFileName(infoPath);

        if (name.Length < 2 || !name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) return null;

        return Path.Combine(Path.GetDirectoryName(infoPath) ?? "", "$R" + name[2..]);
    }

    /// <summary>
    /// The metadata format, which has had two versions and is otherwise
    /// unchanged since Vista.
    ///
    /// <code>
    ///   0  version   8 bytes   1 = Vista..8.1, 2 = Windows 10 and later
    ///   8  size      8 bytes   payload size in bytes
    ///  16  deleted   8 bytes   FILETIME, UTC
    ///  24  path                v1: 260 fixed UTF-16 chars
    ///                          v2: 4-byte length in chars, then that many
    /// </code>
    ///
    /// An unknown version returns false rather than reading offset 24 hopefully.
    /// The consequence of guessing wrong is not a bad listing — Restore would
    /// move a file to a path invented from unrelated bytes.
    /// </summary>
    internal static bool TryParse(
        byte[] bytes, out string originalPath, out long size, out DateTimeOffset deleted)
    {
        originalPath = "";
        size = 0;
        deleted = default;

        if (bytes.Length < 24) return false;

        var version = BitConverter.ToInt64(bytes, 0);
        if (version is not (1 or 2)) return false;

        size = BitConverter.ToInt64(bytes, 8);
        if (size < 0) return false;

        var filetime = BitConverter.ToInt64(bytes, 16);
        if (filetime <= 0) return false;

        try { deleted = DateTimeOffset.FromFileTime(filetime); }
        catch (ArgumentOutOfRangeException) { return false; }

        if (version == 1)
        {
            // A fixed 260-character field, NUL-padded.
            if (bytes.Length < 24 + 520) return false;
            originalPath = Trim(Encoding.Unicode.GetString(bytes, 24, 520));
        }
        else
        {
            if (bytes.Length < 28) return false;

            var characters = BitConverter.ToInt32(bytes, 24);

            // The length counts the terminating NUL, and a path cannot be
            // longer than the buffer holding it.
            if (characters <= 1 || 28 + (characters * 2) > bytes.Length) return false;

            originalPath = Trim(Encoding.Unicode.GetString(bytes, 28, (characters - 1) * 2));
        }

        return originalPath.Length > 0 && Path.IsPathFullyQualified(originalPath);
    }

    private static string Trim(string value)
    {
        var nul = value.IndexOf('\0');
        return (nul < 0 ? value : value[..nul]).Trim();
    }
}
