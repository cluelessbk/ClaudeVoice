using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVoice.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClaudeVoice;

public partial class MainWindow : Window
{
    private readonly FileWatcherService _fw;
    private readonly TtsService _tts;
    private readonly SttService _stt;
    private readonly TerminalLink _link;
    private HotkeyService?  _hotkeys;
    private JabraService?   _jabra;

    private int  _ttsRate    = 0;
    private bool _ttsEnabled = true;

    public MainWindow()
    {
        InitializeComponent();

        _fw   = new FileWatcherService();
        _tts  = new TtsService(_fw);
        _stt  = new SttService();
        _link = new TerminalLink(_fw.ClaudeDir);

        _fw.TtsEnabledChanged += enabled => Dispatcher.Invoke(() => SyncTtsToggle(enabled));
        _fw.TtsRateChanged    += rate    => Dispatcher.Invoke(() => SyncRate(rate));

        _link.Changed += UpdateLinkStatus;

        // STT: transcribed text is typed into the linked terminal
        _stt.StateChanged += state => Dispatcher.Invoke(() => UpdateSpeakButton(state));
        _stt.Transcribed  += text => Dispatcher.Invoke(() =>
        {
            if (!CanTypeIntoLink(text)) return;
            var hwnd = _link.WindowHandle;
            Task.Run(() =>
            {
                TerminalTypist.FocusAndType(text, hwnd);
                PlayTranscriptionDing();
            });
            _hotkeys?.SetSubmitPending(true);
        });

        // Headset mic mute → start/stop recording (same as Ctrl+Space but hands-free).
        // Callback fires on a COM thread — dispatch to UI thread for WaveIn safety.
        _stt.MicUnmuted += () => Dispatcher.Invoke(StartSpeak);
        _stt.MicMuted   += () => Dispatcher.InvokeAsync(StopAndTranscribe);

        // TTS errors are shown until the next message plays successfully
        _tts.TtsErrorOccurred += msg => Dispatcher.Invoke(() => StatusMessage.Text = msg);
        _tts.PlayingChanged   += playing =>
        {
            if (playing) Dispatcher.Invoke(() => StatusMessage.Text = "");
        };

        _ttsEnabled = _fw.ReadTtsEnabled();
        _ttsRate    = _fw.ReadTtsRate();
        SyncTtsToggle(_ttsEnabled);
        SyncRate(_ttsRate);
        UpdateLinkStatus();

        RestorePosition();
        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyService(this);

        _hotkeys.SpeakStarted     += () => Dispatcher.Invoke(StartSpeak);
        _hotkeys.SpeakReleased    += () => Dispatcher.InvokeAsync(StopAndTranscribe);
        _hotkeys.ToggleTtsPressed += () => Dispatcher.Invoke(() => SetTts(!_ttsEnabled));
        _hotkeys.SpeedUpPressed   += () => Dispatcher.Invoke(SpeedUp);
        _hotkeys.SpeedDownPressed += () => Dispatcher.Invoke(SlowDown);
        _hotkeys.StopPressed      += () => _tts.StopCurrent();
        _hotkeys.ReReadPressed    += () => _tts.ReplayLast();
        _hotkeys.SubmitPressed    += () => Dispatcher.Invoke(() =>
        {
            if (!CanTypeIntoLink(null)) return;
            var hwnd = _link.WindowHandle;
            Task.Run(() => TerminalTypist.SendEnter(hwnd));
        });

        _fw.Start();

        // Jabra SDK: register as softphone so the mic arm fires events directly.
        // Callbacks fire on an SDK thread — dispatch to UI thread for WaveIn safety.
        _jabra = new JabraService();
        _jabra.MicUnmuted    += () => Dispatcher.Invoke(StartSpeak);
        _jabra.MicMuted      += () => Dispatcher.InvokeAsync(StopAndTranscribe);
        _jabra.HangUpPressed += () =>
        {
            // Submit a waiting transcription; otherwise stop the voice
            if (_hotkeys != null && _hotkeys.TrySubmit()) return;
            _tts.StopCurrent();
        };
        _jabra.Init();
        if (!_jabra.IsInitialized)
        {
            var detail = _jabra.InitError.Length > 0
                ? $" ({_jabra.InitError[..Math.Min(50, _jabra.InitError.Length)]})"
                : "";
            StatusMessage.Text = $"Jabra: not connected{detail}";
        }

        try
        {
            SpeakStatus.Text = "Loading model…";
            await Task.Run(() => _stt.InitAsync());
            SpeakStatus.Text = "Ready";
        }
        catch (Exception ex)
        {
            SpeakStatus.Text = "STT unavailable";
            System.Diagnostics.Debug.WriteLine($"[STT] Init failed: {ex.Message}");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePosition();
        _hotkeys?.Dispose();
        _jabra?.Dispose();
        _tts.Dispose();
        _stt.Dispose();
        _link.Dispose();
        _fw.Dispose();
    }

    // ── Borderless-window resize + aspect-ratio lock ────────────────────────────
    // WindowStyle=None + AllowsTransparency means Windows draws no resize border, so
    // we hit-test the edges ourselves (WM_NCHITTEST) to make them draggable, and pin
    // the design's width:height ratio (WM_SIZING) so the Viewbox always fills with no gaps.
    private const double DesignWidth = 320.0;
    private double _designHeight = 560.0;   // recalculated by FitDesignHeight()
    private double DesignAspect => DesignWidth / _designHeight;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        src.AddHook(ResizeHook);
        FitDesignHeight();
    }

    /// <summary>
    /// Sizes the design to its content (which changes when the hotkey list is
    /// shown/hidden) and resizes the window to match, keeping the current scale.
    /// </summary>
    private void FitDesignHeight()
    {
        RootGrid.Height = double.NaN;
        RootGrid.Measure(new Size(DesignWidth, double.PositiveInfinity));
        _designHeight   = Math.Ceiling(RootGrid.DesiredSize.Height);
        RootGrid.Height = _designHeight;

        MinHeight = MinWidth / DesignAspect;
        Height    = Width / DesignAspect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private IntPtr ResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_SIZING    = 0x0214;

        if (msg == WM_NCHITTEST)
        {
            // 6 device-independent px grab border, scaled to physical pixels for DPI
            double scale = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice.M11;
            int b = (int)(6 * scale);

            GetWindowRect(hwnd, out RECT r);
            int x = (short)((long)lParam & 0xFFFF);
            int y = (short)(((long)lParam >> 16) & 0xFFFF);

            bool left   = x <  r.Left   + b;
            bool right  = x >= r.Right  - b;
            bool top    = y <  r.Top    + b;
            bool bottom = y >= r.Bottom - b;

            int hit =
                top && left     ? 13 /*HTTOPLEFT*/     :
                top && right    ? 14 /*HTTOPRIGHT*/    :
                bottom && left  ? 16 /*HTBOTTOMLEFT*/  :
                bottom && right ? 17 /*HTBOTTOMRIGHT*/ :
                left            ? 10 /*HTLEFT*/        :
                right           ? 11 /*HTRIGHT*/       :
                top             ? 12 /*HTTOP*/         :
                bottom          ? 15 /*HTBOTTOM*/      :
                                  1  /*HTCLIENT*/;
            handled = true;
            return (IntPtr)hit;
        }

        if (msg == WM_SIZING)
        {
            var r = Marshal.PtrToStructure<RECT>(lParam);
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;

            // wParam = WMSZ_*: 1 L, 2 R, 3 T, 4 TL, 5 TR, 6 B, 7 BL, 8 BR
            switch (wParam.ToInt32())
            {
                case 1: case 2:           // left / right edge → height from width
                    r.Bottom = r.Top + (int)(w / DesignAspect); break;
                case 3: case 6:           // top / bottom edge → width from height
                    r.Right = r.Left + (int)(h * DesignAspect); break;
                case 4: case 5:           // top corners → adjust top edge
                    r.Top = r.Bottom - (int)(w / DesignAspect); break;
                default:                   // bottom corners (7, 8)
                    r.Bottom = r.Top + (int)(w / DesignAspect); break;
            }
            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return (IntPtr)1;
        }

        return IntPtr.Zero;
    }

    // ── Drag bar ──────────────────────────────────────────────────────────────
    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimiseButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Hotkey list (collapsed by default) ────────────────────────────────────
    private void HotkeysToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = HotkeysPanel.Visibility != Visibility.Visible;
        HotkeysPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HotkeysToggleLabel.Text = show ? "▾  HOTKEYS" : "▸  HOTKEYS";
        FitDesignHeight();
    }

    // ── Speak button ──────────────────────────────────────────────────────────
    private void SpeakButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) StartSpeak();
    }

    private async void SpeakButton_MouseUp(object sender, MouseButtonEventArgs e)
        => await StopAndTranscribe();

    private void StartSpeak()
    {
        if (_stt.CurrentState == SttState.Idle)
            _stt.StartRecording();
    }

    private async Task StopAndTranscribe()
    {
        if (_stt.CurrentState == SttState.Recording)
            await _stt.StopRecordingAndTranscribeAsync();
    }

    private void UpdateSpeakButton(SttState state)
    {
        switch (state)
        {
            case SttState.Idle:
                SpeakButton.Background = new SolidColorBrush(Color.FromRgb(15, 52, 96));
                SpeakIcon.Text   = "🎤";
                SpeakLabel.Text  = "SPEAK";
                SpeakStatus.Text = "Ready";
                break;
            case SttState.Recording:
                SpeakButton.Background = new SolidColorBrush(Color.FromRgb(183, 28, 28));
                SpeakIcon.Text   = "🔴";
                SpeakLabel.Text  = "RECORDING";
                SpeakStatus.Text = "Release to send";
                break;
            case SttState.Transcribing:
                SpeakButton.Background = new SolidColorBrush(Color.FromRgb(30, 60, 30));
                SpeakIcon.Text   = "⏳";
                SpeakLabel.Text  = "TRANSCRIBING";
                SpeakStatus.Text = "Please wait…";
                break;
        }
    }

    // ── Terminal link ─────────────────────────────────────────────────────────

    private void UpdateLinkStatus()
    {
        bool linked = _link.IsLinked;
        LinkStatus.Text         = linked ? $"●  Linked: {_link.DisplayName}" : "○  Not linked";
        LinkStatus.Foreground   = (Brush)FindResource(linked ? "AccentBrush" : "SubtextBrush");
        LinkButton.Content      = linked ? "Re-link" : "Link terminal";
        Grid.SetColumnSpan(LinkButton, linked ? 1 : 3);
        UnlinkButton.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        _link.Unlink();
        StatusMessage.Text = "";
    }

    /// <summary>
    /// Typing goes to whichever tab is showing in the linked window, so refuse
    /// when that isn't the linked tab. Dictated text is kept on the clipboard.
    /// </summary>
    private bool CanTypeIntoLink(string? dictatedText)
    {
        string? problem =
            !_link.IsLinked             ? "Link a terminal first" :
            !_link.IsLinkedTabActive()  ? $"Switch to the linked tab ({_link.DisplayName})" :
            null;
        if (problem == null) return true;

        if (dictatedText != null)
        {
            try { Clipboard.SetText(dictatedText); problem += " — dictation copied to clipboard"; }
            catch { problem += " — dictation not sent"; }
        }
        StatusMessage.Text = problem;
        return false;
    }

    // Link gesture: user clicks "Link terminal", then clicks their terminal window.
    // A DispatcherTimer polls GetForegroundWindow() every 200 ms. When focus moves to
    // a window that doesn't belong to ClaudeVoice, we capture it and link it.
    private DispatcherTimer? _linkTimer;
    private int _linkTickCount;

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        LinkButton.IsEnabled = false;
        LinkStatus.Text = "Click your terminal window…";
        _linkTickCount = 0;

        _linkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _linkTimer.Tick += LinkTimer_Tick;
        _linkTimer.Start();
    }

    private void LinkTimer_Tick(object? sender, EventArgs e)
    {
        _linkTickCount++;

        // 600 ms grace period — ignore the first 3 ticks so the user has time to move
        // the mouse away from the Link button before we start tracking focus changes.
        if (_linkTickCount <= 3) return;

        // Timeout after ~6 s (30 tracking ticks × 200 ms)
        if (_linkTickCount > 33)
        {
            FinishLink();
            StatusMessage.Text = "Link timed out — try again";
            return;
        }

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        // Skip any window that belongs to our own process
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)Environment.ProcessId) return;

        FinishLink();
        StatusMessage.Text = _link.Link(fg) ?? "";
    }

    private void FinishLink()
    {
        _linkTimer?.Stop();
        _linkTimer = null;
        LinkButton.IsEnabled = true;
        UpdateLinkStatus();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── TTS ───────────────────────────────────────────────────────────────────
    private void TtsToggle_Checked(object sender, RoutedEventArgs e)   => SetTts(true);
    private void TtsToggle_Unchecked(object sender, RoutedEventArgs e) => SetTts(false);

    private void SetTts(bool enabled)
    {
        _ttsEnabled = enabled;
        _fw.WriteTtsEnabled(enabled);
        SyncTtsToggle(enabled);
    }

    private void SyncTtsToggle(bool enabled)
    {
        _ttsEnabled         = enabled;
        TtsToggle.IsChecked = enabled;
        TtsStatusLabel.Text = enabled ? "ON" : "OFF";
        TtsStatusLabel.Foreground = enabled
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("SubtextBrush");
    }

    // ── Speed ─────────────────────────────────────────────────────────────────
    private void SpeedUp_Click(object sender, RoutedEventArgs e)  => SpeedUp();
    private void SlowDown_Click(object sender, RoutedEventArgs e) => SlowDown();

    private void SpeedUp()
    {
        _ttsRate = Math.Min(_ttsRate + 10, 100);
        ApplyRate();
    }

    private void SlowDown()
    {
        _ttsRate = Math.Max(_ttsRate - 10, -50);
        ApplyRate();
    }

    private void ApplyRate()
    {
        _fw.WriteTtsRate(_ttsRate);
        SyncRate(_ttsRate);
    }

    private void SyncRate(int rate)
    {
        _ttsRate       = rate;
        RateLabel.Text = rate >= 0 ? $"+{rate}%" : $"{rate}%";
    }

    // ── Playback controls ─────────────────────────────────────────────────────
    private void Stop_Click(object sender, RoutedEventArgs e)
        => _tts.StopCurrent();

    private void ReRead_Click(object sender, RoutedEventArgs e)
        => _tts.ReplayLast();

    // ── Transcription-complete ding ─────────────────────────────────────────

    private static void PlayTranscriptionDing()
    {
        // Two-tone rising ding (1200 Hz → 1500 Hz) to signal "transcription done, ready to submit".
        Task.Run(() =>
        {
            try
            {
                using var player = new WaveOutEvent();

                var tone1 = new SignalGenerator(22050, 1)
                    { Gain = 0.30, Frequency = 1200, Type = SignalGeneratorType.Sin }
                    .Take(TimeSpan.FromMilliseconds(80));
                var tone2 = new SignalGenerator(22050, 1)
                    { Gain = 0.30, Frequency = 1500, Type = SignalGeneratorType.Sin }
                    .Take(TimeSpan.FromMilliseconds(100));

                player.Init(new ConcatenatingSampleProvider(new[] { tone1, tone2 }));
                player.Play();
                while (player.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(10);
            }
            catch { }
        });
    }

    // ── Window position ───────────────────────────────────────────────────────
    private static readonly string PosFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "claudevoice_pos.txt");

    private void SavePosition()
    {
        try { File.WriteAllText(PosFile, $"{Left},{Top}"); } catch { }
    }

    private void RestorePosition()
    {
        try
        {
            if (!File.Exists(PosFile)) return;
            var parts = File.ReadAllText(PosFile).Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var l) &&
                double.TryParse(parts[1], out var t))
            { Left = l; Top = t; }
        }
        catch { }
    }
}
