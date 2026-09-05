using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// <see cref="IOperationHandle.Landed"/> hands out a copy, the way
/// <see cref="IOperationHandle.Problems"/> next to it does.
///
/// **The engine writes from the pool and the pane reads on the UI thread.**
/// Both engines report their landings from inside the same Task.Run the copying
/// happens in, and the pane reads the property from a dispatcher job hung off
/// Completion — so a getter that handed back the field itself would hand the UI
/// a list another thread is still appending to, and the pane walks it twice
/// (once to see whether any of it is on screen, once to select). A List that
/// grows during a foreach throws.
/// </summary>
public sealed class LandedIsASnapshotTests
{
    [Fact]
    public void What_was_read_does_not_change_when_more_arrives()
    {
        var handle = new OperationHandle();

        handle.Arrived(["/dst/one.txt"]);

        var read = handle.Landed;

        handle.Arrived(["/dst/two.txt"]);

        Assert.Equal(["/dst/one.txt"], read);
    }

    /// <summary>
    /// And the two calls accumulate rather than replacing, which is what makes
    /// the line above a copy rather than an accident of the second call
    /// starting a new list.
    /// </summary>
    [Fact]
    public void Everything_reported_is_there_to_read_afterwards()
    {
        var handle = new OperationHandle();

        handle.Arrived(["/dst/one.txt"]);
        handle.Arrived(["/dst/two.txt"]);

        Assert.Equal(["/dst/one.txt", "/dst/two.txt"], handle.Landed);
    }
}
