using System.Runtime.InteropServices;

namespace ForzaOverlay.Core.Telemetry;

public static class TelemetryPacketParser
{
    public static bool TryParse(ReadOnlySpan<byte> buffer, long receivedTickMs, out TelemetryFrame frame)
    {
        if (buffer.Length != ForzaDataPacket.PacketSize)
        {
            frame = default;
            return false;
        }

        ForzaDataPacket packet;
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                packet = Marshal.PtrToStructure<ForzaDataPacket>((IntPtr)ptr);
            }
        }

        frame = new TelemetryFrame(packet, receivedTickMs);
        return true;
    }
}
