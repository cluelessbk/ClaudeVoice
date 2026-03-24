using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ClaudeVoice.Models;

public class TerminalSession : INotifyPropertyChanged
{
    // fix #13: static frozen brushes — avoids allocating a new brush on every property change notification
    private static readonly Brush ActiveBrush;
    private static readonly Brush InactiveBrush = Brushes.Transparent;

    static TerminalSession()
    {
        var b = new SolidColorBrush(Color.FromRgb(15, 52, 96));
        b.Freeze();
        ActiveBrush = b;
    }

    private bool _isActive;
    private bool _hasPending;
    private string _displayName = "";

    public string SessionId      { get; set; } = "";
    public int    Badge          { get; set; }         // stable number assigned at link time (1, 2, 3…)
    public string TranscriptPath { get; set; } = "";
    public int?   ProcessId      { get; set; }        // terminal PID — null if discovered via transcript scan
    public IntPtr WindowHandle   { get; set; }        // HWND captured at link time for display name refresh

    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(RowBackground)); }
    }

    public bool HasPending
    {
        get => _hasPending;
        set { _hasPending = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); }
    }

    public string StatusIcon => HasPending ? "🔔" : (IsActive ? "●" : "○");

    public Brush RowBackground => _isActive ? ActiveBrush : InactiveBrush;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
