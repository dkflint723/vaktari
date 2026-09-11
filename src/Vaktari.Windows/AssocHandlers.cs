using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The shell's own list of applications that can open a file.
///
/// **This is the list docs/history/WINDOWS.md recorded as needing COM and therefore
/// deferred.** The deferral was never a design decision, only an untested
/// assumption that source-generated COM interop would fail under NativeAOT. It
/// does not — an IShellItem enumeration of the Recycle Bin proved that in a
/// published AOT binary — so this is now ordinary work.
///
/// `SHAssocEnumHandlers` is the same enumeration Explorer's own "Open with"
/// submenu is built from, which is why the names match what a user already sees
/// elsewhere on their machine.
/// </summary>
internal static partial class AssocHandlers
{
    private const uint FilterRecommended = 1;

    private static readonly Guid ShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid DataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    /// <summary>
    /// Everything registered for this file's extension, in the shell's own
    /// order — which puts the default first, so the caller does not have to
    /// work out what "default" means.
    /// </summary>
    internal static IReadOnlyList<LaunchOption> For(string path)
    {
        var extension = Path.GetExtension(path);

        // A file with no extension has nothing registered against it. The shell
        // would return the "choose an app" list, which is the picker's job.
        if (string.IsNullOrEmpty(extension)) return [];

        var options = new List<LaunchOption>();

        // **Two installs of the same application produce two identical menu
        // entries.** Observed with Firefox, present both in Program Files and
        // under the user's AppData — the shell reports both, correctly, and a
        // menu offering "Firefox" twice tells the user nothing about which is
        // which. First wins, and the shell orders by preference, so the one
        // kept is the one it would have used.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Enumerate(extension, (_, name, ui) =>
            {
                // The identity is the shell's own name for the handler, because
                // that is what Invoke has to match on later. It is usually an
                // executable path and sometimes not — "Photos" is a packaged
                // app and names itself — which is exactly why OpenWith invokes
                // the handler rather than trying to start a process.
                if (ui.Length > 0 && seen.Add(ui)) options.Add(new LaunchOption(ui, name));
                return true;
            });
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // An empty list is a documented answer for this interface, and a
            // context menu is not worth failing to open over.
            Quiet.Swallowed("open-with", e);
        }

        return options;
    }

    /// <summary>
    /// Hands the file to one specific handler, and says whether it managed to.
    ///
    /// **Invoked through the shell rather than started as a process.** The
    /// obvious implementation — take the executable path and run it with the
    /// file as an argument — works for ordinary desktop applications and fails
    /// silently for packaged ones: `Photos` reports its name as "Photos", which
    /// is not a path and cannot be started. Invoke is what Explorer itself
    /// calls, so both kinds behave the same.
    /// </summary>
    internal static bool Invoke(string path, string id)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return false;

        // **On its own STA thread, because the shell requires one.** Measured:
        // the identical call returns false from an MTA thread and true from an
        // STA one, with the application actually starting in the second case.
        // Nothing in the HRESULT says "wrong apartment" — Invoke simply fails,
        // which is the kind of quiet wrong answer that gets diagnosed as "the
        // list is broken".
        //
        // Not left to the caller. Avalonia's UI thread happens to be STA on
        // Windows, so this would work today by luck from a menu click and fail
        // from anywhere else — a background refresh, a test, a future caller on
        // the thread pool. A method whose correctness depends on which thread
        // reached it is a trap for whoever calls it next.
        var invoked = false;

        var thread = new Thread(() => invoked = InvokeOnThisThread(path, extension, id))
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return invoked;
    }

    private static bool InvokeOnThisThread(string path, string extension, string id)
    {

        var data = DataObjectFor(path);
        if (data == IntPtr.Zero) return false;

        try
        {
            var invoked = false;

            Enumerate(extension, (handler, name, _) =>
            {
                if (!string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) return true;

                invoked = handler.Invoke(data) == 0;
                return false;                       // found it; stop walking
            });

            return invoked;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Quiet.Swallowed("open-with", e);
            return false;
        }
        finally
        {
            Marshal.Release(data);
        }
    }

    /// <summary>
    /// Walks the handlers for an extension, handing each one's name and display
    /// name to <paramref name="visit"/>. Returning false stops the walk.
    /// </summary>
    private static void Enumerate(string extension, Func<IAssocHandler, string, string, bool> visit)
    {
        if (Native.SHAssocEnumHandlers(extension, FilterRecommended, out var enumerator) != 0
            || enumerator == IntPtr.Zero)
            return;

        var com = new StrategyBasedComWrappers();

        try
        {
            var handlers = (IEnumAssocHandlers)com.GetOrCreateObjectForComInstance(
                enumerator, CreateObjectFlags.None);

            while (handlers.Next(1, out var handlerPtr, out var fetched) == 0
                   && fetched == 1
                   && handlerPtr != IntPtr.Zero)
            {
                try
                {
                    var handler = (IAssocHandler)com.GetOrCreateObjectForComInstance(
                        handlerPtr, CreateObjectFlags.None);

                    var name = Read(handler.GetName, out var gotName);
                    var ui = Read(handler.GetUIName, out _);

                    if (!gotName) continue;
                    if (!visit(handler, name, ui)) return;
                }
                finally
                {
                    Marshal.Release(handlerPtr);
                }
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }
    }

    /// <summary>
    /// An IDataObject carrying one file, which is what a handler expects to be
    /// given. Built through the shell so a handler receives the same shape of
    /// object it would from Explorer.
    /// </summary>
    private static IntPtr DataObjectFor(string path)
    {
        var iid = ShellItem;

        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, in iid, out var itemPtr) != 0
            || itemPtr == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var item = (IShellItem)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.None);

            var bhid = BhidDataObject;
            var dataIid = DataObject;

            return item.BindToHandler(IntPtr.Zero, in bhid, in dataIid, out var data) == 0
                ? data
                : IntPtr.Zero;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Quiet.Swallowed("open-with", e);
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    /// <summary>
    /// Reads one of the interface's LPWSTR out-parameters and frees it.
    ///
    /// The shell allocates these with CoTaskMemAlloc and the caller owns them,
    /// so each one read is one that has to be freed — hence the out-IntPtr
    /// shape on the interface rather than letting the marshaller produce a
    /// string it would not know to release.
    /// </summary>
    private static string Read(GetString get, out bool ok)
    {
        ok = false;

        if (get(out var ptr) != 0 || ptr == IntPtr.Zero) return "";

        try
        {
            ok = true;
            return Marshal.PtrToStringUni(ptr) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private delegate int GetString(out IntPtr value);

    [GeneratedComInterface]
    [Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA")]
    internal partial interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    }

    [GeneratedComInterface]
    [Guid("F04061AC-1659-4A3F-A954-775AA57FC083")]
    internal partial interface IAssocHandler
    {
        [PreserveSig] int GetName(out IntPtr ppsz);
        [PreserveSig] int GetUIName(out IntPtr ppsz);
        [PreserveSig] int GetIconLocation(out IntPtr ppszPath, out int pIndex);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int Invoke(IntPtr pdo);
        [PreserveSig] int CreateInvoker(IntPtr pdo, out IntPtr ppInvoker);
    }

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

    private static partial class Native
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHAssocEnumHandlers(
            string pszExtra, uint afFilter, out IntPtr ppEnumHandler);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, in Guid riid, out IntPtr ppv);
    }
}
