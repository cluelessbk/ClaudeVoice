using Jabra.NET.Sdk.Core;
using Jabra.NET.Sdk.Core.Types;
using Jabra.NET.Sdk.Modules.EasyCallControl;
using Jabra.NET.Sdk.Modules.EasyCallControl.Types;

namespace ClaudeVoice.Services;

/// <summary>
/// Registers ClaudeVoice as a Jabra softphone application so the physical
/// mic arm fires MuteState events and the hang-up button fires CallActive.
///
/// Hang-up button solution: with callActive=true, pressing hang-up triggers
/// CallActive→False. We immediately call StartCall() to restore mic arm,
/// wait 300ms for the SDK to settle, then fire HangUpPressed so the app
/// can send Enter to the terminal via PowerShell SendKeys.
/// </summary>
public class JabraService : IDisposable
{
    private IApi?                    _api;
    private IEasyCallControlFactory? _eccFactory;
    private readonly List<IDisposable>        _subscriptions = new();
    private readonly List<ISingleCallControl> _callControls  = new();
    private volatile bool _disposed;

    /// <summary>True if at least one Jabra device was registered successfully.</summary>
    public bool IsInitialized => _callControls.Count > 0;

    /// <summary>Set to the exception message if Init() fails; empty string otherwise.</summary>
    public string InitError { get; private set; } = "";

    /// <summary>Fired when arm goes up (mic muted) → stop recording + transcribe.</summary>
    public event Action? MicMuted;

    /// <summary>Fired when arm goes down (mic unmuted) → start recording.</summary>
    public event Action? MicUnmuted;

    /// <summary>Fired when the hang-up button is pressed on the headset.</summary>
    public event Action? HangUpPressed;

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
                    case MuteState.Muted:   MicMuted?.Invoke();   break;
                    case MuteState.Unmuted: MicUnmuted?.Invoke(); break;
                }
            }));

            _subscriptions.Add(ecc.CallActive.Subscribe(async active =>
            {
                if (_disposed) return;

                if (!active)
                {
                    // Hang-up button pressed — restore call mode so mic arm keeps working
                    try { await ecc.StartCall(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[JabraService] StartCall() failed: {ex.Message}");
                    }

                    // Let SDK finish its own key handling, then fire our event
                    await Task.Delay(300);
                    HangUpPressed?.Invoke();
                }
            }));

            System.Diagnostics.Debug.WriteLine($"[JabraService] Registered device: {device.Name}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JabraService] Device setup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var sub in _subscriptions) sub.Dispose();
        foreach (var cc in _callControls)
        {
            try { _ = cc.Teardown(true); } catch { }
        }
        _subscriptions.Clear();
        _callControls.Clear();
    }
}
