using System.Runtime.InteropServices;

namespace Vaktari.Windows;

/// <summary>
/// Lets the running copy take the foreground when a later launch hands it a
/// folder, or asks for nothing but the window.
///
/// **Windows does not let a background process bring itself forward.** The
/// running Vaktari is a background process by the time a second launch
/// reaches it, so its Activate — Avalonia's SetForegroundWindow — is refused
/// under the foreground-lock rules and the taskbar button flashes instead: a
/// double-clicked folder opened in a tab behind whatever the person was
/// looking at. The launch is the process Explorer just started, which
/// ordinarily does hold the right, and AllowSetForegroundWindow is how it
/// passes that on.
///
/// ASFW_ANY rather than the running copy's process id, because the handover
/// channel is a socket and carries no id — and what it hands out is only the
/// right this launch already held and is about to exit with.
/// </summary>
public static partial class ForegroundHandover
{
    private const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// Called by the launch, before it hands over. False when this process had
    /// no foreground right to give — started from a script, say — which is not
    /// an error: the running copy then flashes rather than rising, exactly as
    /// it did before.
    /// </summary>
    public static bool Allow() => AllowSetForegroundWindow(ASFW_ANY);
}
