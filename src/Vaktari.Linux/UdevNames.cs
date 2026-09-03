using System.Text;

namespace Vaktari.Linux;

/// <summary>
/// Undoes the escaping udev puts on the names under <c>/dev/disk/by-*</c>.
///
/// **A stick called "MY STICK" showed up in the sidebar as "MY\x20STICK".**
/// Those directories are made of symlink NAMES, and a name cannot hold every
/// byte a label can — so udev writes anything outside its safe set as a
/// <c>\xNN</c> hex escape. Reading the link name and printing it puts the
/// escape on screen, and a space is the commonest label character there is
/// after the letters.
/// </summary>
internal static class UdevNames
{
    /// <summary>
    /// The label a person gave the volume.
    ///
    /// Bytes are collected and decoded as UTF-8 at the end rather than one at a
    /// time, because udev escapes each BYTE: an accented letter is two escapes
    /// that mean one character, and decoding them separately produces two
    /// replacement characters instead.
    ///
    /// Anything that is not a well-formed escape is left exactly as it stands.
    /// A backslash is legal in a label, and a name this does not understand is
    /// still the best answer available — an empty sidebar row would not be.
    /// </summary>
    internal static string Decode(string name)
    {
        if (!name.Contains("\\x", StringComparison.Ordinal)) return name;

        var bytes = new List<byte>(name.Length);

        for (var i = 0; i < name.Length;)
        {
            if (i + 3 < name.Length
                && name[i] == '\\' && name[i + 1] == 'x'
                && Hex(name[i + 2]) is { } high
                && Hex(name[i + 3]) is { } low)
            {
                bytes.Add((byte)((high << 4) | low));
                i += 4;
                continue;
            }

            // Not an escape: the character's own UTF-8, so the two kinds of
            // content can be decoded together at the end.
            bytes.AddRange(Encoding.UTF8.GetBytes(name[i].ToString()));
            i++;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int? Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => null,
    };
}
