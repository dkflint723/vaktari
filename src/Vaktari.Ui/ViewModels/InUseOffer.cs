namespace Vaktari.Ui.ViewModels;

/// <summary>
/// A rename refused because something has the item, or something inside it,
/// open — what to say, and how to try again once the person has closed it.
///
/// **It says THAT something has it open, never what.** Vaktari does not look
/// inside other programs, so it cannot name one; the hint says where a person
/// can look instead (<see cref="Hint"/>), and Try again is how they come back.
/// </summary>
/// <param name="Sentence">The whole line the prompt shows: the item by name
/// and why it could not be renamed.</param>
/// <param name="TryAgain">The same rename again. True when it went through;
/// a second refusal raises a fresh offer of its own.</param>
public sealed record InUseOffer(string Sentence, Func<Task<bool>> TryAgain)
{
    /// <summary>
    /// Where to look for what has it open, on the one desktop that has a tool
    /// for it built in or one step away; null elsewhere.
    ///
    /// **Pointed to, never run.** Both are the person's to open: Resource
    /// Monitor ships with Windows, and File Locksmith with PowerToys.
    /// </summary>
    public static string? Hint => OperatingSystem.IsWindows()
        ? "to find what has it open: Resource Monitor (CPU tab ▸ Associated Handles, search the name) or PowerToys File Locksmith"
        : null;

    /// <summary>The sentence for <paramref name="name"/>, refused with
    /// <paramref name="why"/>.</summary>
    public static string For(string name, Exception why)
        => $"could not rename “{name}” — {Core.FileSystem.Failures.Describe(why, "rename that")}";
}
