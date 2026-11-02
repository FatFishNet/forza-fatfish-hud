using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.Core.Aggregation;

public sealed class LatestTelemetryBuffer
{
    private sealed class FrameBox
    {
        public FrameBox(TelemetryFrame frame)
        {
            Frame = frame;
        }

        public TelemetryFrame Frame { get; }
    }

    private FrameBox? _box;

    public void Publish(in TelemetryFrame frame)
    {
        Volatile.Write(ref _box, new FrameBox(frame));
    }

    public bool TryRead(out TelemetryFrame frame)
    {
        FrameBox? box = Volatile.Read(ref _box);
        if (box is null)
        {
            frame = default;
            return false;
        }

        frame = box.Frame;
        return true;
    }
}
