namespace ForzaOverlay.Core.Configuration;

using System.Text.Json.Serialization;

public sealed class AppSettings
{
    public string TelemetryIp { get; set; } = "127.0.0.1";

    public int TelemetryPort { get; set; } = 23333;

    public string HotkeyText { get; set; } = "F3";

    public bool ShowPerformancePanel { get; set; } = true;

    public HudInstrumentStyle InstrumentStyle { get; set; }

    public bool ShowSteeringGauge { get; set; } = true;

    public bool ShowTirePanel { get; set; } = true;

    public bool ShowPerfPanel { get; set; }

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
