using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Logging;
using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.App.Services;

/// <summary>
/// 超速提示音：时速超过设定值时播放提示音，支持 3/5/10 声或一直响。
/// 用 WPF MediaPlayer 播放 MP3；等 MediaOpened 拿到真实时长后再调度，避免提前播放下一遍。
/// </summary>
public sealed class OverspeedSoundPlayer : IDisposable
{
    private static readonly TimeSpan PlaybackPadding = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>回差：降速要低于设定值 3 km/h 才重置，避免速度抖动反复触发。</summary>
    private const double HysteresisKmh = 3.0;

    private readonly DispatcherTimer _timer;
    private MediaPlayer? _player;
    private string _path = string.Empty;
    private TimeSpan _duration = TimeSpan.FromSeconds(2.5);
    private DateTime _playStartedAt;
    private DateTime _openRequestedAt;
    private bool _opened;
    private bool _pendingStart;
    private bool _playing;
    private bool _finished;
    private bool _stopRequested;
    private bool _preview;
    private bool _hasNextPlay;
    private DateTime _nextPlayAt;
    private bool _hasLastFrame;
    private TelemetryFrame _lastFrame;
    private string _missingPath = string.Empty;
    private int _remaining;
    private bool _looping;
    private bool _active;

    public OverspeedSoundPlayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>供设置窗口触发预览/停止。</summary>
    public static OverspeedSoundPlayer? Shared { get; set; }

    /// <summary>按当前设置（次数/延迟/音量）播放一次预览。</summary>
    public void Preview()
    {
        AppSettings? settings = AppServices.CurrentSettings;
        if (settings is null || !settings.EnableOverspeedSound || string.IsNullOrWhiteSpace(settings.OverspeedSoundPath))
        {
            return;
        }

        _preview = true;
        _active = true;
        _finished = false;
        _stopRequested = false;
        _hasNextPlay = false;
        _pendingStart = false;
        _playing = false;
        _player?.Stop();
        _remaining = Math.Max(0, settings.OverspeedSoundCount);
        _looping = settings.OverspeedSoundCount <= 0;
        EnsurePlayer(settings.OverspeedSoundPath);
        if (!_opened)
        {
            _pendingStart = true;
            return;
        }

        StartOne();
    }

    /// <summary>停止预览或超速提示音。</summary>
    public void StopAll()
    {
        Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        Stop();
        _player?.Close();
        _player = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        AppSettings? settings = AppServices.CurrentSettings;
        if (settings is null || !settings.EnableOverspeedSound)
        {
            StopIfActive();
            return;
        }

        // 功能开启时就预加载音频并读取真实时长，超速触发时可以直接播放。
        EnsurePlayer(settings.OverspeedSoundPath);

        if (_preview)
        {
            TickPreview(settings);
            return;
        }

        if (!AppServices.TryReadLatest(out TelemetryFrame frame))
        {
            // 渲染线程也在消费遥测队列，UI 定时器读不到新包时沿用上一帧，
            // 否则 120ms 的定时器会被误判成“没有遥测”而反复重置。
            if (!_hasLastFrame)
            {
                StopIfActive();
                return;
            }

            frame = _lastFrame;
        }
        else
        {
            _lastFrame = frame;
            _hasLastFrame = true;
        }

        if (!frame.Packet.RaceOn)
        {
            StopIfActive();
            return;
        }

        double speedKmh = frame.Packet.SpeedKmh;
        int threshold = Math.Max(1, settings.OverspeedSpeedKmh);
        if (_active && speedKmh < threshold - HysteresisKmh)
        {
            // 速度降回阈值以下：不要立刻掐断正在播放的声音，等这一遍播完再停。
            _stopRequested = true;
            return;
        }

        if (!_active)
        {
            if (speedKmh < threshold)
            {
                return;
            }
            _active = true;
            _finished = false;
            _stopRequested = false;
            _hasNextPlay = false;
            _remaining = Math.Max(0, settings.OverspeedSoundCount);
            _looping = settings.OverspeedSoundCount <= 0;
            Diagnostics.Write($"[超速音] 开始: 速度={frame.Packet.SpeedKmh:F0} 阈值={settings.OverspeedSpeedKmh} 次数={(settings.OverspeedSoundCount <= 0 ? "一直响" : settings.OverspeedSoundCount.ToString(CultureInfo.InvariantCulture))} 时长={_duration.TotalSeconds:F2}s 就绪={_opened}");
            EnsurePlayer(settings.OverspeedSoundPath);
            if (!_opened)
            {
                // 等媒体打开完成，拿到真实时长后再开始，避免用默认时长提前切歌。
                _pendingStart = true;
                return;
            }

            StartOne();
            return;
        }

        if (_stopRequested)
        {
            if (!_playing)
            {
                // 没在播放（或已播完），直接重置，等待下次超速。
                Stop();
                return;
            }

            // 正在播放：等这一遍播完再停（下方完成判定处 Stop）。
        }
        else if (_finished)
        {
            // 设定次数已播完，仍超速也不再重复（等降速后再触发）。
            return;
        }

        if (_hasNextPlay)
        {
            if (DateTime.UtcNow < _nextPlayAt)
            {
                return;
            }

            _hasNextPlay = false;
            if (_stopRequested)
            {
                Stop();
                return;
            }

            StartOne();
            return;
        }

        if (_pendingStart)
        {
            if (_stopRequested)
            {
                Stop();
                return;
            }

            if (_opened || DateTime.UtcNow - _openRequestedAt > OpenTimeout)
            {
                _pendingStart = false;
                StartOne();
            }

            return;
        }

        if (!_playing)
        {
            return;
        }

        if (DateTime.UtcNow - _playStartedAt < _duration + PlaybackPadding)
        {
            return;
        }

        _playing = false;
        if (_stopRequested)
        {
            // 这一遍已经播完，现在才真正停止。
            Stop();
            return;
        }

        int delayMs = Math.Clamp(AppServices.CurrentSettings?.OverspeedSoundDelayMs ?? 200, 100, 500);
        if (_looping)
        {
            _nextPlayAt = DateTime.UtcNow.AddMilliseconds(delayMs);
            _hasNextPlay = true;
            return;
        }

        _remaining--;
        if (_remaining > 0)
        {
            _nextPlayAt = DateTime.UtcNow.AddMilliseconds(delayMs);
            _hasNextPlay = true;
            return;
        }

        // 播完指定次数：保持“已触发”状态，速度不降就不再响。
        _finished = true;
        _playing = false;
        Diagnostics.Write("[超速音] 已完成设定次数，等待降速重置");
    }

    private void TickPreview(AppSettings settings)
    {
        if (_stopRequested && !_playing)
        {
            Stop();
            return;
        }

        if (_hasNextPlay)
        {
            if (DateTime.UtcNow < _nextPlayAt)
            {
                return;
            }

            _hasNextPlay = false;
            StartOne();
            return;
        }

        if (_pendingStart)
        {
            if (_opened || DateTime.UtcNow - _openRequestedAt > OpenTimeout)
            {
                _pendingStart = false;
                StartOne();
            }

            return;
        }

        if (!_playing)
        {
            return;
        }

        if (DateTime.UtcNow - _playStartedAt < _duration + PlaybackPadding)
        {
            return;
        }

        _playing = false;
        int delayMs = Math.Clamp(settings.OverspeedSoundDelayMs, 100, 500);
        if (_looping)
        {
            _nextPlayAt = DateTime.UtcNow.AddMilliseconds(delayMs);
            _hasNextPlay = true;
            return;
        }

        _remaining--;
        if (_remaining > 0)
        {
            _nextPlayAt = DateTime.UtcNow.AddMilliseconds(delayMs);
            _hasNextPlay = true;
            return;
        }

        // 预览播完即停；一直响模式由「停止播放」按钮结束。
        if (!_looping)
        {
            Stop();
        }
    }

    private void StopIfActive()
    {
        if (_active)
        {
            Stop();
        }
    }

    private void Stop()
    {
        bool wasActive = _active;
        _active = false;
        _playing = false;
        _pendingStart = false;
        _finished = false;
        _stopRequested = false;
        _preview = false;
        _hasNextPlay = false;
        _remaining = 0;
        _looping = false;
        _player?.Stop();
        if (wasActive)
        {
            Diagnostics.Write("[超速音] 重置（速度低于阈值或关闭开关）");
        }
    }

    private void EnsurePlayer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Stop();
            return;
        }

        string fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(fullPath))
        {
            if (!string.Equals(_missingPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _missingPath = fullPath;
                Diagnostics.Write($"超速提示音文件不存在: {fullPath}");
            }

            Stop();
            return;
        }

        _missingPath = string.Empty;

        if (_player is null)
        {
            _player = new MediaPlayer();
            _player.MediaOpened += OnMediaOpened;
            _player.MediaFailed += (_, args) => Diagnostics.Write("超速提示音播放失败: " + args.ErrorException?.Message);
        }

        if (!string.Equals(_path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            _path = fullPath;
            _opened = false;
            _duration = TimeSpan.FromSeconds(2.5);
            _openRequestedAt = DateTime.UtcNow;
            _player.Open(new Uri(fullPath, UriKind.Absolute));
        }
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (_player is not null && _player.NaturalDuration.HasTimeSpan)
        {
            _duration = _player.NaturalDuration.TimeSpan;
        }

        _opened = true;
        Diagnostics.Write($"[超速音] 媒体就绪: 时长={_duration.TotalSeconds:F2}s 文件={Path.GetFileName(_path)}");
        if (_active && _pendingStart)
        {
            _pendingStart = false;
            StartOne();
        }
    }

    private void StartOne()
    {
        if (_player is null)
        {
            Stop();
            return;
        }

        try
        {
            _player.Position = TimeSpan.Zero;
            _player.Volume = Math.Clamp(AppServices.CurrentSettings?.OverspeedSoundVolume ?? 50, 0, 100) / 100.0;
            _player.Play();
            _playStartedAt = DateTime.UtcNow;
            _playing = true;
            Diagnostics.Write($"[超速音] 播放: 剩余={(_looping ? "循环" : (_remaining - 1).ToString(CultureInfo.InvariantCulture))} 时长={_duration.TotalSeconds:F2}s");
        }
        catch (Exception ex)
        {
            Diagnostics.Write("超速提示音播放异常: " + ex.Message);
            Stop();
        }
    }
}
