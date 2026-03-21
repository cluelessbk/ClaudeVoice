using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace ClaudeVoice.Services;

/// <summary>
/// Manages per-session audio queues. Each terminal session accumulates its own
/// queue independently. Only the active session's queue plays. Switching sessions
/// auto-pauses the current one (interrupted item goes back to front of its queue).
/// </summary>
public class TtsService : IDisposable
{
    private readonly FileWatcherService _fw;
    private readonly CancellationTokenSource _cts = new();   // service lifetime
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private readonly object _queueLock = new();
    private readonly Dictionary<string, LinkedList<string>> _sessionQueues = new();

    private volatile WaveOutEvent? _player;
    private volatile Process? _currentEdgeProc;
    private CancellationTokenSource _stopCts = new();

    private readonly string   _edgeTtsFileName;
    private readonly string[] _edgeTtsPrefixArgs;

    private string _lastText        = "";
    private string _activeSessionId = "";
    private volatile bool _isPaused = false;
    private int  _rate    = 0;
    private bool _enabled = true;

    public string ActiveSessionId  => _activeSessionId;
    public bool   IsPaused         => _isPaused;
    // volatile int: 0 = unknown, positive = PID. Read safely from any thread.
    private volatile int _activeProcessId = 0;
    public int? ActiveProcessId => _activeProcessId == 0 ? null : _activeProcessId;

    // true = playback started, false = playback ended
    public event Action<bool>?         PlayingChanged;
    // fired when edge-tts fails — message surfaced to UI
    public event Action<string>?       TtsErrorOccurred;
    // true = paused, false = playing/resumed
    public event Action<bool>?         PausedChanged;
    // (sessionId, hasPending) — drives the 🔔 badge on terminal rows
    public event Action<string, bool>? SessionPendingChanged;

    public TtsService(FileWatcherService fw)
    {
        _fw = fw;
        _enabled = fw.ReadTtsEnabled();
        _rate    = fw.ReadTtsRate();
        (_edgeTtsFileName, _edgeTtsPrefixArgs) = FindEdgeTts();

        // Initialise active session from the saved file
        var activePath = fw.ReadActiveSession();
        if (!string.IsNullOrEmpty(activePath))
            _activeSessionId = ComputeSessionId(activePath);

        fw.AudioFileArrived  += OnAudioFileArrived;
        fw.TtsEnabledChanged += v => _enabled = v;
        fw.TtsRateChanged    += v => _rate = v;

        // Pick up any files already waiting in the queue folder
        foreach (var f in fw.GetQueueFiles())
            OnAudioFileArrived(f);

        Task.Run(() => ProcessLoop(_cts.Token));
    }

    // ── Session management ────────────────────────────────────────────────────

    /// <summary>
    /// Switch the active session. Auto-pauses the current session (saves interrupted
    /// item to front of its queue) and starts playing the new session's queue.
    /// Clicking the already-active session toggles pause/resume.
    /// </summary>
    public void SetActiveSession(string sessionId, string transcriptPath, int? processId = null)
    {
        _activeProcessId = processId ?? 0;

        if (sessionId == _activeSessionId)
        {
            TogglePause();
            return;
        }

        // Switch to new session
        _activeSessionId = sessionId;
        _isPaused        = false;

        // Cancel current playback so ProcessLoop wakes up and starts the new session
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        // Clear badge on newly active session
        SessionPendingChanged?.Invoke(sessionId, false);
        PausedChanged?.Invoke(false);

        // Keep the active_session.txt file in sync for Python hooks
        _fw.WriteActiveSession(transcriptPath);
    }

    public void TogglePause()
    {
        if (_isPaused) Resume();
        else Pause();
    }

    public void Pause()
    {
        if (_isPaused) return;
        _isPaused = true;

        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        PausedChanged?.Invoke(true);
    }

    public void Resume()
    {
        if (!_isPaused) return;
        _isPaused = false;
        PausedChanged?.Invoke(false);
        // ProcessLoop will pick up the queue on its next poll
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops playback and clears the active session's queue only.
    /// Then replays the last spoken message so you don't miss what was interrupted.
    /// Use Pause to temporarily halt without losing your place in the queue.
    /// </summary>
    public void StopCurrent()
    {
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        // Clear the active session's queue only
        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue))
            {
                var node = queue.First;
                while (node != null) { _fw.DeleteQueueFile(node.Value); node = node.Next; }
                queue.Clear();
            }
        }

        _isPaused = false;
        PausedChanged?.Invoke(false);

        // Re-queue the last message so you can hear what was interrupted
        if (!string.IsNullOrWhiteSpace(_lastText))
            RequeueTextAtFront(_activeSessionId, _lastText);
    }

    // ── Queue management ──────────────────────────────────────────────────────

    private void OnAudioFileArrived(string filePath)
    {
        var (sessionId, _) = ParseQueueFilename(filePath);
        if (string.IsNullOrEmpty(sessionId)) sessionId = _activeSessionId;

        // Auto-adopt: if no active session is set, treat the first incoming session as active.
        // This makes single-terminal usage work without requiring explicit linking or selection.
        if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
            _activeSessionId = sessionId;

        lock (_queueLock)
        {
            if (!_sessionQueues.TryGetValue(sessionId, out var queue))
            {
                queue = new LinkedList<string>();
                _sessionQueues[sessionId] = queue;
            }
            queue.AddLast(filePath);
        }

        // Show badge if audio arrived for a non-active session
        if (sessionId != _activeSessionId)
            SessionPendingChanged?.Invoke(sessionId, true);
    }

    private static (string sessionId, bool isActive) ParseQueueFilename(string path)
    {
        var name  = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        if (parts.Length >= 3)
            return (parts[0], parts[^1] == "active");
        return ("", true);
    }

    private bool TryDequeueForActiveSession(out string filePath)
    {
        filePath = "";
        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue) && queue.Count > 0)
            {
                filePath = queue.First!.Value;
                queue.RemoveFirst();
                return true;
            }
        }
        return false;
    }

    private void RequeueTextAtFront(string sessionId, string text)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(text)) return;
        var tmpPath = Path.Combine(Path.GetTempPath(), $"cv_pause_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tmpPath, text);
            lock (_queueLock)
            {
                if (!_sessionQueues.TryGetValue(sessionId, out var queue))
                {
                    queue = new LinkedList<string>();
                    _sessionQueues[sessionId] = queue;
                }
                queue.AddFirst(tmpPath);  // front of queue — replays from start on resume
            }
        }
        catch { }
    }

    // ── Process loop ──────────────────────────────────────────────────────────

    private async Task ProcessLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_isPaused && _enabled && TryDequeueForActiveSession(out var filePath))
            {
                var sessionAtStart = _activeSessionId;
                using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);

                try { await SpeakFile(filePath, linked.Token); }
                catch (OperationCanceledException) { }

                // Re-queue the interrupted item if needed.
                // Pause case: check _isPaused directly — the token may not be cancelled if audio
                // ended naturally at the exact instant Pause was pressed (race condition).
                // Session switch: requires token cancellation to distinguish from natural finish.
                // Stop: _isPaused=false and session unchanged → neither condition is true → no re-queue.
                if (!string.IsNullOrEmpty(_lastText))
                {
                    bool paused        = _isPaused && _activeSessionId == sessionAtStart;
                    bool sessionSwitch = linked.Token.IsCancellationRequested && _activeSessionId != sessionAtStart;
                    if (paused || sessionSwitch)
                        RequeueTextAtFront(sessionAtStart, _lastText);
                }
            }
            else
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SpeakFile(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;

        string text;
        try   { text = await File.ReadAllTextAsync(filePath, ct); }
        catch { return; }
        finally { _fw.DeleteQueueFile(filePath); }

        text = text.Trim();
        if (text.Length < 5) return;

        _lastText = text;
        await SpeakText(text, ct);
    }

    private async Task SpeakText(string text, CancellationToken ct)
    {
        await _playLock.WaitAsync(ct);
        var mp3Path = Path.Combine(Path.GetTempPath(), $"cv_{Guid.NewGuid()}.mp3");
        try
        {
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_fw.ClaudeDir, "tts_last.txt"), text, ct)
                    .ConfigureAwait(false);
            }
            catch { }

            string rateStr = _rate >= 0 ? $"+{_rate}%" : $"{_rate}%";

            var edgePsi = new ProcessStartInfo
            {
                FileName              = _edgeTtsFileName,
                UseShellExecute       = false,
                CreateNoWindow        = true,
                RedirectStandardError = true,
            };
            foreach (var arg in _edgeTtsPrefixArgs)
                edgePsi.ArgumentList.Add(arg);
            edgePsi.ArgumentList.Add("--voice");
            edgePsi.ArgumentList.Add("en-GB-RyanNeural");
            edgePsi.ArgumentList.Add($"--rate={rateStr}");
            edgePsi.ArgumentList.Add("--text");
            edgePsi.ArgumentList.Add(text);
            edgePsi.ArgumentList.Add("--write-media");
            edgePsi.ArgumentList.Add(mp3Path);

            try
            {
                using var edgeProc = Process.Start(edgePsi);
                _currentEdgeProc = edgeProc;
                string stderr = "";
                try
                {
                    if (edgeProc != null)
                    {
                        // Read stderr concurrently — must start before WaitForExitAsync
                        // to avoid deadlock if the error buffer fills up
                        var stderrTask = edgeProc.StandardError.ReadToEndAsync(ct);
                        await edgeProc.WaitForExitAsync(ct);
                        stderr = await stderrTask;
                    }
                }
                catch (OperationCanceledException)
                {
                    try { edgeProc?.Kill(entireProcessTree: true); } catch { }
                    return;
                }
                finally { _currentEdgeProc = null; }

                if (!File.Exists(mp3Path))
                {
                    var msg = string.IsNullOrWhiteSpace(stderr) ? "no output file" : stderr.Trim();
                    Debug.WriteLine($"[TtsService] edge-tts failed: {msg}");
                    TtsErrorOccurred?.Invoke(msg);
                    return;
                }

                if (ct.IsCancellationRequested) return;

                PlayingChanged?.Invoke(true);
                using var reader = new Mp3FileReader(mp3Path);
                using var wave   = new WaveOutEvent();
                _player = wave;
                wave.Init(reader);
                wave.Play();

                while (wave.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
                    await Task.Delay(100, ct).ConfigureAwait(false);

                wave.Stop();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TtsService] Playback error: {ex.Message}");
            }
            finally
            {
                _player = null;
                PlayingChanged?.Invoke(false);
                try { File.Delete(mp3Path); } catch { }
            }
        }
        finally
        {
            _playLock.Release();
        }
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given session has audio items waiting in its queue.
    /// Used by MainWindow to restore the 🔔 badge when a session is discovered
    /// after its audio has already arrived.
    /// </summary>
    public bool HasQueuedItems(string sessionId)
    {
        lock (_queueLock)
            return _sessionQueues.TryGetValue(sessionId, out var q) && q.Count > 0;
    }

    // ── Replay last ───────────────────────────────────────────────────────────

    /// <summary>
    /// Replays the last spoken message, then continues with whatever was already queued.
    /// Does NOT clear the queue — use Stop for a nuclear reset.
    /// </summary>
    public void ReplayLast()
    {
        if (string.IsNullOrWhiteSpace(_lastText)) return;

        // Cancel current playback without touching the queue
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        // Put last message at front — ProcessLoop will play it then continue with the queue
        RequeueTextAtFront(_activeSessionId, _lastText);

        _isPaused = false;
        PausedChanged?.Invoke(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the edge-tts executable at startup.
    /// C# Process.Start with UseShellExecute=false only sees the system PATH,
    /// not the user's shell PATH where pip installs scripts. So we check
    /// the known per-user Python Scripts location explicitly first.
    /// Falls back to "python -m edge_tts" if the binary isn't found.
    /// </summary>
    private static (string FileName, string[] PrefixArgs) FindEdgeTts()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pythonBase   = Path.Combine(localAppData, "Programs", "Python");

        if (Directory.Exists(pythonBase))
        {
            // Sort descending so the highest Python version is tried first
            foreach (var dir in Directory.GetDirectories(pythonBase, "Python3*")
                                         .OrderByDescending(d => d))
            {
                var exe = Path.Combine(dir, "Scripts", "edge-tts.exe");
                if (File.Exists(exe)) return (exe, Array.Empty<string>());
            }
        }

        // Fallback: use python -m edge_tts (works as long as python.exe is in system PATH)
        return ("python", new[] { "-m", "edge_tts" });
    }

    private static string ComputeSessionId(string transcriptPath)
        => Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                Encoding.UTF8.GetBytes(transcriptPath)))[..8].ToLowerInvariant();

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();
        _playLock.Dispose();
        _stopCts.Dispose();
        _cts.Dispose();
    }
}
