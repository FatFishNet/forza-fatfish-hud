using System.Globalization;
using System.Numerics;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Learning;
using ForzaOverlay.Core.Telemetry;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 咬地鲨三表（Lexus Altezza / IS200 仪表集群）渲染器。
/// 几何来自原型 <c>temp/panel-cluster-altezza</c>，内部坐标按原型画布 1440×720 书写。
/// <para>
/// 对外基准尺寸取图形实际包围盒（1043.4×711.4）四边各加 5px 后取整 = 1054×722，
/// 差异由 <see cref="ContentShiftX"/> / <see cref="ContentShiftY"/> 平移吸收，
/// 布局编辑器的框才不会被画布空白撑大。
/// </para>
/// <para>
/// 图层顺序照原型：两个表盘 → 三小表 → 三小表指针 → 转速长指针 → 速度长指针。
/// 指针必须压在全部表盘之上，否则银盘与小表底盘会把针身拦腰盖住。
/// </para>
/// 由 <see cref="HudScene"/> 委派调用，本类只负责画，不参与遥测采集与布局调度。
/// </summary>
internal sealed class AltezzaScene : IDisposable
{
    private const float BaseWidth = 1054f;
    private const float BaseHeight = 722f;

    // 原型画布 1440×720，图形包围盒左上角在 (199.1, 4.3)，
    // 平移后落到 (5,5) 的 5px 安全边距上。
    private const float ContentShiftX = -194.1f;
    private const float ContentShiftY = 0.7f;

    // ── 左侧转速表 ──
    private const float TachoX = 471.1f;
    private const float TachoY = 412.9f;
    private const float TachoR = 272f;
    private const float TachoAngleStart = 126.5f;
    private const float TachoSweep = 157.1f;
    private const float TachoNumRatio = 0.735f;
    private const float TachoTickMajorIn = 0.848f;
    private const float TachoTickMinorIn = 0.898f;
    private const float TachoTickOut = 0.960f;

    /// <summary>
    /// 大刻度（每千转）比小刻度加粗 20%、缩短 30%。
    /// 缩短是「砍掉长度」而不是整体缩放：外端仍对齐 <see cref="TachoTickOut"/>，
    /// 只把内端往外推 —— 否则刻度会脱离盘沿、看起来悬空。
    /// </summary>
    private const float TachoTickMajorWidthScale = 1.2f;
    private const float TachoTickMajorLengthScale = 0.7f;
    private const float TachoTickMajorWidth = 5.4f * TachoTickMajorWidthScale;
    private const float TachoTickMinorWidth = 2.4f;

    /// <summary>小刻度同样从外端往内缩，缩短 12%（长度 × 0.88）。</summary>
    private const float TachoTickMinorLengthScale = 0.88f;

    /// <summary>
    /// 指针长度：要恰好扫到大刻度的一半处。
    /// 大刻度内端随 <see cref="TachoTickMajorLengthScale"/> 变化，所以这里由公式推导，
    /// 不要写死数字 —— 改刻度长度时指针会跟着对齐。
    /// </summary>
    private static readonly float TachoNeedleLenToMajorHalf =
        (TachoTickOut
            - ((TachoTickOut - TachoTickMajorIn) * TachoTickMajorLengthScale)
            + TachoTickOut) / 2f;
    private const float TachoHubRatio = 0.098f;
    private const float TachoNeedleTailRatio = 0.135f;
    private const float TachoNeedleWidth = 7f;

    /// <summary>
    /// 转速表单位文字的基线补偿。原型是 SVG，<c>y</c> 表示基线；
    /// 这里走 D2D 的行高居中，语义不同，实测稳定偏低约 8px。
    /// </summary>
    private const float TachoUnitBaselineFix = 8f;

    /// <summary>
    /// <c>IDWriteTextLayout1.SetCharacterSpacing</c> 的单位换算：传入 1 单位约等于多少像素。
    /// 文档只写「em 的千分之一」，但按那换算实测严重过宽。
    /// 实测标定：传入 4.5 → 宽度 +38px / 10 字符 = 每字符 3.8px，故 1 单位 ≈ 0.844px。
    /// 改字号后要重新标定一次。
    /// </summary>
    private const float CharacterSpacingUnitToPx = 0.844f;

    // ── 中间速度表（双单位：外圈 MPH、内圈 km/h）──
    private const float SpeedoX = 886.8f;
    private const float SpeedoY = 360f;
    private const float SpeedoR = 354.2f;
    private const float SpeedoAngleStart = 139.3f;
    private const float SpeedoSweep = 267.1f;
    private const float SpeedoVmaxMph = 220f;
    private const float SpeedoStepMph = 20f;
    private const float SpeedoKmhMax = 360f;
    private const float SpeedoStepKmh = 20f;
    private const float SpeedoDiskRatio = 0.680f;
    private const float SpeedoTeethInRatio = 0.680f;
    private const float SpeedoTeethOutRatio = 0.7292f;
    private const int SpeedoTeeth = 90;
    private const float SpeedoToothRatio = 0.41f;
    private const float SpeedoKmhNumRatio = 0.7945f;
    private const float SpeedoNumRatio = 0.912f;
    private const float SpeedoHubRatio = 0.105f;
    private const float SpeedoNeedleLenRatio = 0.905f * 0.93f;
    private const float SpeedoNeedleTailRatio = 0.145f;
    private const float SpeedoNeedleWidth = 8f;
    private const float UnitKmhAngle = 62.5f;
    private const float UnitMphAngle = 64f;

    // 银盘与齿环共用的渐变轴（原型 gSilverAbs，userSpaceOnUse）。
    // 齿若各自按百分比取渐变，每颗齿会独立映射整条渐变，深浅不一。
    private const float SilverAxisX1 = 723.0f;
    private const float SilverAxisY1 = 138.4f;
    private const float SilverAxisX2 = 1050.6f;
    private const float SilverAxisY2 = 581.6f;

    // ── 档位液晶 ──
    private const float GearX = 468.5f;
    private const float GearY = 560f;
    private const float GearW = 100f;
    private const float GearH = 94f;

    // ── LEXUS 字标 ──
    private const float LexusX = 754.2f;
    private const float LexusY = 362.3f;
    private const float LexusW = 151.9f;
    private const float LexusH = 21.4f;

    // ── 三小表（半径与扫掠一致，只有中心与量程不同）──
    private const float SubR = 84.8f;
    private const float SubAngleStart = 90f;
    private const float SubSweep = 180f;
    private const float SubTickMajorIn = 0.62f;
    private const float SubTickMinorIn = 0.69f;
    private const float SubTickOut = 0.80f;
    private const float SubLabelRatio = 0.36f;
    private const float SubHubRatio = 0.105f;
    private const float SubNeedleLenRatio = 0.78f * 0.93f;
    private const float SubNeedleWidth = 3.4f;

    private const float BoostX = 890.0f;
    private const float BoostY = 221.1f;
    private const float BoostVmax = 3f;
    private const float BoostMajor = 1.5f;
    private const float BoostMinor = 0.25f;

    private const float VoltX = 1027.7f;
    private const float VoltY = 358.1f;
    private const float VoltVmax = 18f;
    private const float VoltMajor = 9f;
    private const float VoltMinor = 3f;
    /// <summary>电压表没有遥测数据源，按装饰表处理：静止基准 13.8V。</summary>
    private const float VoltBase = 13.8f;

    private const float TempX = 889.6f;
    private const float TempY = 496.2f;
    private const float TempVmax = 1f;
    private const float TempMajor = 0.25f;
    private const float TempMinor = 0.125f;
    /// <summary>
    /// 水温表刻度范围：C = 0℃、H = 180℃，线性映射到 90°~270° 的扫掠上。
    /// </summary>
    private const float TempMinCelsius = 0f;
    private const float TempMaxCelsius = 180f;

    /// <summary>
    /// 水温表默认停在 100℃ 的位置。
    /// 换算：ratio = (100 - 0) / (180 - 0) = 0.5556 → angle = 90 + 0.5556 × 180 ≈ 190°。
    /// 注意不是 180°（180° 是 90℃ 的中点，不是 100℃）。
    /// </summary>
    private const float TempDefaultCelsius = 100f;

    private static readonly float TempAngleAtDefault =
        SubAngleStart + ((TempDefaultCelsius - TempMinCelsius) / (TempMaxCelsius - TempMinCelsius) * SubSweep);

    /// <summary>
    /// 水温与速度的联动比：速度表每顺时针转 5°，水温表转 1°。
    /// 允许超出 C~H 量程（用户明确要求可超转）。
    /// </summary>
    private const float TempLinkRatio = 5f;

    // ── 配色（三色制：奶油白 / 金 / 红）──
    private static readonly Color4 CreamColor = new(0xEB / 255f, 0xD2 / 255f, 0xB2 / 255f, 1f);
    private static readonly Color4 GoldColor = new(0xD5 / 255f, 0xAD / 255f, 0x4A / 255f, 1f);
    private static readonly Color4 RedColor = new(0xD9 / 255f, 0x1A / 255f, 0x24 / 255f, 1f);
    private static readonly Color4 InkColor = new(0x2A / 255f, 0x26 / 255f, 0x20 / 255f, 1f);
    private static readonly Color4 NeedleTailColor = new(0xF4 / 255f, 0xEC / 255f, 0xDA / 255f, 1f);
    private static readonly Color4 LexusColor = new(0x1B / 255f, 0x19 / 255f, 0x1E / 255f, 1f);
    private static readonly Color4 FaceStrokeColor = new(4f / 255f, 2f / 255f, 5f / 255f, 1f);
    private static readonly Color4 DiskInnerRingColor = new(0x15 / 255f, 0x16 / 255f, 0x11 / 255f, 1f);
    private static readonly Color4 SubFaceColor = new(0x1A / 255f, 0x1A / 255f, 0x18 / 255f, 1f);
    private static readonly Color4 SubFaceStrokeColor = new(2f / 255f, 2f / 255f, 0f, 1f);
    private static readonly Color4 HubOuterColor = new(0x23 / 255f, 0x22 / 255f, 0x28 / 255f, 1f);
    private static readonly Color4 HubInnerColor = new(0x15 / 255f, 0x14 / 255f, 0x19 / 255f, 1f);
    private static readonly Color4 HubRingColor = new(0x0A / 255f, 0x09 / 255f, 0x0C / 255f, 1f);
    private static readonly Color4 LcdStrokeColor = new(0x7C / 255f, 0x74 / 255f, 0x61 / 255f, 1f);
    private static readonly Color4 LcdInnerStrokeColor = new(0xC6 / 255f, 0xBE / 255f, 0xA8 / 255f, 1f);
    private static readonly Color4 LcdDividerColor = new(0xBE / 255f, 0xB6 / 255f, 0x9F / 255f, 1f);

    private readonly AltezzaGlyphCache _glyphs = new();
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushCache = [];
    private readonly IDWriteFactory? _dwrite;

    private readonly IDWriteTextFormat? _tachoNumFormat;
    private readonly IDWriteTextFormat? _tachoUnitFormat;
    private readonly IDWriteTextFormat? _mphNumFormat;
    private readonly IDWriteTextFormat? _kmhNumFormat;
    private readonly IDWriteTextFormat? _mphUnitFormat;
    private readonly IDWriteTextFormat? _kmhUnitFormat;
    private readonly IDWriteTextFormat? _gearFormat;
    private readonly IDWriteTextFormat? _subNumFormat;
    private readonly IDWriteTextFormat? _subNumSmallFormat;
    private readonly IDWriteTextFormat? _subLabelFormat;
    private readonly IDWriteTextFormat? _subLabelSmallFormat;
    private readonly IDWriteTextFormat? _boostCaptionFormat;
    private readonly IDWriteTextFormat? _boostUnitFormat;

    private ID2D1StrokeStyle? _roundStroke;
    private ID2D1StrokeStyle? _flatStroke;
    private ID2D1PathGeometry? _teethGeometry;
    private ID2D1PathGeometry? _tempWaveUpper;
    private ID2D1PathGeometry? _tempWaveLower;

    // 表盘渐变画刷：渲染目标在整个进程里只创建一次（见 GpuOverlayWindow.InitializeD3D），
    // 所以这里可以整表缓存，不必每帧重建几条渐变。
    private ID2D1RadialGradientBrush? _tachoFaceBrush;
    private ID2D1RadialGradientBrush? _speedoFaceBrush;
    private ID2D1LinearGradientBrush? _silverBrush;
    private ID2D1LinearGradientBrush? _lcdBrush;

    // 装饰小表：隔几秒轻微跳一下，再平滑跟上（每帧随机抖动会像坏了）
    private readonly Random _random = new();
    private long _lastDecorMs;
    private long _voltNextJumpMs;
    private float _voltDrift;
    private float _voltTarget;

    internal AltezzaScene(IDWriteFactory? dwrite)
    {
        if (dwrite is null)
        {
            return;
        }

        _dwrite = dwrite;

        // 原型字体是 Bahnschrift SemiCondensed；本机只有 Bahnschrift 本体。
        // 字体族名不能带 " SemiCondensed"，否则找不到会回落到默认字体，改用 FontStretch 请求窄体。
        _tachoNumFormat = CreateFormat(dwrite, 50f, FontWeight.DemiBold, FontStretch.SemiCondensed);
        _tachoUnitFormat = CreateFormat(dwrite, 22f, FontWeight.Normal, FontStretch.SemiCondensed);
        _mphNumFormat = CreateFormat(dwrite, 46f, FontWeight.Medium, FontStretch.SemiCondensed);
        _kmhNumFormat = CreateFormat(dwrite, 36f, FontWeight.Normal, FontStretch.SemiCondensed);
        _mphUnitFormat = CreateFormat(dwrite, 41f, FontWeight.Medium, FontStretch.SemiCondensed);
        _kmhUnitFormat = CreateFormat(dwrite, 31f, FontWeight.Normal, FontStretch.SemiCondensed);
        _gearFormat = CreateFormat(dwrite, 54f, FontWeight.Normal, FontStretch.SemiCondensed);
        _subNumFormat = CreateFormat(dwrite, 23f, FontWeight.Medium, FontStretch.Normal);
        _subNumSmallFormat = CreateFormat(dwrite, 20f, FontWeight.Medium, FontStretch.Normal);
        _subLabelFormat = CreateFormat(dwrite, 19f, FontWeight.Medium, FontStretch.Normal);
        _subLabelSmallFormat = CreateFormat(dwrite, 16f, FontWeight.Medium, FontStretch.Normal);
        _boostCaptionFormat = CreateFormat(dwrite, 15f, FontWeight.Normal, FontStretch.Normal);
        _boostUnitFormat = CreateFormat(dwrite, 12f, FontWeight.Normal, FontStretch.Normal);
    }

    private static IDWriteTextFormat? CreateFormat(IDWriteFactory dwrite, float size, FontWeight weight, FontStretch stretch)
    {
        // 原型文字统一居中、不换行；与 Nfs19Scene.CreateFormat 保持同一套参数顺序
        IDWriteTextFormat format = dwrite.CreateTextFormat("Bahnschrift", null!, weight, FontStyle.Normal, stretch, size, "zh-CN");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
        return format;
    }

    internal void Draw(
        ID2D1RenderTarget target,
        HudModuleLayout layout,
        ForzaDataPacket packet,
        bool hasPacket,
        ShiftPointLearner learner,
        float maxRpm,
        bool hasBoost,
        bool useLearnedRedline,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.34f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        UpdateDecorations();

        float spanRpm = ResolveTachoSpan(maxRpm);
        float redlineRpm = ResolveRedline(learner, packet, hasPacket, maxRpm, spanRpm, useLearnedRedline);

        float rpmRatio = hasPacket ? Math.Clamp(packet.CurrentEngineRpm / spanRpm, 0f, 1f) : 0f;
        float mph = hasPacket ? packet.SpeedKmh / 1.609344f : 0f;

        // 速度表允许爆表：这是有明确数字刻度的主表，超出量程时指针继续顺时针转出表盘，
        // 玩家喜欢这种效果（2026-11-12 用户明确要求）。只保留下界 0，不夹上界。
        float mphRatio = Math.Max(mph / SpeedoVmaxMph, 0f);
        string gearText = hasPacket ? FormatGear(packet.GearNumber) : "N";

        float boostValue = hasPacket && hasBoost ? Math.Clamp(packet.BoostBar, 0f, BoostVmax) : 0f;
        float voltValue = Math.Clamp(VoltBase + _voltDrift, 0f, VoltVmax);

        // 水温表与速度联动（2026-11-14 用户要求）：默认停在 100℃ 位置，
        // 速度表每顺时针转 5°，水温表顺时针转 1°；减速时反向回摆，允许超出 C~H 量程。
        float tempAngleDeg = TempAngleAtDefault + ((mphRatio * SpeedoSweep) / TempLinkRatio);

        Matrix3x2 caller = target.Transform;

        // 先按原型坐标整体平移（ContentShift），再统一缩放并落到布局位置。
        // 平移量是设计坐标，要和缩放一起乘，所以放在 Scale 之前。
        target.Transform = Matrix3x2.CreateTranslation(ContentShiftX, ContentShiftY)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation(left, top);

        DrawTachoFace(target, spanRpm, redlineRpm);
        DrawGearLcd(target, gearText);
        DrawSpeedoFace(target);
        DrawSubFaces(target);

        // 指针一律最后画，压在全部表盘之上
        DrawSubNeedle(target, BoostX, BoostY, boostValue / BoostVmax);
        DrawSubNeedle(target, VoltX, VoltY, voltValue / VoltVmax);
        DrawSubNeedleAtAngle(target, TempX, TempY, tempAngleDeg);
        DrawNeedle(target, TachoX, TachoY, TachoR * TachoNeedleLenToMajorHalf, TachoR * TachoNeedleTailRatio, TachoR * TachoHubRatio, TachoNeedleWidth, TachoAngleStart + (rpmRatio * TachoSweep));
        DrawNeedle(target, SpeedoX, SpeedoY, SpeedoR * SpeedoNeedleLenRatio, SpeedoR * SpeedoNeedleTailRatio, SpeedoR * SpeedoHubRatio, SpeedoNeedleWidth, SpeedoAngleStart + (mphRatio * SpeedoSweep));

        target.Transform = caller;
    }

    /// <summary>
    /// 转速表量程：跟随车辆最大转速并按千转向上取整（7500→8 千转）。
    /// 固定 8 千转会让高转车指针顶死、低转车剩一大段空白。
    /// </summary>
    private static float ResolveTachoSpan(float maxRpm)
    {
        int thousands = (int)Math.Ceiling(Math.Max(maxRpm, 1000f) / 1000f);
        thousands = Math.Clamp(thousands, 4, 16);
        return thousands * 1000f;
    }

    private static float ResolveRedline(
        ShiftPointLearner learner,
        in ForzaDataPacket packet,
        bool hasPacket,
        float maxRpm,
        float spanRpm,
        bool useLearnedRedline)
    {
        if (!hasPacket)
        {
            return spanRpm * 0.75f;
        }

        if (!useLearnedRedline)
        {
            // 关闭红区校正时回退到原型固定的 6.0 千转，低转车取量程 92% 避免红区出界
            return Math.Min(6000f, spanRpm * 0.92f);
        }

        double ratio = Math.Clamp(learner.GetRedlineRatio(packet), 0.5, 1.0);
        return Math.Clamp((float)ratio * maxRpm, 0f, spanRpm);
    }

    /// <summary>档位文案：-1→R、0→N、1..10 原样、11 是换挡瞬间，保持上一帧。</summary>
    private string FormatGear(int gear)
    {
        if (gear == 11)
        {
            return _stableGearText;
        }

        string text = gear switch
        {
            < 0 => "R",
            0 => "N",
            <= 10 => gear.ToString(CultureInfo.InvariantCulture),
            _ => _stableGearText,
        };

        _stableGearText = text;
        return text;
    }

    private string _stableGearText = "N";

    // ─────────────────────────── 转速表 ───────────────────────────

    private void DrawTachoFace(ID2D1RenderTarget target, float spanRpm, float redlineRpm)
    {
        float vmax = spanRpm / 1000f;

        ID2D1RadialGradientBrush face = GetFaceBrush(target, TachoX, TachoY, TachoR, speedo: false);
        target.FillEllipse(new Ellipse(new Vector2(TachoX, TachoY), TachoR, TachoR), face);

        ID2D1SolidColorBrush cream = GetBrush(target, CreamColor);
        ID2D1SolidColorBrush red = GetBrush(target, RedColor);

        // 刻度按 250 转步进、千转为大刻度；红区只换刻度颜色，不做整块遮罩。
        // 大刻度缩短 30%：外端固定在 TachoTickOut，内端往外推，保持刻度贴住盘沿。
        float majorInnerRatio = TachoTickOut
            - ((TachoTickOut - TachoTickMajorIn) * TachoTickMajorLengthScale);

        int steps = (int)Math.Round(vmax / 0.25f);
        for (int i = 0; i <= steps; i++)
        {
            float thousand = i * 0.25f;
            bool major = i % 4 == 0;
            bool inRed = thousand * 1000f >= redlineRpm - 0.5f;

            float angle = TachoAngleStart + (thousand / vmax * TachoSweep);
            float innerRatio = major
                ? majorInnerRatio
                : TachoTickOut - ((TachoTickOut - TachoTickMinorIn) * TachoTickMinorLengthScale);
            Vector2 a = PointOn(TachoX, TachoY, TachoR * innerRatio, angle);
            Vector2 b = PointOn(TachoX, TachoY, TachoR * TachoTickOut, angle);

            // 平头笔帽：圆头会在刻度两端鼓出半圆，红区大刻度尤其明显（实测像「糖葫芦」）
            target.DrawLine(
                a,
                b,
                inRed ? red : cream,
                major ? TachoTickMajorWidth : TachoTickMinorWidth,
                FlatStroke(target));
        }

        for (int i = 0; i <= (int)Math.Round(vmax); i++)
        {
            bool inRed = i * 1000f >= redlineRpm - 0.5f;
            DrawDialText(
                target,
                i.ToString(CultureInfo.InvariantCulture),
                _tachoNumFormat,
                inRed ? red : cream,
                TachoX,
                TachoY,
                TachoR * TachoNumRatio,
                TachoAngleStart + (i / vmax * TachoSweep),
                up: false);
        }

        DrawHub(target, TachoX, TachoY, TachoR * TachoHubRatio);

        // 单位照实车横排，落在盘内底部。
        // 原型是 SVG：y 表示基线，字号 22 配 letter-spacing 1.4、opacity 0.92。
        // 这里走 D2D 的行高居中，基线语义不同，实测偏低 8px，用常量补回。
        DrawSpacedText(
            _dwrite,
            target,
            "x1000r/min",
            _tachoUnitFormat,
            cream,
            TachoX,
            TachoY + (TachoR * 0.905f) - 5f - TachoUnitBaselineFix,
            220f,
            30f,
            letterSpacingPx: 1.4f,
            opacity: 0.92f);
    }

    /// <summary>
    /// 带字距的居中文字。DirectWrite 的 <c>IDWriteTextLayout1.SetCharacterSpacing</c>
    /// 单位是「em 的千分之一」，而设计稿给的是像素，所以要按字号换算。
    /// </summary>
    private static void DrawSpacedText(
        IDWriteFactory? dwrite,
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1SolidColorBrush brush,
        float centerX,
        float centerY,
        float width,
        float height,
        float letterSpacingPx,
        float opacity)
    {
        if (format is null || dwrite is null)
        {
            return;
        }

        using IDWriteTextLayout layout = dwrite.CreateTextLayout(text, format, width, height);

        // 字距只在 IDWriteTextLayout1 上有；拿不到就按原样画，不至于画不出来
        if (letterSpacingPx > 0f)
        {
            using IDWriteTextLayout1? layout1 = layout.QueryInterfaceOrNull<IDWriteTextLayout1>();
            if (layout1 is not null)
            {
                // 单位见 CharacterSpacingUnitToPx 的注释，按实测标定换算
                float em = letterSpacingPx / CharacterSpacingUnitToPx;
                layout1.SetCharacterSpacing(0f, em, 0f, new TextRange(0, (uint)text.Length));            }
        }

        float originalOpacity = brush.Opacity;
        try
        {
            brush.Opacity = originalOpacity * opacity;
            target.DrawTextLayout(
                new System.Numerics.Vector2(centerX - (width / 2f), centerY - (height / 2f)),
                layout,
                brush);
        }
        finally
        {
            brush.Opacity = originalOpacity;
        }
    }

    // ─────────────────────────── 档位液晶 ───────────────────────────

    private void DrawGearLcd(ID2D1RenderTarget target, string gearText)
    {
        float halfW = GearW / 2f;
        float halfH = GearH / 2f;

        ID2D1LinearGradientBrush lcdBrush = GetLcdBrush(target);

        ID2D1SolidColorBrush stroke = GetBrush(target, LcdStrokeColor);
        ID2D1SolidColorBrush innerStroke = GetBrush(target, LcdInnerStrokeColor);
        ID2D1SolidColorBrush divider = GetBrush(target, LcdDividerColor);
        ID2D1SolidColorBrush ink = GetBrush(target, InkColor);

        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(GearX - halfW, GearY - halfH, GearW, GearH), 8f, 8f),
            lcdBrush);
        target.DrawRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(GearX - halfW, GearY - halfH, GearW, GearH), 8f, 8f),
            stroke,
            2f);
        target.DrawRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(GearX - halfW + 4f, GearY - halfH + 4f, GearW - 8f, GearH - 8f), 5f, 5f),
            innerStroke,
            1f);
        target.DrawLine(
            new Vector2(GearX, GearY - halfH + 7f),
            new Vector2(GearX, GearY + halfH - 7f),
            divider,
            1f);

        // 1~9 / N / R 只占右格；两位档位时左格才亮起，平时留一个浅色 "1" 当占位
        bool twoDigits = gearText.Length >= 2;
        string tensText = twoDigits ? gearText[..1] : "1";
        string onesText = twoDigits ? gearText[1..] : gearText;

        float originalOpacity = ink.Opacity;
        try
        {
            ink.Opacity = twoDigits ? originalOpacity : 0.24f;
            HudDraw.CenteredText(target, tensText, _gearFormat, ink, GearX - (GearW / 4f), GearY + 1f, 50f, 70f);
            ink.Opacity = originalOpacity;
            HudDraw.CenteredText(target, onesText, _gearFormat, ink, GearX + (GearW / 4f), GearY + 1f, 50f, 70f);
        }
        finally
        {
            ink.Opacity = originalOpacity;
        }
    }

    // ─────────────────────────── 速度表 ───────────────────────────

    private void DrawSpeedoFace(ID2D1RenderTarget target)
    {
        ID2D1RadialGradientBrush face = GetFaceBrush(target, SpeedoX, SpeedoY, SpeedoR, speedo: true);
        ID2D1SolidColorBrush faceStroke = GetBrush(target, FaceStrokeColor);
        target.FillEllipse(new Ellipse(new Vector2(SpeedoX, SpeedoY), SpeedoR, SpeedoR), face);
        target.DrawEllipse(new Ellipse(new Vector2(SpeedoX, SpeedoY), SpeedoR, SpeedoR), faceStroke, 3f);

        // 齿环与银盘共用一条 userSpaceOnUse 渐变轴，颜色才对得上
        ID2D1LinearGradientBrush silver = GetSilverBrush(target);

        ID2D1PathGeometry? teeth = BuildTeeth(target);
        if (teeth is not null)
        {
            target.FillGeometry(teeth, silver);
        }

        float diskR = SpeedoR * SpeedoDiskRatio;
        target.FillEllipse(new Ellipse(new Vector2(SpeedoX, SpeedoY), diskR, diskR), silver);
        target.DrawEllipse(new Ellipse(new Vector2(SpeedoX, SpeedoY), diskR, diskR), silver, 1.5f);

        ID2D1SolidColorBrush innerRing = GetBrush(target, DiskInnerRingColor);
        target.DrawEllipse(new Ellipse(new Vector2(SpeedoX, SpeedoY), diskR - 7f, diskR - 7f), innerRing, 7f);

        ID2D1SolidColorBrush cream = GetBrush(target, CreamColor);
        ID2D1SolidColorBrush gold = GetBrush(target, GoldColor);

        // 外圈：每 20 mph 一个数字，数字之间（每 10）一个奶油白圆点；外圈没有刻度线
        float dotRadius = SpeedoR - 36f;
        for (float v = SpeedoStepMph / 2f; v < SpeedoVmaxMph - 0.01f; v += SpeedoStepMph)
        {
            Vector2 p = PointOn(SpeedoX, SpeedoY, dotRadius, SpeedoAngleStart + (v / SpeedoVmaxMph * SpeedoSweep));
            target.FillEllipse(new Ellipse(p, 8f, 8f), cream);
        }

        int mphSteps = (int)Math.Round(SpeedoVmaxMph / SpeedoStepMph);
        for (int i = 0; i <= mphSteps; i++)
        {
            float v = i * SpeedoStepMph;
            DrawDialText(
                target,
                v.ToString("F0", CultureInfo.InvariantCulture),
                _mphNumFormat,
                cream,
                SpeedoX,
                SpeedoY,
                SpeedoR * SpeedoNumRatio,
                SpeedoAngleStart + (v / SpeedoVmaxMph * SpeedoSweep),
                up: false);
        }

        // 内圈：每 20 km/h 一个金色数字，数字之间一个金色小点
        int kmhSteps = (int)Math.Round(SpeedoKmhMax / SpeedoStepKmh);
        for (int i = 0; i <= kmhSteps; i++)
        {
            float v = i * SpeedoStepKmh;
            DrawDialText(
                target,
                v.ToString("F0", CultureInfo.InvariantCulture),
                _kmhNumFormat,
                gold,
                SpeedoX,
                SpeedoY,
                SpeedoR * SpeedoKmhNumRatio,
                SpeedoAngleStart + (v / SpeedoKmhMax * SpeedoSweep),
                up: false);
        }

        for (float v = SpeedoStepKmh / 2f; v < SpeedoKmhMax - 0.01f; v += SpeedoStepKmh)
        {
            Vector2 p = PointOn(SpeedoX, SpeedoY, SpeedoR * SpeedoKmhNumRatio, SpeedoAngleStart + (v / SpeedoKmhMax * SpeedoSweep));
            target.FillEllipse(new Ellipse(p, 2.5f, 2.5f), gold);
        }

        // 两个单位标签落在底部缺口里，字头朝外（up），与各自数字环同字号同字重
        DrawDialText(target, "km/h", _kmhUnitFormat, gold, SpeedoX, SpeedoY, SpeedoR * SpeedoKmhNumRatio, UnitKmhAngle, up: true);
        DrawDialText(target, "MPH", _mphUnitFormat, cream, SpeedoX, SpeedoY, SpeedoR * SpeedoNumRatio, UnitMphAngle, up: true);

        DrawLexusWordmark(target);

        DrawHub(target, SpeedoX, SpeedoY, SpeedoR * SpeedoHubRatio);
    }

    /// <summary>90 颗梯形齿合并成一条几何，只建一次；外沿略窄于内沿做出梯形收口。</summary>
    private ID2D1PathGeometry? BuildTeeth(ID2D1RenderTarget target)
    {
        if (_teethGeometry is not null)
        {
            return _teethGeometry;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            float period = 360f / SpeedoTeeth;
            float half = period * SpeedoToothRatio / 2f;
            float rIn = SpeedoR * SpeedoTeethInRatio;
            float rOut = SpeedoR * SpeedoTeethOutRatio;

            for (int i = 0; i < SpeedoTeeth; i++)
            {
                float a = period * i;
                Vector2 p1 = PointOn(SpeedoX, SpeedoY, rIn, a - half);
                Vector2 p2 = PointOn(SpeedoX, SpeedoY, rIn, a + half);
                Vector2 p3 = PointOn(SpeedoX, SpeedoY, rOut, a + (half * 0.78f));
                Vector2 p4 = PointOn(SpeedoX, SpeedoY, rOut, a - (half * 0.78f));

                sink.BeginFigure(p1, FigureBegin.Filled);
                sink.AddLine(p2);
                sink.AddLine(p3);
                sink.AddLine(p4);
                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        _teethGeometry = geometry;
        return geometry;
    }

    private void DrawLexusWordmark(ID2D1RenderTarget target)
    {
        ID2D1PathGeometry? geometry = _glyphs.Wordmark(target);
        if (geometry is null)
        {
            return;
        }

        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateScale(
                LexusW / AltezzaGlyphCache.BoxWidth,
                LexusH / AltezzaGlyphCache.BoxHeight)
            * Matrix3x2.CreateTranslation(LexusX - (LexusW / 2f), LexusY - (LexusH / 2f))
            * saved;

        target.FillGeometry(geometry, GetBrush(target, LexusColor));
        target.Transform = saved;
    }

    // ─────────────────────────── 三小表 ───────────────────────────

    private void DrawSubFaces(ID2D1RenderTarget target)
    {
        DrawSubFace(target, BoostX, BoostY, BoostVmax, BoostMajor, BoostMinor);
        DrawSubLabelsBoost(target);
        DrawSubFace(target, VoltX, VoltY, VoltVmax, VoltMajor, VoltMinor);
        DrawSubLabelsVolt(target);
        DrawSubFace(target, TempX, TempY, TempVmax, TempMajor, TempMinor);
        DrawSubLabelsTemp(target);
    }

    private void DrawSubFace(ID2D1RenderTarget target, float cx, float cy, float vmax, float major, float minor)
    {
        ID2D1SolidColorBrush faceBrush = GetBrush(target, SubFaceColor);
        ID2D1SolidColorBrush stroke = GetBrush(target, SubFaceStrokeColor);
        ID2D1SolidColorBrush cream = GetBrush(target, CreamColor);

        target.FillEllipse(new Ellipse(new Vector2(cx, cy), SubR * 0.97f, SubR * 0.97f), faceBrush);
        target.DrawEllipse(new Ellipse(new Vector2(cx, cy), SubR * 0.97f, SubR * 0.97f), stroke, SubR * 0.12f * 0.4f);

        int steps = (int)Math.Round(vmax / minor);
        int majorEvery = Math.Max(1, (int)Math.Round(major / minor));
        for (int i = 0; i <= steps; i++)
        {
            bool isMajor = i % majorEvery == 0;
            float angle = SubAngleStart + (i / (float)steps * SubSweep);
            Vector2 a = PointOn(cx, cy, SubR * (isMajor ? SubTickMajorIn : SubTickMinorIn), angle);
            Vector2 b = PointOn(cx, cy, SubR * SubTickOut, angle);
            target.DrawLine(a, b, cream, isMajor ? 3.0f : 1.5f, RoundStroke(target));
        }

        DrawHub(target, cx, cy, SubR * SubHubRatio);
    }

    /// <summary>小表数字直立排布（不随弧旋转），小盘才能一眼读数。</summary>
    private void DrawSubLabel(ID2D1RenderTarget target, float cx, float cy, float vmax, float value, string text, IDWriteTextFormat? format)
    {
        Vector2 p = PointOn(cx, cy, SubR * SubLabelRatio, SubAngleStart + (value / vmax * SubSweep));
        HudDraw.CenteredText(target, text, format, GetBrush(target, CreamColor), p.X, p.Y, 60f, 34f);
    }

    private void DrawSubLabelsBoost(ID2D1RenderTarget target)
    {
        DrawSubLabel(target, BoostX, BoostY, BoostVmax, 0f, "0", _subNumFormat);
        DrawSubLabel(target, BoostX, BoostY, BoostVmax, 1.5f, "1.5", _subNumSmallFormat);
        DrawSubLabel(target, BoostX, BoostY, BoostVmax, 3f, "3", _subNumFormat);

        // BOOST + bar 作为一个整体围绕文字块中心缩放，两行字与行距一起放大
        float txtX = BoostX + (SubR * 0.33f);
        float txtA = BoostY - 8f;
        float txtB = BoostY + 11f;
        float midY = (txtA + txtB) / 2f;
        const float scaleK = 1.3f * 0.9f;
        const float shiftX = 15f;

        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scaleK)
            * Matrix3x2.CreateTranslation(txtX + shiftX - (txtX * scaleK), midY - (midY * scaleK))
            * saved;

        // 这两行原本也试过补 letter-spacing: 0.5，但 SetCharacterSpacing 的换算系数
        // 随字号变化（此处 15/12 号，与转速单位的 22 号不同），实测被放大成三倍宽度，
        // 反而严重偏离原型。0.5px 字距对 3~5 个字符只有 1.5~2.5px 影响，不做更准。
        HudDraw.CenteredText(target, "BOOST", _boostCaptionFormat, GetBrush(target, CreamColor), txtX, txtA, 80f, 20f);
        HudDraw.CenteredText(target, "bar", _boostUnitFormat, GetBrush(target, GoldColor), txtX, txtB, 80f, 18f);
        target.Transform = saved;
    }

    private void DrawSubLabelsVolt(ID2D1RenderTarget target)
    {
        DrawSubLabel(target, VoltX, VoltY, VoltVmax, 0f, "0", _subLabelFormat);
        DrawSubLabel(target, VoltX, VoltY, VoltVmax, 9f, "9", _subLabelFormat);
        DrawSubLabel(target, VoltX, VoltY, VoltVmax, 18f, "18V", _subLabelSmallFormat);

        // 电池图案：以小表盘心为基准放大 1.4 倍并右移 20px
        float bx = VoltX + (SubR * SubLabelRatio);
        float by = VoltY + 2f;
        const float scaleK = 1.4f;
        const float shiftX = 20f;

        ID2D1SolidColorBrush cream = GetBrush(target, CreamColor);
        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scaleK)
            * Matrix3x2.CreateTranslation(bx + shiftX - (bx * scaleK), by - (by * scaleK))
            * saved;

        target.DrawRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(bx - 10f, by - 6f, 20f, 12f), 2f, 2f),
            cream,
            1.5f);
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(bx - 3.5f, by - 9f, 7f, 3.5f), 1f, 1f),
            cream);
        target.DrawLine(new Vector2(bx - 5f, by - 2f), new Vector2(bx - 5f, by + 2f), cream, 1.3f);
        target.DrawLine(new Vector2(bx + 5f, by - 2f), new Vector2(bx + 5f, by + 2f), cream, 1.3f);

        target.Transform = saved;
    }

    private void DrawSubLabelsTemp(ID2D1RenderTarget target)
    {
        DrawSubLabel(target, TempX, TempY, TempVmax, 1f, "H", _subLabelFormat);
        DrawSubLabel(target, TempX, TempY, TempVmax, 0f, "C", _subLabelFormat);

        float tx = TempX + (SubR * SubLabelRatio);
        float ty = TempY + 4f;
        const float scaleK = 1.4f;
        const float shiftX = 20f;

        ID2D1SolidColorBrush cream = GetBrush(target, CreamColor);
        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scaleK)
            * Matrix3x2.CreateTranslation(tx + shiftX - (tx * scaleK), ty - (ty * scaleK))
            * saved;

        target.DrawLine(new Vector2(tx, ty - 13f), new Vector2(tx, ty + 3f), cream, 1.8f);
        target.DrawEllipse(new Ellipse(new Vector2(tx, ty + 7f), 4.2f, 4.2f), cream, 1.8f);

        target.DrawGeometry(BuildTempWaveUpper(target), cream, 1.4f);
        target.DrawGeometry(BuildTempWaveLower(target), cream, 1.4f);

        target.Transform = saved;
    }

    /// <summary>温度计上那道长波浪（三段二次贝塞尔，交替起伏）。</summary>
    private ID2D1PathGeometry BuildTempWaveUpper(ID2D1RenderTarget target)
    {
        if (_tempWaveUpper is not null)
        {
            return _tempWaveUpper;
        }

        float tx = TempX + (SubR * SubLabelRatio);
        float ty = TempY + 4f;

        _tempWaveUpper = BuildWave(target, new Vector2(tx - 10f, ty), 3, 5f, 2.6f);
        return _tempWaveUpper;
    }

    /// <summary>温度计下那道短波浪（两段）。</summary>
    private ID2D1PathGeometry BuildTempWaveLower(ID2D1RenderTarget target)
    {
        if (_tempWaveLower is not null)
        {
            return _tempWaveLower;
        }

        float tx = TempX + (SubR * SubLabelRatio);
        float ty = TempY + 4f;

        _tempWaveLower = BuildWave(target, new Vector2(tx - 7f, ty + 5f), 2, 5f, 2.6f);
        return _tempWaveLower;
    }

    private static ID2D1PathGeometry BuildWave(ID2D1RenderTarget target, Vector2 start, int segments, float dx, float dy)
    {
        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            // 起伏方向交替（对应原型 q...t... 的平滑二次贝塞尔）
            sink.BeginFigure(start, FigureBegin.Hollow);
            Vector2 current = start;
            for (int i = 0; i < segments; i++)
            {
                float sign = i % 2 == 0 ? -1f : 1f;
                Vector2 control = new(current.X + (dx / 2f), current.Y + (sign * dy));
                current = new Vector2(current.X + dx, current.Y);
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = control, Point2 = current });
            }

            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return geometry;
    }

    private void DrawSubNeedle(ID2D1RenderTarget target, float cx, float cy, float ratio)
    {
        float angle = SubAngleStart + (Math.Clamp(ratio, 0f, 1f) * SubSweep);
        DrawNeedle(target, cx, cy, SubR * SubNeedleLenRatio, SubR * SubHubRatio, SubR * SubHubRatio, SubNeedleWidth, angle);
    }

    /// <summary>
    /// 直接按角度画小表指针。水温表与速度联动后角度可能超出 C~H 量程，
    /// 不能走 <see cref="DrawSubNeedle"/> 的 ratio 路径（那里夹了 0~1）。
    /// </summary>
    private void DrawSubNeedleAtAngle(ID2D1RenderTarget target, float cx, float cy, float angleDeg)
    {
        DrawNeedle(target, cx, cy, SubR * SubNeedleLenRatio, SubR * SubHubRatio, SubR * SubHubRatio, SubNeedleWidth, angleDeg);
    }

    // ─────────────────────────── 轴心与指针 ───────────────────────────

    /// <summary>
    /// 轴心：暗底两层 + 一圈深色粗外环。外环在这里就画好，
    /// 指针在之后单独绘制，才能压在外环之上而不被拦腰遮断。
    /// </summary>
    private void DrawHub(ID2D1RenderTarget target, float cx, float cy, float r)
    {
        Vector2 center = new(cx, cy);
        target.FillEllipse(new Ellipse(center, r * 0.92f, r * 0.92f), GetBrush(target, HubOuterColor));
        target.FillEllipse(new Ellipse(center, r * 0.70f, r * 0.70f), GetBrush(target, HubInnerColor));
        target.DrawEllipse(new Ellipse(center, r * 0.96f, r * 0.96f), GetBrush(target, HubRingColor), r * 0.28f);
    }

    /// <summary>
    /// 指针两段式：红色针身从轴心伸向针尖（圆头），白色尾巴反向穿过圆心伸出一截。
    /// 轴心外环在指针之前画好（见 <see cref="DrawHub"/>），
    /// 否则深色粗环会把针身拦腰遮断一段。
    /// </summary>
    private void DrawNeedle(
        ID2D1RenderTarget target,
        float cx,
        float cy,
        float length,
        float tailLength,
        float hubRadius,
        float width,
        float angleDeg)
    {
        float startGap = -hubRadius * 0.12f;

        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateRotation(angleDeg * MathF.PI / 180f, new Vector2(cx, cy)) * saved;

        ID2D1StrokeStyle stroke = RoundStroke(target);
        Vector2 root = new(cx + startGap, cy);

        target.DrawLine(root, new Vector2(cx + length, cy), GetBrush(target, RedColor), width, stroke);
        target.DrawLine(root, new Vector2(cx - tailLength, cy), GetBrush(target, NeedleTailColor), width * 0.60f, stroke);

        target.Transform = saved;
    }

    // ─────────────────────────── 文字 ───────────────────────────

    /// <summary>
    /// 沿半径排布的刻度文字：以盘心为轴旋转，<paramref name="up"/> 时文字头朝外
    /// （底部缺口里的单位标签用），否则正常朝外。
    /// </summary>
    private static void DrawDialText(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float cx,
        float cy,
        float radius,
        float angleDeg,
        bool up)
    {
        if (format is null)
        {
            return;
        }

        Vector2 p = PointOn(cx, cy, radius, angleDeg);
        float rotate = up ? angleDeg - 90f : angleDeg + 90f;

        Matrix3x2 saved = target.Transform;
        target.Transform = Matrix3x2.CreateRotation(rotate * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(p.X, p.Y)
            * saved;

        target.DrawText(text, format, new Rect(-200f, -80f, 400f, 160f), brush);
        target.Transform = saved;
    }

    // ─────────────────────────── 装饰小表 ───────────────────────────

    /// <summary>
    /// 装饰小表隔几秒轻微跳一下再平滑停住。每帧随机抖动会像仪表坏了，
    /// 所以目标是"每隔几秒换一个目标值"，中间用 180ms 时间常数追上去。
    /// </summary>
    private void UpdateDecorations()
    {
        long now = Environment.TickCount64;
        if (_lastDecorMs == 0)
        {
            _lastDecorMs = now;
        }

        float dt = Math.Clamp((now - _lastDecorMs) / 1000f, 0f, 0.5f);
        _lastDecorMs = now;

        if (now >= _voltNextJumpMs)
        {
            _voltTarget = NextDrift(0.42f);
            _voltNextJumpMs = now + _random.Next(2200, 6800);
        }

        // 水温表已改为与速度联动，不再需要随机漂移
        float k = 1f - MathF.Exp(-dt / 0.18f);
        _voltDrift += (_voltTarget - _voltDrift) * k;
    }

    private float NextDrift(float amplitude)
    {
        return (float)((_random.NextDouble() * 2.0) - 1.0) * amplitude;
    }

    // ─────────────────────────── 公共工具 ───────────────────────────

    /// <summary>与原型 px() 一致：0° 指向正右方，顺时针增大（y 轴向下）。</summary>
    private static Vector2 PointOn(float cx, float cy, float radius, float angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        return new Vector2(
            cx + (float)(radius * Math.Cos(radians)),
            cy + (float)(radius * Math.Sin(radians)));
    }

    /// <summary>
    /// 原型 gFace：中心偏左上、半径 1.36R 的径向渐变。
    /// 渲染目标在进程内只创建一次，画刷按表缓存后每帧复用。
    /// </summary>
    private ID2D1RadialGradientBrush GetFaceBrush(ID2D1RenderTarget target, float cx, float cy, float radius, bool speedo)
    {
        if (speedo ? _speedoFaceBrush is not null : _tachoFaceBrush is not null)
        {
            return speedo ? _speedoFaceBrush! : _tachoFaceBrush!;
        }

        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0x27 / 255f, 0x26 / 255f, 0x2C / 255f, 1f) },
            new GradientStop { Position = 0.58f, Color = new Color4(0x1A / 255f, 0x19 / 255f, 0x1E / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0x0E / 255f, 0x0D / 255f, 0x11 / 255f, 1f) },
        ]);

        ID2D1RadialGradientBrush brush = target.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = new Vector2(cx - (radius * 0.08f), cy - (radius * 0.2f)),
                GradientOriginOffset = Vector2.Zero,
                RadiusX = radius * 1.36f,
                RadiusY = radius * 1.36f,
            },
            stops);

        if (speedo)
        {
            _speedoFaceBrush = brush;
        }
        else
        {
            _tachoFaceBrush = brush;
        }

        return brush;
    }

    /// <summary>银盘与齿环共用的 userSpaceOnUse 渐变轴，颜色才对得上。</summary>
    private ID2D1LinearGradientBrush GetSilverBrush(ID2D1RenderTarget target)
    {
        if (_silverBrush is not null)
        {
            return _silverBrush;
        }

        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0x8E / 255f, 0x8B / 255f, 0x84 / 255f, 1f) },
            new GradientStop { Position = 0.42f, Color = new Color4(0x7D / 255f, 0x7A / 255f, 0x73 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0x67 / 255f, 0x64 / 255f, 0x5E / 255f, 1f) },
        ]);

        _silverBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(SilverAxisX1, SilverAxisY1),
                EndPoint = new Vector2(SilverAxisX2, SilverAxisY2),
            },
            stops);

        return _silverBrush;
    }

    private ID2D1LinearGradientBrush GetLcdBrush(ID2D1RenderTarget target)
    {
        if (_lcdBrush is not null)
        {
            return _lcdBrush;
        }

        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0xF1 / 255f, 0xEC / 255f, 0xE0 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0xDC / 255f, 0xD5 / 255f, 0xC3 / 255f, 1f) },
        ]);

        _lcdBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(GearX - (GearW / 2f), GearY - (GearH / 2f)),
                EndPoint = new Vector2(GearX - (GearW * 0.44f), GearY + (GearH / 2f)),
            },
            stops);

        return _lcdBrush;
    }

    private ID2D1StrokeStyle RoundStroke(ID2D1RenderTarget target)
    {
        _roundStroke ??= target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
                LineJoin = LineJoin.Round,
            });

        return _roundStroke;
    }

    /// <summary>
    /// 平头笔帽。刻度线用它：圆头会在两端各鼓出半个线宽，
    /// 大刻度（5.4px 加粗到 6.48px）尤其明显，看起来像一串圆球而不是直线。
    /// </summary>
    private ID2D1StrokeStyle FlatStroke(ID2D1RenderTarget target)
    {
        _flatStroke ??= target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties
            {
                StartCap = CapStyle.Flat,
                EndCap = CapStyle.Flat,
                LineJoin = LineJoin.Miter,
            });

        return _flatStroke;
    }

    private ID2D1SolidColorBrush GetBrush(ID2D1RenderTarget target, Color4 color)
    {
        uint key = ((uint)(color.R * 255f) << 24)
            | ((uint)(color.G * 255f) << 16)
            | ((uint)(color.B * 255f) << 8)
            | (uint)(color.A * 255f);

        if (!_brushCache.TryGetValue(key, out ID2D1SolidColorBrush? brush))
        {
            brush = target.CreateSolidColorBrush(color);
            _brushCache[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        foreach (ID2D1SolidColorBrush brush in _brushCache.Values)
        {
            brush.Dispose();
        }

        _brushCache.Clear();

        _tachoFaceBrush?.Dispose();
        _speedoFaceBrush?.Dispose();
        _silverBrush?.Dispose();
        _lcdBrush?.Dispose();
        _teethGeometry?.Dispose();
        _tempWaveUpper?.Dispose();
        _tempWaveLower?.Dispose();
        _roundStroke?.Dispose();
        _flatStroke?.Dispose();
        _glyphs.Dispose();

        _tachoNumFormat?.Dispose();
        _tachoUnitFormat?.Dispose();
        _mphNumFormat?.Dispose();
        _kmhNumFormat?.Dispose();
        _mphUnitFormat?.Dispose();
        _kmhUnitFormat?.Dispose();
        _gearFormat?.Dispose();
        _subNumFormat?.Dispose();
        _subNumSmallFormat?.Dispose();
        _subLabelFormat?.Dispose();
        _subLabelSmallFormat?.Dispose();
        _boostCaptionFormat?.Dispose();
        _boostUnitFormat?.Dispose();
    }
}
