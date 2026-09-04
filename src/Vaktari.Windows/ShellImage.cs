using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The shell's own picture of one file, as pixels.
///
/// **IShellItemImageFactory rather than SHGetFileInfo.** The old API answers
/// with a 32×32 HICON and nothing larger, which is a blurry smear on a grid
/// tile; this one composes at whatever size is asked for, and returns the
/// executable's own icon and the custom folder icon that the shell would draw.
/// It is what Explorer uses.
///
/// **One call site for two features.** The same factory answers "what icon does
/// this desktop draw" and "what thumbnail does this desktop have", and the only
/// difference between the two is a flag — see <see cref="IconOnly"/> and
/// <see cref="ThumbnailOnly"/>. Everything below the flag is identical and was
/// measured once: the HBITMAP row order, the empty alpha channel, the entry
/// point name. A second copy of that would be a second place for those lessons
/// to be relearned.
/// </summary>
internal static partial class ShellImage
{
    /// <summary>
    /// Compose an icon and never a thumbnail.
    ///
    /// **Deliberate for the icon feature.** Without it the shell returns a
    /// THUMBNAIL where it has one, so every photograph would come back as a
    /// picture of itself — which Vaktari already does, through the thumbnail
    /// provider, with its own cache and its own size rules. Two systems racing
    /// to draw the same cell is how you get a grid that flickers between two
    /// answers.
    ///
    /// Not the shortcut overlay: asked this way it composes none, which is why
    /// a .lnk gets its arrow from the listing's own emblem instead.
    /// </summary>
    internal const uint IconOnly = 0x00000004;

    /// <summary>
    /// Return a real thumbnail or nothing — never the file type's icon.
    ///
    /// **This is the whole of the thumbnail feature.** Asked without it, a file
    /// the shell cannot thumbnail comes back with its ICON instead, and an
    /// HRESULT of S_OK to say so; there is no field distinguishing the two, so
    /// the caller would cache a picture of the .txt glyph as that file's
    /// thumbnail and draw it over the icon layer that was already showing the
    /// same thing. Measured on a 4 KB .txt asked for at 256 pixels: with this
    /// flag the call came back with no bitmap, and with the flags left at zero
    /// the same call returned a 256×256 picture of the text-file icon.
    /// </summary>
    internal const uint ThumbnailOnly = 0x00000008;

    /// <summary>
    /// Let the shell answer with something LARGER than was asked for.
    ///
    /// Measured, asking for one PNG at two sizes: 512 came back 768×511 with
    /// this flag and 512×341 without; 64 came back 96×64 with it and 64×43
    /// without. Given the choice the shell hands over a size it already holds
    /// rather than resizing to order.
    ///
    /// **Icons want that and thumbnails do not.** An icon asked for at 256 that
    /// exists only at 48 is a blur, so taking the next real size and letting the
    /// UI scale is the better picture. A thumbnail asked for at 512 that arrives
    /// at 768 is a megabyte and a half of pixels instead of seven hundred
    /// kilobytes, in a cache bounded by BYTES, drawn into an Image that scales
    /// it to fit either way. And declining the flag costs a thumbnail nothing:
    /// measured, neither setting makes the shell enlarge past the source — a
    /// 256×256 PNG asked for at 512 came back 256×256 both ways.
    /// </summary>
    internal const uint BiggerSizeOk = 0x00000001;

    /// <summary>
    /// Pixels for one path at about one size, or null.
    ///
    /// Never throws: both callers run once per visible row, and an exception
    /// there is a listing that does not appear at all.
    /// </summary>
    /// <param name="area">Which feature is asking, so a swallowed failure says
    /// so — the two have very different reasons to come back empty.</param>
    internal static IconPixels? Pixels(string path, int size, uint flags, string area)
    {
        var factory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;

        try
        {
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, in ImageFactoryId, out factory) < 0)
                return null;

            var image = Wrappers.GetOrCreateObjectForComInstance(factory, CreateObjectFlags.None)
                as IShellItemImageFactory;

            if (image is null) return null;

            var hr = image.GetImage(new Size { cx = size, cy = size }, flags, out bitmap);

            return hr < 0 ? null : Read(bitmap);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed(area, ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    /// <summary>
    /// Copies an HBITMAP's pixels out.
    ///
    /// Negative height in the header asks GDI for a TOP-DOWN buffer. Left
    /// positive, DIBs come back bottom-up and every icon is drawn upside down —
    /// which looks like a rendering fault rather than a row-order convention.
    /// </summary>
    private static IconPixels? Read(IntPtr bitmap)
    {
        if (!Native.GetObject(bitmap, Marshal.SizeOf<Bitmap>(), out var info) ||
            info.bmWidth <= 0 || info.bmHeight <= 0)
            return null;

        var width = info.bmWidth;
        var height = info.bmHeight;
        var pixels = new byte[width * height * 4];

        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        var dc = Native.GetDC(IntPtr.Zero);

        try
        {
            var copied = Native.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0);
            if (copied == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }

        // **The shell returns 32-bit bitmaps whose alpha is sometimes all
        // zero** — an icon drawn from an older resource that never carried a
        // channel. Taken at face value every one of those is invisible, so a
        // fully transparent result is treated as opaque, which is what the
        // shell itself does when it composites one.
        var opaque = false;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;

            opaque = true;
            break;
        }

        if (!opaque)
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

        return new IconPixels(width, height, pixels);
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [GeneratedComInterface]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    internal partial interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out IntPtr phbm);
    }

    internal static partial class Native
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, in Guid riid, out IntPtr ppv);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(IntPtr ho);

        // **GetObjectW, named explicitly.** gdi32 exports GetObjectA and
        // GetObjectW and no plain GetObject; LibraryImport binds the exact
        // string it is given and does no A/W probing, so this threw
        // EntryPointNotFoundException on the first call — swallowed by the
        // catch around it, which turned "the icon feature does not work at
        // all" into "every icon is null", which is indistinguishable from
        // the setting being off.
        [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetObject(IntPtr h, int c, out Bitmap pv);

        [LibraryImport("gdi32.dll")]
        internal static partial int GetDIBits(
            IntPtr hdc, IntPtr hbm, uint start, uint cLines,
            [Out] byte[] lpvBits, ref BitmapInfoHeader lpbmi, uint usage);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }
}
