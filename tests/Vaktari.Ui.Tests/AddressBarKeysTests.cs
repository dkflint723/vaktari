using System.Windows.Input;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The address bar could not copy or paste.
///
/// **A Window KeyBinding is claimed before the focused control sees the key.**
/// Ctrl+C, Ctrl+X and Ctrl+V were declared in Window.KeyBindings, so pressing
/// Ctrl+V while typing a path did not paste the path — it pasted whatever FILES
/// were on the clipboard into the folder behind the box, or said "clipboard has
/// no files" and left the field looking dead. Ctrl+C was worse than useless:
/// instead of copying the selected text it REPLACED the system clipboard with
/// the listing's selection, so copying a path out of the address bar destroyed
/// the thing being copied. Ctrl+X armed a move of those files.
///
/// Ctrl+Z and both redo spellings had the same fault with a sharper edge: undo
/// after a typo reversed the last copy, move or delete on disk.
///
/// This file is the diagnosis and the rule. The first test proves the framework
/// behaviour the fault rests on, against the Avalonia this project ships; the
/// second is the rule that keeps the gestures out of the markup.
/// </summary>
public sealed class AddressBarKeysTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private sealed class Act(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    /// <summary>
    /// The premise, measured rather than assumed: a window-level binding takes
    /// the keystroke even though a TextBox has focus and wants it.
    ///
    /// Worth a test of its own because the entire fix is "declare them
    /// somewhere else". If a future Avalonia reverses this, the fix becomes
    /// unnecessary rather than wrong — but we should find out from a failing
    /// test rather than from a bug report.
    /// </summary>
    [AvaloniaFact]
    public void A_window_binding_takes_the_key_from_a_focused_text_box()
    {
        var ran = 0;
        var box = new TextBox { Text = "before" };
        var window = new Window { Content = box };

        window.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.V, KeyModifiers.Control),
            Command = new Act(() => ran++),
        });

        window.Show();
        box.Focus();

        Assert.True(box.IsFocused, "the text box has to have focus for this to mean anything");

        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");

        Assert.Equal(1, ran);
        Assert.Equal("before", box.Text);
    }

    /// <summary>Every gesture a text cursor answers to. Shift+Insert and its
    /// two siblings are the older spellings of the same three commands; they
    /// were never bound in the markup, which is why they kept working in the
    /// address bar the whole time this was broken.</summary>
    public static TheoryData<string> TextEditingGestures =>
    [
        "Ctrl+C", "Ctrl+X", "Ctrl+V",
        "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z",
        "Ctrl+A",
        "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
    ];

    /// <summary>
    /// The rule. Handling these in OnWindowKeyDown is what puts them behind the
    /// "a focused text box owns the keyboard" guard; declaring one here would
    /// silently step back in front of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TextEditingGestures))]
    public void No_text_editing_gesture_is_a_window_key_binding(string gesture)
    {
        var path = Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml");
        var window = XDocument.Load(path).Root!;

        var bound = window
            .Elements(Avalonia + "Window.KeyBindings")
            .Elements(Avalonia + "KeyBinding")
            .Select(k => (string?)k.Attribute("Gesture"))
            .OfType<string>()
            .ToList();

        Assert.False(
            bound.Contains(gesture, StringComparer.OrdinalIgnoreCase),
            $"{gesture} is a Window.KeyBinding again, so the address bar cannot use it "
            + "— handle it in OnWindowKeyDown, behind the focused-text-box guard.");
    }

    /// <summary>
    /// And they still work in the listing. Moving where a gesture is handled
    /// must not quietly remove it: the F1 sheet promises all five.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Y")]
    public void The_gesture_is_still_offered_to_the_user(string gesture)
    {
        var listed = Vaktari.Ui.ViewModels.Shortcuts.All
            .SelectMany(group => group.Keys)
            .SelectMany(k => k.Keys.Split(" / ", StringSplitOptions.TrimEntries));

        Assert.Contains(gesture, listed, StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    /// **Right-clicking the address bar used to close it.**
    ///
    /// The path box reverts its edit and hides itself when focus leaves — that
    /// is how clicking away cancels. But an open flyout takes the focus too, so
    /// opening the box's own Cut/Copy/Paste menu counted as leaving: the field
    /// collapsed back to breadcrumbs out from under the menu, taking the typed
    /// path with it. The mouse route to paste destroyed the thing being pasted
    /// into.
    /// </summary>
    [AvaloniaFact]
    public void Opening_the_boxs_own_menu_does_not_count_as_leaving_it()
    {
        var reverted = 0;
        var flyout = new MenuFlyout();
        var box = new TextBox { Text = "D:/typed/so/far", ContextFlyout = flyout };
        var elsewhere = new TextBox();

        FocusBehavior.SetLostFocusCommand(box, new Act(() => reverted++));

        var window = new Window { Content = new StackPanel { Children = { box, elsewhere } } };

        window.Show();
        box.Focus();

        // What a right-click does: the menu opens and takes the focus.
        flyout.ShowAt(box);
        elsewhere.Focus();

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, reverted);
    }

    /// <summary>
    /// And it still cancels when focus really does go somewhere else — the
    /// guard must not turn "click away to cancel" into a box that never closes.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_away_still_cancels()
    {
        var reverted = 0;
        var box = new TextBox { Text = "D:/typed/so/far", ContextFlyout = new MenuFlyout() };
        var elsewhere = new TextBox();

        FocusBehavior.SetLostFocusCommand(box, new Act(() => reverted++));

        var window = new Window { Content = new StackPanel { Children = { box, elsewhere } } };

        window.Show();
        box.Focus();
        elsewhere.Focus();

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, reverted);
    }

    /// <summary>
    /// When the menu closes and focus has not come back, the box still closes.
    /// Skipping the revert while the menu is open must not leave the field
    /// stranded — open, unfocused, with no further event coming for it.
    /// </summary>
    [AvaloniaFact]
    public void The_box_still_closes_once_the_menu_is_gone()
    {
        var reverted = 0;
        var flyout = new MenuFlyout();
        var box = new TextBox { Text = "D:/typed/so/far", ContextFlyout = flyout };
        var elsewhere = new TextBox();

        FocusBehavior.SetLostFocusCommand(box, new Act(() => reverted++));

        var window = new Window { Content = new StackPanel { Children = { box, elsewhere } } };

        window.Show();
        box.Focus();

        flyout.ShowAt(box);
        elsewhere.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, reverted);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, reverted);
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
