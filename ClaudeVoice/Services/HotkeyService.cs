using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeVoice.Services;

/// <summary>
/// Registers system-wide hotkeys via Win32 RegisterHotKey for simple actions.
/// Uses WH_KEYBOARD_LL (low-level keyboard hook) for Ctrl+Space so we can
/// detect both press (start recording) and release (stop + transcribe).
/// </summary>
public class HotkeyService : IDisposable
{
    // RegisterHotKey modifiers
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual keys
    private const uint VK_T               = 0x54;
    private const uint VK_UP              = 0x26;
    private const uint VK_DOWN            = 0x28;
    private const uint VK_LEFT            = 0x25;
    private const uint VK_RIGHT           = 0x27;
    private const uint VK_X               = 0x58;
    private const uint VK_R               = 0x52;
    private const uint VK_P               = 0x50;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    // RegisterHotKey IDs (Speak removed — handled by keyboard hook below)
    private const int ID_TOGGLE_TTS    = 9002;
    private const int ID_SPEED_UP      = 9003;
    private const int ID_SPEED_DOWN    = 9004;
    private const int ID_STOP          = 9005;
    private const int ID_REREAD        = 9006;
    private const int ID_PAUSE         = 9007;
    private const int ID_NEXT_TERMINAL = 9008;
    private const int ID_PREV_TERMINAL = 9009;

    // Low-level keyboard hook constants
    private const int WH_KEYBOARD_LL  = 13;
    private const int WM_KEYDOWN      = 0x0100;
    private const int WM_KEYUP        = 0x0101;
    private const uint VK_SPACE       = 0x20;
    private const uint VK_LCONTROL    = 0xA2;
    private const uint VK_RCONTROL    = 0xA3;
    private const uint VK_CONTROL     = 0x11;
    private const uint VK_VOLUME_MUTE = 0xAD;   // keyboard media mute key (fallback for non-HID headsets)

    // Raw HID input constants — for telephony headsets like Jabra that send HID reports
    private const int    WM_INPUT              = 0x00FF;
    private const uint   RIDEV_INPUTSINK       = 0x00000100; // receive input even when not foreground
    private const uint   RID_INPUT             = 0x10000003;
    private const uint   RIDI_PREPARSEDDATA    = 0x20000005;
    private const int    RIM_TYPEHID           = 2;
    private const int    HidP_Input            = 0;
    private const int    HIDP_STATUS_SUCCESS   = 0x00110000;
    private const ushort HID_USAGE_PAGE_TELEPHONY = 0x000B;  // telephony device page (Jabra etc.)
    private const ushort HID_USAGE_PAGE_CONSUMER  = 0x000C;  // consumer controls page (simpler headsets)
    private const ushort HID_USAGE_PHONE_MUTE     = 0x002F;  // telephony: Phone Mute
    private const ushort HID_USAGE_CONSUMER_MUTE  = 0x00E2;  // consumer: Mute
    private const ushort HID_USAGE_HOOK_SWITCH     = 0x0020;  // telephony: Hook Switch (answer/end button)
    private const ushort HID_USAGE_CONSUMER_PLAY   = 0x00CD;  // consumer: Play/Pause
    private const ushort HID_USAGE_JABRA_PLAY      = 0x0080;  // consumer: Jabra Link 380 Play button

    private readonly IntPtr _hwnd;
    private HwndSource? _source;
    private readonly LowLevelKeyboardProc _hookProc;  // must be kept alive — GC will collect it otherwise
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool  _spaceDown    = false;
    private bool? _micHidMuted  = null;  // null = not yet observed; avoids firing on first HID report
    private volatile bool _submitPending  = false;
    private volatile bool _cycleEnabled   = false;

    // Events fired by RegisterHotKey
    public event Action? ToggleTtsPressed;
    public event Action? SpeedUpPressed;
    public event Action? SpeedDownPressed;
    public event Action? StopPressed;
    public event Action? ReReadPressed;
    public event Action? PauseTogglePressed;
    public event Action? NextTerminalPressed;
    public event Action? PrevTerminalPressed;

    // Events fired by the keyboard hook (hold-to-speak + media key)
    public event Action? SpeakStarted;       // Ctrl+Space pressed down
    public event Action? SpeakReleased;      // Ctrl+Space released
    public event Action? MediaSubmitPressed; // Headset Play when submit is pending
    public event Action? MediaCyclePressed;  // Headset Play when sessions with pending audio exist

    // Fired when HID Play button (0x0080) is pressed on the headset
    public event Action? HidButtonPressed;

    /// <summary>Set after a transcription lands — next Play press sends Enter.</summary>
    public void SetSubmitPending(bool pending) => _submitPending = pending;

    /// <summary>Set by MainWindow whenever any session has a 🔔 badge.</summary>
    public void SetCycleEnabled(bool enabled) => _cycleEnabled = enabled;

    /// <summary>
    /// Applies the same priority logic as the keyboard media play/pause key.
    /// Called by JabraService when the headset play/answer button fires a HookSwitch signal.
    /// </summary>
    public bool TriggerMediaPlay()
    {
        if (_submitPending)
        {
            _submitPending = false;
            MediaSubmitPressed?.Invoke();
            return true;
        }
        if (_cycleEnabled)
        {
            MediaCyclePressed?.Invoke();
            return true;
        }
        return false;
    }

    public HotkeyService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterSimpleHotkeys();

        // Install low-level keyboard hook for Ctrl+Space hold detection
        _hookProc = KeyboardHookCallback;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

        // Register for raw HID input from telephony headsets (Jabra etc.) and consumer devices.
        // RIDEV_INPUTSINK ensures we receive reports even when ClaudeVoice is not the foreground window.
        RegisterHidDevices();
    }

    private void RegisterHidDevices()
    {
        try
        {
            var rids = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = HID_USAGE_PAGE_TELEPHONY, usUsage = 0x0005, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
                new() { usUsagePage = HID_USAGE_PAGE_CONSUMER,  usUsage = 0x0001, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            };
            RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyService] HID registration failed: {ex.Message}");
        }
    }

    private void RegisterSimpleHotkeys()
    {
        RegisterHotKey(_hwnd, ID_TOGGLE_TTS,    MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_T);
        RegisterHotKey(_hwnd, ID_SPEED_UP,      MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_UP);
        RegisterHotKey(_hwnd, ID_SPEED_DOWN,    MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_DOWN);
        RegisterHotKey(_hwnd, ID_STOP,          MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_X);
        RegisterHotKey(_hwnd, ID_REREAD,        MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_R);
        RegisterHotKey(_hwnd, ID_PAUSE,         MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_P);
        RegisterHotKey(_hwnd, ID_NEXT_TERMINAL, MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_RIGHT);
        RegisterHotKey(_hwnd, ID_PREV_TERMINAL, MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_LEFT);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = (uint)Marshal.ReadInt32(lParam);
            bool isDown = wParam.ToInt32() == WM_KEYDOWN;
            bool isUp   = wParam.ToInt32() == WM_KEYUP;

            if (vkCode == VK_SPACE)
            {
                bool ctrlDown = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

                if (isDown && ctrlDown && !_spaceDown)
                {
                    _spaceDown = true;
                    SpeakStarted?.Invoke();
                    // Consume this keystroke — prevent Ctrl+Space from reaching other apps
                    return (IntPtr)1;
                }

                if (isUp && _spaceDown)
                {
                    _spaceDown = false;
                    SpeakReleased?.Invoke();
                    return (IntPtr)1;
                }
            }

            // VK_VOLUME_MUTE — fallback for headsets that fire a keyboard media key instead of HID.
            // Consumed here so the system audio mute doesn't toggle alongside our recording action.
            if (vkCode == VK_VOLUME_MUTE && isDown)
            {
                _micHidMuted = !(_micHidMuted ?? false);
                if (_micHidMuted.Value) SpeakReleased?.Invoke();
                else                    SpeakStarted?.Invoke();
                return (IntPtr)1; // consume — prevent double-fire via Windows audio endpoint mute
            }

            // Headset Play/Pause button — priority order:
            //   1. Submit pending (Enter) — always takes priority
            //   2. Pending sessions to cycle to
            //   3. Pass through to media player (Spotify etc.)
            if (vkCode == VK_MEDIA_PLAY_PAUSE && isDown)
            {
                if (_submitPending)
                {
                    _submitPending = false;
                    MediaSubmitPressed?.Invoke();
                    return (IntPtr)1;
                }
                if (_cycleEnabled)
                {
                    MediaCyclePressed?.Invoke();
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case ID_TOGGLE_TTS:    ToggleTtsPressed?.Invoke();    break;
                case ID_SPEED_UP:      SpeedUpPressed?.Invoke();      break;
                case ID_SPEED_DOWN:    SpeedDownPressed?.Invoke();    break;
                case ID_STOP:          StopPressed?.Invoke();         break;
                case ID_REREAD:        ReReadPressed?.Invoke();       break;
                case ID_PAUSE:         PauseTogglePressed?.Invoke();  break;
                case ID_NEXT_TERMINAL: NextTerminalPressed?.Invoke(); break;
                case ID_PREV_TERMINAL: PrevTerminalPressed?.Invoke(); break;
            }
            handled = true;
        }

        // Raw HID input — headset mute button (Jabra arm, etc.)
        if (msg == WM_INPUT)
            HandleRawHidInput(lParam);

        return IntPtr.Zero;
    }

    private void HandleRawHidInput(IntPtr lParam)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return;

            // Check this is a HID device (not mouse/keyboard)
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return;

            // Get preparsed data for this device to understand its HID descriptor
            uint preparsedSize = 0;
            GetRawInputDeviceInfo(header.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
            if (preparsedSize == 0) return;

            IntPtr preparsed = Marshal.AllocHGlobal((int)preparsedSize);
            try
            {
                GetRawInputDeviceInfo(header.hDevice, RIDI_PREPARSEDDATA, preparsed, ref preparsedSize);

                // HID report data sits after RAWINPUTHEADER + RAWHID's two DWORDs (dwSizeHid + dwCount)
                int    hidDataOffset = (int)headerSize + 8;
                uint   dwSizeHid     = (uint)Marshal.ReadInt32(buffer, (int)headerSize);
                uint   dwCount       = (uint)Marshal.ReadInt32(buffer, (int)headerSize + 4);
                IntPtr reportPtr     = IntPtr.Add(buffer, hidDataOffset);
                uint   reportLen     = dwSizeHid * dwCount;
                if (reportLen == 0) return;

                // Check for mute usage on both telephony and consumer pages
                CheckMuteUsage(preparsed, reportPtr, reportLen, HID_USAGE_PAGE_TELEPHONY, HID_USAGE_PHONE_MUTE);
                CheckMuteUsage(preparsed, reportPtr, reportLen, HID_USAGE_PAGE_CONSUMER,  HID_USAGE_CONSUMER_MUTE);
            }
            finally
            {
                Marshal.FreeHGlobal(preparsed);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private volatile bool _hookSwitchDown = false;

    private void CheckMuteUsage(IntPtr preparsed, IntPtr reportPtr, uint reportLen, ushort usagePage, ushort muteUsage)
    {
        var usages    = new ushort[64];
        uint usageLen = (uint)usages.Length;
        int result = HidP_GetUsages(HidP_Input, usagePage, 0, usages, ref usageLen, preparsed, reportPtr, reportLen);
        if (result != HIDP_STATUS_SUCCESS) return;

        bool mutedNow = false;
        bool buttonNow = false;
        for (int i = 0; i < usageLen; i++)
        {
            if (usages[i] == muteUsage) mutedNow = true;
            if (usages[i] == HID_USAGE_HOOK_SWITCH ||
                usages[i] == HID_USAGE_CONSUMER_PLAY ||
                usages[i] == HID_USAGE_JABRA_PLAY) buttonNow = true;
        }

        // Play button detection — fire on press (transition to true)
        if (buttonNow && !_hookSwitchDown)
        {
            _hookSwitchDown = true;
            HidButtonPressed?.Invoke();
        }
        else if (!buttonNow)
        {
            _hookSwitchDown = false;
        }

        // Mute handling (existing logic)
        // First report: initialise state silently (avoids a spurious event on app start)
        if (_micHidMuted == null) { _micHidMuted = mutedNow; return; }
        if (mutedNow == _micHidMuted) return; // no change

        _micHidMuted = mutedNow;
        if (_micHidMuted.Value) SpeakReleased?.Invoke(); // arm UP = mic muted = stop recording
        else                    SpeakStarted?.Invoke();  // arm DOWN = mic unmuted = start recording
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(uint nVirtKey);

    // Raw HID input P/Invoke
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int ReportType, ushort UsagePage, ushort LinkCollection,
        [Out] ushort[] UsageList, ref uint UsageLength, IntPtr PreparsedData, IntPtr Report, uint ReportLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint   dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint   dwType;
        public uint   dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        for (int id = ID_TOGGLE_TTS; id <= ID_PREV_TERMINAL; id++)
            UnregisterHotKey(_hwnd, id);
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
