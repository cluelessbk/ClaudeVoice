using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace ClaudeVoice.Services;

/// <summary>
/// Manages per-session audio queues keyed by badge number (e.g. "1", "2").
/// Each linked terminal gets a badge at link time. Python hooks include the
/// badge in audio filenames (b1_*.txt, b2_*.txt). Only the active badge's
/// queue plays. Switching sessions auto-pauses the current one.
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
    private string _activeSessionId = "";       // badge number as string ("1", "2", …)
    private int  _rate    = 0;
    private bool _enabled = true;

    public string ActiveSessionId  => _activeSessionId;
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
    // (badge, hasPending) — drives the notification badge on terminal rows
    public event Action<string, bool>? SessionPendingChanged;

    public TtsService(FileWatcherService fw)
    {
        _fw = fw;
        _enabled = fw.ReadTtsEnabled();
        _rate    = fw.ReadTtsRate();
        (_edgeTtsFileName, _edgeTtsPrefixArgs) = FindEdgeTts();

        // No session init from file — auto-adopt handles it when first audio arrives

        fw.AudioFileArrived  += OnAudioFileArrived;
        fw.TtsEnabledChanged += v => _enabled = v;
        fw.TtsRateChanged    += v => _rate = v;

        // Clear any stale queue files left over from a previous session.
        foreach (var f in fw.GetQueueFiles())
            fw.DeleteQueueFile(f);

        Task.Run(() => ProcessLoop(_cts.Token));
    }

    // ── Session management ────────────────────────────────────────────────────

    /// <summary>
    /// Switch the active session by badge.
    /// </summary>
    public void SetActiveSession(string badge, int? processId = null, IntPtr windowHandle = default)
    {
        _activeProcessId    = processId ?? 0;
        _activeWindowHandle = windowHandle;

        if (badge == _activeSessionId)
            return;

        // Switch to new session
        _activeSessionId = badge;

        // Cancel current playback so ProcessLoop wakes up and starts the new session
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        SessionPendingChanged?.Invoke(badge, false);
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    public void StopCurrent()
    {
        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue))
            {
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

    }

    // ── Queue management ──────────────────────────────────────────────────────

    private void OnAudioFileArrived(string filePath)
    {
        var badge = ParseBadgeFromFilename(filePath);
        if (string.IsNullOrEmpty(badge))
        {
            _fw.DeleteQueueFile(filePath);
            return;
        }

        // Auto-adopt: if no active session, treat the first badge as active.
        if (string.IsNullOrEmpty(_activeSessionId))
            _activeSessionId = badge;

        // Only accept audio from known badges (active or previously queued).
        // Badges come from claudevoice_badge_{pid}.txt files written by TerminalService,
        // so only linked terminals produce valid badges.
        bool known = badge == _activeSessionId;
        if (!known)
        {
            lock (_queueLock) { known = _sessionQueues.ContainsKey(badge); }
        }
        if (!known)
        {
            _fw.DeleteQueueFile(filePath);
            return;
        }

        lock (_queueLock)
        {
            if (!_sessionQueues.TryGetValue(badge, out var queue))
            {
                queue = new LinkedList<string>();
                _sessionQueues[badge] = queue;
            }
            queue.AddLast(filePath);
        }

        if (badge != _activeSessionId)
            SessionPendingChanged?.Invoke(badge, true);
    }

    /// <summary>
    /// Extracts badge number from filename format "b{N}_{timestamp}.txt".
    /// Returns "" for files that don't match (old format or invalid).
    /// </summary>
    private static string ParseBadgeFromFilename(string path)
    {
        var name  = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        if (parts.Length >= 2 && parts[0].StartsWith('b') && parts[0].Length > 1)
            return parts[0][1..];   // "b1" → "1"
        return "";
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

    // ── Process loop ──────────────────────────────────────────────────────────

    private int _pollCounter = 0;

    private async Task ProcessLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_enabled && TryDequeueForActiveSession(out var filePath))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);

                try { await SpeakFile(filePath, linked.Token); }
                catch (OperationCanceledException) { }
            }
            else
            {
                // Fallback polling: every ~2s, scan queue directory for missed files
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

    private void PollQueueDirectory()
    {
        var filesOnDisk = _fw.GetQueueFiles();
        if (filesOnDisk.Length == 0) return;

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

                while (!ct.IsCancellationRequested &&
                       wave.PlaybackState == PlaybackState.Playing)
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

    public bool HasQueuedItems(string badge)
    {
        lock (_queueLock)
            return _sessionQueues.TryGetValue(badge, out var q) && q.Count > 0;
    }

    public void RemoveSessionQueue(HashSet<string> liveBadges)
    {
        lock (_queueLock)
        {
            var dead = _sessionQueues.Keys.Where(id => !liveBadges.Contains(id)).ToList();
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

    public void ReplayLast()
    {
        if (string.IsNullOrWhiteSpace(_lastText)) return;

        var oldCts = Interlocked.Exchange(ref _stopCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();

        lock (_queueLock)
        {
            if (_sessionQueues.TryGetValue(_activeSessionId, out var queue))
            {
                var node = queue.First;
                while (node != null) { _fw.DeleteQueueFile(node.Value); node = node.Next; }
                queue.Clear();
            }
        }

        // Re-enqueue the last text at the front so ProcessLoop picks it up immediately
        var tmpPath = Path.Combine(Path.GetTempPath(), $"cv_replay_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tmpPath, _lastText);
            lock (_queueLock)
            {
                if (!_sessionQueues.TryGetValue(_activeSessionId, out var q))
                {
                    q = new LinkedList<string>();
                    _sessionQueues[_activeSessionId] = q;
                }
                q.AddFirst(tmpPath);
            }
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string FileName, string[] PrefixArgs) FindEdgeTts()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pythonBase   = Path.Combine(localAppData, "Programs", "Python");

        if (Directory.Exists(pythonBase))
        {
            foreach (var dir in Directory.GetDirectories(pythonBase, "Python3*")
                                         .OrderByDescending(d => d))
            {
                var exe = Path.Combine(dir, "Scripts", "edge-tts.exe");
                if (File.Exists(exe)) return (exe, Array.Empty<string>());
            }
        }

        return ("python", new[] { "-m", "edge_tts" });
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();
        _playLock.Dispose();
        _stopCts.Dispose();
        _cts.Dispose();

        foreach (var f in _fw.GetQueueFiles())
            _fw.DeleteQueueFile(f);
    }
}
