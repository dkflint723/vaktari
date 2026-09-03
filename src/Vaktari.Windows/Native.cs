using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vaktari.Windows;

/// <summary>
/// The Win32 surface this assembly needs, in one place.
///
/// **`LibraryImport`, never `DllImport`.** WINDOWS.md §6 is explicit: the
/// project publishes with `PublishAot=true` and turns the AOT analyser on for
/// every project, with warnings as errors. Source-generated P/Invoke is
/// AOT-clean; the reflection-based marshaller behind `DllImport` is not, and
/// would fail at runtime rather than at build time.
///
/// Nothing here is reached for by preference. Each block exists because the BCL
/// offers no equivalent, or offers one this project cannot take:
///
/// - **Registry** — there is a managed API, but only on the `net10.0-windows`
///   TFM this project does not use, and adopting it would cost the free Linux
///   compile-check (§9). Two `RegGetValueW` calls are cheaper than that trade.
/// - **The Recycle Bin** — no BCL API at all.
/// - **Junctions** — `Directory.CreateSymbolicLink` needs a privilege an
///   ordinary user does not hold; the reparse-point ioctl needs none.
/// - **Network connections and DNS-SD** — the redirector and the mDNS responder
///   are both services already running on the machine, and asking them is the
///   Windows shape of what gvfs and avahi do on Linux.
/// </summary>
internal static partial class Native
{
    // ---- Registry ----------------------------------------------------------

    internal static readonly nint HKEY_CURRENT_USER = unchecked((nint)(long)0x80000001);

    internal const uint RRF_RT_REG_DWORD = 0x00000010;
    internal const uint RRF_RT_REG_BINARY = 0x00000008;
    internal const uint KEY_READ = 0x00020019;
    internal const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
    internal const int ERROR_SUCCESS = 0;

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegGetValue(
        nint hkey, string subKey, string value, uint flags,
        nint type, out uint data, ref uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegGetValueBinary(
        nint hkey, string subKey, string value, uint flags,
        nint type, Span<byte> data, ref uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(
        nint hkey, string subKey, uint options, uint desired, out nint result);

    /// <summary>
    /// Called with <c>asynchronous: false</c>, which blocks the calling thread
    /// until the key changes. That is why the theme provider gives it a thread
    /// of its own — and a background one, so a blocked wait cannot hold the
    /// process open at exit.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegNotifyChangeKeyValue")]
    internal static partial int RegNotifyChangeKeyValue(
        nint hkey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    internal static partial int RegCloseKey(nint hkey);

    /// <summary>A DWORD from HKCU, or null if it is not there.</summary>
    internal static uint? ReadDword(string subKey, string value)
    {
        uint data = 0;
        var size = (uint)sizeof(uint);

        var status = RegGetValue(
            HKEY_CURRENT_USER, subKey, value, RRF_RT_REG_DWORD, 0, out data, ref size);

        return status == ERROR_SUCCESS ? data : null;
    }

    /// <summary>
    /// A REG_BINARY from HKCU, or null if it is not there.
    ///
    /// One call with a fixed buffer rather than the usual size-then-read dance:
    /// the only caller wants a 36-byte structure, and a value too big to fit
    /// comes back as ERROR_MORE_DATA, which lands on the same null as an absent
    /// one — "the desktop did not say" either way.
    /// </summary>
    internal static byte[]? ReadBinary(string subKey, string value, int max = 256)
    {
        var buffer = new byte[max];
        var size = (uint)buffer.Length;

        var status = RegGetValueBinary(
            HKEY_CURRENT_USER, subKey, value, RRF_RT_REG_BINARY, 0, buffer, ref size);

        return status == ERROR_SUCCESS && size <= buffer.Length ? buffer[..(int)size] : null;
    }

    // ---- Shell file operations ---------------------------------------------

    internal const uint FO_DELETE = 0x0003;

    /// <summary>Recycle rather than destroy. Without it this is a permanent delete.</summary>
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// Warn before something is destroyed rather than recycled. **Partially
    /// overrides FOF_NOCONFIRMATION, which is the entire point** — see
    /// WindowsFileOperations.Trash.
    /// </summary>
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Default packing, which is correct on x64 and ARM64. The
    /// <c>#include &lt;pshpack1.h&gt;</c> around this structure in shellapi.h
    /// applies to 32-bit builds only; this application publishes 64-bit.
    /// The string fields are raw pointers so the structure stays blittable and
    /// <c>LibraryImport</c> will accept it — see <see cref="DoubleNullTerminated"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHFILEOPSTRUCTW
    {
        internal nint hwnd;
        internal uint wFunc;
        internal nint pFrom;
        internal nint pTo;
        internal ushort fFlags;
        internal int fAnyOperationsAborted;
        internal nint hNameMappings;
        internal nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    internal static partial int SHFileOperation(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// The list format SHFileOperation wants: entries separated by NUL and the
    /// whole thing terminated by a second NUL.
    ///
    /// <see cref="Marshal.StringToHGlobalUni"/> appends one NUL of its own, so
    /// the string handed to it ends with a single explicit NUL and the pair
    /// comes out right. Getting this wrong reads past the buffer.
    /// </summary>
    internal static nint DoubleNullTerminated(IReadOnlyList<string> paths)
        => Marshal.StringToHGlobalUni(string.Join('\0', paths) + '\0');

    // ---- Junctions ---------------------------------------------------------

    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_ALL = 0x00000007;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    internal static readonly nint INVALID_HANDLE_VALUE = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFile(
        string fileName, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        nint device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>
    /// Points an existing, empty directory at <paramref name="target"/>, making
    /// it a junction.
    ///
    /// **This exists because the BCL only offers the kind of link that needs a
    /// privilege.** Directory.CreateSymbolicLink requires
    /// SeCreateSymbolicLinkPrivilege — Developer Mode, or elevation — and
    /// throws "a required privilege is not held by the client" without it. A
    /// junction needs nothing but write access to the directory, which is why
    /// `mklink /J` works for an ordinary user and why Windows uses junctions
    /// for its own compatibility links. Copying a folder that contains one is
    /// not an exotic case (node_modules, package caches, the legacy profile
    /// links), so it cannot be allowed to fail on a machine in its default
    /// configuration.
    ///
    /// The structure is REPARSE_DATA_BUFFER in its mount-point form: an
    /// eight-byte header, four USHORT offsets and lengths, then both names back
    /// to back. ReparseDataLength counts everything after the header, and the
    /// two NUL terminators sit in the buffer without being counted in either
    /// length — the usual way to get this wrong.
    /// </summary>
    internal static void CreateJunction(string path, string target)
    {
        var full = Path.GetFullPath(target);

        // A drive root keeps its separator; anything else loses a trailing one.
        var print = full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;

        // The object-manager name the reparse point actually stores.
        var substitute = @"\??\" + print;

        var names = (substitute.Length + 1 + print.Length + 1) * 2;
        var buffer = new byte[16 + names];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)(8 + names));
        // Bytes 6..8 are Reserved, and are already zero.
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)(substitute.Length * 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)((substitute.Length + 1) * 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)(print.Length * 2));

        var text = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(16));
        substitute.CopyTo(text);
        print.CopyTo(text[(substitute.Length + 1)..]);

        // BACKUP_SEMANTICS to open a directory at all; OPEN_REPARSE_POINT so the
        // handle is the directory itself rather than whatever it may point at.
        var handle = CreateFile(
            path, GENERIC_WRITE, FILE_SHARE_ALL, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);

        if (handle == INVALID_HANDLE_VALUE)
            throw new IOException(
                $"Could not open '{path}' to make it a junction.",
                Marshal.GetHRForLastWin32Error());

        try
        {
            unsafe
            {
                fixed (byte* data = buffer)
                {
                    if (!DeviceIoControl(
                            handle, FSCTL_SET_REPARSE_POINT,
                            (nint)data, (uint)buffer.Length, 0, 0, out _, 0))
                        throw new IOException(
                            $"Could not point '{path}' at '{print}'.",
                            Marshal.GetHRForLastWin32Error());
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- Ejecting a volume -------------------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;

    /// <summary>
    /// The five volume control codes, DERIVED rather than transcribed.
    ///
    /// CTL_CODE is a documented bit layout, and writing 0x002D4808 by hand is
    /// one typo away from sending a different command to a device — a class of
    /// mistake that fails at runtime, on hardware, with a meaningless error.
    /// </summary>
    internal static uint CtlCode(uint device, uint function, uint method, uint access)
        => (device << 16) | (access << 14) | (function << 2) | method;

    private const uint FILE_DEVICE_FILE_SYSTEM = 0x00000009;
    private const uint FILE_DEVICE_MASS_STORAGE = 0x0000002D;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_READ_ACCESS = 1;

    internal static readonly uint FSCTL_LOCK_VOLUME =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 6, METHOD_BUFFERED, FILE_ANY_ACCESS);

    internal static readonly uint FSCTL_DISMOUNT_VOLUME =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 8, METHOD_BUFFERED, FILE_ANY_ACCESS);

    internal static readonly uint IOCTL_STORAGE_GET_DEVICE_NUMBER =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS);

    internal static readonly uint IOCTL_STORAGE_MEDIA_REMOVAL =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0201, METHOD_BUFFERED, FILE_READ_ACCESS);

    internal static readonly uint IOCTL_STORAGE_EJECT_MEDIA =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0202, METHOD_BUFFERED, FILE_READ_ACCESS);

    internal const uint FILE_DEVICE_CD_ROM = 0x00000002;
    internal const uint FILE_DEVICE_DISK = 0x00000007;

    /// <summary>Which physical device a volume sits on. Two volumes sharing a
    /// DeviceNumber are partitions of one stick, and one cannot be unplugged
    /// without the other.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_NUMBER
    {
        internal uint DeviceType;
        internal uint DeviceNumber;
        internal int PartitionNumber;
    }

    // cfgmgr32 rather than setupapi: every parameter is a Guid, a uint or a
    // pointer, so the source generator takes all of it with no marshaller and
    // no analyser suppression under TreatWarningsAsErrors — and it avoids
    // SP_DEVICE_INTERFACE_DETAIL_DATA_W, whose cbSize must be hard-coded to a
    // value that differs by architecture and which fails as a bare
    // ERROR_INVALID_PARAMETER when it is wrong.

    internal const uint CR_SUCCESS = 0;
    internal const uint CR_REMOVE_VETOED = 0x17;

    internal const uint CM_GETIDLIST_FILTER_NONE = 0;
    internal const uint DN_REMOVABLE = 0x00004000;

    internal static readonly Guid GUID_DEVINTERFACE_DISK =
        new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>DEVPKEY_Device_InstanceId — the string CM_Locate_DevNodeW takes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        internal Guid Fmtid;
        internal uint Pid;
    }

    internal static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new()
    {
        Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        Pid = 256,
    };

    /// <summary>Why the system refused. Only the two that name an application
    /// are ever shown to a person — the rest describe the device tree, and
    /// PNP_VetoOutstandingOpen's "name" is a device instance path.</summary>
    internal enum PnpVetoType
    {
        TypeUnknown = 0,
        LegacyDevice,
        PendingClose,
        WindowsApp,
        WindowsService,
        OutstandingOpen,
        Device,
        Driver,
        IllegalDeviceRequest,
        InsufficientPower,
        NonDisableable,
        LegacyDriver,
        InsufficientRights,
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    internal static partial uint CM_Get_Device_Interface_List_Size(
        out uint length, in Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    internal static partial uint CM_Get_Device_Interface_List(
        in Guid interfaceClass, nint deviceId, nint buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    internal static partial uint CM_Get_Device_Interface_Property(
        nint deviceInterface, in DEVPROPKEY key, out uint propertyType,
        nint buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Locate_DevNode(
        out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static partial uint CM_Get_DevNode_Status(
        out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    internal static partial uint CM_Request_Device_Eject(
        uint devInst, out PnpVetoType vetoType, nint vetoName, uint nameLength, uint flags);

    // ---- Mounting a disk image ---------------------------------------------

    /// <summary>
    /// Which provider handles the image.
    ///
    /// **Always named explicitly, never DEVICE_UNKNOWN.** The catch-all was
    /// measured selecting its provider by FILE EXTENSION rather than content:
    /// a genuine ISO named .img came back ERROR_VIRTDISK_PROVIDER_NOT_FOUND.
    /// It only looks like content sniffing when the extension happens to agree.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VIRTUAL_STORAGE_TYPE
    {
        internal uint DeviceId;
        internal Guid VendorId;
    }

    internal const uint VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;

    internal static readonly Guid VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT =
        new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    /// <summary>
    /// The access masks, and they are NOT interchangeable — 0x00080000 is
    /// GET_INFO, not DETACH. Opening with the wrong one attaches fine and then
    /// fails the detach with a bare ERROR_ACCESS_DENIED, which reads like a
    /// permissions problem and is actually a typo. Found by mounting a real
    /// image and failing to put it away again.
    ///
    /// READ is the union ATTACH_RO | DETACH | GET_INFO, so a handle opened to
    /// mount can also detach.
    /// </summary>
    internal const uint VIRTUAL_DISK_ACCESS_ATTACH_RO = 0x00010000;
    internal const uint VIRTUAL_DISK_ACCESS_DETACH = 0x00040000;
    internal const uint VIRTUAL_DISK_ACCESS_GET_INFO = 0x00080000;
    internal const uint VIRTUAL_DISK_ACCESS_READ = 0x000D0000;

    internal const uint OPEN_VIRTUAL_DISK_FLAG_NONE = 0;

    /// <summary>
    /// **PERMANENT_LIFETIME, deliberately.** Without it the image detaches when
    /// the handle closes — so a mount would evaporate the moment the call
    /// returned, or survive only as long as Vaktari did. Explorer's own Mount
    /// verb leaves an image attached after the window closes, and a file
    /// manager that silently unmounted someone's ISO on exit would be losing
    /// their work in progress.
    /// </summary>
    internal const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x00000001;

    /// <summary>
    /// **0x4, and getting this wrong fails SILENTLY.** 0x10 is an ignored bit,
    /// so AttachVirtualDisk still returns success — and the attach stays tied to
    /// the handle, so closing it detaches the image immediately. Measured: with
    /// 0x1|0x10 the letter exists while the handle is open and is gone the
    /// instant it closes, which reads as "Windows gave it no drive letter" and
    /// blames the operating system for a constant.
    /// </summary>
    internal const uint ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME = 0x00000004;

    internal const uint GET_STORAGE_DEPENDENCY_FLAG_HOST_VOLUMES = 0x00000001;
    internal const uint GET_STORAGE_DEPENDENCY_FLAG_DISK_HANDLE = 0x00000002;

    /// <summary>
    /// STORAGE_DEPENDENCY_INFO with its version-2 entry inline — enough of the
    /// shape to reach DependentVolumeRelativePath, which is the image file a
    /// mounted volume came from.
    ///
    /// Pointer fields rather than marshalled strings: the struct crosses a
    /// LibraryImport boundary, so every field must be blittable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEPENDENCY_INFO_TYPE_2
    {
        internal uint DependencyTypeFlags;
        internal uint ProviderSpecificFlags;
        internal VIRTUAL_STORAGE_TYPE VirtualStorageType;
        internal uint AncestorLevel;
        internal nint DependencyDeviceName;
        internal nint HostVolumeName;
        internal nint DependentVolumeName;
        internal nint DependentVolumeRelativePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEPENDENCY_INFO_V2
    {
        internal uint Version;
        internal uint NumberEntries;
        internal STORAGE_DEPENDENCY_INFO_TYPE_2 FirstEntry;
    }

    internal const uint STORAGE_DEPENDENCY_INFO_VERSION_2 = 2;

    [LibraryImport("virtdisk.dll", EntryPoint = "GetStorageDependencyInformation")]
    internal static partial int GetStorageDependencyInformation(
        nint objectHandle, uint flags, uint infoSize, nint info, out uint sizeUsed);

    internal const uint DETACH_VIRTUAL_DISK_FLAG_NONE = 0;

    internal const int ERROR_FILE_CORRUPT = 1392;
    internal const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_VIRTDISK_PROVIDER_NOT_FOUND = unchecked((int)0xC03A0014);

    [LibraryImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int OpenVirtualDisk(
        in VIRTUAL_STORAGE_TYPE storageType, string path,
        uint accessMask, uint flags, nint parameters, out nint handle);

    [LibraryImport("virtdisk.dll", EntryPoint = "AttachVirtualDisk")]
    internal static partial int AttachVirtualDisk(
        nint handle, nint securityDescriptor, uint flags,
        uint providerSpecificFlags, nint parameters, nint overlapped);

    [LibraryImport("virtdisk.dll", EntryPoint = "DetachVirtualDisk")]
    internal static partial int DetachVirtualDisk(
        nint handle, uint flags, uint providerSpecificFlags);

    [LibraryImport("virtdisk.dll", EntryPoint = "GetVirtualDiskPhysicalPath")]
    internal static partial int GetVirtualDiskPhysicalPath(
        nint handle, ref uint pathSizeInBytes, nint path);

    // ---- The desktop's UI font ---------------------------------------------

    internal const uint SPI_GETICONTITLELOGFONT = 0x001F;

    /// <summary>
    /// <c>ushort</c> rather than <c>char</c>, and that is forced rather than
    /// stylistic. <c>char</c> is not blittable — the runtime marshaller has a
    /// conversion for it — so a structure containing one cannot cross a
    /// <c>LibraryImport</c> boundary without disabling runtime marshalling for
    /// the whole assembly (SYSLIB1051). UTF-16 code units are what the field
    /// holds anyway; <see cref="MemoryMarshal.Cast{TFrom,TTo}(ReadOnlySpan{TFrom})"/>
    /// reads them back as text for free.
    /// </summary>
    [InlineArray(32)]
    internal struct FaceName
    {
        private ushort _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LOGFONTW
    {
        internal int lfHeight;
        internal int lfWidth;
        internal int lfEscapement;
        internal int lfOrientation;
        internal int lfWeight;
        internal byte lfItalic;
        internal byte lfUnderline;
        internal byte lfStrikeOut;
        internal byte lfCharSet;
        internal byte lfOutPrecision;
        internal byte lfClipPrecision;
        internal byte lfQuality;
        internal byte lfPitchAndFamily;
        internal FaceName lfFaceName;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(
        uint action, uint param, ref LOGFONTW data, uint winIni);

    // ---- Network connections (mpr.dll) -------------------------------------
    //
    // The Windows answer to gvfs: the redirector already speaks SMB and, with
    // the WebClient service, WebDAV, and exposes both as ordinary paths. So the
    // whole of WindowsRemoteMounts is asking mpr who is connected and telling it
    // to connect one more, exactly as LinuxRemoteMounts reads gvfs and shells to
    // gio.

    internal const uint RESOURCE_CONNECTED = 0x00000001;
    internal const uint RESOURCETYPE_DISK = 0x00000001;

    internal const uint RESOURCEUSAGE_CONNECTABLE = 0x00000001;
    internal const uint RESOURCEDISPLAYTYPE_SHARE = 0x00000003;

    /// <summary>Prompt for credentials rather than failing, using Windows' own dialog.</summary>
    internal const uint CONNECT_INTERACTIVE = 0x00000008;
    internal const uint CONNECT_PROMPT = 0x00000010;

    internal const int NO_ERROR = 0;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_PASSWORD = 86;
    internal const int ERROR_LOGON_FAILURE = 1326;
    internal const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    internal const int ERROR_BAD_NET_NAME = 67;
    internal const int ERROR_BAD_NETPATH = 53;
    internal const int ERROR_CANCELLED = 1223;
    internal const int ERROR_OPEN_FILES = 2401;
    internal const int ERROR_DEVICE_IN_USE = 2404;

    /// <summary>
    /// NETRESOURCEW. The four string fields are pointers rather than marshalled
    /// strings so the structure stays blittable and LibraryImport will take it,
    /// the same trade SHFILEOPSTRUCTW makes above.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NETRESOURCEW
    {
        internal uint dwScope;
        internal uint dwType;
        internal uint dwDisplayType;
        internal uint dwUsage;
        internal nint lpLocalName;
        internal nint lpRemoteName;
        internal nint lpComment;
        internal nint lpProvider;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetOpenEnumW")]
    internal static partial int WNetOpenEnum(
        uint scope, uint type, uint usage, nint netResource, out nint handle);

    [LibraryImport("mpr.dll", EntryPoint = "WNetEnumResourceW")]
    internal static partial int WNetEnumResource(
        nint handle, ref uint count, nint buffer, ref uint bufferSize);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCloseEnum")]
    internal static partial int WNetCloseEnum(nint handle);

    /// <summary>
    /// Connects a share. A null <c>lpLocalName</c> makes it deviceless — a
    /// connection to `\\server\share` with no drive letter — which is what
    /// WindowsRemoteMounts wants, so a connection it makes does not also appear
    /// in Places as a lettered network drive.
    /// </summary>
    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetAddConnection2(
        ref NETRESOURCEW netResource, string? password, string? username, uint flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetCancelConnection2(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    // ---- DNS Service Discovery (dnsapi.dll) --------------------------------
    //
    // The Windows equivalent of asking avahi: Windows 10 has run its own mDNS
    // responder since 1703, and DnsServiceBrowse is the documented way to ask
    // it. INetworkDiscovery's own rule -- do not implement mDNS, ask the
    // responder that has been listening since boot -- therefore holds here just
    // as it does on Linux.

    internal const uint DNS_REQUEST_PENDING = 9506;
    internal const uint DNS_QUERY_REQUEST_VERSION1 = 1;

    internal const ushort DNS_TYPE_A = 0x0001;
    internal const ushort DNS_TYPE_PTR = 0x000C;
    internal const ushort DNS_TYPE_AAAA = 0x001C;
    internal const ushort DNS_TYPE_SRV = 0x0021;

    /// <summary>
    /// DNS_SERVICE_BROWSE_REQUEST. pBrowseCallback is a function pointer to an
    /// <c>[UnmanagedCallersOnly]</c> static, which is what keeps this AOT-clean:
    /// a managed delegate marshalled to native would need the reflection-based
    /// marshaller this assembly does not have.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DNS_SERVICE_BROWSE_REQUEST
    {
        internal uint Version;
        internal uint InterfaceIndex;
        internal nint QueryName;
        internal nint pBrowseCallback;
        internal nint pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DNS_SERVICE_CANCEL
    {
        internal nint reserved;
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceBrowse")]
    internal static partial int DnsServiceBrowse(
        ref DNS_SERVICE_BROWSE_REQUEST request, ref DNS_SERVICE_CANCEL cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceBrowseCancel")]
    internal static partial int DnsServiceBrowseCancel(ref DNS_SERVICE_CANCEL cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFree")]
    internal static partial void DnsFree(nint data, int freeType);

    /// <summary>
    /// DNS_SERVICE_RESOLVE_REQUEST — turns one instance name into a host, an
    /// address and a port.
    ///
    /// **A second callback API rather than a DnsQuery for the SRV record**,
    /// which was the first attempt and does not work: DnsQuery_W is the unicast
    /// resolver, and it answers nothing for a `.local` name however the flags
    /// are set. Measured against a network full of Chromecasts — the browse
    /// found every instance and every SRV lookup came back empty. Only
    /// DnsServiceResolve goes to the multicast responder.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DNS_SERVICE_RESOLVE_REQUEST
    {
        internal uint Version;
        internal uint InterfaceIndex;
        internal nint QueryName;
        internal nint pResolveCompletionCallback;
        internal nint pQueryContext;
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceResolve")]
    internal static partial int DnsServiceResolve(
        ref DNS_SERVICE_RESOLVE_REQUEST request, ref DNS_SERVICE_CANCEL cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceResolveCancel")]
    internal static partial int DnsServiceResolveCancel(ref DNS_SERVICE_CANCEL cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceFreeInstance")]
    internal static partial void DnsServiceFreeInstance(nint instance);

    /// <summary>
    /// Field offsets into DNS_SERVICE_INSTANCE on x64. Read by hand for the
    /// same reason DNS_RECORDW is: only three of its eleven fields are wanted,
    /// and two of the rest are parallel string arrays.
    ///
    ///   0  pszInstanceName   8  pszHostName   16 ip4Address   24 ip6Address
    ///   32 wPort            34 wPriority     36 wWeight      40 dwPropertyCount
    /// </summary>
    internal static class DnsServiceInstance
    {
        internal const int HostName = 8;

        /// <summary>A POINTER to an IP4_ADDRESS, not the address itself.</summary>
        internal const int Ip4Address = 16;

        internal const int Port = 32;
    }

    /// <summary>DnsFreeRecordList.</summary>
    internal const int DnsFreeRecordList = 1;

    /// <summary>
    /// Field offsets into DNS_RECORDW on x64, which is read by hand rather than
    /// declared as a struct: the Data member is a union whose largest arm would
    /// force a size the shorter records do not have, and only three of its arms
    /// are ever wanted here.
    ///
    ///   0  pNext        8  pName       16  wType      18  wDataLength
    ///   20 Flags        24 dwTtl       28  dwReserved 32  Data
    /// </summary>
    internal static class DnsRecord
    {
        internal const int Next = 0;
        internal const int Name = 8;
        internal const int Type = 16;
        internal const int Data = 32;

        /// <summary>DNS_PTR_DATAW.pNameHost, and DNS_SRV_DATAW.pNameTarget.</summary>
        internal const int TargetName = Data;

        /// <summary>DNS_SRV_DATAW.wPort, past pNameTarget, wPriority and wWeight.</summary>
        internal const int SrvPort = Data + 8 + 2 + 2;

        /// <summary>DNS_A_DATA.IpAddress, in network order.</summary>
        internal const int AAddress = Data;
    }

    // ---- open-with chooser ------------------------------------------------

    [Flags]
    internal enum OpenAsFlags : uint
    {
        AllowRegistration = 0x00000001,
        Exec = 0x00000004,
    }

    /// <summary>
    /// OPENASINFO. Sequential and Unicode, matching the header; the two strings
    /// are marshalled as LPWStr rather than left to the default, which on a
    /// struct is ANSI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string FileName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? ClassName;
        internal OpenAsFlags Flags;
    }

    // DllImport, like ShellExecuteEx and for the same reason: OPENASINFO carries
    // string fields and is not blittable, so the source generator cannot marshal
    // it. Flagged explicitly so the exception to the project's LibraryImport rule
    // is visible rather than looking like an oversight.
    [DllImport("shell32.dll", EntryPoint = "SHOpenWithDialog", CharSet = CharSet.Unicode)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054",
        Justification = "OPENASINFO is not blittable; the generator cannot marshal it.")]
    internal static extern int SHOpenWithDialog(nint parent, ref OpenAsInfo info);
}