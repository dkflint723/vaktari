namespace Vaktari.Core;

/// <summary>
/// A trace for failures that are deliberately swallowed.
///
/// **Why this exists.** A `catch { }` is sometimes the right answer — a machine
/// without `avahi` should not see an error because network discovery found no
/// daemon, and a shutdown path that cannot delete its own lock file has nothing
/// useful to say. But a completely silent catch is also how a
/// `NullReferenceException` hid for two rounds of debugging on 30 July 2026, and
/// this project's standing rule is that a failure which prints nothing is
/// invisible.
///
/// So these catches stay non-fatal and stay quiet by default, and say what
/// happened when asked: <c>VAKTARI_QUIET_DEBUG=1</c>.
///
/// **This is not for failures a user should know about.** Those get their own
/// message, unconditionally. This is for the ones where carrying on is genuinely
/// correct and the only cost of silence is a harder debugging session later.
/// </summary>
public static class Quiet
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("VAKTARI_QUIET_DEBUG") == "1";

    /// <param name="area">
    /// Where it happened, in the same short form the other diagnostics use —
    /// "avahi", "scripts", "vcs". Enough to grep for.
    /// </param>
    public static void Swallowed(string area, Exception ex)
    {
        // To the log whether or not stderr is asked for: this is exactly the
        // class of failure a bug report needs and a user never sees. The log
        // redacts paths itself, so nothing is lost by passing the message on.
        Diagnostics.Log.Warn(area, $"{ex.GetType().Name}: {ex.Message}");

        if (!Enabled) return;

        // Type as well as message: "Object reference not set" without a type or a
        // frame is exactly the uninformative line that cost those two rounds.
        Console.Error.WriteLine($"[vaktari] quiet: {area} — {ex.GetType().Name}: {ex.Message}");
    }
}
