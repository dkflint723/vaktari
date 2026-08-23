using System.Formats.Tar;
using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Unpacking a downloaded icon theme.
///
/// **Two things are being proved, and the second one matters more.** That a
/// theme comes out usable, links and all, without any privilege Windows would
/// have to grant; and that an archive which is not what it claims to be cannot
/// reach outside the folder it is unpacked into, cannot leave anything
/// executable behind, and cannot fill the disk.
///
/// Archives are built here rather than checked in. A fixture would be a hundred
/// megabytes, and the interesting cases — a name that climbs out of the
/// destination, a link that points at the profile folder — are exactly the ones
/// no real theme contains.
/// </summary>
/// <summary>
/// Shares the icon-index collection because reading a theme now WRITES a cache,
/// and where it writes is a static. Run alongside the cache tests, this class
/// drops its own cache files into whichever folder those tests are asserting
/// about — which is how it was found: two of them failed intermittently, and
/// only when the whole suite ran.
/// </summary>
[Collection("icon index cache")]
public sealed class IconThemeArchiveTests : IDisposable
{
    private readonly string _root;
    private readonly string _destination;

    public IconThemeArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-arch-" + Guid.NewGuid().ToString("N")[..12]);
        _destination = Path.Combine(_root, "icons");

        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ---- building archives ------------------------------------------------

    private sealed record Item(string Name, TarEntryType Type, string? Content = null, string? Link = null);

    private static Stream TarGz(params Item[] items)
    {
        var raw = new MemoryStream();

        using (var gzip = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var item in items)
            {
                var entry = new PaxTarEntry(item.Type, item.Name);

                if (item.Link is not null) entry.LinkName = item.Link;

                if (item.Content is not null)
                    entry.DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Content));

                tar.WriteEntry(entry);
            }
        }

        raw.Position = 0;
        return raw;
    }

    private static Item Dir(string name) => new(name + "/", TarEntryType.Directory);
    private static Item File_(string name, string content = "<svg/>") =>
        new(name, TarEntryType.RegularFile, content);
    private static Item Link(string name, string target) =>
        new(name, TarEntryType.SymbolicLink, Link: target);

    /// <summary>The shape a real download has: a wrapper folder named after the
    /// repository, several themes inside it, and a variant built out of links
    /// into the theme beside it.</summary>
    private static Stream Papirus() => TarGz(
        Dir("papirus-icon-theme-master"),
        Dir("papirus-icon-theme-master/Papirus"),
        Dir("papirus-icon-theme-master/Papirus/48x48"),
        Dir("papirus-icon-theme-master/Papirus/48x48/mimetypes"),
        Dir("papirus-icon-theme-master/Papirus/48x48/places"),
        File_("papirus-icon-theme-master/Papirus/index.theme", "[Icon Theme]\nName=Papirus\n"),
        File_("papirus-icon-theme-master/Papirus/48x48/mimetypes/text-x-generic.svg"),
        File_("papirus-icon-theme-master/Papirus/48x48/places/folder.svg"),
        Link("papirus-icon-theme-master/Papirus/48x48/mimetypes/text-plain.svg", "text-x-generic.svg"),

        Dir("papirus-icon-theme-master/Papirus-Dark"),
        Dir("papirus-icon-theme-master/Papirus-Dark/48x48"),
        File_("papirus-icon-theme-master/Papirus-Dark/index.theme", "[Icon Theme]\nName=Papirus-Dark\n"),
        Link("papirus-icon-theme-master/Papirus-Dark/48x48/mimetypes", "../../Papirus/48x48/mimetypes"),

        // The things a repository carries that a theme is not made of.
        File_("papirus-icon-theme-master/Makefile", "install:\n\tcp -r ..."),
        File_("papirus-icon-theme-master/install.sh", "#!/bin/sh\nrm -rf ~\n"));

    // ---- what it should do ------------------------------------------------

    [Fact]
    public void A_downloaded_theme_lands_ready_to_use()
    {
        var installed = IconThemeArchive.Install(Papirus(), _destination);

        // Both themes, side by side, with the wrapper folder gone.
        Assert.Equal(
            ["Papirus", "Papirus-Dark"],
            installed.Themes.Select(t => Path.GetFileName(t)!).Order().ToArray());

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Papirus"));

        Assert.NotNull(theme);
        Assert.NotNull(theme!.Resolve(["folder"], 48));
    }

    /// <summary>
    /// **The link that Windows will not create.** text-plain is not a file in
    /// Papirus, it is another name for text-x-generic — and an extraction that
    /// cannot make links simply loses it. Nothing is copied to achieve this and
    /// no privilege is needed: the alias is a line of text.
    /// </summary>
    [Fact]
    public void An_icon_that_is_only_a_link_still_resolves()
    {
        IconThemeArchive.Install(Papirus(), _destination);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Papirus"))!;

        var resolved = theme.Resolve(["text-plain"], 48);

        Assert.NotNull(resolved);
        Assert.EndsWith("text-x-generic.svg", resolved!, StringComparison.Ordinal);

        // And it is genuinely an alias, not a second copy on disk.
        Assert.False(File.Exists(Path.Combine(
            _destination, "Papirus", "48x48", "mimetypes", "text-plain.svg")));
    }

    /// <summary>
    /// A whole folder linked into another theme, which is how a dark variant is
    /// built and the reason one arrives with no file icons at all.
    ///
    /// **The themes are deliberately not named as a variant pair.** Written as
    /// Papirus and Papirus-Dark this passed while folder links were being
    /// dropped entirely, because the reader's other repair — falling back to a
    /// base theme whose name the variant extends — quietly covered for it. Odin
    /// and Frigg are related only by the link, so only the link can satisfy it.
    /// </summary>
    [Fact]
    public void A_theme_built_out_of_linked_folders_resolves_through_them()
    {
        IconThemeArchive.Install(TarGz(
            Dir("pack"),
            File_("pack/Odin/index.theme", "[Icon Theme]\nName=Odin\n"),
            File_("pack/Odin/48x48/mimetypes/text-x-generic.svg"),
            File_("pack/Frigg/index.theme", "[Icon Theme]\nName=Frigg\n"),
            Link("pack/Frigg/48x48/mimetypes", "../../Odin/48x48/mimetypes")), _destination);

        var frigg = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Frigg"));

        Assert.NotNull(frigg);

        var resolved = frigg!.Resolve(["text-x-generic"], 48);

        Assert.NotNull(resolved);
        Assert.Contains("Odin", resolved!, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An alias whose target is another alias.**
    ///
    /// A chained target is not a file on disk — it is only another line in the
    /// index — so looking for it on the filesystem finds nothing, and the entry
    /// used to be dropped in silence. Whole themes are built this way: Kora
    /// 2.0.4 chains all three names the reader probes with, so it resolved
    /// nothing, was refused as not an icon theme at all, and never appeared in
    /// Settings — while its folder icon, a real file, would have worked.
    ///
    /// **Four hops, not two.** The report that surfaced this said Kora's chains
    /// ran one to three deep and that a small depth limit would do; measuring
    /// the theme found thirty-one aliases needing a fourth hop. A limit chosen
    /// from the common cases would have dropped exactly those and looked fine.
    /// </summary>
    [Fact]
    public void An_alias_that_names_another_alias_follows_the_chain()
    {
        IconThemeArchive.Install(TarGz(
            Dir("pack"),
            File_("pack/Odin/index.theme", "[Icon Theme]\nName=Odin\n"),
            File_("pack/Odin/48x48/mimetypes/application-document.svg"),
            Link("pack/Odin/48x48/mimetypes/application-text.svg", "application-document.svg"),
            Link("pack/Odin/48x48/mimetypes/text-plain.svg", "application-text.svg"),
            Link("pack/Odin/48x48/mimetypes/text-x-generic.svg", "text-plain.svg"),
            File_("pack/Odin/48x48/places/folder.svg")), _destination);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Odin"));

        Assert.NotNull(theme);

        // One, two, and three hops from a real file, all landing on it.
        foreach (var name in new[] { "application-text", "text-plain", "text-x-generic" })
        {
            var resolved = theme!.Resolve([name], 48);

            Assert.NotNull(resolved);
            Assert.EndsWith("application-document.svg", resolved!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// **Two aliases naming each other.** Kora has six such cycles, which the
    /// report that surfaced the chain bug did not mention at all — so a walk
    /// bounded only by a depth number would have followed each of them to that
    /// number on every lookup. Terminating is the assertion; the test hangs
    /// rather than fails if this regresses.
    /// </summary>
    [Fact]
    public void Aliases_that_name_each_other_resolve_to_nothing_and_stop()
    {
        IconThemeArchive.Install(TarGz(
            Dir("pack"),
            File_("pack/Odin/index.theme", "[Icon Theme]\nName=Odin\n"),
            File_("pack/Odin/48x48/mimetypes/text-x-generic.svg"),
            Link("pack/Odin/48x48/places/here.svg", "there.svg"),
            Link("pack/Odin/48x48/places/there.svg", "here.svg")), _destination);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Odin"));

        // The theme is still read: one bad pair is not a reason to lose it.
        Assert.NotNull(theme);
        Assert.NotNull(theme!.Resolve(["text-x-generic"], 48));

        // And the cycle contributes nothing rather than looping.
        Assert.Null(theme.Resolve(["here"], 48));
        Assert.Null(theme.Resolve(["there"], 48));
    }

    /// <summary>
    /// **A destination written with forward slashes.** Windows accepts them
    /// everywhere, and GetFullPath turns them into backslashes — so a path that
    /// had been normalised and one that had not shared no common prefix, every
    /// containment check said "outside", and all fifty thousand of Papirus's
    /// links were silently dropped. The theme still installed and still looked
    /// like it had worked; it had simply lost every icon that was an alias,
    /// which for folders is all of them at the size a listing asks for.
    ///
    /// Found by installing the real Papirus and looking, not by any test here.
    /// </summary>
    [Fact]
    public void A_destination_written_with_forward_slashes_is_the_same_folder()
    {
        var slashed = _destination.Replace('\\', '/');

        IconThemeArchive.Install(Papirus(), slashed);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Papirus"))!;

        Assert.NotNull(theme.Resolve(["text-plain"], 48));
    }

    // ---- what it must refuse ----------------------------------------------

    /// <summary>
    /// **Nothing that is not an icon is written.** A theme archive is also a
    /// source repository: it carries makefiles, shell scripts and whatever else
    /// its authors keep in there. None of it is needed and none of it is
    /// written, so there is nothing to decide about afterwards.
    /// </summary>
    [Fact]
    public void Only_icons_come_out_of_the_archive()
    {
        IconThemeArchive.Install(Papirus(), _destination);

        var written = Directory
            .EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();

        Assert.DoesNotContain("install.sh", written);
        Assert.DoesNotContain("Makefile", written);

        Assert.All(written, name =>
            Assert.True(
                name!.EndsWith(".svg", StringComparison.Ordinal)
                || name.EndsWith(".png", StringComparison.Ordinal)
                || name is "index.theme" or IconThemeArchive.AliasIndex,
                $"'{name}' should not have been written"));
    }

    /// <summary>
    /// **The oldest bug in archive readers.** An entry may call itself anything
    /// at all, including a path that climbs out of the folder it is being
    /// unpacked into; a reader that just combines the two writes exactly where
    /// it is told. Named for the icon it pretends to be, so that a check on the
    /// extension alone would let it through.
    /// </summary>
    [Fact]
    public void An_entry_that_climbs_out_of_the_destination_is_refused()
    {
        // Two depths, because the unpacking happens in a folder inside the
        // destination: one ".." too few and an escape lands beside the themes
        // rather than outside them, which is still a file written where no
        // file was asked for.
        IconThemeArchive.Install(TarGz(
            Dir("theme"),
            File_("theme/index.theme", "[Icon Theme]\nName=theme\n"),
            File_("theme/../../near.svg"),
            File_("theme/../../../far.svg"),
            File_("theme/48x48/places/folder.svg")), _destination);

        // **Asserted as a sweep rather than against guessed paths.** Written
        // the other way round this test passed with the containment check
        // removed, because it looked for the escaped file in two places and it
        // had landed in a third.
        var themes = Path.Combine(_destination, "theme") + Path.DirectorySeparatorChar;

        var stray = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(themes, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(stray);

        // The rest of the archive still arrives: one bad entry is skipped, not
        // a reason to abandon a theme that is otherwise fine.
        Assert.True(File.Exists(Path.Combine(_destination, "theme", "48x48", "places", "folder.svg")));
    }

    /// <summary>
    /// The same climb, by way of a link — which is the form it takes in
    /// practice, and the one 7-Zip refuses with "Dangerous link path was
    /// ignored". An alias pointing outside the theme would make Vaktari read a
    /// file from somewhere else entirely and draw it in a listing.
    /// </summary>
    [Fact]
    public void A_link_that_points_outside_the_destination_is_not_recorded()
    {
        IconThemeArchive.Install(TarGz(
            Dir("theme"),
            File_("theme/index.theme", "[Icon Theme]\nName=theme\n"),
            File_("theme/48x48/mimetypes/text-x-generic.svg"),
            Link("theme/48x48/mimetypes/folder.svg", "../../../../../secret.svg"),
            Link("theme/48x48/mimetypes/text-plain.svg", "text-x-generic.svg")), _destination);

        var theme = Path.Combine(_destination, "theme");
        var index = Path.Combine(theme, IconThemeArchive.AliasIndex);

        // **Asserted on what was recorded, not on what happened to resolve.**
        // Written as "the icon does not come back" this passed with the
        // containment check removed: the escaping line WAS recorded, and then
        // missed its target only because publishing moves the theme up a level
        // and the relative path no longer reached. That is an accident, not a
        // guarantee.
        Assert.True(File.Exists(index), "the alias index should have been written");

        var inside = Path.GetFullPath(_destination) + Path.DirectorySeparatorChar;

        foreach (var line in File.ReadAllLines(index))
        {
            var target = Path.GetFullPath(Path.Combine(theme, line[(line.IndexOf('\t') + 1)..]));

            Assert.StartsWith(inside, target, StringComparison.OrdinalIgnoreCase);
        }

        // And the legitimate alias in the same archive still works, so this is
        // not passing because nothing was recorded at all.
        Assert.NotNull(FreedesktopIconTheme.FromFolder(theme)!.Resolve(["text-plain"], 48));
    }

    /// <summary>
    /// An archive with no theme in it leaves nothing behind — including the
    /// folder it was unpacked into, which would otherwise accumulate.
    /// </summary>
    [Fact]
    public void An_archive_with_no_theme_in_it_installs_nothing()
    {
        var installed = IconThemeArchive.Install(TarGz(
            Dir("stuff"),
            File_("stuff/notes.svg")), _destination);

        Assert.Empty(installed.Themes);
        Assert.Empty(Directory.EnumerateDirectories(_destination));
    }

    // ---- an archive somebody downloaded themselves ------------------------

    private static Stream Zip(params Item[] items)
    {
        var raw = new MemoryStream();

        using (var zip = new ZipArchive(raw, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                // A zip has no notion of a link at all, which is the whole
                // reason the tar form is preferred.
                if (item.Type is not TarEntryType.RegularFile) continue;

                using var writing = zip.CreateEntry(item.Name).Open();
                using var text = new StreamWriter(writing);

                text.Write(item.Content);
            }
        }

        raw.Position = 0;
        return raw;
    }

    /// <summary>
    /// **A .zip works too, minus what a .zip cannot carry.** Somebody who found
    /// a theme elsewhere may well have one, and refusing it because the format
    /// records no symbolic links would be refusing a theme that is mostly fine.
    /// </summary>
    [Fact]
    public void A_zip_installs_what_it_can()
    {
        var installed = IconThemeArchive.Install(Zip(
            File_("pack/Odin/index.theme", "[Icon Theme]\nName=Odin\n"),
            File_("pack/Odin/48x48/mimetypes/text-x-generic.svg"),
            File_("pack/Odin/48x48/places/folder.svg"),
            File_("pack/Odin/install.sh", "#!/bin/sh\n")), _destination);

        Assert.Single(installed.Themes);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Odin"));

        Assert.NotNull(theme);
        Assert.NotNull(theme!.Resolve(["folder"], 48));

        // The same whitelist: a zip is not a reason to relax it.
        Assert.False(File.Exists(Path.Combine(_destination, "Odin", "install.sh")));
    }

    /// <summary>
    /// A .tar.xz holding a theme, a script that should not be written, and a
    /// symbolic link.
    ///
    /// **Checked in as bytes because nothing in .NET can produce one.** There
    /// is no xz encoder in the framework and the library used to read them does
    /// not write them, so the alternative to a literal is no test at all. Built
    /// with Python's lzma module over a GNU tar:
    ///
    ///     pack/Odin/index.theme
    ///     pack/Odin/48x48/mimetypes/text-x-generic.svg
    ///     pack/Odin/48x48/places/folder.svg
    ///     pack/Odin/install.sh
    ///     pack/Odin/48x48/mimetypes/text-plain.svg -> text-x-generic.svg
    /// </summary>
    private const string ThemeTarXz =
        "/Td6WFoAAATm1rRGAgAhARwAAAAQz1jM4Cf/APxdADgYSJnKtl/lT/UgnRg18y+0ai9pGl8h/8mmEGuHHLvQxLU1"
        + "lUyEtIufEE2k7bodhm9sHEHVhRV6lmFo6wIioduZNxgjfsHN5yyxbyonAgGP3LJrKJTj4m4OAahbz3DqlsNv/hnd"
        + "pF6+gReP3p1UOqoX/rY3QugLHlbhNy1jZYE9okcIZXVFsgQn+UeB3laOb31kc3W41X6COxUNSIObW1pN/cDAjRcy"
        + "U1U6J9PTPpoD3ytHk8HvBkjsoo0vHJZzHAUn9O36OKVAmm+a0iAVH6nxe/KmAR/+RgpebWvKqenhqXf1QE2vCx/p"
        + "NoMGog133WlWAWzsIFNw6lobAADSbxckqtxjtAABmAKAUAAAGItFybHEZ/sCAAAAAARZWg==";

    /// <summary>
    /// **.xz is what the KDE Store mostly serves**, and .NET has no decoder for
    /// it at all — gzip and zip are in the framework and xz is not. Without
    /// this, half the themes somebody finds have to be recompressed before
    /// Vaktari will look at them.
    ///
    /// The links matter as much as the decompression: xz wraps a tar, and tar
    /// is the format that records them, so a theme from one arrives complete
    /// where a zip's would not.
    /// </summary>
    [Fact]
    public void A_tar_xz_installs_with_its_links_intact()
    {
        var installed = IconThemeArchive.Install(
            new MemoryStream(Convert.FromBase64String(ThemeTarXz)), _destination);

        Assert.Single(installed.Themes);
        Assert.Equal(1, installed.Aliases);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Odin"))!;

        Assert.NotNull(theme.Resolve(["folder"], 48));

        // The alias resolves to the file it names, and is not a second copy.
        var resolved = theme.Resolve(["text-plain"], 48);

        Assert.NotNull(resolved);
        Assert.EndsWith("text-x-generic.svg", resolved!, StringComparison.Ordinal);

        // And the same whitelist applies: xz is not a way in for anything else.
        Assert.False(File.Exists(Path.Combine(_destination, "Odin", "install.sh")));
    }

    /// <summary>Hands back a few bytes at a time, which every Stream is
    /// entitled to do and a network one does constantly.</summary>
    private sealed class Trickle(Stream inner) : Stream
    {
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, Math.Min(c, 7));
        public override int Read(Span<byte> b) => inner.Read(b[..Math.Min(b.Length, 7)]);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>
    /// **A stream is allowed to return less than it was asked for, and the xz
    /// decoder does not cope.**
    ///
    /// Reading an archive off a network is nothing but short reads, and
    /// sniffing the format puts a handful of bytes in front of the rest — so
    /// the very first read this makes is a short one. The result was not a
    /// clean failure but "Block check corrupt" on a perfectly good 5 MB theme,
    /// which reads as a damaged download and sends anybody looking in the wrong
    /// place entirely.
    ///
    /// Gzip and zip loop properly, so nothing showed until xz was added, and
    /// nothing in this file caught it because a MemoryStream always returns
    /// everything asked of it. Found by unpacking a real download.
    /// </summary>
    [Fact]
    public void An_archive_arriving_a_few_bytes_at_a_time_still_unpacks()
    {
        var installed = IconThemeArchive.Install(
            new Trickle(new MemoryStream(Convert.FromBase64String(ThemeTarXz))), _destination);

        Assert.Single(installed.Themes);
        Assert.Equal(1, installed.Aliases);

        var theme = FreedesktopIconTheme.FromFolder(Path.Combine(_destination, "Odin"))!;

        Assert.NotNull(theme.Resolve(["folder"], 48));
    }

    /// <summary>
    /// **The format is read from the file, not from its name.** A person
    /// choosing a file they downloaded may hand over anything at all, and
    /// guessing from the extension turns a plain mistake into a strange error
    /// from deep inside a decompressor.
    /// </summary>
    [Fact]
    public void Something_that_is_not_an_archive_says_so_plainly()
    {
        var nonsense = new MemoryStream("I am a text file, not a theme."u8.ToArray());

        var thrown = Assert.Throws<InvalidDataException>(
            () => IconThemeArchive.Install(nonsense, _destination));

        Assert.Contains(".tar.gz", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(".tar.xz", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    /// <summary>
    /// **A header may lie.** An entry is free to declare one byte and then
    /// supply as many as it likes, so the limit has to hold while the bytes are
    /// being written rather than being checked against what was claimed.
    /// </summary>
    [Fact]
    public void An_icon_far_too_large_to_be_one_stops_the_unpacking()
    {
        var enormous = new string('x', 40 * 1024 * 1024);

        var thrown = Assert.Throws<InvalidDataException>(() => IconThemeArchive.Install(TarGz(
            Dir("pack"),
            File_("pack/Odin/index.theme", "[Icon Theme]\nName=Odin\n"),
            File_("pack/Odin/48x48/mimetypes/text-x-generic.svg", enormous)), _destination));

        Assert.Contains("too large", thrown.Message, StringComparison.Ordinal);

        // And nothing of it survives: the staging folder goes with the failure.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    /// <summary>The folder a pack lands in comes from the file's name, and the
    /// double extension is the one that catches people out.</summary>
    [Theory]
    [InlineData("papirus-icon-theme-master.tar.gz", "papirus-icon-theme-master")]
    [InlineData("Tela.tgz", "Tela")]
    [InlineData("numix.zip", "numix")]
    [InlineData("odd", "odd")]
    public void The_pack_folder_is_named_after_the_file(string file, string expected)
    {
        Assert.Equal(expected, IconThemeInstaller.PackName(file));
    }

    /// <summary>Unpacking the same theme again replaces it rather than merging
    /// into it, or a smaller update would leave the previous version's icons
    /// lying underneath.</summary>
    [Fact]
    public void Installing_again_replaces_what_was_there()
    {
        IconThemeArchive.Install(Papirus(), _destination);

        var stale = Path.Combine(_destination, "Papirus", "48x48", "places", "gone.svg");
        File.WriteAllText(stale, "<svg/>");

        IconThemeArchive.Install(Papirus(), _destination);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(
            _destination, "Papirus", "48x48", "places", "folder.svg")));
    }
}
