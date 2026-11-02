namespace ForzaOverlay.Core.Telemetry;

public readonly record struct TelemetryFrame(ForzaDataPacket Packet, long ReceivedTickMs);
