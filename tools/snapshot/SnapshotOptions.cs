using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.Tools.Snapshot;

/// <summary>预设里的单个边界用例。</summary>
internal sealed record SnapshotCase(
    string Name,
    string Description,
    float Speed,
    int Gear,
    float Rpm,
    float? Boost = null);

/// <summary>
/// 命令行参数。设计目标是「一行命令渲染一个确定的状态」，
/// 每个字段都能精确指定，不依赖正弦动画或随机数。
/// </summary>
internal sealed class SnapshotOptions
{
    public string Style { get; private set; } = "altezza";

    public string OutputPath { get; private set; } = string.Empty;

    public int Width { get; private set; } = 1100;

    public int Height { get; private set; } = 760;

    public float Rpm { get; private set; } = 6500f;

    public float MaxRpm { get; private set; } = 8000f;

    /// <summary>时速，单位为 km/h（对用户友好）；构造包时换算成 m/s。</summary>
    public float SpeedKmh { get; private set; } = 200f;

    public int Gear { get; private set; } = 4;

    /// <summary>增压，单位 bar。为 null 表示不指定（由面板按无增压处理）。</summary>
    public float? Boost { get; private set; } = 1.2f;

    /// <summary><c>--boost none</c> 时置真：告诉面板按「无增压车辆」处理。</summary>
    public bool NoBoost { get; private set; }

    public byte Throttle { get; private set; } = 200;

    public byte Brake { get; private set; }

    public double PositionX { get; private set; } = 0.5;

    public double PositionY { get; private set; } = 0.5;

    public bool ShowHelp { get; private set; }

    /// <summary>
    /// 预设批量模式。非空时忽略单个数据参数，按预设列表渲出一组图。
    /// 目前只有 <c>extremes</c>：把官方遥测文档里会遇到的边界值全跑一遍。
    /// </summary>
    public string Preset { get; private set; } = string.Empty;

    /// <summary>预设模式下的输出目录（未给 -o 时用它）。</summary>
    public string OutputDirectory { get; private set; } = "temp/snapshots/preset";

    /// <summary>
    /// 边界场景清单。每一项 = 名字 + 说明 + 该用例要覆盖的值。
    /// 覆盖依据见 FH6 官方 Data Out 文档与实机约定：
    /// 速度 0~999（999 是卡 bug 极值）、档位 R 与 1~10、增压负压到满压、转速零点与断油。
    /// </summary>
    public static IReadOnlyList<SnapshotCase> ExtremesCases { get; } =
    [
        new("01-speed-zero", "时速 0：指针应停在 0 位", Speed: 0f, Gear: 1, Rpm: 900f),
        new("02-speed-999", "时速 999（游戏内卡 bug 极值）：指针应顺时针转出表盘", Speed: 999f, Gear: 10, Rpm: 7800f),
        new("03-gear-r", "倒挡 R：液晶右格应显示 R", Speed: 0f, Gear: -1, Rpm: 900f),
        new("04-gear-10", "10 档：液晶左格亮 1、右格亮 0", Speed: 320f, Gear: 10, Rpm: 7200f),
        new("05-boost-minus3", "增压 -3.00 bar：进气真空，指针在负压端", Speed: 80f, Gear: 3, Rpm: 3000f, Boost: -3f),
        new("06-boost-plus3", "增压 +3.00 bar：满压，指针在正压端", Speed: 180f, Gear: 4, Rpm: 5500f, Boost: 3f),
        new("07-rpm-zero", "转速 0：指针应停在刻度 0", Speed: 0f, Gear: 0, Rpm: 0f),
        new("08-rpm-max", "转速顶到最大转速：指针应到量程末端", Speed: 260f, Gear: 6, Rpm: 8000f),
    ];

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法： snapshot --style <altezza|nfs19|es|ring|driftchain> -o <输出.png> [参数]

            必填
              --style <名>        仪表样式：altezza / nfs19 / es / ring / driftchain
              -o, --output <路径> 输出 PNG 路径

            画布
              --width <px>        输出宽（默认 1100）
              --height <px>       输出高（默认 760）
              --position <x>,<y>  面板左上角在视口中的归一化位置（默认 0,0）

            遥测数据（缺省即是确定值，不用随机数）
              --rpm <转>          当前转速（默认 6500）
              --max-rpm <转>      最大转速（默认 8000）
              --speed <km/h>      时速（默认 200）
              --gear <档>         档位：-1=R、0=N、1~10、11=换挡中（默认 4）
              --boost <bar>       增压，可负（默认 1.2）；给 none 表示不指定
              --throttle <0-255>  油门（默认 200）
              --brake <0-255>     刹车（默认 0）

            示例
              # 咬地鲨，静置小图
              snapshot --style altezza -o out.png

              # 咬地鲨，10 档高速，验双格档位与指针爆表
              snapshot --style altezza --rpm 7800 --speed 500 --gear 10 -o fast.png

              # 咬地鲨，负压（进气真空）
              snapshot --style altezza --boost -0.4 -o vacuum.png

              # ES 数字转速盘，1:1 出图（基准 500×500）
              snapshot --style es --width 500 --height 500 -o es.png

              # 极简弧环 / 漂移锁链，1:1 出图（基准 312×330 / 304×289）
              snapshot --style ring --width 312 --height 330 -o ring.png
              snapshot --style driftchain --width 304 --height 289 -o chain.png

            注意：输出 PNG 是「透明背景 + 直通 alpha」，
                  交给 tools 里的 measure.py / colors.py 前要加 --flatten。
            """);
    }

    public static SnapshotOptions Parse(string[] args)
    {
        SnapshotOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    return options;

                case "--style":
                    options.Style = Next(args, ref i).ToLowerInvariant();
                    break;

                case "-o":
                case "--output":
                    options.OutputPath = Next(args, ref i);
                    break;

                case "--preset":
                    options.Preset = Next(args, ref i).ToLowerInvariant();
                    break;

                case "--out-dir":
                    options.OutputDirectory = Next(args, ref i);
                    break;

                case "--width":
                    options.Width = (int)ParseFloat(Next(args, ref i));
                    break;

                case "--height":
                    options.Height = (int)ParseFloat(Next(args, ref i));
                    break;

                case "--rpm":
                    options.Rpm = ParseFloat(Next(args, ref i));
                    break;

                case "--max-rpm":
                    options.MaxRpm = ParseFloat(Next(args, ref i));
                    break;

                case "--speed":
                    options.SpeedKmh = ParseFloat(Next(args, ref i));
                    break;

                case "--gear":
                    options.Gear = (int)ParseFloat(Next(args, ref i));
                    break;

                case "--boost":
                {
                    string text = Next(args, ref i);
                    options.NoBoost = string.Equals(text, "none", StringComparison.OrdinalIgnoreCase);
                    options.Boost = options.NoBoost ? null : ParseFloat(text);
                    break;
                }

                case "--throttle":
                    options.Throttle = (byte)Math.Clamp(ParseFloat(Next(args, ref i)), 0, 255);
                    break;

                case "--brake":
                    options.Brake = (byte)Math.Clamp(ParseFloat(Next(args, ref i)), 0, 255);
                    break;

                case "--position":
                {
                    string text = Next(args, ref i);
                    string[] parts = text.Split(',');
                    if (parts.Length != 2)
                    {
                        throw new ArgumentException($"--position 要写成 x,y，收到：{text}");
                    }

                    options.PositionX = ParseFloat(parts[0]);
                    options.PositionY = ParseFloat(parts[1]);
                    break;
                }

                default:
                    throw new ArgumentException($"未知参数：{arg}");
            }
        }

        if (!string.IsNullOrEmpty(options.Preset)
            && !string.Equals(options.Preset, "extremes", StringComparison.Ordinal))
        {
            throw new ArgumentException($"未知预设：{options.Preset}（可用：extremes）");
        }

        // 预设模式会按用例自动命名，不需要 -o
        bool presetMode = !string.IsNullOrEmpty(options.Preset);
        if (!presetMode && string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new ArgumentException("缺少 -o/--output");
        }

        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentException("--width / --height 必须为正数");
        }

        return options;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"参数 {args[i]} 缺少取值");
        }

        return args[++i];
    }

    private static float ParseFloat(string text)
    {
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            throw new ArgumentException($"不是有效数字：{text}");
        }

        return value;
    }

    /// <summary>构造一个字段全部确定的遥测包，不复用任何随机/正弦逻辑。</summary>
    public ForzaDataPacket BuildPacket()
    {
        return BuildCore(Rpm, SpeedKmh, Gear, Boost);
    }

    /// <summary>按预设用例构造包，其余字段沿用 <paramref name="template"/>。</summary>
    public static ForzaDataPacket BuildPacket(SnapshotCase item, SnapshotOptions template)
    {
        return template.BuildCore(item.Rpm, item.Speed, item.Gear, item.Boost ?? template.Boost);
    }

    private ForzaDataPacket BuildCore(float rpm, float speedKmh, int gear, float? boostBar)
    {
        ForzaDataPacket packet = new()
        {
            IsRaceOn = 1,
            TimestampMs = 1000,
            EngineMaxRpm = MaxRpm,
            EngineIdleRpm = 800f,
            CurrentEngineRpm = rpm,

            // Speed 的单位是 m/s，命令行按 km/h 给，这里换算
            Speed = speedKmh / ForzaDataPacket.MetersPerSecondToKmh,

            Gear = gear switch
            {
                < 0 => 0,                     // -1（倒挡）在包里用 0 表示，GearNumber 会映射回 R
                _ => (byte)Math.Clamp(gear, 0, 11),
            },

            Accel = Throttle,
            Brake = Brake,

            // Power / Torque 给一组合理值，经典面板会显示它
            Power = 320000f,
            Torque = 480f,
            Fuel = 0.75f,

            // 四个轮胎给不同值，方便检查各格是否独立
            TireTempFrontLeft = 82f,
            TireTempFrontRight = 86f,
            TireTempRearLeft = 78f,
            TireTempRearRight = 90f,
            NormalizedSuspensionTravelFrontLeft = 0.35f,
            NormalizedSuspensionTravelFrontRight = 0.42f,
            NormalizedSuspensionTravelRearLeft = 0.28f,
            NormalizedSuspensionTravelRearRight = 0.31f,
        };

        if (boostBar is float value)
        {
            // Boost 字段是 psi，BoostBar 属性做 psi→bar
            packet.Boost = value / ForzaDataPacket.PsiToBar;
        }

        return packet;
    }
}
