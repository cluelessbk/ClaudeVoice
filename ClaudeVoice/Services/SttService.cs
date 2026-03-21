using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace ClaudeVoice.Services;

public enum SttState { Idle, Recording, Transcribing }

/// <summary>
/// Handles microphone capture and Whisper transcription.
/// </summary>
public class SttService : IDisposable
{
    private const string ModelName = "ggml-base.en.bin";

    private readonly string _modelPath;
    private WhisperFactory? _factory;
    private WaveInEvent? _mic;
    private MemoryStream? _audioBuffer;
    private WaveFileWriter? _writer;
    private SttState _state = SttState.Idle;

    // Mic mute monitor — subscribes to both capture endpoint roles because headsets
    // report their mute button through either Communications or Multimedia depending on
    // driver and Windows settings. Shared _lastMuteState deduplicates dual notifications.
    private MMDeviceEnumerator? _deviceEnumerator;
    private readonly List<MMDevice> _micDevices = new();
    private bool _lastMuteState = true;

    public event Action<SttState>? StateChanged;
    public event Action<string>?   Transcribed;
    // Fires when the headset mic is unmuted (start recording) or muted (stop + transcribe).
    // Subscribed by MainWindow, which dispatches to the UI thread.
    public event Action? MicUnmuted;
    public event Action? MicMuted;

    public SttService()
    {
        _modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "models", ModelName);
        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
    }

    public async Task EnsureModelAsync(IProgress<double>? progress = null)
    {
        if (File.Exists(_modelPath)) return;

        // fix #10: download to .tmp, rename on success — avoids corrupt model on interrupted download
        var tmpPath = _modelPath + ".tmp";
        try
        {
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
            using var fs = File.OpenWrite(tmpPath);
            await modelStream.CopyToAsync(fs);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }

        File.Move(tmpPath, _modelPath, overwrite: true);
    }

    public async Task InitAsync()
    {
        await EnsureModelAsync();
        _factory = WhisperFactory.FromPath(_modelPath);
        InitMuteMonitor();
    }

    private void InitMuteMonitor()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            // Subscribe to both — headsets may expose mute through either role
            SubscribeMicEndpoint(Role.Communications);
            SubscribeMicEndpoint(Role.Multimedia);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SttService] MicMuteMonitor init failed: {ex.Message}");
        }
    }

    private void SubscribeMicEndpoint(Role role)
    {
        try
        {
            var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            _lastMuteState = device.AudioEndpointVolume.Mute; // initialise from whichever answers
            device.AudioEndpointVolume.OnVolumeNotification += OnMicVolumeNotification;
            _micDevices.Add(device);
        }
        catch { /* endpoint may not exist on this machine */ }
    }

    private void OnMicVolumeNotification(AudioVolumeNotificationData data)
    {
        if (data.Muted == _lastMuteState) return;
        _lastMuteState = data.Muted;

        if (data.Muted) MicMuted?.Invoke();
        else            MicUnmuted?.Invoke();
    }

    public void StartRecording()
    {
        if (_state != SttState.Idle) return;

        _audioBuffer = new MemoryStream();
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
        _writer = new WaveFileWriter(_audioBuffer, _mic.WaveFormat);

        _mic.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _mic.StartRecording();

        SetState(SttState.Recording);
    }

    public async Task StopRecordingAndTranscribeAsync()
    {
        if (_state != SttState.Recording) return;

        _mic?.StopRecording();
        _mic?.Dispose();
        _mic = null;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        SetState(SttState.Transcribing);

        if (_factory == null || _audioBuffer == null)
        {
            SetState(SttState.Idle);
            return;
        }

        // WaveFileWriter.Dispose() closes the MemoryStream — ToArray() works on closed streams.
        var wavBytes = _audioBuffer.ToArray();
        _audioBuffer.Dispose();
        _audioBuffer = null;

        // WAV header = 44 bytes. 16kHz mono 16-bit audio = 32000 bytes/sec.
        // Require at least ~500ms of audio before bothering Whisper.
        // Shorter clips (from accidental key-repeat) would crash the native library.
        const int MinAudioBytes = 44 + 16000; // header + ~500ms
        if (wavBytes.Length < MinAudioBytes)
        {
            SetState(SttState.Idle);
            return;
        }

        var text = await Task.Run(() =>
        {
            try { return TranscribeWav(wavBytes); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STT] Whisper failed: {ex.Message}");
                return "";
            }
        });
        SetState(SttState.Idle);

        if (!string.IsNullOrWhiteSpace(text))
            Transcribed?.Invoke(text.Trim());
    }

    private string TranscribeWav(byte[] wavBytes)
    {
        if (_factory == null) return "";
        var results = new System.Text.StringBuilder();

        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithSegmentEventHandler(seg => results.Append(seg.Text))
            .Build();

        using var ms = new MemoryStream(wavBytes);
        processor.Process(ms);

        return results.ToString();
    }

    private void SetState(SttState s)
    {
        _state = s;
        StateChanged?.Invoke(s);
    }

    public SttState CurrentState => _state;

    public void Dispose()
    {
        foreach (var device in _micDevices)
        {
            try { device.AudioEndpointVolume.OnVolumeNotification -= OnMicVolumeNotification; } catch { }
            device.Dispose();
        }
        _micDevices.Clear();
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
        _mic?.Dispose();
        _writer?.Dispose();
        _audioBuffer?.Dispose();
        _factory?.Dispose();
    }
}
