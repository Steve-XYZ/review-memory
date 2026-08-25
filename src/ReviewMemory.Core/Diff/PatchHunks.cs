using System.Text.RegularExpressions;

namespace ReviewMemory.Core.Diff;

public static partial class PatchHunks
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled)]
    private static partial Regex HunkHeader();

    /// <summary>
    /// Splits a file's unified patch into individual hunks.
    /// Returns an empty list when the patch is null or contains no hunks.
    /// </summary>
    public static IReadOnlyList<CodeHunk> Parse(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        var hunks = new List<CodeHunk>();
        var current = new List<string>();
        int? oldStart = null, oldLines = null, newStart = null, newLines = null;

        foreach (var line in patch.Split('\n'))
        {
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                if (oldStart is not null)
                {
                    hunks.Add(Build(current, oldStart.GetValueOrDefault(), oldLines.GetValueOrDefault(),
                        newStart.GetValueOrDefault(), newLines.GetValueOrDefault()));
                    current.Clear();
                }

                oldStart = int.Parse(match.Groups[1].Value);
                oldLines = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                newStart = int.Parse(match.Groups[3].Value);
                newLines = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                current.Add(line);
            }
            else if (oldStart is not null)
            {
                current.Add(line);
            }
        }

        if (oldStart is not null)
        {
            hunks.Add(Build(current, oldStart.GetValueOrDefault(), oldLines.GetValueOrDefault(),
                newStart.GetValueOrDefault(), newLines.GetValueOrDefault()));
        }

        return hunks;
    }

    private static CodeHunk Build(List<string> lines, int oldStart, int oldLines, int newStart, int newLines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return new CodeHunk(oldStart, oldLines, newStart, newLines, string.Join('\n', lines));
    }
}
