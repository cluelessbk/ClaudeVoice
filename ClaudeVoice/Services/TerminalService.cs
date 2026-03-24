using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeVoice.Models;

namespace ClaudeVoice.Services;

/// <summary>
/// Manages manually linked terminal sessions. Sessions are added by the user
/// clicking "Link terminal" and then clicking the target PowerShell window.
/// Sessions are automatically removed when their process exits.
/// Badge files (claudevoice_badge_{pid}.txt) are written for all descendant
/// PIDs so Python hooks can identify which terminal they belong to.
/// </summary>
public class TerminalService : IDisposable
{
    private readonly string _claudeDir;
    private readonly string _projectsDir;
    private readonly System.Timers.Timer _monitorTimer;
    private volatile bool _disposed;
    private int _nextBadge = 1;

    public ObservableCollection<TerminalSession> Sessions { get; } = new();
    public event Action? SessionsChanged;

    public TerminalService()
    {
        _claudeDir   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _projectsDir = Path.Combine(_claudeDir, "projects");

        _monitorTimer = new System.Timers.Timer(3000);
        _monitorTimer.Elapsed += (_, _) => MonitorTick();
        _monitorTimer.AutoReset = true;
    }

    /// <summary>Starts the process-alive monitor. Call once from OnLoaded.</summary>
    public void Start() => _monitorTimer.Start();

    /// <summary>
    /// Called from the UI thread when the user clicks a terminal window during the link flow.
    /// Captures the ProcessId from the HWND, assigns a badge, and adds a new session.
    /// </summary>
    public void LinkTerminal(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);

        // Don't link the same window twice (using HWND, not PID — Windows Terminal
        // shares a single PID across all its windows, so PID check blocks the second link)
        if (Sessions.Any(s => s.WindowHandle == hwnd)) return;

        int badge          = _nextBadge++;
        string sessionId   = badge.ToString();
        string displayName = GetDisplayName(hwnd, (int)pid);

        // Must be called from UI thread — directly add to ObservableCollection
        Sessions.Add(new TerminalSession
        {
            SessionId    = sessionId,
            Badge        = badge,
            DisplayName  = displayName,
            ProcessId    = (int)pid,
            WindowHandle = hwnd,
        });

        // Write badge files immediately so hooks can start routing audio
        WriteBadgeFilesForSession(Sessions.Last());

        SessionsChanged?.Invoke();
    }

    // ── Display name ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the best display name for a linked terminal.
    /// CWD-based first (gives project folder name), but skips CWDs already claimed
    /// by another session (handles Windows Terminal shared PIDs). Falls back to window title.
    /// </summary>
    private string GetDisplayName(IntPtr hwnd, int terminalPid)
    {
        // 1. Per-process hook files (CWD-based) — gives the project folder name
        var cwd = ReadCwdFromProcessTree(terminalPid);
        if (!string.IsNullOrEmpty(cwd))
        {
            var folderName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            // Skip if another session already uses this exact name (shared PID clash)
            if (!string.IsNullOrEmpty(folderName) && !Sessions.Any(s => s.DisplayName == folderName))
                return folderName;
        }

        // 2. Window title (unique per window even with shared PIDs)
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, 512);
        return ExtractDisplayName(sb.ToString());
    }

    // ── Process tree ──────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the process tree rooted at terminalPid and reads the CWD from the first
    /// per-process hook file (claudevoice_active_{childPid}.txt) found for any descendant.
    /// Returns empty string if none found.
    /// </summary>
    private string ReadCwdFromProcessTree(int terminalPid)
    {
        if (terminalPid <= 0) return "";
        try
        {
            foreach (var childPid in GetDescendantPids(terminalPid))
            {
                var perProcFile = Path.Combine(_claudeDir, $"claudevoice_active_{childPid}.txt");
                if (!File.Exists(perProcFile)) continue;

                var cwd = File.ReadAllText(perProcFile).Trim();
                if (!string.IsNullOrEmpty(cwd)) return cwd;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Returns all process IDs that are descendants of rootPid (BFS via WMI parent map).
    /// Used to find the Claude process running inside a given terminal window.
    /// </summary>
    private static HashSet<int> GetDescendantPids(int rootPid)
    {
        var result = new HashSet<int>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId FROM Win32_Process");

            // Build parent → children map
            var children = new Dictionary<int, List<int>>();
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                var pid  = Convert.ToInt32(obj["ProcessId"]);
                var ppid = Convert.ToInt32(obj["ParentProcessId"]);
                if (pid == ppid) continue;
                if (!children.TryGetValue(ppid, out var list))
                    children[ppid] = list = new List<int>();
                list.Add(pid);
            }

            // BFS from rootPid
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!children.TryGetValue(current, out var kids)) continue;
                foreach (var kid in kids)
                {
                    result.Add(kid);
                    queue.Enqueue(kid);
                }
            }
        }
        catch { }
        return result;
    }

    // ── Display name refresh ──────────────────────────────────────────────────

    /// <summary>
    /// Re-checks the hook files and updates the session's DisplayName if a better name
    /// is now available. Called a few seconds after linking in case write_active.py
    /// hadn't fired yet at link time.
    /// Must be called from the UI thread (writes to a bound property).
    /// </summary>
    public void TryRefreshDisplayName(TerminalSession session)
    {
        if (session.WindowHandle == IntPtr.Zero) return;
        var name = GetDisplayName(session.WindowHandle, session.ProcessId ?? 0);
        if (!string.IsNullOrEmpty(name) && name != session.DisplayName)
            session.DisplayName = name;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a project path to match Claude Code's ~/.claude/projects/ directory naming.
    /// e.g. "D:\My Claude\TalkingPoint" → "D--My-Claude-TalkingPoint"
    /// </summary>
    private static string EncodeProjectPath(string path)
        => path.Replace(":", "-").Replace("\\", "-").Replace("/", "-").Replace(" ", "-");

    /// <summary>
    /// Fuzzy fallback: scan ~/.claude/projects/ for a directory whose name contains
    /// the last segment of the project path (e.g. "TalkingPoint").
    /// </summary>
    private string? FindMatchingProjectDir(string projectPath)
    {
        if (!Directory.Exists(_projectsDir)) return null;
        var lastSegment = Path.GetFileName(projectPath.TrimEnd('\\', '/'))
                              .Replace(" ", "-")
                              .ToLowerInvariant();
        if (string.IsNullOrEmpty(lastSegment)) return null;

        foreach (var dir in Directory.GetDirectories(_projectsDir))
        {
            if (Path.GetFileName(dir).Contains(lastSegment, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Tries to extract a short display name from a window title.
    /// Handles "D:\My Claude\TalkingPoint" → "TalkingPoint".
    /// Falls back to a truncated title if no path is found.
    /// </summary>
    private static string ExtractDisplayName(string windowTitle)
    {
        try
        {
            var match = Regex.Match(windowTitle, @"[A-Za-z]:\\[^\x00-\x1F]*");
            if (match.Success)
            {
                var path = match.Value.TrimEnd('\\', ' ');
                var name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(windowTitle)) return "Terminal";
        return windowTitle.Length > 35 ? windowTitle[..35] + "…" : windowTitle;
    }

    // ── Monitor tick (dead sessions + badge refresh) ─────────────────────────

    private void MonitorTick()
    {
        if (_disposed) return;

        var dead = new List<TerminalSession>();

        foreach (var s in Sessions)
        {
            // HWND check: catches tab-closed-but-process-alive (Windows Terminal shares a PID)
            if (s.WindowHandle != IntPtr.Zero && !IsWindow(s.WindowHandle))
            {
                dead.Add(s);
                continue;
            }

            if (s.ProcessId == null) continue;
            try
            {
                var proc = Process.GetProcessById(s.ProcessId.Value);
                if (proc.HasExited) { dead.Add(s); proc.Dispose(); continue; }
                proc.Dispose();
            }
            catch
            {
                dead.Add(s);
            }
        }

        // Refresh badge files for all live sessions (handles PID changes from
        // Claude Code restarts, new child processes, etc.)
        RefreshBadgeFiles(dead);

        if (dead.Count == 0) return;

        App.Current.Dispatcher.Invoke(() =>
        {
            if (_disposed) return;
            foreach (var s in dead) Sessions.Remove(s);
            SessionsChanged?.Invoke();
        });
    }

    // ── Badge file management ────────────────────────────────────────────────

    /// <summary>
    /// Writes claudevoice_badge_{pid}.txt for all descendant PIDs of a session.
    /// Called at link time for immediate availability.
    /// </summary>
    private void WriteBadgeFilesForSession(TerminalSession session)
    {
        if (session.ProcessId == null || session.ProcessId <= 0) return;
        var badge = session.Badge.ToString();
        foreach (var pid in GetDescendantPids(session.ProcessId.Value))
        {
            try { File.WriteAllText(Path.Combine(_claudeDir, $"claudevoice_badge_{pid}.txt"), badge); }
            catch { }
        }
    }

    /// <summary>
    /// Clears all badge files then rewrites them for live sessions.
    /// Runs every 3s on the monitor timer thread.
    /// </summary>
    private void RefreshBadgeFiles(List<TerminalSession> deadSessions)
    {
        try
        {
            // Clear all existing badge files
            foreach (var f in Directory.GetFiles(_claudeDir, "claudevoice_badge_*.txt"))
                try { File.Delete(f); } catch { }
        }
        catch { }

        // Write fresh badge files for all live sessions
        var deadSet = new HashSet<TerminalSession>(deadSessions);
        foreach (var s in Sessions)
        {
            if (deadSet.Contains(s)) continue;
            WriteBadgeFilesForSession(s);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _monitorTimer.Stop();
        _monitorTimer.Dispose();

        // Clean up badge files on shutdown
        try
        {
            foreach (var f in Directory.GetFiles(_claudeDir, "claudevoice_badge_*.txt"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
}
