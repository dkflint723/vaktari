using System.Runtime.InteropServices;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// The desktop's colour scheme, such as Windows has one.
///
/// **This is the shape of the difference from KDE.** `kdeglobals` hands over a
/// complete scheme — window, view, selection and their foregrounds, all chosen
/// together by whoever designed the theme. Windows publishes exactly two facts:
/// light or dark, and one accent colour. Every other role below is ours,
/// derived to match what Windows 11's own surfaces look like.
///
/// That is not a shortcut, it is how the platform works: a Windows application
/// is expected to bring its own neutrals and tint them with the system accent.
/// The alternative — inventing a full scheme from the accent — would drift away
/// from the desktop rather than towards it.
/// </summary>
public sealed class WindowsThemeProvider : IThemeProvider, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private readonly CancellationTokenSource _stopping = new();

    public event EventHandler? Changed;

    public WindowsThemeProvider()
    {
        Watch(PersonalizeKey);
        Watch(DwmKey);
    }

    public ThemePalette? Read()
    {
        // 0 means dark. Absent means a Windows old enough not to have the
        // setting, where light was the only answer.
        var dark = Native.ReadDword(PersonalizeKey, "AppsUseLightTheme") is 0;

        var accent = ReadAccent() ?? (dark ? "#4CC2FF" : "#0067C0");

        var colours = dark ? DarkNeutrals() : LightNeutrals();

        colours[ThemeRole.Accent] = accent;
        colours[ThemeRole.SelectionBackground] = accent;
        colours[ThemeRole.SelectionText] = ReadableOn(accent);

        return new ThemePalette
        {
            Colours = colours,
            FontFamily = ReadUiFont(),

            // Left to the application's own default. The system font size is a
            // LOGFONT height in device units, and turning it into points needs
            // the DPI the window actually lands on — which this cannot know and
            // Avalonia already handles by scaling.
            FontSize = null,

            IsDark = dark,

            // Windows has no icon theme to name: icons come per-file from the
            // shell rather than from a theme of named icons. Null, so the drawn
            // fallbacks are used.
            IconTheme = null,

            // **This used to be flatly null, on the strength of a comment that
            // was wrong twice** — it named Explorer\Advanced when the value is
            // under Explorer, and called the blob undocumented when it is the
            // SHELLSTATE structure. So "Whatever the desktop is set to" always
            // collapsed to double on Windows, however Folder Options was set,
            // while the same option worked on KDE. ShellState reads it.
            //
            // Null is still an answer, and still means "the desktop did not
            // say" — the value is absent, or does not decode as this layout —
            // in which case the application's own default applies.
            SingleClick = ShellState.OpensOnSingleClick(),
        };
    }

    /// <summary>
    /// **The two registry values disagree about byte order**, which is worth
    /// stating because both look like a colour and only one will be right.
    /// Measured on Windows 11: <c>AccentColor = 0xFF4F4737</c> and
    /// <c>ColorizationColor = 0xC437474F</c> on the same machine, and both mean
    /// <c>#37474F</c>. AccentColor is ABGR; ColorizationColor is ARGB.
    ///
    /// AccentColor first because it is the accent proper. ColorizationColor is
    /// the window-chrome tint and carries an alpha that is not opacity of the
    /// colour but strength of the blend, so it is only a fallback.
    /// </summary>
    private static string? ReadAccent()
    {
        if (Native.ReadDword(DwmKey, "AccentColor") is { } abgr)
            return $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

        if (Native.ReadDword(DwmKey, "ColorizationColor") is { } argb)
            return $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";

        return null;
    }

    /// <summary>Windows 11 dark surfaces, matched to Explorer rather than invented.</summary>
    private static Dictionary<string, string> DarkNeutrals() => new(StringComparer.Ordinal)
    {
        [ThemeRole.WindowBackground] = "#202020",
        [ThemeRole.WindowText] = "#FFFFFF",
        [ThemeRole.ViewBackground] = "#191919",
        [ThemeRole.ViewAlternate] = "#1F1F1F",
        [ThemeRole.ViewText] = "#FFFFFF",
        [ThemeRole.ViewDimText] = "#A0A0A0",
        [ThemeRole.Border] = "#333333",
    };

    private static Dictionary<string, string> LightNeutrals() => new(StringComparer.Ordinal)
    {
        [ThemeRole.WindowBackground] = "#F3F3F3",
        [ThemeRole.WindowText] = "#000000",
        [ThemeRole.ViewBackground] = "#FFFFFF",
        [ThemeRole.ViewAlternate] = "#FAFAFA",
        [ThemeRole.ViewText] = "#000000",
        [ThemeRole.ViewDimText] = "#5D5D5D",
        [ThemeRole.Border] = "#E5E5E5",
    };

    /// <summary>
    /// Black or white, whichever can actually be read on the accent. The user
    /// picks the accent and can pick a pale one, so hardcoding white text on
    /// selection is how a selected row becomes invisible.
    ///
    /// sRGB relative luminance, the same weighting WCAG uses, rather than a
    /// plain average — the eye is far more sensitive to green than to blue.
    /// </summary>
    private static string ReadableOn(string hex)
    {
        if (hex.Length != 7) return "#FFFFFF";

        try
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);

            return 0.2126 * r + 0.7152 * g + 0.0722 * b > 140 ? "#000000" : "#FFFFFF";
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return "#FFFFFF";
        }
    }

    /// <summary>
    /// The desktop's UI font family — Segoe UI Variable Text on Windows 11,
    /// Segoe UI before it. Read rather than hardcoded, so a machine configured
    /// otherwise is followed.
    /// </summary>
    private static string? ReadUiFont()
    {
        try
        {
            var font = default(Native.LOGFONTW);

            if (!Native.SystemParametersInfo(
                    Native.SPI_GETICONTITLELOGFONT,
                    (uint)Marshal.SizeOf<Native.LOGFONTW>(),
                    ref font,
                    0))
                return null;

            // The face name is a fixed 32-unit field padded with NULs, not a
            // string — everything from the first NUL on is uninitialised.
            ReadOnlySpan<ushort> raw = font.lfFaceName;
            var name = MemoryMarshal.Cast<ushort, char>(raw);

            var end = name.IndexOf('\0');
            if (end >= 0) name = name[..end];

            return name.IsEmpty ? null : new string(name);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("theme", ex);
            return null;
        }
    }

    /// <summary>
    /// One background thread per key, each blocked in RegNotifyChangeKeyValue
    /// until something changes. Blocking is the simple form of this API and
    /// costs a thread that is asleep rather than spinning; the alternative is an
    /// event handle and a wait loop for no behavioural gain.
    ///
    /// **Background threads**, so a wait that never returns cannot keep the
    /// process alive at exit — there is no way to cancel a blocking wait on a
    /// registry key from outside it.
    /// </summary>
    private void Watch(string subKey)
    {
        if (Native.RegOpenKeyEx(
                Native.HKEY_CURRENT_USER, subKey, 0, Native.KEY_READ, out var key)
            != Native.ERROR_SUCCESS)
            return;

        var thread = new Thread(() =>
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var status = Native.RegNotifyChangeKeyValue(
                        key, watchSubtree: false, Native.REG_NOTIFY_CHANGE_LAST_SET,
                        eventHandle: 0, asynchronous: false);

                    if (status != Native.ERROR_SUCCESS) break;
                    if (_stopping.IsCancellationRequested) break;

                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Quiet.Swallowed("theme", ex);
            }
            finally
            {
                Native.RegCloseKey(key);
            }
        })
        {
            IsBackground = true,
            Name = "vaktari-theme-watch",
        };

        thread.Start();
    }

    public void Dispose()
    {
        // Stops the loops from raising Changed after disposal. It cannot
        // interrupt a wait already in progress, which is why those threads are
        // background threads.
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
