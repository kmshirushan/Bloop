using System.Diagnostics;
using System.IO;
using Bloop.Core.Interfaces;
using Bloop.Core.Productivity;

namespace Bloop.Windows.Productivity;

/// <summary>
/// Fast, cached Windows Start Menu application and recent file/folder launcher service.
/// Discovers .lnk and .appref-ms entries from user and common Start Menu folders without full-disk scans,
/// validates paths before launch, and executes via standard non-elevated ShellExecute.
/// </summary>
public sealed class WindowsLauncherCatalogService : ILauncherCatalogService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IReadOnlyList<string> _searchRoots;
    private readonly object _syncLock = new();

    private List<LauncherEntry> _cachedApplications = [];
    private Dictionary<string, LauncherEntry> _recentById = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCatalogScanUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> ExcludedShortcutKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall",
        "remove ",
        "readme",
        "release notes",
        "help",
        "documentation",
        "website",
        "license"
    };

    public event EventHandler<LauncherDiagnosticEvent>? DiagnosticLogged;

    public WindowsLauncherCatalogService(ISettingsRepository settingsRepository, IEnumerable<string>? customSearchRoots = null)
    {
        _settingsRepository = settingsRepository;

        if (customSearchRoots is not null)
        {
            _searchRoots = customSearchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        }
        else
        {
            var roots = new List<string>();
            var userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            if (!string.IsNullOrWhiteSpace(userStartMenu))
            {
                roots.Add(Path.Combine(userStartMenu, "Programs"));
            }

            var commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            if (!string.IsNullOrWhiteSpace(commonStartMenu))
            {
                roots.Add(Path.Combine(commonStartMenu, "Programs"));
            }

            _searchRoots = roots;
        }

        LoadRecentsFromStorage();
    }

    public IReadOnlyList<LauncherEntry> GetCatalogSnapshot(bool forceRefresh = false)
    {
        lock (_syncLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (forceRefresh || _cachedApplications.Count == 0 || (now - _lastCatalogScanUtc) > CacheTtl)
            {
                RefreshApplicationCatalogLocked(now);
            }

            var merged = new Dictionary<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in _cachedApplications)
            {
                if (_recentById.TryGetValue(app.Id, out var recentUsage))
                {
                    merged[app.Id] = app with
                    {
                        LastUsedUtc = recentUsage.LastUsedUtc,
                        LaunchCount = recentUsage.LaunchCount
                    };
                }
                else
                {
                    merged[app.Id] = app;
                }
            }

            foreach (var recent in _recentById.Values)
            {
                if (!merged.ContainsKey(recent.Id) && EntryTargetExists(recent))
                {
                    merged[recent.Id] = recent;
                }
            }

            return merged.Values.ToList();
        }
    }

    public IReadOnlyList<LauncherEntry> Search(string? query, int maxResults = 30)
    {
        var snapshot = GetCatalogSnapshot(forceRefresh: false);
        var results = LauncherSearchMatcher.FilterAndRank(snapshot, query, maxResults);
        LogEvent("LauncherQueryChanged", $"Query='{query ?? string.Empty}' -> {results.Count} match(es)");
        return results;
    }

    public bool TryLaunchEntry(LauncherEntry entry, out string? errorMessage)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            errorMessage = "Launcher entry path is empty.";
            LogEvent("LauncherItemLaunchFailed", errorMessage);
            return false;
        }

        if (!EntryTargetExists(entry))
        {
            errorMessage = $"Target no longer exists: {entry.TargetPath}";
            LogEvent("LauncherItemLaunchFailed", errorMessage, entry.TargetPath);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.TargetPath,
                UseShellExecute = true
            };
            _ = Process.Start(psi);

            RecordRecentLaunch(entry);
            errorMessage = null;
            LogEvent("LauncherItemLaunched", $"Launched {entry.Kind}: {entry.DisplayName}", entry.TargetPath);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not open '{entry.DisplayName}': {ex.Message}";
            LogEvent("LauncherItemLaunchFailed", errorMessage, entry.TargetPath);
            return false;
        }
    }

    public bool TryOpenFilePath(string filePath, out LauncherEntry? addedRecentEntry, out string? errorMessage)
    {
        addedRecentEntry = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = $"File does not exist: {filePath}";
            LogEvent("LauncherItemLaunchFailed", errorMessage, filePath);
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var name = Path.GetFileName(fullPath);
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        var entry = new LauncherEntry(
            Id: $"file:{fullPath.ToLowerInvariant()}",
            DisplayName: string.IsNullOrWhiteSpace(name) ? fullPath : name,
            TargetPath: fullPath,
            Kind: LauncherEntryKind.File,
            Category: "Recent File",
            Subtitle: dir,
            LastUsedUtc: DateTimeOffset.UtcNow,
            LaunchCount: 1);

        if (TryLaunchEntry(entry, out errorMessage))
        {
            addedRecentEntry = entry;
            return true;
        }

        return false;
    }

    public bool TryOpenFolderPath(string folderPath, out LauncherEntry? addedRecentEntry, out string? errorMessage)
    {
        addedRecentEntry = null;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            errorMessage = $"Folder does not exist: {folderPath}";
            LogEvent("LauncherItemLaunchFailed", errorMessage, folderPath);
            return false;
        }

        var fullPath = Path.GetFullPath(folderPath);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fullPath;
        }

        var entry = new LauncherEntry(
            Id: $"folder:{fullPath.ToLowerInvariant()}",
            DisplayName: name,
            TargetPath: fullPath,
            Kind: LauncherEntryKind.Folder,
            Category: "Recent Folder",
            Subtitle: fullPath,
            LastUsedUtc: DateTimeOffset.UtcNow,
            LaunchCount: 1);

        if (TryLaunchEntry(entry, out errorMessage))
        {
            addedRecentEntry = entry;
            return true;
        }

        return false;
    }

    public void ClearRecentItems()
    {
        lock (_syncLock)
        {
            _recentById.Clear();
            _settingsRepository.SaveRecentLauncherEntries([]);
        }
    }

    private void LoadRecentsFromStorage()
    {
        try
        {
            var stored = _settingsRepository.LoadRecentLauncherEntries();
            lock (_syncLock)
            {
                _recentById.Clear();
                foreach (var item in stored)
                {
                    if (!string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.TargetPath))
                    {
                        _recentById[item.Id] = item;
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void RecordRecentLaunch(LauncherEntry launched)
    {
        lock (_syncLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_recentById.TryGetValue(launched.Id, out var existing))
            {
                _recentById[launched.Id] = launched with
                {
                    LastUsedUtc = now,
                    LaunchCount = existing.LaunchCount + 1
                };
            }
            else
            {
                _recentById[launched.Id] = launched with
                {
                    LastUsedUtc = now,
                    LaunchCount = Math.Max(1, launched.LaunchCount)
                };
            }

            var topRecents = _recentById.Values
                .OrderByDescending(x => x.LastUsedUtc ?? DateTimeOffset.MinValue)
                .Take(25)
                .ToList();

            _settingsRepository.SaveRecentLauncherEntries(topRecents);
        }
    }

    private void RefreshApplicationCatalogLocked(DateTimeOffset nowUtc)
    {
        var byDisplayName = new Dictionary<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".appref-ms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var displayName = Path.GetFileNameWithoutExtension(file)?.Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                if (ShouldSkipShortcut(displayName))
                {
                    continue;
                }

                if (byDisplayName.ContainsKey(displayName))
                {
                    continue;
                }

                var parentDirName = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                var category = string.IsNullOrWhiteSpace(parentDirName) ||
                               string.Equals(parentDirName, "Programs", StringComparison.OrdinalIgnoreCase)
                    ? "Application"
                    : parentDirName;

                var id = $"app:{displayName.ToLowerInvariant()}";
                byDisplayName[displayName] = new LauncherEntry(
                    Id: id,
                    DisplayName: displayName,
                    TargetPath: file,
                    Kind: LauncherEntryKind.Application,
                    Category: category,
                    Subtitle: category);
            }
        }

        _cachedApplications = byDisplayName.Values
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _lastCatalogScanUtc = nowUtc;

        LogEvent("LauncherCatalogLoaded", $"Discovered {_cachedApplications.Count} Start Menu application entries");
    }

    private static bool ShouldSkipShortcut(string displayName)
    {
        foreach (var keyword in ExcludedShortcutKeywords)
        {
            if (displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool EntryTargetExists(LauncherEntry entry)
    {
        return entry.Kind == LauncherEntryKind.Folder
            ? Directory.Exists(entry.TargetPath)
            : File.Exists(entry.TargetPath);
    }

    private void LogEvent(string eventName, string summary, string? targetPath = null)
    {
        DiagnosticLogged?.Invoke(
            this,
            new LauncherDiagnosticEvent(eventName, summary, targetPath, DateTimeOffset.UtcNow));
    }
}
