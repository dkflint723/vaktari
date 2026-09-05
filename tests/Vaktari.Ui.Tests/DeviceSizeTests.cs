using Avalonia;
using Avalonia.Controls;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Avalonia.Headless.XUnit;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How big a picture is asked for.
///
/// **Every icon and thumbnail in a listing was asked for in logical units and
/// drawn into device ones.** The row templates ask for 32, 48 and 64; those are
/// layout units, and on a display at 150% one of them is one and a half pixels.
/// So a 32 became 48 pixels of screen with a 32-pixel bitmap stretched over it,
/// on most of what a file manager draws. Nothing in the UI assembly read
/// RenderScaling before this — a repository-wide search for it returned nothing.
///
/// Measured on the other side too: asking WindowsFileIcons for 16, 32, 40, 48,
/// 64 and 96 returned 16x16, 32x32, 48x48, 48x48, 96x96 and 96x96. It rounds up
/// to a size it composes at and never consults the display, so the number it is
/// handed is the whole of what decides the detail.
/// </summary>
public class DeviceSizeTests
{
    /// <summary>
    /// **The whole finding**, at the scaling most high-DPI laptops ship at: a
    /// 32-unit slot is 48 pixels of screen, so 48 pixels is what gets asked for.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.5, 32, 48)]
    [InlineData(1.5, 48, 72)]
    [InlineData(2.0, 32, 64)]
    [InlineData(2.0, 64, 128)]
    [InlineData(1.25, 32, 40)]
    public void A_scaled_display_asks_for_the_pixels_it_will_draw(
        double scaling, int logical, int expected)
        => Assert.Equal(expected, Scaled(scaling, logical));

    /// <summary>
    /// **At 100% nothing changes at all**, which is what keeps every existing
    /// cache entry, every existing bucket and every existing test the same.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void An_unscaled_display_asks_for_exactly_what_it_always_did(int logical)
        => Assert.Equal(logical, Scaled(1.0, logical));

    /// <summary>
    /// Rounded UP, not to nearest: half a pixel short is still a stretch, and
    /// both providers round up to a size they have anyway. 1.1 of 32 is 35.2.
    /// </summary>
    [AvaloniaFact]
    public void A_fractional_scaling_rounds_up()
        => Assert.Equal(36, Scaled(1.1, 32));

    /// <summary>
    /// A nonsense scaling cannot produce a zero-pixel request, which is the one
    /// answer a provider cannot do anything with.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void An_impossible_scaling_asks_for_the_logical_size(double scaling)
        => Assert.Equal(32, Scaled(scaling, 32));

    /// <summary>
    /// **A control with no window falls back to 1.0**, which is the one half of
    /// the visual-tree read a test here can reach: the headless platform
    /// renders at 1.0 and offers no way to claim otherwise, so DeviceSize.For
    /// says in its own doc that it is a guard, and the arithmetic it delegates
    /// to is what everything above pins.
    /// </summary>
    [AvaloniaFact]
    public void A_control_with_no_window_is_not_scaled()
        => Assert.Equal(32, DeviceSize.For(new Image(), 32));

    private static int Scaled(double scaling, int logical) => DeviceSize.Scale(scaling, logical);
}

/// <summary>
/// And that the two things a listing draws actually ask that way.
///
/// The arithmetic being right is worth nothing if the row still hands over the
/// number from the markup, and at 1.0 — which is all the headless platform
/// renders at — the two are the same number. So the display is stood in for,
/// through the same kind of seam the Linux mount table and the Windows Quick
/// access walk are given.
/// </summary>
public sealed class DeviceSizeCallSiteTests : IDisposable
{
    private readonly Func<Visual, double>? _before = DeviceSize.ScalingOverride;
    private readonly IFileIconProvider? _iconsBefore = IconLoader.Files;

    /// <summary>
    /// **An icon THEME switches the desktop-icons route off entirely**, and a
    /// sibling class in this assembly leaves one installed. UseSystemIcons is
    /// `Files is not null && Provider is null && the setting`, so without
    /// clearing this the row never asks the provider anything and the
    /// assertion below fails on a full run while passing on its own — which is
    /// exactly what it did.
    /// </summary>
    private readonly IIconThemeProvider? _themeBefore = IconLoader.Provider;

    private readonly IThumbnailProvider? _thumbsBefore = ThumbnailLoader.Provider;
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    public void Dispose()
    {
        DeviceSize.ScalingOverride = _before;
        IconLoader.Files = _iconsBefore;
        IconLoader.Provider = _themeBefore;
        ThumbnailLoader.Provider = _thumbsBefore;
        AppSettings.Apply(_settingsBefore);

        ThumbnailLoader.Forget();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Remembers the size it was asked for and answers nothing.
    ///
    /// The size is also published as a task, because the row asks from a
    /// thread-pool continuation: pumping the dispatcher a fixed number of
    /// times is a guess about how busy the machine is, and this one guessed
    /// wrong the first time it ran beside three other builds.
    /// </summary>
    private sealed class RecordingIcons : IFileIconProvider
    {
        private readonly TaskCompletionSource<int> _asked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<int> Asked => _asked.Task;

        public IconPixels? IconFor(string path, bool isDirectory, int size)
        {
            _asked.TrySetResult(size);
            return null;
        }
    }

    /// <summary>The same, for the layer above it.</summary>
    private sealed class RecordingThumbs : IThumbnailProvider
    {
        private readonly TaskCompletionSource<int> _asked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<int> Asked => _asked.Task;

        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
        {
            _asked.TrySetResult(size);
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<IconPixels?>(null);
    }

    /// <summary>
    /// **The row icon asks in device pixels.** A 32-unit slot on a display at
    /// 150% is 48 pixels of screen, and the shell answers with exactly the
    /// number it is handed — measured: 16, 32, 40, 48, 64 and 96 came back as
    /// 16x16, 32x32, 48x48, 48x48, 96x96 and 96x96.
    /// </summary>
    [AvaloniaFact]
    public async Task The_row_icon_asks_in_device_pixels()
    {
        var icons = new RecordingIcons();

        IconLoader.Files = icons;

        // Both halves of UseSystemIcons, because both are static and a sibling
        // class sets the other one.
        IconLoader.Provider = null;

        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { UseSystemIcons = true },
        });

        DeviceSize.ScalingOverride = _ => 1.5;

        var image = new Image();

        RowIcon.SetSize(image, 32);
        RowIcon.SetEntry(
            image,
            new FileEntry("x.txt", Path.Combine(Path.GetTempPath(), "x.txt"), 1, DateTime.UtcNow, EntryFlags.None));

        Assert.Equal(48, await Answered(icons.Asked));
    }

    /// <summary>
    /// **And so does the thumbnail**, or one cell would hold a crisp icon
    /// behind a soft picture.
    /// </summary>
    [AvaloniaFact]
    public async Task The_thumbnail_asks_in_device_pixels()
    {
        var thumbs = new RecordingThumbs();

        ThumbnailLoader.Forget();
        ThumbnailLoader.Provider = thumbs;

        DeviceSize.ScalingOverride = _ => 1.5;

        var image = new Image();

        ThumbnailImage.SetSize(image, 32);
        ThumbnailImage.SetPath(image, Path.Combine(Path.GetTempPath(), "photo.png"));

        Assert.Equal(48, await Answered(thumbs.Asked));
    }

    /// <summary>
    /// Waits for the size the row actually asked for, rather than pumping and
    /// hoping. Both routes reach their provider from a thread-pool
    /// continuation, so nothing here needs the dispatcher to turn — and a
    /// timeout is what tells "asked for the wrong number" apart from "never
    /// asked at all", which are different bugs and looked identical when this
    /// counted dispatcher turns.
    /// </summary>
    private static async Task<int> Answered(Task<int> asked)
        => await asked.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
}
