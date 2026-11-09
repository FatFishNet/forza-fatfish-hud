using System.Diagnostics;
using System.Runtime.InteropServices;
using ForzaOverlay.Core.Logging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 基于 D3D11 + Direct2D + DirectComposition 的透明覆盖窗口。
/// 绘制全部发生在 GPU 上，避免 WPF AllowsTransparency 的软件渲染瓶颈。
/// </summary>
public sealed class GpuOverlayWindow : IDisposable
{
    private const int PrimaryScreenWidth = 0;
    private const int PrimaryScreenHeight = 1;

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolwindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly WndProcDelegate _wndProc;
    private readonly object _sync = new();

    private Thread? _messageThread;
    private Thread? _renderThread;
    private volatile bool _running;
    private IntPtr _hwnd;
    private int _width;
    private int _height;

    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _context;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1RenderTarget? _d2dRenderTarget;

    private volatile bool _visible;
    private volatile bool _followPacketRate;
    private volatile int _refreshIntervalMs = 16;
    private IntPtr _waitTimer;
    private bool _endDrawErrorLogged;
    private double _fps;
    private double _lastDrawMs;
    private double _lastPresentMs;

    public double Fps => _fps;

    /// <summary>初始化时选中的显卡名称，写入性能日志的环境头。</summary>
    public string AdapterName { get; private set; } = "unknown";

    /// <summary>每帧回调，参数为 D2D 设备上下文与帧间隔秒数。回调在渲染线程执行。</summary>
    public event Action<ID2D1RenderTarget, double>? Render;

    /// <summary>窗口隐藏时的空闲回调，用于继续拉取遥测数据（不绘制）。</summary>
    public event Action? IdleTick;

    /// <summary>每帧调用，返回是否收到新的遥测包；用于「以游戏发包为准」模式。</summary>
    public event Func<bool>? HasNewPacket;

    public void SetRefreshMode(int intervalMs, bool followPacketRate)
    {
        _refreshIntervalMs = Math.Max(0, intervalMs);
        _followPacketRate = followPacketRate;
    }

    public GpuOverlayWindow()
    {
        _wndProc = WndProc;
    }

    public bool Start()
    {
        _waitTimer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x00000002, 0x1F0003);

        _width = GetSystemMetrics(PrimaryScreenWidth);
        _height = GetSystemMetrics(PrimaryScreenHeight);

        WNDCLASSEXW wc = new()
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0020 | 0x0002 | 0x0001,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = IntPtr.Zero,
            lpszClassName = "ForzaOverlayGpuWindow",
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            return false;
        }

        _hwnd = CreateWindowExW(
            WsExNoRedirectionBitmap | WsExLayered | WsExTopmost | WsExTransparent | WsExNoActivate | WsExToolwindow,
            "ForzaOverlayGpuWindow",
            "ForzaOverlay GPU",
            WsPopup,
            0,
            0,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!InitializeD3D())
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }

        _running = true;

        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "ForzaOverlay.GpuMessage",
        };
        _messageThread.Start();

        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "ForzaOverlay.GpuRender",
        };
        _renderThread.Start();

        return true;
    }

    private void MessageLoop()
    {
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private void WaitFor(double seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        if (_waitTimer == IntPtr.Zero)
        {
            Thread.Sleep((int)Math.Max(1, seconds * 1000));
            return;
        }

        long due = -(long)(seconds * 10_000_000);
        if (SetWaitableTimerEx(_waitTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            _ = WaitForSingleObject(_waitTimer, 0xFFFFFFFF);
        }
        else
        {
            Thread.Sleep((int)Math.Max(1, seconds * 1000));
        }
    }

    private void RenderLoop()
    {
        Stopwatch clock = Stopwatch.StartNew();
        double previous = 0;
        double lastRender = 0;
        int frames = 0;
        double fpsAccumulator = 0;

        while (_running)
        {
            if (!_visible)
            {
                IdleTick?.Invoke();
                Thread.Sleep(10);
                continue;
            }

            double now = clock.Elapsed.TotalSeconds;
            bool hasNewPacket = HasNewPacket?.Invoke() ?? false;

            if (_followPacketRate)
            {
                if (!hasNewPacket)
                {
                    WaitFor(0.002);
                    continue;
                }
            }
            else
            {
                double target = _refreshIntervalMs <= 16 ? 0.0165 : 0.0330;
                double wait = target - (now - lastRender);
                if (wait > 0.0005)
                {
                    WaitFor(wait);
                    continue;
                }
            }

            double delta = now - previous;
            previous = now;
            lastRender = now;

            try
            {
                RenderFrame(delta);
                ForzaOverlay.App.Services.PerformanceMonitor.Tick();
                PerfRecorder.OnFrame(delta * 1000.0, _lastDrawMs, _lastPresentMs, _visible);
                frames++;
            }
            catch (Exception ex)
            {
                ForzaOverlay.Core.Logging.Diagnostics.Write("GPU 渲染帧异常: " + ex);
                Thread.Sleep(200);
            }

            fpsAccumulator += delta;
            if (fpsAccumulator >= 1.0)
            {
                _fps = frames / fpsAccumulator;
                frames = 0;
                fpsAccumulator = 0;
            }
        }
    }

    private bool InitializeD3D()
    {
        try
        {
            using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

            using IDXGIAdapter1 adapter = GetHardwareAdapter(factory);
            AdapterName = adapter.Description1.Description;
            Vortice.Direct3D.FeatureLevel[] featureLevels =
            [
                (Vortice.Direct3D.FeatureLevel)45312, // 11.1
                (Vortice.Direct3D.FeatureLevel)45056, // 11.0
                (Vortice.Direct3D.FeatureLevel)41216, // 10.1
                (Vortice.Direct3D.FeatureLevel)40960, // 10.0
            ];

            var result = D3D11.D3D11CreateDevice(
                adapter,
                Vortice.Direct3D.DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out ID3D11Device rawDevice,
                out Vortice.Direct3D.FeatureLevel _,
                out ID3D11DeviceContext rawContext);

            if (result.Failure)
            {
                ForzaOverlay.Core.Logging.Diagnostics.Write("GPU 覆盖层初始化失败: D3D11CreateDevice 返回 " + result);
                return false;
            }

            _device = rawDevice.QueryInterface<ID3D11Device1>();
            _context = rawContext.QueryInterface<ID3D11DeviceContext1>();
            rawDevice.Dispose();
            rawContext.Dispose();

            SwapChainDescription1 description = new()
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };

            _swapChain = factory.CreateSwapChainForComposition(_device, description);

            using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _compositionDevice.CreateTargetForHwnd(_hwnd, true, out _compositionTarget);

            _compositionVisual = _compositionDevice.CreateVisual();
            _compositionVisual.SetContent(_swapChain);
            _compositionTarget.SetRoot(_compositionVisual);
            _compositionDevice.Commit();

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();

            using (IDXGISurface surface = _swapChain.GetBuffer<IDXGISurface>(0))
            {
                _d2dRenderTarget = _d2dFactory.CreateDxgiSurfaceRenderTarget(
                    surface,
                    new RenderTargetProperties
                    {
                        PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                        Type = RenderTargetType.Hardware,
                    });

                _d2dRenderTarget.TextAntialiasMode = TextAntialiasMode.Grayscale;
            }

            return true;
        }
        catch (Exception ex)
        {
            ForzaOverlay.Core.Logging.Diagnostics.Write("GPU 覆盖层初始化失败: " + ex);
            return false;
        }
    }

    private static IDXGIAdapter1 GetHardwareAdapter(IDXGIFactory2 factory)
    {
        using IDXGIFactory6? factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is not null)
        {
            for (uint index = 0; factory6.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success && adapter is not null; index++)
            {
                if ((adapter.Description1.Flags & AdapterFlags.Software) == AdapterFlags.None)
                {
                    return adapter;
                }

                adapter.Dispose();
            }
        }

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success && adapter is not null; index++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) == AdapterFlags.None)
            {
                return adapter;
            }

            adapter.Dispose();
        }

        throw new InvalidOperationException("No hardware adapter found");
    }

    private void RenderFrame(double delta)
    {
        if (_swapChain is null || _d2dRenderTarget is null)
        {
            _lastDrawMs = 0;
            _lastPresentMs = 0;
            return;
        }

        long drawStart = Stopwatch.GetTimestamp();
        _d2dRenderTarget.BeginDraw();
        _d2dRenderTarget.Clear(new Color4(0, 0, 0, 0));

        try
        {
            Render?.Invoke(_d2dRenderTarget, delta);
        }
        finally
        {
            var endResult = _d2dRenderTarget.EndDraw(out ulong tag1, out ulong tag2);
            if (endResult.Failure && !_endDrawErrorLogged)
            {
                _endDrawErrorLogged = true;
                ForzaOverlay.Core.Logging.Diagnostics.Write(
                    $"GPU EndDraw 失败: {endResult} tag1={tag1} tag2={tag2}");
            }
        }

        long drawEnd = Stopwatch.GetTimestamp();
        _swapChain.Present(0, PresentFlags.None);
        _compositionDevice?.Commit();
        long presentEnd = Stopwatch.GetTimestamp();

        _lastDrawMs = (drawEnd - drawStart) * 1000.0 / Stopwatch.Frequency;
        _lastPresentMs = (presentEnd - drawEnd) * 1000.0 / Stopwatch.Frequency;
    }

    private void ReleaseD3D()
    {
        Render = null;
        HasNewPacket = null;
        _d2dRenderTarget?.Dispose();
        _d2dFactory?.Dispose();
        _compositionVisual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _d2dRenderTarget = null;
        _d2dFactory = null;
        _compositionVisual = null;
        _compositionTarget = null;
        _compositionDevice = null;
        _swapChain = null;
        _context = null;
        _device = null;
    }

    public void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero || _visible == visible)
        {
            return;
        }

        _visible = visible;
        PerfRecorder.OnVisibilityChanged(visible);
        ShowWindow(_hwnd, visible ? SwShow : SwHide);

        if (visible)
        {
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNomove | SwpNosize);
        }
    }

    public void Dispose()
    {
        _running = false;
        _renderThread?.Join(500);
        _messageThread?.Join(500);

        ReleaseD3D();

        if (_waitTimer != IntPtr.Zero)
        {
            _ = CloseHandle(_waitTimer);
            _waitTimer = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WmDestroy = 0x0002;
        const uint WmClose = 0x0010;
        const uint WmNchittest = 0x0084;

        if (msg == WmNchittest)
        {
            return new IntPtr(-1);
        }

        if (msg == WmDestroy)
        {
            // 窗口在 UI 线程创建，PostQuitMessage 会毒死 WPF 的消息循环，不能调。
            return IntPtr.Zero;
        }

        if (msg == WmClose)
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
