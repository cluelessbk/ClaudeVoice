using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeVoice.Services;

/// <summary>
/// Brings the linked terminal window to the foreground and types into it.
/// </summary>
public static class TerminalTypist
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    // keybd_event is used for the Alt-key trick that makes SetForegroundWindow
    // work when called from a background process
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const byte VK_MENU         = 0x12;  // Alt key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Focus the window and type <paramref name="text"/> into it.</summary>
    public static void FocusAndType(string text, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Focus(hwnd);
        SendViaPowerShell(EscapeForSendKeys(text));
    }

    /// <summary>Focus the window and press Enter.</summary>
    public static void SendEnter(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Focus(hwnd);
        // PowerShell SendKeys — SendInput with VK_RETURN doesn't reach Windows Terminal.
        SendViaPowerShell("{ENTER}");
    }

    private static void Focus(IntPtr hwnd)
    {
        // Windows normally blocks SetForegroundWindow from background processes.
        // Briefly simulating Alt down/up bypasses this.
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        // Give the window time to actually receive focus before we send keystrokes
        Thread.Sleep(150);
    }

    /// <summary>
    /// Sends a SendKeys string via PowerShell. The caller is responsible for
    /// any escaping — this writes the string directly into the SendKeys call.
    /// </summary>
    private static void SendViaPowerShell(string sendKeysStr)
    {
        // Write the SendKeys string to a temp file and Base64-encode the script
        // so no content ever appears in the PowerShell argument string.
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
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    /// <summary>
    /// SendKeys treats + ^ % ~ ( ) [ ] { } as commands; each must be wrapped in
    /// braces to be typed literally. Done in one pass — escaping in several
    /// passes re-escapes the braces added by earlier passes.
    /// </summary>
    internal static string EscapeForSendKeys(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if ("+^%~()[]{}".Contains(c)) sb.Append('{').Append(c).Append('}');
            else                          sb.Append(c);
        }
        return sb.ToString();
    }
}
