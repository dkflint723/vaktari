namespace Vaktari.Benchmarks;

/// <summary>
/// Names shaped like the ones a folder holds, from a fixed seed so two runs
/// sort the same input: camera files with zero-padded counters, documents
/// with spaces and bare numbers, chapters, a few accented words, and plain
/// files — the mix that makes the natural sort's digit runs and accent rule
/// both do work, rather than a list that any comparison walks in one pass.
/// </summary>
public static class Names
{
    public static string[] Realistic(int count, int seed = 42)
    {
        var random = new Random(seed);
        var names = new string[count];

        for (var i = 0; i < count; i++)
        {
            names[i] = random.Next(6) switch
            {
                0 => $"IMG_{random.Next(1, 99_999):D4}.jpg",
                1 => $"Report {random.Next(1, 500)}.docx",
                2 => $"chapter{random.Next(1, 120)}.md",
                3 => $"Été {random.Next(1, 40)} été.txt",
                4 => $"file{random.Next(1, 1_000_000)}",
                _ => $"{random.Next(1, 30)}. Track {random.Next(1, 30)}.flac",
            };
        }

        return names;
    }
}
