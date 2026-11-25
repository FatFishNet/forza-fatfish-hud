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

    /// <summary>自动挡（无断油数据）时使用的红区起点比例，接近游戏红区（约 90%）。</summary>
    public const double AutoRedlineRatio = 0.90;

    /// <summary>红区宽度 = (最大转速 - 断油点) × 系数；断油点离上限远的车（老爷车）红区更宽。</summary>
    public const double RedlineWidthFactorHigh = 1.7;

    /// <summary>断油比例低于阈值（如老爷车）时使用的更宽系数。</summary>
    public const double RedlineWidthFactorLow = 1.9;

    private const double LowCutRatioThreshold = 0.88;

    /// <summary>
    /// 计算红区时取**最近**这么多条升挡样本。
    /// 用户方案（2026-11-21）：升挡数据持续采集、最新鲜，用最近 3 条重算就能覆盖掉旧的错误值，
    /// 不需要额外的「重新学习」流程，也不需要清理按钮。
    /// </summary>
    private const int RecentShiftSampleCount = 3;

    /// <summary>
    /// 至少要有这么多条升挡样本才敢用它当红区。
    /// 2 是下限（单条可能刚好是异常换挡），3 条才能凑出稳定中位数。
    /// </summary>
    private const int MinShiftSamplesForRedline = 2;

    /// <summary>换挡预警 = 断油点 × 该系数。</summary>
    public const double WarnFactor = 0.93;

    /// <summary>最佳换挡 = 断油点 × 该系数。</summary>
    public const double ShiftFactor = 0.98;

    private const double WotThreshold = 0.85;
    private const double CutSawAmplitudeRpmHigh = 100.0;

    /// <summary>
    /// 低动力 / 低转车的**兜底**锯齿幅度。
    /// 断油开关的剧烈程度取决于该车的扭矩水平：小马力自然吸气车复油后转速回升很软，
    /// 锯齿常常只有 45~60 rpm，用 60 会把一整类车永久排除在外。
    /// 配合「转速平台 drift ≤ 40」与「falls ≥ 3」两道闸，45 不会把高转巡航放进来。
    /// </summary>
    private const double CutSawAmplitudeRpmMinimal = 45.0;
    private const double LowRpmEngineMax = 7500.0;
    private const double CutExtensionRpm = 40.0;
    private const double CutMinRpmRatio = 0.75;
    private const double CutOutlierTolerance = 0.02;
    private const double CutCandidateTolerance = 0.02;

    /// <summary>
    /// 断油平台期车速允许的上升量（km/h）。
    /// 断油只切断推力、不断惯性，低档短齿比的车一个断油周期内车速仍会明显上升：
    /// 1 档约 +2.6 km/h、2 档约 +1.7 km/h（按 1.2s 窗口估算），用 1.5 会把样本全丢掉。
    /// </summary>
    private const double CutSpeedRiseTolerance = 1.5;

    /// <summary>
    /// 低档（≤3）放宽后的车速容差。低档齿比短、惯性涨速最明显，给 5 km/h 的余量；
    /// 高档 WOT 涨速本来就小，仍用严格值，避免高转巡航被误判成断油。
    /// </summary>
    private const double CutSpeedRiseToleranceLowGear = 5.0;

    /// <summary>低于等于该档位时使用放宽的车速容差。</summary>
    private const int CutLowGearThreshold = 3;

    /// <summary>
    /// 断油平台期车速允许的**下降**量（km/h）。超过就判定为降档制动 / 拖档，不是断油。
    /// 断油是「推力中断但车还在走」，车速只会涨或持平；降档刹车时车速必然在掉。
    /// </summary>
    private const double CutSpeedDropTolerance = 2.0;

    /// <summary>
    /// 转速达到最大转速的这个比例时，视为**饱和值**，不参与断油学习。
    /// 实测（car 1124）：强行降档把转速拖到 8000（= EngineMaxRpm），
    /// 学出来的断油点 = 8000 − 40 = 7960，红区直接贴到 99%，整辆车报废。
    /// 真实断油通常发生在满表之前（游戏限速器会先切断），贴满表的一定是异常工况。
    /// </summary>
    private const double CutSaturationRatio = 0.99;
    private const double MaxWotTolerance = 0.99;
    private const double SteerHoldThreshold = 0.30;
    private const int SteerHoldLimitMs = 500;
    private const int CutRelearnDeviationCount = 5;
    private const double CutPlatformDriftRpm = 40.0;
    private const int CutWindowMs = 1200;
    private const int CutMinSamples = 20;
    private const int CutMinRises = 2;
    private const int CutMinFalls = 3;
    private const double ShiftSamplingThrottle = 0.80;
    private const double ShiftSamplingSteer = 0.35;
    private const double ShiftSamplingSlip = 1.5;
    private const int MaxCutSamples = 10;

    /// <summary>
    /// 计算红区时只取最近这么多个断油样本。
    /// 用全历史中位数会被陈旧样本锚死，学错了就永远纠正不过来（2026-11-21 用户反馈）。
    /// </summary>
    private const int RecentCutSampleCount = 3;
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
            if (packet.GearNumber is >= 1 and <= 10)
            {
                _lastGear = packet.GearNumber;
            }

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

            // 最高档位每帧记录（不受 WOT/断油采样条件限制），换挡提示用它判断是否最高档。
            // 只接受 1~10 的有效前进档：11 是换挡中、0/-1 是空档/倒挡。
            // 曾漏掉这层判断，导致每辆车 MaxGearSeen 都被记成 11，换挡提示永不亮。
            if (packet.GearNumber is >= 1 and <= 10 && packet.GearNumber > profile.MaxGearSeen)
            {
                profile.MaxGearSeen = packet.GearNumber;
            }

            ObserveCut(packet, tickMs, profile);
            ObserveUpshift(packet, profile);

            if (tickMs - _lastSaveTick > SaveIntervalMs)
            {
                _lastSaveTick = tickMs;
                Save();
            }
        }

        // 只记录有效前进档（1~10）；0/-1（空档/倒挡）和 11（换挡中）都不算。
        int gear = packet.GearNumber;
        if (gear >= 1 && gear <= 10)
        {
            if (gear != _lastGear)
            {
                Diagnostics.Write($"[档位] {_lastGear} -> {gear} 转速={packet.CurrentEngineRpm:F0} 油门={packet.ThrottleNormalized:F2} 滑移={AverageSlip(packet):F2}");
            }

            _lastGear = gear;
            _lastRpm = packet.CurrentEngineRpm;
            _lastThrottle = packet.ThrottleNormalized;
            _lastSteer = packet.SteerNormalized;
            _lastSlip = AverageSlip(packet);
        }
    }

    /// <summary>红区起点占最大转速的比例，供绘制红线区起点。</summary>
    /// <remarks>
    /// <para>
    /// **断油优先**（2026-11-22 定）：能学到断油点的是手动挡，红区由断油**往前推**，
    /// 红区 = Max − (Max−断油) × k，k 按断油点离上限的距离分档（越近红区越窄）。
    /// 这个值只跟断油点走，采集再多也不会漂。
    /// </para>
    /// <para>
    /// **不能拿升挡点当红区**（2026-11-21 试过，2026-11-22 用户实机发现回退）：
    /// 手动挡玩家是照着换挡提示换挡的，而提示点 = 断油 × 0.98，
    /// 于是升挡样本天然挤在断油点下方，拿它当红区等于把红区顶到断油附近 ——
    /// 红区带被压窄，而且升挡点会随驾驶习惯一路走高，表现为「红区逐步变窄」。
    /// 实测 4221 升挡点首末差 +425 转，红区从 86.8% 一路爬到 92.4%，而断油公式稳在 90.0%。
    /// </para>
    /// <para>
    /// 自动挡才用升挡中位数：游戏在红区前就自动升档，永远碰不到限速器，学不到断油。
    /// </para>
    /// </remarks>
    public double GetRedlineRatio(in ForzaDataPacket packet)
    {
        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        (double cut, bool learned) = GetCutRpmInfo(packet);

        if (!learned)
        {
            // 自动挡：断油学不到，只能看游戏在哪里换挡；换挡点之后才是红区。
            double? shiftRpm = GetMedianShiftRpm(packet);
            if (shiftRpm is double autoShift && autoShift > 0)
            {
                return Math.Clamp(autoShift / max, 0.50, 0.98);
            }

            return AutoRedlineRatio;
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

    /// <summary>该车已见最高的 WOT 转速；自动挡用它作为断油估计。</summary>
    public double GetMaxWotRpm(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile)
                ? profile.MaxWotRpm
                : 0;
        }
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

    /// <summary>
    /// 断油值取**最近 3 个样本**的中位数，而不是全部历史的中位数。
    /// 理由（2026-11-21 用户提出）：旧的学习值一旦学错，用全历史中位数会被陈旧样本永久锚住，
    /// 新样本再准也纠正不过来。只取最近 3 个，旧值会随新样本自然淘汰，等于给了自动纠错能力。
    /// </summary>
    /// <remarks>
    /// **饱和样本在读的时候也要挡**（2026-11-22）：采样侧已经有 <see cref="CutSaturationRatio"/> 那道闸，
    /// 但存量数据里还留着修复之前学进去的脏值 —— 实测 car 1124 的断油 = 7960 = 99.5% max，
    /// 是当年强行降档把转速拖到限速器学的。这种值拿去套公式会得到 98% 的红区，整辆车报废。
    /// 读时视为「没学到」，让它回落到升挡中位数，不用让用户手工清档。
    /// </remarks>
    private (double Rpm, bool Learned) GetCutRpmInfo(in ForzaDataPacket packet)
    {
        double max = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        double saturation = max * CutSaturationRatio;

        lock (_gate)
        {
            if (_profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile))
            {
                if (profile.CutValues.Count > 0)
                {
                    int take = Math.Min(RecentCutSampleCount, profile.CutValues.Count);
                    List<double> recent = profile.CutValues
                        .GetRange(profile.CutValues.Count - take, take)
                        .Where(value => value > 0 && value < saturation)
                        .ToList();

                    if (recent.Count > 0)
                    {
                        return (Median(recent), true);
                    }
                }

                // 兼容旧版本数据（只有单一 CutRpm，没有样本列表）。
                // 同样是饱和脏值就当作没学到。
                if (profile.CutRpm is double legacy && legacy > 0 && legacy < saturation)
                {
                    return (legacy, true);
                }
            }
        }

        return (max * FallbackRedlineRatio, false);
    }

    /// <summary>清空全部车辆的学习结果（内存 + 磁盘）。</summary>
    /// <remarks>
    /// 给设置页的「清空自学习数据」用（2026-11-26 用户提出）。
    /// 有些脏值会让某辆车**永久**学不到新断油点 —— 实测 car 1124 的 `MaxWotRpm` 被强行降档
    /// 拖到满表后，与 0.99 饱和过滤正好卡成「窗口最大转速永远差一丝」的死锁，代码里没有出口，
    /// 只能清档重学。与其在代码里猜哪个值脏了，不如把清档能力交给用户。
    /// </remarks>
    public void ClearAll()
    {
        lock (_gate)
        {
            _profiles.Clear();
            _activeKey = string.Empty;
            _cutWindow.Clear();
            Save();
        }
    }

    /// <summary>没有学习器实例时（GPU 覆盖层启动失败）直接清磁盘，避免提示与实际不符。</summary>
    public static void ClearStorage(string storageDirectory)
    {
        string path = Path.Combine(storageDirectory, "shift-learning.json");
        try
        {
            Directory.CreateDirectory(storageDirectory);
            Dictionary<string, ShiftPointProfile> empty = new(StringComparer.Ordinal);
            File.WriteAllText(path, JsonSerializer.Serialize(empty, SerializerOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Write("换挡学习数据清空失败: " + ex.Message);
        }
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// 当前车辆的「典型升挡转速」。**只在学不到断油时**（自动挡）才拿来当红区。
    /// <para>
    /// 取**最近 3 条**升挡样本的中位数。这是 2026-11-22 用户拍板的取法：
    /// 不做档位过滤（曾提议排除 ≤3 档的早换样本，被否），也不按高转分组 ——
    /// 只认时间上最新的 3 条，新样本一进来旧的学习值自然淘汰，学错了也能被纠正。
    /// </para>
    /// <para>
    /// 曾试过「取最高 3 条」，会专挑偶发的超转样本，已被否决，不要改回去。
    /// 手动挡不要拿这个值当红区，原因见 <see cref="GetRedlineRatio"/>。
    /// </para>
    /// </summary>
    private double? GetMedianShiftRpm(in ForzaDataPacket packet)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(BuildKey(packet), out ShiftPointProfile? profile))
            {
                double[] values = profile.Shifts
                    .Where(sample => sample.Gear > 1 && sample.Rpm > 0)
                    .TakeLast(RecentShiftSampleCount)
                    .Select(sample => sample.Rpm)
                    .OrderBy(rpm => rpm)
                    .ToArray();

                if (values.Length >= MinShiftSamplesForRedline)
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

        // 降档立刻清窗：高速降档刹车的瞬间，转速会被档位拖到限速器，
        // 锯齿特征和断油几乎一样，会污染学习结果（2026-11-21 用户实测发现）。
        if (_lastGear >= 1 && packet.GearNumber >= 1 && packet.GearNumber < _lastGear)
        {
            _cutWindow.Clear();
            return;
        }

        if (!wot
            || packet.GearNumber is < 1 or > 10
            || packet.ClutchNormalized > 0.5
            || rpm < maxRpm * CutMinRpmRatio)
        {
            _cutWindow.Clear();
            return;
        }

        // 转速贴满表 = 饱和，不是真断油：
        //   - 强行降档把转速拖到限速器
        //   - 腾空 / 撞墙等异常让转速爆表
        //   - 游戏数值本身到顶
        // 这类样本学进去会让断油点 = Max - 40，红区直接贴到满表（2026-11-21 实测 car 1124）。
        if (rpm >= maxRpm * CutSaturationRatio)
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
        // 阈值随排量/动力水平分三档 —— 小马力车复油后转速回升很软，锯齿常常只有 45~60 rpm。
        // 防误判主要靠下面的「转速平台 drift ≤ 40」和 falls ≥ 3，幅度阈值只负责挡掉完全没起伏的匀速。
        double amplitude = maxRpm < LowRpmEngineMax
            ? CutSawAmplitudeRpmMinimal
            : CutSawAmplitudeRpmHigh;
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

        // 断油平台时动力中断；但**车速仍可能上升**——断油只切断推力，惯性还在，
        // 低档短齿比的车尤其明显（1 档一个断油周期约 +2.6 km/h）。
        // 所以低档放宽到 5 km/h，高档维持 1.5：真正用来防「高转巡航」的是下面的转速平台判定。
        double speedRiseLimit = packet.GearNumber <= CutLowGearThreshold
            ? CutSpeedRiseToleranceLowGear
            : CutSpeedRiseTolerance;
        double firstSpeedAvg = firstSpeedSum / half;
        double secondSpeedAvg = secondSpeedSum / (samples.Length - half);
        double speedDelta = secondSpeedAvg - firstSpeedAvg;

        if (speedDelta > speedRiseLimit)
        {
            return;
        }

        // 车速明显下降 → 这是降档制动 / 拖档，不是断油（断油时车速只涨不跌）。
        if (speedDelta < -CutSpeedDropTolerance)
        {
            return;
        }

        // 转速平台（主判据）：断油时两半段的峰值都贴着限速器，差值很小；
        // 高转巡航 / 上坡 WOT 时转速还在爬，这条会失败。
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

        // 只接受「当前帧仍在断油峰值附近」的样本。
        // 门槛必须跟着实际锯齿幅度走：高动力车锯齿 100 rpm，谷底时 |rpm - windowMax| 就有 100，
        // 用固定的 80（CutExtensionRpm*2）会把谷底那一半样本全丢掉。
        if (Math.Abs(rpm - windowMax) > amplitude * 1.5)
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

        // 与 GetCutRpmInfo 保持一致：只用最近 3 个样本，旧值自然淘汰
        int take = Math.Min(RecentCutSampleCount, profile.CutValues.Count);
        profile.CutRpm = Median(profile.CutValues.GetRange(profile.CutValues.Count - take, take));
        _cutWindow.Clear();
        Diagnostics.Write($"学习断油点: {cut:F0} RPM（近 {take} 样本中位 {profile.CutRpm:F0}，累计 {profile.CutValues.Count}）");
    }

    private void ObserveUpshift(in ForzaDataPacket packet, ShiftPointProfile profile)
    {
        int gear = packet.GearNumber;

        // 换挡瞬间可能出现 0/-1（空档/倒挡）或 11（换挡中），都不算升挡。
        if (gear < 1 || gear > 10)
        {
            return;
        }

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
            Diagnostics.Write(
                $"[换挡学习] 跳过 {_lastGear}->{gear}: 油门={_lastThrottle:F2} 转向={_lastSteer:F2} 滑移={_lastSlip:F2} 转速={_lastRpm:F0}");
            return;
        }

        Diagnostics.Write($"[换挡学习] 记录 {_lastGear}->{gear}: 转速={_lastRpm:F0}");
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
                // 旧版本曾把「换挡中」的 11 也记进最高档，存量数据要顺手清掉，
                // 否则老车永远停在 11，换挡提示的判断永远走不到正常分支。
                if (pair.Value.MaxGearSeen is < 1 or > 10)
                {
                    pair.Value.MaxGearSeen = 0;
                }

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
