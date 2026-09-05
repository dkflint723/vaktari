using System.Text.Json;
using Vaktari.Core.Settings;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// How <see cref="StartupLocation"/> survives a trip through settings.json.
///
/// **The enum is persisted, so adding <see cref="StartupLocation.Computer"/>
/// to it was a change to a file format and not only to a type.** The member
/// was appended rather than slotted in beside HomeFolder, and the reason is
/// written on the enum: today the value is written by NAME, which makes the
/// order free, but the two things that would make it costly are one edit
/// apart — dropping <c>UseStringEnumConverter</c>, or a converter that falls
/// back to numbers. Either turns the declaration order into the on-disk
/// meaning, and a member inserted in the middle would then have renumbered
/// SpecificFolder underneath every settings.json already written: somebody
/// whose file said "open a specific folder" would launch on the drive listing.
///
/// Both halves are pinned here — the name on disk AND the numbering behind it
/// — because each is what makes the other safe, and the dialog and launch tests
/// in Vaktari.Ui.Tests exercise the enum in memory, where neither shows.
///
/// What the OTHER direction costs — a build that does not have the name
/// reading a file that does — is measured in
/// StartupOnTheDriveListingTests.A_startup_name_this_build_does_not_have_costs_the_whole_file,
/// and it has to live there: the answer belongs to JsonSettingsStore, which is
/// in Vaktari.Ui, and this project references Vaktari.Core only. A version of
/// that test written here caught its own JsonException and asserted on a
/// substitute it had built itself, so it said nothing about the store it named.
/// </summary>
public sealed class StartupLocationPersistenceTests
{
    private static string Write(StartupLocation location) => JsonSerializer.Serialize(
        new SettingsState { Startup = new StartupSettings { ShowOnStartup = location } },
        SettingsJsonContext.Default.SettingsState);

    /// <summary>
    /// Every choice is written as its own name, so a settings.json stays
    /// readable and the declaration order stays free.
    /// </summary>
    [Theory]
    [InlineData(StartupLocation.RestoreSession, "RestoreSession")]
    [InlineData(StartupLocation.HomeFolder, "HomeFolder")]
    [InlineData(StartupLocation.SpecificFolder, "SpecificFolder")]
    [InlineData(StartupLocation.Computer, "Computer")]
    public void The_choice_is_written_by_name(StartupLocation location, string expected)
    {
        Assert.Contains("\"showOnStartup\": \"" + expected + "\"", Write(location));
    }

    /// <summary>
    /// And a file already on disk naming any of them is read back as that one.
    ///
    /// **Written as literal JSON rather than as a serialize-then-deserialize
    /// round trip, because the round trip had no teeth.** Both directions
    /// share whatever the converter happens to be, so a settings file written
    /// as numbers round-trips just as happily as one written as names — and an
    /// enum member aliased onto another's value round-trips too, since
    /// comparing enums compares the numbers. Reading a fixed string is the
    /// direction that matters anyway: the file outlives the build that wrote
    /// it.
    /// </summary>
    [Theory]
    [InlineData("RestoreSession", StartupLocation.RestoreSession)]
    [InlineData("HomeFolder", StartupLocation.HomeFolder)]
    [InlineData("SpecificFolder", StartupLocation.SpecificFolder)]
    [InlineData("Computer", StartupLocation.Computer)]
    public void A_file_naming_a_choice_is_read_as_that_choice(
        string onDisk, StartupLocation expected)
    {
        var read = JsonSerializer.Deserialize(
            "{\"version\":1,\"startup\":{\"showOnStartup\":\"" + onDisk + "\"}}",
            SettingsJsonContext.Default.SettingsState);

        Assert.Equal(expected, read?.Startup.ShowOnStartup);
    }

    /// <summary>
    /// The three that shipped keep the numbers they shipped with, and the new
    /// one is past the end of them.
    ///
    /// **This is the assertion that would have caught a tidy-minded insertion**
    /// — Computer reads naturally beside HomeFolder, and putting it there
    /// compiles, passes every in-memory test, and silently redefines what a 2
    /// means in every settings.json already on disk.
    /// </summary>
    [Fact]
    public void The_members_that_shipped_keep_their_numbers()
    {
        Assert.Equal(0, (int)StartupLocation.RestoreSession);
        Assert.Equal(1, (int)StartupLocation.HomeFolder);
        Assert.Equal(2, (int)StartupLocation.SpecificFolder);
        Assert.Equal(3, (int)StartupLocation.Computer);
    }
}
