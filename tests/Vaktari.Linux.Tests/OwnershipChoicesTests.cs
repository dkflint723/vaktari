using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What the properties sheet may offer, and what it does with the answer.
///
/// **Owner and group reached that window as two lines of text.** They are two
/// thirds of what a POSIX mode means — "group: read, write" says nothing until
/// you know which group — so the sheet let you set the bits and not the
/// principals, and both desktops it sits beside offer them.
///
/// The rules are not "put every account in a box". chown(2) is root-only on
/// purpose, so that nobody dodges a quota by handing their files to a stranger;
/// and the group half is open to the file's owner, over the groups they are in.
/// A chooser that offered what would be refused would be a list of failures.
/// </summary>
public sealed class OwnershipChoicesTests
{
    private static readonly string[] Passwd =
    [
        "root:x:0:0:root:/root:/bin/bash",
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
    ];

    private static Vaktari.Core.FileSystem.Ownership Decide(
        string owner, string group, string me, bool root)
        => LinuxPropertiesProvider.Decide(owner, group, Passwd, Group, me, root);

    // ---- who may change what -----------------------------------------------

    /// <summary>
    /// **chown is root-only, and an editable box full of refusals is worse than
    /// a line of text.** Somebody who is not root gets the owner as a name they
    /// cannot change, which is exactly what the sheet showed before — the
    /// difference is that the box beside it is now live.
    /// </summary>
    [Fact]
    public void An_ordinary_person_may_not_give_a_file_away()
    {
        var owned = Decide("amelia", "amelia", me: "amelia", root: false);

        Assert.False(owned.CanChangeOwner);
        Assert.Equal(["amelia"], owned.Owners);
    }

    [Fact]
    public void Root_may_hand_a_file_to_anybody_on_the_machine()
    {
        var owned = Decide("amelia", "amelia", me: "root", root: true);

        Assert.True(owned.CanChangeOwner);
        Assert.Equal(["root", "amelia", "gil"], owned.Owners);
    }

    /// <summary>The half an ordinary person does get, which is the common
    /// case: your own file, into a group you are in.</summary>
    [Fact]
    public void The_owner_may_move_their_own_file_between_their_own_groups()
    {
        var owned = Decide("amelia", "amelia", me: "amelia", root: false);

        Assert.True(owned.CanChangeGroup);
        Assert.Equal(["amelia", "sudo", "audio"], owned.Groups);
    }

    /// <summary>
    /// Somebody else's file is somebody else's: chgrp wants ownership as well
    /// as membership, so offering the box here would offer a refusal.
    /// </summary>
    [Fact]
    public void A_file_belonging_to_somebody_else_offers_no_group_either()
    {
        var owned = Decide("gil", "gil", me: "amelia", root: false);

        Assert.False(owned.CanChangeGroup);
    }

    [Fact]
    public void Root_may_move_any_file_into_any_group()
    {
        var owned = Decide("gil", "gil", me: "root", root: true);

        Assert.True(owned.CanChangeGroup);
        Assert.Equal(["root", "sudo", "amelia", "gil", "audio"], owned.Groups);
    }

    /// <summary>A person in no group at all has nothing to choose between, so
    /// the box stays shut rather than opening onto one entry.</summary>
    [Fact]
    public void Somebody_in_no_group_is_offered_no_change()
    {
        var owned = LinuxPropertiesProvider.Decide(
            "kit", "kit", Passwd, Group, me: "kit", root: false);

        Assert.False(owned.CanChangeGroup);
    }

    // ---- the name that is already there ------------------------------------

    /// <summary>
    /// **The NSS case, and the reason the current value is added by hand.** A
    /// machine whose accounts come from LDAP or SSSD has those names in neither
    /// /etc file — so a chooser built from the files alone could not display
    /// the value it was bound to, and would have silently proposed changing it
    /// to whatever sorted first.
    /// </summary>
    [Fact]
    public void A_name_in_neither_file_is_still_shown_as_the_one_it_has()
    {
        var owned = Decide("ldapuser", "ldapgroup", me: "root", root: true);

        Assert.Equal("ldapuser", owned.Owners[0]);
        Assert.Equal("ldapgroup", owned.Groups[0]);
    }

    /// <summary>And it is not added twice when it is already there.</summary>
    [Fact]
    public void A_name_that_is_in_the_file_is_listed_once()
    {
        var owned = Decide("amelia", "audio", me: "root", root: true);

        Assert.Single(owned.Owners, n => n == "amelia");
        Assert.Single(owned.Groups, n => n == "audio");
    }

    // ---- handing it over ---------------------------------------------------

    /// <summary>
    /// Both at once, because chown takes both at once — two calls would leave a
    /// file half moved when the second was refused.
    /// </summary>
    [Fact]
    public async Task Both_names_go_in_one_call()
    {
        IReadOnlyList<string>? argv = null;

        var provider = new LinuxPropertiesProvider
        {
            RunOverride = (a, _) => { argv = a; return Task.FromResult((0, "")); },
        };

        Assert.Null(await provider.SetOwnershipAsync(
            "/home/amelia/notes.txt", "gil", "audio", recursive: false, default));

        Assert.Equal(["gil:audio", "/home/amelia/notes.txt"], argv);
    }

    [Fact]
    public async Task Applying_to_the_contents_says_so_to_chown()
    {
        IReadOnlyList<string>? argv = null;

        var provider = new LinuxPropertiesProvider
        {
            RunOverride = (a, _) => { argv = a; return Task.FromResult((0, "")); },
        };

        await provider.SetOwnershipAsync("/srv/share", "gil", "audio", recursive: true, default);

        Assert.Equal("-R", argv![0]);
    }

    /// <summary>
    /// **chown's own words, because they name which half was the problem.**
    /// "invalid group" and "Operation not permitted" send somebody to two
    /// different places, and a sentence of ours saying "failed" sends them to a
    /// terminal to find out which.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_reported_in_the_words_chown_used()
    {
        var provider = new LinuxPropertiesProvider
        {
            RunOverride = (_, _) => Task.FromResult(
                (1, "chown: invalid group: 'gil:nosuch'\n")),
        };

        var said = await provider.SetOwnershipAsync(
            "/home/amelia/notes.txt", "gil", "nosuch", recursive: false, default);

        Assert.Equal("invalid group: 'gil:nosuch'", said);
    }

    /// <summary>A refusal with nothing to say still has to say something: an
    /// empty status line reads as success.</summary>
    [Fact]
    public async Task A_silent_refusal_still_says_something()
    {
        var provider = new LinuxPropertiesProvider
        {
            RunOverride = (_, _) => Task.FromResult((1, "   \n")),
        };

        var said = await provider.SetOwnershipAsync(
            "/home/amelia/notes.txt", "gil", "audio", recursive: false, default);

        Assert.False(string.IsNullOrWhiteSpace(said));
    }
}
