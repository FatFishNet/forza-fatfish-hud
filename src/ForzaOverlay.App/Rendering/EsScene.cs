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
/// ES 数字转速盘（单表转速 + 中央车速/档位）渲染器。
/// 几何来自原型 <c>temp/es.html</c>，原型画布 1000×1000，本类内部坐标统一取原型的 0.5 倍，
/// 对外基准直接就是 500×500，不需要 ContentShift 之类的平移吸收。
/// <para>
/// 角度制跟咬地鲨同系（SVG 制）：<c>0° = 正右</c>、y 轴向下、<see cref="PointOn"/> 用 <c>cos/sin</c>。
/// 不要和仿NFS19 / SHIFT TREE 的「0° 正上、sin/-cos」混用 —— 混了指针整体偏，编译却零错误。
/// </para>
/// <para>
/// 图层顺序照原型：表盘面 → 刻度 → 点阵 → 红区带 → 转速数字 → 紫带 → 中央信息 → 指针。
/// 指针必须最后画，压在全部表盘元素之上。
/// </para>
/// 由 <see cref="HudScene"/> 委派调用，本类只负责画，不参与遥测采集与布局调度。
/// </summary>
internal sealed class EsScene : IDisposable
{
    private const float BaseWidth = 500f;
    private const float BaseHeight = 500f;

    /// <summary>
    /// 原型坐标 → 本场景坐标的缩放系数。
    /// <para>
    /// **不是 0.5**。原型 <c>es.html</c> 里 <c>.wrap {{ width: min(94vmin, 1000px) }}</c>，
    /// SVG 只占画布的 94%，所以 viewBox 里写 r438 的元素实际画在 r411.7 上。
    /// 实测验证：原型 1000px 图上金属环描边跨 r393~430，
    /// 与 (438±21)×0.94 = 411.7±19.7 吻合。
    /// </para>
    /// <para>
    /// 所以系数 = 0.94（wrap）× 0.5（1000 → 500 基准）= <b>0.47</b>。
    /// </para>
    /// </summary>
    private const float Scale = 0.47f;

    /// <summary>
    /// 原型**半径/长度** → 本场景长度。半径不涉及圆心偏移，直接乘 <see cref="Scale"/> 即可。
    /// </summary>
    private static float R(float protoLength) => protoLength * Scale;

    /// <summary>
    /// 原型**坐标** → 本场景坐标。**必须以圆心 (500,500) 为基准**，
    /// 不能写成 <c>proto * Scale</c> —— 那等价于把圆心也缩到了 235。
    /// 2026-11-21 踩过：指针端点因此短了 15px、<c>x1000r/min</c> 位置偏了 265px。
    /// </summary>
    private static float ToCanvas(float protoCoord) => (250f) + ((protoCoord - 500f) * Scale);

    /// <summary>
    /// 圆心固定在画布正中。
    /// <c>Scale</c> 只作用于长度与相对偏移，不作用于画布绝对坐标。
    /// </summary>
    private const float Cx = BaseWidth * 0.5f;
    private const float Cy = BaseHeight * 0.5f;

    // ── 外环与盘面 ──
    private const float BezelR = 438f * Scale;
    private const float BezelWidth = 42f * Scale;
    private const float HaloR = 452f * Scale;

    /// <summary>
    /// 盘面半径要一直铺到**外环内沿**，否则两者之间会露出一圈透明带。
    /// 原型 r409 比外环内沿（438-21=417）小 8，SVG 里那圈是 <c>#02090c</c> 的描边补上的；
    /// 本项目没画那圈描边，所以直接让盘面填满到内沿（2026-11-21 实机截图发现透明带）。
    /// </summary>
    private const float FaceR = 438f * Scale - (BezelWidth * 0.5f);

    // ── 刻度（长 412→460，短 424→450）──
    private const float TickMajorIn = 412f * Scale;
    private const float TickMajorOut = 460f * Scale;
    private const float TickMinorIn = 424f * Scale;
    private const float TickMinorOut = 450f * Scale;
    private const float TickMajorWidth = 9f * Scale;
    private const float TickMinorWidth = 4f * Scale;

    /// <summary>
    /// 原型固定量程（千转）：0~8，占 288°。
    /// <para>
    /// **转速区域永远只有这 288°，不随实车量程变大**。
    /// 量程超过 8 千转时把刻度与数字压密（9 千转就每 32° 一个大刻度），
    /// 而不是把扫掠角撑到 324° —— 那样会挤掉紫带、圈也画不下（用户 2026-11-21 实机截图指出）。
    /// </para>
    /// </summary>
    private const int ProtoTopThousand = 8;

    /// <summary>转速区域固定 288°（= 原型 8 千转 × 36°），不随量程变化。</summary>
    private const float SweepDeg = ProtoTopThousand * 36f;

    /// <summary>
    /// **大刻度**（每千转一根）的角度步进，由量程推导：
    /// 8 千转 → 36°（原型值），9 千转 → 32°，10 千转 → 28.8°。
    /// </summary>
    private static float ThousandDegFor(float spanRpm)
    {
        int thousands = ThousandCountFor(spanRpm);
        return SweepDeg / thousands;
    }

    /// <summary>
    /// **小刻度**（每 500 转一根）的角度步进 —— 恒为大刻度的一半。
    /// 之前写成固定「每 18° 一根、一圈 20 根」，那只在原型 8 千转时成立；
    /// 量程一变，刻度间距就和数字对不上了（2026-11-21 实机截图：数字每千转一个、刻度还固定 18°）。
    /// </summary>
    private static float MinorTickDegFor(float spanRpm) => ThousandDegFor(spanRpm) * 0.5f;

    /// <summary>量程的千转数（0~N 共 N 段，数字 0..N 共 N+1 个）。</summary>
    private static int ThousandCountFor(float spanRpm) => Math.Max(1, (int)Math.Round(spanRpm / 1000f));

    // ── 点阵（6 层，r342~406）──
    private const float DotFirstR = 342f * Scale;
    private const float DotLayerStep = 12.8f * Scale;
    private const int DotLayers = 6;

    /// <summary>
    /// 点半径 1.7（viewBox 单位，非 CSS 写的 2.1）。
    /// 实测原型 1000px 图里点的中位直径 3.19px，反推 viewBox 半径 = 1.70；
    /// CSS 的 r=2.1 含描边与抗锯齿会偏大，按 2.1 画出来点会糊成一团。
    /// </summary>
    private const float DotRadius = 1.7f * Scale;
    private const float DotStepDeg = 2.6f;
    private const float DotAngleStart = 92f;
    private const float DotAngleEnd = 380f;

    // ── 转速数字（环半径 354，字号 78）──
    private const float NumR = 354f * Scale;
    private const float NumFontSize = 78f * Scale;
    private const float UnitFontSize = 30f * Scale;

    /// <summary>单位文字基线 y=242（viewBox），必须走 <see cref="ToCanvas"/> 换算。</summary>
    private static readonly float UnitY = ToCanvas(242f);

    // ── 中央信息（车速 y357 字号158；km/h y462 字号30；档位 y610 字号96）──
    private static readonly float SpeedY = ToCanvas(357f);
    private const float SpeedFontSize = 158f * Scale;

    /// <summary>
    /// 车速逐位定槽的槽宽。
    /// 项目约定：数字必须定槽，否则比例字体下 1 和 8 宽度不同，整块区域会随数值抖动。
    /// 实测原型 1000px 图上单字墨迹宽 56（viewBox = 26.3px @500）；
    /// 槽宽取 64（= 30.1px）留出字间呼吸，等于字宽会粘在一起（2026-11-21 两次调参确认）。
    /// </summary>
    private const float SpeedSlotWidth = 64f * Scale;

    private static readonly float KmhY = ToCanvas(462f);
    private const float KmhFontSize = 30f * Scale;
    private static readonly float KmhLineLeftX1 = ToCanvas(370f);
    private static readonly float KmhLineLeftX2 = ToCanvas(445f);
    private static readonly float KmhLineRightX1 = ToCanvas(555f);
    private static readonly float KmhLineRightX2 = ToCanvas(630f);
    private static readonly float KmhLineY = ToCanvas(458f);
    private static readonly float GearY = ToCanvas(610f);

    /// <summary>档位字号：原型 96，用户 2026-11-21 要求再加大 30% → 124.8。</summary>
    private const float GearFontSize = 96f * 1.3f * Scale;

    // ── 紫带 Sport S（26°~82°，描边 42，文字叠加在紫带中线上）──
    private const float PurpleStartDeg = 26f;
    private const float PurpleEndDeg = 82f;

    /// <summary>
    /// 文字沿弧摆放的半径。
    /// 原型用 <c>textPath</c> 铺在 r448 的独立路径上，配合 SVG 的基线规则恰好落在紫带中线；
    /// 本项目的 <see cref="HudDraw.CenteredText"/> 按行高居中，照抄 448 会压在带外沿。
    /// 基址取紫带中心 <see cref="BezelR"/>，再向内收 2px（用户 2026-11-21 要求）。
    /// </summary>
    private const float PurpleTextR = BezelR - 2f;
    private const float PurpleTextFontSize = 30f * Scale;

    /// <summary>
    /// Sport S 文字的分布：实测原型中心在 54.6°、横跨约 12°（48.1°~60.2°）。
    /// 不是铺满紫带 26°~82° —— 那样文字会被拉得很散。
    /// </summary>
    private const float SportTextCenterDeg = 54.6f;
    private const float SportTextSpanDeg = 14f;

    // ── 指针（原型：发光三角 525,494→888,500→525,506；主体 520,488→888,500→520,512；轴心 r11）──
    // 指针沿 +x 摆放，X 是画布绝对坐标，必须走 ToCanvas；宽度是长度，乘 Scale 即可
    private static readonly float NeedleBaseX = ToCanvas(520f);
    private static readonly float NeedleTipX = ToCanvas(888f);

    /// <summary>主体三角半宽：原型 488→512 共 24px，半宽 12。</summary>
    private const float NeedleHalfWidth = 12f * Scale;

    /// <summary>发光内层三角半宽：原型 494→506 共 12px，半宽 6。</summary>
    private const float NeedleGlowHalfWidth = 6f * Scale;

    /// <summary>发光内层三角起点：原型 x=525（比主体基座 520 更靠外）。</summary>
    private static readonly float NeedleGlowBaseX = ToCanvas(525f);

    /// <summary>高光线：原型 x=545 → 880。</summary>
    private static readonly float NeedleHighlightX1 = ToCanvas(545f);
    private static readonly float NeedleHighlightX2 = ToCanvas(880f);

    private const float HubR = 11f * Scale;

    /// <summary>
    /// 转速映射：0 在 90°（正下），全程 <see cref="TachoSweepDeg"/>，量程内每千转 36°。
    /// 量程由 <see cref="ResolveTachoSpan"/> 按千转向上取整，所以 S 由 span 推导，不写死。
    /// </summary>
    private const float TachoAngleStart = 90f;

    // 配色（取自原型 CSS 变量与 defs）
    private static readonly Color4 Cyan = new(0x1F / 255f, 0xB9 / 255f, 0xCC / 255f, 1f);
    private static readonly Color4 Red = new(0xFF / 255f, 0x3B / 255f, 0x18 / 255f, 1f);
    private static readonly Color4 White = new(0xF2 / 255f, 0xFB / 255f, 0xFF / 255f, 1f);
    private static readonly Color4 UnitText = new(0x9F / 255f, 0xB9 / 255f, 0xBF / 255f, 1f);
    private static readonly Color4 KmhText = new(0xD7 / 255f, 0xE8 / 255f, 0xEB / 255f, 1f);
    private static readonly Color4 KmhLine = new(0x7F / 255f, 0x9E / 255f, 0xA4 / 255f, 1f);
    private static readonly Color4 TickMajorColor = new(0x02 / 255f, 0x18 / 255f, 0x1D / 255f, 1f);
    private static readonly Color4 TickMinorColor = new(0x0A / 255f, 0x2B / 255f, 0x31 / 255f, 1f);

    /// <summary>
    /// 红区内的刻度：红底很亮，深色细线会被抗锯齿吃掉，所以用**不透明纯深色 + 略加粗**。
    /// 原型在红区里重画刻度时也是同一套 drawTick，只是底色变红、深色刻度自然清晰。
    /// </summary>
    private static readonly Color4 RedTickColor = new(0x02 / 255f, 0x10 / 255f, 0x14 / 255f, 1f);

    private readonly IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _numFormat;
    private IDWriteTextFormat? _unitFormat;
    private IDWriteTextFormat? _sportFormat;
    private IDWriteTextFormat? _speedFormat;
    private IDWriteTextFormat? _kmhFormat;
    private IDWriteTextFormat? _gearFormat;

    private ID2D1PathGeometry? _dotsGeometry;
    private ID2D1PathGeometry? _redDotsGeometry;
    private float _dotsGeometrySpan = -1f;
    private float _dotsGeometryRatio = -1f;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];

    // 渐变画刷：与咬地鲨同一套「首次用时创建、字段缓存、Dispose 释放」的做法
    private ID2D1LinearGradientBrush? _bezelBrush;
    private ID2D1RadialGradientBrush? _faceBrush;
    private ID2D1LinearGradientBrush? _needleBrush;

    private string _stableGearText = "N";

    internal EsScene(IDWriteFactory? dwrite)
    {
        _dwrite = dwrite;

        _numFormat = CreateFormat(dwrite, NumFontSize, FontWeight.Bold);
        _unitFormat = CreateFormat(dwrite, UnitFontSize, FontWeight.Normal);
        _sportFormat = CreateFormat(dwrite, PurpleTextFontSize, FontWeight.Bold);
        _speedFormat = CreateFormat(dwrite, SpeedFontSize, FontWeight.Bold);
        _kmhFormat = CreateFormat(dwrite, KmhFontSize, FontWeight.Normal);
        _gearFormat = CreateFormat(dwrite, GearFontSize, FontWeight.Bold);
    }

    private static IDWriteTextFormat? CreateFormat(IDWriteFactory? dwrite, float size, FontWeight weight)
    {
        // 原型用 Arial Narrow；本机已装，但为了和其余面板一致仍走 Bahnschrift + SemiCondensed。
        if (dwrite is null)
        {
            return null;
        }

        IDWriteTextFormat format = dwrite.CreateTextFormat(
            "Bahnschrift", null!, weight, FontStyle.Normal, FontStretch.SemiCondensed, size, "zh-CN");
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
        bool useLearnedRedline,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.26f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        float spanRpm = ResolveTachoSpan(maxRpm);
        float redlineRpm = ResolveRedline(learner, packet, hasPacket, spanRpm, useLearnedRedline);
        float redlineRatio = spanRpm > 1f ? Math.Clamp(redlineRpm / spanRpm, 0f, 1f) : 0.75f;

        float rpmRatio = hasPacket ? Math.Clamp(packet.CurrentEngineRpm / spanRpm, 0f, 1f) : 0f;
        string gearText = hasPacket ? FormatGear(packet.GearNumber) : "N";
        string speedText = hasPacket
            ? HudDraw.Truncate(Math.Max(packet.SpeedKmh, 0f), 0)
            : "--";

        // 转速区域固定 288°，不随实车量程变；量程变大只把数字挤密。
        // （之前写成 spanRpm/1000 × 36°，9 千转时扫掠 324°，整圈被撑满、挤掉紫带 —— 实机截图发现）
        float sweepDeg = SweepDeg;

        Matrix3x2 caller = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        DrawFace(target);

        // 刻度先画：每千转一个大刻度（正对数字）、每 500 转一个小刻度，两者都由量程推导。
        DrawTicks(target, spanRpm);

        // 红区带盖在刻度之上，所以要把落在红区里的刻度**按同一套步进**再描一遍深色，
        // 否则红底上的刻度几乎看不见（原型也是这个做法：drawTick(a, redG)）。
        DrawRedArc(target, redlineRatio, sweepDeg, spanRpm);

        EnsureDotGeometry(target, spanRpm, redlineRatio);
        DrawDots(target);

        // 紫带在刻度之后画：它会盖住那一段刻度（原型行为，紫带是压在环上的装饰色）
        DrawPurpleBand(target);

        DrawNumbers(target, spanRpm);
        DrawCenterInfo(target, speedText, gearText);
        DrawNeedle(target, TachoAngleStart + (rpmRatio * sweepDeg));

        target.Transform = caller;
    }

    /// <summary>
    /// 转速表量程：跟随车辆最大转速并按千转向上取整（7500→8 千转），夹在 4~16 之间。
    /// 与咬地鲨各自维护一份：单表要按整数千转标数字，零头量程没法标刻度。
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
        float spanRpm,
        bool useLearnedRedline)
    {
        if (!hasPacket)
        {
            return spanRpm * 0.75f;
        }

        if (!useLearnedRedline)
        {
            // 关闭红区校正时回退到 75%，保证低转车红区也不出界
            return spanRpm * 0.75f;
        }

        double ratio = Math.Clamp(learner.GetRedlineRatio(packet), 0.5, 1.0);
        return Math.Clamp((float)ratio * spanRpm, 0f, spanRpm);
    }

    /// <summary>档位文案：-1→R、0→N、1..10 原样、11 是换挡瞬间，保持上一帧。</summary>
    private string FormatGear(int gear)
    {
        string text = gear switch
        {
            11 => _stableGearText,
            < 0 => "R",
            0 => "N",
            <= 10 => gear.ToString(CultureInfo.InvariantCulture),
            _ => _stableGearText,
        };

        _stableGearText = text;
        return text;
    }

    /// <summary>极坐标 → 直角坐标。0° 正右、顺时针（y 向下），与咬地鲨同系。</summary>
    private static Vector2 PointOn(float radius, float angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        return new Vector2(
            Cx + (float)(radius * Math.Cos(radians)),
            Cy + (float)(radius * Math.Sin(radians)));
    }

    private ID2D1SolidColorBrush GetBrush(ID2D1RenderTarget target, Color4 color)
    {
        uint key = ((uint)(color.R * 255f) << 24)
            | ((uint)(color.G * 255f) << 16)
            | ((uint)(color.B * 255f) << 8)
            | (uint)(color.A * 255f);

        if (!_brushes.TryGetValue(key, out ID2D1SolidColorBrush? brush))
        {
            brush = target.CreateSolidColorBrush(color);
            _brushes[key] = brush;
        }

        return brush;
    }

    private void DrawFace(ID2D1RenderTarget target)
    {
        // 外圈青色柔光：原型靠 feGaussianBlur(70px)，本项目做不到弥散，
        // 用三层递增透明度的描边仿「边缘发亮」（用户已知悉并接受）。
        // ⚠️ 外扩量必须收在画布内：原型 500 图的外径是 228（四周留白 22px），
        // 而柔光若按 CSS 的 r452 + 描边加粗来画会冲到 r248、几乎顶满 500 画布，
        // 整幅图比原型大 8.9%（2026-11-21 实测踩过）。
        // 这里用 HaloR 作内起点、每层只外扩 2.5px，末层约 r222，留出安全边距。
        for (int i = 3; i >= 1; i--)
        {
            float alpha = 0.06f * i;
            float r = HaloR + (i * 2.5f);
            target.DrawEllipse(
                new Ellipse(new Vector2(Cx, Cy), r, r),
                GetBrush(target, new Color4(0x0A / 255f, 0x8C / 255f, 0x98 / 255f, alpha)),
                BezelWidth * 0.6f);
        }

        // 盘面：径向渐变（中心偏左上一档，模拟原型 radialGradient cx=50% cy=45%）
        if (GetFaceBrush(target) is { } face)
        {
            target.FillEllipse(new Ellipse(new Vector2(Cx, Cy), FaceR, FaceR), face);
        }

        // 金属外环：斜向四段线性渐变（原型 bezel 从左上高光过渡到右下暗部）
        if (GetBezelBrush(target) is { } bezel)
        {
            target.DrawEllipse(new Ellipse(new Vector2(Cx, Cy), BezelR, BezelR), bezel, BezelWidth);
        }

        // 青绿色内缘高光：压在金属环内侧，做出金属反边的亮线
        target.DrawEllipse(
            new Ellipse(new Vector2(Cx, Cy), BezelR - (BezelWidth * 0.5f) - (1f * Scale), BezelR - (BezelWidth * 0.5f) - (1f * Scale)),
            GetBrush(target, new Color4(0xE8 / 255f, 0xFF / 255f, 0xFF / 255f, 0.55f)),
            1.5f * Scale);

        // 内缘青光（原型 filter="url(#glowCyan)"，同样用双层叠加代替）
        target.DrawEllipse(
            new Ellipse(new Vector2(Cx, Cy), FaceR, FaceR),
            GetBrush(target, new Color4(0x2B / 255f, 0xD5 / 255f, 0xE5 / 255f, 0.28f)),
            3f * Scale);
    }

    private ID2D1LinearGradientBrush? GetBezelBrush(ID2D1RenderTarget target)
    {
        if (_bezelBrush is not null)
        {
            return _bezelBrush;
        }

        if (target.Factory is null)
        {
            return null;
        }

        // 原型四段：亮白 → 浅青 → 中青 → 暗青 → 再回一点亮，做金属体积感
        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0xE8 / 255f, 0xFF / 255f, 0xFF / 255f, 1f) },
            new GradientStop { Position = 0.22f, Color = new Color4(0xA6 / 255f, 0xE4 / 255f, 0xEC / 255f, 1f) },
            new GradientStop { Position = 0.48f, Color = new Color4(0x2B / 255f, 0x78 / 255f, 0x85 / 255f, 1f) },
            new GradientStop { Position = 0.72f, Color = new Color4(0x0D / 255f, 0x39 / 255f, 0x42 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0x38 / 255f, 0xA9 / 255f, 0xAD / 255f, 1f) },
        ]);

        _bezelBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                // 原型 x1=0 y1=0 → x2=1 y2=1 是相对**整个包围盒**的，
                // 拿它直接给一个细圆环描边用，环带只覆盖渐变中段的一小截，
                // 画出来就是一条近乎纯色的环、完全没有金属明暗（2026-11-21 对比原型发现）。
                // 这里把渐变轴沿对角线**拉长到环带两端之外**（各外扩一个环宽），
                // 让环带从左上亮部一直吃到右下暗部，才有金属体积感。
                StartPoint = new Vector2(
                    Cx - BezelR - (BezelWidth * 0.5f),
                    Cy - BezelR - (BezelWidth * 0.5f)),
                EndPoint = new Vector2(
                    Cx + BezelR + (BezelWidth * 0.5f),
                    Cy + BezelR + (BezelWidth * 0.5f)),
            },
            stops);

        return _bezelBrush;
    }

    private ID2D1RadialGradientBrush? GetFaceBrush(ID2D1RenderTarget target)
    {
        if (_faceBrush is not null)
        {
            return _faceBrush;
        }

        if (target.Factory is null)
        {
            return null;
        }

        // 原型 radialGradient cx=50% cy=45% r=60%：中心亮、外缘沉
        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0x0C / 255f, 0x20 / 255f, 0x27 / 255f, 1f) },
            new GradientStop { Position = 0.65f, Color = new Color4(0x06 / 255f, 0x12 / 255f, 0x16 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0x02 / 255f, 0x08 / 255f, 0x09 / 255f, 1f) },
        ]);

        _faceBrush = target.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = new Vector2(Cx, Cy - (FaceR * 0.1f)),
                GradientOriginOffset = Vector2.Zero,
                RadiusX = FaceR * 1.2f,
                RadiusY = FaceR * 1.2f,
            },
            stops);

        return _faceBrush;
    }

    /// <summary>
    /// 刻度与数字是**同一套东西**：每千转一个大刻度（正对数字），每 500 转一个小刻度。
    /// 两者都由量程推导，量程变化时一起压密 —— 不能像以前那样把刻度写死成「每 18° 一根」，
    /// 那只在原型 8 千转时恰好等于 500 转（2026-11-21 实机截图：10 千转时数字每千转一个、刻度还是 18°）。
    /// </summary>
    private void DrawTicks(ID2D1RenderTarget target, float spanRpm)
    {
        ID2D1SolidColorBrush major = GetBrush(target, TickMajorColor);
        ID2D1SolidColorBrush minor = GetBrush(target, TickMinorColor);

        int thousands = ThousandCountFor(spanRpm);
        float minorDeg = MinorTickDegFor(spanRpm);

        // 0 到 span 之间每 500 转一根，共 thousands*2 + 1 根（含两端的 0 和满量程）
        int tickCount = (thousands * 2) + 1;

        for (int i = 0; i < tickCount; i++)
        {
            float angle = TachoAngleStart + (i * minorDeg);
            bool isMajor = (i % 2) == 0;

            Vector2 p1 = PointOn(isMajor ? TickMajorIn : TickMinorIn, angle);
            Vector2 p2 = PointOn(isMajor ? TickMajorOut : TickMinorOut, angle);

            target.DrawLine(
                p1,
                p2,
                isMajor ? major : minor,
                isMajor ? TickMajorWidth : TickMinorWidth);
        }
    }

    /// <summary>
    /// 6 层点阵合并成一条几何，量程或红区分界变化时重建。
    /// 660 个点若每帧逐个 DrawEllipse，帧时间会明显上升，所以这里缓存。
    /// 点阵青→红两态、不经黄色 —— 这是本项目明确定义的例外（见 docs/HUD模块开发说明.md §5.3），
    /// 与 RpmColorScale 的三段式不同，不要「顺手改成规范」。
    /// </summary>
    private void DrawDots(ID2D1RenderTarget target)
    {
        if (_dotsGeometry is null || _redDotsGeometry is null)
        {
            return;
        }

        // 不透明度取自实测：原型点阵区青色像素峰值 (89,174,187)，
        // 接近 #1fb9cc 满值；用 0.55 会明显偏暗、点几乎看不见（踩过一次）
        target.FillGeometry(_dotsGeometry, GetBrush(target, new Color4(Cyan.R, Cyan.G, Cyan.B, 0.85f)), null);
        target.FillGeometry(_redDotsGeometry, GetBrush(target, new Color4(Red.R, Red.G, Red.B, 0.92f)), null);
    }

    /// <summary>
    /// 重建点阵几何。红区分界由红区比例决定，所以几何缓存键是 (spanRpm, redlineRatio)。
    /// 分成两条几何而不是一个填充：红点不透明度 0.92、青点 0.55，单条几何只能用一种画刷。
    /// </summary>
    private void EnsureDotGeometry(ID2D1RenderTarget target, float spanRpm, float redlineRatio)
    {
        if (_dotsGeometry is not null
            && Math.Abs(_dotsGeometrySpan - spanRpm) < 0.5f
            && Math.Abs(_dotsGeometryRatio - redlineRatio) < 0.001f)
        {
            return;
        }

        _dotsGeometry?.Dispose();
        _redDotsGeometry?.Dispose();
        _dotsGeometry = null;
        _redDotsGeometry = null;

        if (target.Factory is null)
        {
            return;
        }

        // 点阵铺满固定的 288° 区域（92°~380°），红点按实际红区比例切换
        float spanDeg = SweepDeg;
        float normalEnd = TachoAngleStart + (spanDeg * redlineRatio);

        ID2D1PathGeometry cells = target.Factory.CreatePathGeometry();
        ID2D1PathGeometry redCells = target.Factory.CreatePathGeometry();

        using (ID2D1GeometrySink sink = cells.Open())
        using (ID2D1GeometrySink redSink = redCells.Open())
        {
            for (float a = DotAngleStart; a <= DotAngleEnd; a += DotStepDeg)
            {
                ID2D1GeometrySink active = a >= normalEnd ? redSink : sink;

                for (int layer = 0; layer < DotLayers; layer++)
                {
                    float r = DotFirstR + (layer * DotLayerStep);
                    Vector2 p = PointOn(r, a);

                    active.BeginFigure(new Vector2(p.X + DotRadius, p.Y), FigureBegin.Filled);
                    active.AddArc(new ArcSegment
                    {
                        Point = new Vector2(p.X - DotRadius, p.Y),
                        Size = new Size(DotRadius, DotRadius),
                        RotationAngle = 0f,
                        SweepDirection = SweepDirection.Clockwise,
                        ArcSize = ArcSize.Small,
                    });
                    active.AddArc(new ArcSegment
                    {
                        Point = new Vector2(p.X + DotRadius, p.Y),
                        Size = new Size(DotRadius, DotRadius),
                        RotationAngle = 0f,
                        SweepDirection = SweepDirection.Clockwise,
                        ArcSize = ArcSize.Small,
                    });
                    active.EndFigure(FigureEnd.Closed);
                }
            }

            sink.Close();
            redSink.Close();
        }

        _dotsGeometry = cells;
        _redDotsGeometry = redCells;
        _dotsGeometrySpan = spanRpm;
        _dotsGeometryRatio = redlineRatio;
    }

    /// <summary>
    /// 红区带：只是把这一段的环底色换成红色，**刻度由 <see cref="DrawTicks"/> 统一绘制**，
    /// 不要在红区里再画一遍 —— 重画时若以红区起点为基准递增，起点不是 18° 的倍数就会整体歪掉。
    /// </summary>
    private void DrawRedArc(ID2D1RenderTarget target, float redlineRatio, float sweepDeg, float spanRpm)
    {
        float ratio = Math.Clamp(redlineRatio, 0f, 1f);

        float a1 = TachoAngleStart + (ratio * sweepDeg);
        float a2 = TachoAngleStart + sweepDeg;

        if (a2 <= a1)
        {
            return;
        }

        // 红区带：原型带 feGaussianBlur 外发光，这里用更宽的低透明度描边垫底
        if (BuildArc(target, BezelR, a1, a2) is { } glowArc)
        {
            target.DrawGeometry(
                glowArc,
                GetBrush(target, new Color4(Red.R, Red.G, Red.B, 0.30f)),
                BezelWidth + 8f);
            glowArc.Dispose();
        }

        if (BuildArc(target, BezelR, a1, a2) is { } redArc)
        {
            target.DrawGeometry(redArc, GetBrush(target, Red), BezelWidth);
            redArc.Dispose();
        }

        // 红带盖住了刻度，按同一套步进把红区内的刻度再描一遍深色
        RedrawRedZoneTicks(target, spanRpm, a1, a2);
    }

    /// <summary>
    /// 红区内的刻度需要**用深色重画一遍**，压在红底上才看得见。
    /// 但重画必须复用 <see cref="DrawTicks"/> 的整套步进逻辑（每千转大刻度、每 500 转小刻度），
    /// 不能像 2026-11-21 那样从红区起点按固定 18° 递增 —— 起点不是步进的整数倍，整段刻度会歪。
    /// 所以这里只负责「把落在红区范围内、且角度与正常刻度重合的那些」再描一遍深色。
    /// </summary>
    private void RedrawRedZoneTicks(ID2D1RenderTarget target, float spanRpm, float a1, float a2)
    {
        ID2D1SolidColorBrush dark = GetBrush(target, RedTickColor);

        int thousands = ThousandCountFor(spanRpm);
        float minorDeg = MinorTickDegFor(spanRpm);
        int tickCount = (thousands * 2) + 1;

        for (int i = 0; i < tickCount; i++)
        {
            float angle = TachoAngleStart + (i * minorDeg);

            if (angle < a1 - 0.01f || angle > a2 + 0.01f)
            {
                continue;
            }

            bool isMajor = (i % 2) == 0;
            Vector2 p1 = PointOn(isMajor ? TickMajorIn : TickMinorIn, angle);
            Vector2 p2 = PointOn(isMajor ? TickMajorOut : TickMinorOut, angle);

            // 红底上小刻度要多给 1.5px 线宽，否则 2px 会被抗锯齿糊掉、几乎看不见
            float width = isMajor ? TickMajorWidth : TickMinorWidth + (1.5f * Scale);
            target.DrawLine(p1, p2, dark, width);
        }
    }

    private static ID2D1PathGeometry? BuildArc(ID2D1RenderTarget target, float radius, float a1, float a2)
    {
        if (target.Factory is null)
        {
            return null;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();

        Vector2 start = PointOn(radius, a1);
        Vector2 end = PointOn(radius, a2);

        sink.BeginFigure(start, FigureBegin.Hollow);
        sink.AddArc(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            RotationAngle = 0f,
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = (a2 - a1) > 180f ? ArcSize.Large : ArcSize.Small,
        });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        return geometry;
    }

    private void DrawNumbers(ID2D1RenderTarget target, float spanRpm)
    {
        int top = Math.Max(1, (int)Math.Round(spanRpm / 1000f));

        // 数字始终铺满固定的 288°：8 千转时 36°/个（原型值），9 千转时 32°/个，贴得更密但不越界
        float step = ThousandDegFor(top * 1000f);

        ID2D1SolidColorBrush brush = GetBrush(target, White);

        for (int i = 0; i <= top; i++)
        {
            Vector2 p = PointOn(NumR, TachoAngleStart + (i * step));
            HudDraw.CenteredText(target, i.ToString(CultureInfo.InvariantCulture), _numFormat, brush, p.X, p.Y, 40f * Scale, 50f * Scale);
        }

        HudDraw.CenteredText(target, "x1000 r/min", _unitFormat, GetBrush(target, UnitText), Cx, UnitY, 120f * Scale, 20f * Scale);
    }

    private void DrawPurpleBand(ID2D1RenderTarget target)
    {
        // 纯装饰的运动模式标识，不参与任何数据映射（用户明确要求保持静态）
        if (BuildArc(target, BezelR, PurpleStartDeg, PurpleEndDeg) is not { } arc)
        {
            return;
        }

        target.DrawGeometry(arc, GetBrush(target, new Color4(0xB0 / 255f, 0x2A / 255f, 0xC0 / 255f, 1f)), BezelWidth);
        arc.Dispose();

        // 两端封口横线（原型 26°/82° 各一条，浅紫）
        ID2D1SolidColorBrush edge = GetBrush(target, new Color4(0xF0 / 255f, 0xAB / 255f, 0xFC / 255f, 0.8f));
        foreach (float a in (float[])[PurpleStartDeg, PurpleEndDeg])
        {
            Vector2 p1 = PointOn(BezelR - (BezelWidth * 0.5f), a);
            Vector2 p2 = PointOn(BezelR + (BezelWidth * 0.5f), a);
            target.DrawLine(p1, p2, edge, 1f * Scale);
        }

        // Sport S 文字：原型沿 textPath 排版，实测只占紫带中间偏右一小段
        // （中心 54.6°、跨度仅约 12°），不是铺满整条 26°~82° 的带子。
        DrawArcText(target, "Sport S", _sportFormat, White, PurpleTextR, SportTextCenterDeg, SportTextSpanDeg);
    }

    /// <summary>
    /// 沿一段圆弧逐字排版：每个字符按自己的角度旋转并推出半径。
    /// 参数是**中心角**与**总跨度**，不是起止角 —— 原型的 textPath 用 startOffset 50% 居中，
    /// 这里保持一致，文字才会落在紫带中段而不是铺满整条带。
    /// </summary>
    private void DrawArcText(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        Color4 color,
        float radius,
        float centerDeg,
        float spanDeg)
    {
        if (format is null || text.Length == 0)
        {
            return;
        }

        // 字距按**非空格字符数**均分：空格不占独立字位，否则 "Sport S" 会被拉成 "S p o r t   S"。
        int glyphCount = 0;
        foreach (char c in text)
        {
            if (c != ' ')
            {
                glyphCount++;
            }
        }

        if (glyphCount == 0)
        {
            return;
        }

        float step = spanDeg / glyphCount;

        // 原型：`arcD(448, 80, 28)` —— 路径从 80° 走到 28°，**角度递减**，
        // textPath 沿「前进方向」排字，所以首字母 S 落在 80° 端（右上），末尾 S 落在 28° 端（右下）。
        // 这里严格照搬：角度递减排布。
        float cursor = centerDeg + (spanDeg * 0.5f) - (step * 0.5f);

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            float a = cursor;

            // 与上面的递减方向保持一致
            cursor -= ch == ' ' ? step * 0.5f : step;

            if (ch == ' ')
            {
                continue;
            }

            Vector2 p = PointOn(radius, a);

            Matrix3x2 saved = target.Transform;

            // 旋转角 = a - 90°，配合上面的「角度递减」。
            // 这组搭配的字序与方向都与原型一致；单独把旋转角反过来会得到倒字。
            // 变换顺序（System.Numerics 行向量约定，A * B = 先 A 后 B）：先转、再平移到该字符的位置。
            target.Transform = Matrix3x2.CreateRotation((a - 90f) * MathF.PI / 180f)
                * Matrix3x2.CreateTranslation(p.X, p.Y)
                * saved;

            // 旋转后字符中心在局部原点，所以以 (0,0) 为中心画。
            // 槽宽按字符跨度估算后放大 1.6 倍，太窄会把字挤歪。
            float slotWidth = Math.Max(6f, (step * radius * MathF.PI / 180f) * 1.6f);
            HudDraw.CenteredText(
                target,
                ch.ToString(),
                format,
                GetBrush(target, color),
                0f,
                0f,
                slotWidth,
                20f * Scale);

            target.Transform = saved;
        }
    }

    /// <summary>中央信息区：车速定槽数字 → km/h + 两侧横线 → 档位。</summary>
    private void DrawCenterInfo(ID2D1RenderTarget target, string speedText, string gearText)
    {
        // 速度必须走定槽绘制 —— 项目明确约定，比例字体下数字宽度不同会让整块区域抖动。
        // SHIFT TREE 用 FixedDigits，GHOST 手写定槽循环，这里沿用 FixedDigits。
        HudDraw.FixedDigits(
            target,
            speedText,
            _speedFormat,
            GetBrush(target, White),
            Cx,
            SpeedY,
            SpeedSlotWidth,
            100f * Scale);

        HudDraw.CenteredText(target, "km/h", _kmhFormat!, GetBrush(target, KmhText), Cx, KmhY, 80f * Scale, 20f * Scale);

        ID2D1SolidColorBrush line = GetBrush(target, KmhLine);
        target.DrawLine(new Vector2(KmhLineLeftX1, KmhLineY), new Vector2(KmhLineLeftX2, KmhLineY), line, 1f * Scale);
        target.DrawLine(new Vector2(KmhLineRightX1, KmhLineY), new Vector2(KmhLineRightX2, KmhLineY), line, 1f * Scale);

        HudDraw.CenteredText(target, gearText, _gearFormat!, GetBrush(target, White), Cx, GearY, 100f * Scale, 60f * Scale);
    }

    private void DrawNeedle(ID2D1RenderTarget target, float angleDeg)
    {
        Matrix3x2 saved = target.Transform;

        // 绕圆心旋转：System.Numerics 是行向量约定，A * B 表示「先 A 后 B」，
        // 所以顺序必须是 T(-C) → R → T(+C)。写反会把指针甩出画布（踩过一次）。
        target.Transform = Matrix3x2.CreateTranslation(-Cx, -Cy)
            * Matrix3x2.CreateRotation(angleDeg * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(Cx, Cy)
            * saved;

        // 指针本体：一个指向 +x 的尖三角
        Vector2 baseLow = new(NeedleBaseX, Cy + NeedleHalfWidth);
        Vector2 baseHigh = new(NeedleBaseX, Cy - NeedleHalfWidth);
        Vector2 tip = new(NeedleTipX, Cy);

        // 外发光：原型 filter="url(#glowRed)" 是 feGaussianBlur 弥散，本项目做不出模糊，
        // 用「三层由宽到窄、由淡到实的三角叠加」模拟边缘扩散。
        // 单层硬边三角在实测里会让指针在 r80 处只有 5px（原型 7px），看着明显偏瘦。
        for (int i = 3; i >= 1; i--)
        {
            float widen = NeedleGlowHalfWidth + (i * 2.5f * Scale);
            float alpha = 0.10f * (4 - i);

            if (BuildTriangle(
                    target,
                    new Vector2(NeedleGlowBaseX - (i * 3f * Scale), Cy + widen),
                    new Vector2(NeedleTipX, Cy),
                    new Vector2(NeedleGlowBaseX - (i * 3f * Scale), Cy - widen)) is { } halo)
            {
                target.FillGeometry(halo, GetBrush(target, new Color4(0xFF / 255f, 0x3D / 255f, 0x00 / 255f, alpha)), null);
                halo.Dispose();
            }
        }

        // 内层实心发光（原型 525,494→888,500→525,506）
        if (BuildTriangle(
                target,
                new Vector2(NeedleGlowBaseX, Cy + NeedleGlowHalfWidth),
                new Vector2(NeedleTipX, Cy),
                new Vector2(NeedleGlowBaseX, Cy - NeedleGlowHalfWidth)) is { } glow)
        {
            target.FillGeometry(glow, GetBrush(target, new Color4(0xFF / 255f, 0x3D / 255f, 0x00 / 255f, 0.75f)), null);
            glow.Dispose();
        }

        // 主体：原型 needleGrad 沿长度方向渐变（暗红 → 橙 → 亮橙），不是纯色
        if (GetNeedleBrush(target) is { } needleBrush)
        {
            FillTriangle(target, baseLow, tip, baseHigh, needleBrush);
        }
        else if (BuildTriangle(target, baseLow, tip, baseHigh) is { } body)
        {
            target.FillGeometry(body, GetBrush(target, new Color4(0xFF / 255f, 0x4D / 255f, 0x00 / 255f, 1f)), null);
            body.Dispose();
        }

        // 高光细线（原型 545,500 → 880,500）
        target.DrawLine(
            new Vector2(NeedleHighlightX1, Cy),
            new Vector2(NeedleHighlightX2, Cy),
            GetBrush(target, new Color4(0xFF / 255f, 0xD0 / 255f, 0x8A / 255f, 0.8f)),
            1.5f * Scale);

        target.Transform = saved;

        // 转轴盖不随指针转，画在旋转之外
        target.FillEllipse(
            new Ellipse(new Vector2(Cx, Cy), HubR, HubR),
            GetBrush(target, new Color4(0x07 / 255f, 0x13 / 255f, 0x17 / 255f, 1f)));

        target.DrawEllipse(
            new Ellipse(new Vector2(Cx, Cy), HubR, HubR),
            GetBrush(target, new Color4(0xFF / 255f, 0x5A / 255f, 0x1F / 255f, 0.9f)),
            2f * Scale);
    }

    /// <summary>
    /// 指针渐变：原型 <c>needleGrad</c> 沿长度方向从暗红过渡到亮橙。
    /// 渐变轴必须横跨指针全长（基座 → 针尖），否则尖端会整块变色。
    /// </summary>
    private ID2D1LinearGradientBrush? GetNeedleBrush(ID2D1RenderTarget target)
    {
        if (_needleBrush is not null)
        {
            return _needleBrush;
        }

        if (target.Factory is null)
        {
            return null;
        }

        using ID2D1GradientStopCollection stops = target.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0f, Color = new Color4(0x8B / 255f, 0x1A / 255f, 0x00 / 255f, 1f) },
            new GradientStop { Position = 0.55f, Color = new Color4(0xFF / 255f, 0x4D / 255f, 0x00 / 255f, 1f) },
            new GradientStop { Position = 1f, Color = new Color4(0xFF / 255f, 0xB3 / 255f, 0x47 / 255f, 1f) },
        ]);

        _needleBrush = target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(NeedleBaseX, Cy),
                EndPoint = new Vector2(NeedleTipX, Cy),
            },
            stops);

        return _needleBrush;
    }

    /// <summary>用渐变画刷填充三角形：建几何 → 填 → 立即释放（几何每帧新建，不进缓存）。</summary>
    private static void FillTriangle(
        ID2D1RenderTarget target,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        ID2D1Brush brush)
    {
        if (BuildTriangle(target, a, b, c) is not { } geometry)
        {
            return;
        }

        target.FillGeometry(geometry, brush);
        geometry.Dispose();
    }

    private static ID2D1PathGeometry? BuildTriangle(ID2D1RenderTarget target, params Vector2[] points)
    {        if (target.Factory is null || points.Length < 3)
        {
            return null;
        }

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();

        sink.BeginFigure(points[0], FigureBegin.Filled);
        for (int i = 1; i < points.Length; i++)
        {
            sink.AddLine(points[i]);
        }

        sink.EndFigure(FigureEnd.Closed);
        sink.Close();

        return geometry;
    }

    public void Dispose()
    {
        foreach (ID2D1SolidColorBrush brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();

        _dotsGeometry?.Dispose();
        _redDotsGeometry?.Dispose();

        _bezelBrush?.Dispose();
        _faceBrush?.Dispose();
        _needleBrush?.Dispose();

        _numFormat?.Dispose();
        _unitFormat?.Dispose();
        _sportFormat?.Dispose();
        _speedFormat?.Dispose();
        _kmhFormat?.Dispose();
        _gearFormat?.Dispose();
    }
}
