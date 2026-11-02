using System.Diagnostics;
using System.Runtime.InteropServices;
using ForzaOverlay.Core.Logging;
using ForzaOverlay.Core.State;

namespace ForzaOverlay.Core.Monitoring;

public sealed class GameProcessMonitor : IDisposable
{
    private static readonly (string Identifier, ForzaGameKind Kind)[] Games =
    [
        ("ForzaHorizon4", ForzaGameKind.ForzaHorizon4),
        ("ForzaHorizon5", ForzaGameKind.ForzaHorizon5),
        ("ForzaHorizon6", ForzaGameKind.ForzaHorizon6),
    ];

    private const int PollIntervalMs = 10000;
    private const int HandleRetryAttempts = 2;
    private const int HandleRetryDelayMs = 200;

    private readonly TelemetryStateManager _stateManager;
    private readonly object _startGate = new();
    private Timer? _timer;
    private int _polling;
    private bool _wasGameRunningLastCheck;
    private IntPtr _gameWindowHandle;
    private ForzaGameKind _gameKind;

    public event Action<IntPtr, ForzaGameKind>? GameWindowHandleChanged;

    public GameProcessMonitor(TelemetryStateManager? stateManager = null)
    {
        _stateManager = stateManager ?? TelemetryStateManager.Instance;
    }

    public IntPtr GameWindowHandle => _gameWindowHandle;

    public void Start()
    {
        lock (_startGate)
        {
            if (_timer is not null)
            {
                return;
            }
            _timer = new Timer(OnPoll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(PollIntervalMs));
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_startGate)
        {
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    private void OnPoll(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            bool isGameRunning = CheckGameRunning();

            if (isGameRunning && !_wasGameRunningLastCheck)
            {
                _stateManager.IsTelemetryEnabled = true;
            }
            else if (!isGameRunning && _wasGameRunningLastCheck)
            {
                _stateManager.IsTelemetryEnabled = false;
            }

            _wasGameRunningLastCheck = isGameRunning;

            RefreshGameWindowHandle(isGameRunning);
            Diagnostics.Write($"扫描: 运行中={isGameRunning} 类型={_gameKind}");
        }
        catch (Exception)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void RefreshGameWindowHandle(bool isGameRunning)
    {
        (IntPtr handle, ForzaGameKind kind) = isGameRunning
            ? CaptureGameWindow()
            : (IntPtr.Zero, ForzaGameKind.None);

        if (handle == _gameWindowHandle && kind == _gameKind)
        {
            return;
        }

        _gameWindowHandle = handle;
        _gameKind = kind;
        GameWindowHandleChanged?.Invoke(handle, kind);
    }

    private static (IntPtr Handle, ForzaGameKind Kind) CaptureGameWindow()
    {
        (IntPtr handle, ForzaGameKind kind) = FindByWindowEnumeration();
        if (handle != IntPtr.Zero)
        {
            return (handle, kind);
        }

        (handle, kind) = FindByProcessName();
        if (handle != IntPtr.Zero)
        {
            return (handle, kind);
        }

        return FindByWindowTitle();
    }

    private static (IntPtr Handle, ForzaGameKind Kind) FindByWindowEnumeration()
    {
        (IntPtr handle, ForzaGameKind kind) found = (IntPtr.Zero, ForzaGameKind.None);

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return true;
            }

            char[] buffer = new char[length + 1];
            if (GetWindowText(hwnd, buffer, buffer.Length) <= 0)
            {
                return true;
            }

            string title = new string(buffer).TrimEnd('\0');
            ForzaGameKind matched = MatchTitle(title);
            if (matched == ForzaGameKind.None)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            found = (hwnd, matched);
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static ForzaGameKind MatchTitle(string title)
    {
        string haystack = title.Replace(" ", string.Empty);
        foreach (var (identifier, gameKind) in Games)
        {
            if (haystack.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            {
                return gameKind;
            }
        }

        return ForzaGameKind.None;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static (IntPtr Handle, ForzaGameKind Kind) FindByProcessName()
    {
        foreach (var (identifier, gameKind) in Games)
        {
            Process[]? processes = null;
            try
            {
                processes = Process.GetProcessesByName(identifier);
                if (processes.Length > 0)
                {
                    Diagnostics.Write($"发现同名进程: {identifier}，数量={processes.Length}");
                }

                foreach (Process process in processes)
                {
                    IntPtr handle = ReadMainWindowHandleWithRetry(process);
                    Diagnostics.Write($"进程 {identifier} 主窗口句柄={(handle == IntPtr.Zero ? "0" : $"0x{handle:X}")}");
                    if (handle != IntPtr.Zero)
                    {
                        return (handle, gameKind);
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (processes is not null)
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        return (IntPtr.Zero, ForzaGameKind.None);
    }

    private static (IntPtr Handle, ForzaGameKind Kind) FindByWindowTitle()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return (IntPtr.Zero, ForzaGameKind.None);
        }

        try
        {
            Diagnostics.Write("进入窗口标题扫描（全进程枚举）");
            foreach (Process process in processes)
            {
                ForzaGameKind kind = MatchGameKeywords(process);
                if (kind == ForzaGameKind.None)
                {
                    continue;
                }

                Diagnostics.Write($"窗口标题匹配: 进程={process.ProcessName} 类型={kind}");
                IntPtr handle = ReadMainWindowHandle(process);
                if (handle != IntPtr.Zero)
                {
                    return (handle, kind);
                }
            }

            return (IntPtr.Zero, ForzaGameKind.None);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static IntPtr ReadMainWindowHandleWithRetry(Process process)
    {
        IntPtr handle = ReadMainWindowHandle(process);
        for (int attempt = 1; handle == IntPtr.Zero && attempt < HandleRetryAttempts; attempt++)
        {
            Thread.Sleep(HandleRetryDelayMs);
            handle = ReadMainWindowHandle(process);
        }

        return handle;
    }

    private static IntPtr ReadMainWindowHandle(Process process)
    {
        try
        {
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static bool CheckGameRunning()
    {
        if (AnyNamedGameProcess())
        {
            return true;
        }

        if (FindByWindowEnumeration().Handle != IntPtr.Zero)
        {
            return true;
        }

        return AnyWindowTitledGameProcess();
    }

    private static bool AnyNamedGameProcess()
    {
        foreach (var (identifier, _) in Games)
        {
            Process[]? processes = null;
            try
            {
                processes = Process.GetProcessesByName(identifier);
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (processes is not null)
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        return false;
    }

    private static bool AnyWindowTitledGameProcess()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            foreach (Process process in processes)
            {
                if (MatchGameKeywords(process) != ForzaGameKind.None)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static ForzaGameKind MatchGameKeywords(Process process)
    {
        try
        {
            string processName = process.ProcessName;
            string windowTitle;
            try
            {
                windowTitle = process.MainWindowTitle;
            }
            catch (Exception)
            {
                windowTitle = string.Empty;
            }

            string haystack = string.Concat(processName, " ", windowTitle).Replace(" ", string.Empty);
            foreach (var (identifier, gameKind) in Games)
            {
                if (haystack.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    return gameKind;
                }
            }
        }
        catch (Exception)
        {
        }

        return ForzaGameKind.None;
    }
}
