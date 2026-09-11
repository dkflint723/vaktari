namespace Vaktari.Core;

/// <summary>
/// A last-in-first-out stack that forgets its OLDEST entry once it holds more
/// than it was told to.
///
/// **The undo history had no ceiling.** Every copy, move, rename, batch rename,
/// creation and delete pushed an entry that held the paths it landed — a batch
/// rename holds every old and new name — and nothing ever let one go for the
/// life of the process. A session that renamed a few thousand photos and moved
/// a few hundred folders carried all of it until Vaktari closed, for the sake
/// of an undo nobody was going to press a thousand steps back.
///
/// Thread-safe, because the operations that push run on the pool while the
/// window reads the top to name the Undo row. A lock rather than
/// <see cref="System.Collections.Concurrent.ConcurrentStack{T}"/>, which was
/// what this replaced: that type can only drop from the top, and the entry to
/// forget is the one at the bottom.
/// </summary>
public sealed class BoundedStack<T>
{
    private readonly LinkedList<T> _items = new();
    private readonly int _capacity;
    private readonly object _gate = new();

    /// <param name="capacity">How many are kept. The oldest goes when the next
    /// one arrives; a capacity below one would keep nothing and is refused.</param>
    public BoundedStack(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public bool IsEmpty
    {
        get { lock (_gate) return _items.Count == 0; }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Puts <paramref name="item"/> on top, letting the oldest go if
    /// the stack is already full.</summary>
    public void Push(T item)
    {
        lock (_gate)
        {
            _items.AddFirst(item);

            if (_items.Count > _capacity) _items.RemoveLast();
        }
    }

    public bool TryPop(out T item)
    {
        lock (_gate)
        {
            if (_items.First is { } first)
            {
                item = first.Value;
                _items.RemoveFirst();
                return true;
            }
        }

        item = default!;
        return false;
    }

    public bool TryPeek(out T item)
    {
        lock (_gate)
        {
            if (_items.First is { } first)
            {
                item = first.Value;
                return true;
            }
        }

        item = default!;
        return false;
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }
}
