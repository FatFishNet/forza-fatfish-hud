using System.Net;
using System.Net.Sockets;
using ForzaOverlay.Core.Configuration;

namespace ForzaOverlay.App.Services;

public static class AppStartupValidator
{
    public static bool IsValid(AppSettings settings)
    {
        if (!HotkeyGesture.TryParse(settings.HotkeyText, out _, out _))
        {
            return false;
        }

        return IsEndpointValid(settings.TelemetryIp, settings.TelemetryPort);
    }

    private static bool IsEndpointValid(string? ipText, int port)
    {
        return IsIpValid(ipText) && port is >= 1 and <= 65535;
    }

    private static bool IsIpValid(string? ipText)
    {
        string text = ipText?.Trim() ?? string.Empty;
        return IPAddress.TryParse(text, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && text.Split('.').Length == 4;
    }
}
