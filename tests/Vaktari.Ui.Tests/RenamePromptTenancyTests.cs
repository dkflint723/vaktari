using System.Xml.Linq;
using Avalonia.Controls;
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
}
