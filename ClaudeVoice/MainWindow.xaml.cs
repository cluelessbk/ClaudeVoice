using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVoice.Models;
using ClaudeVoice.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClaudeVoice;

public partial class MainWindow : Window
{
    private readonly FileWatcherService _fw;
    private readonly TtsService _tts;
    private readonly SttService _stt;
    private readonly TerminalService _terminals;
    private HotkeyService?  _hotkeys;
    private JabraService?   _jabra;

    private int  _ttsRate    = 0;
    private bool _ttsEnabled = true;

    // Beep notification: tracks when each session became pending (for the 5-min schedule)
    private readonly Dictionary<string, DateTime> _pendingNotifyStart = new();
    private DispatcherTimer? _notifyTimer;

    public MainWindow()
    {
        InitializeComponent();

        _fw        = new FileWatcherService();
        _tts       = new TtsService(_fw);
        _stt       = new SttService();
        _terminals = new TerminalService();

        _fw.TtsEnabledChanged += enabled => Dispatcher.Invoke(() => SyncTtsToggle(enabled));
        _fw.TtsRateChanged    += rate    => Dispatcher.Invoke(() => SyncRate(rate));

        _terminals.Sessions.CollectionChanged += (_, _) =>
            Dispatcher.Invoke(() => TerminalsList.ItemsSource = _terminals.Sessions);
        _terminals.SessionsChanged += () =>
            Dispatcher.Invoke(UpdateActiveSession);

        // STT: transcribed text goes to the specific Claude terminal for the active session.
        // ActiveWindowHandle targets the exact window (safe even when Windows Terminal shares a PID).
        _stt.StateChanged += state => Dispatcher.Invoke(() => UpdateSpeakButton(state));
        _stt.Transcribed  += text =>
        {
            var hwnd = _tts.ActiveWindowHandle;
            Task.Run(() => TerminalTypist.FocusAndType(text, hwnd));
            _hotkeys?.SetSubmitPending(true);
        };

        // Headset mic mute → start/stop recording (same as Ctrl+Space but hands-free).
        // Callback fires on a COM thread — dispatch to UI thread for WaveIn safety.
        _stt.MicUnmuted += () => Dispatcher.Invoke(StartSpeak);
        _stt.MicMuted   += () => Dispatcher.InvokeAsync(StopAndTranscribe);

        // TTS pause/resume state → update Pause button label
        _tts.PausedChanged += paused =>
            Dispatcher.Invoke(() => PausePlayButton.Content = paused ? "▶  Resume" : "⏸  Pause");

        _tts.PlayingChanged += _ => { }; // placeholder for future Play button restoration

        // Surface TTS errors to the UI so silent failures are visible
        _tts.TtsErrorOccurred += msg =>
            Dispatcher.Invoke(() => LinkStatus.Text = $"TTS error: {msg[..Math.Min(60, msg.Length)]}");

        // Badge: a session has pending audio — start beep notification schedule
        _tts.SessionPendingChanged += (sessionId, hasPending) =>
            Dispatcher.Invoke(() =>
            {
                var session = _terminals.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session != null) session.HasPending = hasPending;
                if (hasPending)
                    StartPendingNotification(sessionId);
                else
                    StopPendingNotification(sessionId);
                UpdateCycleEnabled();
            });

        _ttsEnabled = _fw.ReadTtsEnabled();
        _ttsRate    = _fw.ReadTtsRate();
        SyncTtsToggle(_ttsEnabled);
        SyncRate(_ttsRate);

        RestorePosition();
        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyService(this);

        _hotkeys.SpeakStarted       += () => Dispatcher.Invoke(StartSpeak);
        _hotkeys.SpeakReleased      += () => Dispatcher.InvokeAsync(StopAndTranscribe);
        _hotkeys.ToggleTtsPressed   += () => Dispatcher.Invoke(() => SetTts(!_ttsEnabled));
        _hotkeys.SpeedUpPressed     += () => Dispatcher.Invoke(SpeedUp);
        _hotkeys.SpeedDownPressed   += () => Dispatcher.Invoke(SlowDown);
        _hotkeys.StopPressed        += () => _tts.StopCurrent();
        _hotkeys.ReReadPressed      += () => _tts.ReplayLast();
        _hotkeys.PauseTogglePressed += () => _tts.TogglePause();
        _hotkeys.NextTerminalPressed += () => Dispatcher.Invoke(() => SwitchTerminal(+1));
        _hotkeys.PrevTerminalPressed += () => Dispatcher.Invoke(() => SwitchTerminal(-1));
        _hotkeys.MediaSubmitPressed  += () => Task.Run(() => TerminalTypist.SendEnter(_tts.ActiveWindowHandle));
        _hotkeys.MediaCyclePressed   += () => Dispatcher.Invoke(CycleToPendingSession);
        _hotkeys.HidButtonPressed    += () =>
        {
            // Raw HID Play button (0x0080) — ready for Phase 2 solution
            _hotkeys.TriggerMediaPlay();
        };

        _fw.Start();
        _terminals.Start();
        TerminalsList.ItemsSource = _terminals.Sessions;

        // Jabra SDK: register as softphone so the mic arm fires events directly.
        // Callbacks fire on an SDK thread — dispatch to UI thread for WaveIn safety.
        _jabra = new JabraService();
        _jabra.MicUnmuted        += () => Dispatcher.Invoke(StartSpeak);
        _jabra.MicMuted          += () => Dispatcher.InvokeAsync(StopAndTranscribe);
        _jabra.HangUpPressed     += () =>
        {
            // Priority: submit transcription > cycle to pending session > send Enter (fallback)
            if (_hotkeys != null && _hotkeys.TriggerMediaPlay()) return;
            Task.Run(() => TerminalTypist.SendEnter(_tts.ActiveWindowHandle));
        };
        _jabra.Init();
        if (!_jabra.IsInitialized)
        {
            var detail = _jabra.InitError.Length > 0
                ? $" ({_jabra.InitError[..Math.Min(50, _jabra.InitError.Length)]})"
                : "";
            LinkStatus.Text = $"Jabra: not connected{detail}";
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
        _terminals.Dispose();
        _fw.Dispose();
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

    // ── Terminals ─────────────────────────────────────────────────────────────

    // Link gesture: user clicks "Link terminal", then clicks their PowerShell window.
    // A DispatcherTimer polls GetForegroundWindow() every 200 ms. When focus moves to
    // a window that doesn't belong to ClaudeVoice, we capture it and call LinkTerminal.
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
            FinishLink("Timed out — try again");
            return;
        }

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        // Skip any window that belongs to our own process
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)Environment.ProcessId) return;

        // Found an external window — link it
        _linkTimer!.Stop();
        _linkTimer = null;
        _terminals.LinkTerminal(fg);
        FinishLink("");

        // Auto-activate: if this is the first terminal linked, make it the active session
        // immediately so TTS works without requiring the user to click it.
        var justLinked = _terminals.Sessions.LastOrDefault();
        if (justLinked != null && _terminals.Sessions.Count == 1)
        {
            justLinked.IsActive = true;
            _tts.SetActiveSession(justLinked.SessionId, justLinked.TranscriptPath, justLinked.ProcessId, justLinked.WindowHandle);
        }
        if (justLinked != null)
        {
            var refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            refreshTimer.Tick += (s, _) =>
            {
                refreshTimer.Stop();
                _terminals.TryRefreshDisplayName(justLinked);
            };
            refreshTimer.Start();
        }
    }

    private void FinishLink(string statusMessage)
    {
        _linkTimer?.Stop();
        _linkTimer = null;
        LinkButton.IsEnabled = true;
        LinkStatus.Text = statusMessage;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private void TerminalRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TerminalSession session)
        {
            // Mark session active in the UI
            foreach (var s in _terminals.Sessions)
                s.IsActive = s.SessionId == session.SessionId;

            // Tell TtsService — switches the audio queue, writes active_session.txt,
            // and stores the processId so TerminalTypist targets the right window
            _tts.SetActiveSession(session.SessionId, session.TranscriptPath, session.ProcessId, session.WindowHandle);

            session.HasPending = false;
            StopPendingNotification(session.SessionId);
            UpdateCycleEnabled();
        }
    }

    private void UpdateActiveSession()
    {
        var activePath = _fw.ReadActiveSession();
        bool matched = false;
        foreach (var s in _terminals.Sessions)
        {
            s.IsActive = s.TranscriptPath == activePath;
            if (s.IsActive) matched = true;

            // Audio for this session may have arrived before TerminalService scanned it.
            // Restore the badge now that the session is in the list.
            if (!s.IsActive && _tts.HasQueuedItems(s.SessionId))
                s.HasPending = true;
        }

        // Auto-select the only terminal when nothing is marked active
        if (!matched && _terminals.Sessions.Count == 1)
        {
            var only = _terminals.Sessions[0];
            only.IsActive = true;
            _tts.SetActiveSession(only.SessionId, only.TranscriptPath, only.ProcessId, only.WindowHandle);
        }

        UpdateCycleEnabled();
    }

    private void SwitchTerminal(int delta)
    {
        var sessions = _terminals.Sessions;
        if (sessions.Count < 2) return;

        int current = -1;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].IsActive) { current = i; break; }
        if (current < 0) current = 0;

        int next = (current + delta + sessions.Count) % sessions.Count;
        var session = sessions[next];

        foreach (var s in sessions)
            s.IsActive = s.SessionId == session.SessionId;

        _tts.SetActiveSession(session.SessionId, session.TranscriptPath, session.ProcessId, session.WindowHandle);
        session.HasPending = false;
        StopPendingNotification(session.SessionId);
        UpdateCycleEnabled();
    }

    private void CycleToPendingSession()
    {
        var pending = _terminals.Sessions.Where(s => s.HasPending).ToList();
        if (pending.Count == 0) return;

        // Find the next pending session after the currently active one
        int activeIdx = -1;
        for (int i = 0; i < _terminals.Sessions.Count; i++)
            if (_terminals.Sessions[i].IsActive) { activeIdx = i; break; }

        // Pick the first pending session that comes AFTER the active one (wraps around)
        var next = pending.FirstOrDefault(s =>
            _terminals.Sessions.IndexOf(s) > activeIdx)
            ?? pending[0];

        foreach (var s in _terminals.Sessions)
            s.IsActive = s.SessionId == next.SessionId;

        _tts.SetActiveSession(next.SessionId, next.TranscriptPath, next.ProcessId, next.WindowHandle);
        next.HasPending = false;
        StopPendingNotification(next.SessionId);
        UpdateCycleEnabled();
    }

    private void UpdateCycleEnabled()
        => _hotkeys?.SetCycleEnabled(_terminals.Sessions.Any(s => s.HasPending));

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

    private void PausePlay_Click(object sender, RoutedEventArgs e)
        => _tts.TogglePause();

    // ── Ping sound ────────────────────────────────────────────────────────────

    private static void PlayPing()
    {
        // Fire-and-forget: short 880 Hz sine tone (150 ms) with fade-out envelope.
        // Runs on a background thread so it never blocks the UI.
        Task.Run(() =>
        {
            try
            {
                var signal = new SignalGenerator(22050, 1)
                {
                    Gain      = 0.35,
                    Frequency = 880,
                    Type      = SignalGeneratorType.Sin,
                };
                var taken = signal.Take(TimeSpan.FromMilliseconds(150));
                using var player = new WaveOutEvent();
                player.Init(taken);
                player.Play();
                while (player.PlaybackState == PlaybackState.Playing)
                    System.Threading.Thread.Sleep(10);
            }
            catch { }
        });
    }

    // ── Pending session notification schedule ───────────────────────────────

    private void StartPendingNotification(string sessionId)
    {
        if (_pendingNotifyStart.ContainsKey(sessionId)) return; // already tracking

        _pendingNotifyStart[sessionId] = DateTime.UtcNow;
        PlayPing(); // immediate first beep

        // Start the shared timer if not already running
        if (_notifyTimer == null)
        {
            _notifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _notifyTimer.Tick += NotifyTimer_Tick;
            _notifyTimer.Start();
        }
    }

    private void StopPendingNotification(string sessionId)
    {
        _pendingNotifyStart.Remove(sessionId);
        if (_pendingNotifyStart.Count == 0 && _notifyTimer != null)
        {
            _notifyTimer.Stop();
            _notifyTimer = null;
        }
    }

    private void NotifyTimer_Tick(object? sender, EventArgs e)
    {
        var expired = new List<string>();
        bool shouldBeep = false;

        foreach (var (sessionId, startTime) in _pendingNotifyStart)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

            if (elapsed >= 300) // 5 minutes — stop notifying
            {
                expired.Add(sessionId);
                continue;
            }

            // Beep in the first 10 seconds of each minute
            if (elapsed % 60 < 10)
                shouldBeep = true;
        }

        foreach (var id in expired)
            _pendingNotifyStart.Remove(id);

        if (_pendingNotifyStart.Count == 0 && _notifyTimer != null)
        {
            _notifyTimer.Stop();
            _notifyTimer = null;
        }

        if (shouldBeep)
            PlayPing();
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
