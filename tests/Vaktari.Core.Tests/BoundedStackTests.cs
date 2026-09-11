using Vaktari.Core;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The stack the undo history sits on.
///
/// **It replaced a ConcurrentStack that could only ever grow.** These pin the
/// two things that type could not do — forget the oldest, and say how many it
/// holds — alongside the stack behaviour it has to keep.
/// </summary>
public sealed class BoundedStackTests
{
    [Fact]
    public void The_oldest_entry_goes_when_the_ceiling_is_passed()
    {
        var stack = new BoundedStack<int>(3);

        stack.Push(1);
        stack.Push(2);
        stack.Push(3);
        stack.Push(4);

        Assert.Equal(3, stack.Count);

        // Top to bottom: 4, 3, 2 — the 1 is gone, and it is the 1 rather than
        // the 4, which is the whole difference from a stack that drops from
        // the top.
        Assert.True(stack.TryPop(out var top));
        Assert.Equal(4, top);
        Assert.True(stack.TryPop(out var next));
        Assert.Equal(3, next);
        Assert.True(stack.TryPop(out var last));
        Assert.Equal(2, last);
        Assert.False(stack.TryPop(out _));
    }

    [Fact]
    public void Under_the_ceiling_it_is_an_ordinary_stack()
    {
        var stack = new BoundedStack<string>(10);

        Assert.True(stack.IsEmpty);
        Assert.False(stack.TryPeek(out _));

        stack.Push("a");
        stack.Push("b");

        Assert.False(stack.IsEmpty);
        Assert.True(stack.TryPeek(out var peeked));
        Assert.Equal("b", peeked);
        Assert.Equal(2, stack.Count);

        stack.Clear();

        Assert.True(stack.IsEmpty);
    }

    /// <summary>A capacity of zero would keep nothing and make every Push a
    /// silent no-op, which is the kind of stack nobody means to build.</summary>
    [Fact]
    public void A_capacity_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedStack<int>(0));
    }
}
