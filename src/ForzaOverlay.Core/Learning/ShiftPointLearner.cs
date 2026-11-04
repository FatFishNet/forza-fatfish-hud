namespace ForzaOverlay.Core.Learning;

using System.Globalization;
using System.Text.Json;
using ForzaOverlay.Core.Logging;
using ForzaOverlay.Core.Telemetry;

/// <summary>
/// 从遥测学习换挡相关数据：断油转速（WOT 锯齿平台）与直线升挡点。
/// 只依赖遥测，不读取游戏文件；学习结果按车/调校指纹缓存到 data/shift-learning.json。
/// </summary>
public sealed class ShiftPointLearner
{
    /// <summary>学不到断油点时使用的红区起点比例（占最大转速）。</summary>
    public const double FallbackRedlineRatio = 0.80;

    /// <summary>红区宽度 = (最大转速 - 断油点) × 系数；断油点离上限远的车（老爷车）红区更宽。</summary>
    public const double RedlineWidthFactorHigh = 1.7;

    /// <summary>断油比例低于阈值（如老爷车）时使用的更宽系数。</summary>
    public const double RedlineWidthFactorLow = 1.9;

    private const double LowCutRatioThreshold = 0.88;

    /// <summary>换挡预警 = 断油点 × 该系数。</summary>
    public const double WarnFactor = 0.93;

    /// <summary>最佳换挡 = 断油点 × 该系数。</summary>
    public const double ShiftFactor = 0.98;

    private const double WotThreshold = 0.90;
    private const double CutSawAmplitudeRpmHigh = 100.0;
    private const double CutSawAmplitudeRpmLow = 60.0;
    private const double LowRpmEngineMax = 7500.0;
    private const double CutExtensionRpm = 40.0;
    private const double CutMinRpmRatio = 0.75;
    private const double CutOutlierTolerance = 0.02;
    private const double CutCandidateTolerance = 0.02;
    private const double CutSpeedRiseTolerance = 1.5;
    private const double MaxWotTolerance = 0.99;
    private const double SteerHoldThreshold = 0.30;
    private const int SteerHoldLimitMs = 500;
    private const int CutRelearnDeviationCount = 5;
    private const double CutPlatformDriftRpm = 40.0;
    private const int CutWindowMs = 1500;
    private const int CutMinSamples = 20;
    private const int CutMinRises = 2;
    private const int CutMinFalls = 3;
    private const double ShiftSamplingThrottle = 0.80;
    private const double ShiftSamplingSteer = 0.20;
    private const double ShiftSamplingSlip = 0.6;
    private const int MaxCutSamples = 10;
    private const int MaxShiftSamplesPerGear = 5;
    private const int MaxShiftSamples = 60;
    private const int SaveIntervalMs = 5000;

    private sealed class GearShiftSample
    {
        public int Gear { get; set; }

        public double Rpm { get; set; }
    }

    private sealed class ShiftPointProfile
    {
        public double EngineMaxRpm { get; set; }

        /// <summary>最近一次样本的中位数（便于查看/兼容旧数据）。</summary>
        public double? CutRpm { get; set; }

        public List<double> CutValues { get; set; } = [];

        public double? CutCandidate { get; set; }

        public int CutDeviationCount { get; set; }

        /// <summary>该车已见最高的 WOT 转速；断油点不可能低于它太多。</summary>
        public double MaxWotRpm { get; set; }

        /// <summary>该车已见最高档位；最高档不再提示升挡。</summary>
        public int MaxGearSeen { get; set; }

        public List<GearShiftSample> Shifts { get; set; } = [];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _storageDirectory;
    private readonly string _storagePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, ShiftPointProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Queue<(double Rpm, double SpeedKmh, long Tick)> _cutWindow = new();

    private long _lastSaveTick;
    private long _lastUpdateTick;
    private double _steerHoldMs;
    private string _activeKey = string.Empty;
    private int _lastGear = int.MinValue;
    private double _lastRpm;
    private double _lastThrottle;
    private double _lastSteer;
    private double _lastSlip;

    public ShiftPointLearner(string storageDirectory)
    {
        _storageDirectory = storageDirectory;
        _storagePath = Path.Combine(storageDirectory, "shift-learning.json");
        Load();
    }

    /// <summary>每帧调用：更新断油检测与升挡采样。</summary>
    public void Update(in ForzaDataPacket packet, long tickMs)
    {
        if (!packet.RaceOn || packet.EngineMaxRpm <= 1f)
        {
            _lastGear = packet.GearNumber;
            return;
        }

        if (_lastUpdateTick > 0)
        {
            long delta = Math.Clamp(tickMs - _lastUpdateTick, 0, 100);
            if (Math.Abs(packet.SteerNormalized) > SteerHoldThreshold)
            {
                _steerHoldMs += delta;
            }
            else
            {
                _steerHoldMs = 0;
            }
        }

        _lastUpdateTick = tickMs;

        lock (_gate)
        {
            string key = BuildKey(packet);
            if (!string.Equals(key, _activeKey, StringComparison.Ordinal))
            {
                _activeKey = key;
                _cutWindow.Clear();
            }

            ShiftPointProfile profile = GetOrCreateProfile(key, packet.EngineMaxRpm);
            ObserveCut(packet, tickMs, profile);
            ObserveUpshift(packet, profile);

            if (tickMs - _lastSaveTick > SaveIntervalMs)
            {
                _lastSaveTick = tickMs;
                Save();
            }
        }

        _lastGear = packet.GearNumber;
        _lastRpm = packet.CurrentEngineRpm;
        _lastThrottle = packet.ThrottleNormalized;
        _lastSteer = packet.SteerNormalized;
        _lastSlip = AverageSlip(packet);
    }

    /// <summary>红区起点占最大转速的比例，供绘制红线区起点。</summary>
    /// <remarks>
    /// 红区宽度 = (最大转速 - 断油点) × 1.7：断油点越低红区越宽、越接近上限越窄，
    /// 与实测「有些车红区只有几百转」吻合。只用断油点，不受换挡点影响。
    /// </remarks>
    public double GetRedlineRatio(in ForzaDataPacket packet)
    {
        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        (double cut, bool learned) = GetCutRpmInfo(packet);
        if (!learned)
        {
            return FallbackRedlineRatio;
        }

        double margin = Math.Max(0, max - cut);
        double cutRatio = cut / max;
        double factor = cutRatio < LowCutRatioThreshold ? RedlineWidthFactorLow : RedlineWidthFactorHigh;
        double redline = max - (margin * factor);
        return Math.Clamp(redline / max, 0.50, 0.98);
    }

    /// <summary>(预警、最佳换挡) 的转速比例，供换挡提示。</summary>
    public (double Warn, double Best) GetShiftHintRatios(in ForzaDataPacket packet)
    {
        (double cut, _) = GetCutRpmInfo(packet);
        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        return (
            Math.Clamp(cut * WarnFactor / max, 0.30, 1.0),
            Math.Clamp(cut * ShiftFactor / max, 0.30, 1.0));
    }

    /// <summary>当前车辆是否已学到断油点。</summary>
    public bool HasLearnedCut(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile)
                && profile.CutRpm is double cut
                && cut > 0;
        }
    }

    private (double Rpm, bool Learned) GetCutRpmInfo(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile))
            {
                if (profile.CutValues.Count > 0)
                {
                    return (Median(profile.CutValues), true);
                }

                // 兼容旧版本数据（只有单一 CutRpm，没有样本列表）。
                if (profile.CutRpm is double legacy && legacy > 0)
                {
                    return (legacy, true);
                }
            }
        }

        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        return (max * FallbackRedlineRatio, false);
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private double? GetMedianShiftRpm(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile)
                && profile.Shifts.Count >= 2)
            {
                double[] values = profile.Shifts
                    .Where(sample => sample.Gear > 1)
                    .Select(sample => sample.Rpm)
                    .OrderBy(rpm => rpm)
                    .ToArray();
                if (values.Length >= 2)
                {
                    return values[values.Length / 2];
                }
            }
        }

        return null;
    }

    /// <summary>断油点与最大转速；用于换挡提示灯组计算。</summary>
    public (double CutRpm, double MaxRpm, bool Learned) GetCutInfo(in ForzaDataPacket packet)
    {
        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        (double cut, bool learned) = GetCutRpmInfo(packet);
        return (cut, max, learned);
    }

    /// <summary>该车已学到的最高档位；0 表示还没学到。</summary>
    public int GetMaxGearSeen(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile)
                ? profile.MaxGearSeen
                : 0;
        }
    }

    /// <summary>换挡点中位数（排除 1 档），供后续换挡提示使用。</summary>
    public double? GetShiftPointRpm(in ForzaDataPacket packet)
    {
        return GetMedianShiftRpm(packet);
    }

    private ShiftPointProfile GetOrCreateProfile(string key, double engineMaxRpm)
    {
        if (!_profiles.TryGetValue(key, out ShiftPointProfile? profile))
        {
            profile = new ShiftPointProfile { EngineMaxRpm = engineMaxRpm };
            _profiles[key] = profile;
        }

        return profile;
    }

    private void ObserveCut(in ForzaDataPacket packet, long tickMs, ShiftPointProfile profile)
    {
        bool wot = packet.ThrottleNormalized >= WotThreshold && packet.BrakeNormalized <= 0.05;
        double maxRpm = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        double rpm = packet.CurrentEngineRpm;

        if (!wot
            || packet.GearNumber < 1
            || packet.ClutchNormalized > 0.5
            || rpm < maxRpm * CutMinRpmRatio)
        {
            _cutWindow.Clear();
            return;
        }

        // 1 档打滑时轮胎空转会把转速拉高甚至进断油，不能当作可用样本。
        if (packet.GearNumber == 1 && AverageSlip(packet) > 1.2)
        {
            _cutWindow.Clear();
            return;
        }

        // 持续转弯（方向盘按住超过 0.5 秒）不是直线加速，丢弃窗口；
        // 直线上的短促修正不受影响。
        if (_steerHoldMs > SteerHoldLimitMs)
        {
            _cutWindow.Clear();
            return;
        }

        if (rpm > profile.MaxWotRpm)
        {
            profile.MaxWotRpm = rpm;
        }

        if (packet.GearNumber > profile.MaxGearSeen)
        {
            profile.MaxGearSeen = packet.GearNumber;
        }

        while (_cutWindow.Count > 0 && tickMs - _cutWindow.Peek().Tick > CutWindowMs)
        {
            _cutWindow.Dequeue();
        }

        _cutWindow.Enqueue((rpm, packet.SpeedKmh, tickMs));
        if (_cutWindow.Count < CutMinSamples)
        {
            return;
        }

        (double Rpm, double SpeedKmh, long Tick)[] samples = _cutWindow.ToArray();
        double windowMax = double.MinValue;
        double windowMin = double.MaxValue;
        int rises = 0;
        int falls = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            windowMax = Math.Max(windowMax, samples[i].Rpm);
            windowMin = Math.Min(windowMin, samples[i].Rpm);
            if (i > 0)
            {
                if (samples[i].Rpm > samples[i - 1].Rpm)
                {
                    rises++;
                }
                else if (samples[i].Rpm < samples[i - 1].Rpm)
                {
                    falls++;
                }
            }
        }

        // 真实断油是锯齿：峰值不再上升且反复回落；普通加速只有上升、没有回落。
        // 低转发动机断油振幅更小，放宽阈值。
        double amplitude = maxRpm < LowRpmEngineMax ? CutSawAmplitudeRpmLow : CutSawAmplitudeRpmHigh;
        if (rises < CutMinRises || falls < CutMinFalls || windowMax - windowMin < amplitude)
        {
            return;
        }

        int half = samples.Length / 2;
        double firstSpeedSum = 0;
        double secondSpeedSum = 0;
        double firstHalfMax = double.MinValue;
        double secondHalfMax = double.MinValue;
        for (int i = 0; i < samples.Length; i++)
        {
            if (i < half)
            {
                firstSpeedSum += samples[i].SpeedKmh;
                firstHalfMax = Math.Max(firstHalfMax, samples[i].Rpm);
            }
            else
            {
                secondSpeedSum += samples[i].SpeedKmh;
                secondHalfMax = Math.Max(secondHalfMax, samples[i].Rpm);
            }
        }

        // 断油平台时动力中断，速度应基本停止上升；
        // 明显仍在加速说明只是高转巡航/转向，不是断油。
        double firstSpeedAvg = firstSpeedSum / half;
        double secondSpeedAvg = secondSpeedSum / (samples.Length - half);
        if (secondSpeedAvg - firstSpeedAvg > CutSpeedRiseTolerance)
        {
            return;
        }

        // 后半段仍明显高于前半段 → 还在加速，不是平台。
        if (secondHalfMax - firstHalfMax > CutPlatformDriftRpm)
        {
            return;
        }

        // 断油点不可能明显低于该车已跑到的最高 WOT 转速；
        // 高转巡航/转向时转速停在较低区间，不满足这条会被拒绝。
        if (profile.MaxWotRpm > 0 && windowMax < profile.MaxWotRpm * MaxWotTolerance)
        {
            return;
        }

        if (Math.Abs(rpm - windowMax) > CutExtensionRpm * 2)
        {
            return;
        }

        double cut = windowMax - CutExtensionRpm;
        double? median = profile.CutValues.Count > 0 ? Median(profile.CutValues) : null;

        if (median is null)
        {
            // 单次检测先作为候选，第二次落在 ±2% 内才确认，避免换挡抖动被当成断油。
            if (profile.CutCandidate is double candidate
                && Math.Abs(cut - candidate) <= candidate * CutCandidateTolerance)
            {
                double confirmed = (candidate + cut) / 2;
                profile.CutValues.Add(confirmed);
                profile.CutRpm = confirmed;
                profile.CutCandidate = null;
                profile.CutDeviationCount = 0;
                _cutWindow.Clear();
                Diagnostics.Write($"确认断油点: {confirmed:F0} RPM");
            }
            else
            {
                profile.CutCandidate = cut;
            }

            return;
        }

        if (Math.Abs(cut - median.Value) > median.Value * CutOutlierTolerance)
        {
            // 连续多次偏离旧值 → 旧学习值可能错误（误检/换调校），重新学习。
            profile.CutDeviationCount++;
            if (profile.CutDeviationCount >= CutRelearnDeviationCount)
            {
                profile.CutValues.Clear();
                profile.CutValues.Add(cut);
                profile.CutRpm = cut;
                profile.CutCandidate = null;
                profile.CutDeviationCount = 0;
                _cutWindow.Clear();
                Diagnostics.Write($"断油点重新学习: {cut:F0} RPM");
            }

            return;
        }

        profile.CutDeviationCount = 0;
        profile.CutValues.Add(cut);
        if (profile.CutValues.Count > MaxCutSamples)
        {
            profile.CutValues.RemoveAt(0);
        }

        profile.CutRpm = Median(profile.CutValues);
        _cutWindow.Clear();
        Diagnostics.Write($"学习断油点: {cut:F0} RPM（中位 {profile.CutRpm:F0}，样本 {profile.CutValues.Count}）");
    }

    private void ObserveUpshift(in ForzaDataPacket packet, ShiftPointProfile profile)
    {
        int gear = packet.GearNumber;
        bool upshift = _lastGear >= 1 && gear == _lastGear + 1;
        if (!upshift)
        {
            return;
        }

        bool cleanSample = _lastThrottle >= ShiftSamplingThrottle
            && Math.Abs(_lastSteer) <= ShiftSamplingSteer
            && _lastSlip <= ShiftSamplingSlip
            && _lastRpm > 0;
        if (!cleanSample)
        {
            return;
        }

        AddShiftSample(profile, _lastGear, _lastRpm);
    }

    private static void AddShiftSample(ShiftPointProfile profile, int gear, double rpm)
    {
        // 1 档容易打滑，换挡点不可靠，不参与统计。
        if (gear <= 1)
        {
            return;
        }

        List<GearShiftSample> sameGear = profile.Shifts.FindAll(sample => sample.Gear == gear);
        if (sameGear.Count >= MaxShiftSamplesPerGear)
        {
            return;
        }

        if (sameGear.Exists(sample => Math.Abs(sample.Rpm - rpm) < 60))
        {
            return;
        }

        profile.Shifts.Add(new GearShiftSample { Gear = gear, Rpm = rpm });
        if (profile.Shifts.Count > MaxShiftSamples)
        {
            profile.Shifts.RemoveAt(0);
        }
    }

    private static double AverageSlip(in ForzaDataPacket packet)
    {
        double sum = Math.Abs(packet.TireCombinedSlipFrontLeft)
            + Math.Abs(packet.TireCombinedSlipFrontRight)
            + Math.Abs(packet.TireCombinedSlipRearLeft)
            + Math.Abs(packet.TireCombinedSlipRearRight);
        return sum / 4.0;
    }

    private static string BuildKey(in ForzaDataPacket packet)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{packet.CarOrdinal}|{packet.CarClass}|{packet.CarPerformanceIndex}|{Math.Round(packet.EngineMaxRpm)}|{Math.Round(packet.EngineIdleRpm)}|{packet.DrivetrainType}|{packet.NumCylinders}");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storagePath))
            {
                return;
            }

            Dictionary<string, ShiftPointProfile>? data =
                JsonSerializer.Deserialize<Dictionary<string, ShiftPointProfile>>(File.ReadAllText(_storagePath), SerializerOptions);
            if (data is null)
            {
                return;
            }

            foreach (KeyValuePair<string, ShiftPointProfile> pair in data)
            {
                _profiles[pair.Key] = pair.Value;
            }

            Diagnostics.Write($"换挡学习数据已加载: {_profiles.Count} 辆车");
        }
        catch (Exception ex)
        {
            Diagnostics.Write("换挡学习数据加载失败: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_storageDirectory);
            Dictionary<string, ShiftPointProfile> data = new(_profiles, StringComparer.Ordinal);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(data, SerializerOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Write("换挡学习数据保存失败: " + ex.Message);
        }
    }
}
