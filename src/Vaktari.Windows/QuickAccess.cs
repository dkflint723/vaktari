using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// The folders someone has pinned to Quick access, which on Windows 11 is
/// called Home.
///
/// **These are where a Windows user's real bookmarks live, and nothing read
/// them.** The importer took the two lists that are FILES — the Links folder,
/// which is where Explorer kept Favorites before Quick access replaced it, and
/// Network Shortcuts — and the comment beside it said Quick access was
/// "waiting on the same COM decision as the Trash view". That decision was
/// made: WINDOWS.md §7c records the spike that proved source-generated COM
/// works in a published NativeAOT binary, and four files in this assembly have
/// used it since. The comment outlived its reason by a release.
///
/// **Quick access is not a file.** It is a shell namespace extension whose
/// backing store is an OLE compound jumplist, so it is read the way Explorer
/// reads it: bind the folder, enumerate its children, and ask each one whether
/// it is pinned.
///
/// **Pinned is asked per item, not by subtracting the Frequent folders list.**
/// Measured on Windows 11 26200: shell:::{679f85cb…} and the Frequent folders
/// folder shell:::{3936E9E4…} returned the SAME ten items in the same order, so
/// subtracting one from the other yields nothing at all and would have looked
/// exactly like "this user has no pins". The same ten items answer
/// System.Home.IsPinned individually — seven true, three false — which is the
/// distinction Explorer itself draws.
/// </summary>
internal static partial class QuickAccess
{
    /// <summary>
    /// Quick access, as a parsing name. The GUID is Windows' own and is stable
    /// across Windows 10, where the folder is called Quick access, and Windows
    /// 11, where the same folder is called Home.
    /// </summary>
    private const string Folder = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    /// <summary>
    /// Whether an item is pinned rather than merely frequently opened. Resolved
    /// by NAME through propsys rather than written out as a fmtid and a pid: a
    /// hand-copied PROPERTYKEY is unreadable, unverifiable, and wrong in a way
    /// nothing would report.
    /// </summary>
    private const string IsPinned = "System.Home.IsPinned";

    /// <summary>
    /// Stands in for the shell walk in tests.
    ///
    /// The same seam, and for the same reason, as LinuxPlacesProvider's mount
    /// table: reading the literal source here is what would make the import
    /// rules below testable only on the machine being described. Null in the
    /// application.
    /// </summary>
    internal static Func<IReadOnlyList<string>>? Override { get; set; }

    /// <summary>
    /// The pinned items that are real folders, in the order Explorer holds
    /// them.
    ///
    /// Items with no filesystem path are dropped rather than reported: Quick
    /// access holds the Recycle Bin on a default profile — measured on this
    /// machine, pinned and among the seven — and it parses to
    /// "::{645FF040-5081-101B-9F08-00AA002F954E}", which is not a path anything
    /// downstream could list.
    /// </summary>
    internal static IReadOnlyList<string> Pinned()
    {
        if (Override is { } stub) return stub();

        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            return Walk();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or DllNotFoundException)
        {
            // A shell that will not answer costs the import, not the startup —
            // the same bargain the two file-backed lists already make.
            Quiet.Swallowed("quick-access", e);
            return [];
        }
    }

    private static IReadOnlyList<string> Walk()
    {
        var found = new List<string>();

        var itemId = ShellItemId;

        if (Native.SHCreateItemFromParsingName(Folder, IntPtr.Zero, in itemId, out var folderPtr) != 0
            || folderPtr == IntPtr.Zero)
            return found;

        var wrappers = new StrategyBasedComWrappers();

        try
        {
            var folder = (IShellItem)wrappers
                .GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);

            var bhid = EnumItemsId;
            var enumId = EnumShellItemsId;

            if (folder.BindToHandler(IntPtr.Zero, in bhid, in enumId, out var enumPtr) != 0
                || enumPtr == IntPtr.Zero)
                return found;

            if (Native.PSGetPropertyKeyFromName(IsPinned, out var pinnedKey) != 0) return found;

            try
            {
                var items = (IEnumShellItems)wrappers
                    .GetOrCreateObjectForComInstance(enumPtr, CreateObjectFlags.None);

                // One at a time. The interface allows a batch, but a batch
                // hands back an array of raw pointers this would then have to
                // release individually on every early exit — and this list is
                // ten items on a default profile.
                while (items.Next(1, out var childPtr, out var fetched) == 0
                       && fetched == 1
                       && childPtr != IntPtr.Zero)
                {
                    try
                    {
                        if (PathOfPinned(wrappers, childPtr, in pinnedKey) is { Length: > 0 } path)
                            found.Add(path);
                    }
                    finally
                    {
                        Marshal.Release(childPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(enumPtr);
            }
        }
        finally
        {
            Marshal.Release(folderPtr);
        }

        return found;
    }

    /// <summary>
    /// One child's path, or empty for anything that is not a pinned folder.
    ///
    /// IShellItem2 rather than IShellItem: GetBool is what reads a property,
    /// and IShellItem has no property access at all.
    /// </summary>
    private static string PathOfPinned(
        StrategyBasedComWrappers wrappers, IntPtr childPtr, in PropertyKey pinnedKey)
    {
        var child = (IShellItem2)wrappers
            .GetOrCreateObjectForComInstance(childPtr, CreateObjectFlags.None);

        // A failed read is NOT treated as pinned. An item whose pinned state
        // cannot be established is a frequent folder as far as this is
        // concerned, because importing a folder somebody opened twice as though
        // they had chosen it is the worse of the two mistakes.
        if (child.GetBool(in pinnedKey, out var pinned) != 0 || pinned == 0) return "";

        if (child.GetDisplayName(SigdnFileSysPath, out var namePtr) != 0 || namePtr == IntPtr.Zero)
            return "";

        try
        {
            return Marshal.PtrToStringUni(namePtr) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    /// <summary>
    /// SIGDN_FILESYSPATH. Asking for the filesystem path is also how a virtual
    /// item is rejected: the Recycle Bin has no such name and the call fails,
    /// which is exactly the answer wanted.
    /// </summary>
    private const uint SigdnFileSysPath = 0x80058000;

    /// <summary>
    /// PROPERTYKEY: a format id and a property id WITHIN that format, and both
    /// halves are load-bearing. Written out as a struct rather than passed as
    /// the GUID alone, which is the shape it superficially resembles — the
    /// GUID alone is 16 bytes where the shell reads and writes 20, so
    /// PSGetPropertyKeyFromName would have written the pid past the end of what
    /// it was given.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid ShellItem2Id = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");
    private static readonly Guid EnumShellItemsId = new("70629033-E363-4A28-A567-0DB78006E6D7");

    /// <summary>BHID_EnumItems — the handler that turns a folder into its children.</summary>
    private static readonly Guid EnumItemsId = new("94F60519-2850-4924-AA5A-D15E84868039");

    [GeneratedComInterface]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal partial interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }

    /// <summary>
    /// IShellItem2 derives from IShellItem, so its own five methods come after
    /// the base five in the vtable — the base declarations here are what keeps
    /// GetBool at the right slot and are never called.
    /// </summary>
    [GeneratedComInterface]
    [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
    internal partial interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);

        [PreserveSig] int GetPropertyStore(uint flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(
            uint flags, IntPtr punkCreateObject, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(
            IntPtr rgKeys, uint cKeys, uint flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(in PropertyKey keyType, in Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(in PropertyKey key, out IntPtr ppropvar);
        [PreserveSig] int GetCLSID(in PropertyKey key, out Guid pclsid);
        [PreserveSig] int GetFileTime(in PropertyKey key, out long pft);
        [PreserveSig] int GetInt32(in PropertyKey key, out int pi);
        [PreserveSig] int GetString(in PropertyKey key, out IntPtr ppsz);
        [PreserveSig] int GetUInt32(in PropertyKey key, out uint pui);
        [PreserveSig] int GetUInt64(in PropertyKey key, out ulong pull);
        [PreserveSig] int GetBool(in PropertyKey key, out int pf);
    }

    [GeneratedComInterface]
    [Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
    internal partial interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    private static partial class Native
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, in Guid riid, out IntPtr ppv);

        [LibraryImport("propsys.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int PSGetPropertyKeyFromName(string pszName, out PropertyKey pkey);
    }
}
