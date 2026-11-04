namespace ForzaOverlay.Core.Configuration;

public static class HudLayoutDefaults
{
    public const double PerformanceWidth = 820;
    public const double PerformanceHeight = 150;

    public const double SteeringWidth = 220;
    public const double SteeringHeight = 118;

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

    public static HudModuleLayout FestivalDefault()
    {
        return new HudModuleLayout { X = 0.4824, Y = 0.6738, WidthRatio = 0.2888 };
    }

    public static HudModuleLayout ShiftTreeDefault()
    {
        return new HudModuleLayout { X = 0.2628, Y = 0.6356, WidthRatio = 0.4809 };
    }

    public static HudModuleLayout PerformanceDefault()
    {
        return new HudModuleLayout { X = 0.2700, Y = 0.7900, WidthRatio = 0.4600 };
    }

    public static HudModuleLayout SteeringDefault()
    {
        return new HudModuleLayout { X = 0.4427, Y = 0.0167, WidthRatio = 0.1146 };
    }

    public static HudModuleLayout TireDefault()
    {
        return new HudModuleLayout { X = 0.0095, Y = 0.2680, WidthRatio = 0.1831 };
    }

    public static HudModuleLayout PerfDefault()
    {
        return new HudModuleLayout { X = 0.8564, Y = 0.3932, WidthRatio = 0.1383 };
    }

    public static HudModuleLayout ShiftHintRgb3Default()
    {
        return new HudModuleLayout { X = 0.3600, Y = 0.2600, WidthRatio = 0.4500 };
    }
}
