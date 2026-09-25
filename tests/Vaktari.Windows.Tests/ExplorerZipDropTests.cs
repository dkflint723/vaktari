using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Dragging out of a zip opened in Explorer, with the data object Explorer's
/// own zip view makes rather than one written here.
///
/// **The shell builds the object, not this file.** The zip is opened as a
/// shell folder (CLSID_CompressedFolder, zipfldr.dll), its items enumerated,
/// and the folder asked for their IDataObject through GetUIObjectOf — the call
/// Explorer's view makes when a drag starts on a selection. What VirtualFileDrop
/// then reads is whatever zipfldr put in it: its descriptor records, its flags,
/// its media. <see cref="VirtualFileDropTests"/> proves the conversation with a
/// source that has the SDK right; this proves it with the source that ships.
///
/// **Measured on Windows 11 26200, and printed by every run.** zipfldr offers
/// Shell IDList Array, FileGroupDescriptorW and FileContents (listed as a
/// stream only) — no narrow descriptor and no CF_HDROP. Every descriptor record
/// carries FD_ATTRIBUTES, and a folder's has FILE_ATTRIBUTE_DIRECTORY; asked
/// for a folder's contents it answers E_FAIL, so a folder not known for one is
/// a folder lost. It answers a file's contents as a stream whatever medium is
/// asked for. And its stream answers the short last read that still carries
/// bytes with a success code other than S_OK — the memory stream behind
/// <see cref="NativeDropSource"/> answers it with S_OK, so a pump that took
/// any code but S_OK for "nothing more" passes there and, measured, drops the
/// last 36,160 bytes of the 200,000 here.
///
/// **What it does not stage is the drag itself.** A real drag from Explorer
/// crosses a process, so the drop holds an OLE proxy and every GetData — and
/// every read of the stream it hands back — is a call into Explorer. The
/// second case here comes as close as a test can without driving a mouse: the
/// object is made on one STA thread and read from another through the
/// standard marshaller, which is the same proxy and the same STGMEDIUM
/// marshalling a drag between processes uses. What it cannot reproduce is the
/// other process itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ExplorerZipDropTests(ITestOutputHelper output)
{
    /// <summary>Long enough that a loaded machine never trips it; a hang is
    /// still a red test rather than a stuck run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Items_dragged_out_of_a_zip_in_Explorer_land_on_disk(bool throughAProxy)
    {
        var top = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 + 7)).ToArray();
        var nested = "inside a folder inside the zip"u8.ToArray();

        using var tree = new TempTree();
        var zip = tree.At("archive.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Add(archive, "top.bin", top);
            Add(archive, "inner/note.txt", nested);

            // A folder entry with nothing in it: the name ending in a slash is
            // how a zip says "a folder", and nothing else in the archive
            // implies it exists.
            archive.CreateEntry("empty/");
        }

        var drops = Path.Combine(Path.GetTempPath(), "Vaktari", "drops");
        string? folder = null;

        try
        {
            var roots = throughAProxy ? TakenThroughAProxy(zip) : Taken(zip);

            Assert.NotEmpty(roots);
            folder = Path.GetDirectoryName(roots[0])!;
            Assert.Equal(drops, Path.GetDirectoryName(folder));

            // The roots are the three things dragged, whatever order the zip
            // folder enumerated them in.
            Assert.Equal(
                ["empty", "inner", "top.bin"],
                roots.Select(root => Path.GetFileName(root)).Order(StringComparer.OrdinalIgnoreCase).ToArray());

            Assert.Equal(top, File.ReadAllBytes(Path.Combine(folder, "top.bin")));
            Assert.Equal(nested, File.ReadAllBytes(Path.Combine(folder, "inner", "note.txt")));

            // **A folder arrives as a folder**, the empty one included: nothing
            // else in the drop would make it, so it is here only if its own
            // descriptor entry was read as a folder.
            Assert.True(Directory.Exists(Path.Combine(folder, "empty")), "the empty folder did not arrive as a folder");
            Assert.False(File.Exists(Path.Combine(folder, "empty")), "the empty folder arrived as a file");
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(folder, "empty")));

            Assert.Equal(
                [Path.Combine(folder, "inner", "note.txt")],
                Directory.EnumerateFileSystemEntries(Path.Combine(folder, "inner"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (folder is not null && Path.GetDirectoryName(folder) == drops && Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Made and read on one STA thread: the zip folder's own object,
    /// no proxy between.</summary>
    private IReadOnlyList<string> Taken(string zip)
    {
        IReadOnlyList<string> roots = [];

        OnSta(() =>
        {
            var data = Offered(zip);

            try
            {
                roots = Take(data);
            }
            finally
            {
                Marshal.Release(data);
            }
        });

        return roots;
    }

    /// <summary>
    /// Made on one STA thread and read from another, through the standard
    /// marshaller — so the drop holds a proxy, its GetData is a call into the
    /// other apartment, and the stream in each medium arrives marshalled.
    ///
    /// The making thread waits for the reading one to finish, and a managed
    /// wait on an STA thread pumps, which is what lets the proxy's calls land.
    /// </summary>
    private IReadOnlyList<string> TakenThroughAProxy(string zip)
    {
        IReadOnlyList<string> roots = [];
        using var ready = new ManualResetEventSlim();
        using var done = new ManualResetEvent(false);
        var carried = IntPtr.Zero;
        var made = IntPtr.Zero;

        OnSta(
            () =>
            {
                try
                {
                    made = Offered(zip);

                    Check(CoMarshalInterThreadInterfaceInStream(in DataObjectId, made, out carried),
                        "marshalling the data object to another apartment");
                }
                finally
                {
                    ready.Set();
                }

                try
                {
                    Assert.True(done.WaitOne(Patience), "the reading apartment never finished");
                }
                finally
                {
                    Marshal.Release(made);
                }
            },
            () =>
            {
                try
                {
                    Assert.True(ready.Wait(Patience), "the making apartment never handed the object over");
                    Assert.NotEqual(IntPtr.Zero, carried);

                    Check(CoGetInterfaceAndReleaseStream(carried, in DataObjectId, out var proxy),
                        "unmarshalling the data object");

                    try
                    {
                        // Only a proxy proves anything here: an object that
                        // aggregates the free-threaded marshaller would arrive
                        // as itself, and this case would be the first one again.
                        Assert.NotEqual(made, proxy);

                        roots = Take(proxy);
                    }
                    finally
                    {
                        Marshal.Release(proxy);
                    }
                }
                finally
                {
                    done.Set();
                }
            });

        return roots;
    }

    /// <summary>The zip folder's data object for everything at the top of
    /// the zip, with what it offers written to the test output.</summary>
    private IntPtr Offered(string zip)
    {
        var data = ZipViewDataObject(zip, out var children);

        output.WriteLine($"items the zip folder enumerated: {string.Join(", ", children)}");
        Report(data);

        return data;
    }

    /// <summary>What the drop does with a native data object, handed over in
    /// the shape Avalonia hands it over.</summary>
    private static IReadOnlyList<string> Take(IntPtr data)
    {
        var drag = new VirtualFileDropTests.AvaloniaShapedWrapper(new VirtualFileDropTests.MicroComShapedProxy(data));
        var drop = new VirtualFileDrop();

        Assert.True(drop.Offers(drag), "the drop did not see a descriptor in the shell's own data object");

        return drop.Take(drag);
    }

    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();

        stream.Write(bytes);
    }

    /// <summary>
    /// **Each on an STA thread of its own**, because the compressed folder is
    /// an apartment-threaded shell object and a drop target's thread is an STA.
    /// Everything the shell hands out is made, used and released on the thread
    /// that asked for it.
    /// </summary>
    private static void OnSta(params Action[] bodies)
    {
        var failures = new ExceptionDispatchInfo?[bodies.Length];

        var threads = bodies.Select((body, i) => new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failures[i] = ExceptionDispatchInfo.Capture(e); }
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        foreach (var thread in threads) thread.Join();

        foreach (var failure in failures) failure?.Throw();
    }

    // ---- the shell's side ----------------------------------------------------

    /// <summary>
    /// The IDataObject for every item at the top of the zip, asked of the zip
    /// folder the way Explorer's view asks it when a drag starts: the folder
    /// bound as an IShellFolder, its children enumerated as child ids, and
    /// GetUIObjectOf over all of them. The caller owns the reference.
    /// </summary>
    private static IntPtr ZipViewDataObject(string zip, out List<string> children)
    {
        children = [];

        Check(SHParseDisplayName(zip, IntPtr.Zero, out var pidl, 0, out _), "SHParseDisplayName on the zip");

        var ids = new List<IntPtr>();
        var bound = IntPtr.Zero;
        var listing = IntPtr.Zero;

        try
        {
            Check(SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, in ShellFolderId, out bound), "binding the zip as a folder");

            var folder = (IShellFolder)Wrappers.GetOrCreateObjectForComInstance(bound, CreateObjectFlags.UniqueInstance);

            try
            {
                Check(folder.EnumObjects(IntPtr.Zero, ContentsFolders | ContentsNonFolders | ContentsHidden, out listing),
                    "enumerating the zip folder");

                // S_FALSE with nothing listed is a folder with nothing in it,
                // which this zip is not — the assertion below says so.
                if (listing != IntPtr.Zero)
                {
                    var walk = (IEnumIdList)Wrappers.GetOrCreateObjectForComInstance(listing, CreateObjectFlags.UniqueInstance);

                    try
                    {
                        while (walk.Next(1, out var child, out var got) == 0 && got == 1)
                        {
                            ids.Add(child);
                            children.Add(DisplayName(folder, child));
                        }
                    }
                    finally
                    {
                        ((ComObject)(object)walk).FinalRelease();
                    }
                }

                Assert.Equal(3, ids.Count);

                var handles = ids.ToArray();

                unsafe
                {
                    fixed (IntPtr* first = handles)
                    {
                        Check(folder.GetUIObjectOf(IntPtr.Zero, (uint)handles.Length, (IntPtr)first,
                                in DataObjectId, IntPtr.Zero, out var data),
                            "GetUIObjectOf(IID_IDataObject) on the zip's items");

                        return data;
                    }
                }
            }
            finally
            {
                ((ComObject)(object)folder).FinalRelease();
            }
        }
        finally
        {
            foreach (var id in ids) CoTaskMemFree(id);
            if (listing != IntPtr.Zero) Marshal.Release(listing);
            if (bound != IntPtr.Zero) Marshal.Release(bound);
            CoTaskMemFree(pidl);
        }
    }

    private static string DisplayName(IShellFolder folder, IntPtr child)
    {
        var buffer = Marshal.AllocCoTaskMem(1024);

        try
        {
            // STRRET is a type, then a union; SHGDN_INFOLDER|SHGDN_FORPARSING
            // names the entry the way the zip does.
            if (folder.GetDisplayNameOf(child, 0x8001, buffer) != 0) return "?";

            var text = Marshal.AllocCoTaskMem(260 * sizeof(char));

            try
            {
                return StrRetToBufW(buffer, child, text, 260) == 0
                    ? Marshal.PtrToStringUni(text) ?? "?"
                    : "?";
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// Writes what the shell's object offers — every format it enumerates, and
    /// each descriptor record's flags, attributes and name — so what zipfldr
    /// actually said is in the test output, not only whether the drop agreed.
    /// </summary>
    private void Report(IntPtr unknown)
    {
        var data = (IDescribedDataObject)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);

        try
        {
            if (data.EnumFormatEtc(1, out var listing) == 0 && listing != IntPtr.Zero)
            {
                var formats = (IEnumFormatEtc)Wrappers.GetOrCreateObjectForComInstance(listing, CreateObjectFlags.UniqueInstance);

                try
                {
                    while (formats.Next(1, out var format, out var got) == 0 && got == 1)
                        output.WriteLine($"offers {FormatName(format.Format)} (cf {format.Format}) index {format.Index} tymed {format.Tymed}");
                }
                finally
                {
                    ((ComObject)(object)formats).FinalRelease();
                    Marshal.Release(listing);
                }
            }

            ReportMedia(data);

            var wanted = new FormatEtc
            {
                Format = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW"),
                Aspect = 1,
                Index = -1,
                Tymed = 1,
            };

            if (data.GetData(in wanted, out var medium) != 0)
            {
                output.WriteLine("FileGroupDescriptorW refused");
                return;
            }

            try
            {
                var block = GlobalLock(medium.Handle);
                var count = Marshal.ReadInt32(block);

                for (var i = 0; i < count; i++)
                {
                    var record = block + 4 + (i * 592);

                    output.WriteLine(
                        $"descriptor {i}: flags 0x{Marshal.ReadInt32(record):X}"
                        + $" attributes 0x{Marshal.ReadInt32(record + 36):X}"
                        + $" size {(long)(((ulong)(uint)Marshal.ReadInt32(record + 64) << 32) | (uint)Marshal.ReadInt32(record + 68))}"
                        + $" name '{Marshal.PtrToStringUni(record + 72)}'");
                }

                GlobalUnlock(medium.Handle);
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        finally
        {
            ((ComObject)(object)data).FinalRelease();
        }
    }

    /// <summary>
    /// Which medium each item's contents come back in, for each medium asked
    /// for — what EnumFormatEtc lists is not the whole of what GetData answers.
    /// </summary>
    private void ReportMedia(IDescribedDataObject data)
    {
        var contents = (ushort)RegisterClipboardFormatW("FileContents");

        for (var index = 0; index < 2; index++)
        {
            foreach (var (asked, label) in new[] { (5u, "stream|memory"), (4u, "stream"), (1u, "memory") })
            {
                var wanted = new FormatEtc { Format = contents, Aspect = 1, Index = index, Tymed = asked };
                var hr = data.GetData(in wanted, out var medium);

                output.WriteLine(
                    $"FileContents {index} asked as {label}: hr 0x{hr:X8}"
                    + (hr == 0 ? $", answered tymed {medium.Tymed}" : ""));

                if (hr == 0) ReleaseStgMedium(ref medium);
            }
        }
    }

    private static string FormatName(ushort format)
    {
        var name = Marshal.AllocCoTaskMem(256 * sizeof(char));

        try
        {
            var length = GetClipboardFormatNameW(format, name, 256);

            return length > 0 ? Marshal.PtrToStringUni(name, length) : $"predefined {format}";
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) Assert.Fail($"{what} failed: 0x{hr:X8}");
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid DataObjectId = new("0000010e-0000-0000-C000-000000000046");

    private const uint ContentsFolders = 0x20;
    private const uint ContentsNonFolders = 0x40;
    private const uint ContentsHidden = 0x80;

    // ---- declarations, written out here rather than borrowed ---------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct FormatEtc
    {
        public ushort Format;
        public IntPtr TargetDevice;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StgMedium
    {
        public uint Tymed;
        public IntPtr Handle;
        public IntPtr ReleaseWith;
    }

    /// <summary>IShellFolder as shobjidl_core.h has it, slot for slot.</summary>
    [GeneratedComInterface]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    internal partial interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd, IntPtr pbc, IntPtr pszDisplayName, out uint pchEaten,
            out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner, uint cidl, IntPtr apidl, in Guid riid, IntPtr rgfReserved,
            out IntPtr ppv);

        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, IntPtr pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [GeneratedComInterface]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    internal partial interface IEnumIdList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    /// <summary>IDataObject, for reporting what the shell's object offers.</summary>
    [GeneratedComInterface]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    internal partial interface IDescribedDataObject
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

    [GeneratedComInterface]
    [Guid("00000103-0000-0000-C000-000000000046")]
    internal partial interface IEnumFormatEtc
    {
        [PreserveSig] int Next(uint celt, out FormatEtc rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToObject(
        IntPtr psf, IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);

    [LibraryImport("shlwapi.dll")]
    private static partial int StrRetToBufW(IntPtr pstr, IntPtr pidl, IntPtr pszBuf, uint cchBuf);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport("ole32.dll")]
    private static partial int CoMarshalInterThreadInterfaceInStream(in Guid riid, IntPtr pUnk, out IntPtr ppStm);

    [LibraryImport("ole32.dll")]
    private static partial int CoGetInterfaceAndReleaseStream(IntPtr pStm, in Guid iid, out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(ref StgMedium medium);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("user32.dll")]
    private static partial int GetClipboardFormatNameW(uint format, IntPtr name, int max);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);
}
