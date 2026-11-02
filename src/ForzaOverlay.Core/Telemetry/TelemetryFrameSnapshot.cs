namespace ForzaOverlay.Core.Telemetry;

public sealed class TelemetryFrameSnapshot
{
    public TelemetryFrameSnapshot(TelemetryFrame frame)
    {
        Frame = frame;
    }

    public TelemetryFrame Frame { get; }
}
