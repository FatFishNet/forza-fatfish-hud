using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ForzaOverlay.Core.Logging;

/// <summary>
/// 卡顿诊断记录器：渲染线程只做内存累加，写盘交给后台线程。
/// 默认关闭，由「运行日志」页的开关控制；记录失败时静默停用，不影响 HUD。
/// </summary>
public static class PerfRecorder
{
    private const long MaxFileBytes = 16L * 1024 * 1024;
    private const int MaxFiles = 10;
    private const int RetainDays = 7;
    private const int FrameRingSize = 240;
    private const int SampleCapacity = 2048;
    private const int GapCapacity = 512;
    private const double DropThresholdMs = 16.7;

    // 单帧 3 帧以上才算卡顿；33ms 时代会把「切换场景掉 1~2 帧」也报成卡顿。
    private const double HitchMinMs = 50.0;
    private const double HitchP50Factor = 3.0;
    private const int QueueCapacity = 8192;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly object StateGate = new();
    private static readonly object WriterGate = new();
    private static readonly Process Self = Process.GetCurrentProcess();

    private static readonly float[] FrameSeconds = new float[SampleCapacity];
    private static readonly float[] DrawSeconds = new float[SampleCapacity];
    private static readonly float[] PresentSeconds = new float[SampleCapacity];
    private static readonly float[] GapSeconds = new float[GapCapacity];
    private static readonly float[] FrameRing = new float[FrameRingSize];

    // 后台结算线程用的快照缓冲，避免每秒重新分配。
    private static readonly float[] SnapshotFrames = new float[SampleCapacity];
    private static readonly float[] SnapshotDraw = new float[SampleCapacity];
    private static readonly float[] SnapshotPresent = new float[SampleCapacity];
    private static readonly float[] SnapshotGaps = new float[GapCapacity];

    private static volatile bool _enabled;
    private static volatile bool _writerReady;
    private static volatile float _cpu;
    private static volatile float _gpu;
    private static volatile float _systemGpu;
    private static volatile float _vramMb;

    private static string _environment = string.Empty;
    private static BlockingCollection<string>? _queue;
    private static Thread? _writerThread;
    private static StreamWriter? _writer;
    private static long _writtenBytes;
    private static long _startedTick;
    private static long _hitchTotal;
    private static string? _currentPath;

    private static int _frameCount;
    private static int _visibleFrames;
    private static int _dropFrames;
    private static int _hitchSegments;
    private static bool _inHitch;
    private static int _ringIndex;
    private static int _gapCount;
    private static int _secondPackets;
    private static float _maxPacketAge;
    private static volatile float _lastP50;
    private static long _lastPacketTick;
    private static long _lastFrameTick;
    private static long _lastScanTick = -100000;
    private static double _lastScanMs;
    private static int _gc0;
    private static int _gc1;
    private static int _gc2;
    private static long _pauseTicks;
    private static long _allocBytes;
    private static Timer? _sampler;
    private static int _sampling;
    private static int _samplerTick;
    private static Func<double>? _systemGpuSampler;
    private static volatile bool _skipNextFrame;

    public static bool Enabled => _enabled;

    public static string PerfDirectoryPath => Path.Combine(Diagnostics.LogDirectoryPath, "perf");

    public static void Configure(bool enabled)
    {
        SetEnabled(enabled);
    }

    public static void SetEnvironment(string environment)
    {
        _environment = environment ?? string.Empty;
        if (_enabled)
        {
            Enqueue("# env=" + _environment);
        }
    }

    /// <summary>
    /// HUD 显隐变化时调用。恢复显示后的第一帧，delta 是整段空闲时间，
    /// 不是真实帧耗时，必须跳过，否则启动/切场景会被误记成卡顿。
    /// </summary>
    public static void OnVisibilityChanged(bool visible)
    {
        if (visible)
        {
            _skipNextFrame = true;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        if (enabled)
        {
            if (StartWriter())
            {
                lock (StateGate)
                {
                    ResetSecond();
                    _lastPacketTick = 0;
                    _lastFrameTick = 0;
                    _gapCount = 0;
                    _inHitch = false;
                    _lastP50 = 0;
                    _lastScanTick = -100000;
                    _lastScanMs = 0;
                    _gc0 = GC.CollectionCount(0);
                    _gc1 = GC.CollectionCount(1);
                    _gc2 = GC.CollectionCount(2);
                    _pauseTicks = GC.GetTotalPauseDuration().Ticks;
                    _allocBytes = GC.GetTotalAllocatedBytes(false);
                }

                _startedTick = Environment.TickCount64;
                Interlocked.Exchange(ref _hitchTotal, 0);
                _samplerTick = 0;
                _skipNextFrame = false;
                _enabled = true;
                _sampler = new Timer(SampleTick, null, 1000, 1000);
                Enqueue("# start");
            }

            return;
        }

        _enabled = false;
        StopSampler();
        Enqueue("# stop");
        StopWriter();
    }

    public static void Shutdown()
    {
        if (_enabled)
        {
            _enabled = false;
            StopSampler();
            Enqueue("# shutdown");
            StopWriter();
        }
    }

    /// <summary>整机 GPU 采样由后台结算线程调用，避免 PDH 开销卡住渲染线程。</summary>
    public static void SetSystemGpuSampler(Func<double>? sampler)
    {
        _systemGpuSampler = sampler;
    }

    private static void StopSampler()
    {
        Timer? sampler = _sampler;
        _sampler = null;
        sampler?.Dispose();
    }

    public static string GetStatusText()
    {
        if (!_enabled)
        {
            return "未开启";
        }

        if (!_writerReady || _currentPath is null)
        {
            return "记录中（日志目录不可写）";
        }

        long seconds = (Environment.TickCount64 - _startedTick) / 1000;
        double kb = Interlocked.Read(ref _writtenBytes) / 1024.0;
        return "已记录 "
            + (seconds / 60).ToString(Inv)
            + " 分 "
            + (seconds % 60).ToString(Inv)
            + " 秒 · "
            + kb.ToString("0", Inv)
            + " KB · 卡顿 "
            + Interlocked.Read(ref _hitchTotal).ToString(Inv)
            + " 次";
    }

    public static void OnFrame(double totalMs, double drawMs, double presentMs, bool visible)
    {
        if (!_enabled)
        {
            return;
        }

        if (_skipNextFrame)
        {
            _skipNextFrame = false;
            _lastFrameTick = Environment.TickCount64;
            return;
        }

        // 隐藏后恢复显示时，第一帧的 delta 是整段空闲时间，不能当成真实帧。
        if (totalMs is <= 0 or > 5000)
        {
            return;
        }

        long now = Environment.TickCount64;
        lock (StateGate)
        {
            if (_frameCount < SampleCapacity)
            {
                FrameSeconds[_frameCount] = (float)totalMs;
                DrawSeconds[_frameCount] = (float)drawMs;
                PresentSeconds[_frameCount] = (float)presentMs;
                _frameCount++;
            }

            if (visible)
            {
                _visibleFrames++;
            }

            if (totalMs > DropThresholdMs)
            {
                _dropFrames++;
            }

            // 包到达后紧接着的这一帧，它的「包龄」就是遥测到画面的延迟。
            if (_lastPacketTick > _lastFrameTick && _lastPacketTick > 0)
            {
                float age = (float)(now - _lastPacketTick);
                if (age > _maxPacketAge)
                {
                    _maxPacketAge = age;
                }
            }

            FrameRing[_ringIndex] = (float)totalMs;
            _ringIndex = (_ringIndex + 1) % FrameRingSize;

            double threshold = Math.Max(HitchMinMs, _lastP50 * HitchP50Factor);
            if (totalMs > threshold)
            {
                if (!_inHitch)
                {
                    _inHitch = true;
                    _hitchSegments++;
                    Interlocked.Increment(ref _hitchTotal);
                }

                WriteHitch(totalMs, drawMs, presentMs);
            }
            else
            {
                _inHitch = false;
            }

            _lastFrameTick = now;
        }
    }

    public static void OnPacket(long tickMs)
    {
        if (!_enabled)
        {
            return;
        }

        long now = tickMs > 0 ? tickMs : Environment.TickCount64;
        lock (StateGate)
        {
            if (_lastPacketTick > 0)
            {
                double gap = now - _lastPacketTick;
                if (gap is > 0 and < 1000 && _gapCount < GapCapacity)
                {
                    GapSeconds[_gapCount++] = (float)gap;
                }
            }

            _lastPacketTick = now;
            _secondPackets++;
        }
    }

    public static void SampleSystem(double cpuPercent, double gpuPercent, double vramMb)
    {
        if (!_enabled)
        {
            return;
        }

        _cpu = (float)cpuPercent;
        _gpu = (float)gpuPercent;
        _vramMb = (float)vramMb;
    }

    public static void ReportScan(double milliseconds)
    {
        if (!_enabled)
        {
            return;
        }

        lock (StateGate)
        {
            _lastScanTick = Environment.TickCount64;
            _lastScanMs = milliseconds;
        }
    }

    public static bool Mark()
    {
        if (!_enabled)
        {
            return false;
        }

        Enqueue("MARK," + TimeText());
        return true;
    }

    /// <summary>
    /// 后台线程每秒结算一次。渲染线程只负责写样本，排序、读进程信息、读 PDH、拼行全部在这里做，
    /// 否则每秒一次的开销会直接吃掉一帧（实测曾造成 400ms 卡顿）。
    /// </summary>
    private static void SampleTick(object? state)
    {
        if (!_enabled || Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            int frames;
            int visible;
            int drops;
            int hitchSegments;
            int gaps;
            int packets;
            float maxPacketAge;
            long scanMs;
            long now = Environment.TickCount64;

            lock (StateGate)
            {
                frames = _frameCount;
                visible = _visibleFrames;
                drops = _dropFrames;
                hitchSegments = _hitchSegments;
                gaps = _gapCount;
                packets = _secondPackets;
                maxPacketAge = _maxPacketAge;
                scanMs = now - _lastScanTick <= 1000 ? (long)Math.Round(_lastScanMs) : 0;

                Array.Copy(FrameSeconds, SnapshotFrames, frames);
                Array.Copy(DrawSeconds, SnapshotDraw, frames);
                Array.Copy(PresentSeconds, SnapshotPresent, frames);
                Array.Copy(GapSeconds, SnapshotGaps, gaps);

                ResetSecond();
            }

            WriteSecond(frames, visible, drops, hitchSegments, gaps, packets, maxPacketAge, scanMs);
        }
        catch (Exception)
        {
            // 后台结算失败不能影响渲染，跳过这一秒。
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private static void WriteSecond(
        int frames,
        int visibleFrames,
        int dropFrames,
        int hitchSegments,
        int gaps,
        int packets,
        float maxPacketAge,
        long scanMs)
    {
        try
        {
            float p50 = 0;
            float p99 = 0;
            float frameMax = 0;
            float drawP99 = 0;
            float presentP99 = 0;
            float presentMax = 0;

            if (frames > 0)
            {
                Array.Sort(SnapshotFrames, 0, frames);
                p50 = Percentile(SnapshotFrames, frames, 0.50);
                p99 = Percentile(SnapshotFrames, frames, 0.99);
                frameMax = SnapshotFrames[frames - 1];

                Array.Sort(SnapshotDraw, 0, frames);
                drawP99 = Percentile(SnapshotDraw, frames, 0.99);

                Array.Sort(SnapshotPresent, 0, frames);
                presentP99 = Percentile(SnapshotPresent, frames, 0.99);
                presentMax = SnapshotPresent[frames - 1];

                _lastP50 = p50;
            }

            float gapP50 = 0;
            float gapP99 = 0;
            float gapMax = 0;
            if (gaps > 0)
            {
                Array.Sort(SnapshotGaps, 0, gaps);
                gapP50 = Percentile(SnapshotGaps, gaps, 0.50);
                gapP99 = Percentile(SnapshotGaps, gaps, 0.99);
                gapMax = SnapshotGaps[gaps - 1];
            }

            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            long pauseTicks = GC.GetTotalPauseDuration().Ticks;
            long allocBytes = GC.GetTotalAllocatedBytes(false);

            double pauseMs = (pauseTicks - _pauseTicks) / (double)TimeSpan.TicksPerMillisecond;
            double allocKb = (allocBytes - _allocBytes) / 1024.0;

            Self.Refresh();
            int threads = Self.Threads.Count;
            int handles = Self.HandleCount;
            double workingSetMb = Self.WorkingSet64 / 1024.0 / 1024.0;

            // 整机 GPU 走 PDH，开销大，隔一秒采一次，而且只在后台线程采。
            Func<double>? gpuSampler = _systemGpuSampler;
            if (gpuSampler is not null)
            {
                _samplerTick++;
                if (_samplerTick % 2 == 1)
                {
                    try
                    {
                        _systemGpu = (float)gpuSampler();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            StringBuilder line = new(192);
            line.Append(TimeText()).Append(',')
                .Append(frames.ToString(Inv)).Append(',')
                .Append(visibleFrames.ToString(Inv)).Append(',')
                .Append(dropFrames.ToString(Inv)).Append(',')
                .Append(hitchSegments.ToString(Inv)).Append(',')
                .Append(p50.ToString("0.0", Inv)).Append(',')
                .Append(p99.ToString("0.0", Inv)).Append(',')
                .Append(frameMax.ToString("0.0", Inv)).Append(',')
                .Append(drawP99.ToString("0.0", Inv)).Append(',')
                .Append(presentP99.ToString("0.0", Inv)).Append(',')
                .Append(presentMax.ToString("0.0", Inv)).Append(',')
                .Append(_cpu.ToString("0.0", Inv)).Append(',')
                .Append(_gpu.ToString("0.0", Inv)).Append(',')
                .Append(_systemGpu.ToString("0.0", Inv)).Append(',')
                .Append(_vramMb.ToString("0", Inv)).Append(',')
                .Append(workingSetMb.ToString("0", Inv)).Append(',')
                .Append(threads.ToString(Inv)).Append(',')
                .Append(handles.ToString(Inv)).Append(',')
                .Append((gc0 - _gc0).ToString(Inv)).Append(',')
                .Append((gc1 - _gc1).ToString(Inv)).Append(',')
                .Append((gc2 - _gc2).ToString(Inv)).Append(',')
                .Append(pauseMs.ToString("0.0", Inv)).Append(',')
                .Append(allocKb.ToString("0", Inv)).Append(',')
                .Append(packets.ToString(Inv)).Append(',')
                .Append(gapP50.ToString("0.0", Inv)).Append(',')
                .Append(gapP99.ToString("0.0", Inv)).Append(',')
                .Append(gapMax.ToString("0.0", Inv)).Append(',')
                .Append(maxPacketAge.ToString("0.0", Inv)).Append(',')
                .Append(scanMs.ToString(Inv));

            Enqueue(line.ToString());

            _gc0 = gc0;
            _gc1 = gc1;
            _gc2 = gc2;
            _pauseTicks = pauseTicks;
            _allocBytes = allocBytes;
        }
        catch (Exception)
        {
        }
    }

    private static void ResetSecond()
    {
        _frameCount = 0;
        _visibleFrames = 0;
        _dropFrames = 0;
        _hitchSegments = 0;
        _gapCount = 0;
        _secondPackets = 0;
        _maxPacketAge = 0;
    }

    private static void WriteHitch(double total, double draw, double present)
    {
        StringBuilder line = new(160);
        line.Append("HITCH,").Append(TimeText()).Append(',')
            .Append(total.ToString("0.0", Inv)).Append(',')
            .Append(draw.ToString("0.0", Inv)).Append(',')
            .Append(present.ToString("0.0", Inv)).Append(",ring=");

        int count = Math.Min(12, FrameRingSize);
        for (int i = count; i > 0; i--)
        {
            int index = (_ringIndex - i + FrameRingSize) % FrameRingSize;
            line.Append(FrameRing[index].ToString("0.0", Inv));
            if (i > 1)
            {
                line.Append('|');
            }
        }

        Enqueue(line.ToString());
    }

    private static float Percentile(float[] values, int count, double percentile)
    {
        if (count <= 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(percentile * count) - 1;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= count)
        {
            index = count - 1;
        }

        return values[index];
    }

    private static string TimeText()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);
    }

    private static void Enqueue(string line)
    {
        BlockingCollection<string>? queue = _queue;
        if (queue is null)
        {
            return;
        }

        try
        {
            queue.TryAdd(line);
        }
        catch (Exception)
        {
        }
    }

    private static bool StartWriter()
    {
        lock (WriterGate)
        {
            if (_writerThread is not null)
            {
                return true;
            }

            BlockingCollection<string> queue = new(QueueCapacity);
            Thread thread = new(() => WriterLoop(queue))
            {
                IsBackground = true,
                Name = "PerfRecorder",
            };

            _queue = queue;
            _writerThread = thread;
            thread.Start();
            return true;
        }
    }

    private static void StopWriter()
    {
        BlockingCollection<string>? queue = _queue;
        Thread? thread = _writerThread;
        _queue = null;
        _writerThread = null;

        try
        {
            queue?.CompleteAdding();
        }
        catch (Exception)
        {
        }

        try
        {
            thread?.Join(1000);
        }
        catch (Exception)
        {
        }

        lock (WriterGate)
        {
            CloseWriter();
        }

        queue?.Dispose();
    }

    private static void WriterLoop(BlockingCollection<string> queue)
    {
        if (!OpenFile())
        {
            return;
        }

        try
        {
            foreach (string line in queue.GetConsumingEnumerable())
            {
                WriteLine(line);
            }
        }
        catch (Exception)
        {
        }

        lock (WriterGate)
        {
            CloseWriter();
        }
    }

    private static bool OpenFile()
    {
        lock (WriterGate)
        {
            try
            {
                string directory = PerfDirectoryPath;
                Directory.CreateDirectory(directory);
                PruneFiles(directory);

                string path = Path.Combine(
                    directory,
                    "perf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".csv");

                _writer = new StreamWriter(path, false, new UTF8Encoding(false), 8192)
                {
                    AutoFlush = true,
                };
                _currentPath = path;
                _writtenBytes = 0;
                WriteHeader();
                _writerReady = true;
                return true;
            }
            catch (Exception)
            {
                _writer = null;
                _writerReady = false;
                return false;
            }
        }
    }

    private static void WriteHeader()
    {
        WriteRaw("# Forza Fat Fish HUD perf log");
        WriteRaw("# env=" + _environment);
        WriteRaw("# columns: ts,frames,vis_frames,drop16,hitch_seg,frame_p50,frame_p99,frame_max,draw_p99,present_p99,present_max,cpu,gpu,sys_gpu,vram_mb,ws_mb,threads,handles,gc0,gc1,gc2,gc_pause_ms,alloc_kb,pkts,gap_p50,gap_p99,gap_max,pkt_age_ms,scan_ms");
    }

    private static void WriteRaw(string line)
    {
        try
        {
            _writer?.WriteLine(line);
            _writtenBytes += line.Length + 2;
        }
        catch (Exception)
        {
            _writer = null;
            _writerReady = false;
        }
    }

    private static void WriteLine(string line)
    {
        lock (WriterGate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
                _writtenBytes += line.Length + 2;

                if (_writtenBytes >= MaxFileBytes)
                {
                    CloseWriter();
                    OpenFile();
                }
            }
            catch (Exception)
            {
                CloseWriter();
            }
        }
    }

    private static void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
        }

        _writer = null;
        _currentPath = null;
        _writerReady = false;
    }

    private static void PruneFiles(string directory)
    {
        try
        {
            DateTime threshold = DateTime.Now.AddDays(-RetainDays);
            foreach (string file in Directory.GetFiles(directory, "perf-*.csv"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                }
            }

            string[] remaining = Directory.GetFiles(directory, "perf-*.csv");
            if (remaining.Length > MaxFiles)
            {
                Array.Sort(remaining, StringComparer.Ordinal);
                for (int i = 0; i < remaining.Length - MaxFiles; i++)
                {
                    try
                    {
                        File.Delete(remaining[i]);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
