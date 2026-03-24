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
    private volatile bool _activeSessionIdIsPlaceholder = false;
    private int  _rate    = 0;
    private bool _enabled = true;

    public string ActiveSessionId  => _activeSessionId;
    public bool   IsPaused         => _isPaused;
    // volatile int: 0 = unknown, positive = PID. Read safely from any thread.
    private volatile int _activeProcessId = 0;
    public int? ActiveProcessId => _activeProcessId == 0 ? null : _activeProcessId;

    // HWND of the active session's terminal window — targets the exact window
    // even when Windows Terminal shares a PID across multiple windows.
    private IntPtr _activeWindowHandle = IntPtr.Zero;
    public IntPtr ActiveWindowHandle => _activeWindowHandle;

    // true = playback started, false = playback ended
    public event Action<bool>?         PlayingChanged;
    // fired when edge-tts fails — message surfaced to UI
    public event Action<string>?       TtsErrorOccurred;
    // true = paused, false = playing/resumed
    public event Action<bool>?         PausedChanged;
    // (sessionId, hasPending) — drives the 🔔 badge on terminal rows
    public event Action<string, bool>? SessionPendingChanged;
    // (oldId, newId) — fired when a placeholder session ID is resolved to the real one
    public event Action<string, string>? SessionIdResolved;

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

        // Clear any stale queue files left over from a previous session.
        // ClaudeVoice.exe is a single instance — files present at startup are always
        // from a dead session (app wasn't running, so nothing could have played them).
        foreach (var f in fw.GetQueueFiles())
            fw.DeleteQueueFile(f);

        Task.Run(() => ProcessLoop(_cts.Token));
    }

    // ── Session management ────────────────────────────────────────────────────

    /// <summary>
    /// Switch the active session. Auto-pauses the current session (saves interrupted
    /// item to front of its queue) and starts playing the new session's queue.
    /// Clicking the already-active session toggles pause/resume.
    /// </summary>
    public void SetActiveSession(string sessionId, string transcriptPath, int? processId = null, IntPtr windowHandle = default, bool isPlaceholder = false)
    {
        _activeProcessId              = processId ?? 0;
        _activeWindowHandle           = windowHandle;
        _activeSessionIdIsPlaceholder = isPlaceholder;

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

        // If NAudio is actively playing, pause it in place (resumes from same position).
        // If edge-tts is still generating, just set the flag — SpeakText checks _isPaused
        // before starting playback so it will pause without needing to regenerate.
        var player = _player;
        if (player != null && player.PlaybackState == PlaybackState.Playing)
            player.Pause();

        PausedChanged?.Invoke(true);
    }

    public void Resume()
    {
        if (!_isPaused) return;
        _isPaused = false;

        // If the player is paused, resume it directly
        var player = _player;
        if (player != null && player.PlaybackState == PlaybackState.Paused)
            player.Play();

        PausedChanged?.Invoke(false);
        // If edge-tts was generating when paused, SpeakText will start playback
        // once generation finishes (it checks _isPaused before playing).
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops playback and clears the active session's queue only.
    /// Sets _lastText to the last queued item (the tail) so Replay plays the final part.
    /// </summary>
    public void StopCurrent()
    {
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        // Read the last queued item's text before clearing, so Replay gets the final part
        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue))
            {
                // Grab text from the last item in the queue for Replay
                if (queue.Last != null)
                {
                    try
                    {
                        var lastFile = queue.Last.Value;
                        if (File.Exists(lastFile))
                            _lastText = File.ReadAllText(lastFile).Trim();
                    }
                    catch { }
                }

                var node = queue.First;
                while (node != null) { _fw.DeleteQueueFile(node.Value); node = node.Next; }
                queue.Clear();
            }
        }

        _isPaused = false;
        PausedChanged?.Invoke(false);
    }

    // ── Queue management ──────────────────────────────────────────────────────

    private void OnAudioFileArrived(string filePath)
    {
        var (sessionId, _) = ParseQueueFilename(filePath);
        if (string.IsNullOrEmpty(sessionId)) sessionId = _activeSessionId;

        // Auto-adopt: if no active session is set, treat the first incoming session as active.
        if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
        {
            _activeSessionId = sessionId;
            _activeSessionIdIsPlaceholder = false;
        }

        // Re-adopt: if the active session has a placeholder ID (FindTranscriptPath failed
        // at link time), replace it with the real session ID from the audio queue filename.
        // This resolves the mismatch that causes audio to be treated as pending (beep).
        if (sessionId != _activeSessionId && _activeSessionIdIsPlaceholder)
        {
            var oldId = _activeSessionId;
            _activeSessionId = sessionId;
            _activeSessionIdIsPlaceholder = false;
            SessionIdResolved?.Invoke(oldId, sessionId);
        }

        // Discard audio from unlinked sessions. Python hooks fire for ALL Claude sessions
        // and write to audio_queue/ — we only want audio from sessions the user has linked.
        // A session is "known" if it's the active one or already has a queue (was previously active).
        bool known = sessionId == _activeSessionId;
        if (!known)
        {
            lock (_queueLock) { known = _sessionQueues.ContainsKey(sessionId); }
        }
        if (!known)
        {
            _fw.DeleteQueueFile(filePath);
            return;
        }

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

    private int _pollCounter = 0;

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
                // Fallback polling: every ~2s (10 × 200ms), scan the queue directory
                // for files the FileSystemWatcher may have missed (buffer overflow, timing, etc.)
                if (++_pollCounter >= 10)
                {
                    _pollCounter = 0;
                    PollQueueDirectory();
                }

                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Scans the queue directory for .txt files that aren't already in any session queue.
    /// Re-feeds them through OnAudioFileArrived so they get properly queued and played.
    /// </summary>
    private void PollQueueDirectory()
    {
        var filesOnDisk = _fw.GetQueueFiles();
        if (filesOnDisk.Length == 0) return;

        // Build set of all file paths currently in session queues
        HashSet<string> knownFiles;
        lock (_queueLock)
        {
            knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var queue in _sessionQueues.Values)
                foreach (var f in queue)
                    knownFiles.Add(f);
        }

        foreach (var f in filesOnDisk)
        {
            if (!knownFiles.Contains(f))
                OnAudioFileArrived(f);
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

                // If paused during edge-tts generation, wait here until resumed
                // instead of killing the process and regenerating.
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                if (ct.IsCancellationRequested) return;

                PlayingChanged?.Invoke(true);
                using var reader = new Mp3FileReader(mp3Path);
                using var wave   = new WaveOutEvent();
                _player = wave;
                wave.Init(reader);
                wave.Play();

                // Wait while playing OR paused — only exit on stop/cancel or natural end
                while (!ct.IsCancellationRequested &&
                       (wave.PlaybackState == PlaybackState.Playing ||
                        wave.PlaybackState == PlaybackState.Paused))
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

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

    /// <summary>
    /// Removes queued audio for any session not in the live set.
    /// Called when terminals are removed (closed) so their orphaned audio doesn't linger.
    /// </summary>
    public void RemoveSessionQueue(HashSet<string> liveSessionIds)
    {
        lock (_queueLock)
        {
            var dead = _sessionQueues.Keys.Where(id => !liveSessionIds.Contains(id)).ToList();
            foreach (var id in dead)
            {
                if (_sessionQueues.TryGetValue(id, out var queue))
                {
                    foreach (var f in queue) _fw.DeleteQueueFile(f);
                    queue.Clear();
                }
                _sessionQueues.Remove(id);
            }
        }
    }

    // ── Replay last ───────────────────────────────────────────────────────────

    /// <summary>
    /// Stops current playback, clears the active session's queue, and replays only
    /// the last spoken message. Nothing else plays after it.
    /// </summary>
    public void ReplayLast()
    {
        if (string.IsNullOrWhiteSpace(_lastText)) return;

        // Stop current playback
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        // Clear the active session's queue so nothing plays after the replay
        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue))
            {
                var node = queue.First;
                while (node != null) { _fw.DeleteQueueFile(node.Value); node = node.Next; }
                queue.Clear();
            }
        }

        // Re-queue only the last message
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
                Encoding.UTF8.GetBytes(transcriptPath.Replace('/', '\\'))))[..8].ToLowerInvariant();

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();
        _playLock.Dispose();
        _stopCts.Dispose();
        _cts.Dispose();

        // Clean up queue folder on shutdown — files can't be played with the app closed,
        // and this prevents them from being picked up as stale on next startup.
        foreach (var f in _fw.GetQueueFiles())
            _fw.DeleteQueueFile(f);
    }
}
