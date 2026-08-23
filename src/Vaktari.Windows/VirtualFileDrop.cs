using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Files that a drop offers without putting them anywhere.
///
/// **This is what dragging out of 7-Zip is.** An archive's contents do not
/// exist on disk, so the drag carries a list of names (CFSTR_FILEDESCRIPTORW)
/// and a stream per item (CFSTR_FILECONTENTS) rather than paths, and the
/// receiver is expected to ask for each one it wants. Explorer's own zip view
/// does the same. A handler that looks only for paths sees an empty drop, which
/// is why this appeared to do nothing whatsoever.
///
/// **Reached through a private field, which is a deliberate and guarded
/// choice.** Avalonia's public drop surface offers formats and bytes, and
/// CFSTR_FILECONTENTS cannot be expressed as bytes: it is retrieved one item at
/// a time, by index, as a stream. The Windows backend does hold the underlying
/// IDataObject — OleDataObjectToDataTransferWrapper._oleDataObject — and
/// nothing in the public API leads to it.
///
/// So the lookup is defensive at every step and gives up quietly. A future
/// Avalonia that renames the field costs this feature and nothing else: the
/// drop falls back to the message explaining that the files are inside an
/// archive. VirtualFileDropTests asserts the shape is still there, so the day
/// it changes is a failing test rather than a silent loss.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class VirtualFileDrop : IVirtualFileDrop
{
    /// <summary>The formats a shell folder offers files with. The wide one is
    /// what everything modern sends; the narrow one is still legal.</summary>
    private const string DescriptorW = "FileGroupDescriptorW";
    private const string DescriptorA = "FileGroupDescriptor";
    private const string Contents = "FileContents";

    /// <summary>
    /// **Bounded, because this writes to disk on a gesture.** A drag is not a
    /// considered decision, and an archive can hold a great deal more than
    /// anybody meant to drop.
    /// </summary>
    private const int MaxItems = 5_000;
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// **Never throws.** This is asked on every pointer move while a drag is
    /// over the window, and an exception out of a drag handler takes the drag
    /// with it — so a strange data object costs the feature rather than the
    /// gesture.
    /// </summary>
    public bool Offers(object dataTransfer)
    {
        try
        {
            return Native(dataTransfer) is { } data && Describes(data);
        }
        catch (Exception e) when (e is COMException or InvalidCastException
                                    or NotSupportedException or MemberAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> Take(object dataTransfer, CancellationToken token = default)
    {
        // **Said out loud from here down.** Every failure in this method used to
        // be swallowed, so a drop where the shell refused all of it arrived as
        // "nothing came out of that archive" with nothing, anywhere, saying
        // why — and the COM conversation cannot be reproduced in a test, so the
        // log is the only instrument there is.
        if (Native(dataTransfer) is not { } data)
        {
            Console.Error.WriteLine("[vaktari] drop: no native data object behind that drag");
            return [];
        }

        var names = Names(data);

        if (names.Count == 0)
        {
            Console.Error.WriteLine("[vaktari] drop: the drag names no files");
            return [];
        }

        // One folder per drop, so two drags of the same name do not fight and
        // what a cancelled drop leaves behind can be recognised.
        var folder = Path.Combine(
            Path.GetTempPath(), "Vaktari", "drops", Guid.NewGuid().ToString("N")[..12]);

        Directory.CreateDirectory(folder);

        var taken = new List<string>(names.Count);
        var written = 0L;
        var refused = 0;

        for (var i = 0; i < names.Count && i < MaxItems; i++)
        {
            token.ThrowIfCancellationRequested();

            // The descriptor carries a path, not just a name, when the archive
            // held folders — so the tree is recreated rather than flattened, or
            // two files of the same name collide.
            if (Contained(folder, names[i]) is not { } target) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                written += Write(data, i, target);

                if (written > MaxTotalBytes)
                    throw new IOException("that drop unpacks to more than a drop should.");

                taken.Add(target);
            }
            catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
            {
                // One entry the shell will not hand over is not a reason to
                // lose the rest of the drop — but it is a reason to say which,
                // and why. An HRESULT names the fault exactly where a message
                // does not: 0x8001010E is this being asked from the wrong
                // thread, which no amount of retrying will fix.
                refused++;

                if (refused <= 5)
                    Console.Error.WriteLine($"[vaktari] drop: '{names[i]}' refused — {Fault(e)}");
            }
        }

        Console.Error.WriteLine(
            $"[vaktari] drop: took {taken.Count} of {names.Count}"
            + (refused > 0 ? $", {refused} refused" : "")
            + $" · {written} bytes · apartment={Apartment()}");

        // Only the roots, or a tree would be copied flat into the destination.
        return Roots(folder, taken);
    }

    /// <summary>
    /// The topmost thing each taken path sits under, deduplicated — so a folder
    /// dragged out of an archive arrives as a folder.
    /// </summary>
    internal static List<string> Roots(string folder, IReadOnlyList<string> taken)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in taken)
        {
            var relative = Path.GetRelativePath(folder, path);
            var first = relative.Split(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            var root = Path.Combine(folder, first);

            if (seen.Add(root)) roots.Add(root);
        }

        return roots;
    }

    // ---- the data object ---------------------------------------------------

    /// <summary>
    /// Digs the native data object out of Avalonia's wrapper. Null the moment
    /// anything is not as expected, which costs this feature and nothing else.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification =
            "The field is Avalonia's own and is read and written by its own drag "
            + "and drop code, so trimming keeps it: it is reachable regardless of "
            + "anything here. The analyser cannot see that because the type "
            + "arrives as object. Every step is null-guarded, so if a future "
            + "version renames or removes it the result is null and the drop "
            + "falls back to explaining that the files are inside an archive — "
            + "which VirtualFileDropTests asserts is still the shape.")]
    internal static IDataObject? Native(object dataTransfer)
    {
        for (var type = dataTransfer.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("_oleDataObject", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field?.GetValue(dataTransfer) is not { } held) continue;

            // **Not a cast.** What Avalonia holds is its own declaration of the
            // interface — Avalonia.Win32.Win32Com.IDataObject — which has
            // nothing to do with the framework's ComTypes.IDataObject beyond
            // describing the same native object. Casting between them returns
            // null, silently, and the whole feature never fires.
            //
            // So the native pointer is asked for the interface this code knows
            // how to speak, which is what QueryInterface is for.
            if (Retype(held) is { } data) return data;
        }

        return null;
    }

    /// <summary>
    /// The same native object, seen through the framework's own declaration of
    /// IDataObject.
    ///
    /// Source-generated COM wrappers are not reachable with
    /// Marshal.GetIUnknownForObject, so the pointer is asked for by the API
    /// that knows about them, with the older call as the fallback for a plain
    /// runtime-callable wrapper.
    /// </summary>
    private static IDataObject? Retype(object held)
    {
        if (held is IDataObject already) return already;

        var unknown = IntPtr.Zero;
        var owned = false;

        try
        {
            if (System.Runtime.InteropServices.ComWrappers.TryGetComInstance(held, out unknown))
            {
                owned = true;
            }
            else
            {
                try
                {
                    unknown = Marshal.GetIUnknownForObject(held);
                    owned = true;
                }
                catch (Exception e) when (e is InvalidCastException or NotSupportedException)
                {
                    return null;
                }
            }

            if (unknown == IntPtr.Zero) return null;

            return Marshal.GetTypedObjectForIUnknown(unknown, typeof(IDataObject)) as IDataObject;
        }
        catch (Exception e) when (e is InvalidCastException or COMException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            // GetTypedObjectForIUnknown took its own reference.
            if (owned && unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// What went wrong, with the HRESULT where there is one. A COM failure's
    /// message is often a generic sentence; the number is the part that
    /// identifies it.
    /// </summary>
    private static string Fault(Exception e)
        => e is COMException com
            ? $"0x{com.HResult:X8} {com.Message.Trim()}"
            : $"{e.GetType().Name}: {e.Message.Trim()}";

    /// <summary>
    /// Which apartment this ran in. A shell data object belongs to the thread
    /// that received the drop, and asking it from anywhere else is the failure
    /// that looks like an archive refusing every file in it.
    /// </summary>
    private static string Apartment()
        => Thread.CurrentThread.GetApartmentState() switch
        {
            ApartmentState.STA => "STA",
            ApartmentState.MTA => "MTA — wrong for a drop",
            _ => "unknown",
        };

    private static bool Describes(IDataObject data) =>
        Available(data, DescriptorW) || Available(data, DescriptorA);

    private static bool Available(IDataObject data, string format)
    {
        try
        {
            var descriptor = Descriptor(format, -1, TYMED.TYMED_HGLOBAL);

            return data.QueryGetData(ref descriptor) == 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static FORMATETC Descriptor(string name, int index, TYMED tymed) => new()
    {
        cfFormat = (short)RegisterClipboardFormatW(name),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        tymed = tymed,
    };

    // ---- names -------------------------------------------------------------

    /// <summary>
    /// Reads FILEGROUPDESCRIPTORW: a count, then that many fixed-size records
    /// whose file name sits at a known offset.
    /// </summary>
    private static List<string> Names(IDataObject data)
    {
        foreach (var (format, wide) in new[] { (DescriptorW, true), (DescriptorA, false) })
        {
            if (!Available(data, format)) continue;

            var descriptor = Descriptor(format, -1, TYMED.TYMED_HGLOBAL);
            STGMEDIUM medium = default;

            try
            {
                data.GetData(ref descriptor, out medium);

                if (medium.unionmember == IntPtr.Zero) continue;

                var block = GlobalLock(medium.unionmember);
                if (block == IntPtr.Zero) continue;

                try
                {
                    return Parse(block, wide);
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
            }
            catch (COMException)
            {
                // Try the other spelling, then give up.
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }

        return [];
    }

    /// <summary>
    /// **The offsets are the format's, not a guess.** FILEDESCRIPTORW is 592
    /// bytes with its name at 72; the ANSI form is 332 with its name in the
    /// same place. Both begin with a UINT count.
    /// </summary>
    internal static List<string> Parse(IntPtr block, bool wide)
    {
        var count = Marshal.ReadInt32(block);
        var names = new List<string>();

        if (count <= 0) return names;

        var size = wide ? 592 : 332;
        const int nameOffset = 72;

        for (var i = 0; i < count && i < MaxItems; i++)
        {
            var at = block + 4 + (i * size) + nameOffset;

            var name = wide ? Marshal.PtrToStringUni(at, 260) : Marshal.PtrToStringAnsi(at, 260);

            if (name is null) continue;

            // A fixed-width buffer, so the name is padded with nulls rather
            // than ended by the record.
            var end = name.IndexOf('\0');
            if (end >= 0) name = name[..end];

            if (name.Length > 0) names.Add(name);
        }

        return names;
    }

    // ---- contents ----------------------------------------------------------

    /// <summary>
    /// Asks for one item's bytes and writes them out.
    ///
    /// A stream where the shell offers one, which is how anything large
    /// arrives, and a memory block where it does not. Both are legal; 7-Zip
    /// uses the stream.
    /// </summary>
    private static long Write(IDataObject data, int index, string target)
    {
        var wanted = Descriptor(Contents, index, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium = default;

        try
        {
            data.GetData(ref wanted, out medium);

            using var file = File.Create(target);

            if (medium.tymed == TYMED.TYMED_ISTREAM && medium.unionmember != IntPtr.Zero)
                return Pump((IStream)Marshal.GetObjectForIUnknown(medium.unionmember), file);

            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
            {
                var block = GlobalLock(medium.unionmember);

                if (block == IntPtr.Zero) return 0;

                try
                {
                    var size = (long)(ulong)GlobalSize(medium.unionmember);
                    var buffer = new byte[size];

                    Marshal.Copy(block, buffer, 0, (int)size);
                    file.Write(buffer, 0, buffer.Length);

                    return size;
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
            }

            // A folder inside the archive arrives as an entry with no contents,
            // which is how it says "make this and put things in it".
            return 0;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static long Pump(IStream stream, Stream file)
    {
        var buffer = new byte[81920];
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        var total = 0L;

        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);

                var got = Marshal.ReadInt32(read);
                if (got <= 0) break;

                file.Write(buffer, 0, got);
                total += got;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            Marshal.ReleaseComObject(stream);
        }

        return total;
    }

    /// <summary>
    /// Where a descriptor's name may be written.
    ///
    /// The name comes from the drag source, so it is not to be trusted with a
    /// path that climbs out of the folder — the same rule the theme unpacker
    /// applies to an archive, and for the same reason.
    /// </summary>
    internal static string? Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal)) return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var anchored = Path.GetFullPath(root);

            if (!anchored.EndsWith(Path.DirectorySeparatorChar))
                anchored += Path.DirectorySeparatorChar;

            return full.StartsWith(anchored, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// **DllImport rather than LibraryImport**, which is the exception in this
    /// assembly and not a slip: STGMEDIUM is a union with an IUnknown in it and
    /// the source generator refuses to marshal it (SYSLIB1051). The older
    /// attribute is still ahead-of-time compatible; it just writes the stub
    /// itself.
    /// </summary>
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial UIntPtr GlobalSize(IntPtr handle);
}
