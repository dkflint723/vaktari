using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Getting OUT of a dialog was fixed; getting THROUGH one was not.
///
/// **Escape landed on all six windows and Enter landed on one.** Cancel took
/// IsCancel everywhere, so the key that abandons a dialog worked from the
/// keyboard — and Save, Rename and Share were left with no keyboard route at
/// all. Only the conflict prompt, which was written later, had a default
/// button. So the half of the convention that throws work away was honoured
/// and the half that keeps it was not, in three dialogs whose entire input is
/// text you type.
///
/// Driven by pressing the real key at a real window rather than by reading the
/// markup, because the markup half is the part that was already obviously
/// missing. What was NOT obvious — and is the reason this was worth pausing
/// over — is what a default button does to the rest of a window full of text
/// boxes, spinners and lists. That is a question about Avalonia's dispatch,
/// and only a key press can answer it.
/// </summary>
public sealed class EnterConfirmsDialogsTests
{
    private static void Enter(Window window)
        => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static IEnumerable<TextBox> VisibleBoxes(Window window)
        => window.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible);

    /// <summary>
    /// Settings.
    ///
    /// The assertion is on Result rather than on the window having closed:
    /// Cancel closes it too, and a test that only checked for a closed window
    /// would pass just as well if Enter had found the wrong button.
    /// </summary>
    [AvaloniaFact]
    public void Enter_in_the_settings_form_saves()
    {
        var model = new SettingsViewModel(new SettingsState())
        {
            // A marker on the General page, which is the page a fresh window
            // opens on — so the box below is realised and can take focus.
            ProtonDriveFolder = @"D:\marker",
        };

        var window = new SettingsWindow { DataContext = model };

        window.Show();
        window.Measure(new Avalonia.Size(700, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 700, 560));

        var box = VisibleBoxes(window).FirstOrDefault(b => b.Text == @"D:\marker");

        Assert.NotNull(box);

        box!.Focus();
        Assert.Same(box, window.FocusManager?.GetFocusedElement());

        Enter(window);

        Assert.Equal(@"D:\marker", model.Result.General.ProtonDriveFolder);

        window.Close();
    }

    /// <summary>Batch rename: Enter from the pattern box starts it.</summary>
    [AvaloniaFact]
    public void Enter_in_the_batch_rename_pattern_renames()
    {
        var renamed = new List<string>();

        var model = new BatchRenameViewModel(
            [Entry("one.txt"), Entry("two.txt")],
            (_, name) => { renamed.Add(name); return Task.CompletedTask; })
        {
            Pattern = "img ###",
        };

        var window = new BatchRenameWindow(model);

        window.Show();
        window.UpdateLayout();

        var pattern = VisibleBoxes(window).First(b => b.Text == "img ###");

        pattern.Focus();
        Assert.Same(pattern, window.FocusManager?.GetFocusedElement());

        Enter(window);

        Assert.Equal(["img 001.txt", "img 002.txt"], renamed);

        window.Close();
    }

    /// <summary>
    /// And not when there is nothing to do.
    ///
    /// The disabled Rename button is what stands between Enter and a rename
    /// nobody asked for: Avalonia's default button ignores the key while it is
    /// not effectively enabled, so IsEnabled is load bearing for the keyboard
    /// now as well as for the pointer.
    ///
    /// **The button's state is asserted as well as the outcome, and it has to
    /// be.** ApplyAsync opens with its own `if (!CanApply) return;`, so the
    /// two guards cover for each other: replacing the IsEnabled binding with a
    /// bare True left this test green on the view model's refusal alone, which
    /// is exactly a test that cannot fail for the reason it is named after.
    ///
    /// The second half stops the first passing for the wrong reason — the same
    /// key, at the same box, once there IS something to rename.
    /// </summary>
    [AvaloniaFact]
    public void Enter_with_nothing_to_rename_does_nothing()
    {
        var renamed = new List<string>();

        var model = new BatchRenameViewModel(
            [Entry("one.txt"), Entry("two.txt")],
            (_, name) => { renamed.Add(name); return Task.CompletedTask; })
        {
            // Find and replace, with an empty Find: every name comes out as it
            // went in, so there is nothing to apply.
            IsNumbered = false,
        };

        var window = new BatchRenameWindow(model);

        window.Show();
        window.UpdateLayout();

        Assert.False(model.CanApply);

        var rename = window.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Rename");

        Assert.False(rename.IsEffectivelyEnabled);

        var find = VisibleBoxes(window).First();

        find.Focus();
        Assert.Same(find, window.FocusManager?.GetFocusedElement());

        Enter(window);

        Assert.Empty(renamed);

        // The same key, the same box, with something to rename.
        find.Text = "o";
        window.UpdateLayout();

        Assert.Equal("o", model.Find);
        Assert.True(model.CanApply);
        Assert.True(rename.IsEffectivelyEnabled);

        Enter(window);

        Assert.Equal(["ne.txt", "tw.txt"], renamed);

        window.Close();
    }

    /// <summary>Share: Enter from the path box starts the share.</summary>
    [AvaloniaFact]
    public void Enter_in_the_share_path_starts_the_share()
    {
        var root = Path.Combine(Path.GetTempPath(), "vaktari-enter-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "inner"));

        try
        {
            var shared = new List<string>();

            var model = new ShareRequestViewModel(
                root, (path, _) => { shared.Add(path); return Task.CompletedTask; });

            var window = new ShareWindow(model);

            window.Show();
            window.UpdateLayout();

            var path = VisibleBoxes(window).First();

            path.Focus();
            Assert.Same(path, window.FocusManager?.GetFocusedElement());

            Enter(window);

            Assert.Equal([root], shared);

            window.Close();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Not vacuous, and the other half of the rule the Escape tests state.
    ///
    /// **The main window must have no default button**, for the same reason it
    /// must have no IsCancel one: Enter there belongs to the filter box, the
    /// address bar and the rename bar, and a default button anywhere in that
    /// markup would take it from all three the moment focus sat anywhere else.
    /// A dialog convention swept into the window that is not a dialog would be
    /// a far worse bug than the one this file is about.
    /// </summary>
    [Fact]
    public void The_main_window_has_no_default_button()
        => Assert.DoesNotContain("IsDefault=\"True\"", RepoSource.Ui("MainWindow.axaml"));

    private static FileEntry Entry(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);
}
