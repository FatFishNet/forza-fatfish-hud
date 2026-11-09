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

    public static double Fps { get; private set; }

    public static double CpuPercent { get; private set; }

    public static double MemoryMb { get; private set; }

    public static double GpuPercent { get; private set; }

    public static double VramMb { get; private set; }

    /// <summary>整机 GPU 引擎利用率合计（含游戏），用来判断是否已经 GPU 饱和。</summary>
    public static double SystemGpuPercent { get; private set; }

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

        RefreshGpu();
        RefreshVram();

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
        RefreshCategory("GPU Engine", "Utilization Percentage", GpuCounters, out double total);
        GpuPercent = total;
    }

    private static void RefreshVram()
    {
        RefreshCategory("GPU Process Memory", "Local Usage", VramCounters, out double bytes);
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

            PerformanceCounterCategory category = new(categoryName);
            HashSet<string> seen = [];

            foreach (string instance in category.GetInstanceNames())
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

    /// <summary>供 PerfRecorder 的后台结算线程每 2 秒调用一次；PDH 枚举开销大，不能放在渲染线程。</summary>
    public static double CaptureSystemGpu()
    {
        RefreshSystemGpu();
        return SystemGpuPercent;
    }

    private static void RefreshCategory(string categoryName, string counterName, Dictionary<string, PerformanceCounter> counters, out double total)
    {
        total = 0;

        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return;
            }

            PerformanceCounterCategory category = new(categoryName);
            string marker = "pid_" + Environment.ProcessId + "_";
            HashSet<string> seen = [];
            double sum = 0;

            foreach (string instance in category.GetInstanceNames())
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
}
