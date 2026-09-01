using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Writes <c>.lnk</c> shortcuts the way Explorer does: IShellLink for the
/// target, IPersistFile for the save.
///
/// Source-generated COM, the same way the rest of this assembly talks to the
/// shell — see <see cref="AssocHandlers"/> for the pattern and WINDOWS.md for
/// why the runtime-built RCW machinery is off the table under NativeAOT.
///
/// Named as Explorer names them: "report.pdf - Shortcut.lnk", then
/// "report.pdf - Shortcut (2).lnk" — the listing shows them without the
/// extension, so matching Explorer's wording is what makes the result read as
/// "a shortcut" rather than as a mystery file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsShortcuts : IShortcutMaker
{
    /// <summary>
    /// What a .lnk points at.
    ///
    /// **ShellLink.TargetOf existed, was tested, and had exactly one consumer:
    /// the places import.** So a shortcut to a folder could be imported as a
    /// place and could not be opened by double-clicking it — that handed the
    /// .lnk to the shell, which opened a separate Explorer window instead of
    /// navigating the pane.
    /// </summary>
    public string? TargetOf(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            return ShellLink.TargetOf(path) is { Length: > 0 } target ? target : null;
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("shortcuts", ex);
            return null;
        }
    }

    public string CreateShortcut(string target, string destinationFolder)
    {
        var full = Path.GetFullPath(target);

        var stem = Path.Combine(
            destinationFolder, PathRules.LeafName(full) + " - Shortcut");

        var landing = stem + ".lnk";

        for (var n = 2; File.Exists(landing) || Directory.Exists(landing); n++)
            landing = $"{stem} ({n}).lnk";

        Write(full, landing);

        return landing;
    }

    private static void Write(string target, string landing)
    {
        var clsid = ClsidShellLink;
        var iid = IidShellLink;

        var hr = CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iid, out var raw);

        if (hr != 0 || raw == IntPtr.Zero)
            throw new IOException($"the shell would not make a shortcut (0x{hr:X8})");

        try
        {
            var link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(
                raw, CreateObjectFlags.None);

            Throw(link.SetPath(target), "aim the shortcut");

            // A shortcut to a file starts programs in the file's own folder,
            // which is what Explorer writes and what most programs assume.
            if (File.Exists(target) && Path.GetDirectoryName(target) is { Length: > 0 } home)
                Throw(link.SetWorkingDirectory(home), "set its folder");

            // The same wrapper answers for IPersistFile: the cast is a
            // QueryInterface under source-generated ComWrappers.
            Throw(((IPersistFile)link).Save(landing, fRemember: true), "save it");
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private static void Throw(int hr, string doing)
    {
        if (hr != 0) throw new IOException($"could not {doing} (0x{hr:X8})");
    }

    /// <summary>Where an existing shortcut points, for the tests to close the
    /// loop with — a written file proves nothing about its contents.</summary>
    internal static string? ReadTarget(string lnkPath)
    {
        var clsid = ClsidShellLink;
        var iid = IidShellLink;

        if (CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iid, out var raw) != 0
            || raw == IntPtr.Zero)
            return null;

        try
        {
            var link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(
                raw, CreateObjectFlags.None);

            if (((IPersistFile)link).Load(lnkPath, 0) != 0) return null;

            var buffer = Marshal.AllocHGlobal(MaxPath * 2);

            try
            {
                return link.GetPath(buffer, MaxPath, IntPtr.Zero, 0) == 0
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private const int MaxPath = 260;
    private const uint ClsctxInprocServer = 1;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLink = new("000214F9-0000-0000-C000-000000000046");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    /// <summary>
    /// IShellLinkW, vtable order exactly as the SDK declares it — a misordered
    /// slot here calls the wrong method with the right arguments and fails in
    /// ways that look like anything but this file.
    /// </summary>
    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(IntPtr pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(IntPtr pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IPersistFile, IPersist's GetClassID included — it comes first
    /// in the vtable and skipping it shifts every slot after it.</summary>
    [GeneratedComInterface]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }
}
