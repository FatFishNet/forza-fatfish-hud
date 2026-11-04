namespace ForzaOverlay.Core.Configuration;

using System.Text.Json.Serialization;

public sealed class AppSettings
{
    public string TelemetryIp { get; set; } = "127.0.0.1";

    public int TelemetryPort { get; set; } = 23333;

    public string HotkeyText { get; set; } = "F3";

    public bool ShowPerformancePanel { get; set; } = true;

    public HudInstrumentStyle InstrumentStyle { get; set; }

    /// <summary>红区校正：按学习到的断油转速自动校正仪表红区。</summary>
    public bool EnableRedlineCalibration { get; set; } = true;

    /// <summary>换挡提示样式：关闭 / VTEC 模拟灯 / 3 色圆灯；需先开启红区校正。</summary>
    public HudShiftHintStyle ShiftHintStyle { get; set; }

    public bool ShowSteeringGauge { get; set; } = true;

    public bool ShowTirePanel { get; set; } = true;

    public bool ShowPerfPanel { get; set; }

    /// <summary>超速提示音：时速达到设定值时播放提示音。</summary>
    public bool EnableOverspeedSound { get; set; }

    /// <summary>超速提示音触发的时速（km/h）。</summary>
    public int OverspeedSpeedKmh { get; set; } = 200;

    /// <summary>提示音播放次数：3 / 5 / 10，0 表示一直响。</summary>
    public int OverspeedSoundCount { get; set; } = 5;

    /// <summary>相邻两次提示音之间的延迟（毫秒，100~500）。</summary>
    public int OverspeedSoundDelayMs { get; set; } = 200;

    /// <summary>提示音音量（0~100，默认 50）。</summary>
    public int OverspeedSoundVolume { get; set; } = 50;

    /// <summary>提示音文件路径（相对程序目录或绝对路径，允许中文名）。</summary>
    public string OverspeedSoundPath { get; set; } = "Assets\\Sounds\\AE86windchime.mp3";

    public int BasicPanelBackgroundOpacity { get; set; } = 70;

    public int HudRefreshRate { get; set; } = 1;

    public bool AlwaysShowHud { get; set; } = true;

    public int HudAspectRatio { get; set; }

    public int CustomAspectWidth { get; set; } = 3440;

    public int CustomAspectHeight { get; set; } = 1440;

    public HudLayoutProfile Profile16By9 { get; set; } = new();

    public HudLayoutProfile Profile16By10 { get; set; } = new();

    public HudLayoutProfile Profile21By9 { get; set; } = new();

    public HudLayoutProfile Profile32By9 { get; set; } = new();

    public HudLayoutProfile Profile48By9 { get; set; } = new();

    public HudLayoutProfile ProfileCustom { get; set; } = new();

    [JsonIgnore]
    public HudLayoutProfile ActiveLayout =>
        HudAspectRatio switch
        {
            1 => Profile16By10,
            2 => Profile21By9,
            3 => Profile32By9,
            4 => Profile48By9,
            5 => ProfileCustom,
            _ => Profile16By9,
        };
}
