using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The second button beside "retry N", and when it is there at all.
///
/// **"You do not have permission to copy that" had nowhere to go.** Elevation
/// was launch-only — a file could be run as administrator and a terminal opened
/// as one — and the retry beside that sentence went again as the same person
/// who had just been refused, which fails the same way every time.
/// </summary>
public sealed class ElevatedRetryOfferTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    private static ElevatedRequest Refused(int howMany) => new(
        ElevatedVerb.Copy, Path.Combine(Root, "into"),
        [.. Enumerable.Range(0, howMany).Select(i => Path.Combine(Root, $"f{i}.txt"))]);

    private static RetryOffer Offer(int count, ElevatedRequest? elevated)
        => new(count, () => new OperationHandle(), elevated);

    /// <summary>Stands in for a desktop that can and cannot ask for rights.</summary>
    private sealed class Desktop(bool canElevate) : IApplicationLauncher
    {
        public IReadOnlyList<string>? Asked { get; private set; }

        public bool CanElevate => canElevate;

        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes once consent has actually been asked for.
        ///
        /// **The asking happens on a pool thread.** ElevatedRun.Start does its
        /// waiting inside a Task.Run so the button returns at once, so a test
        /// that read <see cref="Asked"/> straight after pressing raced that
        /// thread: measured failing six times in eight, and in every full run
        /// of this assembly.
        /// </summary>
        public Task Reached => _reached.Task;

        public ValueTask<int?> RunSelfElevatedAsync(
            IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Asked = arguments;
            _reached.TrySetResult();

            // Never answers AFTER that, so the handle stays open and the test
            // is looking at the moment consent was asked for rather than at
            // its result.
            return new ValueTask<int?>(new TaskCompletionSource<int?>().Task);
        }

        public Exception? Open(string path) => null;
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private ShellViewModel Shell(bool canElevate, out Desktop desktop)
    {
        desktop = new Desktop(canElevate);

        // Owned, like every shell in this assembly. A pane that has loaded
        // keeps a file watcher, two dispatcher timers and a background git
        // task; OwnedViewModels exists because the casualty of a leaked one is
        // always some other test.
        return Own(new ShellViewModel(new Nothing(), launcher: desktop));
    }

    [AvaloniaFact]
    public void With_nothing_left_behind_there_is_nothing_to_offer()
    {
        var shell = Shell(canElevate: true, out _);

        Assert.False(shell.CanRetryAsAdministrator);
        Assert.Equal("Retry as administrator", shell.RetryAsAdministratorLabel);
    }

    /// <summary>
    /// **A failure that was not about permission is offered no rights.**
    /// Elevation does nothing about a file another program has open, and a
    /// shielded button that changes nothing teaches somebody to reach for the
    /// consent prompt when the consent prompt is not the answer.
    /// </summary>
    [AvaloniaFact]
    public void A_failure_that_was_not_about_permission_offers_no_rights()
    {
        var shell = Shell(canElevate: true, out _);

        shell.Retryable = Offer(2, null);

        Assert.True(shell.CanRetryOperation);
        Assert.False(shell.CanRetryAsAdministrator);
    }

    /// <summary>
    /// And a desktop with no way to ask — no pkexec — is offered none either,
    /// however the failure went. The row is absent rather than present and
    /// failing, which is the rule the admin entries already follow.
    /// </summary>
    [AvaloniaFact]
    public void A_desktop_with_no_way_to_ask_is_offered_none()
    {
        var shell = Shell(canElevate: false, out _);

        shell.Retryable = Offer(2, Refused(2));

        Assert.True(shell.CanRetryOperation);
        Assert.False(shell.CanRetryAsAdministrator);
    }

    /// <summary>
    /// **Its own count, which can be smaller than the one beside it.** A batch
    /// that lost one file to a program holding it open and three to a protected
    /// folder reads "Retry 4" beside "Retry 3 as administrator", and both
    /// numbers are true.
    /// </summary>
    [AvaloniaFact]
    public void The_button_says_how_many_it_will_ask_rights_for()
    {
        var shell = Shell(canElevate: true, out _);

        shell.Retryable = Offer(4, Refused(3));

        Assert.True(shell.CanRetryAsAdministrator);
        Assert.Equal("Retry 4", shell.RetryLabel);
        Assert.Equal("Retry 3 as administrator", shell.RetryAsAdministratorLabel);
    }

    /// <summary>
    /// **Both new properties are announced when the offer changes.** Neither is
    /// stored: they are worked out from the offer, so nothing raises a change
    /// for them on its own. A button bound to a label that is never announced
    /// keeps the count from the operation before — "Retry 3 as administrator"
    /// sitting over a batch that lost seven.
    /// </summary>
    [AvaloniaFact]
    public void The_bar_is_told_when_the_offer_changes()
    {
        var shell = Shell(canElevate: true, out _);

        var announced = new List<string>();
        shell.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        shell.Retryable = Offer(4, Refused(3));

        Assert.Contains(nameof(ShellViewModel.CanRetryAsAdministrator), announced);
        Assert.Contains(nameof(ShellViewModel.RetryAsAdministratorLabel), announced);
    }

    /// <summary>
    /// **The elevated retry hangs its progress where the operation that failed
    /// reported, exactly as the plain retry does.** The pane is remembered when
    /// the offer is taken, so a retry pressed after switching tabs still
    /// reports on the tab whose copy failed rather than on whichever one is in
    /// front.
    ///
    /// **THIS IS A GUARD, and it reads source text rather than running it.**
    /// The remembered pane is only ever set to the active one, so making the
    /// two differ needs a completed operation on one tab and a switch to
    /// another before pressing — and the identical expression in the plain
    /// retry beside it has no such test either. What this can see is the two
    /// staying the same as each other, which is the way the pair rots.
    /// </summary>
    [Fact]
    public void The_administrator_retry_reports_on_the_pane_that_ran_it()
    {
        var source = RepoSource.Ui("ViewModels", "ShellViewModel.cs");

        Assert.Equal(
            2,
            source.Split("(_retryPane ?? ActiveTab)?.Adopt(").Length - 1);
    }

    /// <summary>
    /// Pressing it takes the offer and hands the request over unchanged — the
    /// argument list is the whole of what crosses to a process with rights this
    /// one does not have.
    ///
    /// **Awaited, because the asking happens on a pool thread.**
    /// ElevatedRun.Start does its waiting inside a Task.Run so the button
    /// returns at once; reading <c>Asked</c> straight after pressing raced that
    /// thread and failed six times in eight.
    /// </summary>
    [AvaloniaFact]
    public async Task Pressing_it_hands_the_request_over_and_takes_the_offer()
    {
        var shell = Shell(canElevate: true, out var desktop);
        var request = Refused(2);

        shell.Start(null, Path.GetTempPath());

        shell.OperationStatus = "two were left behind";
        shell.OperationProblems = [new ProblemRow("f0.txt", "f0.txt", "no permission")];
        shell.Retryable = Offer(2, request);

        shell.RetryAsAdministratorCommand.Execute(null);

        await desktop.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(request.ToArguments(), desktop.Asked);
        Assert.Null(shell.Retryable);
        Assert.False(shell.CanRetryAsAdministrator);
        Assert.Empty(shell.OperationProblems);
    }

    /// <summary>
    /// **The bar says what it is waiting for.** A consent dialog can sit on
    /// screen for as long as somebody takes to read it, and an empty bar
    /// underneath it says the button did nothing.
    /// </summary>
    [AvaloniaFact]
    public void The_bar_says_it_is_waiting_for_the_prompt()
    {
        var shell = Shell(canElevate: true, out _);

        shell.Start(null, Path.GetTempPath());

        shell.OperationStatus = "two were left behind";
        shell.Retryable = Offer(2, Refused(2));

        shell.RetryAsAdministratorCommand.Execute(null);

        Assert.Equal("waiting for administrator…", shell.OperationStatus);
    }

    /// <summary>
    /// Pressing it with nothing offered starts nothing at all.
    ///
    /// The sentence is what is asserted rather than only the argument list:
    /// <c>Asked</c> is written on a pool thread, so a null read of it a moment
    /// after pressing would also be what a command that HAD fired looked like.
    /// The status line is written on this thread, before the run is started.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_it_with_nothing_offered_asks_for_nothing()
    {
        var shell = Shell(canElevate: true, out var desktop);

        shell.OperationStatus = "nothing happened here";

        shell.RetryAsAdministratorCommand.Execute(null);

        Assert.Equal("nothing happened here", shell.OperationStatus);
        Assert.Null(desktop.Asked);
    }

    /// <summary>
    /// The button carries the count, hides when there is nothing to ask rights
    /// for, and says in a tip whose consent is being asked for.
    /// </summary>
    [Fact]
    public void The_bar_shows_it_only_when_rights_would_help()
    {
        var button = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("Command")
                         == "{Binding RetryAsAdministratorCommand}");

        Assert.Equal("{Binding CanRetryAsAdministrator}", (string?)button.Attribute("IsVisible"));
        Assert.Equal("{Binding RetryAsAdministratorLabel}", (string?)button.Attribute("Content"));

        Assert.Contains(
            "Vaktari holds no rights of its own",
            (string?)button.Attribute("ToolTip.Tip") ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **The elevated launch is answered before the window.** It runs with no
    /// display attached — pkexec unsets DISPLAY outright — and must depend on
    /// no window, no settings file and no theme, exactly as the uninstaller's
    /// flag above it does.
    ///
    /// Run for real, unelevated, which is every part of this route a test can
    /// prove: what comes after is the system's consent dialog, and nothing
    /// automated can answer that.
    /// </summary>
    [Fact]
    public void The_elevated_flag_does_the_work_and_reports_a_count()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "vaktari-elevated-" + Guid.NewGuid().ToString("N")[..12]);

        Directory.CreateDirectory(directory);

        try
        {
            var doomed = Path.Combine(directory, "gone.txt");
            File.WriteAllText(doomed, "one");

            var code = Vaktari.Ui.Program.ElevatedExitCode(
                [ElevatedRequest.Flag, "delete", doomed]);

            Assert.Equal(0, code);
            Assert.False(File.Exists(doomed));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>And an ordinary launch is not one of these at all.</summary>
    [Fact]
    public void An_ordinary_launch_is_not_an_elevated_one()
        => Assert.Null(Vaktari.Ui.Program.ElevatedExitCode([Path.GetTempPath()]));

    /// <summary>
    /// And it is answered where the uninstaller's flag is: before the instance
    /// mutex, before the settings, before the window. This copy runs with no
    /// display and must depend on none of them — and as root it must not touch
    /// the state directory, or it leaves files there the person can no longer
    /// write.
    ///
    /// Read off the source, because the order of two statements in Main is not
    /// something a unit can be asked about.
    /// </summary>
    [Fact]
    public void The_elevated_launch_is_answered_before_the_window()
    {
        var source = RepoSource.Ui("Program.cs");

        var answered = source.IndexOf(
            "if (ElevatedExitCode(args) is { } code)", StringComparison.Ordinal);

        Assert.True(answered > 0, "nothing in Main answers an elevated launch");

        foreach (var later in new[] { "ClaimInstanceMutex();", "Run(args);" })
            Assert.True(
                answered < source.IndexOf(later, StringComparison.Ordinal),
                $"the elevated launch is answered after {later}");
    }

    private sealed class Nothing : IFileSystemProvider
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

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
