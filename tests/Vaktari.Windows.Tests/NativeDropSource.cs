using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A drag source that offers files the way 7-Zip and Explorer's zip view do:
/// FileGroupDescriptorW for the names and FileContents, one index at a time,
/// for the bytes — seen from the other side only as a native pointer.
///
/// **Its own declarations, deliberately not the ones under test.** The
/// interface and both structs are written out again here from the SDK, so a
/// slot or a field that VirtualFileDrop gets wrong meets a server that has it
/// right, and the drop comes back empty instead of agreeing with itself.
///
/// The streams are the shell's own (SHCreateMemStream), so the IStream half of
/// the conversation is with a real native object and not with this file.
/// </summary>
[SupportedOSPlatform("windows")]
[GeneratedComClass]
internal sealed partial class NativeDropSource : NativeDropSource.IServedDataObject
{
    private readonly (string Name, byte[] Bytes, bool AsStream)[] _entries;
    private readonly ushort _descriptorW = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW");
    private readonly ushort _contents = (ushort)RegisterClipboardFormatW("FileContents");

    public NativeDropSource(params (string Name, byte[] Bytes, bool AsStream)[] entries)
        => _entries = entries;

    public int GetData(in FormatEtc format, out StgMedium medium)
    {
        medium = default;

        if (!Offered(format, out var tymed)) return DvEFormatEtc;

        var entry = format.Format == _contents ? _entries[format.Index] : default;

        medium.Tymed = tymed;
        medium.Handle = format.Format == _descriptorW
            ? Global(Descriptor())
            : tymed == TymedIStream
                ? SHCreateMemStream(entry.Bytes, (uint)entry.Bytes.Length)
                : Global(entry.Bytes);

        return medium.Handle == IntPtr.Zero ? EOutOfMemory : 0;
    }

    public int QueryGetData(in FormatEtc format) => Offered(format, out _) ? 0 : DvEFormatEtc;

    public int GetDataHere(in FormatEtc format, ref StgMedium medium) => ENotImpl;
    public int GetCanonicalFormatEtc(in FormatEtc format, out FormatEtc canonical)
    {
        canonical = default;
        return ENotImpl;
    }
    public int SetData(in FormatEtc format, in StgMedium medium, int release) => ENotImpl;
    public int EnumFormatEtc(uint direction, out IntPtr formats)
    {
        formats = IntPtr.Zero;
        return ENotImpl;
    }
    public int DAdvise(in FormatEtc format, uint flags, IntPtr sink, out uint connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }
    public int DUnadvise(uint connection) => OleEAdviseNotSupported;
    public int EnumDAdvise(out IntPtr advises)
    {
        advises = IntPtr.Zero;
        return OleEAdviseNotSupported;
    }

    /// <summary>
    /// What a shell folder answers: the descriptor as memory for index -1, and
    /// each item's contents by its own index in the one medium it offers.
    /// </summary>
    private bool Offered(in FormatEtc format, out uint tymed)
    {
        tymed = 0;

        if (format.Aspect != DvaspectContent || format.TargetDevice != IntPtr.Zero) return false;

        if (format.Format == _descriptorW && format.Index == -1)
            tymed = TymedHGlobal;
        else if (format.Format == _contents && format.Index >= 0 && format.Index < _entries.Length)
            tymed = _entries[format.Index].AsStream ? TymedIStream : TymedHGlobal;

        return tymed != 0 && (format.Tymed & tymed) != 0;
    }

    /// <summary>FILEGROUPDESCRIPTORW: a UINT count, then a 592-byte record per
    /// item with its name at offset 72.</summary>
    private byte[] Descriptor()
    {
        var bytes = new byte[4 + (_entries.Length * 592)];

        BitConverter.GetBytes(_entries.Length).CopyTo(bytes, 0);

        for (var i = 0; i < _entries.Length; i++)
            System.Text.Encoding.Unicode.GetBytes(_entries[i].Name).CopyTo(bytes, 4 + (i * 592) + 72);

        return bytes;
    }

    /// <summary>Movable global memory, which is what ReleaseStgMedium frees
    /// when the receiver is done with it.</summary>
    private static IntPtr Global(byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)Math.Max(bytes.Length, 1));
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        Marshal.Copy(bytes, 0, GlobalLock(handle), bytes.Length);
        GlobalUnlock(handle);

        return handle;
    }

    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int EOutOfMemory = unchecked((int)0x8007000E);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const uint TymedHGlobal = 1;
    private const uint TymedIStream = 4;
    private const uint DvaspectContent = 1;
    private const uint GmemMoveable = 2;

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

    /// <summary>IDataObject as objidl.h has it, slot for slot.</summary>
    [GeneratedComInterface]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IServedDataObject
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

    [LibraryImport("shlwapi.dll")]
    private static partial IntPtr SHCreateMemStream(byte[] init, uint size);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, UIntPtr size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);
}
