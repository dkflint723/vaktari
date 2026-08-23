using System.Text;
using System.Text.RegularExpressions;
using Vaktari.Core.FileSystem;

namespace Vaktari.Core;

public enum RenameMode
{
    /// <summary>A pattern with # runs standing in for a counter.</summary>
    Numbered,

    /// <summary>Find and replace within the existing name.</summary>
    Replace,
}

public sealed record BatchRenameOptions
{
    public RenameMode Mode { get; init; } = RenameMode.Numbered;

    /// <summary>Runs of # become the counter, zero-padded to the run's length —
    /// the same convention Dolphin uses.</summary>
    public string Pattern { get; init; } = "";

    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool CaseSensitive { get; init; }

    /// <summary>Rename the stem only, leaving the extension alone.</summary>
    public bool KeepExtension { get; init; } = true;

    public int StartAt { get; init; } = 1;
}

/// <summary>One row of the preview. Problems are reported, never silently fixed.</summary>
public sealed record RenamePreview(
    string FullPath,
    string OldName,
    string NewName,
    string? Problem)
{
    public bool IsChanged => OldName != NewName;
    public bool IsValid => Problem is null;
}

/// <summary>
/// Works out what a batch rename would do, without doing any of it.
///
/// Separated from the renaming itself so the preview and the execution cannot
/// disagree: the UI shows exactly the list that will be applied. In Core rather
/// than the platform layer because none of it is OS-specific — only the illegal
/// character set differs, and '/' and NUL are illegal everywhere we target.
/// </summary>
public static class BatchRename
{
    /// <param name="folder">Everything in the folder, so a planned name can be
    /// checked against files that are NOT being renamed. Empty keeps the old
    /// behaviour of checking only within the batch — which was all this ever
    /// did, because the set of bystanders was declared and never filled.</param>
    public static IReadOnlyList<RenamePreview> Plan(
        IReadOnlyList<FileEntry> entries, BatchRenameOptions options,
        IReadOnlyList<FileEntry>? folder = null)
    {
        var results = new List<RenamePreview>(entries.Count);

        // **Compared the way the filesystem compares them.** Ordinal here would
        // flag renaming "readme" to "README" as a collision on Windows, where it
        // is a legitimate case-fix, and would miss a genuine "A.txt"/"a.txt"
        // clash. The executors already compare case-insensitively on Windows,
        // so an Ordinal preview disagreed with what would actually happen.
        var names = StringComparer.FromComparison(PathRules.Comparison);

        // Names being taken by this very batch, so two files cannot be planned
        // onto the same target.
        var claimed = new HashSet<string>(names);

        var selected = entries.Select(e => e.Name).ToHashSet(names);

        // Names already on disk that are NOT part of the selection: renaming
        // onto one of those would overwrite a bystander.
        //
        // **This set was declared, checked, and never filled**, so the check
        // below could never fire: renaming three files onto a name a fourth
        // file already had previewed as fine, and the rename then failed at the
        // filesystem — or, worse, on a platform that overwrites, did not.
        var untouched = new HashSet<string>(names);

        if (folder is not null)
            foreach (var entry in folder)
                if (!selected.Contains(entry.Name))
                    untouched.Add(entry.Name);

        var counter = options.StartAt;

        foreach (var entry in entries)
        {
            var stem = options.KeepExtension
                ? Path.GetFileNameWithoutExtension(entry.Name)
                : entry.Name;

            var extension = options.KeepExtension ? Path.GetExtension(entry.Name) : "";

            string renamed;

            try
            {
                renamed = options.Mode == RenameMode.Numbered
                    ? ApplyPattern(options.Pattern, counter)
                    : ApplyReplace(stem, options);
            }
            catch (RegexParseException ex)
            {
                results.Add(new RenamePreview(entry.FullPath, entry.Name, entry.Name,
                    $"pattern: {ex.Message}"));
                continue;
            }

            counter++;

            var name = renamed + extension;
            var problem = Validate(name);

            if (problem is null && !claimed.Add(name) )
                problem = "two files would get this name";

            if (problem is null && untouched.Contains(name))
                problem = "a file with this name is already here";

            results.Add(new RenamePreview(entry.FullPath, entry.Name, name, problem));
        }

        return results;
    }

    /// <summary>Runs of # become the counter; everything else is literal.</summary>
    private static string ApplyPattern(string pattern, int counter)
    {
        if (pattern.Length == 0) return pattern;

        var builder = new StringBuilder(pattern.Length + 8);
        var index = 0;

        while (index < pattern.Length)
        {
            if (pattern[index] != '#')
            {
                builder.Append(pattern[index++]);
                continue;
            }

            var run = 0;
            while (index < pattern.Length && pattern[index] == '#') { run++; index++; }

            builder.Append(counter.ToString(new string('0', run)));
        }

        return builder.ToString();
    }

    private static string ApplyReplace(string stem, BatchRenameOptions options)
    {
        if (options.Find.Length == 0) return stem;

        if (!options.UseRegex)
        {
            return stem.Replace(options.Find, options.Replace,
                options.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase);
        }

        var flags = RegexOptions.None;
        if (!options.CaseSensitive) flags |= RegexOptions.IgnoreCase;

        // Bounded so a pathological pattern cannot hang the preview, which
        // re-runs on every keystroke.
        return Regex.Replace(stem, options.Find, options.Replace, flags,
            TimeSpan.FromMilliseconds(250));
    }

    private static string? Validate(string name) => name switch
    {
        { Length: 0 } => "empty name",
        "." or ".." => "reserved name",
        _ when name.Contains('/') => "names cannot contain '/'",
        _ when name.Contains('\0') => "names cannot contain NUL",
        { Length: > 255 } => "longer than 255 characters",
        _ => null,
    };
}
