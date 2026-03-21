using System.IO;

namespace ClaudeVoice.Services;

/// <summary>
/// Watches audio_queue/ for new .txt files written by the Python hooks.
/// Also monitors tts_disabled, tts_rate.txt, and active_session.txt.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly string _claudeDir;
    private readonly string _queueDir;
    private FileSystemWatcher? _queueWatcher;
    private FileSystemWatcher? _flagWatcher;

    public string ClaudeDir => _claudeDir;
    public string QueueDir  => _queueDir;

    public event Action<string>? AudioFileArrived;
    public event Action<bool>?   TtsEnabledChanged;
    public event Action<int>?    TtsRateChanged;
    public event Action<string>? ActiveSessionChanged;

    public FileWatcherService()
    {
        _claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
        _queueDir = Path.Combine(_claudeDir, "audio_queue");
        Directory.CreateDirectory(_queueDir);
    }

    public void Start()
    {
        _queueWatcher = new FileSystemWatcher(_queueDir, "*.txt")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _queueWatcher.Created += (_, e) => AudioFileArrived?.Invoke(e.FullPath);

        _flagWatcher = new FileSystemWatcher(_claudeDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _flagWatcher.Created += OnFlagChanged;
        _flagWatcher.Deleted += OnFlagChanged;
        _flagWatcher.Changed += OnFlagChanged;
    }

    private void OnFlagChanged(object _, FileSystemEventArgs e)
    {
        switch (Path.GetFileName(e.Name))
        {
            case "tts_disabled":
                TtsEnabledChanged?.Invoke(!File.Exists(e.FullPath));
                break;
            case "tts_rate.txt":
                if (TryReadRate(e.FullPath, out var rate))
                    TtsRateChanged?.Invoke(rate);
                break;
            case "active_session.txt":
                // fix #9: small delay before reading — Python may still be writing the file
                Task.Delay(150).ContinueWith(_ =>
                {
                    try
                    {
                        if (File.Exists(e.FullPath))
                            ActiveSessionChanged?.Invoke(File.ReadAllText(e.FullPath).Trim());
                    }
                    catch { }
                });
                break;
        }
    }

    private static bool TryReadRate(string path, out int rate)
    {
        rate = 0;
        try { return int.TryParse(File.ReadAllText(path).Trim(), out rate); }
        catch { return false; }
    }

    // --- State readers (called on startup to sync initial state) ---

    public bool ReadTtsEnabled()
        => !File.Exists(Path.Combine(_claudeDir, "tts_disabled"));

    public int ReadTtsRate()
    {
        var f = Path.Combine(_claudeDir, "tts_rate.txt");
        return TryReadRate(f, out var r) ? r : 0;
    }

    public string ReadActiveSession()
    {
        var f = Path.Combine(_claudeDir, "active_session.txt");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
    }

    public void WriteTtsEnabled(bool enabled)
    {
        var flag = Path.Combine(_claudeDir, "tts_disabled");
        // fix #7: both branches wrapped — write can fail just as easily as delete
        if (enabled) { try { File.Delete(flag); }          catch { } }
        else         { try { File.WriteAllText(flag, ""); } catch { } }
    }

    public void WriteTtsRate(int rate)
        => File.WriteAllText(Path.Combine(_claudeDir, "tts_rate.txt"), rate.ToString());

    public void WriteActiveSession(string transcriptPath)
        => File.WriteAllText(Path.Combine(_claudeDir, "active_session.txt"), transcriptPath);

    public string[] GetQueueFiles()
    {
        try { return Directory.GetFiles(_queueDir, "*.txt"); }
        catch { return []; }
    }

    public void DeleteQueueFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        _queueWatcher?.Dispose();
        _flagWatcher?.Dispose();
    }
}
