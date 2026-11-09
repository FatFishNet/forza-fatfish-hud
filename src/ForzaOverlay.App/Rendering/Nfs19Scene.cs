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
/// 仿NFS19 仪表渲染器。几何来自最终版原型 <c>temp/panel-speedo-nfs19</c>，内部坐标按原型 640×479 书写。
/// 对外基准尺寸是 601×416（图形包围盒 + 5px 安全边距），差异由 <see cref="ContentShiftX"/> 平移吸收。
/// 由 <see cref="HudScene"/> 委派调用，本类只负责画，不参与遥测采集与布局调度。
/// </summary>
internal sealed class Nfs19Scene : IDisposable
{
    private const float BaseWidth = 601f;
    private const float BaseHeight = 416f;

    // 原型画布 640×479，但图形实际只占 590.16×405.72（左上角在 7.84,43）。
    // 内部坐标仍按原型写，绘制时整体平移，让内容左上角落到 (5,5) 的 5px 安全边距上——
    // 这样基准尺寸等于图形包围盒，布局编辑器的框才不会被空白撑大。
    private const float ContentShiftX = -2.84f;
    private const float ContentShiftY = -38f;

    // 主表：圆心、扫掠、刻度与底带半径
    private const float CenterX = 236f;
    private const float CenterY = 237f;
    private const float AngleStart = 235f;
    private const float AngleSweep = 251f;
    private const float TickOuter = 183f;
    private const float TickMajor = 158f;
    private const float TickMinor = 170f;
    private const float LabelRadius = 131f;
    private const float GlyphScaleX = 0.98f;
    private const float GlyphScaleY = 1.05f;
    private const float GlyphAdvance = 23f;
    private const float GlyphBoxWidth = 20f;
    private const float GlyphBaseline = 9.45f;
    private const float BandOuter = 189f;
    private const float BandInner = 131f;
    private const float BandPad = 5f;

    // 增压小表：整组 translate(-80 -74) scale(1.44)
    private const float BoostTranslateX = -80f;
    private const float BoostTranslateY = -74f;
    private const float BoostScale = 1.44f;
    private const float BoostCenterX = 128f;
    private const float BoostCenterY = 296f;
    private const float BoostRadius = 58f;
    private const float BoostAngle = 120f;
    private const float BoostTickOuter = 58f;
    private const float BoostTickMajor = 53.1f;
    private const float BoostTickMinor = 54.8f;
    private const float BoostBandOuter = 63f;
    private const float BoostBandInner = 41f;
    private const float BoostBandPad = 4f;
    private const float BoostBandRedWidth = 15f;
    private const float BoostLabelRadius = 41f;
    private const float BoostZeroScale = 0.55f;
    /// <summary>增压表量程：0 居中，左右各到此值占满 120°。超出即爆表外摆。</summary>
    private const float BoostFullScaleBar = 2.0f;
    private const float MainOffsetX = 168f;
    private const float NeedleTipY = 59f;
    private const float NeedleTailY = 271f;

    // 液晶窗与数字槽位
    private const float LcdX = 159f;
    private const float LcdY = 343.5f;
    private const float LcdWidth = 154f;
    private const float LcdHeight = 57f;
    private const float LcdRadius = 4f;
    private const float SpeedSlotX = 187.5f;
    private const float SpeedSlotStep = 33.09f;
    private const float SpeedSlotY = 347.8f;
    /// <summary>时速七段缩放：数字高 44×1.0545 ≈ 46.4，比 1.10 时小 2 个设计像素。</summary>
    private const float SpeedSlotScale = 1.0545f;
    private const float GearSlotY = 275f;
    private const float GearSlotScale = 1.02f;
    private const float GearGap = 4f;
    /// <summary>单个档位槽的宽度（含字形两侧留白），两位数字总宽 = 2×此值 + 间距。</summary>
    private const float GearSlotWidth = 26f * GearSlotScale;

    // 配色（来自原型 CSS）
    private static readonly Color4 TickColor = new(0xC4 / 255f, 0xED / 255f, 0xFF / 255f, 1f);
    private static readonly Color4 RedColor = new(0xFF / 255f, 0x3A / 255f, 0x2F / 255f, 1f);
    private static readonly Color4 GlyphColor = new(0xBD / 255f, 0xE8 / 255f, 0xFF / 255f, 1f);
    private static readonly Color4 CaptionColor = new(0xB4 / 255f, 0xE4 / 255f, 0xFF / 255f, 1f);
    private static readonly Color4 UnitColor = new(0xBD / 255f, 0xE8 / 255f, 0xFF / 255f, 1f);
    private static readonly Color4 BandBlueColor = new(139f / 255f, 220f / 255f, 255f / 255f, 0.035f);
    private static readonly Color4 BandRedColor = new(1f, 64f / 255f, 72f / 255f, 0.05f);
    private static readonly Color4 SpeedOnColor = new(0x16 / 255f, 0x28 / 255f, 0x3F / 255f, 1f);
    private static readonly Color4 SpeedOffColor = new(0x3A / 255f, 0x55 / 255f, 0x66 / 255f, 0.14f);
    private static readonly Color4 GearOnColor = new(1f, 0x62 / 255f, 0x3C / 255f, 1f);
    /// <summary>
    /// 档位熄灭段的"幽灵色"：与亮段同色（#FF623C），仅靠低透明度压暗，
    /// 使未点亮的笔画保留同色系的淡淡轮廓，数字整体形状始终可辨。
    /// 与原型（熄灭段完全隐藏）不同，属于有意调整。
    /// </summary>
    private static readonly Color4 GearOffColor = new(1f, 0x62 / 255f, 0x3C / 255f, 0.21f);

    private readonly Nfs19GlyphCache _glyphs = new();
    private readonly BoostPresence boostPresence = new();
    private readonly IDWriteTextFormat? _captionFormat;
    private readonly IDWriteTextFormat? _captionSmallFormat;
    private readonly IDWriteTextFormat? _unitFormat;
    private readonly IDWriteTextFormat? _boostCaptionFormat;
    private readonly IDWriteTextFormat? _boostSignFormat;

    private ID2D1StrokeStyle? _roundStroke;
    private ID2D1PathGeometry? _needleBody;
    private ID2D1PathGeometry? _needleCore;
    private ID2D1PathGeometry? _boostNeedleBody;
    private ID2D1PathGeometry? _boostNeedleCore;
    private string _stableGearText = "N";
    private ID2D1PathGeometry? _mainBandBlue;
    private ID2D1PathGeometry? _mainBandRed;
    private ID2D1PathGeometry? _boostBandBlue;
    private ID2D1PathGeometry? _boostBandRed;

    // 几何按 (maxRpm, redlineRpm) 缓存，换车时重建一次
    private float _cachedMaxRpm;
    private float _cachedRedlineRpm;
    private bool _geometryBuilt;

    internal Nfs19Scene(IDWriteFactory? dwrite)
    {
        if (dwrite is null)
        {
            return;
        }

        // RPM / x1000 两行字号与行距按同一比例缩放，相对大小保持不变；
        // km/h 与主标签 RPM 同级同字号
        _captionFormat = CreateFormat(dwrite, 17.82f, FontWeight.Bold);
        _captionSmallFormat = CreateFormat(dwrite, 13.77f, FontWeight.Bold);
        _unitFormat = CreateFormat(dwrite, 17.82f, FontWeight.Bold);
        _boostCaptionFormat = CreateFormat(dwrite, 15f, FontWeight.Bold);
        _boostSignFormat = CreateFormat(dwrite, 28f, FontWeight.Bold);
    }

    private static IDWriteTextFormat? CreateFormat(IDWriteFactory dwrite, float size, FontWeight weight)
    {
        // 原型文字统一居中、不换行；与 HudScene.CreateFormat 保持同一套参数顺序
        IDWriteTextFormat format = dwrite.CreateTextFormat("Microsoft YaHei UI", null!, weight, FontStyle.Normal, FontStretch.Normal, size, "zh-CN");
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
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.3962f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        float redlineRpm = hasPacket ? ResolveRedline(learner, packet, maxRpm) : maxRpm * 0.75f;
        EnsureGeometry(target, maxRpm, redlineRpm);

        ID2D1SolidColorBrush tickBrush = GetBrush(target, TickColor);
        ID2D1SolidColorBrush redBrush = GetBrush(target, RedColor);
        ID2D1SolidColorBrush glyphBrush = GetBrush(target, GlyphColor);
        ID2D1SolidColorBrush captionBrush = GetBrush(target, CaptionColor);
        ID2D1SolidColorBrush unitBrush = GetBrush(target, UnitColor);

        // 先按原型坐标整体平移（ContentShift），再统一缩放并落到布局位置。
        // 平移量是设计坐标，要和缩放一起乘，所以放在 Scale 之前。
        target.Transform = Matrix3x2.CreateTranslation(ContentShiftX, ContentShiftY)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation(left, top);

        float rpmRatio = hasPacket && maxRpm > 1f ? packet.CurrentEngineRpm / maxRpm : 0f;
        int speedKmh = hasPacket ? (int)Math.Round(packet.SpeedKmh) : 0;
        string gearText = hasPacket ? FormatGear(packet.GearNumber) : "N";

        DrawMainDtr(target, maxRpm, redlineRpm, rpmRatio, speedKmh, gearText, hasPacket, tickBrush, redBrush, glyphBrush, captionBrush, unitBrush);
        DrawBoostGauge(target, hasPacket ? packet.BoostBar : 0f, hasPacket && boostPresence.HasBoost, tickBrush, redBrush, glyphBrush, captionBrush);

        target.Transform = Matrix3x2.Identity;
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

    private void DrawMainDtr(
        ID2D1RenderTarget target,
        float maxRpm,
        float redlineRpm,
        float rpmRatio,
        int speedKmh,
        string gearText,
        bool hasData,
        ID2D1SolidColorBrush tickBrush,
        ID2D1SolidColorBrush redBrush,
        ID2D1SolidColorBrush glyphBrush,
        ID2D1SolidColorBrush captionBrush,
        ID2D1SolidColorBrush unitBrush)
    {
        Matrix3x2 outer = target.Transform;
        target.Transform = Matrix3x2.CreateTranslation(MainOffsetX, 0f) * outer;

        if (_mainBandBlue is not null)
        {
            target.FillGeometry(_mainBandBlue, GetBrush(target, BandBlueColor));
        }

        if (_mainBandRed is not null)
        {
            target.FillGeometry(_mainBandRed, GetBrush(target, BandRedColor));
        }

        DrawMainTicks(target, maxRpm, redlineRpm, tickBrush, redBrush);

        DrawDialLabels(target, maxRpm, redlineRpm, glyphBrush, redBrush);

        DrawNeedle(target, rpmRatio);

        DrawLcdWindow(target);
        DrawSpeedDigits(target, speedKmh, hasData);
        DrawGearDigits(target, gearText, hasData);

        // 两行以组心 162.775 为轴等比缩放，组心位置不变；km/h 与 RPM 同字号，
        // 块高按字号同步放大并保持块心 420 不动
        DrawTextBlock(target, "RPM", _captionFormat, captionBrush, CenterX, 142f, 163.8f, 24.57f);
        DrawTextBlock(target, "x1000", _captionSmallFormat, captionBrush, CenterX, 160.68f, 112.32f, 21.06f);
        DrawTextBlock(target, "km/h", _unitFormat, unitBrush, CenterX, 406.65f, 155.9f, 26.7f);

        target.Transform = outer;
    }

    /// <summary>
    /// 主表指针：原型是三条路径（外发光宽描边 + 热渐变主体 + 亮芯），绕 (236,237) 旋转。
    /// 渐变是 userSpaceOnUse 竖直方向，因此旋转前先把渐变轴建在指针的竖直位置上。
    /// </summary>
    private void DrawNeedle(ID2D1RenderTarget target, float ratio)
    {
        float angle = AngleStart + (Math.Clamp(ratio, 0f, 1f) * AngleSweep);

        // 指针根部红晕（原型 r=42 的 radialGradient），先画，压在指针下面
        using (ID2D1GradientStopCollection haloStops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 0x53 / 255f, 0x34 / 255f, 0.54f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1f, 0x39 / 255f, 0x2B / 255f, 0.27f) },
            new GradientStop { Position = 1f, Color = new Color4(1f, 0x30 / 255f, 0x28 / 255f, 0f) },
        ]))
        using (ID2D1RadialGradientBrush halo = target.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = new Vector2(CenterX, CenterY),
                GradientOriginOffset = Vector2.Zero,
                RadiusX = 42f,
                RadiusY = 42f,
            },
            haloStops))
        {
            target.FillEllipse(new Ellipse(new Vector2(CenterX, CenterY), 42f, 42f), halo);
        }

        // 轴心黑盘先画，指针再从它上面穿过——否则指针根部会被这块圆盖住
        target.FillEllipse(
            new Ellipse(new Vector2(CenterX, CenterY), 19f, 19f),
            GetBrush(target, new Color4(8f / 255f, 9f / 255f, 9f / 255f, 1f)));

        Matrix3x2 parent = target.Transform;
        target.Transform = Matrix3x2.CreateRotation(angle * MathF.PI / 180f, new Vector2(CenterX, CenterY)) * parent;

        using (ID2D1GradientStopCollection bodyStops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 0x4A / 255f, 0x4A / 255f, 0.60f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1f, 0x52 / 255f, 0x45 / 255f, 1f) },
            new GradientStop { Position = 0.80f, Color = new Color4(1f, 0x6A / 255f, 0x48 / 255f, 1f) },
            new GradientStop { Position = 0.88f, Color = new Color4(1f, 0x7F / 255f, 0x52 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0xCF / 255f, 0x3A / 255f, 0x33 / 255f, 0.90f) },
        ]))
        using (ID2D1LinearGradientBrush bodyBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(CenterX, NeedleTipY),
                EndPoint = new Vector2(CenterX, NeedleTailY),
            },
            bodyStops))
        {
            ID2D1PathGeometry body = BuildNeedleBody(target);

            // 指针本身够粗，宽描边不会糊；适度加一层贴边柔光即可（22/14 那种会成光带）。
            float bodyOpacity = bodyBrush.Opacity;
            try
            {
                bodyBrush.Opacity = bodyOpacity * 0.14f;
                DrawStroke(target, body, bodyBrush, 13f);
                bodyBrush.Opacity = bodyOpacity * 0.22f;
                DrawStroke(target, body, bodyBrush, 8f);
            }
            finally
            {
                bodyBrush.Opacity = bodyOpacity;
            }

            target.FillGeometry(body, bodyBrush);

            using ID2D1LinearGradientBrush coreBrush = target.CreateLinearGradientBrush(
                new LinearGradientBrushProperties
                {
                    StartPoint = new Vector2(CenterX, NeedleTipY),
                    EndPoint = new Vector2(CenterX, NeedleTailY),
                },
                bodyStops);
            coreBrush.Opacity = 0.5f;
            target.FillGeometry(BuildNeedleCore(target), coreBrush);
        }

        target.Transform = parent;
    }

    private ID2D1PathGeometry BuildNeedleBody(ID2D1RenderTarget target)
    {
        if (_needleBody is not null)
        {
            return _needleBody;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(231.52f, 271f), FigureBegin.Filled);
            sink.AddLine(new Vector2(233.76f, 76f));
            sink.AddLine(new Vector2(236f, 59f));
            sink.AddLine(new Vector2(238.24f, 76f));
            sink.AddLine(new Vector2(240.48f, 271f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _needleBody = geometry;
        return geometry;
    }

    private ID2D1PathGeometry BuildNeedleCore(ID2D1RenderTarget target)
    {
        if (_needleCore is not null)
        {
            return _needleCore;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(234.88f, 268f), FigureBegin.Filled);
            sink.AddLine(new Vector2(236f, 66f));
            sink.AddLine(new Vector2(237.12f, 268f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _needleCore = geometry;
        return geometry;
    }

    private void DrawBoostGauge(
        ID2D1RenderTarget target,
        float boostBar,
        bool hasBoost,
        ID2D1SolidColorBrush tickBrush,
        ID2D1SolidColorBrush redBrush,
        ID2D1SolidColorBrush glyphBrush,
        ID2D1SolidColorBrush captionBrush)
    {
        Matrix3x2 outer = target.Transform;
        target.Transform = Matrix3x2.CreateScale(BoostScale)
            * Matrix3x2.CreateTranslation(BoostTranslateX, BoostTranslateY)
            * outer;

        if (_boostBandBlue is not null)
        {
            target.FillGeometry(_boostBandBlue, GetBrush(target, BandBlueColor));
        }

        if (_boostBandRed is not null)
        {
            target.FillGeometry(_boostBandRed, GetBrush(target, BandRedColor));
        }

        DrawBoostTicks(target, tickBrush, redBrush);

        // 唯一的 0 字形，复用刻度字形并按原型缩放
        ID2D1PathGeometry? zero = _glyphs.DialGlyph(target, '0');
        if (zero is not null)
        {
            Vector2 p = PointOnCircle(BoostCenterX, BoostCenterY, BoostLabelRadius, 0f);
            Matrix3x2 inner = target.Transform;
            target.Transform = Matrix3x2.CreateScale(BoostZeroScale)
                * Matrix3x2.CreateTranslation(p.X - (GlyphBoxWidth * 0.5f * BoostZeroScale), p.Y - (GlyphBaseline * BoostZeroScale))
                * inner;
            DrawStroke(target, zero, glyphBrush, 4.4f);
            target.Transform = inner;
        }

        DrawTextBlock(target, "−", _boostSignFormat, captionBrush, 98.3f, 298f, 40f, 34f);
        DrawTextBlock(target, "+", _boostSignFormat, captionBrush, 157.7f, 298f, 40f, 34f);

        // 轴心黑盘先画，指针再从它上面穿过
        target.FillEllipse(
            new Ellipse(new Vector2(BoostCenterX, BoostCenterY), 9f, 9f),
            GetBrush(target, new Color4(8f / 255f, 9f / 255f, 9f / 255f, 1f)));

        DrawBoostNeedle(target, boostBar, hasBoost);

        DrawTextBlock(target, "BOOST", _boostCaptionFormat, captionBrush, BoostCenterX, 330f, 120f, 20f);

        target.Transform = outer;
    }

    /// <summary>
    /// 增压表指针：0 点为正上方的“0”字形，左半区 0→-2.0、右半区 0→+2.0，各占 120°。
    /// 不夹取量程——超过 ±2.0 允许指针继续外摆（高级涡轮车才可能爆表）。
    /// </summary>
    private void DrawBoostNeedle(ID2D1RenderTarget target, float boostBar, bool hasBoost)
    {
        // 无增压车辆：指针稳定停在正上方 0，不跟随漂移的原始值
        float value = hasBoost ? boostBar / BoostFullScaleBar : 0f;
        float angle = value * BoostAngle;

        using ID2D1GradientStopCollection haloStops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 0x53 / 255f, 0x34 / 255f, 0.54f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1f, 0x39 / 255f, 0x2B / 255f, 0.27f) },
            new GradientStop { Position = 1f, Color = new Color4(1f, 0x30 / 255f, 0x28 / 255f, 0f) },
        ]);
        using (ID2D1RadialGradientBrush halo = target.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = new Vector2(BoostCenterX, BoostCenterY),
                GradientOriginOffset = Vector2.Zero,
                RadiusX = 20f,
                RadiusY = 20f,
            },
            haloStops))
        {
            target.FillEllipse(new Ellipse(new Vector2(BoostCenterX, BoostCenterY), 20f, 20f), halo);
        }

        ID2D1PathGeometry? body = BuildBoostNeedleBody(target);
        ID2D1PathGeometry? core = BuildBoostNeedleCore(target);
        if (body is null || core is null)
        {
            return;
        }

        Matrix3x2 parent = target.Transform;
        target.Transform = Matrix3x2.CreateRotation(angle * MathF.PI / 180f, new Vector2(BoostCenterX, BoostCenterY)) * parent;

        using (ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 0x4A / 255f, 0x4A / 255f, 0.60f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1f, 0x52 / 255f, 0x45 / 255f, 1f) },
            new GradientStop { Position = 0.80f, Color = new Color4(1f, 0x6A / 255f, 0x48 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0xCF / 255f, 0x3A / 255f, 0x33 / 255f, 0.90f) },
        ]))
        using (ID2D1LinearGradientBrush brush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(BoostCenterX, 239.6f),
                EndPoint = new Vector2(BoostCenterX, 309f),
            },
            stops))
        {
            // 原型 boostGlow：模糊 1.9 的红光，用两层宽描边近似
            float baseOpacity = brush.Opacity;
            try
            {
                brush.Opacity = baseOpacity * 0.12f;
                DrawStroke(target, body, brush, 6f);
                brush.Opacity = baseOpacity * 0.20f;
                DrawStroke(target, body, brush, 4f);
            }
            finally
            {
                brush.Opacity = baseOpacity;
            }

            target.FillGeometry(body, brush);
            brush.Opacity = 0.55f;
            target.FillGeometry(core, brush);
        }

        target.Transform = parent;
    }

    private ID2D1PathGeometry BuildBoostNeedleBody(ID2D1RenderTarget target)
    {
        if (_boostNeedleBody is not null)
        {
            return _boostNeedleBody;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(125.45f, 309f), FigureBegin.Filled);
            sink.AddLine(new Vector2(127.2f, 245f));
            sink.AddLine(new Vector2(128f, 239.6f));
            sink.AddLine(new Vector2(128.8f, 245f));
            sink.AddLine(new Vector2(130.55f, 309f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _boostNeedleBody = geometry;
        return geometry;
    }

    private ID2D1PathGeometry BuildBoostNeedleCore(ID2D1RenderTarget target)
    {
        if (_boostNeedleCore is not null)
        {
            return _boostNeedleCore;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(127.4f, 307.8f), FigureBegin.Filled);
            sink.AddLine(new Vector2(128f, 241.8f));
            sink.AddLine(new Vector2(128.6f, 307.8f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _boostNeedleCore = geometry;
        return geometry;
    }

    /// <summary>
    /// 主表刻度按转速值定位：大刻度每 1000 转、小刻度每 500 转，与标签一一对应。
    /// 不能用"等分格数"的思路——量程不是 1000 的整数倍时（如 11000），等分会让刻度与
    /// 千转整数错位，标签也就对不上。这里直接按 RPM 步进，量程多少都自动对齐。
    /// </summary>
    private static void DrawMainTicks(
        ID2D1RenderTarget target,
        float maxRpm,
        float redlineRpm,
        ID2D1SolidColorBrush normalBrush,
        ID2D1SolidColorBrush redBrush)
    {
        const float MinorStepRpm = 500f;

        for (float value = 0f; value <= maxRpm + 0.5f; value += MinorStepRpm)
        {
            float angle = AngleStart + (value / maxRpm * AngleSweep);
            bool major = Math.Abs(value % 1000f) < 0.5f;
            bool red = value >= redlineRpm;

            Vector2 a = PointOnCircle(CenterX, CenterY, TickOuter, angle);
            Vector2 b = PointOnCircle(CenterX, CenterY, major ? TickMajor : TickMinor, angle);

            ID2D1SolidColorBrush brush = red ? redBrush : normalBrush;
            float width = major ? 3.4f : 2.0f;
            DrawLineGlow(target, a, b, brush, width, red ? 1.6f : 1.4f, red ? 0.55f : 0.45f);
        }
    }

    /// <summary>
    /// <summary>
    /// 刻度线的辉光近似：紧贴实体线叠一层很淡的窄边，再画实体线。
    /// 注意不能用大幅加宽——刻度只有 2~3.4px 宽，宽描边会变成"变粗发虚"，
    /// 加上圆头笔帽还会在每根刻度两端鼓出圆球，整圈看起来糊成一团。
    /// 这里只用两层很窄的外扩（总共约 +1.2px），视觉上是"边缘微微发亮"。
    /// </summary>
    private static void DrawLineGlow(
        ID2D1RenderTarget target,
        Vector2 a,
        Vector2 b,
        ID2D1SolidColorBrush brush,
        float width,
        float glowWidth,
        float glowAlpha)
    {
        float originalOpacity = brush.Opacity;
        try
        {
            brush.Opacity = originalOpacity * glowAlpha * 0.45f;
            target.DrawLine(a, b, brush, width + glowWidth);

            brush.Opacity = originalOpacity * glowAlpha * 0.75f;
            target.DrawLine(a, b, brush, width + (glowWidth * 0.45f));

            brush.Opacity = originalOpacity;
            target.DrawLine(a, b, brush, width);
        }
        finally
        {
            brush.Opacity = originalOpacity;
        }
    }

    private static void DrawBoostTicks(ID2D1RenderTarget target, ID2D1SolidColorBrush tickBrush, ID2D1SolidColorBrush redBrush)
    {
        const int steps = 16;
        const int majorEvery = 4;

        for (int i = 0; i <= steps; i++)
        {
            float angle = -BoostAngle + (i / (float)steps * BoostAngle * 2f);
            bool major = i % majorEvery == 0;
            Vector2 a = PointOnCircle(BoostCenterX, BoostCenterY, BoostRadius, angle);
            Vector2 b = PointOnCircle(BoostCenterX, BoostCenterY, major ? BoostTickMajor : BoostTickMinor, angle);
            // 两端两格按原型标红
            bool red = i <= 1 || i >= steps - 1;

            ID2D1SolidColorBrush brush = red ? redBrush : tickBrush;
            float width = major ? 3.9f : 3.0f;
            DrawLineGlow(target, a, b, brush, width, 1.3f, red ? 0.55f : 0.45f);
        }
    }

    private void DrawDialLabels(
        ID2D1RenderTarget target,
        float maxRpm,
        float redlineRpm,
        ID2D1SolidColorBrush glyphBrush,
        ID2D1SolidColorBrush redBrush)
    {
        // 标签是千转整数（0、1、2…10、11），配合盘面中央的 x1000 读作"×1000 转"。
        // 按值定位，与大刻度（每 1000 转）一一对齐；量程超一万时自动多一个千转标签。
        const float LabelStepRpm = 1000f;

        for (float value = 0f; value <= maxRpm + 0.5f; value += LabelStepRpm)
        {
            float angle = AngleStart + (value / maxRpm * AngleSweep);
            bool red = value >= redlineRpm;

            string label = ((int)Math.Round(value / 1000f)).ToString(CultureInfo.InvariantCulture);

            Vector2 p = PointOnCircle(CenterX, CenterY, LabelRadius, angle);
            float textWidth = label.Length * GlyphBoxWidth;
            float originX = p.X - (textWidth * GlyphScaleX * 0.49f);
            float originY = p.Y - GlyphBaseline;

            ID2D1SolidColorBrush brush = red ? redBrush : glyphBrush;
            Matrix3x2 inner = target.Transform;
            target.Transform = Matrix3x2.CreateScale(GlyphScaleX, GlyphScaleY) * Matrix3x2.CreateTranslation(originX, originY) * inner;

            for (int c = 0; c < label.Length; c++)
            {
                ID2D1PathGeometry? glyph = _glyphs.DialGlyph(target, label[c]);
                if (glyph is null)
                {
                    continue;
                }

                Matrix3x2 glyphInner = target.Transform;
                target.Transform = Matrix3x2.CreateTranslation(c * GlyphAdvance, 0f) * glyphInner;
                DrawStrokeGlow(target, glyph, brush, 4.4f, 1.6f, 0.42f, layers: 2);
                target.Transform = glyphInner;
            }

            target.Transform = inner;
        }
    }

    /// <summary>
    /// 背光液晶窗：原型是左右对称的横向渐变（两端亮、中间偏暗），
    /// 再叠一层左上亮、右下暗的斜向高光（lcdShade）。
    /// 另按 blueGlow 用一圈外扩描边模拟窗体的发光边缘。
    /// </summary>
    private static void DrawLcdWindow(ID2D1RenderTarget target)
    {
        RoundedRectangle rect = new(new System.Drawing.RectangleF(LcdX, LcdY, LcdWidth, LcdHeight), LcdRadius, LcdRadius);

        // 原型是左右对称的横向渐变：两端亮、中间偏暗的背光液晶
        using (ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0xB8 / 255f, 0xF0 / 255f, 1f, 1f) },
            new GradientStop { Position = 0.08f, Color = new Color4(0x85 / 255f, 0xC9 / 255f, 0xE8 / 255f, 1f) },
            new GradientStop { Position = 0.30f, Color = new Color4(0x69 / 255f, 0xA9 / 255f, 0xCE / 255f, 1f) },
            new GradientStop { Position = 0.70f, Color = new Color4(0x6C / 255f, 0xAD / 255f, 0xD0 / 255f, 1f) },
            new GradientStop { Position = 0.93f, Color = new Color4(0x8B / 255f, 0xD0 / 255f, 0xEC / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0xB8 / 255f, 0xF0 / 255f, 1f, 1f) },
        ]))
        using (ID2D1LinearGradientBrush lcdBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(LcdX, LcdY),
                EndPoint = new Vector2(LcdX + LcdWidth, LcdY),
            },
            stops))
        {
            target.FillRoundedRectangle(rect, lcdBrush);
        }

        // 斜向高光（原型 lcdShade）：左上白、右下暗蓝，沿对角线过渡。
        // 夜晚观感偏平，因此把高光与暗侧都加重了一档。
        using (ID2D1GradientStopCollection shadeStops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 1f, 1f, 0.42f) },
            new GradientStop { Position = 0.18f, Color = new Color4(1f, 1f, 1f, 0.20f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1f, 1f, 1f, 0.06f) },
            new GradientStop { Position = 0.62f, Color = new Color4(1f, 1f, 1f, 0.10f) },
            new GradientStop { Position = 1f, Color = new Color4(0x11 / 255f, 0x2E / 255f, 0x48 / 255f, 0.30f) },
        ]))
        using (ID2D1LinearGradientBrush shadeBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(LcdX, LcdY),
                EndPoint = new Vector2(LcdX + LcdWidth, LcdY + LcdHeight),
            },
            shadeStops))
        {
            target.FillRoundedRectangle(rect, shadeBrush);
        }

        // 顶部一道细高光，模拟玻璃反光（夜晚尤其明显）
        using (ID2D1GradientStopCollection rimStops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(1f, 1f, 1f, 0.55f) },
            new GradientStop { Position = 1f, Color = new Color4(1f, 1f, 1f, 0f) },
        ]))
        using (ID2D1LinearGradientBrush rimBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(LcdX, LcdY + 2f),
                EndPoint = new Vector2(LcdX, LcdY + 14f),
            },
            rimStops))
        {
            target.FillRoundedRectangle(
                new RoundedRectangle(new System.Drawing.RectangleF(LcdX + 3f, LcdY + 2f, LcdWidth - 6f, 12f), 3f, 3f),
                rimBrush);
        }
    }

    private void DrawSpeedDigits(ID2D1RenderTarget target, int speed, bool hasData)
    {
        // 固定三位槽位，不足补 0；无数据时全灭（保留底影）
        string text = hasData
            ? Math.Clamp(speed, 0, 999).ToString("D3", CultureInfo.InvariantCulture)
            : "000";

        for (int i = 0; i < 3; i++)
        {
            float x = SpeedSlotX + (i * SpeedSlotStep);
            DrawSevenSegment(target, text[i], x, SpeedSlotY, SpeedSlotScale, hasData, false);
        }
    }

    /// <summary>
    /// 档位固定两个槽位、右对齐：个位恒在右槽，十位（只有 10 档的 "1"）进左槽。
    /// 若按实际字符数居中，1 位与 2 位之间切换时整体会横向跳动。
    /// 左槽在非 10 档时按空格处理（只画幽灵段），保持两槽布局稳定。
    /// </summary>
    private void DrawGearDigits(ID2D1RenderTarget target, string gear, bool hasData)
    {
        string text = gear.Length > 2 ? gear[^2..] : gear;
        if (text.Length == 0)
        {
            return;
        }

        // 补齐到两位：' 7' → 左槽空、右槽 7；'10' → 左槽 1、右槽 0
        char tens = text.Length >= 2 ? text[^2] : ' ';
        char ones = text[^1];

        // 两个槽以盘面中心对称布局，整体宽度 = 槽宽 × 2 + 间距
        float slotWidth = GearSlotWidth;
        float leftSlotX = CenterX - slotWidth - (GearGap * 0.5f);
        float rightSlotX = CenterX + (GearGap * 0.5f);

        DrawGearAtSlot(target, tens, leftSlotX, hasData);
        DrawGearAtSlot(target, ones, rightSlotX, hasData);
    }

    /// <summary>
    /// 在给定槽位右缘对齐绘制一位档位数字。空槽（非 10 档的左槽）不跳过——
    /// 照常画出全灭的"8"幽灵轮廓，让盘面中央始终有稳定的双槽骨架。
    /// </summary>
    private void DrawGearAtSlot(ID2D1RenderTarget target, char ch, float slotX, bool hasData)
    {
        (float inkLeft, float inkRight) = Nfs19GlyphCache.InkOf(ch == ' ' ? '8' : ch);
        float inkWidth = (inkRight - inkLeft) * GearSlotScale;

        // 右对齐到槽位右缘
        float x = slotX + GearSlotWidth - inkWidth - (inkLeft * GearSlotScale);
        DrawSevenSegment(target, ch, x, GearSlotY, GearSlotScale, hasData && ch != ' ', true);
    }

    /// <summary>
    /// 时速数字的加粗量（段面坐标）：每段两侧各外扩 0.15，段厚 3 → 3.3。
    /// 七段之间的最近缝隙只有 1.387 个段面单位，描边向两侧各外扩一半，
    /// 因此外扩量越大缝隙越小：0.6 时缝隙仅剩 0.79（几乎粘连），
    /// 0.3 时回到 1.09，视觉上七段能明确分开。上限约 0.8，超过就会填死缝隙。
    /// 只管时速：档位数字保持原粗细。
    /// </summary>
    private const float SpeedSegmentInflate = 0.3f;

    private void DrawSevenSegment(
        ID2D1RenderTarget target,
        char ch,
        float x,
        float y,
        float scale,
        bool active,
        bool gearStyle)
    {
        string lit = active ? Nfs19GlyphCache.SegmentsOf(ch) : string.Empty;
        ID2D1SolidColorBrush onBrush = GetBrush(target, gearStyle ? GearOnColor : SpeedOnColor);
        ID2D1SolidColorBrush offBrush = GetBrush(target, gearStyle ? GearOffColor : SpeedOffColor);

        Matrix3x2 inner = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(x, y) * inner;

        foreach (char segment in "abcdefg")
        {
            ID2D1PathGeometry? geometry = _glyphs.Segment(target, segment);
            if (geometry is null)
            {
                continue;
            }

            bool isOn = lit.Contains(segment, StringComparison.Ordinal);
            if (isOn)
            {
                // 档位段只有约 3px 宽，七段之间的缝隙更小；任何宽描边都会把缝隙填满、
                // 让数字糊成一团。这里不加辉光，保持纯填充的清晰轮廓。
                target.FillGeometry(geometry, onBrush);

                if (!gearStyle)
                {
                    // 时速数字按 SpeedSegmentInflate 膨胀一圈，粗细上贴近液晶窗的观感。
                    // 亮段是不透明色，描边与填充叠加不会产生半透明重影，故不必做几何并集。
                    DrawStroke(target, geometry, onBrush, SpeedSegmentInflate);
                }
            }
            else
            {
                // 熄灭段保留暗色"幽灵"路径，让数字整体轮廓始终可辨
                target.FillGeometry(geometry, offBrush);
            }
        }

        target.Transform = inner;
    }

    private void DrawStroke(ID2D1RenderTarget target, ID2D1PathGeometry geometry, ID2D1Brush brush, float width)
    {
        _roundStroke ??= target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
                LineJoin = LineJoin.Round,
            });

        target.DrawGeometry(geometry, brush, width, _roundStroke);
    }

    /// <summary>
    /// 辉光近似：原型用 feGaussianBlur 做柔光，而当前只有 ID2D1RenderTarget（无 DeviceContext），
    /// 无法用 GaussianBlur effect。这里改成"由宽到窄叠若干遍低透明度描边"模拟外扩的柔边，
    /// 再画一遍实体笔画。层次越多越接近真实模糊，但每层都是一次几何描边，注意别叠太多。
    /// </summary>
    private void DrawStrokeGlow(
        ID2D1RenderTarget target,
        ID2D1PathGeometry geometry,
        ID2D1Brush brush,
        float width,
        float glowWidth,
        float glowAlpha,
        int layers = 3)
    {
        float originalOpacity = brush.Opacity;
        try
        {
            for (int i = layers; i >= 1; i--)
            {
                // 由外向内：最外层最宽最淡，逐层收窄并加深
                float t = i / (float)layers;
                brush.Opacity = originalOpacity * glowAlpha * (1f - (t * 0.45f)) / layers * 1.6f;
                DrawStroke(target, geometry, brush, width + (glowWidth * t));
            }

            brush.Opacity = originalOpacity;
            DrawStroke(target, geometry, brush, width);
        }
        finally
        {
            brush.Opacity = originalOpacity;
        }
    }

    private static void DrawTextBlock(ID2D1RenderTarget target, string text, IDWriteTextFormat? format, ID2D1Brush brush, float centerX, float top, float width, float height)
    {
        if (format is null)
        {
            return;
        }

        target.DrawText(text, format, new Rect(centerX - (width * 0.5f), top, width, height), brush);
    }

    private static float ResolveRedline(ShiftPointLearner learner, in ForzaDataPacket packet, float maxRpm)
    {
        // B + A 兜底：学过用学习到的换挡比例，没学过按 0.75
        double ratio = learner.GetRedlineRatio(packet);
        return (float)Math.Clamp(ratio, 0.5, 1.0) * maxRpm;
    }

    private void EnsureGeometry(ID2D1RenderTarget target, float maxRpm, float redlineRpm)
    {
        if (_geometryBuilt
            && Math.Abs(_cachedMaxRpm - maxRpm) < 0.5f
            && Math.Abs(_cachedRedlineRpm - redlineRpm) < 0.5f)
        {
            return;
        }

        _mainBandBlue?.Dispose();
        _mainBandRed?.Dispose();
        _boostBandBlue?.Dispose();
        _boostBandRed?.Dispose();
        _needleBody?.Dispose();
        _needleCore?.Dispose();
        _boostNeedleBody?.Dispose();
        _boostNeedleCore?.Dispose();
        _needleBody = null;
        _needleCore = null;
        _boostNeedleBody = null;
        _boostNeedleCore = null;

        float split = AngleStart + (redlineRpm / maxRpm * AngleSweep);
        float blueStart = AngleStart - BandPad;
        float redEnd = AngleStart + AngleSweep + BandPad;

        _mainBandBlue = BuildSector(target, blueStart, split, BandOuter, BandInner, CenterX, CenterY);
        _mainBandRed = BuildSector(target, split, redEnd, BandOuter, BandInner, CenterX, CenterY);

        float boostLeft = -BoostAngle - BoostBandPad;
        float boostRight = BoostAngle + BoostBandPad;
        _boostBandBlue = BuildSector(target, boostLeft + BoostBandRedWidth, boostRight - BoostBandRedWidth, BoostBandOuter, BoostBandInner, BoostCenterX, BoostCenterY);
        _boostBandRed = BuildSector(target, boostLeft, boostLeft + BoostBandRedWidth, BoostBandOuter, BoostBandInner, BoostCenterX, BoostCenterY);

        _cachedMaxRpm = maxRpm;
        _cachedRedlineRpm = redlineRpm;
        _geometryBuilt = true;
    }

    /// <summary>环形扇区：蓝段截止红线、红段从红线到末端，内沿停在数字中线。</summary>
    private static ID2D1PathGeometry BuildSector(ID2D1RenderTarget target, float startAngle, float endAngle, float outerRadius, float innerRadius, float cx, float cy)
    {
        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();

        float sweep = endAngle - startAngle;
        if (Math.Abs(sweep) < 0.01f)
        {
            sink.BeginFigure(Vector2.Zero, FigureBegin.Filled);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
            return geometry;
        }

        Vector2 o1 = PointOnCircle(cx, cy, outerRadius, startAngle);
        Vector2 o2 = PointOnCircle(cx, cy, outerRadius, endAngle);
        Vector2 i2 = PointOnCircle(cx, cy, innerRadius, endAngle);
        Vector2 i1 = PointOnCircle(cx, cy, innerRadius, startAngle);

        bool large = Math.Abs(sweep) > 180f;
        ArcSize arcSize = large ? ArcSize.Large : ArcSize.Small;

        sink.BeginFigure(o1, FigureBegin.Filled);
        sink.AddArc(new ArcSegment(o2, new Size(outerRadius, outerRadius), 0f, SweepDirection.Clockwise, arcSize));
        sink.AddLine(i2);
        sink.AddArc(new ArcSegment(i1, new Size(innerRadius, innerRadius), 0f, SweepDirection.CounterClockwise, arcSize));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();

        return geometry;
    }

    /// <summary>与原型 point() 一致：0° 指向正上方，顺时针增大。</summary>
    private static Vector2 PointOnCircle(float cx, float cy, float radius, float angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        return new Vector2(
            cx + (float)(radius * Math.Sin(radians)),
            cy - (float)(radius * Math.Cos(radians)));
    }

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushCache = [];

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

        _mainBandBlue?.Dispose();
        _mainBandRed?.Dispose();
        _boostBandBlue?.Dispose();
        _boostBandRed?.Dispose();
        _needleBody?.Dispose();
        _needleCore?.Dispose();
        _boostNeedleBody?.Dispose();
        _boostNeedleCore?.Dispose();
        _roundStroke?.Dispose();

        _captionFormat?.Dispose();
        _captionSmallFormat?.Dispose();
        _unitFormat?.Dispose();
        _boostCaptionFormat?.Dispose();
        _boostSignFormat?.Dispose();
    }
}
