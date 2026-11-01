namespace FalconLoad.Core.Model;

/// <summary>
/// The tags and note a person attaches to a run, such as the build or commit it tested. Tags are what history is
/// filtered by, so they are kept short, trimmed and free of case-only duplicates. Shared by the CLI, the UI and the API
/// so they all accept and reject the same input.
/// </summary>
public static class RunLabels
{
    public const int MaxTags = 10;

    public const int MaxTagLength = 64;

    public const int MaxNoteLength = 2000;

    /// <summary>
    /// Trims the tags and drops empty ones and repeats that differ only in case, keeping the first spelling.
    /// Returns the problems found, if any; the tags are then not to be used.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string?>? tags, out IReadOnlyList<string> normalized)
    {
        var result = new List<string>();
        var problems = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = raw?.Trim();
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            if (tag.Length > MaxTagLength)
            {
                problems.Add($"A tag can be at most {MaxTagLength} characters long.");
            }
            else if (tag.Any(char.IsControl) || tag.Contains(','))
            {
                problems.Add($"The tag '{tag}' can't contain commas or control characters.");
            }
            else if (!result.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(tag);
            }
        }

        if (result.Count > MaxTags)
        {
            problems.Add($"A run can have at most {MaxTags} tags.");
        }

        normalized = problems.Count == 0 ? result : [];
        return problems.Distinct().ToList();
    }

    /// <summary>Trims the note; an empty note is null. Returns a problem when it is too long.</summary>
    public static string? NormalizeNote(string? note, out string? normalized, out string? problem)
    {
        normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        problem = normalized?.Length > MaxNoteLength ? $"A note can be at most {MaxNoteLength} characters long." : null;
        if (problem is not null)
        {
            normalized = null;
        }

        return problem;
    }

    /// <summary>True when the run's tags include every tag asked for, ignoring case. No tags asked for matches everything.</summary>
    public static bool HasAll(IReadOnlyList<string> runTags, IEnumerable<string> wanted) =>
        wanted.All(w => runTags.Contains(w.Trim(), StringComparer.OrdinalIgnoreCase));
}
