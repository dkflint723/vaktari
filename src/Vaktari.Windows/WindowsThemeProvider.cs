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
public sealed class WindowsThemeProvider : IThemeProvider
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>
    /// Where Settings &gt; Accessibility &gt; Text size writes the slider:
    /// <c>TextScaleFactor</c>, a REG_DWORD percentage from 100 to 225. The same
    /// number WinRT's <c>UISettings.TextScaleFactor</c> reports, read from the
    /// registry rather than through WinRT because this application is trimmed
    /// and AOT-published and a projection assembly for one integer is a cost
    /// nobody would defend.
    /// </summary>
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility";

    /// <summary>
    /// **One set of watchers for the whole process, shared by every window.**
    /// Each provider started three threads of its own, each blocked in
    /// RegNotifyChangeKeyValue — and a blocking wait on a registry key cannot
    /// be called off from outside, so Dispose stopped them raising and never
    /// stopped them. Every window that built its own services built a
    /// provider: measured, opening and closing thirty windows took a test
    /// process from 14 threads to 120, three a window, and a full Ui test
    /// run ended near 740 (0.11.1 QA). The keys are the user's, not the
    /// window's, so one wait per key answers for every window there is.
    ///
    /// So the watches are one per key for the process, and <see cref="Changed"/>
    /// is the shared event: subscribing through any provider is subscribing to
    /// every key, and a window that closes takes its handler back off it
    /// (MainWindow.OnClosed) — the static event holds no window that has said
    /// goodbye.
    ///
    /// **And no thread at all now, and only the keys a setting follows.** Each
    /// watch is an asynchronous, thread-agnostic notification on an event
    /// that the thread pool's one shared wait thread waits on (<see cref="KeyWatch"/>),
    /// so a key can be stopped as well as started — which a blocked wait could
    /// not be. <see cref="Follow"/> arms the keys the window's settings defer
    /// to the desktop on, and disarms the rest.
    /// </summary>
    private static readonly Lock Gate = new();

    private static EventHandler? _changed;

    /// <summary>The armed watch on each of the three keys, by key.</summary>
    private static readonly Dictionary<string, KeyWatch> Armed = new(StringComparer.Ordinal);

    /// <summary>Whether <see cref="Follow"/> has been called before: a key
    /// armed on a later call may have changed while it was not watched.</summary>
    private static bool _followed;

    /// <summary>How many key watches this process has started — one per key
    /// that a setting follows, however many providers are made. For the tests.</summary>
    internal static int WatchersStarted => Volatile.Read(ref _watchersStarted);

    private static int _watchersStarted;

    /// <summary>The keys armed now. For the tests.</summary>
    internal static IReadOnlyCollection<string> Watched
    {
        get { lock (Gate) return [.. Armed.Keys]; }
    }

    /// <summary>
    /// Watches the keys behind what the window follows, and stops watching
    /// the rest:
    ///
    /// - **Personalize** (AppsUseLightTheme) while the lightness follows the
    ///   desktop — the default — or the desktop's colours are layered on,
    ///   since those bring the desktop's lightness with them;
    /// - **DWM** (the accent) only while the desktop's colours are on;
    /// - **Accessibility** (TextScaleFactor) while the text size follows the
    ///   desktop — moving that slider is a scheme change as far as the window
    ///   is concerned, and fires none of the events the other two keys do.
    ///
    /// Process-wide, like the watches: the first window's call and a second
    /// window's land on the same three slots. A key armed after the first call
    /// raises <see cref="Changed"/> once, because it may have changed while
    /// nothing watched it.
    ///
    /// **Armed against the key as it exists at the time**, which is the
    /// measured limit: a key that cannot be opened is not watched, and the
    /// value arrives at the next palette read instead — never missed, only
    /// late. On the machine this was written on all three are present.
    /// </summary>
    public void Follow(ThemeNeeds needs)
    {
        var raise = false;

        lock (Gate)
        {
            raise |= Want(PersonalizeKey, (needs & (ThemeNeeds.Lightness | ThemeNeeds.Colours)) != 0);
            raise |= Want(DwmKey, (needs & ThemeNeeds.Colours) != 0);
            raise |= Want(AccessibilityKey, (needs & ThemeNeeds.TextSize) != 0);

            raise &= _followed;
            _followed = true;
        }

        if (raise) Notify();
    }

    /// <summary>Arms or disarms one key; true when it was newly armed.</summary>
    private static bool Want(string subKey, bool wanted)
    {
        if (wanted)
        {
            if (Armed.ContainsKey(subKey)) return false;

            if (Watch(subKey) is not { } watch) return false;

            Armed[subKey] = watch;

            // **A watch that ends on its own leaves its slot** (QA). A key
            // deleted under it — or any re-arm that fails — ends the watch,
            // and a slot still holding the dead one would make every later
            // Follow think the key was watched, so it was never armed again.
            _ = watch.Ended.ContinueWith(
                _ =>
                {
                    lock (Gate)
                        if (Armed.TryGetValue(subKey, out var current) && ReferenceEquals(current, watch))
                            Armed.Remove(subKey);
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return true;
        }

        if (Armed.Remove(subKey, out var armed)) _ = armed.Stop();

        return false;
    }

    /// <summary>What <see cref="Follow"/> does for one key, on a key of the
    /// test's own: the three real ones are the user's settings.</summary>
    internal static bool FollowKey(string subKey, bool wanted)
    {
        lock (Gate) return Want(subKey, wanted);
    }

    /// <summary>Raised on a watcher thread when any of the three keys
    /// changes, for every subscriber of every provider.</summary>
    public event EventHandler? Changed
    {
        add { lock (Gate) _changed += value; }
        remove { lock (Gate) _changed -= value; }
    }

    /// <summary>
    /// Tells every subscriber that a key changed — what a watcher thread does
    /// when its wait returns.
    ///
    /// **Each handler on its own, each throw swallowed on its own.** A plain
    /// Invoke let one subscriber's exception out of the watcher's loop: the
    /// handlers after it never heard the change, and the watcher's catch
    /// ended its thread for good, so the process stopped hearing that key
    /// until it restarted.
    /// </summary>
    internal static void Notify()
    {
        if (Volatile.Read(ref _changed) is not { } changed) return;

        foreach (var handler in changed.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Quiet.Swallowed("theme", ex);
            }
        }
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

            // **And that null is exactly why the text size needed its own
            // field.** Windows says how big text should be as a percentage,
            // under Accessibility, and never as a point size — so a UI layer
            // reading only FontSize followed the desktop's text size on Plasma
            // and ignored "Make text bigger" completely.
            TextScale = ScaleFromPercent(Native.ReadDword(AccessibilityKey, "TextScaleFactor")),

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
    /// The slider's percentage as the multiplier the UI layer wants, or null
    /// when the value says nothing this can act on.
    ///
    /// **Null for nothing read AND for a number outside 100-225**, which is the
    /// range the setting is documented to write. Both mean the same thing here:
    /// the desktop did not state a text size, so the application's own default
    /// applies. Clamping an out-of-range number instead would take a value
    /// written by something other than the slider and act on it.
    ///
    /// **A default machine states 100, it does not stay silent.** Measured on
    /// the Windows 11 machine this was written on, with the slider untouched:
    /// <c>TextScaleFactor</c> is present under
    /// <c>HKCU\Software\Microsoft\Accessibility</c> and reads 0x64, so 1.0 is
    /// the ordinary answer and null is the unusual one — a Windows without the
    /// value, or a read that failed, both of which <c>Native.ReadDword</c>
    /// hands over as null rather than as an error.
    ///
    /// Pure and internal so it can be tested without a registry: the conversion
    /// is the part with a decision in it.
    /// </summary>
    internal static double? ScaleFromPercent(uint? percent)
        => percent is >= 100 and <= 225 ? percent.Value / 100.0 : null;

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
    /// Starts a watch on one HKCU key: <see cref="Notify"/> each time a value
    /// under it is written, until <see cref="KeyWatch.Stop"/> or until the key
    /// is deleted. No thread of its own — see <see cref="KeyWatch"/>.
    ///
    /// Internal so a test can watch a key of its own, which it can change and
    /// delete — the three real ones are the user's settings. Returns null when
    /// the key cannot be opened. The watch's <see cref="KeyWatch.Ended"/> lets
    /// such a test wait it out: deleting a watched key wakes the wait before
    /// it fails, and a raise from that must not land in whatever test runs
    /// next.
    /// </summary>
    internal static KeyWatch? Watch(string subKey)
    {
        var watch = KeyWatch.Start(subKey, Notify);

        if (watch is not null) Interlocked.Increment(ref _watchersStarted);

        return watch;
    }
}
