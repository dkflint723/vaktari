using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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
/// archive. AvaloniaDropShapeTests asserts the shape is still there, so the day
/// it changes is a failing test rather than a silent loss.
///
/// **Source-generated COM throughout, because the shipped build is NativeAOT.**
/// This used to speak the framework's ComTypes.IDataObject and IStream through
/// Marshal.GetTypedObjectForIUnknown and Marshal.GetObjectForIUnknown — the
/// runtime-built RCW machinery, which NativeAOT does not have. Every one of
/// those calls threw, the throw was caught as "not a data object", and the
/// published build answered every archive drag with the archive message. (A
/// build under the JIT fared no better, for a different reason — see
/// <see cref="Retype"/>.) The interfaces are declared at the bottom of this
/// file, the way <see cref="WindowsShortcuts"/> declares its own.
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
    private const string TooMuch = "that drop unpacks to more than a drop should.";

    /// <summary>
    /// **Never throws.** This is asked on every pointer move while a drag is
    /// over the window, and an exception out of a drag handler takes the drag
    /// with it — so a strange data object costs the feature rather than the
    /// gesture.
    /// </summary>
    public bool Offers(object dataTransfer)
    {
        IDataObject? data = null;

        try
        {
            data = Native(dataTransfer);

            return data is not null && Describes(data);
        }
        catch (Exception e) when (e is COMException or InvalidCastException
                                    or NotSupportedException or MemberAccessException)
        {
            return false;
        }
        finally
        {
            Release(data);
        }
    }

    public IReadOnlyList<string> Take(object dataTransfer, CancellationToken token = default)
    {
        // **Said out loud from here down.** Every failure in this method used to
        // be swallowed, so a drop where the shell refused all of it arrived as
        // "nothing came out of that archive" with nothing, anywhere, saying
        // why — and a real drag cannot be staged in a test, so the log is the
        // instrument for everything past the data object itself.
        if (Native(dataTransfer) is not { } data)
        {
            Console.Error.WriteLine("[vaktari] drop: no native data object behind that drag");
            return [];
        }

        try
        {
            return Take(data, token);
        }
        finally
        {
            Release(data);
        }
    }

    private static IReadOnlyList<string> Take(IDataObject data, CancellationToken token)
    {
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
            if (Contained(folder, names[i].Name) is not { } target) continue;

            try
            {
                // **A folder is made, not asked for contents.** Asked, a source
                // that answered with nothing left an empty FILE of the folder's
                // name, and every file inside it was then refused; one that
                // refused dropped an empty folder from the drop altogether.
                if (names[i].IsFolder)
                {
                    Directory.CreateDirectory(target);
                    taken.Add(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // By the entry's own place in the descriptor, which is the
                // index its contents are asked for by — not its place in this
                // list, which a skipped nameless entry would shift.
                written += Write(data, names[i].Index, target, MaxTotalBytes - written);

                if (written > MaxTotalBytes) throw new IOException(TooMuch);

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
                    Console.Error.WriteLine($"[vaktari] drop: '{names[i].Name}' refused — {Fault(e)}");
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
    ///
    /// What comes back is this drop's own reference, and <see cref="Release"/> is
    /// what gives it back.
    ///
    /// **The field and the pointer are rooted by name.** Trimming keeps
    /// _oleDataObject because Avalonia's own drag code uses it, but NativeAOT
    /// only answers GetField and GetProperty for members something asked to
    /// keep for reflection — a field present in the object and absent from the
    /// metadata comes back null, and the drop would give up without a word.
    /// The two DynamicDependency attributes are that request.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields,
        "Avalonia.Win32.OleDataObjectToDataTransferWrapper", "Avalonia.Win32")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties,
        "MicroCom.Runtime.MicroComProxyBase", "MicroCom.Runtime")]
    [UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification =
            "Both members are Avalonia's own and are named in the DynamicDependency "
            + "attributes above, so trimming keeps them and NativeAOT keeps their "
            + "metadata. The analyser cannot see that because the type arrives as "
            + "object. Every step is null-guarded, so if a future version renames "
            + "or removes either the result is null and the drop falls back to "
            + "explaining that the files are inside an archive — which "
            + "AvaloniaDropShapeTests asserts is still the shape.")]
    internal static IDataObject? Native(object dataTransfer)
    {
        for (var type = dataTransfer.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("_oleDataObject", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field?.GetValue(dataTransfer) is not { } held) continue;

            // **Not a cast.** What Avalonia holds is its own declaration of the
            // interface — Avalonia.Win32.Win32Com.IDataObject — which has
            // nothing to do with the one declared here beyond describing the
            // same native object. Casting between them returns null, silently,
            // and the whole feature never fires.
            //
            // So the native pointer is asked for the interface this code knows
            // how to speak, which is what QueryInterface is for.
            if (Retype(held) is { } data) return data;
        }

        return null;
    }

    /// <summary>
    /// The native object behind whatever Avalonia holds, seen through the
    /// IDataObject declared in this file.
    ///
    /// **Avalonia holds a MicroCom proxy, not a runtime wrapper.** Its
    /// interop is its own small COM layer, and a proxy there is an ordinary
    /// managed object carrying the pointer in NativePointer. The old route —
    /// Marshal.GetIUnknownForObject — does not find that pointer: it builds a
    /// fresh COM face for the managed proxy itself, which answers no
    /// IDataObject, so it came back null even under the JIT. The property is
    /// the pointer, read directly.
    ///
    /// ComWrappers comes first all the same, so the day Avalonia moves to
    /// source-generated COM this still finds the object without being told.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "See Native: NativePointer is rooted there by name.")]
    private static IDataObject? Retype(object held)
    {
        try
        {
            if (ComWrappers.TryGetComInstance(held, out var unknown))
            {
                try
                {
                    return Wrap(unknown);
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }

            // Borrowed rather than owned: the proxy keeps its own reference for
            // as long as the drag lasts, and Wrap takes one of its own.
            var property = held.GetType().GetProperty("NativePointer", BindingFlags.Instance | BindingFlags.Public);

            return property?.PropertyType == typeof(IntPtr)
                   && property.GetValue(held) is IntPtr pointer && pointer != IntPtr.Zero
                ? Wrap(pointer)
                : null;
        }
        catch (Exception e) when (e is TargetInvocationException or InvalidCastException
                                    or COMException or NotSupportedException)
        {
            // A proxy disposed under the drag throws from its getter, which
            // reflection hands over wrapped.
            return null;
        }
    }

    /// <summary>
    /// A wrapper of this drop's own around a native data object, or null if the
    /// object is not one.
    ///
    /// **Unique rather than cached**, so that <see cref="Release"/> can release it
    /// the moment the drop is done with it. A cached wrapper is released by the
    /// finalizer, on the finalizer's thread — and a shell data object belongs
    /// to the thread that received the drop.
    /// </summary>
    internal static IDataObject? Wrap(IntPtr unknown)
    {
        var wrapper = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);

        if (wrapper is IDataObject data) return data;

        (wrapper as ComObject)?.FinalRelease();

        return null;
    }

    /// <summary>Gives back a reference <see cref="Native"/> or
    /// <see cref="Wrap"/> took.</summary>
    internal static void Release(object? wrapper) => (wrapper as ComObject)?.FinalRelease();

    private static readonly StrategyBasedComWrappers Wrappers = new();

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
        var descriptor = Descriptor(format, -1, TymedHGlobal);

        return data.QueryGetData(in descriptor) == 0;
    }

    private static FormatEtc Descriptor(string name, int index, uint tymed) => new()
    {
        Format = (ushort)RegisterClipboardFormatW(name),
        Aspect = DvaspectContent,
        Index = index,
        Tymed = tymed,
    };

    // ---- names -------------------------------------------------------------

    /// <summary>
    /// Reads FILEGROUPDESCRIPTORW: a count, then that many fixed-size records
    /// whose file name sits at a known offset.
    /// </summary>
    private static List<Described> Names(IDataObject data)
    {
        foreach (var (format, wide) in new[] { (DescriptorW, true), (DescriptorA, false) })
        {
            if (!Available(data, format)) continue;

            var descriptor = Descriptor(format, -1, TymedHGlobal);
            StgMedium medium = default;

            try
            {
                // A refusal tries the other spelling, then gives up. What a
                // refusing call left in the medium is not the caller's to free.
                if (data.GetData(in descriptor, out medium) != 0)
                {
                    medium = default;
                    continue;
                }

                if (medium.Handle == IntPtr.Zero) continue;

                var block = GlobalLock(medium.Handle);
                if (block == IntPtr.Zero) continue;

                try
                {
                    return Describe(block, wide);
                }
                finally
                {
                    GlobalUnlock(medium.Handle);
                }
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
        => [.. Describe(block, wide).Select(d => d.Name)];

    /// <summary>One entry of the descriptor: where it sits, its path, and whether it is a folder.</summary>
    internal readonly record struct Described(int Index, string Name, bool IsFolder);

    /// <summary>
    /// The entries, with whether each is a folder: dwFlags at 0 says the
    /// attributes are filled in (FD_ATTRIBUTES), and dwFileAttributes at 36
    /// says which are folders.
    /// </summary>
    internal static List<Described> Describe(IntPtr block, bool wide)
    {
        var count = Marshal.ReadInt32(block);
        var entries = new List<Described>();

        if (count <= 0) return entries;

        var size = wide ? 592 : 332;
        const int nameOffset = 72;
        const int FdAttributes = 0x4;
        const int Directory = 0x10;

        for (var i = 0; i < count && i < MaxItems; i++)
        {
            var record = block + 4 + (i * size);
            var at = record + nameOffset;

            var name = wide ? Marshal.PtrToStringUni(at, 260) : Marshal.PtrToStringAnsi(at, 260);

            if (name is null) continue;

            // A fixed-width buffer, so the name is padded with nulls rather
            // than ended by the record.
            var end = name.IndexOf('\0');
            if (end >= 0) name = name[..end];

            if (name.Length == 0) continue;

            var flags = Marshal.ReadInt32(record);
            var attributes = Marshal.ReadInt32(record + 36);

            entries.Add(new Described(i, name, (flags & FdAttributes) != 0 && (attributes & Directory) != 0));
        }

        return entries;
    }

    // ---- contents ----------------------------------------------------------

    /// <summary>
    /// Asks for one item's bytes and writes them out.
    ///
    /// A stream where the shell offers one, which is how anything large
    /// arrives, and a memory block where it does not. Both are legal; 7-Zip
    /// uses the stream.
    ///
    /// **The bound holds inside one item too.** Checked only between items, a
    /// stream that never runs dry — a broken source, or a hostile one — writes
    /// until the disk is full before the check is ever reached. A test that
    /// read from the wrong vtable slot did exactly that: four gigabytes of the
    /// same 80 KB before anything noticed.
    /// </summary>
    private static long Write(IDataObject data, int index, string target, long budget)
    {
        var wanted = Descriptor(Contents, index, TymedIStream | TymedHGlobal);
        StgMedium medium = default;

        try
        {
            var hr = data.GetData(in wanted, out medium);

            if (hr != 0)
            {
                medium = default;
                throw new COMException("the drag source would not hand it over", hr);
            }

            using var file = File.Create(target);

            if (medium.Tymed == TymedIStream && medium.Handle != IntPtr.Zero)
                return Pump(medium.Handle, file, budget);

            if (medium.Tymed == TymedHGlobal && medium.Handle != IntPtr.Zero)
            {
                var block = GlobalLock(medium.Handle);

                if (block == IntPtr.Zero) return 0;

                try
                {
                    var size = (long)(ulong)GlobalSize(medium.Handle);

                    if (size > budget || size > Array.MaxLength) throw new IOException(TooMuch);

                    var buffer = new byte[size];

                    Marshal.Copy(block, buffer, 0, (int)size);
                    file.Write(buffer, 0, buffer.Length);

                    return size;
                }
                finally
                {
                    GlobalUnlock(medium.Handle);
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

    /// <summary>
    /// Copies a native IStream out until it runs dry.
    ///
    /// The medium keeps its own reference, which ReleaseStgMedium gives back;
    /// the wrapper here takes one more and gives it back before that happens.
    /// </summary>
    private static long Pump(IntPtr unknown, Stream file, long budget)
    {
        var wrapper = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);

        try
        {
            // E_NOINTERFACE, said as the shell would: a medium that calls
            // itself a stream and is not one is a refusal of this entry, not
            // of the drop.
            if (wrapper is not IStream stream)
                throw new COMException("the drag source's stream is not one", unchecked((int)0x80004002));

            var buffer = new byte[81920];
            var total = 0L;

            while (true)
            {
                int hr;
                uint got;

                unsafe
                {
                    fixed (byte* at = buffer)
                    {
                        hr = stream.Read((IntPtr)at, (uint)buffer.Length, out got);
                    }
                }

                // S_FALSE is the end arriving with this read, not a failure.
                if (hr < 0) throw new COMException("the drag source's stream stopped", hr);
                if (got == 0) break;

                // The count comes from the other side of the drag.
                if (got > buffer.Length)
                    throw new IOException("the drag source's stream claimed more than it was asked for.");

                total += got;

                if (total > budget) throw new IOException(TooMuch);

                file.Write(buffer, 0, (int)got);
            }

            return total;
        }
        finally
        {
            Release(wrapper);
        }
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

    // ---- the native surface ------------------------------------------------

    private const uint TymedHGlobal = 1;
    private const uint TymedIStream = 4;
    private const uint DvaspectContent = 1;

    /// <summary>
    /// FORMATETC, blittable. The clipboard format is a WORD; the padding after
    /// it is the layout's, which Sequential reproduces.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FormatEtc
    {
        public ushort Format;
        public IntPtr TargetDevice;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    /// <summary>
    /// STGMEDIUM, blittable: the union is read as the one pointer-sized member
    /// it is, and the release object as the raw pointer it is.
    ///
    /// **Blittable is what lets LibraryImport and the COM generator carry it.**
    /// The framework's STGMEDIUM holds an object, which is why ReleaseStgMedium
    /// used to be the one DllImport in this assembly (SYSLIB1051).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StgMedium
    {
        public uint Tymed;
        public IntPtr Handle;
        public IntPtr ReleaseWith;
    }

    /// <summary>
    /// IDataObject, vtable order exactly as the SDK declares it — every slot,
    /// the ones this never calls included. A missing slot shifts every one after
    /// it, and QueryGetData then lands on GetDataHere and every drop looks empty.
    /// </summary>
    [GeneratedComInterface]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDataObject
    {
        [PreserveSig] int GetData(in FormatEtc format, out StgMedium medium);
        [PreserveSig] int GetDataHere(in FormatEtc format, ref StgMedium medium);
        [PreserveSig] int QueryGetData(in FormatEtc format);
        [PreserveSig] int GetCanonicalFormatEtc(in FormatEtc format, out FormatEtc canonical);
        [PreserveSig] int SetData(in FormatEtc format, in StgMedium medium, int release);
        [PreserveSig] int EnumFormatEtc(uint direction, out IntPtr formats);
        [PreserveSig] int DAdvise(in FormatEtc format, uint flags, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr advises);
    }

    /// <summary>
    /// IStream, with ISequentialStream's Read and Write first — they come
    /// before Seek in the vtable, and skipping them would make Read a Seek.
    /// </summary>
    [GeneratedComInterface]
    [Guid("0000000c-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IStream
    {
        [PreserveSig] int Read(IntPtr buffer, uint count, out uint read);
        [PreserveSig] int Write(IntPtr buffer, uint count, out uint written);
        [PreserveSig] int Seek(long move, uint origin, out ulong position);
        [PreserveSig] int SetSize(ulong size);
        [PreserveSig] int CopyTo(IntPtr stream, ulong count, out ulong read, out ulong written);
        [PreserveSig] int Commit(uint flags);
        [PreserveSig] int Revert();
        [PreserveSig] int LockRegion(ulong offset, ulong count, uint type);
        [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint type);
        [PreserveSig] int Stat(IntPtr stat, uint flags);
        [PreserveSig] int Clone(out IntPtr stream);
    }

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(ref StgMedium medium);

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
