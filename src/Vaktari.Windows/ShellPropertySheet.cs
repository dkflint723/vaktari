using System.Runtime.InteropServices;

namespace Vaktari.Windows;

/// <summary>
/// Opens the shell's own properties sheet — the one with Security, Details and
/// the Unblock checkbox on it.
///
/// **Nothing in Vaktari can replace this one.** A hand-written properties
/// window can show sizes and dates, and does; it cannot edit an NTFS ACL, clear
/// the mark-of-the-web on a downloaded file, or host the property pages other
/// applications add to the shell. Those are the reasons anyone opens properties
/// on Windows in the first place.
///
/// **On its own STA thread with a message pump, and both parts are required.**
/// The shell needs an STA, exactly as IAssocHandler.Invoke does. And
/// ShellExecuteEx with the `properties` verb returns as soon as the sheet is
/// created rather than when it closes — the sheet is modeless and belongs to
/// the thread that made it, so without a pump it appears and then never
/// repaints or responds.
/// </summary>
internal static partial class ShellPropertySheet
{
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // DllImport rather than LibraryImport for this one struct: SHELLEXECUTEINFOW
    // carries seven string fields, and the source generator wants a blittable
    // layout it can marshal itself. Marked explicitly so the AOT analyser is
    // told this is deliberate rather than an oversight -- see docs/history/WINDOWS.md §6,
    // which bans DllImport precisely so a lapse is visible.
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054",
        Justification = "SHELLEXECUTEINFOW is not blittable; the generator cannot marshal it.")]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(
        out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumThreadWindows(uint threadId, nint callback, nint param);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();

    /// <summary>
    /// Shows the sheet for one path. Returns false when the shell declined, so
    /// the caller can fall back to Vaktari's own window rather than leaving
    /// somebody with a menu item that does nothing.
    /// </summary>
    internal static bool Show(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // **Checked here so the caller can fall back.** Handed a path that is
        // gone — a tab still open on a deleted folder, a file removed by
        // something else — the shell puts up its own "Windows cannot find"
        // dialog, which is a dead end: it names a path with no context and
        // offers nothing but OK. Returning false instead sends the request to
        // Vaktari's own properties window, which reports the same fact inside
        // the application that asked the question.
        //
        // It also has to happen BEFORE the thread starts, because the answer is
        // needed synchronously and the shell's own failure arrives long after
        // this method has returned.
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        var shown = false;

        var thread = new Thread(() => shown = ShowOnThisThread(path))
        {
            // The sheet outlives the call, and this thread lives with it. A
            // background thread means a sheet left open cannot keep the
            // application from exiting.
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // NOT joined. Waiting here would block Vaktari's UI thread for as long
        // as the sheet stayed open, which is the whole time it is useful.
        return true;
    }

    private static bool ShowOnThisThread(string path)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),

            // INVOKEIDLIST is what makes `properties` work at all: without it
            // the shell looks for a registered verb by that name and finds
            // none. With it, the call goes through the item's context menu,
            // which is where the property sheet actually lives.
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };

        if (!ShellExecuteEx(ref info))
        {
            Console.Error.WriteLine(
                $"[vaktari] properties sheet refused: {Marshal.GetLastWin32Error()}");
            return false;
        }

        Pump();
        return true;
    }

    /// <summary>
    /// Runs the thread's message loop until its windows are gone.
    ///
    /// **A plain GetMessage loop would never end.** The sheet does not post
    /// WM_QUIT when it closes, so the thread would sit in GetMessage forever —
    /// harmless while the process runs, but one stranded thread per properties
    /// window. Counting the thread's own windows gives a real finish line.
    ///
    /// The grace period covers the gap before the sheet exists: the window is
    /// not created synchronously by ShellExecuteEx, so an immediate count of
    /// zero means "not yet", not "already closed".
    /// </summary>
    private static void Pump()
    {
        var id = GetCurrentThreadId();
        var appeared = false;
        var waited = 0;

        while (true)
        {
            while (PeekMessage(out var msg, 0, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            var windows = CountWindows(id);

            if (windows > 0) appeared = true;
            else if (appeared) return;
            else if ((waited += 25) > 10_000) return;   // it never showed up

            Thread.Sleep(25);
        }
    }

    private static int CountWindows(uint threadId)
    {
        var count = 0;

        // The callback has to be kept alive across the call; a lambda converted
        // inline would be collectable while the shell is still using it.
        EnumWindowsProc counter = (_, _) => { count++; return true; };
        var handle = Marshal.GetFunctionPointerForDelegate(counter);

        EnumThreadWindows(threadId, handle, 0);

        GC.KeepAlive(counter);
        return count;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsProc(nint hwnd, nint param);
}
