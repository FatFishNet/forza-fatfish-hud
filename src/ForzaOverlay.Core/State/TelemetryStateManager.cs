using ForzaOverlay.Core.Logging;

namespace ForzaOverlay.Core.State;

public sealed class TelemetryStateManager
{
    private readonly object _gate = new();
    private bool _isTelemetryEnabled = true;

    public static TelemetryStateManager Instance { get; } = new();

    public event Action<bool>? IsTelemetryEnabledChanged;

    public bool IsTelemetryEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isTelemetryEnabled;
            }
        }
        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _isTelemetryEnabled != value;
                if (changed)
                {
                    _isTelemetryEnabled = value;
                }
            }

            if (changed)
            {
                Diagnostics.Write($"遥测开关: {(value ? "开" : "关")}");
                IsTelemetryEnabledChanged?.Invoke(value);
            }
        }
    }

    public void Toggle()
    {
        bool newValue;
        lock (_gate)
        {
            _isTelemetryEnabled = !_isTelemetryEnabled;
            newValue = _isTelemetryEnabled;
        }

        Diagnostics.Write($"遥测开关(快捷键): {(newValue ? "开" : "关")}");
        IsTelemetryEnabledChanged?.Invoke(newValue);
    }

    private TelemetryStateManager()
    {
    }
}
