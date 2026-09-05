using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The New submenu, which on Windows is Explorer's — the per-extension
/// <c>ShellNew</c> keys under HKEY_CLASSES_ROOT.
///
/// **Synthetic keys, not this machine's.** What ShellNew holds is a fact about
/// what is installed: Word puts a .docx there, VMware puts thirteen, and a
/// clean CI box has four. Asserting on any of them would be asserting that this
/// particular machine has Office. So the shapes below are the ones measured on
/// Windows 11 26200, handed to the interpreter directly, and the registry walk
/// that produces them has a seam of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellNewTemplatesTests : IDisposable
{
    /// <summary>The 22 bytes HKCR\.zip\ShellNew carries: the end-of-central-directory
    /// record of an empty archive, which is what makes the file a zip a reader opens.</summary>
    private static readonly byte[] EmptyZip =
        [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly TempTree _tree = new();

    public void Dispose()
    {
        WindowsTemplates.Override = null;
        _tree.Dispose();
    }

    // ---- what Discover reads ------------------------------------------------

    /// <summary>
    /// **The menu used to be fed by <c>%APPDATA%\Microsoft\Windows\Templates</c>,
    /// measured at 0 files.** Nothing populates that folder — not Windows, not
    /// an installer — so the row was there and offered nothing on the machine
    /// where every application had registered a ShellNew key.
    /// </summary>
    [WindowsFact]
    public void Discover_offers_what_ShellNew_holds()
    {
        WindowsTemplates.Override = () =>
            [new ShellNewKey(".vktest") { NullFile = true, TypeName = "Vaktari Test Document" }];

        var offered = new WindowsTemplates().Discover();

        var one = Assert.Single(offered);
        Assert.Equal("Vaktari Test Document", one.Name);
        Assert.Equal("Vaktari Test Document.vktest", one.Path);
    }

    /// <summary>
    /// **A source check, because no test can populate the folder it stands in
    /// for.** <c>%APPDATA%\Microsoft\Windows\Templates</c> is a real per-user
    /// folder outside any temp tree, and it measures 0 files here — so a
    /// Discover that read it again on top of the registry would hand back the
    /// same rows as one that did not, and
    /// <see cref="Discover_offers_what_ShellNew_holds"/> would stay green. What
    /// that test does catch is the fault as it actually shipped, where the
    /// folder was read *instead* of the registry.
    ///
    /// So this one asks the file: neither the folder nor a directory walk is
    /// named in it. Both halves have a mutation — spelling
    /// <see cref="Environment.SpecialFolder"/>.ApplicationData where the seed
    /// folder says Windows, and returning a
    /// <c>Directory.EnumerateFiles</c> of that folder from Discover.
    /// </summary>
    [WindowsFact]
    public void The_source_names_neither_the_roaming_folder_nor_a_directory_walk()
    {
        var source = RepoSource.Read("src", "Vaktari.Windows", "WindowsTemplates.cs");

        Assert.DoesNotContain("SpecialFolder.ApplicationData", source);
        Assert.DoesNotContain("Directory.", source);
    }

    /// <summary>
    /// **Read once per process, unlike the Linux provider.** The listing's
    /// context menu calls RefreshTemplates on every right-click; the walk was
    /// measured at 111-119 ms over five runs — 1,104 extension keys and 977
    /// ProgID subkeys under them — which is a tenth of a second on the UI
    /// thread per menu. A ShellNew key changes when software is installed, not
    /// when a file appears in a folder, so there is nothing to re-read for.
    ///
    /// This one deliberately runs with no seam, against the real registry — so
    /// it also has to put the cache back. <see cref="Dispose"/> cannot: the
    /// cache is private and only <see cref="WindowsTemplates.Forget"/> clears
    /// it, and Vaktari.Windows.Tests does not disable parallelisation, so a
    /// whole real-registry walk left cached is a walk some later test in this
    /// assembly would silently inherit.
    ///
    /// The Assert.Null after Forget is a GUARD and cannot fail: it says the
    /// seam is a seam, so that a Forget that stopped forgetting would be caught
    /// here rather than three tests later.
    /// </summary>
    [WindowsFact]
    public void The_registry_is_walked_once_and_the_answer_kept()
    {
        WindowsTemplates.Override = null;
        WindowsTemplates.Forget();

        Assert.Null(WindowsTemplates.Cached);

        try
        {
            new WindowsTemplates().Discover();

            var first = WindowsTemplates.Cached;
            Assert.NotNull(first);

            new WindowsTemplates().Discover();

            Assert.Same(first, WindowsTemplates.Cached);
        }
        finally
        {
            WindowsTemplates.Forget();
        }
    }

    /// <summary>
    /// The walk itself, against keys this test writes under
    /// <c>HKCU\Software\Classes</c> — which HKEY_CLASSES_ROOT merges, so the
    /// walk sees them exactly as it sees an installer's. The same trick
    /// WindowsShellThumbnails' tests use, and for the same reason: no
    /// administrator, nothing installed, and the same answer on any machine.
    ///
    /// **Both shapes are real and the second is the one that matters.**
    /// Measured on Windows 11 26200: <c>HKCR\.ext\ShellNew</c> — the documented
    /// shape — accounted for four entries, all shipped with the operating
    /// system, while <c>HKCR\.ext\ProgID\ShellNew</c> held nineteen and every
    /// one of them arrived with an application. Reading only the documented
    /// shape would have found Explorer's stock menu and nothing anybody
    /// installed.
    /// </summary>
    [WindowsFact]
    public void The_walk_reads_both_shapes_and_every_directive()
    {
        var seed = _tree.Write("seed.vktfile", "seed");

        try
        {
            // Shape one: straight off the extension, named through the
            // extension's own ProgID.
            Set($@"{Classes}\.vktplain", null, "Vaktari.Plain");
            Set($@"{Classes}\Vaktari.Plain", null, "Vaktari Plain Document");
            Set($@"{Classes}\.vktplain\ShellNew", "NullFile", "");

            // Shape two: under a ProgID subkey, which is where Word, Excel,
            // Publisher, Access, thirteen VMware types and Proton Drive put
            // theirs. MenuText here, so the read of that value is pinned too.
            Set($@"{Classes}\Vaktari.Nested", null, "Vaktari Nested Document");
            Set($@"{Classes}\.vktnested\Vaktari.Nested\ShellNew", "Data", new byte[] { 1, 2, 3 });
            Set($@"{Classes}\.vktnested\Vaktari.Nested\ShellNew", "MenuText", "Vaktari Menu Text");

            // A seed file on disk, the shape an installer leaves behind.
            Set($@"{Classes}\.vktfile", null, "Vaktari.Seeded");
            Set($@"{Classes}\Vaktari.Seeded", null, "Vaktari Seeded Document");
            Set($@"{Classes}\.vktfile\ShellNew", "FileName", seed);

            // **Spelled the way .contact spells it**, which is lower case,
            // while .mdb spells the same value Command. Registry value names
            // are case-insensitive and this one has to be matched that way.
            Set($@"{Classes}\.vktruns\ShellNew", "NullFile", "");
            Set($@"{Classes}\.vktruns\ShellNew", "command", "notepad.exe %1");

            // And the other kind of code, which is what .lnk and .library-ms
            // carry beside their NullFile.
            Set($@"{Classes}\.vkthook\ShellNew", "NullFile", "");
            Set($@"{Classes}\.vkthook\ShellNew", "Handler", "{ceefea1b-3e29-4ef1-b34c-fec79c4f70af}");

            // **Only the keys that begin with a dot are extensions.** HKCR's
            // top level is ProgIDs, CLSIDs and interface names as well —
            // measured, 6,040 subkeys of which 1,104 start with a dot — and the
            // extension is what the new file's name ends in, so a ProgID walked
            // as one would have offered "VaktariNotAnExtension file" and made a
            // file called exactly that.
            Set($@"{Classes}\VaktariNotAnExtension\ShellNew", "NullFile", "");

            WindowsTemplates.Override = null;
            WindowsTemplates.Forget();

            var names = new WindowsTemplates().Discover().Select(t => t.Name).ToList();

            Assert.Contains("Vaktari Plain Document", names);
            Assert.Contains("Vaktari Menu Text", names);
            Assert.Contains("Vaktari Seeded Document", names);
            Assert.DoesNotContain("VKTRUNS file", names);
            Assert.DoesNotContain("VKTHOOK file", names);
            Assert.DoesNotContain("VAKTARINOTANEXTENSION file", names);
        }
        finally
        {
            foreach (var key in Written)
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{key}", throwOnMissingSubKey: false);

            WindowsTemplates.Forget();
        }
    }

    private const string Classes = @"Software\Classes";

    private static readonly string[] Written =
    [
        ".vktplain", ".vktnested", ".vktfile", ".vktruns", ".vkthook",
        "Vaktari.Plain", "Vaktari.Nested", "Vaktari.Seeded", "VaktariNotAnExtension",
    ];

    private static void Set(string subKey, string? name, object value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key?.SetValue(name, value);
    }

    // ---- what a key turns into ---------------------------------------------

    /// <summary>
    /// NullFile is the commonest directive and names no file at all, which is
    /// the half of ShellNew the old comment declined the whole registry over.
    /// </summary>
    [WindowsFact]
    public void A_NullFile_entry_becomes_an_empty_file_of_that_extension()
    {
        var offered = Offer(new ShellNewKey(".txt")
        {
            NullFile = true,
            TypeName = "Text Document",
        });

        var one = Assert.Single(offered);

        Assert.Equal("Text Document.txt", one.Path);
        Assert.NotNull(one.Content);
        Assert.Empty(one.Content);
    }

    /// <summary>
    /// **HKCR\.zip\ShellNew carries both, and only Data opens.** A 0-byte .zip
    /// is not an archive; the 22 bytes beside the NullFile are.
    /// </summary>
    [WindowsFact]
    public void A_Data_entry_carries_its_bytes_and_beats_NullFile()
    {
        var offered = Offer(new ShellNewKey(".zip")
        {
            NullFile = true,
            Data = EmptyZip,
            TypeName = "Compressed (zipped) Folder",
        });

        var content = Assert.Single(offered).Content;

        Assert.NotNull(content);
        Assert.Equal(EmptyZip, content);
    }

    /// <summary>
    /// The shape every installed application uses — measured, Word's
    /// <c>…\VFS\Windows\ShellNew\word.docx</c>. A real file, so a copy, so no
    /// Content.
    /// </summary>
    [WindowsFact]
    public void A_FileName_entry_is_a_copy_of_that_file_and_beats_Data()
    {
        var seed = _tree.Write("word.docx", "seed");

        var offered = Offer(new ShellNewKey(".docx")
        {
            FileName = seed,
            Data = EmptyZip,
            TypeName = "Microsoft Word Document",
        });

        var one = Assert.Single(offered);
        Assert.Equal(seed, one.Path);
        Assert.Null(one.Content);
    }

    /// <summary>
    /// **The seed's leaf is the installer's name, not the row's.** Measured on
    /// Windows 11 26200, HKCR\.accdb\Access.Application.16\ShellNew names
    /// <c>…\Office16\1033\ACCESS12.ACC</c> — so a copy that let the new file
    /// take its name from the source made "New &gt; Microsoft Access Database"
    /// produce ACCESS12.ACC: not the row's name, and not the .accdb the row is
    /// for. The other four seeded rows here had the same shape (word.docx,
    /// excel12.xlsx, powerpoint.pptx, mspub.pub), so five of the six rows this
    /// provider offers were named after a file nobody had heard of. Explorer
    /// makes "New Microsoft Access Database.accdb" from that key.
    /// </summary>
    [WindowsFact]
    public void A_copied_seed_is_named_for_the_row_and_the_extension()
    {
        var seed = _tree.Write("ACCESS12.ACC", "seed");

        var offered = Offer(new ShellNewKey(".accdb")
        {
            FileName = seed,
            TypeName = "Microsoft Access Database",
        });

        var one = Assert.Single(offered);

        // What to copy, and what to call it, are two different answers.
        Assert.Equal(seed, one.Path);
        Assert.Equal("Microsoft Access Database.accdb", one.Leaf);
    }

    /// <summary>
    /// The legacy form: a bare name meant relative to <c>%SystemRoot%\ShellNew</c>.
    /// Every FileName measured on Windows 11 was absolute and that folder was
    /// gone, but the bare form is what the shape has always meant.
    /// </summary>
    [WindowsFact]
    public void A_bare_FileName_is_resolved_against_the_ShellNew_folder()
    {
        var folder = _tree.Dir("ShellNew");
        _tree.Write("ShellNew/Bitmap Image.bmp", "seed");

        var offered = WindowsTemplates.Offer(
            [new ShellNewKey(".bmp") { FileName = "Bitmap Image.bmp", TypeName = "Bitmap Image" }],
            folder);

        Assert.Equal(Path.Combine(folder, "Bitmap Image.bmp"), Assert.Single(offered).Path);
    }

    /// <summary>
    /// **An uninstaller leaves the key behind.** A row that always ends in
    /// "that file is not there any more" is worse than no row.
    /// </summary>
    [WindowsFact]
    public void A_seed_that_is_not_on_disk_is_not_offered()
    {
        var offered = Offer(new ShellNewKey(".docx")
        {
            FileName = _tree.At("uninstalled.docx"),
            TypeName = "Microsoft Word Document",
        });

        Assert.Empty(offered);
    }

    /// <summary>
    /// **.lnk says Handler and NullFile together**, and Explorer runs the
    /// handler — the shortcut wizard — rather than making an empty file. Taking
    /// the NullFile at its word would have put a 0-byte .lnk in the folder,
    /// which is a shortcut nothing can open.
    /// </summary>
    [WindowsFact]
    public void A_key_that_runs_a_handler_is_not_offered()
    {
        var offered = Offer(new ShellNewKey(".lnk")
        {
            NullFile = true,
            Runs = true,
            TypeName = "Shortcut",
        });

        Assert.Empty(offered);
    }

    /// <summary>
    /// **A ShellNew key that says nothing is not an offer.** Measured: 13 of
    /// the 24 keys on this machine hold no value whatsoever — eleven VMware
    /// types and two Proton Drive ones, e.g.
    /// <c>HKCR\.vmx\VMware.Document\ShellNew</c> with valueCount 0 — and
    /// Explorer shows no New row for any of them. Treating the bare key as an
    /// offer would have put thirteen rows in the menu that make empty files
    /// nothing on the machine opens.
    /// </summary>
    [WindowsFact]
    public void A_key_with_no_directive_at_all_is_not_offered()
    {
        var offered = Offer(new ShellNewKey(".vmx")
        {
            TypeName = "VMware virtual machine configuration",
        });

        Assert.Empty(offered);
    }

    /// <summary>
    /// **A registry this process cannot read is a machine with no templates.**
    /// The menu opens either way; the alternative is a right-click that throws
    /// out of the context menu build.
    /// </summary>
    [WindowsFact]
    public void A_registry_that_cannot_be_read_is_an_empty_menu()
    {
        WindowsTemplates.Override = () => throw new UnauthorizedAccessException("no");

        Assert.Empty(new WindowsTemplates().Discover());
    }

    // ---- what the row says --------------------------------------------------

    [WindowsFact]
    public void MenuText_names_the_row_ahead_of_the_type_name()
    {
        var offered = Offer(new ShellNewKey(".rtf")
        {
            NullFile = true,
            MenuText = "Rich Text Document",
            TypeName = "Rich Text Format",
        });

        Assert.Equal("Rich Text Document", Assert.Single(offered).Name);
    }

    [WindowsFact]
    public void The_ProgID_type_name_names_the_row_when_there_is_no_MenuText()
    {
        var offered = Offer(new ShellNewKey(".vmx")
        {
            NullFile = true,
            TypeName = "VMware virtual machine configuration",
        });

        Assert.Equal("VMware virtual machine configuration", Assert.Single(offered).Name);
    }

    /// <summary>
    /// **A leading @ is a resource reference, not a name.** Every MenuText
    /// measured on this machine is one — <c>@shell32.dll,-30318</c> — and
    /// showing it verbatim would put that string in the menu. The ProgID's
    /// plain default value says the same thing without a module load.
    /// </summary>
    [WindowsFact]
    public void A_resource_reference_is_not_a_name()
    {
        var offered = Offer(new ShellNewKey(".library-ms")
        {
            NullFile = true,
            MenuText = "@shell32.dll,-30318",
            TypeName = "Library Folder",
        });

        Assert.Equal("Library Folder", Assert.Single(offered).Name);
    }

    /// <summary>
    /// A ProgID whose default value is empty is common — measured on
    /// <c>.zip</c> and <c>.contact</c> — so there has to be an answer that
    /// names nothing but the extension.
    /// </summary>
    [WindowsFact]
    public void An_extension_no_key_names_is_offered_by_its_extension()
    {
        var offered = Offer(new ShellNewKey(".vmdk") { NullFile = true });

        Assert.Equal("VMDK file", Assert.Single(offered).Name);
    }

    /// <summary>
    /// **The label is registry text and it lands in a path.** A ProgID default
    /// value is whatever an installer wrote there; carried into
    /// NewItemName.Free unchanged, a separator in it would have created the
    /// file somewhere other than the folder the user was looking at.
    /// </summary>
    [WindowsFact]
    public void A_name_with_a_separator_in_it_cannot_leave_the_folder()
    {
        var offered = Offer(new ShellNewKey(".txt")
        {
            NullFile = true,
            TypeName = @"Bad\..\Name",
        });

        Assert.Equal("Bad..Name.txt", Assert.Single(offered).Path);
    }

    // ---- how the keys are grouped ------------------------------------------

    /// <summary>
    /// **.zip has two ShellNew keys and neither is enough on its own.**
    /// Measured: <c>HKCR\.zip\ShellNew</c> carries the Data blob and hangs off
    /// an empty ProgID, so it can be named nothing;
    /// <c>HKCR\.zip\CompressedFolder\ShellNew</c> is where the name lives.
    /// Taking one key whole would have offered either an unnamed row or a
    /// 0-byte archive.
    /// </summary>
    [WindowsFact]
    public void One_row_per_extension_takes_its_name_from_whichever_key_has_one()
    {
        var offered = WindowsTemplates.Offer(
            [
                new ShellNewKey(".zip") { NullFile = true, Data = EmptyZip },
                new ShellNewKey(".zip") { Data = EmptyZip, TypeName = "Compressed (zipped) Folder" },
            ],
            _tree.Root);

        var one = Assert.Single(offered);

        Assert.Equal("Compressed (zipped) Folder", one.Name);
        Assert.NotNull(one.Content);
        Assert.Equal(EmptyZip, one.Content);
    }

    /// <summary>
    /// **One dead key used to delete the whole extension.** The first key that
    /// said anything about seeding won the group outright, so a leftover
    /// <c>FileName</c> — the shape <c>Copy</c> exists to survive, because the
    /// seed goes with the uninstaller and the key often does not — took the row
    /// down with it even when a second key of the same extension carried
    /// perfectly good bytes. .zip is the extension with two keys on this
    /// machine, so it is the row that would have gone: the only one Windows
    /// itself ships.
    /// </summary>
    [WindowsFact]
    public void A_dead_seed_does_not_take_the_live_one_with_it()
    {
        var offered = WindowsTemplates.Offer(
            [
                new ShellNewKey(".zip") { FileName = _tree.At("uninstalled.zip") },
                new ShellNewKey(".zip") { Data = EmptyZip, TypeName = "Compressed (zipped) Folder" },
            ],
            _tree.Root);

        var one = Assert.Single(offered);

        Assert.Equal("Compressed (zipped) Folder", one.Name);
        Assert.Equal(EmptyZip, one.Content);
    }

    /// <summary>
    /// **A row is named by the keys that could have made it.** Measured: the
    /// only two keys here whose MenuText is a bare @ resource reference are
    /// <c>.lnk</c>, which names a Handler, and <c>.contact</c>, which names a
    /// lower-case <c>command</c> — both code Explorer runs and Vaktari does
    /// not. Naming off every key with the extension would have let one of those
    /// put the shortcut wizard's own label on a row that quietly makes an empty
    /// file instead.
    ///
    /// It costs nothing where the name and the seed really do live in different
    /// keys: both of .zip's carry the Data blob, which
    /// <see cref="One_row_per_extension_takes_its_name_from_whichever_key_has_one"/>
    /// holds.
    /// </summary>
    [WindowsFact]
    public void A_key_that_runs_code_does_not_name_the_row()
    {
        var offered = WindowsTemplates.Offer(
            [
                new ShellNewKey(".vkt") { Runs = true, NullFile = true, MenuText = "Shortcut wizard" },
                new ShellNewKey(".vkt") { NullFile = true, TypeName = "Vaktari Document" },
            ],
            _tree.Root);

        Assert.Equal("Vaktari Document", Assert.Single(offered).Name);
    }

    /// <summary>
    /// By name, the way XdgTemplates sorts. The registry hands them over in the
    /// alphabet of file extensions, which is not the alphabet the menu shows.
    /// </summary>
    [WindowsFact]
    public void Rows_are_sorted_by_name_rather_than_by_extension()
    {
        var offered = WindowsTemplates.Offer(
            [
                new ShellNewKey(".aaa") { NullFile = true, TypeName = "Zebra" },
                new ShellNewKey(".zzz") { NullFile = true, TypeName = "Alpha" },
            ],
            _tree.Root);

        Assert.Equal(["Alpha", "Zebra"], offered.Select(t => t.Name));
    }

    private IReadOnlyList<FileTemplate> Offer(params ShellNewKey[] keys)
        => WindowsTemplates.Offer(keys, _tree.Root);
}
