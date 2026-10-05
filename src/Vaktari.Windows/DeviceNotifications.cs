using System.Runtime.InteropServices;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// Hears Windows say a device arrived or left, so the device watch looks then
/// rather than once a second.
///
/// **A hidden top-level window, not a message-only one, and that is the whole
/// trick.** Windows broadcasts <c>WM_DEVICECHANGE</c> for a volume — a drive
/// letter arriving or leaving, a card pushed into a reader, a disc into a bay —
/// to every top-level window, and a message-only window receives no
/// broadcasts at all. The window is never shown (no WS_VISIBLE, and a tool
/// window, so not on the taskbar either) and lives on a thread of its own that
/// sleeps in GetMessage: measured at no CPU over two idle minutes, against
/// about 2 ms a second for the poll it replaces.
///
/// **One window for the process, however many watches listen.** A device
/// change is a fact about the machine, and every watch should hear it, so the
/// window's procedure calls every subscriber. One window per provider was the
/// first shape, and it leaked a thread a provider: every test window builds
/// its own platform, and WindowThreadsTests counted ten windows leaving fifteen
/// threads behind. The window and its thread are made by the first subscriber
/// and kept for the life of the process — a background thread, so they never
/// hold it open — which is the same bargain the theme watches make.
///
/// Rejected: Avalonia's WndProc hook, which fails silently on a top level that
/// is not Win32 and dies with whichever window it hooked; CM_Register_Notification
/// on the volume interface, which needs no window but says nothing when media
/// goes into a card reader — the case DriveSet's key exists for; and WMI, which
/// is a service and a COM round trip for the same answer.
///
/// **Every message is a nudge and nothing more.** What arrived is not read: the
/// watch's own look decides whether anything changed, so a message that means
/// nothing costs one cheap look, and a broadcast that never comes costs only
/// latency — the watch keeps its thirty-second floor (DeviceWatch).
/// </summary>
internal sealed unsafe partial class DeviceNotifications : IDisposable
{
    private const string ClassName = "VaktariDeviceNotifications";

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    internal const uint WM_DEVICECHANGE = 0x0219;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly Lock Gate = new();

    /// <summary>Who hears a change, by subscription.</summary>
    private static readonly Dictionary<DeviceNotifications, Action> Subscribers = [];

    private static nint _hwnd;

    /// <summary>The one window, for the tests that post to it; 0 until the
    /// first subscriber made it.</summary>
    internal static nint Hwnd => Volatile.Read(ref _hwnd);

    /// <summary>How many windows this process has made — one, at most. For
    /// the tests.</summary>
    internal static int WindowsMade => Volatile.Read(ref _windowsMade);

    private static int _windowsMade;

    private DeviceNotifications() { }

    /// <summary>
    /// A subscription that calls <paramref name="nudge"/> on every device
    /// change, or null when the window could not be made — in which case the
    /// caller keeps looking every second, as before this existed.
    /// </summary>
    internal static DeviceNotifications? Start(Action nudge)
    {
        try
        {
            lock (Gate)
            {
                if (_hwnd == 0 && !MakeWindow()) return null;

                var subscription = new DeviceNotifications();
                Subscribers[subscription] = nudge;
                return subscription;
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
            return null;
        }
    }

    /// <summary>Under the gate. The window class first, treating
    /// ERROR_CLASS_ALREADY_EXISTS as success (review M5), then the window on
    /// its own thread.</summary>
    private static bool MakeWindow()
    {
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSW
            {
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = name,
            };

            if (RegisterClassW(&wc) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                return false;
        }

        using var ready = new ManualResetEventSlim();
        nint made = 0;

        var thread = new Thread(() =>
        {
            try
            {
                fixed (char* name = ClassName)
                {
                    made = CreateWindowExW(WS_EX_TOOLWINDOW, name, name, WS_POPUP,
                        0, 0, 0, 0, 0, 0, GetModuleHandleW(null), 0);
                }
            }
            finally
            {
                ready.Set();
            }

            if (made == 0) return;

            MSG msg;

            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        })
        {
            IsBackground = true,
            Name = "vaktari-device-notifications",
        };

        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)) || made == 0) return false;

        Volatile.Write(ref _hwnd, made);
        Interlocked.Increment(ref _windowsMade);
        return true;
    }

    /// <summary>
    /// **Never throws.** An exception leaving a window procedure does not fail
    /// the feature, it ends the process. Each subscriber is called on its own,
    /// so one that throws does not keep the change from the rest.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_DEVICECHANGE:
                    Action[] nudges;

                    lock (Gate) nudges = [.. Subscribers.Values];

                    foreach (var nudge in nudges)
                    {
                        try { nudge(); }
                        catch (Exception ex) { Quiet.Swallowed("places", ex); }
                    }

                    return 1;

                // **Refused** (QA). The window lives for the process, and a
                // WM_CLOSE posted by anything — a tool that closes every
                // window of a process, say — used to destroy it, which left
                // every watch believing a source was running that would never
                // speak again. Nothing of Vaktari's own ever closes it.
                case WM_CLOSE:
                    return 0;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch
        {
            // See above: nothing may leave this frame.
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Stops this subscription hearing changes. The window stays,
    /// for the next subscriber and for the life of the process.</summary>
    public void Dispose()
    {
        lock (Gate) Subscribers.Remove(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassW(WNDCLASSW* wc);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
