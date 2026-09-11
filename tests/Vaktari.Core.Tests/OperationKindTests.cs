using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a handle answers when nobody told it what it was for.
///
/// **A handle said what it was touching and never what it was DOING**, so with
/// several operations running at once there was no sentence to put on a row for
/// any of them — the transfer bar follows the newest handle and reports its
/// progress line, and the rest had nothing on screen at all. The kind is what
/// the window turns into "Copying 3 items to Photos".
///
/// Both defaults are here because both are load-bearing in the same way: a
/// handle built by something that is not one of the four file calls has to
/// construct, and must NOT be given a verb it did not earn — the wrong verb on
/// a row is worse than no verb.
///
/// Measured, so the claim is not larger than the fact: every one of the seven
/// <c>new OperationHandle</c> sites in the repository sets a kind, so nothing
/// in the shipping application answers Other. What reaches it is a handle built
/// in a test, and an implementor written outside this assembly.
/// </summary>
public sealed class OperationKindTests
{
    /// <summary>
    /// Every test that wants a handle without an engine behind it builds one of
    /// these. None of them says what kind of work it is, and none of them
    /// should come back describing itself as a copy.
    /// </summary>
    [Fact]
    public void A_handle_built_without_a_kind_is_of_no_particular_kind()
        => Assert.Equal(OperationKind.Other, new OperationHandle().Kind);

    /// <summary>
    /// And the interface's own default, which is what an implementor written
    /// outside this assembly would get — the same arrangement, and the same
    /// argument, as <see cref="IOperationHandle.CanPause"/> one member below
    /// it: adding a member to a published interface must not break the things
    /// that implement it.
    ///
    /// A stub rather than <see cref="OperationHandle"/>, because
    /// OperationHandle answers from a property of its own and would prove
    /// nothing about the default underneath it. A default interface member is
    /// reachable only through the interface, which is why this is typed as one.
    /// </summary>
    [Fact]
    public void An_implementor_that_declares_no_kind_gets_the_default()
    {
        IOperationHandle bare = new Bare();

        Assert.Equal(OperationKind.Other, bare.Kind);
    }

    /// <summary>
    /// The least an IOperationHandle can be. Every member is the emptiest
    /// honest answer; the point of it is the members it does NOT declare.
    /// </summary>
    private sealed class Bare : IOperationHandle
    {
        public Guid Id => Guid.Empty;
        public OperationState State => OperationState.Queued;
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>();
        public IReadOnlyList<string> Paths => [];
        public IReadOnlyList<ItemProblem> Problems => [];
        public IReadOnlyList<string> Landed => [];
        public Task Completion => Task.CompletedTask;
        public Exception? Error => null;
        public RetryOffer? Retry => null;

        public void Pause() { }
        public void Resume() { }
        public void Cancel() { }

        // Declared with explicit accessors so an event nobody raises is not a
        // warning, which this build treats as an error.
        public event EventHandler<OperationProgress>? Progressed { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }
    }
}
