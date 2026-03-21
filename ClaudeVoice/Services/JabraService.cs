using Jabra.NET.Sdk.Core;
using Jabra.NET.Sdk.Core.Types;
using Jabra.NET.Sdk.Modules.EasyCallControl;
using Jabra.NET.Sdk.Modules.EasyCallControl.Types;

namespace ClaudeVoice.Services;

/// <summary>
/// Registers ClaudeVoice as a Jabra softphone application so the physical
/// mic arm and play/answer button fire events directly into the app —
/// exactly like Teams does.
///
/// Play button behaviour:
///   The Jabra play button only responds when the headset is in "incoming call" state.
///   NotifyPending() triggers that state — rings for 10s, silence for 50s, repeats
///   up to 5 times (~5 min total). Pressing the button fires PlayButtonPressed and
///   cancels the loop. Call CancelPending() to stop the loop without a button press.
/// </summary>
public class JabraService : IDisposable
{
    private IApi?                    _api;
    private IEasyCallControlFactory? _eccFactory;
    private readonly List<IDisposable>        _subscriptions = new();
    private readonly List<ISingleCallControl> _callControls  = new();
    private CancellationTokenSource?          _notifyCts;
    private volatile bool _disposed;

    /// <summary>True if at least one Jabra device was registered successfully.</summary>
    public bool IsInitialized => _callControls.Count > 0;

    /// <summary>Set to the exception message if Init() fails; empty string otherwise.</summary>
    public string InitError { get; private set; } = "";

    /// <summary>Fired when arm goes up (mic muted) → stop recording + transcribe.</summary>
    public event Action? MicMuted;

    /// <summary>Fired when arm goes down (mic unmuted) → start recording.</summary>
    public event Action? MicUnmuted;

    /// <summary>
    /// Fired when user presses the play/answer button during a pending notification.
    /// Caller applies priority: submit pending → send Enter; else cycle session.
    /// </summary>
    public event Action? PlayButtonPressed;

    public void Init()
    {
        try
        {
            _api = Jabra.NET.Sdk.Core.Init.InitSdk(new Config(
                partnerKey: "",
                appId:      "claudevoice",
                appName:    "Claude Voice"
            ));

            _eccFactory = new EasyCallControlFactory(_api);

            foreach (var device in _api.CurrentDevices)
                TrySetupDevice(device);

            _subscriptions.Add(_api.DeviceAdded.Subscribe(TrySetupDevice));
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[JabraService] Init failed: {ex.Message}");
        }
    }

    private async void TrySetupDevice(IDevice device)
    {
        try
        {
            if (_eccFactory == null) return;

            var ecc = await _eccFactory.CreateSingleCallControl(
                device,
                new SingleInitialState(callActive: true, isMuted: false));

            _callControls.Add(ecc);

            _subscriptions.Add(ecc.MuteState.Subscribe(state =>
            {
                if (_disposed) return;
                switch (state)
                {
                    case MuteState.Muted:   MicMuted?.Invoke();   break;  // arm UP
                    case MuteState.Unmuted: MicUnmuted?.Invoke(); break;  // arm DOWN
                }
            }));

            System.Diagnostics.Debug.WriteLine($"[JabraService] Registered device: {device.Name}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JabraService] Device setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the notify loop: rings headset for 10 s, silent for 50 s, up to 5 cycles (~5 min).
    /// If the user presses the play button during any ring window, PlayButtonPressed fires
    /// and the loop stops. Cancels any previously running loop first.
    /// Safe to call when no Jabra device is connected.
    /// </summary>
    public void NotifyPending()
    {
        if (_disposed || _callControls.Count == 0) return;

        _notifyCts?.Cancel();
        _notifyCts = new CancellationTokenSource();
        var ct = _notifyCts.Token;

        _ = RunNotifyLoop(ct);
    }

    /// <summary>Stops any in-progress notify loop without firing PlayButtonPressed.</summary>
    public void CancelPending()
    {
        _notifyCts?.Cancel();
        _notifyCts = null;
    }

    private async Task RunNotifyLoop(CancellationToken ct)
    {
        // 5 cycles: 10 s ring + 50 s silence = 60 s per cycle ≈ 5 min total
        const int Cycles    = 5;
        const int RingMs    = 10_000;
        const int SilenceMs = 50_000;

        for (int i = 0; i < Cycles; i++)
        {
            if (ct.IsCancellationRequested) return;

            // Ring all connected devices simultaneously; first button press wins
            var ringTasks = _callControls
                .Select(ecc => RingDevice(ecc, RingMs, ct))
                .ToArray();

            bool accepted = (await Task.WhenAll(ringTasks)).Any(r => r);

            if (ct.IsCancellationRequested) return;

            if (accepted)
            {
                PlayButtonPressed?.Invoke();
                return;
            }

            // Silence period before next ring (skip after last cycle)
            if (i < Cycles - 1)
            {
                try { await Task.Delay(SilenceMs, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<bool> RingDevice(ISingleCallControl ecc, int ringMs, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return false;
            return await ecc.SignalIncomingCall(ringMs);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JabraService] SignalIncomingCall failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _notifyCts?.Cancel();
        foreach (var sub in _subscriptions) sub.Dispose();
        foreach (var cc in _callControls)
        {
            try { _ = cc.Teardown(true); } catch { }
        }
        _subscriptions.Clear();
        _callControls.Clear();
    }
}
