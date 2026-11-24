using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ClaudeVoice.Services;

/// <summary>
/// Talks to the Claude desktop app's window through Windows UI Automation
/// (the accessibility interface screen readers use).
///
/// The window exposes:
///   - a document named "&lt;session title&gt; - Claude Code" for the session on screen
///   - an edit box named "Prompt" (the message box)
///   - a button named "Send" (also queues a message while Claude is working)
///
/// Setting the box's value and invoking Send works without bringing the window
/// to the front (verified 2026-11-24, Claude app 6.28899).
/// </summary>
public static class DesktopApp
{
    private const string TitleSuffix = " - Claude Code";

    /// <summary>True if the window belongs to the Claude desktop app.</summary>
    public static bool IsClaudeWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }

        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() == "Chrome_WidgetWin_1";
    }

    /// <summary>
    /// Title of the Code session currently on screen, or null when the window
    /// shows something else (a Chat, settings, …).
    /// </summary>
    public static string? VisibleSessionTitle(IntPtr hwnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            var docs = window.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Document));
            foreach (AutomationElement doc in docs)
            {
                var name = doc.Current.Name;
                if (name.EndsWith(TitleSuffix))
                    return name[..^TitleSuffix.Length].Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Adds text to the message box, after anything already typed there.
    /// Returns null on success, or a short reason.
    /// </summary>
    public static string? AppendToPrompt(IntPtr hwnd, string text)
    {
        try
        {
            var box = FindPrompt(hwnd);
            if (box == null) return "Couldn't find the Claude message box";

            var vp      = (ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern);
            var current = vp.Current.Value.Trim();   // an empty box reports "\n"
            vp.SetValue(current.Length == 0 ? text : current + " " + text);
            return null;
        }
        catch (Exception ex) { return $"Couldn't type into Claude: {ex.Message}"; }
    }

    /// <summary>
    /// Presses Send. Returns null on success, or a short reason
    /// (e.g. nothing to send).
    /// </summary>
    public static string? ClickSend(IntPtr hwnd)
    {
        try
        {
            var box = FindPrompt(hwnd);
            if (box != null &&
                ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).Current.Value.Trim().Length == 0)
                return "Nothing to send";

            var window = AutomationElement.FromHandle(hwnd);
            var send = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Send")));
            if (send == null) return "Couldn't find Claude's Send button";

            ((InvokePattern)send.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return null;
        }
        catch (Exception ex) { return $"Couldn't send: {ex.Message}"; }
    }

    private static AutomationElement? FindPrompt(IntPtr hwnd)
    {
        var window = AutomationElement.FromHandle(hwnd);
        return window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.NameProperty, "Prompt")));
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
