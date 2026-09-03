using System.Buffers.Binary;

namespace Vaktari.Windows;

/// <summary>
/// Whether the desktop opens things on one click.
///
/// **Windows does say, and the comment that used to sit where this is now read
/// from insisted it did not** — so "Whatever the desktop is set to" collapsed
/// to double on Windows however Folder Options was set, while the same option
/// on KDE worked. That comment was wrong twice over: it named
/// <c>Explorer\Advanced</c>, and the value lives under <c>Explorer</c>; and it
/// called the blob undocumented, when it is the SHELLSTATE structure out of
/// shlobj_core.h.
///
/// The layout, read back off a real profile rather than taken on trust: 36
/// bytes, a leading DWORD of cbSize that equals that length, then the bitfield,
/// LSB first. Nine of its bits have DWORD mirrors under
/// <c>Explorer\Advanced</c> and every one of them agrees — bit 0 against
/// Hidden, bit 1 against HideFileExt (inverted; the flag is fShowExtensions),
/// bit 11 against ShowInfoTip, bit 15 against ShowSuperHidden, and so on. That
/// is what pins the offset and the bit order, so bit 5 is established rather
/// than guessed.
///
/// Bit 5 is fDoubleClickInWebView, which is the question backwards: a SET bit
/// means double, so the answer is inverted.
///
/// Not watched, deliberately. The theme provider watches Personalize and DWM,
/// and every fire of it flushes the icon cache; this key also holds LogonCount,
/// GlobalAssocChangedCounter and the width of the Browse For Folder dialog, so
/// watching it would throw the thumbnails away on writes that have nothing to
/// do with clicking. Re-read at startup and on every settings save, which is
/// enough for a Folder Options toggle.
/// </summary>
internal static class ShellState
{
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    /// <summary>Past the leading cbSize.</summary>
    private const int BitsAt = 4;

    /// <summary>SHELLSTATE bit 5, fDoubleClickInWebView.</summary>
    private const uint DoubleClickInWebView = 0x20;

    internal static bool? OpensOnSingleClick()
        => OpensOnSingleClick(Native.ReadBinary(ExplorerKey, "ShellState"));

    /// <summary>
    /// Null is a real answer here, and the only one three of these branches can
    /// give: it means the desktop did not say, and the application falls back
    /// to its own setting rather than asserting a preference nobody expressed.
    /// The cbSize check is what keeps a future layout change from being
    /// silently misread — it degrades to null, which is today's behaviour.
    /// </summary>
    internal static bool? OpensOnSingleClick(byte[]? shellState)
    {
        if (shellState is not { Length: >= BitsAt + 4 }) return null;

        if (BinaryPrimitives.ReadUInt32LittleEndian(shellState) != (uint)shellState.Length)
            return null;

        var bits = BinaryPrimitives.ReadUInt32LittleEndian(shellState.AsSpan(BitsAt));

        return (bits & DoubleClickInWebView) == 0;
    }
}
