using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// One HKCU key, watched for written values without a thread of its own.
///
/// **What it replaced:** a background thread per key, blocked in a synchronous
/// RegNotifyChangeKeyValue — asleep, so cheap, but impossible to call off from
/// outside, which is why the theme provider's watches had to live for the
/// whole process whatever the settings said. Here the notification is
/// asynchronous, on an event that the thread pool's one shared wait thread
/// waits on, and it can be stopped.
///
/// **REG_NOTIFY_THREAD_AGNOSTIC is not optional.** Without it the registration
/// belongs to the thread that made it, and the system signals the event when
/// that thread exits — a raise for a change nobody made. Every re-arm happens
/// on a pool thread, and pool threads retire, so without the flag an idle
/// machine would re-read its palette whenever the pool shrank.
///
/// **Stopping races the callback, so both go through one state** (Armed,
/// Stopping, Stopped) under one lock. A callback re-arms only while Armed. A
/// stop marks Stopping, then unregisters and WAITS for any callback in flight —
/// off the callback's thread, so it never waits on itself — and only then
/// closes the key and the event. Closing first would let a callback re-arm a
/// handle value that another RegOpenKeyEx may already have been given.
/// </summary>
internal sealed class KeyWatch
{
    private enum State { Armed, Stopping, Stopped }

    private readonly Lock _gate = new();
    private readonly nint _key;
    private readonly AutoResetEvent _changed = new(false);
    private readonly Action _notify;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RegisteredWaitHandle? _wait;
    private State _state = State.Armed;

    private KeyWatch(nint key, Action notify)
    {
        _key = key;
        _notify = notify;
    }

    /// <summary>Completes once the watch has stopped — asked to, or because
    /// the key went away — and its key and event are closed.</summary>
    internal Task Ended => _ended.Task;

    internal static KeyWatch? Start(string subKey, Action notify)
    {
        if (Native.RegOpenKeyEx(Native.HKEY_CURRENT_USER, subKey, 0, Native.KEY_READ, out var key)
            != Native.ERROR_SUCCESS)
            return null;

        var watch = new KeyWatch(key, notify);

        if (!watch.Arm())
        {
            Native.RegCloseKey(key);
            watch._changed.Dispose();
            return null;
        }

        watch._wait = ThreadPool.RegisterWaitForSingleObject(
            watch._changed, static (state, _) => ((KeyWatch)state!).Woke(), watch,
            Timeout.Infinite, executeOnlyOnce: false);

        return watch;
    }

    private bool Arm()
        => Native.RegNotifyChangeKeyValue(
               _key, watchSubtree: false,
               Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_THREAD_AGNOSTIC,
               _changed.SafeWaitHandle.DangerousGetHandle(), asynchronous: true)
           == Native.ERROR_SUCCESS;

    /// <summary>
    /// The event was signalled. Re-armed FIRST, so a write landing while the
    /// subscribers run is not lost, then raised. A re-arm that fails is a key
    /// that has gone: the watch ends, and says nothing — a deleted key changed
    /// nothing anyone reads.
    /// </summary>
    private void Woke()
    {
        bool rearmed;

        lock (_gate)
        {
            if (_state != State.Armed) return;

            rearmed = Arm();
        }

        if (rearmed)
        {
            try
            {
                _notify();
            }
            catch (Exception ex)
            {
                Quiet.Swallowed("theme", ex);
            }

            return;
        }

        // Not from here: the stop waits for callbacks in flight, and this is one.
        _ = Stop();
    }

    /// <summary>Stops watching, from any thread; the task ends when it has.</summary>
    internal Task Stop()
    {
        lock (_gate)
        {
            if (_state != State.Armed) return Ended;

            _state = State.Stopping;
        }

        _ = Task.Run(Close);

        return Ended;
    }

    private void Close()
    {
        try
        {
            using var drained = new ManualResetEvent(false);

            if (_wait is { } wait && wait.Unregister(drained)) drained.WaitOne();

            Native.RegCloseKey(_key);
            _changed.Dispose();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("theme", ex);
        }
        finally
        {
            lock (_gate) _state = State.Stopped;

            _ended.TrySetResult();
        }
    }
}
