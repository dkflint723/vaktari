using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Owner and group on the properties sheet.
///
/// **They arrived as two lines of text.** A POSIX mode is three sets of bits
/// and two principals, and a dialog that sets the bits and not the principals
/// answers two thirds of the question — "group: read, write" says nothing until
/// you know which group. Dolphin and Nautilus both offer them.
///
/// LinuxPropertiesProvider decides WHO may be offered; these are about the
/// window, which is told the answer and has to draw it, and about the one rule
/// that belongs here rather than there: nothing is sent when nothing moved.
/// </summary>
public sealed class OwnershipSheetTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static Ownership Owned(
        string owner = "amelia", string group = "amelia",
        bool canOwner = false, bool canGroup = true)
        => new(owner, group, ["amelia", "gil"], ["amelia", "audio"], canOwner, canGroup);

    private static async Task<(PropertiesViewModel Model, Editor Access)> Opened(
        Ownership? ownership)
    {
        var access = new Editor(ownership);
        var model = new PropertiesViewModel(new Says(), ["/home/amelia/notes.txt"], access);

        await model.LoadAsync();

        Dispatcher.UIThread.RunJobs();

        return (model, access);
    }

    // ---- what reaches the window -------------------------------------------

    [AvaloniaFact]
    public async Task The_two_names_and_their_choices_reach_the_window()
    {
        var (model, _) = await Opened(Owned());

        Assert.True(model.HasOwnership);
        Assert.Equal("amelia", model.Owner);
        Assert.Equal(["amelia", "gil"], model.OwnerChoices);
        Assert.Equal(["amelia", "audio"], model.GroupChoices);
    }

    /// <summary>Each box is live only where the change would be allowed, which
    /// the provider has already worked out.</summary>
    [AvaloniaFact]
    public async Task Each_box_is_live_only_where_the_change_would_be_allowed()
    {
        var (model, _) = await Opened(Owned(canOwner: false, canGroup: true));

        Assert.False(model.CanEditOwner);
        Assert.True(model.CanEditGroup);
    }

    /// <summary>A platform with no notion of file owners draws neither box,
    /// rather than two empty ones.</summary>
    [AvaloniaFact]
    public async Task A_platform_without_owners_draws_nothing()
    {
        var (model, _) = await Opened(null);

        Assert.False(model.HasOwnership);
        Assert.True(model.CanEditAccess, "the permission bits are a separate question");
    }

    /// <summary>The lists are replaced in place. Assigning a new collection
    /// would leave the bound box pointed at the old one.</summary>
    [AvaloniaFact]
    public async Task A_second_read_refills_the_same_collections()
    {
        var (model, access) = await Opened(Owned());

        var before = model.GroupChoices;

        access.Next = Owned(group: "audio") with { Groups = ["audio"] };

        await model.LoadAsync();

        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, model.GroupChoices);
        Assert.Equal(["audio"], model.GroupChoices);
    }

    // ---- applying ----------------------------------------------------------

    /// <summary>
    /// **Nothing sent when nothing moved.** chown is refused for everybody but
    /// root, so a sheet that ran it on every Apply would report a permission
    /// failure to somebody who had only ticked a box — and it would be telling
    /// the truth about a change they never asked for.
    /// </summary>
    [AvaloniaFact]
    public async Task Ticking_a_box_alone_sends_no_chown()
    {
        var (model, access) = await Opened(Owned());

        model.Access[0].Value = !model.Access[0].Value;

        await model.ApplyAccessCommand.ExecuteAsync(null);

        Assert.Empty(access.HandedOver);
    }

    [AvaloniaFact]
    public async Task Changing_the_group_sends_both_names()
    {
        var (model, access) = await Opened(Owned());

        model.Group = "audio";

        await model.ApplyAccessCommand.ExecuteAsync(null);

        Assert.Equal([("/home/amelia/notes.txt", "amelia", "audio")], access.HandedOver);
    }

    [AvaloniaFact]
    public async Task Applying_to_the_contents_carries_through_to_the_handover()
    {
        var (model, access) = await Opened(Owned());

        model.ApplyRecursively = true;
        model.Group = "audio";

        await model.ApplyAccessCommand.ExecuteAsync(null);

        Assert.True(access.Recursed);
    }

    /// <summary>A refusal is the sentence the platform gave, not "failed".</summary>
    [AvaloniaFact]
    public async Task A_refusal_is_said_in_the_platforms_own_words()
    {
        var (model, access) = await Opened(Owned());

        access.Refuses = "invalid group: 'nosuch'";
        model.Group = "nosuch";

        await model.ApplyAccessCommand.ExecuteAsync(null);

        Assert.Equal("invalid group: 'nosuch'", model.AccessStatus);
    }

    /// <summary>
    /// **Before the read-back, or the read-back reports the old name.** The
    /// apply re-reads the file so it can show what actually took; a chown run
    /// after that read would not appear until the window was reopened, and the
    /// box would go on showing the name it had replaced.
    /// </summary>
    [AvaloniaFact]
    public async Task The_handover_happens_before_the_window_reads_back()
    {
        var (model, access) = await Opened(Owned());

        access.Next = Owned(group: "audio");
        model.Group = "audio";

        // The window's own opening read is not what this is about.
        access.Order.Clear();

        await model.ApplyAccessCommand.ExecuteAsync(null);

        Assert.Equal(["handover", "read"], access.Order);
    }

    // ---- the two boxes -----------------------------------------------------

    /// <summary>
    /// The boxes exist, are bound both ways, and are gated on the two separate
    /// answers — one control gated on the other's permission would offer a
    /// change that is always refused.
    /// </summary>
    [Fact]
    public void The_sheet_carries_a_box_for_each()
    {
        var boxes = XDocument.Parse(RepoSource.Ui("PropertiesWindow.axaml"))
            .Descendants(Avalonia + "ComboBox")
            .ToList();

        var owner = Assert.Single(
            boxes, b => (string?)b.Attribute("ItemsSource") == "{Binding OwnerChoices}");
        var group = Assert.Single(
            boxes, b => (string?)b.Attribute("ItemsSource") == "{Binding GroupChoices}");

        Assert.Equal("{Binding Owner}", (string?)owner.Attribute("SelectedItem"));
        Assert.Equal("{Binding CanEditOwner}", (string?)owner.Attribute("IsEnabled"));

        Assert.Equal("{Binding Group}", (string?)group.Attribute("SelectedItem"));
        Assert.Equal("{Binding CanEditGroup}", (string?)group.Attribute("IsEnabled"));
    }

    /// <summary>And the pair comes and goes with the platform's answer, or a
    /// Windows sheet would draw two dead boxes.</summary>
    [Fact]
    public void The_pair_is_drawn_only_where_there_are_owners()
    {
        var owned = XDocument.Parse(RepoSource.Ui("PropertiesWindow.axaml"))
            .Descendants(Avalonia + "ComboBox")
            .First(b => (string?)b.Attribute("ItemsSource") == "{Binding OwnerChoices}")
            .Ancestors()
            .Any(a => (string?)a.Attribute("IsVisible") == "{Binding HasOwnership}");

        Assert.True(owned, "the owner box is drawn whatever the platform says");
    }

    // ---- doubles -----------------------------------------------------------

    private sealed class Editor(Ownership? ownership) : IAccessEditor
    {
        public Ownership? Next { get; set; } = ownership;
        public string? Refuses { get; set; }
        public bool Recursed { get; private set; }

        public List<(string Path, string Owner, string Group)> HandedOver { get; } = [];
        public List<string> Order { get; } = [];

        public bool CanEdit => true;

        public ValueTask<AccessState?> GetAccessAsync(string path, CancellationToken ct)
        {
            Order.Add("read");

            return ValueTask.FromResult<AccessState?>(
                new AccessState([new AccessToggle("ur", "owner", "read", true)], "644")
                {
                    Ownership = Next,
                });
        }

        public ValueTask<AccessOutcome> SetAccessAsync(
            string path, IReadOnlyList<AccessToggle> toggles, bool recursive,
            IProgress<int>? progress, CancellationToken ct)
            => ValueTask.FromResult(AccessOutcome.Complete);

        public ValueTask<string?> SetOwnershipAsync(
            string path, string owner, string group, bool recursive, CancellationToken ct)
        {
            Order.Add("handover");
            HandedOver.Add((path, owner, group));
            Recursed |= recursive;

            return ValueTask.FromResult(Refuses);
        }
    }

    /// <summary>Answers about a file and nothing else; these are about the
    /// access section.</summary>
    private sealed class Says : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = false,
                Kind = "File",
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 0));

        public bool ShowSystemDialog(string path) => false;
    }
}
