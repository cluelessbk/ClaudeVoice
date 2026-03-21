using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeVoice.Models;

namespace ClaudeVoice.Services;

/// <summary>
/// Manages manually linked terminal sessions. Sessions are added by the user
/// clicking "Link terminal" and then clicking the target PowerShell window.
/// Sessions are automatically removed when their process exits.
/// </summary>
public class TerminalService : IDisposable
{
    private readonly string _claudeDir;
    private readonly string _projectsDir;
    private readonly System.Timers.Timer _monitorTimer;
    private volatile bool _disposed;

    public ObservableCollection<TerminalSession> Sessions { get; } = new();
    public event Action? SessionsChanged;

    public TerminalService()
    {
        _claudeDir   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _projectsDir = Path.Combine(_claudeDir, "projects");

        _monitorTimer = new System.Timers.Timer(3000);
        _monitorTimer.Elapsed += (_, _) => CheckDeadSessions();
        _monitorTimer.AutoReset = true;
    }

    /// <summary>Starts the process-alive monitor. Call once from OnLoaded.</summary>
    public void Start() => _monitorTimer.Start();

    /// <summary>
    /// Called from the UI thread when the user clicks a terminal window during the link flow.
    /// Captures the ProcessId from the HWND, reads the project path from the hook-written file,
    /// and adds a new session to the list.
    /// </summary>
    public void LinkTerminal(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);

        // Don't link the same window twice (using HWND, not PID — Windows Terminal
        // shares a single PID across all its windows, so PID check blocks the second link)
        if (Sessions.Any(s => s.WindowHandle == hwnd)) return;

        // Walk the terminal's process tree to find the right per-process hook file.
        // This prevents cross-contamination when multiple Claude sessions are running.
        string transcriptPath = FindTranscriptPath((int)pid);
        string displayName    = GetDisplayName(hwnd, (int)pid);

        // Session ID: derived from transcript if available, otherwise random (TTS routing deferred)
        string sessionId = string.IsNullOrEmpty(transcriptPath)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()
            : ComputeSessionId(transcriptPath);

        // Must be called from UI thread — directly add to ObservableCollection
        Sessions.Add(new TerminalSession
        {
            SessionId      = sessionId,
            TranscriptPath = transcriptPath,
            DisplayName    = displayName,
            ProcessId      = (int)pid,
            WindowHandle   = hwnd,
        });
        SessionsChanged?.Invoke();
    }

    // ── Display name ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the best display name for a linked terminal.
    /// Checks per-process hook files first (avoids cross-session contamination),
    /// then falls back to the shared file, then the window title.
    /// </summary>
    private string GetDisplayName(IntPtr hwnd, int terminalPid)
    {
        // 1. Per-process files — correct even with multiple simultaneous sessions
        var cwd = ReadCwdFromProcessTree(terminalPid);
        if (!string.IsNullOrEmpty(cwd))
        {
            var folderName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(folderName)) return folderName;
        }

        // 2. Shared fallback file (no staleness guard — folder name stays valid)
        try
        {
            var sharedFile = Path.Combine(_claudeDir, "claudevoice_active.txt");
            if (File.Exists(sharedFile))
            {
                var projectPath = File.ReadAllText(sharedFile).Trim();
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var folderName = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(folderName)) return folderName;
                }
            }
        }
        catch { }

        // 3. Parse the window title
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, 512);
        return ExtractDisplayName(sb.ToString());
    }

    // ── Transcript path ───────────────────────────────────────────────────────

    /// <summary>
    /// Finds the transcript path for the Claude session running inside the given terminal.
    /// Walks the terminal's process tree and checks per-process hook files written by
    /// write_active.py (claudevoice_active_{pid}.txt). Falls back to the shared file.
    /// </summary>
    private string FindTranscriptPath(int terminalPid)
    {
        // 1. Per-process files — immune to cross-session overwriting
        var cwd = ReadCwdFromProcessTree(terminalPid);
        if (!string.IsNullOrEmpty(cwd))
        {
            var transcript = FindTranscriptForCwd(cwd);
            if (!string.IsNullOrEmpty(transcript)) return transcript;
        }

        // 2. Shared fallback with staleness guard (only trust if written recently)
        try
        {
            var sharedFile = Path.Combine(_claudeDir, "claudevoice_active.txt");
            if (!File.Exists(sharedFile)) return "";
            if ((DateTime.Now - File.GetLastWriteTime(sharedFile)).TotalMinutes > 5) return "";

            var projectPath = File.ReadAllText(sharedFile).Trim();
            return FindTranscriptForCwd(projectPath);
        }
        catch { return ""; }
    }

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
    /// Given a project CWD path, finds the most recently modified .jsonl transcript
    /// in the corresponding ~/.claude/projects/ directory.
    /// </summary>
    private string FindTranscriptForCwd(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return "";
        try
        {
            var encoded    = EncodeProjectPath(projectPath);
            var projectDir = Path.Combine(_projectsDir, encoded);

            if (!Directory.Exists(projectDir))
                projectDir = FindMatchingProjectDir(projectPath) ?? "";

            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return "";

            var jsonls = Directory.GetFiles(projectDir, "*.jsonl")
                                  .OrderByDescending(File.GetLastWriteTime)
                                  .ToArray();
            return jsonls.Length > 0 ? jsonls[0] : "";
        }
        catch { return ""; }
    }

    // ── Process tree ──────────────────────────────────────────────────────────

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

    // ── Dead session monitor ──────────────────────────────────────────────────

    private void CheckDeadSessions()
    {
        if (_disposed) return;

        var dead = new List<TerminalSession>();
        foreach (var s in Sessions)
        {
            if (s.ProcessId == null) continue;
            try
            {
                var proc = Process.GetProcessById(s.ProcessId.Value);
                if (proc.HasExited) dead.Add(s);
                proc.Dispose();
            }
            catch
            {
                dead.Add(s); // GetProcessById throws if process not found = already dead
            }
        }

        if (dead.Count == 0) return;

        App.Current.Dispatcher.Invoke(() =>
        {
            if (_disposed) return;
            foreach (var s in dead) Sessions.Remove(s);
            SessionsChanged?.Invoke();
        });
    }

    // ── Public helpers ────────────────────────────────────────────────────────

    // Reused by TtsService and MainWindow for consistent session ID computation.
    // Must match the formula used when audio queue files are named.
    public static string ComputeSessionId(string transcriptPath)
        => Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(transcriptPath)))[..8].ToLowerInvariant();

    public void Dispose()
    {
        _disposed = true;
        _monitorTimer.Stop();
        _monitorTimer.Dispose();
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
