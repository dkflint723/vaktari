using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the keyboard is around the address bar.
///
/// **Its sibling had a bug of exactly this shape, and nobody checked whether
/// this box had it too.** The filter box took the keyboard every time it
/// APPEARED, and a tab switch is an appearance: one box lives in the pane
/// group's chrome with its visibility bound through ActiveTab, so coming back
/// to a tab that had it open flips it hidden-to-shown and the focus behaviour
/// answers that exactly as it answers the gesture.
///
/// The address bar is built the same way — one TextBox, `IsVisible` bound to
/// `ActiveTab.IsPathEditing`, `FocusOnVisible` set — and the reason given for
/// leaving it alone was a sentence in a docstring: it is "created by the gesture
/// rather than revealed by one". This file measures that rather than repeating
/// it, because the fix for pressing Ctrl+L twice depends on the answer.
/// </summary>
public sealed class AddressBarFocusTests : OwnedViewModels
{
    private sealed record Rig(Window Window, PaneGroupViewModel Group, TextBox Listing, TextBox Path);

    /// <summary>
    /// The real arrangement, as MainWindow builds it: ONE path box in the
    /// group's chrome, its visibility and its lost-focus command both bound
    /// through ActiveTab, beside something else that can hold the keyboard.
    /// </summary>
    private Rig Build()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));

        for (var i = 0; i < 2; i++)
        {
            var tab = Own(new PaneViewModel(new Inert()));
            tab.CurrentPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tab" + i);
            group.Tabs.Add(tab);
        }

        group.ActiveTab = group.Tabs[0];

        var listing = new TextBox();
        var path = new TextBox();

        // Text as well as the rest: the selecting half of this behaviour has
        // nothing to select on a box whose contents are not the pane's.
        path.Bind(TextBox.TextProperty, new Binding("ActiveTab.PathText"));
        path.Bind(Visual.IsVisibleProperty, new Binding("ActiveTab.IsPathEditing"));
        path.SetValue(FocusBehavior.FocusOnVisibleProperty, true);
        path.Bind(FocusBehavior.FocusWhenProperty, new Binding("ActiveTab.FocusPathBox"));
        path.Bind(FocusBehavior.LostFocusCommandProperty,
                  new Binding("ActiveTab.RevertPathTextCommand"));

        var panel = new StackPanel { DataContext = group };

        panel.Children.Add(listing);
        panel.Children.Add(path);

        var window = new Window { Content = panel, Width = 600, Height = 200 };

        window.Show();
        window.Measure(new Size(600, 200));
        window.Arrange(new Rect(0, 0, 600, 200));

        return new Rig(window, group, listing, path);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
    }

    /// <summary>Asking for the box puts the caret in it — the gesture the whole
    /// thing exists for.</summary>
    [AvaloniaFact]
    public void Asking_for_the_address_bar_puts_the_caret_in_it()
    {
        var rig = Build();

        try
        {
            rig.Listing.Focus();
            Settle();

            rig.Group.ActiveTab!.BeginEditPath();
            Settle();

            Assert.True(rig.Path.IsFocused);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// **The question the Ctrl+L fix depends on.** If the box survives a tab
    /// switch, then coming back is an APPEARANCE, the behaviour that focuses on
    /// that edge fires, and the address bar has the filter's bug — in which
    /// case FocusOnVisible has to go and the gesture must pulse instead.
    ///
    /// If the box closes on the way out, the docstring's claim holds and
    /// FocusOnVisible is safe to keep.
    ///
    /// Either way this test says which, rather than leaving the next reader to
    /// take a sentence's word for it.
    /// </summary>
    [AvaloniaFact]
    public void Coming_back_to_a_tab_whose_address_bar_was_open_leaves_the_keyboard_alone()
    {
        var rig = Build();

        try
        {
            rig.Group.ActiveTab!.BeginEditPath();
            Settle();

            rig.Group.ActiveTab = rig.Group.Tabs[1];
            Settle();

            rig.Listing.Focus();
            Settle();

            rig.Group.ActiveTab = rig.Group.Tabs[0];
            Settle();

            Assert.True(
                rig.Listing.IsFocused,
                "coming back to the tab took the keyboard into the address bar — the box is "
                + "shared and its visibility re-points, so this is the filter box's own bug "
                + "and FocusOnVisible has to go the same way");
        }
        finally
        {
            rig.Window.Close();
        }
    }

    // ---- pressing it a second time -------------------------------------------

    /// <summary>
    /// **The box cannot be open and unfocused**, which is what makes the
    /// second press mean something narrower than it first appears: moving the
    /// keyboard anywhere else runs the lost-focus command and closes it. So
    /// Ctrl+L over an open address bar is always Ctrl+L over a FOCUSED one, and
    /// the only thing left for it to do is re-select.
    /// </summary>
    [AvaloniaFact]
    public void Moving_the_keyboard_away_closes_the_box()
    {
        var rig = Build();

        try
        {
            rig.Group.ActiveTab!.BeginEditPath();
            Settle();

            rig.Listing.Focus();
            Settle();

            Assert.False(rig.Group.ActiveTab.IsPathEditing);
            Assert.False(rig.Path.IsVisible);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// And it selects what is there, which is the point of the gesture: the
    /// next thing typed replaces the path rather than being appended to it.
    /// </summary>
    [AvaloniaFact]
    public void And_selects_what_is_already_typed()
    {
        var rig = Build();

        try
        {
            var pane = rig.Group.ActiveTab!;

            pane.BeginEditPath();
            Settle();

            pane.PathText = @"C:\half-typed";
            rig.Path.CaretIndex = rig.Path.Text?.Length ?? 0;
            Settle();

            pane.BeginEditPath();
            Settle();

            Assert.Equal(@"C:\half-typed", rig.Path.SelectedText);

            // **And again**, because the signal has to pulse rather than
            // latch: left true after the first re-select it never crosses
            // false-to-true again, and the third press — the second time
            // somebody asks — reaches nothing.
            rig.Path.CaretIndex = rig.Path.Text?.Length ?? 0;
            rig.Path.ClearSelection();
            Settle();

            pane.BeginEditPath();
            Settle();

            Assert.Equal(@"C:\half-typed", rig.Path.SelectedText);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// **And it still keeps what was typed**, which is the half that already
    /// shipped and must not be undone by the half being added: a second press
    /// re-selects the text rather than resetting the box to the folder you are
    /// standing in.
    /// </summary>
    [AvaloniaFact]
    public void And_keeps_it_rather_than_resetting_the_box()
    {
        var rig = Build();

        try
        {
            var pane = rig.Group.ActiveTab!;

            pane.BeginEditPath();
            pane.PathText = @"C:\half-typed";

            pane.BeginEditPath();
            Settle();

            Assert.Equal(@"C:\half-typed", pane.PathText);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// **And the real box is wired to it**, which this file's rig cannot say:
    /// the rig binds the behaviour itself, so every test above would pass with
    /// the markup untouched.
    /// </summary>
    [Fact]
    public void The_real_address_bar_is_wired_to_both_halves()
    {
        var box = System.Xml.Linq.XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(System.Xml.Linq.XNamespace.Get("https://github.com/avaloniaui") + "TextBox")
            .Single(t => (string?)t.Attribute("IsVisible") == "{Binding ActiveTab.IsPathEditing}");

        // The prefix resolves to the namespace the root declares, so the
        // attribute's name carries that rather than the "local:" it is written
        // with.
        var ui = System.Xml.Linq.XNamespace.Get("clr-namespace:Vaktari.Ui");

        Assert.Equal("{Binding ActiveTab.FocusPathBox}",
                     (string?)box.Attribute(ui + "FocusBehavior.FocusWhen"));

        // The first press is still answered by the box appearing. Measured
        // above: unlike the filter, this box closes when the keyboard leaves
        // it, so it never re-appears on a tab switch and cannot steal the
        // keyboard the way the filter's did.
        Assert.Equal("True", (string?)box.Attribute(ui + "FocusBehavior.FocusOnVisible"));
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => System.IO.Path.Combine(basePath, name);
        public string? GetParent(string path) => System.IO.Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
