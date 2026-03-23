using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeVoice.Services;

/// <summary>
/// Finds the Claude terminal window and types text into it,
/// regardless of which window is currently in the foreground.
/// Ports the find_claude_terminal + focus_and_type logic from stt_listen.py.
/// </summary>
public static class TerminalTypist
{
    // ── Win32 imports ─────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hwnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll")] private static extern int  GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    // keybd_event is used for the Alt-key trick that makes SetForegroundWindow
    // work when called from a background process (same trick as stt_listen.py)
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const byte  VK_MENU          = 0x12;  // Alt key
    private const uint  KEYEVENTF_KEYUP  = 0x0002;

    // SendInput is used to inject Enter directly into the focused window's input queue —
    // faster and more reliable than spawning a PowerShell process for a single keypress
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort   wVk;
        public ushort   wScan;
        public uint     dwFlags;
        public uint     time;
        public UIntPtr  dwExtraInfo;
    }

    private const uint   INPUT_KEYBOARD     = 1;
    private const ushort VK_RETURN          = 0x0D;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Find the Claude terminal window, bring it to the foreground,
    /// and type <paramref name="text"/> into it.
    /// If <paramref name="processId"/> is provided, targets that specific claude.exe process
    /// rather than searching all Claude windows.
    /// </summary>
    public static void FocusAndType(string text, int? processId = null)
    {
        var hwnd = processId.HasValue
            ? FindWindowInProcessTree(processId.Value, 0)
            : FindClaudeTerminalHwnd();

        // If specific PID gave no window, fall back to any Claude window
        if (hwnd == IntPtr.Zero && processId.HasValue)
            hwnd = FindClaudeTerminalHwnd();
        if (hwnd == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[TerminalTypist] Claude terminal window not found.");
            return;
        }

        // Alt-key trick: Windows normally blocks SetForegroundWindow from
        // background processes. Briefly simulating Alt down/up bypasses this.
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        // Give the window time to actually receive focus before we send keystrokes
        Thread.Sleep(150);

        SendTextViaPowerShell(text);
    }

    // ── Find the window ───────────────────────────────────────────────────────

    private static IntPtr FindClaudeTerminalHwnd()
    {
        foreach (var proc in Process.GetProcessesByName("claude"))
        {
            using (proc)
            {
                var hwnd = FindWindowInProcessTree(proc.Id, depth: 0);
                if (hwnd != IntPtr.Zero) return hwnd;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindWindowInProcessTree(int pid, int depth)
    {
        if (depth > 5) return IntPtr.Zero;

        var windows = GetVisibleWindowsForPid(pid);
        if (windows.Count > 0)
        {
            // Prefer a window whose title contains "claude" (the active tab)
            var preferred = windows.FirstOrDefault(h =>
                GetTitle(h).Contains("claude", StringComparison.OrdinalIgnoreCase));
            return preferred != IntPtr.Zero ? preferred : windows[0];
        }

        // No window found at this level — walk up to the parent process
        int parentPid = GetParentProcessId(pid);
        if (parentPid > 0 && parentPid != pid)
            return FindWindowInProcessTree(parentPid, depth + 1);

        return IntPtr.Zero;
    }

    private static List<IntPtr> GetVisibleWindowsForPid(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out int wpid);
            if (wpid == pid && GetWindowTextLength(hwnd) > 0)
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int GetParentProcessId(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject obj in searcher.Get())
                return Convert.ToInt32(obj["ParentProcessId"]);
        }
        catch { }
        return -1;
    }

    // ── HWND-based overloads ────────────────────────────────────────────────

    /// <summary>
    /// Focus the given window handle and type text into it.
    /// Preferred over the PID-based overload because it targets the exact window
    /// (Windows Terminal shares one PID across all its windows).
    /// </summary>
    public static void FocusAndType(string text, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) { FocusAndType(text, (int?)null); return; }

        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(150);
        SendTextViaPowerShell(text);
    }

    /// <summary>
    /// Focus the given window handle and press Enter.
    /// </summary>
    public static void SendEnter(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) { SendEnter((int?)null); return; }

        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(150);
        SendViaPowerShell("{ENTER}");
    }

    // ── SendEnter (PID-based, fallback) ──────────────────────────────────────

    /// <summary>
    /// Focus the Claude terminal and press Enter — used by the headset Play button to submit.
    /// </summary>
    public static void SendEnter(int? processId = null)
    {
        var hwnd = processId.HasValue
            ? FindWindowInProcessTree(processId.Value, 0)
            : FindClaudeTerminalHwnd();
        if (hwnd == IntPtr.Zero && processId.HasValue)
            hwnd = FindClaudeTerminalHwnd();
        if (hwnd == IntPtr.Zero) return;

        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        Thread.Sleep(150);

        // Use PowerShell SendKeys — SendInput with VK_RETURN doesn't reach Windows Terminal.
        SendViaPowerShell("{ENTER}");
    }

    // ── Type into the focused window ──────────────────────────────────────────

    private static void SendTextViaPowerShell(string text)
        => SendViaPowerShell(EscapeForSendKeys(text));

    /// <summary>
    /// Sends a SendKeys string via PowerShell. The caller is responsible for
    /// any escaping — this writes the string directly into the SendKeys call.
    /// </summary>
    private static void SendViaPowerShell(string sendKeysStr)
    {
        // Write the SendKeys string to a temp file.
        // Build the PS script as a string and Base64-encode it so no
        // content ever appears in the PowerShell argument string.
        var tmpFile = Path.Combine(Path.GetTempPath(), $"cv_type_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tmpFile, sendKeysStr, Encoding.UTF8);

            var script  = $"Add-Type -AssemblyName System.Windows.Forms; " +
                          $"[System.Windows.Forms.SendKeys]::SendWait(" +
                          $"[System.IO.File]::ReadAllText('{tmpFile}', [System.Text.Encoding]::UTF8))";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName        = "powershell",
                Arguments       = $"-NoProfile -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            try { File.Delete(tmpFile); } catch { }
        }
        catch
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    private static string EscapeForSendKeys(string text)
        => text.Replace("+", "{+}").Replace("^", "{^}").Replace("%", "{%}")
               .Replace("~", "{~}").Replace("(", "{(}").Replace(")", "{)}")
               .Replace("[", "{[}").Replace("]", "{]}")
               .Replace("{", "{{").Replace("}", "}}");
}
