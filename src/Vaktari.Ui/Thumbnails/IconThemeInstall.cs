using System;
using System.Threading.Tasks;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Puts the chosen icon theme in place without making the window wait for it.
///
/// **This was the whole of a slow launch, and it hid for a long time behind a
/// measurement that could not see it.** Reading a theme means enumerating every
/// file in it and resolving its recorded links, once per theme in the
/// inheritance chain. Papirus-Dark chains to Papirus, and Papirus is a quarter
/// of a gigabyte across some fifty thousand files: measured on the machine that
/// reported the problem, <see cref="FreedesktopIconTheme.FromFolder"/> took
/// 2.8–3.1 seconds, and every lookup afterwards took none at all. The cost is
/// entirely in building the index, and it was being paid on the UI thread in
/// the MainWindow constructor — before Show, so nothing was on screen for the
/// duration. A launch that should take 300 ms took 1,750.
///
/// **Two paths, and the ordinary one is the fast one.** A theme whose index is
/// already cached is read before the window opens, because doing so costs
/// milliseconds and the icons are then right from the first frame. Only a theme
/// nobody has read yet — a fresh choice, or one whose folder changed — falls
/// back to opening on the platform's own icons and swapping when the build
/// finishes. That swap is visible, and it now happens once per theme rather
/// than once per launch.
///
/// Nothing new is required to make the swap safe, because a theme can already
/// be exchanged at runtime: following the desktop's colour scheme does exactly
/// this, through the same two calls.
/// </summary>
internal static class IconThemeInstall
{
    /// <summary>
    /// Applies a cached theme outright, or the platform's icons followed by the
    /// theme once it has been built off the calling thread.
    ///
    /// Everything goes through <paramref name="apply"/>, which is what marshals
    /// back to the UI thread — the build must not, and the caller is the only
    /// part of this that knows how.
    ///
    /// The returned task is for tests to await. Nothing in the window does:
    /// awaiting it in the constructor would restore precisely the stall this
    /// exists to remove.
    /// </summary>
    public static Task Begin(
        string? folder,
        IIconThemeProvider? platformIcons,
        Func<string?, IIconThemeProvider?> cached,
        Func<string?, IIconThemeProvider?> build,
        Action<IIconThemeProvider?> apply)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(apply);

        // No theme chosen is the common case and must cost nothing — not even a
        // thread, and not a look at the cache. On Windows the platform's icons
        // are null, which is not a gap: a null provider is what makes
        // IconLoader fall back to the shell's own per-file icons.
        if (string.IsNullOrEmpty(folder))
        {
            apply(platformIcons);
            return Task.CompletedTask;
        }

        // Straight to the theme, with the platform's icons never applied. They
        // would only be replaced before anything painted, and every apply drops
        // each icon cached so far.
        if (cached(folder) is { } ready)
        {
            // Said out loud, because the two paths look identical from outside
            // and differ by more than a second. Working out which one a launch
            // took cost a long diagnosis once already.
            Console.Error.WriteLine("[vaktari] icon theme: from cache, before first paint");

            apply(ready);
            return Task.CompletedTask;
        }

        Console.Error.WriteLine(
            "[vaktari] icon theme: nothing cached — reading it in the background, "
            + "so icons change once shortly after this window opens");

        // First sight of this theme. Something to draw with now, the real thing
        // shortly — and the build leaves a cache behind, so this is the only
        // launch that sees the difference.
        apply(platformIcons);

        return Task.Run(() =>
        {
            // A folder that no longer holds a usable theme returns null, and
            // the platform's icons stay. Replacing them with nothing would take
            // away the icons the window is already drawing with.
            if (build(folder) is { } theme) apply(theme);
        });
    }
}
