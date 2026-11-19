namespace Bloop.Core.Productivity;

public enum LauncherEntryKind
{
    Application = 0,
    File = 1,
    Folder = 2
}

public sealed record LauncherEntry(
    string Id,
    string DisplayName,
    string TargetPath,
    LauncherEntryKind Kind,
    string Category = "Application",
    string? Subtitle = null,
    DateTimeOffset? LastUsedUtc = null,
    int LaunchCount = 0
);

public sealed record LauncherDiagnosticEvent(
    string EventName,
    string Summary,
    string? TargetPath = null,
    DateTimeOffset TimestampUtc = default
);

public interface ILauncherCatalogService
{
    event EventHandler<LauncherDiagnosticEvent>? DiagnosticLogged;

    IReadOnlyList<LauncherEntry> GetCatalogSnapshot(bool forceRefresh = false);
    IReadOnlyList<LauncherEntry> Search(string? query, int maxResults = 30);
    bool TryLaunchEntry(LauncherEntry entry, out string? errorMessage);
    bool TryOpenFilePath(string filePath, out LauncherEntry? addedRecentEntry, out string? errorMessage);
    bool TryOpenFolderPath(string folderPath, out LauncherEntry? addedRecentEntry, out string? errorMessage);
    void ClearRecentItems();
}

/// <summary>
/// Fast, deterministic search and ranking matcher for Quick Launcher entries.
/// Prioritizes exact prefix matches, word-boundary/acronym matches, recent usage, and substring matches.
/// </summary>
public static class LauncherSearchMatcher
{
    public static IReadOnlyList<LauncherEntry> FilterAndRank(
        IEnumerable<LauncherEntry> catalog,
        string? rawQuery,
        int maxResults = 30)
    {
        var query = rawQuery?.Trim() ?? string.Empty;
        int limit = Math.Clamp(maxResults, 1, 200);

        if (string.IsNullOrEmpty(query))
        {
            return catalog
                .OrderByDescending(e => e.LastUsedUtc.HasValue)
                .ThenByDescending(e => e.LastUsedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.LaunchCount)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        var scored = new List<(LauncherEntry Entry, int Score)>();
        foreach (var entry in catalog)
        {
            int score = ScoreMatch(entry, query);
            if (score > 0)
            {
                scored.Add((entry, score));
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.LastUsedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    public static int ScoreMatch(LauncherEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            return 0;
        }

        var name = entry.DisplayName.Trim();
        var q = query.Trim();

        int baseScore = 0;

        if (string.Equals(name, q, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = 1000;
        }
        else if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = 850 - Math.Min(100, name.Length - q.Length);
        }
        else if (MatchesWordBoundaryOrAcronym(name, q))
        {
            baseScore = 700;
        }
        else if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = 500 - Math.Min(100, name.IndexOf(q, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(entry.Category) &&
                 entry.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = 300;
        }
        else if (!string.IsNullOrWhiteSpace(entry.Subtitle) &&
                 entry.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = 250;
        }

        if (baseScore <= 0)
        {
            return 0;
        }

        if (entry.LastUsedUtc.HasValue)
        {
            baseScore += 60 + Math.Min(40, entry.LaunchCount * 5);
        }

        return baseScore;
    }

    private static bool MatchesWordBoundaryOrAcronym(string displayName, string query)
    {
        var words = displayName.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        // 1. Word prefix match
        foreach (var word in words)
        {
            if (word.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Acronym match (e.g., "vsc" matches "Visual Studio Code")
        if (query.Length >= 2 && query.Length <= words.Length)
        {
            bool acronymMatch = true;
            for (int i = 0; i < query.Length; i++)
            {
                if (char.ToUpperInvariant(words[i][0]) != char.ToUpperInvariant(query[i]))
                {
                    acronymMatch = false;
                    break;
                }
            }

            if (acronymMatch)
            {
                return true;
            }
        }

        return false;
    }
}
