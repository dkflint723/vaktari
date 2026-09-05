using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Reading who exists out of /etc/passwd and /etc/group.
///
/// Plain facts, not PosixFacts: these are text parsing and the text is supplied,
/// so they assert the same thing wherever they run.
/// </summary>
public sealed class UnixAccountsTests
{
    private static readonly string[] Passwd =
    [
        "root:x:0:0:root:/root:/bin/bash",
        "daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin",
        "amelia:x:1000:1000:Amelia:/home/amelia:/bin/bash",
        "gil:x:1001:1001:Gil:/home/gil:/bin/bash",
    ];

    private static readonly string[] Group =
    [
        "root:x:0:",
        "sudo:x:27:amelia",
        "amelia:x:1000:",
        "gil:x:1001:",
        "audio:x:29:gil,amelia",
        "plugdev:x:46:gil",
    ];

    [Fact]
    public void Every_login_name_is_read_in_file_order()
        => Assert.Equal(["root", "daemon", "amelia", "gil"], UnixAccounts.UsersIn(Passwd));

    [Fact]
    public void Every_group_name_is_read_in_file_order()
        => Assert.Equal(["root", "sudo", "amelia", "gil", "audio", "plugdev"],
                        UnixAccounts.GroupsIn(Group));

    /// <summary>
    /// **The primary group is named nowhere in /etc/group.** It is a gid in the
    /// user's passwd line, and on most distributions it is the group of the
    /// same name that every one of their files already belongs to — so a
    /// chooser built from the member lists alone would leave out the group the
    /// file is in at the moment it is opened.
    /// </summary>
    [Fact]
    public void The_primary_group_comes_first_and_comes_from_passwd()
    {
        var mine = UnixAccounts.GroupsFor(Passwd, Group, "amelia");

        Assert.Equal("amelia", mine[0]);
    }

    [Fact]
    public void And_every_group_that_lists_them_as_a_member()
    {
        var mine = UnixAccounts.GroupsFor(Passwd, Group, "amelia");

        Assert.Equal(["amelia", "sudo", "audio"], mine);
    }

    /// <summary>A member list is a comma-separated field, and the name being
    /// looked for can sit anywhere in it — matching only the first entry would
    /// have left gil out of audio.</summary>
    [Fact]
    public void A_name_later_in_a_member_list_still_counts()
        => Assert.Contains("audio", UnixAccounts.GroupsFor(Passwd, Group, "gil"));

    /// <summary>A substring is not a member: "gil" is not in a group whose
    /// only member is "gilbert".</summary>
    [Fact]
    public void A_name_that_merely_starts_another_one_does_not_count()
    {
        string[] group = ["staff:x:50:gilbert"];

        Assert.DoesNotContain("staff", UnixAccounts.GroupsFor(Passwd, group, "gil"));
    }

    /// <summary>Nobody is in a group twice, even when they are its primary AND
    /// listed in its members — which is how some distributions write it.</summary>
    [Fact]
    public void A_group_that_names_its_own_primary_member_is_listed_once()
    {
        string[] group = ["amelia:x:1000:amelia"];

        Assert.Equal(["amelia"], UnixAccounts.GroupsFor(Passwd, group, "amelia"));
    }

    /// <summary>
    /// A comment, a blank line and a half-written record are all things a real
    /// /etc file can hold, and none of them is an account. Read as one, the
    /// first would have offered "#" in a chooser.
    /// </summary>
    [Fact]
    public void Comments_and_scraps_are_not_accounts()
    {
        string[] passwd = ["# added by the installer", "", "truncated:x:5", "kit:x:9:9:Kit:/home/kit:/bin/sh"];

        Assert.Equal(["kit"], UnixAccounts.UsersIn(passwd));
    }

    /// <summary>A group with no members ends in a colon and nothing, which is
    /// the common case — so the test for a usable record is the COUNT of
    /// fields and never whether the last one is filled.</summary>
    [Fact]
    public void A_group_with_no_members_is_still_a_group()
        => Assert.Contains("root", UnixAccounts.GroupsIn(["root:x:0:"]));

    [Fact]
    public void A_user_who_is_in_nothing_gets_nothing()
        => Assert.Empty(UnixAccounts.GroupsFor(Passwd, Group, "nobody"));
}
