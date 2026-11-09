using ForzaOverlay.Core.Configuration;

namespace ForzaOverlay.App.Ui;

public sealed class HudModuleDescriptor
{
    public required string Name { get; init; }

    public required double BaseWidth { get; init; }

    public required double BaseHeight { get; init; }

    public required string AccentColor { get; init; }

    public required string FillColor { get; init; }

    public required Func<AppSettings, bool> IsVisible { get; init; }

    public required Func<HudLayoutProfile, HudModuleLayout> Read { get; init; }

    public required Action<HudLayoutProfile, HudModuleLayout> Write { get; init; }
}

public static class HudModuleCatalog
{
    public static IReadOnlyList<HudModuleDescriptor> Modules { get; } = new List<HudModuleDescriptor>
    {
        new()
        {
            Name = "行驶仪表 · 经典",
            BaseWidth = HudLayoutDefaults.PerformanceWidth,
            BaseHeight = HudLayoutDefaults.PerformanceHeight,
            AccentColor = "#FFFF6B6B",
            FillColor = "#33FF6B6B",
            IsVisible = static settings => settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Classic,
            Read = static profile => profile.PerformanceLayout,
            Write = static (profile, layout) => profile.PerformanceLayout = layout,
        },
        new()
        {
            Name = "行驶仪表 · GHOST LADDER",
            BaseWidth = HudLayoutDefaults.FestivalWidth,
            BaseHeight = HudLayoutDefaults.FestivalHeight,
            AccentColor = "#FF00E5FF",
            FillColor = "#3300E5FF",
            IsVisible = static settings => settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Festival,
            Read = static profile => profile.FestivalLayout,
            Write = static (profile, layout) => profile.FestivalLayout = layout,
        },
        new()
        {
            Name = "行驶仪表 · SHIFT TREE",
            BaseWidth = HudLayoutDefaults.ShiftTreeWidth,
            BaseHeight = HudLayoutDefaults.ShiftTreeHeight,
            AccentColor = "#FF36E27B",
            FillColor = "#3336E27B",
            IsVisible = static settings => settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.ShiftTree,
            Read = static profile => profile.ShiftTreeLayout,
            Write = static (profile, layout) => profile.ShiftTreeLayout = layout,
        },
        new()
        {
            Name = "行驶仪表 · 仿NFS19",
            BaseWidth = HudLayoutDefaults.Nfs19Width,
            BaseHeight = HudLayoutDefaults.Nfs19Height,
            AccentColor = "#FF4FC3F7",
            FillColor = "#334FC3F7",
            IsVisible = static settings => settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Nfs19,
            Read = static profile => profile.Nfs19Layout,
            Write = static (profile, layout) => profile.Nfs19Layout = layout,
        },
        new()
        {
            Name = "轮胎与悬挂面板",
            BaseWidth = HudLayoutDefaults.TireWidth,
            BaseHeight = HudLayoutDefaults.TireHeight,
            AccentColor = "#FFFFB74D",
            FillColor = "#33FFB74D",
            IsVisible = static settings => settings.ShowTirePanel,
            Read = static profile => profile.TireLayout,
            Write = static (profile, layout) => profile.TireLayout = layout,
        },
        new()
        {
            Name = "HUD 资源占用监测",
            BaseWidth = HudLayoutDefaults.PerfWidth,
            BaseHeight = HudLayoutDefaults.PerfHeight,
            AccentColor = "#FF7CE6A0",
            FillColor = "#337CE6A0",
            IsVisible = static settings => settings.ShowPerfPanel,
            Read = static profile => profile.PerfLayout,
            Write = static (profile, layout) => profile.PerfLayout = layout,
        },
        new()
        {
            Name = "换挡提示 · 4灯提示",
            BaseWidth = HudLayoutDefaults.ShiftHintWidth,
            BaseHeight = HudLayoutDefaults.ShiftHintHeight,
            AccentColor = "#FF36E27B",
            FillColor = "#3336E27B",
            IsVisible = static settings => settings.EnableRedlineCalibration && settings.ShiftHintStyle == HudShiftHintStyle.Rgb3,
            Read = static profile => profile.ShiftHintRgb3Layout,
            Write = static (profile, layout) => profile.ShiftHintRgb3Layout = layout,
        },
    };
}
