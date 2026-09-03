using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The rename bar has one tenant.
///
/// **F2 re-entered the bar that was already open.** F2 and Shift+F2 were Window
/// KeyBindings, and a KeyBinding is dispatched ahead of the window's own key
/// handler — so the prompt guard at the top of that handler was structurally
/// unable to see them. Pressing F2 again discarded the name being typed and
/// re-pointed the bar at the listing's CURRENT selection, which is a different
/// file the moment another row has been clicked: the bar is inline, non-modal,
/// and the listing behind it stays live.
///
/// F2 was the loud way in. Ctrl+Shift+N is the quiet one — new folder, new file
/// and new-from-template all hand off to the rename bar when they are done, and
/// each raised a fresh request straight over a name somebody was still typing.
///
/// These build a real MainWindow, which nothing in this suite did before. The
/// guard is the only part of this change with runtime behaviour, and its safety
/// was argued rather than measured — a source assertion cannot tell a guard
/// that holds from one that has been inverted.
/// </summary>
public sealed class RenamePromptTenancyTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static FileEntry Row(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    [AvaloniaFact]
    public void A_second_request_does_not_take_the_bar_from_the_first()
    {
        var window = new MainWindow();

        try
        {
            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            var input = window.FindControl<TextBox>("PromptInput");
            Assert.NotNull(input);

            // First tenant: the row the user pressed F2 on.
            pane.SelectedEntry = Row("first.txt");
            pane.BeginRenameCommand.Execute(null);

            Assert.Equal("first.txt", input!.Text);

            // The listing stays live behind the inline bar, so the selection can
            // move under it — and then a second request arrives.
            pane.SelectedEntry = Row("second.txt");
            pane.BeginRenameCommand.Execute(null);

            Assert.Equal("first.txt", input.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And the bar still opens in the ordinary case — a guard that refused
    /// every request would satisfy the test above and make renaming impossible.
    /// </summary>
    [AvaloniaFact]
    public void But_the_first_request_still_opens_it()
    {
        var window = new MainWindow();

        try
        {
            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            var input = window.FindControl<TextBox>("PromptInput");
            var bar = window.FindControl<Border>("PromptBar");

            Assert.NotNull(input);
            Assert.NotNull(bar);

            pane.SelectedEntry = Row("only.txt");
            pane.BeginRenameCommand.Execute(null);

            Assert.True(bar!.IsVisible);
            Assert.Equal("only.txt", input!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Down to Background, because the window posts work at that priority and
    /// a key that half ran is not a key that did nothing.
    /// </summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// **A gesture bound in the markup fired straight through an open rename
    /// bar.** A KeyBinding is dispatched before the window's own key handler
    /// runs at all — before the key is even routed — so the guard that hands
    /// the keyboard to the bar was structurally unable to see one. Typing a
    /// name and reaching for Ctrl+I opened the filter and pulled the caret into
    /// it, so the rest of the name was typed somewhere else entirely.
    /// </summary>
    [AvaloniaFact]
    public void A_gesture_the_markup_used_to_bind_does_not_fire_while_the_bar_is_open()
    {
        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            Assert.False(pane.IsFilterVisible, "the filter is already open, so this proves nothing");

            pane.SelectedEntry = Row("first.txt");
            pane.BeginRenameCommand.Execute(null);
            Settle();

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, null);
            Settle();

            Assert.False(pane.IsFilterVisible,
                         "Ctrl+I opened the filter while a name was being typed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The twin that stops the guard being "swallow it always". With no bar
    /// open the gesture has to do exactly what it always did.
    /// </summary>
    [AvaloniaFact]
    public void But_the_same_gesture_still_works_with_no_prompt_open()
    {
        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            Assert.False(pane.IsFilterVisible, "the filter is already open, so this proves nothing");

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, null);
            Settle();

            Assert.True(pane.IsFilterVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **And the placement is above the text-box guard, not below it.** Two of
    /// these six are deliberately answered while a text box has focus: Ctrl+F
    /// from the path box moves the keyboard to the search field on purpose, and
    /// Ctrl+I from inside the filter box is how the filter is put away. Behind
    /// the guard the fix for a prompt bug would have taken two working keys
    /// away.
    /// </summary>
    [AvaloniaFact]
    public void And_Ctrl_F_still_works_from_the_path_box()
    {
        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            pane.BeginEditPathCommand.Execute(null);
            Settle();

            Assert.True(window.FocusManager?.GetFocusedElement() is TextBox,
                        "the path box does not have the keyboard, so this proves nothing");
            Assert.False(pane.IsSearchOpen);

            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null);
            Settle();

            Assert.True(pane.IsSearchOpen);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- and the gestures reach the guard at all ---------------------------

    /// <summary>
    /// A Window KeyBinding is dispatched ahead of the window's key handler, so
    /// a gesture bound there can never sit behind the prompt guard. Both are in
    /// the handler now.
    /// </summary>
    [Fact]
    public void Neither_rename_gesture_is_a_window_key_binding()
    {
        var bindings = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "KeyBinding")
            .Select(b => (string?)b.Attribute("Gesture"))
            .ToList();

        Assert.DoesNotContain("F2", bindings);
        Assert.DoesNotContain("Shift+F2", bindings);
    }

    [Fact]
    public void And_both_are_handled_behind_the_prompt_guard()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnWindowKeyDown(object? sender, KeyEventArgs e)");

        var rename = body.IndexOf("case Key.F2 when e.KeyModifiers == KeyModifiers.None:",
                                  StringComparison.Ordinal);
        var bulk = body.IndexOf("case Key.F2 when e.KeyModifiers == KeyModifiers.Shift:",
                                StringComparison.Ordinal);

        Assert.True(rename > 0, "F2 is not handled in the guarded key handler");
        Assert.True(bulk > 0, "Shift+F2 is not handled in the guarded key handler");

        // Through the commands, not the methods: the shortcut-inventory tests
        // read the bound name out of this file with a `.XCommand.Execute(`
        // pattern, and calling the method directly would compile and turn them
        // red.
        Assert.Contains("pane.BeginRenameCommand.Execute(null);", body);
        Assert.Contains("_shell.BatchRenameCommand.Execute(null);", body);
    }

    /// <summary>
    /// The runtime tests above cover one gesture each; this is what says the
    /// other five moved rather than being deleted, and that all six sit above
    /// the focused-text-box guard.
    /// </summary>
    [Fact]
    public void The_six_gestures_moved_out_of_the_markup_into_the_handler()
    {
        string[] moved = ["Ctrl+I", "Ctrl+Shift+N", "Ctrl+H", "Ctrl+D", "Ctrl+F", "Ctrl+E"];

        var bindings = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "KeyBinding")
            .Select(b => (string?)b.Attribute("Gesture"))
            .ToList();

        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnWindowKeyDown(object? sender, KeyEventArgs e)");

        var guard = body.IndexOf("if (FocusManager?.GetFocusedElement() is TextBox) return;",
                                 StringComparison.Ordinal);

        Assert.True(guard > 0, "the focused-text-box guard is not in this handler any more");

        foreach (var gesture in moved)
        {
            // Every KeyBinding in the file, not only the window's: a gesture
            // moved to a pane's own TextBox KeyBindings would be claimed ahead
            // of this handler in exactly the same way.
            Assert.False(bindings.Contains(gesture),
                         $"{gesture} is a KeyBinding in the markup again, so the rename "
                         + "bar cannot refuse it — handle it in OnWindowKeyDown.");

            Assert.Contains(gesture, KeyBindingSites.CodeBehindHandled());
        }

        // Above the guard, not below it: two of the six are answered while a
        // text box has focus on purpose.
        foreach (var label in new[]
                 {
                     "case Key.I when e.KeyModifiers == KeyModifiers.Control:",
                     "case Key.N when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):",
                     "case Key.H when e.KeyModifiers == KeyModifiers.Control:",
                     "case Key.D when e.KeyModifiers == KeyModifiers.Control:",
                     "case Key.E when e.KeyModifiers == KeyModifiers.Control:",
                     "case Key.F when e.KeyModifiers == KeyModifiers.Control:",
                 })
        {
            var at = body.IndexOf(label, StringComparison.Ordinal);

            Assert.True(at > 0, $"{label} is not in the guarded key handler");
            Assert.True(at < guard, $"{label} sits behind the focused-text-box guard");
        }
    }
}
