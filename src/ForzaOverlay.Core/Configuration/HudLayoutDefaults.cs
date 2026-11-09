namespace ForzaOverlay.Core.Configuration;

public static class HudLayoutDefaults
{
    public const double PerformanceWidth = 820;
    public const double PerformanceHeight = 150;

    public const double TireWidth = 220;
    public const double TireHeight = 212;

    public const double PerfWidth = 200;
    public const double PerfHeight = 140;

    public const double FestivalWidth = 420;
    public const double FestivalHeight = 238;

    public const double ShiftTreeWidth = 820;
    public const double ShiftTreeHeight = 320;

    public const double ShiftHintWidth = 120;
    public const double ShiftHintHeight = 40;

    // 仿NFS19：主表 + 左侧增压小表。基准取图形实际包围盒（590.16×405.72）
    // 四边各留 5px 安全边距后向上取整，避免布局编辑器的框比图形大一圈。
    public const double Nfs19Width = 601;
    public const double Nfs19Height = 416;

    public const double MinWidthRatio = 0.03;
    public const double MaxWidthRatio = 1.0;

    public static readonly string[] AspectNames =
        ["16:9", "16:10", "21:9", "32:9", "48:9", "自定义"];

    public static double AspectRatio(int index, int customWidth, int customHeight)
    {
        return index switch
        {
            1 => 16.0 / 10.0,
            2 => 21.0 / 9.0,
            3 => 32.0 / 9.0,
            4 => 48.0 / 9.0,
            5 => customWidth > 0 && customHeight > 0
                ? (double)customWidth / customHeight
                : 16.0 / 9.0,
            _ => 16.0 / 9.0,
        };
    }

    // 以下布局默认值取自 2026-11-10 实机调校后的 16:9 配置，作为全部比例档的出厂布局。

    public static HudModuleLayout FestivalDefault()
    {
        return new HudModuleLayout { X = 0.6844, Y = 0.6759, WidthRatio = 0.2888 };
    }

    public static HudModuleLayout ShiftTreeDefault()
    {
        return new HudModuleLayout { X = 0.3005, Y = 0.6632, WidthRatio = 0.3996 };
    }

    public static HudModuleLayout PerformanceDefault()
    {
        return new HudModuleLayout { X = 0.2592, Y = 0.7794, WidthRatio = 0.4654 };
    }

    public static HudModuleLayout TireDefault()
    {
        return new HudModuleLayout { X = 0.0167, Y = 0.3286, WidthRatio = 0.1102 };
    }

    public static HudModuleLayout PerfDefault()
    {
        return new HudModuleLayout { X = 0.8474, Y = 0.3921, WidthRatio = 0.1437 };
    }

    public static HudModuleLayout ShiftHintRgb3Default()
    {
        return new HudModuleLayout { X = 0.4326, Y = 0.1736, WidthRatio = 0.1237 };
    }

    public static HudModuleLayout Nfs19Default()
    {
        return new HudModuleLayout { X = 0.6875, Y = 0.6113, WidthRatio = 0.2946 };
    }
}
