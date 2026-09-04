using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The settings window builds and lays out.
///
/// **Compiling is not the same as running.** Avalonia checks binding paths at
/// build time, and it was a style that compiled perfectly which killed the
/// process when its menu opened in 0.7.0 — the fault only exists once controls
/// are realised. This window is now a page of them, several data-driven, and
/// nothing else in the suite has ever instantiated it.
///
/// Deliberately shallow: it asserts that the thing opens and that the theme
/// list is populated and bound. What each control does belongs in the tests for
/// the view model, which is where it can be asserted without a window.
/// </summary>
public sealed class SettingsWindowRendersTests
{
    [AvaloniaFact]
    public void It_opens_with_the_icon_theme_list_bound()
    {
        var model = new SettingsViewModel(new SettingsState());

        var window = new Vaktari.Ui.SettingsWindow { DataContext = model };

        window.Show();

        // Realises the templates: a binding that throws does so here rather
        // than at construction.
        window.Measure(new Avalonia.Size(700, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 700, 560));

        var combo = window.GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, model.IconThemeChoices));

        Assert.NotNull(combo);

        // Vaktari's own icons is always there, and is what a fresh settings
        // file selects.
        Assert.NotEmpty(model.IconThemeChoices);
        Assert.Same(model.SelectedIconTheme, combo!.SelectedItem);
        Assert.Equal("", model.SelectedIconTheme!.Folder);

        window.Close();
    }

    /// <summary>
    /// The conflict prompt builds and lays out, with its four answers present.
    ///
    /// It is new markup on a path that only runs when two files clash, so it is
    /// exactly the kind of window that ships broken and is found by somebody
    /// mid-copy. Four buttons because there are four answers — losing one to a
    /// typo would leave an operation that can only be cancelled.
    /// </summary>
    [AvaloniaFact]
    public void The_conflict_prompt_opens_with_all_four_answers()
    {
        var root = Path.Combine(Path.GetTempPath(), "vaktari-render-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        try
        {
            var target = Path.Combine(root, "notes.txt");
            var source = Path.Combine(root, "incoming.txt");

            File.WriteAllText(target, "old");
            File.WriteAllText(source, "new");

            var model = new ConflictViewModel(new Vaktari.Core.FileSystem.FileConflict(source, target));
            var window = new Vaktari.Ui.ConflictWindow(model);

            window.Show();
            window.Measure(new Avalonia.Size(520, 400));
            window.Arrange(new Avalonia.Rect(0, 0, 520, 400));

            var labels = window.GetVisualDescendants()
                .OfType<Button>()
                .Select(b => b.Content as string)
                .OfType<string>()
                .ToList();

            Assert.Contains("Overwrite", labels);
            Assert.Contains("Keep both", labels);
            Assert.Contains("Skip", labels);
            Assert.Contains("Cancel", labels);

            Assert.Contains("notes.txt", model.Question, StringComparison.Ordinal);

            window.Close();

            // Closing without choosing is Cancel, which the window wires and
            // an operation on a background thread is waiting for.
            Assert.True(model.Answer.IsCompleted);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// **The middle button's word comes from the view model now**, because the
    /// markup hard-coded "Overwrite" while the engine merged two folders. The
    /// view model is asserted directly in ConflictPromptTests; what this adds is
    /// that the binding actually reaches the button — a Content the markup no
    /// longer writes is exactly the kind of thing that compiles, renders blank,
    /// and is found by somebody mid-copy.
    /// </summary>
    [AvaloniaFact]
    public void The_conflict_prompt_says_merge_when_a_folder_is_already_there()
    {
        var root = Path.Combine(Path.GetTempPath(), "vaktari-merge-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        try
        {
            var target = Path.Combine(root, "photos");
            var source = Path.Combine(root, "incoming", "photos");

            Directory.CreateDirectory(target);
            Directory.CreateDirectory(source);

            File.WriteAllText(Path.Combine(target, "one.jpg"), "old");
            File.WriteAllText(Path.Combine(source, "one.jpg"), "new");

            var model = new ConflictViewModel(new Vaktari.Core.FileSystem.FileConflict(source, target));
            var window = new Vaktari.Ui.ConflictWindow(model);

            window.Show();
            window.Measure(new Avalonia.Size(520, 400));
            window.Arrange(new Avalonia.Rect(0, 0, 520, 400));

            var labels = window.GetVisualDescendants()
                .OfType<Button>()
                .Select(b => b.Content as string)
                .OfType<string>()
                .ToList();

            Assert.Contains("Merge", labels);
            Assert.DoesNotContain("Overwrite", labels);

            // And the sentence that says why the word changed is on screen with
            // it, rather than only on the view model.
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text is { } text
                         && text.Contains("Merging keeps", StringComparison.Ordinal)
                         && text.Contains("1 item arriving", StringComparison.Ordinal));

            window.Close();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }
}
