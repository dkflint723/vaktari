using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The icons Windows itself draws for files, for people who would rather see
/// their own desktop's set than the one this application ships.
///
/// **IShellItemImageFactory rather than SHGetFileInfo.** The old API answers
/// with a 32×32 HICON and nothing larger, which is a blurry smear on a grid
/// tile; this one composes at whatever size is asked for, and returns the
/// executable's own icon and the custom folder icon that the shell would draw.
/// Not the shortcut overlay: asked this way it composes none, which is why a
/// .lnk gets its arrow from the listing's own emblem instead. It is what Explorer uses.
///
/// **SIIGBF_ICONONLY, deliberately.** Without it the shell returns a THUMBNAIL
/// where it has one, so every photograph would come back as a picture of
/// itself — which Vaktari already does, through the thumbnail provider, with
/// its own cache and its own size rules. Two systems racing to draw the same
/// cell is how you get a grid that flickers between two answers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsFileIcons : IFileIconProvider
{
    /// <summary>
    /// Keyed by EXTENSION for ordinary files, and by path for the things whose
    /// icon is their own: executables, shortcuts, folders.
    ///
    /// **Because this is called once per visible row.** Asking the shell for
    /// every .txt in a folder of four thousand is four thousand compositions of
    /// an identical picture; asking once is the difference between a listing
    /// that draws and one that crawls. Getting the key wrong the other way —
    /// caching an .exe by extension — would draw every program with the icon of
    /// whichever one was seen first, which is why those are excluded.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IconPixels?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct icons held before the cache is dropped. Comfortably
    /// more than any one folder needs, and far below what a drive walk
    /// accumulates.</summary>
    private const int MaxCached = 3000;

    /// <summary>How many are held, for the test that pins the bound.</summary>
    internal static int Cached => Cache.Count;

    /// <summary>Types whose icon belongs to the individual file rather than to
    /// the file type.</summary>
    private static readonly HashSet<string> PerFile =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".msi", ".cpl", ".scr", ".url" };

    public IconPixels? IconFor(string path, bool isDirectory, int size)
    {
        // Rounded to the sizes the shell actually composes at, so a pane at 41
        // pixels and one at 43 share a cache entry instead of each building
        // their own copy of every icon in the folder.
        var bucket = size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 48 => 48,
            <= 96 => 96,
            <= 128 => 128,
            _ => 256,
        };

        var extension = Path.GetExtension(path);

        var key = isDirectory || extension.Length == 0 || PerFile.Contains(extension)
            ? $"{path}|{bucket}"
            : $"{extension}|{bucket}";

        // **Bounded, because the per-PATH half of the key has no ceiling.**
        // Extensions are a small fixed set, but folders, shortcuts and
        // executables are keyed individually — so browsing a drive with fifty
        // thousand folders in it would hold fifty thousand bitmaps for the life
        // of the process, at a megabyte each for the large sizes.
        //
        // Cleared wholesale rather than evicted one at a time: the working set
        // is whatever folder is on screen, recomposing an icon is cheap next to
        // the bookkeeping an LRU would need on the path that has to stay fast,
        // and this only ever happens after several thousand distinct icons.
        if (Cache.Count >= MaxCached) Cache.Clear();

        return Cache.GetOrAdd(key, _ => Compose(path, bucket));
    }

    private static IconPixels? Compose(string path, int size)
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

            // BIGGERSIZEOK: the shell would otherwise scale a 48 up to 256 and
            // hand back the blur. Given the choice it returns the next size it
            // actually has, and letting the UI scale down beats scaling up.
            var hr = image.GetImage(
                new Size { cx = size, cy = size },
                IconOnly | BiggerSizeOk,
                out bitmap);

            return hr < 0 ? null : Read(bitmap);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("file-icons", ex);
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

    private const uint IconOnly = 0x00000004;
    private const uint BiggerSizeOk = 0x00000001;

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
