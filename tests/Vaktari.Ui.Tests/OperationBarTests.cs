using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The bar that says what happened, and why nobody ever read it.
///
/// **Every failure message was written and hidden in the same instant.** The
/// completion handler sets "failed: …", or names the files a batch left behind,
/// and then six lines later clears ActiveOperation because nothing is running
/// any more — and the bar's visibility was bound to ActiveOperation. The
/// comment above the message says "a failure stays on screen". It could not.
///
/// So a copy that skipped a locked file reported a clean run, and the person
/// never learned which file did not arrive. That is the whole reason the engine
/// carries on past a locked file instead of stopping.
/// </summary>
public sealed class OperationBarTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    [AvaloniaFact]
    public void Nothing_running_and_nothing_to_say_shows_no_bar()
    {
        var shell = Shell();

        Assert.False(shell.ShowOperationBar);
        Assert.False(shell.OperationFinished);
    }

    /// <summary>The state the bar was built for, and the only one it used to
    /// survive.</summary>
    [AvaloniaFact]
    public void Something_running_shows_the_bar()
    {
        var shell = Shell();

        shell.ActiveOperation = new OperationHandle();

        Assert.True(shell.ShowOperationBar);
        Assert.False(shell.OperationFinished, "there is still something to cancel");
    }

    /// <summary>
    /// **The state that was invisible.** The operation has finished and left a
    /// message; there is nothing to pause or cancel, and the message is the
    /// only thing the user has.
    /// </summary>
    [AvaloniaFact]
    public void A_message_with_nothing_running_still_shows_the_bar()
    {
        var shell = Shell();

        shell.ActiveOperation = null;
        shell.OperationStatus = "failed: could not read notes.txt";

        Assert.True(shell.ShowOperationBar, "the failure message was hidden again");
        Assert.True(shell.OperationFinished, "pause and cancel should give way to dismiss");
    }

    /// <summary>
    /// The bar outlives the operation now, so the message needs a way out that
    /// is not "wait until the next copy".
    /// </summary>
    [AvaloniaFact]
    public void Dismissing_puts_the_message_away()
    {
        var shell = Shell();

        shell.OperationStatus = "2 items were left behind";
        Assert.True(shell.ShowOperationBar);

        shell.DismissOperationStatusCommand.Execute(null);

        Assert.False(shell.ShowOperationBar);
        Assert.Equal("", shell.OperationStatus);
    }

    /// <summary>Both properties are computed, so nothing raises them on their
    /// own — miss the fan-out and the bar never appears.</summary>
    [AvaloniaFact]
    public void Setting_the_message_tells_the_view()
    {
        var shell = Shell();
        var raised = new List<string?>();

        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.OperationStatus = "failed: something";

        Assert.Contains(nameof(ShellViewModel.ShowOperationBar), raised);
        Assert.Contains(nameof(ShellViewModel.OperationFinished), raised);
    }

    /// <summary>
    /// And the markup really binds to it. The fault was entirely in which
    /// property the visibility was bound to, so a view-model-only test would
    /// pass against the bug.
    /// </summary>
    [AvaloniaFact]
    public void The_bar_is_not_bound_to_the_operation_any_more()
    {
        var markup = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var bar = XDocument.Parse(markup)
            .Descendants(Avalonia + "Border")
            .Single(b => b.Descendants(Avalonia + "TextBlock")
                          .Any(t => (string?)t.Attribute("Text") == "{Binding OperationStatus}"));

        Assert.Equal("{Binding ShowOperationBar}", (string?)bar.Attribute("IsVisible"));
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
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

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    // ---- the buttons stay on screen -----------------------------------------

    /// <summary>
    /// **A long file name pushed pause and cancel off the window.** A
    /// horizontal StackPanel measures its children against infinite width, and
    /// the progress line ends in a filename that can be 255 characters in a
    /// monospace font — so the buttons were laid out past the right edge. They
    /// are the ONLY route to either command in the whole application: there is
    /// no key binding and no gesture for pause or cancel. A copy of a
    /// long-named file was unpausable and uncancellable.
    /// </summary>
    [AvaloniaFact]
    public void The_bar_gives_its_buttons_their_space_before_the_text()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var bar = markup.Descendants(Avalonia + "Border")
            .Single(b => (string?)b.Attribute("IsVisible") == "{Binding ShowOperationBar}");

        // A horizontal StackPanel is the shape that pushes them off.
        Assert.Empty(bar.Descendants(Avalonia + "StackPanel"));

        var panel = bar.Elements(Avalonia + "DockPanel").Single();

        foreach (var button in panel.Elements(Avalonia + "Button"))
            Assert.Equal("Right", (string?)button.Attribute("DockPanel.Dock"));
    }

    /// <summary>
    /// The status line is the fill child, so it takes only what is left — and
    /// it trims rather than wrapping, because wrapping made the bar grow down
    /// over the listing while still not fitting.
    /// </summary>
    [AvaloniaFact]
    public void The_status_line_takes_what_is_left_and_trims()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var text = markup.Descendants(Avalonia + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding OperationStatus}");

        // No Dock, so it fills. Anything docked is measured before it.
        Assert.Null(text.Attribute("DockPanel.Dock"));

        Assert.Equal("CharacterEllipsis", (string?)text.Attribute("TextTrimming"));
        Assert.Null(text.Attribute("TextWrapping"));

        // And what was trimmed can still be read.
        Assert.Equal("{Binding OperationStatus}", (string?)text.Attribute("ToolTip.Tip"));
    }

    /// <summary>
    /// Declaration order decides which is rightmost, so this reads
    /// "Pause Cancel" rather than "Cancel Pause" while an operation runs, and
    /// "Retry 3   Dismiss" once one has finished leaving something behind.
    ///
    /// Retry sits to the LEFT of dismiss deliberately: dismiss is the one that
    /// throws the sentence away, and the rightmost slot is where a hand lands
    /// by habit. The two never coexist with pause and cancel — one pair is
    /// while it runs, the other after.
    ///
    /// And "retry as administrator" sits to the left of plain retry, by the
    /// same argument one step on: of the two ways to go again, the one that
    /// asks the system for rights is not the one a habitual hand should find
    /// first.
    /// </summary>
    [AvaloniaFact]
    public void Pause_sits_to_the_left_of_cancel()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var bar = markup.Descendants(Avalonia + "Border")
            .Single(b => (string?)b.Attribute("IsVisible") == "{Binding ShowOperationBar}");

        var commands = bar.Descendants(Avalonia + "Button")
            .Select(b => (string?)b.Attribute("Command"))
            .OfType<string>()
            .ToList();

        Assert.Equal(
            ["{Binding DismissOperationStatusCommand}",
             "{Binding RetryOperationCommand}",
             "{Binding RetryAsAdministratorCommand}",
             "{Binding CancelOperationCommand}",
             "{Binding PauseOperationCommand}"],
            commands);
    }

    /// <summary>
    /// One button, two words. **Both were lower case while every dialog button
    /// beside them was not**, and the bar is the control a person sees most
    /// often.
    ///
    /// The handle is Begun first on purpose: OperationHandle.Pause() returns
    /// early unless the state is Running, so without it the label would still
    /// read "Pause" and this test would pass without the button ever having
    /// worked.
    /// </summary>
    [AvaloniaFact]
    public void The_pause_button_reads_Pause_and_then_Resume()
    {
        var shell = Shell();

        Assert.Equal("Pause", shell.PauseLabel);

        var handle = new OperationHandle();
        handle.Begin(itemsTotal: 1, totalBytes: 1);
        shell.ActiveOperation = handle;

        shell.PauseOperationCommand.Execute(null);

        Assert.Equal(OperationState.Paused, handle.State);
        Assert.Equal("Resume", shell.PauseLabel);
    }
}
