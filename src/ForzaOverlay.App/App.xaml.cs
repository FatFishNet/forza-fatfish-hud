using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
using ForzaOverlay.App.Rendering;
using ForzaOverlay.App.Services;
using ForzaOverlay.App.Ui;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Logging;
using ForzaOverlay.Core.Monitoring;
using ForzaOverlay.Core.State;
using ForzaOverlay.Core.Telemetry;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace ForzaOverlay.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application has no Dispose entry point; every disposable field is released in OnExit.")]
public partial class App : Application
{
    private GameProcessMonitor? _gameProcessMonitor;
    private GpuOverlayWindow? _gpuOverlay;
    private HudScene? _gpuScene;
    private DispatcherTimer? _gpuVisibilityTimer;
    private bool _gpuDriving;
    private double _gpuSpeedKmh;
    private bool _gpuSpeedGateOpen;
    private bool? _gpuLastVisible;
    private bool? _gpuGameFullscreen;
    private (int X, int Y, int Width, int Height)? _gpuViewport;
    private SettingsWindow? _settingsWindow;
    private OverspeedSoundPlayer? _overspeedSound;
    private Mutex? _instanceMutex;

    private const int SwRestore = 9;
    private const int GwlStyle = -16;
    private const long WsOverlappedWindow = 0x00CF0000;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.WriteException("UI 线程未处理异常", e.Exception);
        e.Handled = true;

        try
        {
            MessageBox.Show(
                "程序遇到错误，已记录到 logs 目录。" + Environment.NewLine + Environment.NewLine + e.Exception.Message,
                "Forza Fat Fish HUD",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
        }

        Shutdown();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Diagnostics.WriteException("后台线程未处理异常", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Diagnostics.WriteException("未观察的任务异常", e.Exception);
        e.SetObserved();
    }
    public static void RestartAsElevated()
    {
        if (Current is not App app)
        {
            return;
        }

        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 先释放单实例互斥，否则新实例会以为已有进程在运行而退出
        app._instanceMutex?.Dispose();
        app._instanceMutex = null;

        try
        {
            ProcessStartInfo startInfo = new(path)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            Diagnostics.Write("[权限] 正在以管理员身份重启");
            using Process? started = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Diagnostics.WriteException("以管理员身份重启失败", exception);
            return;
        }

        app.Shutdown();
    }
    private static void ActivateExistingWindow()
    {
        using Process current = Process.GetCurrentProcess();

        foreach (Process process in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                _ = ShowWindow(handle, SwRestore);
                _ = SetForegroundWindow(handle);
                return;
            }
            catch (Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\ForzaFatFishHUD.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        Diagnostics.BeginRun();
        RegisterCrashHandlers();

        AppSettings settings = SettingsStore.Load();
        Diagnostics.Write($"配置: 端口={settings.TelemetryPort} 热键={settings.HotkeyText} 始终显示={settings.AlwaysShowHud}");
        Diagnostics.Write($"目标框架目录: {AppContext.BaseDirectory}");
        if (AppStartupValidator.IsValid(settings))
        {
            AppServices.ApplySettings(settings);
        }
        else
        {
            Diagnostics.Write("配置校验失败，未应用任何设置");
        }

        GlobalHotkeyService.ToggleHudRequested += OnToggleHudRequested;

        _gpuOverlay = new GpuOverlayWindow();
        _gpuScene = new HudScene(
            (float)SystemParameters.PrimaryScreenWidth,
            (float)SystemParameters.PrimaryScreenHeight);
        _gpuOverlay.Render += _gpuScene.Render;

        if (_gpuOverlay.Start())
        {
            Diagnostics.Write("[启动] GPU 覆盖层已创建（D3D11 + Direct2D + DirectComposition）");

            _gpuVisibilityTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _gpuVisibilityTimer.Tick += OnGpuVisibilityTick;
            _gpuVisibilityTimer.Start();

            _gpuOverlay.IdleTick += PumpGpuPacket;
            _gpuOverlay.HasNewPacket += _gpuScene.PumpPacket;

            TelemetryStateManager.Instance.IsTelemetryEnabledChanged += OnGpuTelemetryEnabledChanged;
            ApplyGpuRefreshMode(settings.HudRefreshRate);
            UpdateGpuVisibility();
        }
        else
        {
            _gpuOverlay.Dispose();
            _gpuOverlay = null;
            _gpuScene = null;
            Diagnostics.Write("[启动] GPU 覆盖层启动失败，HUD 不可用");
        }

        _gameProcessMonitor = new GameProcessMonitor();
        _gameProcessMonitor.GameWindowHandleChanged += OnGameWindowHandleChanged;
        _gameProcessMonitor.Start();

        PerfRecorder.SetEnvironment(BuildPerfEnvironment(settings));
        PerfRecorder.SetSystemGpuSampler(PerformanceMonitor.CaptureSystemGpu);
        PerfRecorder.Configure(settings.PerfLogEnabled);

        _overspeedSound = new OverspeedSoundPlayer();
        OverspeedSoundPlayer.Shared = _overspeedSound;

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Show();

        bool hotkeyApplied = GlobalHotkeyService.TryApply(settings.HotkeyText);
        Diagnostics.Write($"热键注册 {settings.HotkeyText}：{(hotkeyApplied ? "成功" : "失败")}");

        Diagnostics.Write("[启动] 设置窗口已显示，启动完成");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Diagnostics.Write("[退出] 程序正常退出");
        if (_gpuOverlay is not null && _gpuScene is not null)
        {
            _gpuOverlay.IdleTick -= PumpGpuPacket;
            _gpuOverlay.HasNewPacket -= _gpuScene.PumpPacket;
        }

        TelemetryStateManager.Instance.IsTelemetryEnabledChanged -= OnGpuTelemetryEnabledChanged;
        _gpuVisibilityTimer?.Stop();
        _gpuVisibilityTimer = null;
        _gpuScene?.Dispose();
        _gpuOverlay?.Dispose();
        if (_gameProcessMonitor is not null)
        {
            _gameProcessMonitor.GameWindowHandleChanged -= OnGameWindowHandleChanged;
            _gameProcessMonitor.Dispose();
        }
        GlobalHotkeyService.ToggleHudRequested -= OnToggleHudRequested;
        GlobalHotkeyService.Clear();
        _overspeedSound?.Dispose();
        _overspeedSound = null;
        PerfRecorder.Shutdown();
        AppServices.ShutdownTelemetryListener();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void PumpGpuPacket()
    {
        _gpuScene?.PumpPacket();
    }

    private string BuildPerfEnvironment(AppSettings settings)
    {
        string version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        string gpu = _gpuOverlay?.AdapterName ?? "unknown";
        string screen = ((int)SystemParameters.PrimaryScreenWidth).ToString(CultureInfo.InvariantCulture)
            + "x"
            + ((int)SystemParameters.PrimaryScreenHeight).ToString(CultureInfo.InvariantCulture);

        return "ver=" + version
            + ";os=" + Environment.OSVersion.VersionString
            + ";cpu_cores=" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
            + ";gpu=" + gpu
            + ";screen=" + screen
            + ";refresh_mode=" + settings.HudRefreshRate.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyGpuRefreshMode(int mode)
    {
        if (_gpuOverlay is null)
        {
            return;
        }

        switch (mode)
        {
            case 0:
                _gpuOverlay.SetRefreshMode(33, false);
                break;
            case 2:
                _gpuOverlay.SetRefreshMode(0, true);
                break;
            default:
                _gpuOverlay.SetRefreshMode(16, false);
                break;
        }
    }

    private void OnGpuTelemetryEnabledChanged(bool isEnabled)
    {
        Dispatcher.BeginInvoke(UpdateGpuVisibility);
    }

    private void OnGpuVisibilityTick(object? sender, EventArgs e)
    {
        UpdateGpuVisibility();
    }

    private void UpdateGpuVisibility()
    {
        if (_gpuOverlay is null)
        {
            return;
        }

        ApplyGpuRefreshMode(AppServices.CurrentSettings?.HudRefreshRate ?? 1);

        bool hasPacket = false;
        ForzaDataPacket packet = default;
        if (_gpuScene is not null)
        {
            hasPacket = _gpuScene.TryGetLatest(out packet);
        }

        _gpuDriving = hasPacket && packet.RaceOn;
        _gpuSpeedKmh = hasPacket ? packet.SpeedKmh : 0;

        _gpuSpeedGateOpen = _gpuSpeedGateOpen
            ? _gpuSpeedKmh > 2.0
            : _gpuSpeedKmh > 4.0;

        bool show = HudVisibilityRules.Evaluate(
            AppServices.GameWindowHandle,
            _gpuDriving,
            _gpuSpeedKmh,
            _gpuSpeedGateOpen,
            out string reason);

        TryUpdateGpuViewport(AppServices.GameWindowHandle);

        UpdateGameWindowModeLog(AppServices.GameWindowHandle);

        _gpuOverlay.SetVisible(show);

        if (_gpuLastVisible != show)
        {
            _gpuLastVisible = show;
            Diagnostics.Write($"GPU HUD {(show ? "显示" : "隐藏")}：{reason}");
        }
    }

    private void UpdateGameWindowModeLog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = GetWindowLongPtrW(hwnd, GwlStyle).ToInt64();
        bool fullscreen = (style & WsOverlappedWindow) == 0;

        if (_gpuGameFullscreen != fullscreen)
        {
            _gpuGameFullscreen = fullscreen;
            Diagnostics.Write($"游戏窗口模式: {(fullscreen ? "全屏" : "窗口化")}");
        }
    }

    private void TryUpdateGpuViewport(IntPtr hwnd)
    {
        if (_gpuScene is null || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetClientRect(hwnd, out RECT rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return;
        }

        POINT origin = default;
        if (!ClientToScreen(hwnd, ref origin))
        {
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        _gpuScene.SetViewport(origin.X, origin.Y, width, height);

        (int X, int Y, int Width, int Height) current = (origin.X, origin.Y, width, height);
        if (_gpuViewport != current)
        {
            _gpuViewport = current;
            Diagnostics.Write($"GPU 视口: x={origin.X} y={origin.Y} w={width} h={height}");
        }
    }

    private static void OnToggleHudRequested()
    {
        TelemetryStateManager.Instance.Toggle();
    }

    private void OnGameWindowHandleChanged(IntPtr hwnd, ForzaGameKind kind)
    {
        Diagnostics.Write($"识别结果: 游戏={kind} 句柄=0x{hwnd:X}");
        AppServices.SetGameWindowHandle(hwnd);
        AppServices.SetActiveGame(kind);
        AppServices.EvaluateGamePrivilege(hwnd);
        TryUpdateGpuViewport(hwnd);
    }

}
