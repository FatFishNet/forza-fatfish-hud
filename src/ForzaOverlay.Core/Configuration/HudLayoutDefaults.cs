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

    // 咬地鲨三表：左侧转速表 + 中间双单位速度表 + 三小表。
    // 原型画布 1440×720，图形实际包围盒 1043.4×711.4，四边各加 5px 后向上取整，
    // 同样是为了让布局编辑器的框紧贴图形。
    public const double AltezzaWidth = 1054;
    public const double AltezzaHeight = 722;

    // ES 单表转速盘：圆形，原型画布 1000×1000，按用户要求对外基准直接取 500×500
    // （内部坐标 = 原型坐标 × 0.5，不再另算包围盒）。
    public const double EsWidth = 500;
    public const double EsHeight = 500;

    // 极简弧环：单表圆形，原型画布 380×380 但图形包围盒只有 302×320，
    // 四边各留 5px 安全边距后基准取 312×330（内部坐标仍是原型原值，靠 ContentShift 平移）。
    public const double RingWidth = 312;
    public const double RingHeight = 330;

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

    // 三表集群比单表宽得多，0.34 在 1920 宽下约 653px；
    // X 取 0.42 让它在底部偏右（用户要求"底部靠右一点"）。
    public static HudModuleLayout AltezzaDefault()
    {
        return new HudModuleLayout { X = 0.42, Y = 0.55, WidthRatio = 0.34 };
    }

    // 单表正方形，比咬地鲨小得多；0.26 在 1920 宽下约 499px。
    // 放右下角，和 GHOST（右下偏中）错开，避免两套样式同时开时压在一起。
    public static HudModuleLayout EsDefault()
    {
        return new HudModuleLayout { X = 0.695, Y = 0.50, WidthRatio = 0.26 };
    }

    // 单表圆形但比 ES 矮（312×330）。0.24 在 1920 宽下约 461px；
    // 放在 ES 位置略上方，避免与 ES / GHOST 重合。
    public static HudModuleLayout RingDefault()
    {
        return new HudModuleLayout { X = 0.715, Y = 0.42, WidthRatio = 0.24 };
    }
}
