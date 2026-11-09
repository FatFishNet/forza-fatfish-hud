using System.Net;
using System.Net.Sockets;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Logging;
using ForzaOverlay.Core.Monitoring;
using ForzaOverlay.Core.Networking;
using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.App.Services;

public static class AppServices
{
    private static readonly List<UdpTelemetryListener> Listeners = [];

    private static AppSettings? _settings;
    private static ForzaGameKind _activeGame = ForzaGameKind.None;

    public static IReadOnlyList<UdpTelemetryListener> TelemetryListeners => Listeners;

    public static AppSettings? CurrentSettings => _settings;

    public static IntPtr GameWindowHandle { get; private set; }

    public static ForzaGameKind ActiveGame => _activeGame;

    public static long TotalReceived => Listeners.Sum(static listener => listener.PacketsReceived);

    public static long TotalDropped => Listeners.Sum(static listener => listener.PacketsDropped);

    public static void SetGameWindowHandle(IntPtr handle)
    {
        GameWindowHandle = handle;
    }

    public static bool IsPrivilegeSufficient { get; private set; } = true;

    public static event Action? PrivilegeStateChanged;

    public static void EvaluateGamePrivilege(IntPtr gameWindow)
    {
        int processId = ProcessPrivilege.GetProcessIdFromWindow(gameWindow);
        bool sufficient = processId <= 0 || !ProcessPrivilege.IsCurrentLowerThan(processId);

        if (sufficient == IsPrivilegeSufficient)
        {
            return;
        }

        IsPrivilegeSufficient = sufficient;
        Diagnostics.Write(sufficient
            ? "[权限] 本程序与游戏权限相当，快捷键可正常工作"
            : "[权限] 本程序权限低于游戏，全局快捷键会被系统拦截");
        PrivilegeStateChanged?.Invoke();
    }
    public static event Action<AppSettings>? SettingsApplied;

    public static void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        GlobalHotkeyService.TryApply(settings.HotkeyText);
        ApplyActiveEndpoint();
        SettingsApplied?.Invoke(settings);
    }

    public static void SuspendHotkey()
    {
        GlobalHotkeyService.Clear();
        Diagnostics.Write("已暂停全局热键（正在录入快捷键）");
    }

    public static void ResumeHotkey()
    {
        if (_settings is null)
        {
            return;
        }

        bool ok = GlobalHotkeyService.TryApply(_settings.HotkeyText);
        Diagnostics.Write($"已恢复全局热键 {_settings.HotkeyText}：{(ok ? "成功" : "失败")}");
    }

    public static void SetActiveGame(ForzaGameKind game)
    {
        if (_activeGame == game)
        {
            return;
        }

        _activeGame = game;
        Diagnostics.Write($"当前游戏切换为 {game}");
        ApplyActiveEndpoint();
    }

    public static bool TryReadLatest(out TelemetryFrame frame)
    {
        foreach (UdpTelemetryListener listener in Listeners)
        {
            if (listener.TryReadLatest(out frame))
            {
                return true;
            }
        }

        frame = default;
        return false;
    }

    public static void ShutdownTelemetryListener()
    {
        foreach (UdpTelemetryListener listener in Listeners)
        {
            listener.Dispose();
        }

        Listeners.Clear();
    }

    private static void ApplyActiveEndpoint()
    {
        if (_settings is null)
        {
            return;
        }

        RestartTelemetryListener(_settings.TelemetryPort, ParseBindAddress(_settings.TelemetryIp));
    }

    private static IPAddress ParseBindAddress(string? ipText)
    {
        if (!string.IsNullOrWhiteSpace(ipText)
            && IPAddress.TryParse(ipText.Trim(), out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address;
        }

        return IPAddress.Loopback;
    }

    private static void RestartTelemetryListener(int port, IPAddress address)
    {
        if (Listeners.Count == 1 && Listeners[0].Port == port && Listeners[0].BindAddress.Equals(address))
        {
            return;
        }

        ShutdownTelemetryListener();

        if (!IsUsablePort(port))
        {
            Diagnostics.Write($"端口 {port} 无效，未监听");
            return;
        }

        UdpTelemetryListener listener = new(port, address);
        try
        {
            listener.Start();
            Listeners.Add(listener);
            Diagnostics.Write($"UDP {address}:{port} 监听成功");
        }
        catch (Exception ex)
        {
            listener.Dispose();
            Diagnostics.Write($"UDP {address}:{port} 监听失败: {ex.Message}");
        }
    }

    private static bool IsUsablePort(int port)
    {
        return port is >= 1 and <= 65535;
    }
}
