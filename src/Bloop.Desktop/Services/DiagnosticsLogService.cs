using System.Diagnostics;
using System.IO;
using Bloop.Core.Events;

namespace Bloop.Desktop.Services;

/// <summary>
/// Writes structured autonomous movement & behaviour diagnostics to Debug output,
/// %LocalAppData%\Bloop\logs\autonomy.log, and an in-memory rolling buffer for the UI.
/// </summary>
public sealed class DiagnosticsLogService
{
    private readonly string _logFilePath;
    private readonly object _sync = new();
    private readonly List<string> _recentEntries = [];

    public event EventHandler<string>? EntryAdded;

    public string LogFilePath => _logFilePath;

    public DiagnosticsLogService()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = !string.IsNullOrWhiteSpace(appData)
                ? Path.Combine(appData, "Bloop", "logs")
                : Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, "autonomy.log");
        }
        catch
        {
            var fallbackDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(fallbackDir);
            _logFilePath = Path.Combine(fallbackDir, "autonomy.log");
        }
    }

    public IReadOnlyList<string> GetRecentEntries()
    {
        lock (_sync)
        {
            return [.. _recentEntries];
        }
    }

    public void Record(AutonomyDiagnosticEventArgs e)
    {
        var ts = (e.Timestamp == default ? DateTimeOffset.UtcNow : e.Timestamp).ToLocalTime();
        var line = $"[{ts:HH:mm:ss.fff}] [{e.Stage}] {e.Summary}";
        WriteLine(line);
    }

    public void RecordCustom(string stage, string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{stage}] {message}";
        WriteLine(line);
    }

    private void WriteLine(string line)
    {
        Debug.WriteLine(line);
        lock (_sync)
        {
            _recentEntries.Add(line);
            if (_recentEntries.Count > 50)
            {
                _recentEntries.RemoveAt(0);
            }
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Ignore primary path contention
            }

            try
            {
                var localMirror = Path.Combine(AppContext.BaseDirectory, "autonomy.log");
                File.AppendAllText(localMirror, line + Environment.NewLine);
            }
            catch
            {
                // Ignore mirror path contention
            }
        }
        EntryAdded?.Invoke(this, line);
    }
}
