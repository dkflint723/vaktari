using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The shell's thumbnails — the video frame, the first page, the HEIC.
///
/// **Every assertion here rests on a handler Windows itself ships**, never on
/// one an installed application registered. The photo handler behind .png and
/// .tif is in-box and reached through PerceivedType=image; .txt has no handler
/// on any Windows. Asserting ".mp4 has a thumbnail" would be asserting that
/// this particular machine has a media codec, which is a fact about the machine
/// and not about the code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellThumbnailTests : IDisposable
{
    private readonly string _folder;
    private readonly string _text;

    public WindowsShellThumbnailTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-thumbs-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);

        _text = Path.Combine(_folder, "sample.txt");
        File.WriteAllText(_text, new string('x', 4096));

        // **Synthetic registrations rather than this machine's.** Measured:
        // .png and .tif — the two extensions the tests above assert on — both
        // resolve ONLY through PerceivedType=image, so the other three walks in
        // Probe were dead code as far as any test could tell, while being the
        // routes that actually carry .pdf, .mp4, .docx and .lnk here. These go
        // under HKCU\Software\Classes, which HKEY_CLASSES_ROOT merges, so the
        // probe sees them exactly as it sees a real handler: no administrator,
        // nothing installed, and the same answer on any machine.

        // Directly under the extension.
        Registry.CurrentUser.CreateSubKey($@"{Classes}\.vkdirect\{Handler}")?.Dispose();

        // Through the extension's ProgID.
        using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\.vkprog"))
            k?.SetValue(null, "vktest.Progid");
        Registry.CurrentUser.CreateSubKey($@"{Classes}\vktest.Progid\{Handler}")?.Dispose();

        // Under SystemFileAssociations for the extension.
        Registry.CurrentUser.CreateSubKey($@"{Classes}\.vksfa")?.Dispose();
        Registry.CurrentUser.CreateSubKey(
            $@"{Classes}\SystemFileAssociations\.vksfa\{Handler}")?.Dispose();

        // Under SystemFileAssociations for the perceived type.
        using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\.vkperc"))
            k?.SetValue("PerceivedType", "vktestkind");
        Registry.CurrentUser.CreateSubKey(
            $@"{Classes}\SystemFileAssociations\vktestkind\{Handler}")?.Dispose();

        // The control: an extension that exists and has no handler anywhere.
        Registry.CurrentUser.CreateSubKey($@"{Classes}\.vknone")?.Dispose();
    }

    private const string Handler = @"ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string Classes = @"Software\Classes";

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }

        foreach (var k in new[] { ".vkdirect", ".vkprog", ".vksfa", ".vkperc", ".vknone", "vktest.Progid" })
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{k}", throwOnMissingSubKey: false); }
            catch { /* a scratch key is not worth failing over */ }

        foreach (var k in new[] { ".vksfa", "vktestkind" })
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\SystemFileAssociations\{k}", throwOnMissingSubKey: false); }
            catch { /* as above */ }
    }

    /// <summary>
    /// The four places a thumbnail handler can be registered, each pinned on
    /// its own. Before these, three of Probe's four walks were dead code as
    /// far as any test could tell — deleting any one of them left the whole
    /// suite green, while they are the routes that carry .pdf, .mp4 and .docx.
    /// </summary>
    [WindowsFact]
    public void A_handler_under_the_extension_itself_is_found() =>
        Assert.True(WindowsShellThumbnails.HasHandler(".vkdirect"));

    [WindowsFact]
    public void A_handler_under_the_extensions_progid_is_found() =>
        Assert.True(WindowsShellThumbnails.HasHandler(".vkprog"));

    [WindowsFact]
    public void A_handler_under_system_file_associations_is_found() =>
        Assert.True(WindowsShellThumbnails.HasHandler(".vksfa"));

    [WindowsFact]
    public void A_handler_under_the_perceived_type_is_found() =>
        Assert.True(WindowsShellThumbnails.HasHandler(".vkperc"));

    /// <summary>The control, so "always true" cannot pass.</summary>
    [WindowsFact]
    public void An_extension_with_no_handler_anywhere_is_declined() =>
        Assert.False(WindowsShellThumbnails.HasHandler(".vknone"));

    /// <summary>A picture committed to this repository, so its shape is known.</summary>
    private static string Screenshot => Path.Combine(RepoSource.Root, "docs", "screenshot-grid.png");

    /// <summary>
    /// The registry answers "can this KIND of file have a thumbnail", and it
    /// answers differently for the two ends of the range: an in-box handler
    /// covers images through their perceived type, and nothing anywhere covers
    /// plain text.
    /// </summary>
    [WindowsFact]
    public void The_machine_says_which_extensions_have_a_handler()
    {
        Assert.True(WindowsShellThumbnails.HasHandler(".png"), "no handler for .png");
        Assert.True(WindowsShellThumbnails.HasHandler(".tif"), "no handler for .tif");

        Assert.False(WindowsShellThumbnails.HasHandler(".txt"), "a handler for .txt");
        Assert.False(WindowsShellThumbnails.HasHandler(""), "a handler for no extension at all");
    }

    /// <summary>
    /// **The flag is the feature.** Without SIIGBF_THUMBNAILONLY the shell
    /// answers a file it cannot thumbnail with that file's ICON and an HRESULT
    /// that says success, so the thumbnail layer would draw a copy of the icon
    /// layer underneath it. This is the assertion that would notice the flag
    /// going missing: measured, the same call with the flags at zero returns a
    /// 256×256 picture of the text-file icon.
    /// </summary>
    [WindowsFact]
    public void A_text_file_gets_no_thumbnail_rather_than_its_icon()
    {
        Assert.Null(ShellImage.Pixels(_text, 256, ShellImage.ThumbnailOnly, "test"));
    }

    /// <summary>
    /// And a file the shell does thumbnail comes back as a picture OF the file:
    /// this screenshot is landscape, and every icon Windows composes is square.
    /// </summary>
    [WindowsFact]
    public void An_image_comes_back_as_a_picture_of_itself()
    {
        var pixels = ShellImage.Pixels(Screenshot, 256, ShellImage.ThumbnailOnly, "test");

        Assert.NotNull(pixels);
        Assert.Equal(pixels!.Width * pixels.Height * 4, pixels.Bgra.Length);
        Assert.True(pixels.Width != pixels.Height,
            $"square at {pixels.Width}×{pixels.Height}, which is the shape of an icon");
    }

    /// <summary>
    /// The provider's per-row gate. .tif is the interesting half: Avalonia
    /// cannot decode it, so before this it was declined outright, and the shell
    /// has had a thumbnail for it all along.
    /// </summary>
    [WindowsFact]
    public void The_provider_offers_formats_it_cannot_decode_itself()
    {
        var provider = new WindowsThumbnailProvider();

        Assert.True(provider.CanThumbnail(@"C:\x\holiday.tif"), "declined .tif");
        Assert.True(provider.CanThumbnail(@"C:\x\holiday.png"), "declined .png");

        Assert.False(provider.CanThumbnail(@"C:\x\notes.txt"), "offered .txt");
        Assert.False(provider.CanThumbnail(@"C:\x\Documents"), "offered a name with no extension");
    }

    /// <summary>
    /// The link that makes the feature real: the PROVIDER, asked about a format
    /// the toolkit cannot decode, hands back pixels rather than the null it
    /// used to.
    ///
    /// **The subject is a .tif holding PNG bytes**, and that is not a cheat so
    /// much as a measured property of the handler: the in-box photo thumbnailer
    /// is WIC-backed and identifies a format from the file's own signature
    /// rather than its name, so this thumbnails exactly as a real TIFF would.
    /// Writing a genuine baseline TIFF by hand would test my encoder rather than
    /// this seam, and the repository has no TIFF to copy.
    /// </summary>
    [WindowsFact]
    public async Task A_format_the_toolkit_cannot_decode_comes_back_as_pixels()
    {
        var tif = Path.Combine(_folder, "scan.tif");
        File.Copy(Screenshot, tif);

        var provider = new WindowsThumbnailProvider();

        // Nothing to point at: .tif is not one of the six the UI can decode.
        // Asked at 512 rather than 256 deliberately: BIGGERSIZEOK makes no
        // difference at 256 on this handler (256x170 either way), so 256 could
        // not tell the flags apart — at 512 it is 512x341 without and 768x511
        // with, which is what the size assertion below is for.
        Assert.Null(await provider.GetThumbnailPathAsync(tif, 512, default));

        var pixels = await provider.GetThumbnailPixelsAsync(tif, 512, default);

        Assert.NotNull(pixels);
        Assert.Equal(pixels!.Width * pixels.Height * 4, pixels.Bgra.Length);
        Assert.True(pixels.Width <= 512 && pixels.Height <= 512,
            $"{pixels.Width}x{pixels.Height} is larger than the 512 asked for");
        Assert.True(pixels.Width != pixels.Height,
            $"square at {pixels.Width}×{pixels.Height}, which is the shape of an icon");
    }

    /// <summary>
    /// **A decodable image is never asked of the shell**, and the reason is the
    /// favicon rule rather than tidiness: a picture too small to enlarge gets NO
    /// thumbnail on purpose, and asking the shell as a second try would hand
    /// back exactly the upscaled mush that rule exists to prevent.
    ///
    /// The 8×8 bitmap here is written by hand so the test owns its own subject —
    /// and BMP is one of the four formats ImageSize can measure, which is what
    /// the rule needs to know.
    /// </summary>
    [WindowsFact]
    public async Task A_picture_too_small_to_enlarge_is_not_rescued_by_the_shell()
    {
        var bmp = Path.Combine(_folder, "tiny.bmp");
        File.WriteAllBytes(bmp, TinyBitmap());

        var provider = new WindowsThumbnailProvider();

        // The path answer is a deliberate null: 8 pixels against a 256 tile.
        Assert.Null(await provider.GetThumbnailPathAsync(bmp, 256, default));

        // And the pixels answer must not overturn it.
        Assert.Null(await provider.GetThumbnailPixelsAsync(bmp, 256, default));
    }

    /// <summary>
    /// The bound, without needing a deliberately broken shell extension
    /// installed: a handler that never answers must cost the row its thumbnail
    /// and nothing else.
    ///
    /// The outer Wait is what makes this a failing test rather than a hanging
    /// one if the bound is ever removed.
    /// </summary>
    [WindowsFact]
    public void A_handler_that_does_not_answer_gives_up()
    {
        var call = Task.Run(async () => await WindowsShellThumbnails.Bounded(
            () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return new IconPixels(1, 1, new byte[4]);
            },
            @"C:\x\slow.mp4",
            CancellationToken.None));

        Assert.True(call.Wait(TimeSpan.FromSeconds(6)), "the wait was not bounded");
        Assert.Null(call.Result);
    }

    /// <summary>An answer inside the bound is not thrown away — the guard on the
    /// test above, which would also pass if this never returned anything.</summary>
    [WindowsFact]
    public async Task An_answer_inside_the_bound_is_kept()
    {
        var pixels = await WindowsShellThumbnails.Bounded(
            () => new IconPixels(2, 2, new byte[16]), @"C:\x\quick.mp4", CancellationToken.None);

        Assert.NotNull(pixels);
    }

    /// <summary>
    /// **The extension map is keyed by something with no natural ceiling.** Real
    /// folders repeat a handful of extensions, but nothing enforces that, and an
    /// unbounded dictionary on the listing path is the fault the icon cache next
    /// door was already fixed for.
    /// </summary>
    [WindowsFact]
    public void The_extension_map_does_not_grow_without_limit()
    {
        for (var i = 0; i < 4200; i++) WindowsShellThumbnails.HasHandler(".vk" + i);

        Assert.True(WindowsShellThumbnails.Remembered < 4200,
            $"held {WindowsShellThumbnails.Remembered} extensions");
    }

    /// <summary>
    /// An 8×8 24-bit BMP: a 14 byte file header, a 40 byte info header, then
    /// three bytes a pixel with each row padded to four.
    /// </summary>
    private static byte[] TinyBitmap()
    {
        const int side = 8;
        const int stride = side * 3;
        var pixels = stride * side;
        var file = new byte[54 + pixels];

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);

        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(side).CopyTo(file, 18);
        BitConverter.GetBytes(side).CopyTo(file, 22);
        BitConverter.GetBytes((short)1).CopyTo(file, 26);
        BitConverter.GetBytes((short)24).CopyTo(file, 28);
        BitConverter.GetBytes(pixels).CopyTo(file, 34);

        for (var i = 54; i < file.Length; i++) file[i] = 0x40;

        return file;
    }
}
