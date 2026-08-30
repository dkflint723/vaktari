using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The shell's own context menu — 7-Zip, VLC, Warp, Defender, Send to — read
/// out of Windows and handed over as plain data.
///
/// **The entries that make Explorer's menu feel full are not Explorer's.** Each
/// is a COM handler registered by an installed application, and there is no list
/// of them anywhere to hand-write: the only way to show them is to ask the shell
/// for IContextMenu, let every extension on the machine add its rows to a real
/// menu handle, and then read that handle back.
///
/// **Everything happens on one dedicated STA thread, which the menu owns for its
/// whole life.** Three separate reasons, any one of which is enough:
///
/// - The shell requires STA. AssocHandlers measured this on the neighbouring
///   interface: the identical call fails from an MTA thread and succeeds from an
///   STA one, with nothing in the HRESULT to say why.
/// - IContextMenu is apartment-bound. The object that built the menu is the only
///   one that can invoke from it, so query and invoke must happen on the SAME
///   thread — which means that thread has to stay alive between them.
/// - **A shell extension can hang, and it must not take the window with it.**
///   This runs other people's code. Doing it on the UI thread would mean one
///   badly-written handler freezing the file manager, with us getting the blame;
///   Explorer's own occasional stalls are this exact hazard. Here the wait is
///   bounded, and a handler that never returns strands a background thread
///   instead of the application.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ShellContextMenu : IShellMenu
{
    /// <summary>
    /// How long the shell gets to produce a menu before we give up on it.
    ///
    /// Generous, because it is not a performance budget — it is the line between
    /// "slow machine, cold handler DLLs" and "this extension is never coming
    /// back". A first right-click after boot genuinely can take a moment while
    /// a dozen handlers are paged in.
    /// </summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The command id range handed to QueryContextMenu. Starts at 1 because 0
    /// is what a menu returns for "nothing was chosen", so an entry with id 0
    /// could not be told apart from a dismissal.
    /// </summary>
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    private IntPtr _menu;
    private IntPtr _contextMenu;

    public IReadOnlyList<ShellMenuEntry> Entries { get; private set; } = [];

    private ShellContextMenu(IReadOnlyList<string> paths)
    {
        var built = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            try
            {
                Entries = BuildOnThisThread(paths);
            }
            catch (Exception ex)
            {
                // A third party's code ran. Nothing it does is worth taking the
                // process down for, and an empty menu is a survivable answer.
                Quiet.Swallowed("shell-menu", ex);
                Entries = [];
            }
            finally
            {
                built.Set();
            }

            // Held open for Invoke: the objects behind these ids belong to this
            // apartment and die with the thread.
            foreach (var job in _work.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception ex) { Quiet.Swallowed("shell-menu", ex); }
            }

            Release();
        })
        {
            IsBackground = true,
            Name = "vaktari-shell-menu",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // **Bounded.** A handler that never returns leaves this thread stuck
        // for the life of the process, which is a leak; leaving the UI stuck
        // would be a hang, and those are not the same size of problem.
        if (!built.Wait(BuildTimeout))
        {
            Console.Error.WriteLine(
                "[vaktari] shell-menu: the shell did not answer in "
                + $"{BuildTimeout.TotalSeconds:0}s — an extension is hanging");
        }
    }

    /// <summary>
    /// The menu for these paths, or null when the shell offers nothing.
    ///
    /// Never throws: it is called while a context menu is opening, and no
    /// third-party handler's opinion is worth failing that.
    /// </summary>
    public static ShellContextMenu? For(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;

        try
        {
            var menu = new ShellContextMenu(paths);

            if (menu.Entries.Count > 0) return menu;

            menu.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("shell-menu", ex);
            return null;
        }
    }

    private IReadOnlyList<ShellMenuEntry> BuildOnThisThread(IReadOnlyList<string> paths)
    {
        if (BindContextMenu(paths) is not { } com) return [];

        _contextMenu = com;
        _menu = Native.CreatePopupMenu();

        if (_menu == IntPtr.Zero) return [];

        if (Wrap<IContextMenu>(com) is not { } contextMenu) return [];

        // CMF_NORMAL plus CMF_EXTENDEDVERBS: the extended set is what the
        // classic "show more options" menu carries, and it is the menu the user
        // asked for — the short Windows 11 one is missing exactly the entries
        // that prompted this.
        var hr = contextMenu.QueryContextMenu(_menu, 0, FirstId, LastId, CmfNormal | CmfExtendedVerbs);
        if (hr < 0) return [];

        return Read(_menu, VerbResolver(contextMenu));
    }

    /// <summary>
    /// Asks the handler for an entry's canonical verb — "open", "cut",
    /// "properties" — which is the locale-proof identity its label is not.
    /// Handlers are free to answer nothing, and plenty do; null means "no
    /// verb", never "no entry".
    /// </summary>
    private static Func<uint, string?> VerbResolver(IContextMenu contextMenu)
        => offset =>
        {
            const uint GcsVerbW = 0x00000004;
            const int Capacity = 256;

            var buffer = Marshal.AllocHGlobal(Capacity * 2);

            try
            {
                Marshal.WriteInt16(buffer, 0);

                return contextMenu.GetCommandString(
                        (IntPtr)offset, GcsVerbW, IntPtr.Zero, buffer, Capacity) == 0
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            catch (Exception ex)
            {
                // Other people's code: a handler that throws on a question
                // keeps its entry rather than taking the menu down.
                Quiet.Swallowed("shell-menu", ex);
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        };

    /// <summary>
    /// The verbs Vaktari already answers natively, filtered out of the hosted
    /// menu the way Windows 11 filters them out of its own modern one.
    ///
    /// **By canonical verb, never by label.** Labels are localized — filtering
    /// "Copy" would work in English and strip nothing in German — while the
    /// verb is the handler's own stable name for the action. The list is only
    /// verbs whose function has a native twin at the TOP of our menu: Open,
    /// Open with, Cut/Copy/Paste, Copy as path, Delete, Rename, Properties,
    /// Run as administrator, and the OS share sheet now that Share is ours.
    /// "Create shortcut" and "Send to" stay: nothing native does what they do.
    /// </summary>
    internal static bool IsRedundantVerb(string? verb)
        => verb is not null && RedundantVerbs.Contains(verb.Trim());

    private static readonly HashSet<string> RedundantVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "openas", "cut", "copy", "paste", "delete",
        "properties", "copyaspath", "rename", "runas",
        "share", "Windows.Share", "Windows.ModernShare",
    };

    /// <summary>
    /// One COM object per pointer, without the unsafe marshaller dance.
    /// </summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static T? Wrap<T>(IntPtr com) where T : class
    {
        if (com == IntPtr.Zero) return null;

        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(com, CreateObjectFlags.None) as T;
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("shell-menu", ex);
            return null;
        }
    }

    /// <summary>
    /// IContextMenu for the whole selection.
    ///
    /// **Through an item array built from every path, not one item.** The
    /// obvious shortcut — bind the first file and be done — hands a handler one
    /// file where the user picked eight, so "add to archive" would quietly make
    /// an archive of one. SHCreateShellItemArrayFromShellItem takes a single
    /// item by definition, so the ids come from SHParseDisplayName and the
    /// array is built from all of them.
    /// </summary>
    private static IntPtr? BindContextMenu(IReadOnlyList<string> paths)
    {
        var ids = new List<IntPtr>(paths.Count);

        try
        {
            foreach (var path in paths)
            {
                if (Native.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) < 0) continue;

                ids.Add(pidl);
            }

            if (ids.Count == 0) return null;

            var handles = ids.ToArray();
            var array = IntPtr.Zero;

            try
            {
                unsafe
                {
                    fixed (IntPtr* first = handles)
                    {
                        if (Native.SHCreateShellItemArrayFromIDLists(
                                (uint)handles.Length, (IntPtr)first, out array) < 0)
                            return null;
                    }
                }

                if (Wrap<IShellItemArray>(array) is not { } items) return null;

                return items.BindToHandler(
                    IntPtr.Zero, in UiObjectId, in ContextMenuId, out var com) < 0
                    ? null
                    : com;
            }
            finally
            {
                if (array != IntPtr.Zero) Marshal.Release(array);
            }
        }
        finally
        {
            // The shell allocated these; it frees them the same way every time.
            foreach (var pidl in ids) Native.CoTaskMemFree(pidl);
        }
    }

    /// <summary>
    /// Walks a native menu into records.
    ///
    /// Recursive because submenus are real menus — 7-Zip's is where its actual
    /// commands live, and an entry that opens nothing would be worse than not
    /// showing it. Bounded in depth: this is other people's data, and a menu
    /// that refers to itself would otherwise recurse until the stack goes.
    /// </summary>
    private static IReadOnlyList<ShellMenuEntry> Read(
        IntPtr menu, Func<uint, string?>? verbOf = null, int depth = 0)
    {
        if (depth > 4) return [];

        var count = Native.GetMenuItemCount(menu);
        if (count <= 0) return [];

        var entries = new List<ShellMenuEntry>(count);
        var text = new char[512];

        for (var i = 0; i < count; i++)
        {
            var info = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                fMask = MiimString | MiimId | MiimSubmenu | MiimState | MiimFType,
                cch = (uint)text.Length,
            };

            bool ok;

            unsafe
            {
                fixed (char* buffer = text)
                {
                    info.dwTypeData = (IntPtr)buffer;
                    ok = Native.GetMenuItemInfoW(menu, (uint)i, true, ref info);
                }
            }

            if (!ok) continue;

            if ((info.fType & MftSeparator) != 0)
            {
                entries.Add(new ShellMenuEntry("", 0, IsSeparator: true));
                continue;
            }

            var label = new string(text, 0, (int)info.cch).Replace("&", "", StringComparison.Ordinal);
            if (label.Length == 0) continue;

            // **Duplicates of our own menu are dropped here**, by verb — the
            // hosted menu re-lists Open, Cut, Copy, Properties and their kin,
            // and every one already sits above the "Windows menu" entry with a
            // shortcut beside it. Only at the TOP level: inside a 7-Zip or
            // Send-to submenu, a verb collision is coincidence, not redundancy.
            if (depth == 0
                && verbOf is not null
                && IsRedundantVerb(verbOf(info.wID - FirstId)))
                continue;

            var opensSubmenu = info.hSubMenu != IntPtr.Zero;
            var children = opensSubmenu ? Read(info.hSubMenu, verbOf, depth + 1) : [];

            // **A row that opens a submenu has no command of its own.** Windows
            // puts the submenu's identity in wID for a popup, not a command id,
            // so a row whose children we could not read must not be left
            // looking clickable — invoking it would hand the shell a number
            // that belongs to some other extension entirely.
            //
            // These exist. An extension may fill its submenu only when Windows
            // sends WM_INITMENUPOPUP, which never arrives here because this menu
            // is read rather than shown, and one past the depth limit is empty
            // for our own reasons. Either way the row is shown and greyed: it
            // says the entry is there without pretending it can be used.
            var enabled = (info.fState & (MfsDisabled | MfsGrayed)) == 0
                && (!opensSubmenu || children.Count > 0);

            entries.Add(new ShellMenuEntry(
                label,
                (int)(info.wID - FirstId),
                IsSeparator: false,
                IsEnabled: enabled,
                children.Count > 0 ? children : null));
        }

        return Trim(entries);
    }

    /// <summary>
    /// Drops the separators that would draw against nothing.
    ///
    /// The shell hands back a menu meant to be merged into another one, so it
    /// happily starts or ends with a rule. Avalonia draws every separator it is
    /// given and collapses none — the same defect this project already shipped
    /// once in its own menu.
    /// </summary>
    private static List<ShellMenuEntry> Trim(List<ShellMenuEntry> entries)
    {
        var kept = new List<ShellMenuEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (kept.Count == 0 || kept[^1].IsSeparator)) continue;

            kept.Add(entry);
        }

        while (kept.Count > 0 && kept[^1].IsSeparator) kept.RemoveAt(kept.Count - 1);

        return kept;
    }

    public void Invoke(int id)
    {
        if (id < 0) return;

        _work.Add(() =>
        {
            if (Wrap<IContextMenu>(_contextMenu) is not { } contextMenu) return;

            // The verb is passed as the id in the LOW WORD of a pointer-sized
            // value, which is what MAKEINTRESOURCE does in C. Anything else and
            // the handler looks for a verb name at that address.
            var invoke = new InvokeCommandInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<InvokeCommandInfoEx>(),
                fMask = UnicodeFlag,
                lpVerb = (IntPtr)id,
                lpVerbW = (IntPtr)id,
                nShow = ShowNormal,
            };

            contextMenu.InvokeCommand(ref invoke);
        });
    }

    private void Release()
    {
        if (_menu != IntPtr.Zero)
        {
            Native.DestroyMenu(_menu);
            _menu = IntPtr.Zero;
        }

        if (_contextMenu != IntPtr.Zero)
        {
            Marshal.Release(_contextMenu);
            _contextMenu = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Ends the thread, which releases the COM objects on the apartment that
    /// created them. Not joined: a handler that hung during the build owns that
    /// thread forever, and waiting on it here would move the hang into whoever
    /// closed the menu.
    /// </summary>
    public void Dispose()
    {
        try { _work.CompleteAdding(); }
        catch (ObjectDisposedException) { }
    }

    // ---- interop ----------------------------------------------------------

    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;

    private const uint MiimState = 0x00000001;
    private const uint MiimId = 0x00000002;
    private const uint MiimSubmenu = 0x00000004;
    private const uint MiimString = 0x00000040;
    private const uint MiimFType = 0x00000100;

    private const uint MftSeparator = 0x00000800;
    private const uint MfsGrayed = 0x00000003;
    private const uint MfsDisabled = 0x00000002;

    private const uint UnicodeFlag = 0x00004000;
    private const int ShowNormal = 1;

    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid ShellItemArrayId = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid UiObjectId = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InvokeCommandInfoEx
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    [GeneratedComInterface]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    internal partial interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref InvokeCommandInfoEx pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [GeneratedComInterface]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    internal partial interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(uint flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(uint attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IntPtr ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    internal static partial class Native
    {
        [LibraryImport("user32.dll")]
        internal static partial IntPtr CreatePopupMenu();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyMenu(IntPtr hMenu);

        [LibraryImport("user32.dll")]
        internal static partial int GetMenuItemCount(IntPtr hMenu);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMenuItemInfoW(
            IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition,
            ref MenuItemInfo lpmii);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHParseDisplayName(
            string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [LibraryImport("shell32.dll")]
        internal static partial int SHCreateShellItemArrayFromIDLists(
            uint cidl, IntPtr rgpidl, out IntPtr ppsiItemArray);

        [LibraryImport("ole32.dll")]
        internal static partial void CoTaskMemFree(IntPtr pv);
    }
}
