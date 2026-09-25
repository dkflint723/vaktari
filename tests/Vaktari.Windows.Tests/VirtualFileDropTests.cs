using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Taking files out of a drop that has none on disk — which is what dragging
/// out of 7-Zip or Explorer's zip view actually is.
///
/// A real drag cannot be staged here, but the data object behind one can:
/// <see cref="NativeDropSource"/> serves the shell's formats from a native
/// pointer, which is all the code under test ever sees of a drag. Around that
/// sit the pieces where the other mistakes live — the record layout, the
/// names, and the guard on where those names may be written.
/// </summary>
[SupportedOSPlatform("windows")]
public class VirtualFileDropTests
{
    /// <summary>
    /// Builds a FILEGROUPDESCRIPTORW by hand: a UINT count, then one 592-byte
    /// record per file with the name at offset 72 in a fixed 260-character
    /// buffer.
    /// </summary>
    private static IntPtr Descriptor(bool wide, params string[] names)
    {
        var size = wide ? 592 : 332;
        var bytes = new byte[4 + (names.Length * size)];

        BitConverter.GetBytes(names.Length).CopyTo(bytes, 0);

        for (var i = 0; i < names.Length; i++)
        {
            var at = 4 + (i * size) + 72;

            var encoded = wide
                ? System.Text.Encoding.Unicode.GetBytes(names[i])
                : System.Text.Encoding.ASCII.GetBytes(names[i]);

            encoded.CopyTo(bytes, at);
        }

        var block = Marshal.AllocHGlobal(bytes.Length);

        Marshal.Copy(bytes, 0, block, bytes.Length);

        return block;
    }

    /// <summary>
    /// **A folder entry is known for one**, by the attributes the descriptor
    /// carries — asked for its contents instead, an empty folder became an
    /// empty file of its name, and everything inside it was refused. And each
    /// entry keeps its own place, which is the index its contents are asked
    /// by: a nameless entry skipped used to shift every one after it.
    /// </summary>
    [Fact]
    public void A_folder_entry_is_a_folder_and_every_entry_keeps_its_place()
    {
        var block = Descriptor(true, "", "empty", @"empty\note.txt");

        try
        {
            // The second record: FD_ATTRIBUTES in dwFlags, FILE_ATTRIBUTE_DIRECTORY at 36.
            Marshal.WriteInt32(block + 4 + 592, 0x4);
            Marshal.WriteInt32(block + 4 + 592 + 36, 0x10);

            Assert.Equal(
                [new VirtualFileDrop.Described(1, "empty", true), new VirtualFileDrop.Described(2, @"empty\note.txt", false)],
                VirtualFileDrop.Describe(block, wide: true));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_names_are_read_out_of_the_descriptor(bool wide)
    {
        var block = Descriptor(wide, "one.txt", "two.txt", "three.txt");

        try
        {
            Assert.Equal(["one.txt", "two.txt", "three.txt"], VirtualFileDrop.Parse(block, wide));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>
    /// **A name is padded with nulls, not ended by its record.** Reading the
    /// whole 260-character buffer would hand back a name with a tail of
    /// nothing attached, which no filesystem will accept.
    /// </summary>
    [Fact]
    public void A_name_stops_at_its_terminator()
    {
        var block = Descriptor(true, "notes.txt");

        try
        {
            var name = Assert.Single(VirtualFileDrop.Parse(block, wide: true));

            Assert.Equal("notes.txt", name);
            Assert.DoesNotContain('\0', name);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>An archive holding folders describes paths, not bare names.</summary>
    [Fact]
    public void A_path_inside_the_archive_survives()
    {
        var block = Descriptor(true, @"inner\deep\note.txt");

        try
        {
            Assert.Equal(@"inner\deep\note.txt", Assert.Single(VirtualFileDrop.Parse(block, true)));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Fact]
    public void An_empty_descriptor_yields_nothing()
    {
        var block = Descriptor(true);

        try
        {
            Assert.Empty(VirtualFileDrop.Parse(block, wide: true));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    // ---- where those names may be written ----------------------------------

    /// <summary>
    /// **The name comes from whatever was dragged.** An archive can carry an
    /// entry calling itself ..\..\Windows\System32\something, and writing it
    /// where it asks would be the oldest bug in unpacking — the same rule the
    /// theme unpacker applies, for the same reason.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\escape.txt")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_climbs_out_is_refused(string name)
    {
        Assert.Null(VirtualFileDrop.Contained(@"C:\temp\drop", name));
    }

    [Fact]
    public void An_ordinary_name_is_allowed()
    {
        Assert.Equal(
            @"C:\temp\drop\notes.txt",
            VirtualFileDrop.Contained(@"C:\temp\drop", "notes.txt"));

        Assert.Equal(
            @"C:\temp\drop\inner\note.txt",
            VirtualFileDrop.Contained(@"C:\temp\drop", @"inner\note.txt"));
    }

    // ---- what gets handed back ---------------------------------------------

    /// <summary>
    /// **Only the roots.** A folder dragged out of an archive describes every
    /// file inside it; handing all of them back would copy the tree flat into
    /// the destination instead of copying the folder.
    /// </summary>
    [Fact]
    public void A_folder_comes_back_as_one_thing()
    {
        var roots = VirtualFileDrop.Roots(@"C:\temp\drop",
        [
            @"C:\temp\drop\inner\a.txt",
            @"C:\temp\drop\inner\deep\b.txt",
            @"C:\temp\drop\loose.txt",
        ]);

        Assert.Equal([@"C:\temp\drop\inner", @"C:\temp\drop\loose.txt"], roots);
    }

    /// <summary>Anything that is not Avalonia's wrapper yields null rather than
    /// throwing, which is what keeps a drop from failing on a strange source.</summary>
    [Fact]
    public void Something_that_is_not_the_wrapper_yields_nothing()
    {
        Assert.Null(VirtualFileDrop.Native(new object()));
        Assert.Null(VirtualFileDrop.Native("not a data transfer"));
    }

    // ---- the data object ---------------------------------------------------

    /// <summary>
    /// **The whole conversation, from a native pointer to bytes on disk.**
    /// This is the path the published build runs: it used to go through the
    /// runtime-built COM wrappers, which NativeAOT does not have, so every
    /// archive drag in a release answered with the archive message. Under the
    /// JIT it failed one step earlier: Marshal.GetIUnknownForObject on a
    /// MicroCom proxy gives the proxy's own COM face, not the drag's, and this
    /// test fails against that code the same way.
    ///
    /// Shaped the way Avalonia hands it over: a wrapper holding _oleDataObject,
    /// which is a MicroCom proxy carrying the pointer in NativePointer.
    /// AvaloniaDropShapeTests holds Avalonia to that shape; this holds the drop
    /// to reading it. One item arrives as a stream longer than one read, the
    /// other as a memory block inside a folder, so both media and the loop are
    /// exercised.
    /// </summary>
    [WindowsFact]
    public void A_file_offered_only_as_contents_lands_on_disk()
    {
        var streamed = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 + 7)).ToArray();
        var held = "held in memory, not in a stream"u8.ToArray();

        var source = new NativeDropSource(("big.bin", streamed, true), (@"inner\note.txt", held, false));
        var unknown = new StrategyBasedComWrappers()
            .GetOrCreateComInterfaceForObject(source, CreateComInterfaceFlags.None);

        var drops = Path.Combine(Path.GetTempPath(), "Vaktari", "drops");
        string? folder = null;

        try
        {
            var drag = new AvaloniaShapedWrapper(new MicroComShapedProxy(unknown));
            var drop = new VirtualFileDrop();

            Assert.True(drop.Offers(drag), "the drop did not see a descriptor behind the native pointer");

            var roots = drop.Take(drag);

            Assert.NotEmpty(roots);
            folder = Path.GetDirectoryName(roots[0])!;
            Assert.Equal(drops, Path.GetDirectoryName(folder));

            Assert.Equal([Path.Combine(folder, "big.bin"), Path.Combine(folder, "inner")], roots);
            Assert.Equal(streamed, File.ReadAllBytes(Path.Combine(folder, "big.bin")));
            Assert.Equal(held, File.ReadAllBytes(Path.Combine(folder, "inner", "note.txt")));
        }
        finally
        {
            Marshal.Release(unknown);

            if (folder is not null && Path.GetDirectoryName(folder) == drops && Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Avalonia's OleDataObjectToDataTransferWrapper, as far as the
    /// drop can tell: a private field by that name.</summary>
    internal sealed class AvaloniaShapedWrapper(object held)
    {
        private readonly object _oleDataObject = held;

        public override string ToString() => $"wrapping {_oleDataObject}";
    }

    /// <summary>A MicroCom proxy, as far as the drop can tell: the pointer in a
    /// public NativePointer.</summary>
    internal sealed class MicroComShapedProxy(IntPtr pointer)
    {
        public IntPtr NativePointer { get; } = pointer;
    }
}
