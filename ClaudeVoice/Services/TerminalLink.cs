using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ClaudeVoice.Services;

/// <summary>
/// Holds the single terminal ClaudeVoice is linked to and writes its process ID
/// to ~/.claude/claudevoice_link.txt. The Python hooks speak only when that
/// process is one of their ancestors.
///
/// Windows Terminal runs every tab and window in ONE process, so linking its PID
/// would link every tab. Instead we find the tab that was active when the user
/// clicked: each tab has its own (hidden) console whose title Claude Code sets,
/// and the window title always equals the active tab's console title. The tab's
/// shell process (a direct child of WindowsTerminal.exe) becomes the link.
/// </summary>
public class TerminalLink : IDisposable
{
    private readonly string _linkFile;
    private readonly DispatcherTimer _monitor;
    private static readonly object ConsoleLock = new();

    private readonly string _claudeDir;
    private readonly TranscriptReader _reader;

    public IntPtr WindowHandle { get; private set; }
    public int    ProcessId    { get; private set; }   // tab shell (Windows Terminal) or window process
    public bool   IsTab        { get; private set; }   // true = a Windows Terminal tab
    public bool   IsDesktop    { get; private set; }   // true = a Claude desktop-app Code session
    public string SessionTitle { get; private set; } = "";   // desktop only
    public string DisplayName  { get; private set; } = "";
    public bool   IsLinked     => WindowHandle != IntPtr.Zero;

    // Fired on the UI thread when the link or its display name changes
    public event Action? Changed;

    public TerminalLink(string claudeDir)
    {
        _claudeDir = claudeDir;
        _reader    = new TranscriptReader(claudeDir);
        _linkFile  = Path.Combine(claudeDir, "claudevoice_link.txt");
        DeleteLinkFile();   // a link never survives an app restart

        _monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _monitor.Tick += (_, _) => MonitorTick();
        _monitor.Start();
    }

    /// <summary>
    /// Links the given window (for Windows Terminal: its active tab), replacing
    /// any previous link. Returns null on success, or a message explaining why not.
    /// </summary>
    public string? Link(IntPtr hwnd)
    {
        if (DesktopApp.IsClaudeWindow(hwnd)) return LinkDesktop(hwnd);

        GetWindowThreadProcessId(hwnd, out uint windowPid);

        int    pid   = (int)windowPid;
        bool   isTab = false;
        string name;

        if (IsWindowsTerminal(pid))
        {
            var windowTitle = Normalize(GetWindowTitle(hwnd));
            var matches = new List<(int Pid, string Title)>();
            foreach (var child in ProcessTree.ChildrenOf(pid))
            {
                var title = GetConsoleTitle(child);
                if (title.Length > 0 && Normalize(title) == windowTitle)
                    matches.Add((child, title));
            }

            if (matches.Count == 0)
                return "Couldn't identify the tab — click inside it and try again";
            if (matches.Count > 1)
                return "Two tabs have the same title — send a message in this one first, then link";

            (pid, name) = matches[0];
            isTab = true;
        }
        else
        {
            name = GetWindowTitle(hwnd);
        }

        try { File.WriteAllText(_linkFile, pid.ToString()); }
        catch (Exception ex) { return $"Couldn't write link file: {ex.Message}"; }

        _reader.Stop();
        WindowHandle = hwnd;
        ProcessId    = pid;
        IsTab        = isTab;
        IsDesktop    = false;
        SessionTitle = "";
        DisplayName  = ShortName(name);
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Claude desktop app: hooks don't run there, so instead of writing a PID we
    /// follow the transcript of the Code session on screen (matched by its title).
    /// </summary>
    private string? LinkDesktop(IntPtr hwnd)
    {
        var title = DesktopApp.VisibleSessionTitle(hwnd);
        if (string.IsNullOrEmpty(title))
            return "Open a Code session in the Claude app, then link";

        var path = TranscriptReader.FindTranscript(_claudeDir, title);
        if (path == null)
            return "Couldn't find this session's transcript — send it a message first, then link";

        DeleteLinkFile();   // no PID: terminal hooks stay silent while a desktop session is linked
        _reader.Start(path);

        GetWindowThreadProcessId(hwnd, out uint windowPid);
        WindowHandle = hwnd;
        ProcessId    = (int)windowPid;
        IsTab        = false;
        IsDesktop    = true;
        SessionTitle = title;
        DisplayName  = ShortName(title);
        Changed?.Invoke();
        return null;
    }

    public void Unlink()
    {
        DeleteLinkFile();
        _reader.Stop();
        WindowHandle = IntPtr.Zero;
        ProcessId    = 0;
        IsTab        = false;
        IsDesktop    = false;
        SessionTitle = "";
        DisplayName  = "";
        Changed?.Invoke();
    }

    /// <summary>
    /// True if keystrokes sent to the window would land in the linked terminal —
    /// for a Windows Terminal tab, only when that tab is the one showing; for the
    /// Claude app, only when the linked session is the one on screen.
    /// </summary>
    public bool IsLinkedTabActive()
    {
        if (!IsLinked) return false;
        if (IsDesktop) return DesktopApp.VisibleSessionTitle(WindowHandle) == SessionTitle;
        if (!IsTab)    return true;
        return Normalize(GetWindowTitle(WindowHandle)) == Normalize(GetConsoleTitle(ProcessId));
    }

    private void MonitorTick()
    {
        if (!IsLinked) return;

        if (!IsWindow(WindowHandle) || !IsProcessAlive(ProcessId)) { Unlink(); return; }
        if (IsDesktop) return;   // name is the session title; the transcript keeps being followed

        // Titles change (Claude Code names the conversation) — keep the name current
        var title = IsTab ? GetConsoleTitle(ProcessId) : GetWindowTitle(WindowHandle);
        var name  = ShortName(title);
        if (name != DisplayName) { DisplayName = name; Changed?.Invoke(); }
    }

    private void DeleteLinkFile()
    {
        try { File.Delete(_linkFile); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsWindowsTerminal(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Reads the console title of another process by briefly attaching to its
    /// console. ClaudeVoice is a GUI app with no console of its own, so this is safe.
    /// </summary>
    private static string GetConsoleTitle(int pid)
    {
        lock (ConsoleLock)
        {
            if (!AttachConsole((uint)pid)) return "";
            try
            {
                var sb = new StringBuilder(512);
                GetConsoleTitleW(sb, (uint)sb.Capacity);
                return sb.ToString();
            }
            finally { FreeConsole(); }
        }
    }

    /// <summary>Drops Claude Code's animated status glyph (✳ ◑ ·…) at the start.</summary>
    private static string Normalize(string title)
        => Regex.Replace(title, @"^[^\p{L}\p{N}]+", "").Trim();

    /// <summary>
    /// Short name: "D:\My Claude\TalkingPoint" → "TalkingPoint", otherwise the
    /// title without its status glyph, truncated.
    /// </summary>
    private static string ShortName(string title)
    {
        title = Normalize(title);

        var match = Regex.Match(title, @"[A-Za-z]:\\[^\x00-\x1F]*");
        if (match.Success)
        {
            var path = match.Value.TrimEnd('\\', ' ');
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        if (title.Length == 0) return "Terminal";
        return title.Length > 30 ? title[..30] + "…" : title;
    }

    public void Dispose()
    {
        _monitor.Stop();
        _reader.Dispose();
        DeleteLinkFile();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetConsoleTitleW(StringBuilder lpConsoleTitle, uint nSize);
}

/// <summary>Lists child processes via a Toolhelp32 snapshot (fast; no WMI).</summary>
internal static class ProcessTree
{
    public static List<int> ChildrenOf(int parentPid)
    {
        var result = new List<int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
                if (pe.th32ParentProcessID == parentPid && pe.th32ProcessID != parentPid)
                    result.Add((int)pe.th32ProcessID);
        }
        finally { CloseHandle(snap); }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint   dwSize;
        public uint   cntUsage;
        public uint   th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint   th32ModuleID;
        public uint   cntThreads;
        public uint   th32ParentProcessID;
        public int    pcPriClassBase;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
}
