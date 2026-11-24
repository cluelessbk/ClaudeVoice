using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeVoice.Services;

/// <summary>
/// Speaks a Claude desktop-app session without hooks: follows the session's
/// transcript (~/.claude/projects/&lt;project&gt;/&lt;session-id&gt;.jsonl) and writes each
/// new assistant text block to audio_queue/, exactly where the hooks would.
///
/// Each finished text block is appended as its own line, so Claude is heard
/// mid-turn (before each tool call) just like with the PreToolUse hook.
/// The format is internal to Claude Code and may change between versions.
/// </summary>
public class TranscriptReader : IDisposable
{
    private readonly string _queueDir;
    private readonly object _lock = new();
    private Timer?  _timer;
    private string? _path;
    private long    _position;
    private int     _fileCounter;

    public string? TranscriptPath => _path;

    public TranscriptReader(string claudeDir)
    {
        _queueDir = Path.Combine(claudeDir, "audio_queue");
    }

    /// <summary>
    /// Finds the transcript whose latest title equals <paramref name="sessionTitle"/>.
    /// Newest files first; only files written in the last 30 days are checked.
    /// </summary>
    public static string? FindTranscript(string claudeDir, string sessionTitle)
    {
        var projects = Path.Combine(claudeDir, "projects");
        if (!Directory.Exists(projects)) return null;

        var cutoff = DateTime.Now.AddDays(-30);
        var files = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(projects)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly)))
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime >= cutoff)
            .OrderByDescending(f => f.LastWriteTime);

        foreach (var file in files)
            if (LatestTitle(file.FullName) == sessionTitle)
                return file.FullName;
        return null;
    }

    private static string? LatestTitle(string path)
    {
        string? title = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!line.Contains("\"custom-title\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("customTitle", out var t))
                        title = t.GetString()?.Trim();
                }
                catch { }
            }
        }
        catch { }
        return title;
    }

    /// <summary>Starts following the file from its current end (old replies are not spoken).</summary>
    public void Start(string path)
    {
        lock (_lock)
        {
            _path     = path;
            _position = new FileInfo(path).Length;
        }
        _timer?.Dispose();
        _timer = new Timer(_ => Poll(), null, 500, 500);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_lock) { _path = null; }
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(_lock)) return;   // previous poll still running
        try
        {
            if (_path == null) return;
            var length = new FileInfo(_path).Length;
            if (length < _position) _position = length;   // file was rewritten — start over at its end
            if (length == _position) return;

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_position, SeekOrigin.Begin);
            var bytes = new byte[length - _position];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }

            // Only complete lines; a half-written last line is read next time
            int lastNewline = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
            if (lastNewline < 0) return;
            _position += lastNewline + 1;

            var chunk = Encoding.UTF8.GetString(bytes, 0, lastNewline + 1);
            foreach (var line in chunk.Split('\n'))
            {
                var text = AssistantText(line);
                if (text == null) continue;
                var cleaned = CleanForTts(text);
                if (cleaned.Length >= 2) WriteToQueue(cleaned);
            }
        }
        catch { }
        finally { Monitor.Exit(_lock); }
    }

    /// <summary>The text blocks of a main-conversation assistant entry, or null.</summary>
    private static string? AssistantText(string line)
    {
        if (line.Length == 0 || !line.Contains("\"assistant\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var content)) return null;

            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind != JsonValueKind.Array)  return null;

            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                    block.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } s)
                    parts.Add(s.Trim());
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Same queue format as the hooks; written via a temp file so it is never read
    /// half-done. The temp file sits outside audio_queue/ so the move shows up there
    /// as a newly created file (a rename inside the folder wouldn't trigger the watcher).
    /// </summary>
    private void WriteToQueue(string text)
    {
        Directory.CreateDirectory(_queueDir);
        var name = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _fileCounter):D3}";
        var tmp  = Path.Combine(Path.GetDirectoryName(_queueDir)!, $"audio_queue_{name}.tmp");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(_queueDir, name + ".txt"));
    }

    /// <summary>Port of clean_for_tts() in hooks/tts_utils.py — strips markdown so speech sounds natural.</summary>
    internal static string CleanForTts(string text)
    {
        var M = RegexOptions.Multiline;
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        text = Regex.Replace(text, @"`([^`\n]+)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", M);
        text = Regex.Replace(text, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");
        text = Regex.Replace(text, @"_{1,3}([^_\n]+)_{1,3}", "$1");
        text = Regex.Replace(text, @"^[-*_]{3,}\s*$", "", M);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", M);
        text = Regex.Replace(text, @"^\s*\d+\.\s+", "", M);
        text = Regex.Replace(text, @"\|[^\n]+\|", "");
        text = Regex.Replace(text, @"^[-|:\s]+$", "", M);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public void Dispose() => Stop();
}
