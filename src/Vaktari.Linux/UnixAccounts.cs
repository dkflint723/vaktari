namespace Vaktari.Linux;

/// <summary>
/// Who exists on this machine, read the way every other Linux tool reads it.
///
/// **Two text files rather than getent.** The properties window asks this once
/// per open, and spawning a process to answer a question that is four lines of
/// parsing puts a fork on the path of a dialog appearing. The same argument the
/// mount table already carries: /proc and /etc are readable, and reading them is
/// reading text.
///
/// NSS is the honest limitation and is stated rather than hidden: a machine
/// whose accounts come from LDAP or SSSD has names in neither file, and this
/// will not list them. That is a smaller failure than it sounds — the list is
/// what a chooser OFFERS, and the name already on the file is read from the
/// file itself, so a directory-service account still shows as the owner. It
/// just cannot be picked from the list.
/// </summary>
internal static class UnixAccounts
{
    /// <summary>
    /// Every login name in /etc/passwd, in file order.
    ///
    /// Comments and blank lines are skipped, and so is any line without the
    /// six colons a passwd entry has — a truncated write or an editor's backup
    /// marker should not become an account named "#".
    /// </summary>
    internal static IReadOnlyList<string> UsersIn(IEnumerable<string> passwd)
    {
        var names = new List<string>();

        foreach (var line in passwd)
            if (Fields(line, 7) is { } parts && parts[0].Length > 0)
                names.Add(parts[0]);

        return names;
    }

    /// <summary>Every group name in /etc/group, in file order.</summary>
    internal static IReadOnlyList<string> GroupsIn(IEnumerable<string> group)
    {
        var names = new List<string>();

        foreach (var line in group)
            if (Fields(line, 4) is { } parts && parts[0].Length > 0)
                names.Add(parts[0]);

        return names;
    }

    /// <summary>
    /// The groups one user is in.
    ///
    /// **Both kinds, and missing either one is wrong in a way people notice.**
    /// The primary group is named nowhere in /etc/group — it is a gid in the
    /// user's passwd line, and on most distributions it is the group of the
    /// same name that every one of their files already belongs to. The
    /// supplementary ones are the member lists. A chooser built from the member
    /// lists alone would leave out the group the file is in right now.
    ///
    /// Primary first, because it is the one a person means.
    /// </summary>
    internal static IReadOnlyList<string> GroupsFor(
        IEnumerable<string> passwd, IEnumerable<string> group, string user)
    {
        string? primaryGid = null;

        foreach (var line in passwd)
            if (Fields(line, 7) is { } parts && parts[0] == user)
            {
                primaryGid = parts[3];
                break;
            }

        var mine = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? primary = null;
        var supplementary = new List<string>();

        foreach (var line in group)
        {
            if (Fields(line, 4) is not { } parts || parts[0].Length == 0) continue;

            if (primaryGid is not null && parts[2] == primaryGid) primary = parts[0];

            foreach (var member in parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (member.Trim() == user) { supplementary.Add(parts[0]); break; }
        }

        if (primary is not null && seen.Add(primary)) mine.Add(primary);

        foreach (var name in supplementary)
            if (seen.Add(name)) mine.Add(name);

        return mine;
    }

    /// <summary>
    /// One record, split, or null when the line is not one.
    ///
    /// A field may legitimately be empty — a group with no members ends in a
    /// colon and nothing — so the test is the COUNT of fields, never whether
    /// they are filled.
    /// </summary>
    private static string[]? Fields(string line, int wanted)
    {
        if (line.Length == 0 || line[0] == '#') return null;

        var parts = line.Split(':');

        return parts.Length >= wanted ? parts : null;
    }
}
