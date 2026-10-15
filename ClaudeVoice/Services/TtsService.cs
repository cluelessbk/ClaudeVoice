using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace ClaudeVoice.Services;

/// <summary>
/// Plays the text files the Python hooks drop into audio_queue/, in order,
/// by turning each into speech with edge-tts and playing the resulting mp3.
/// </summary>
public class TtsService : IDisposable
{
    private const string Voice = "en-GB-RyanNeural";

    private readonly FileWatcherService _fw;
    private readonly CancellationTokenSource _cts = new();   // service lifetime
    private readonly object _queueLock = new();
    private readonly LinkedList<string> _queue = new();

    private volatile WaveOutEvent? _player;
    private volatile Process? _currentEdgeProc;
    // Replaced (and the old one cancelled) on every Stop/Replay. Old ones are
    // deliberately not disposed: the loop may still be reading their Token.
    private CancellationTokenSource _stopCts = new();

    private readonly string   _edgeTtsFileName;
    private readonly string[] _edgeTtsPrefixArgs;

    private string _lastText = "";
    private volatile int  _rate    = 0;
    private volatile bool _enabled = true;

    // true = playback started, false = playback ended
    public event Action<bool>?   PlayingChanged;
    // fired when speech fails — plain-language message for the UI
    public event Action<string>? TtsErrorOccurred;

    public TtsService(FileWatcherService fw)
    {
        _fw = fw;
        _enabled = fw.ReadTtsEnabled();
        _rate    = fw.ReadTtsRate();
        (_edgeTtsFileName, _edgeTtsPrefixArgs) = FindEdgeTts();

        fw.AudioFileArrived  += Enqueue;
        fw.TtsEnabledChanged += v => _enabled = v;
        fw.TtsRateChanged    += v => _rate = v;

        // Clear any stale queue files left over from a previous run.
        foreach (var f in fw.GetQueueFiles())
            fw.DeleteQueueFile(f);

        Task.Run(() => ProcessLoop(_cts.Token));
    }

    // ── Controls ──────────────────────────────────────────────────────────────

    /// <summary>Stops the current speech and drops everything still queued.</summary>
    public void StopCurrent()
    {
        CancelPlayback();

        lock (_queueLock)
        {
            // Remember the newest queued message so Replay can still play it
            if (_queue.Last != null)
            {
                try { _lastText = File.ReadAllText(_queue.Last.Value).Trim(); } catch { }
            }
            ClearQueue();
        }
    }

    /// <summary>Stops whatever is playing and speaks the last message again.</summary>
    public void ReplayLast()
    {
        if (string.IsNullOrWhiteSpace(_lastText)) return;

        CancelPlayback();

        var tmpPath = Path.Combine(Path.GetTempPath(), $"cv_replay_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tmpPath, _lastText);
            lock (_queueLock)
            {
                ClearQueue();
                _queue.AddFirst(tmpPath);
            }
        }
        catch { }
    }

    private void CancelPlayback()
    {
        Interlocked.Exchange(ref _stopCts, new CancellationTokenSource()).Cancel();
        try { _currentEdgeProc?.Kill(entireProcessTree: true); } catch { }
        _player?.Stop();
    }

    // ── Queue ─────────────────────────────────────────────────────────────────

    private void Enqueue(string filePath)
    {
        lock (_queueLock)
        {
            if (!_queue.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                _queue.AddLast(filePath);
        }
    }

    // Caller holds _queueLock
    private void ClearQueue()
    {
        foreach (var f in _queue) _fw.DeleteQueueFile(f);
        _queue.Clear();
    }

    private bool TryDequeue(out string filePath)
    {
        lock (_queueLock)
        {
            if (_queue.First != null)
            {
                filePath = _queue.First.Value;
                _queue.RemoveFirst();
                return true;
            }
        }
        filePath = "";
        return false;
    }

    // ── Process loop ──────────────────────────────────────────────────────────

    private async Task ProcessLoop(CancellationToken ct)
    {
        int pollCounter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_enabled && TryDequeue(out var filePath))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
                    await SpeakFile(filePath, linked.Token);
                    continue;
                }

                // Fallback: every ~2s, pick up files the FileSystemWatcher missed
                if (++pollCounter >= 10)
                {
                    pollCounter = 0;
                    foreach (var f in _fw.GetQueueFiles()) Enqueue(f);
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Never let one bad message kill speech for the rest of the session
                Debug.WriteLine($"[TtsService] Loop error: {ex}");
            }
        }
    }

    private async Task SpeakFile(string filePath, CancellationToken ct)
    {
        string text;
        try   { text = (await File.ReadAllTextAsync(filePath, ct)).Trim(); }
        catch (OperationCanceledException) { throw; }
        catch { return; }
        finally { _fw.DeleteQueueFile(filePath); }

        if (text.Length < 5) return;

        _lastText = text;
        await SpeakText(text, ct);
    }

    private async Task SpeakText(string text, CancellationToken ct)
    {
        var id      = Guid.NewGuid();
        var txtPath = Path.Combine(Path.GetTempPath(), $"cv_{id}.txt");
        var mp3Path = Path.Combine(Path.GetTempPath(), $"cv_{id}.mp3");
        try
        {
            try { await File.WriteAllTextAsync(Path.Combine(_fw.ClaudeDir, "tts_last.txt"), text, ct); }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Text goes via a file: the command line is capped at ~32k characters
            await File.WriteAllTextAsync(txtPath, text, ct);

            var error = await RunEdgeTts(txtPath, mp3Path, ct);
            if (error != null)
            {
                TtsErrorOccurred?.Invoke(error);
                return;
            }

            ct.ThrowIfCancellationRequested();
            await Play(mp3Path, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TtsService] Speak error: {ex}");
            TtsErrorOccurred?.Invoke($"Playback failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(txtPath); } catch { }
            try { File.Delete(mp3Path); } catch { }
        }
    }

    /// <summary>Runs edge-tts. Returns null on success, or a plain-language error.</summary>
    private async Task<string?> RunEdgeTts(string txtPath, string mp3Path, CancellationToken ct)
    {
        // All three streams redirected: ClaudeVoice is a GUI app with no console,
        // so un-redirected stdin/stdout reach Python as None and edge-tts ≥7.3
        // crashes on sys.stdin.isatty().
        var psi = new ProcessStartInfo
        {
            FileName               = _edgeTtsFileName,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var arg in _edgeTtsPrefixArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--voice");
        psi.ArgumentList.Add(Voice);
        psi.ArgumentList.Add(_rate >= 0 ? $"--rate=+{_rate}%" : $"--rate={_rate}%");
        psi.ArgumentList.Add("--file");
        psi.ArgumentList.Add(txtPath);
        psi.ArgumentList.Add("--write-media");
        psi.ArgumentList.Add(mp3Path);

        using var proc = Process.Start(psi);
        if (proc == null) return "Could not start edge-tts.";

        _currentEdgeProc = proc;
        proc.StandardInput.Close();
        string stderr;
        try
        {
            _ = proc.StandardOutput.ReadToEndAsync(ct);   // drain so it can never block
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally { _currentEdgeProc = null; }

        // edge-tts leaves an empty file behind when it fails, so check the size too
        var ok = proc.ExitCode == 0 && File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 0;
        if (ok) return null;

        Debug.WriteLine($"[TtsService] edge-tts failed ({proc.ExitCode}): {stderr}");
        if (stderr.Contains("403"))
            return "Voice service refused (403). Run: pip install -U edge-tts";
        var lastLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                         | StringSplitOptions.TrimEntries).LastOrDefault();
        return $"edge-tts failed: {lastLine ?? $"exit code {proc.ExitCode}"}";
    }

    private async Task Play(string mp3Path, CancellationToken ct)
    {
        PlayingChanged?.Invoke(true);
        try
        {
            using var reader = new Mp3FileReader(mp3Path);
            using var wave   = new WaveOutEvent();
            _player = wave;
            wave.Init(reader);
            wave.Play();

            while (!ct.IsCancellationRequested && wave.PlaybackState == PlaybackState.Playing)
            {
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            wave.Stop();
        }
        finally
        {
            _player = null;
            PlayingChanged?.Invoke(false);
        }
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
        CancelPlayback();
        foreach (var f in _fw.GetQueueFiles())
            _fw.DeleteQueueFile(f);
    }
}
