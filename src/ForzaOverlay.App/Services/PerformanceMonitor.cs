using System.Diagnostics;
using ForzaOverlay.Core.Logging;

namespace ForzaOverlay.App.Services;

public static class PerformanceMonitor
{
    private static readonly Process Self = Process.GetCurrentProcess();

    private static long _windowStartTick = Environment.TickCount64;
    private static TimeSpan _windowStartCpu = Self.TotalProcessorTime;
    private static int _framesInWindow;

    private static readonly Dictionary<string, PerformanceCounter> GpuCounters = [];
    private static readonly Dictionary<string, PerformanceCounter> VramCounters = [];
    private static readonly Dictionary<string, PerformanceCounter> SystemGpuCounters = [];

    /// <summary>
    /// GPU 计数器实例名缓存，以及上次重扫的时刻。
    /// <para>
    /// **枚举 GPU 实例非常贵**：这台机器上 `GPU Engine` 有近 500 个实例，
    /// 每次 `GetInstanceNames()` 分配约 **120 KB**、耗时 0.6 ms；
    /// `GPU Process Memory` 约 11 KB。每秒枚举一次就是 130 KB/秒（452 MB/小时）——
    /// 而实例名的变化其实很慢（程序启动/退出才变）。
    /// </para>
    /// <para>
    /// 所以每 <see cref="InstanceRescanIntervalMs"/> 才真正重扫一次，其余时间复用结果。
    /// 代价是「某个进程刚开始用 GPU」最多延迟这么久才被发现，对这个面板完全够用。
    /// </para>
    /// </summary>
    private const int InstanceRescanIntervalMs = 30_000;

    private static string[] _gpuInstanceNames = [];
    private static string[] _vramInstanceNames = [];
    private static long _lastInstanceScanTick = long.MinValue / 2;

    /// <summary>
    /// 是否需要 GPU / 显存读数。**关掉时整段跳过**，只保留帧率/CPU/内存。
    /// 资源面板没开、性能日志也没开时，没人会看这两个数字。
    /// </summary>
    private static bool _gpuReadingsNeeded;

    public static double Fps { get; private set; }

    public static double CpuPercent { get; private set; }

    public static double MemoryMb { get; private set; }

    public static double GpuPercent { get; private set; }

    public static double VramMb { get; private set; }

    /// <summary>整机 GPU 引擎利用率合计（含游戏），用来判断是否已经 GPU 饱和。</summary>
    public static double SystemGpuPercent { get; private set; }

    /// <summary>
    /// 由调用方在设置变化时告知「现在有没有人看 GPU / 显存读数」。
    /// HUD 的资源面板、设置页的运行日志页、性能日志开关，任一为真就需要。
    /// </summary>
    public static void SetGpuReadingsNeeded(bool needed)
    {
        if (needed == _gpuReadingsNeeded)
        {
            return;
        }

        _gpuReadingsNeeded = needed;

        // 关掉时把计数器一起收掉，别让它们继续占着 PDH 句柄。
        if (!needed)
        {
            DisposeCounters(GpuCounters);
            DisposeCounters(VramCounters);
            _gpuInstanceNames = [];
            _vramInstanceNames = [];
            _lastInstanceScanTick = long.MinValue / 2;
            GpuPercent = 0;
            VramMb = 0;
        }
    }

    public static void Tick()
    {
        _framesInWindow++;

        long now = Environment.TickCount64;
        double elapsedMs = now - _windowStartTick;
        if (elapsedMs < 1000)
        {
            return;
        }

        TimeSpan cpuNow = Self.TotalProcessorTime;
        double cpuMs = (cpuNow - _windowStartCpu).TotalMilliseconds;

        Fps = _framesInWindow * 1000.0 / elapsedMs;
        CpuPercent = elapsedMs > 0 ? cpuMs / elapsedMs * 100.0 / Environment.ProcessorCount : 0;
        MemoryMb = Self.WorkingSet64 / 1024.0 / 1024.0;

        // GPU / 显存读数只在真的有人看时才刷新（省掉 99% 的枚举开销）。
        if (_gpuReadingsNeeded)
        {
            RefreshGpu();
            RefreshVram();
        }

        // 整机 GPU 汇总由性能日志的后台线程采样，这里只上报轻量指标。
        if (PerfRecorder.Enabled)
        {
            PerfRecorder.SampleSystem(CpuPercent, GpuPercent, VramMb);
        }

        _framesInWindow = 0;
        _windowStartTick = now;
        _windowStartCpu = cpuNow;
    }

    private static void RefreshGpu()
    {
        RefreshCategory("GPU Engine", "Utilization Percentage", GpuCounters, out double total, ref _gpuInstanceNames);
        GpuPercent = total;
    }

    private static void RefreshVram()
    {
        RefreshCategory("GPU Process Memory", "Local Usage", VramCounters, out double bytes, ref _vramInstanceNames);
        VramMb = bytes / 1024.0 / 1024.0;
    }

    private static void RefreshSystemGpu()
    {
        double total = 0;
        try
        {
            const string categoryName = "GPU Engine";
            const string counterName = "Utilization Percentage";

            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                SystemGpuPercent = 0;
                return;
            }

            // 整机汇总要看**所有**实例，不能复用上面的「本进程实例名」缓存，
            // 但仍按同样的节流策略重扫，避免每次取样都枚举上百个名字。
            string[] names = GetInstanceNamesCached(categoryName, ref _systemGpuInstanceNames);
            HashSet<string> seen = [];

            foreach (string instance in names)
            {
                seen.Add(instance);

                if (!SystemGpuCounters.TryGetValue(instance, out PerformanceCounter? counter))
                {
                    counter = new PerformanceCounter(categoryName, counterName, instance, readOnly: true);
                    SystemGpuCounters[instance] = counter;
                }

                total += counter.NextValue();
            }

            foreach (string stale in SystemGpuCounters.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                SystemGpuCounters[stale].Dispose();
                SystemGpuCounters.Remove(stale);
            }
        }
        catch (Exception)
        {
            total = 0;
        }

        SystemGpuPercent = total;
    }

    private static string[] _systemGpuInstanceNames = [];

    /// <summary>供 PerfRecorder 的后台结算线程每 2 秒调用一次；PDH 枚举开销大，不能放在渲染线程。</summary>
    public static double CaptureSystemGpu()
    {
        RefreshSystemGpu();
        return SystemGpuPercent;
    }

    private static void RefreshCategory(
        string categoryName,
        string counterName,
        Dictionary<string, PerformanceCounter> counters,
        out double total,
        ref string[] instanceNameCache)
    {
        total = 0;

        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return;
            }

            string[] names = GetInstanceNamesCached(categoryName, ref instanceNameCache);
            string marker = "pid_" + Environment.ProcessId + "_";
            HashSet<string> seen = [];
            double sum = 0;

            foreach (string instance in names)
            {
                if (!instance.Contains(marker, StringComparison.Ordinal))
                {
                    continue;
                }

                seen.Add(instance);

                if (!counters.TryGetValue(instance, out PerformanceCounter? counter))
                {
                    counter = new PerformanceCounter(categoryName, counterName, instance, readOnly: true);
                    counters[instance] = counter;
                }

                sum += counter.NextValue();
            }

            foreach (string stale in counters.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                counters[stale].Dispose();
                counters.Remove(stale);
            }

            total = sum;
        }
        catch (Exception)
        {
            total = 0;
        }
    }

    /// <summary>
    /// 取实例名列表，带节流：只有距上次重扫超过 <see cref="InstanceRescanIntervalMs"/>
    /// 才真正调 <c>GetInstanceNames()</c>，其余时间直接返回上次的数组。
    /// </summary>
    private static string[] GetInstanceNamesCached(string categoryName, ref string[] cache)
    {
        long now = Environment.TickCount64;
        if (cache.Length > 0 && now - _lastInstanceScanTick < InstanceRescanIntervalMs)
        {
            return cache;
        }

        cache = new PerformanceCounterCategory(categoryName).GetInstanceNames();
        _lastInstanceScanTick = now;
        return cache;
    }

    private static void DisposeCounters(Dictionary<string, PerformanceCounter> counters)
    {
        foreach (PerformanceCounter counter in counters.Values)
        {
            counter.Dispose();
        }

        counters.Clear();
    }
}
