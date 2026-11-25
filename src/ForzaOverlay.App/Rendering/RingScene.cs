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
/// 极简弧环（FH4 风格）单表渲染器：一条转速弧 + 中央档位/车速 + 底部增压弧。
/// 几何来自原型 <c>temp/极简弧环.html</c>。
/// <para>
/// 原型画布 380×380，但图形包围盒只有 (39,25)–(341,345) = 302×320；
/// 按项目惯例四边各加 5px 安全边距后对外基准取 <b>312×330</b>，
/// 内部坐标仍是原型原值，靠 <see cref="ShiftX"/> / <see cref="ShiftY"/> 平移吸收。
/// </para>
/// <para>
/// 角度制跟咬地鲨 / ES 同系（SVG 制）：<c>0° = 正右</c>、y 轴向下、<see cref="PointOn"/> 用 <c>cos/sin</c>。
/// 不要和仿NFS19 / SHIFT TREE 的「0° 正上、sin/-cos」混用 —— 混了弧整体偏，编译却零错误。
/// </para>
/// <para>
/// 配色是本项目 <see cref="Ui.RpmColorScale"/> 的**第四个例外**（前三个：SHIFT TREE、咬地鲨、ES）：
/// 原型规定超红区后整条弧变紫 <c>#c86bff</c> 并带呼吸辉光，不走黄→红。
/// </para>
/// </summary>
internal sealed class RingScene : IDisposable
{
    // ── 基准与坐标 ──

    /// <summary>对外基准宽：原型包围盒 302 + 左右各 5px 安全边距。</summary>
    internal const float BaseWidth = 312f;

    /// <summary>对外基准高：原型包围盒 320 + 上下各 5px 安全边距。</summary>
    internal const float BaseHeight = 330f;

    /// <summary>原型包围盒左上角 (39,25) 各减 5px 安全边距，得到内容平移量。</summary>
    private const float ShiftX = -34f;
    private const float ShiftY = -20f;

    /// <summary>原型圆心 (190,176) 平移到基准坐标后落在 (156,156)。</summary>
    private const float Cx = 190f + ShiftX;
    private const float Cy = 176f + ShiftY;

    // ── 弧与刻度（原型值，1:1 不缩放）──

    /// <summary>转速主弧半径。</summary>
    private const float RadialR = 148f;

    /// <summary>主弧线宽（原型 6）。</summary>
    private const float RadialWidth = 6f;

    /// <summary>内侧那条极淡的参考圈半径（原型 104，1px butt）。</summary>
    private const float InnerRingR = 104f;

    /// <summary>刻度起点半径（原型 R-8）。</summary>
    private const float TickR = RadialR - 8f;
    private const float TickLenMajor = 9f;
    private const float TickLenMinor = 4f;

    /// <summary>数字圈半径（原型 R-30）。</summary>
    private const float NumR = RadialR - 30f;

    /// <summary>增压弧半径（原型 128）。</summary>
    private const float BoostR = 128f;
    private const float BoostWidth = 4f;

    /// <summary>表盘起始角：135°（左下），与原型 <c>A0 = Math.PI * 0.75</c> 一致。</summary>
    private const float StartAngleDeg = 135f;

    /// <summary>扫掠角 270°，与原型 <c>SW = Math.PI * 1.5</c> 一致。</summary>
    private const float SweepDeg = 270f;

    /// <summary>增压弧两端角度（原型 <c>Math.PI * .32</c> / <c>.68</c>）。</summary>
    private const float BoostStartDeg = 57.6f;
    private const float BoostEndDeg = 122.4f;

    // ── 增压量程 ──

    /// <summary>量程下限：进气歧管真空（负值是真实物理量，不是脏数据）。</summary>
    private const float BoostMin = -1.0f;

    /// <summary>量程上限。</summary>
    private const float BoostMax = 3.0f;

    private const float BoostSpan = BoostMax - BoostMin;

    /// <summary>0 bar 在量程上的位置 = 0.25，真空段只占 1/4。</summary>
    private const float BoostZeroFraction = (0f - BoostMin) / BoostSpan;

    // ── 文字位置（相对圆心的原型偏移）──

    private const float RpmLabelDy = -86f;
    private const float GearLabelDy = -66f;
    private const float GearNumberDy = -6f;
    private const float SpeedDy = 66f;
    private const float KmhDy = 92f;

    /// <summary>增压读数在原型 y=338。</summary>
    private const float BoostTextY = 338f + ShiftY;

    // ── 字号（原型值）──

    private const float TickNumSize = 12f;
    private const float RpmLabelSize = 9f;
    private const float GearLabelSize = 11f;
    private const float GearNumberSize = 96f;
    private const float SpeedSize = 46f;
    private const float KmhSize = 12f;
    private const float BoostTextSize = 13f;

    /// <summary>
    /// 速度数字的定槽宽度。**必须定槽** —— Rajdhani 是比例字体，
    /// 直接 <c>CenteredText</c> 时「11」与「88」占宽不同，整行会随数值持续左右抖（用户 2026-11-26 实机发现）。
    /// <para>
    /// 实测（46px 字号，2026-11-26）：单字墨迹 24px、888 总宽 68px，单字进位 22.7px，取 23。
    /// </para>
    /// <para>
    /// 用**中心定槽**（<see cref="HudDraw.FixedDigits"/>）而不是右对齐：
    /// 速度表在表盘正中，块中心必须钉在 <see cref="Cx"/>。右对齐会让一位数偏右、三位数偏左各一格。
    /// 定槽后「11」与「88」逐位落点完全相同，只在数字位数变化（99→100）时有一次性位移，那是必然的。
    /// </para>
    /// </summary>
    private const float SpeedSlotWidth = 23f;

    /// <summary>速度行高度：定槽绘制时给足行高，避免「8」的下缘被裁。</summary>
    private const float SpeedSlotHeight = 60f;

    /// <summary>
    /// 增压读数保留的小数位数，统一取项目级标准 <see cref="HudDraw.BoostDecimals"/>。
    /// 用户 2026-11-26 要求任何情况都显示两位，不再跟随原型 <c>fmtBoost</c> 的一位。
    /// </summary>
    private const int BoostDecimals = HudDraw.BoostDecimals;

    /// <summary>
    /// 增压数字的字符格宽度（符号 / 数字 / 小数点 / 两位数字），构造期按字体实测填。
    /// <para>
    /// 增压量程锁死 -1.0~3.0，整数段永远 1 位，所以数字块恒为 5 个字符；
    /// 每格宽度固定后整块宽度恒定，数值怎么变都不会重新居中 —— 这就是「不跳动」的实现方式。
    /// </para>
    /// <para>
    /// 符号格取 <c>+</c> 与 <c>-</c> 里较宽的那个，否则正负切换时整行会横向位移。
    /// </para>
    /// </summary>
    private float[] _boostCells = [];

    /// <summary>「BOOST」「bar」两段与中间空格的实测宽度，构造期算一次。</summary>
    private float _boostPrefixWidth;
    private float _boostSuffixWidth;
    private float _boostGapWidth;

    /// <summary>换挡闪光时档位数字的放大倍率（原型 <c>1 + flash * .12</c>）。</summary>
    private const float GearFlashGrow = 0.12f;

    /// <summary>闪光衰减速度（原型 <c>flash -= dt * 3.2</c>，约 0.31 秒衰减完）。</summary>
    private const float FlashDecayPerSecond = 3.2f;

    // ── 配色 ──

    private static readonly Color4 White = new(1f, 1f, 1f, 1f);

    /// <summary>红区之后的紫色（本场景的配色例外，见类注释）。</summary>
    private static readonly Color4 HotPurple = new(0xC8 / 255f, 0x6B / 255f, 0xFF / 255f, 1f);

    /// <summary>红区起点标记点与红区刻度。</summary>
    private static readonly Color4 RedlineRed = new(0xFF / 255f, 0x3B / 255f, 0x5C / 255f, 1f);

    private static readonly Color4 TrackColor = new(1f, 1f, 1f, 0.10f);
    private static readonly Color4 FaintRingColor = new(1f, 1f, 1f, 0.05f);
    private static readonly Color4 TickMinorColor = new(1f, 1f, 1f, 0.40f);
    private static readonly Color4 TickMajorColor = new(1f, 1f, 1f, 0.80f);
    private static readonly Color4 TickNumColor = new(1f, 1f, 1f, 0.60f);

    /// <summary>增压真空段（负压）灰阶。</summary>
    private static readonly Color4 BoostVacuumColor = new(0x7A / 255f, 0x81 / 255f, 0x94 / 255f, 1f);

    /// <summary>增压正压段紫。</summary>
    private static readonly Color4 BoostPositiveColor = new(0x9D / 255f, 0x6B / 255f, 0xFF / 255f, 1f);

    /// <summary>负压时的读数文字颜色。</summary>
    private static readonly Color4 BoostVacuumText = new(0x9A / 255f, 0xA0 / 255f, 0xB5 / 255f, 0.85f);

    /// <summary>正压时的读数文字颜色。</summary>
    private static readonly Color4 BoostPositiveText = new(0xC9 / 255f, 0xA6 / 255f, 0xFF / 255f, 0.85f);

    /// <summary>升挡箭头紫。</summary>
    private static readonly Color4 ShiftUpColor = new(0xC9 / 255f, 0xA6 / 255f, 0xFF / 255f, 1f);

    /// <summary>降挡箭头灰。</summary>
    private static readonly Color4 ShiftDownColor = new(0x8B / 255f, 0x90 / 255f, 0xA5 / 255f, 1f);

    private readonly IDWriteFactory? _dwrite;

    /// <summary>内置字体集合（含 Rajdhani）；为 null 时回退系统字体栈。</summary>
    private readonly IDWriteFontCollection? _fonts;

    /// <summary>解析出的 Rajdhani 家族名，构造时算一次，避免每帧重找。</summary>
    private readonly string _family;

    private IDWriteTextFormat? _tickNumFormat;
    private IDWriteTextFormat? _rpmLabelFormat;
    private IDWriteTextFormat? _gearLabelFormat;
    private IDWriteTextFormat? _gearNumberFormat;
    private IDWriteTextFormat? _speedFormat;
    private IDWriteTextFormat? _kmhFormat;
    private IDWriteTextFormat? _boostTextFormat;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];

    private ID2D1StrokeStyle? _roundStroke;
    private ID2D1StrokeStyle? _flatStroke;

    /// <summary>换挡闪光强度 0~1，随帧衰减。原型用它驱动档位放大 + 辉光 + 方向箭头。</summary>
    private float _flash;

    /// <summary>本次闪光是升挡（+1）还是降挡（-1）。</summary>
    private int _shiftDirection = 1;

    /// <summary>上一帧档位，用于判断换挡；<c>int.MinValue</c> 表示还没有效样本。</summary>
    private int _lastGear = int.MinValue;

    private string _stableGearText = "N";

    internal RingScene(IDWriteFactory? dwrite, IDWriteFontCollection? customFonts)
    {
        _dwrite = dwrite;
        _fonts = customFonts;
        _family = FindFamilyName(customFonts) ?? "Rajdhani";

        _tickNumFormat = CreateFormat(TickNumSize, FontWeight.Bold);
        _rpmLabelFormat = CreateFormat(RpmLabelSize, FontWeight.SemiBold);
        _gearLabelFormat = CreateFormat(GearLabelSize, FontWeight.Bold);
        _gearNumberFormat = CreateFormat(GearNumberSize, FontWeight.Bold);
        _speedFormat = CreateFormat(SpeedSize, FontWeight.Bold);
        _kmhFormat = CreateFormat(KmhSize, FontWeight.Bold);
        _boostTextFormat = CreateFormat(BoostTextSize, FontWeight.Bold);
        MeasureBoostRow();
    }

    /// <summary>
    /// 建文字格式。**只允许在构造期调用** —— 每帧新建 IDWriteTextFormat 会持续分配，
    /// 动态字号（换挡放大）改用 Matrix3x2 变换缩放，不再重建格式。
    /// </summary>
    private IDWriteTextFormat? CreateFormat(float size, FontWeight weight)
    {
        if (_dwrite is null)
        {
            return null;
        }

        // 原型字体栈是 Rajdhani → Noto Sans SC → sans-serif。项目内置 Rajdhani（OFL）；
        // 集合里找不到时 DWrite 会自己回退，不会抛。
        IDWriteTextFormat format = _fonts is not null
            ? _dwrite.CreateTextFormat(_family, _fonts, weight, FontStyle.Normal, FontStretch.Normal, size, "zh-CN")
            : _dwrite.CreateTextFormat(_family, null!, weight, FontStyle.Normal, FontStretch.Normal, size, "zh-CN");

        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
        return format;
    }

    /// <summary>
    /// 量出增压读数一整行各段的宽度，构造期算一次。
    /// <para>
    /// 数字格取 0~9 里**最宽**的那个，符号格取 <c>+</c>/<c>-</c> 里较宽的、
    /// 小数点单独一格。这样无论读数怎么变，5 个字符格的总宽恒定，
    /// 整行既不会重新居中、位与位之间也不会互相推挤（用户 2026-11-26 要求「不跳动」）。
    /// </para>
    /// </summary>
    private void MeasureBoostRow()
    {
        _boostPrefixWidth = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, "BOOST");
        _boostSuffixWidth = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, "bar");

        // 空格宽度量不到（某些字体的 space 无墨迹、度量返回 0）时按字号兜底，
        // 否则 BOOST 与数字会贴在一起。
        float gap = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, " ");
        _boostGapWidth = gap > 0.5f ? gap : BoostTextSize * 0.34f;

        _boostCells = HudDraw.BuildSignedCells(_dwrite, _boostTextFormat, BoostDecimals, BoostTextSize);
    }

    /// <summary>在内置字体集合里找 Rajdhani；名字可能带字重后缀。</summary>
    private static string? FindFamilyName(IDWriteFontCollection? collection)
    {
        if (collection is null)
        {
            return null;
        }

        string[] candidates = ["Rajdhani", "Rajdhani SemiBold", "Rajdhani-SemiBold"];
        foreach (string candidate in candidates)
        {
            if (collection.FindFamilyName(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
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
        int viewportHeight,
        double deltaSeconds)
    {
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.26f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        float spanRpm = ResolveTachoSpan(maxRpm);
        float redlineRatio = ResolveRedlineRatio(learner, packet, hasPacket, useLearnedRedline);

        float rpmRatio = hasPacket ? Math.Clamp(packet.CurrentEngineRpm / spanRpm, 0f, 1f) : 0f;
        float boostValue = hasPacket ? Math.Clamp(packet.BoostBar, BoostMin, BoostMax) : 0f;

        AdvanceFlash(hasPacket ? packet.GearNumber : int.MinValue, deltaSeconds);

        Matrix3x2 caller = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        DrawInnerRing(target);
        DrawTrack(target);
        DrawValueArc(target, rpmRatio, redlineRatio);
        DrawTicks(target, spanRpm, redlineRatio);
        DrawNumbers(target, spanRpm, redlineRatio);
        DrawStaticLabels(target);
        DrawBoostArc(target, boostValue);
        DrawGearAndSpeed(target, packet, hasPacket);

        target.Transform = caller;
    }

    /// <summary>
    /// 转速量程：按千转向上取整（7500→8 千转），夹在 4~16 之间。
    /// 与 ES / 咬地鲨各自维护一份 —— 单表要按整数千转标数字，零头量程没法标刻度。
    /// </summary>
    private static float ResolveTachoSpan(float maxRpm)
    {
        int thousands = (int)Math.Ceiling(Math.Max(maxRpm, 1000f) / 1000f);
        thousands = Math.Clamp(thousands, 4, 16);
        return thousands * 1000f;
    }

    /// <summary>红区起点比例：开启校正时跟学习值，否则回退 75%（与其余单表一致）。</summary>
    private static float ResolveRedlineRatio(
        ShiftPointLearner learner,
        in ForzaDataPacket packet,
        bool hasPacket,
        bool useLearnedRedline)
    {
        if (!hasPacket || !useLearnedRedline)
        {
            return 0.75f;
        }

        return (float)Math.Clamp(learner.GetRedlineRatio(packet), 0.5, 1.0);
    }

    /// <summary>换挡闪光推进：档位变化时置 1，之后按原型速率衰减。</summary>
    private void AdvanceFlash(int gear, double deltaSeconds)
    {
        if (gear is >= 1 and <= 10)
        {
            if (_lastGear != int.MinValue && gear != _lastGear)
            {
                _shiftDirection = gear > _lastGear ? 1 : -1;
                _flash = 1f;
            }

            _lastGear = gear;
        }

        _flash = Math.Max(0f, _flash - ((float)deltaSeconds * FlashDecayPerSecond));
    }

    private void DrawInnerRing(ID2D1RenderTarget target)
    {
        target.DrawEllipse(
            new Ellipse(new Vector2(Cx, Cy), InnerRingR, InnerRingR),
            GetBrush(target, FaintRingColor),
            1f,
            FlatStroke(target));
    }

    private void DrawTrack(ID2D1RenderTarget target)
    {
        // 底轨用圆头：原型 ring() 的默认 cap 是 "round"，只有内圈显式传了 "butt"。
        DrawArc(target, RadialR, StartAngleDeg, StartAngleDeg + SweepDeg, TrackColor, RadialWidth, RoundStroke(target));
    }

    private void DrawValueArc(ID2D1RenderTarget target, float rpmRatio, float redlineRatio)
    {
        if (rpmRatio <= 0f)
        {
            return;
        }

        bool hot = rpmRatio > redlineRatio;
        Color4 color = hot ? HotPurple : White;
        float endAngle = StartAngleDeg + (SweepDeg * rpmRatio);

        // 原型用 shadowBlur 12~18 做弥散；D2D 没有模糊，用「更宽 + 更低透明度」的三层描边伪造辉光。
        if (hot)
        {
            float pulse = 0.5f + (0.5f * MathF.Sin((float)Environment.TickCount64 / 160f));
            for (int i = 3; i >= 1; i--)
            {
                float alpha = (0.05f + (0.03f * pulse)) * i;
                DrawArc(
                    target,
                    RadialR,
                    StartAngleDeg,
                    endAngle,
                    new Color4(HotPurple.R, HotPurple.G, HotPurple.B, alpha),
                    RadialWidth + (i * 4f),
                    RoundStroke(target));
            }
        }

        DrawArc(target, RadialR, StartAngleDeg, endAngle, color, RadialWidth, RoundStroke(target));

        // 弧末端圆点带头（原型 dot 半径 4.5 + 辉光 14）
        Vector2 head = PointOn(RadialR, endAngle);
        if (hot)
        {
            target.FillEllipse(
                new Ellipse(head, 10f, 10f),
                GetBrush(target, new Color4(HotPurple.R, HotPurple.G, HotPurple.B, 0.18f)));
        }

        target.FillEllipse(new Ellipse(head, 4.5f, 4.5f), GetBrush(target, color));

        // 红区起点标记（原型固定 2.5px 红点，不受红区是否超过影响）
        Vector2 redPoint = PointOn(RadialR, StartAngleDeg + (SweepDeg * redlineRatio));
        target.FillEllipse(new Ellipse(redPoint, 2.5f, 2.5f), GetBrush(target, RedlineRed));
    }

    /// <summary>
    /// 刻度：每个数字间隔打两个刻度（一个长一个短），与原型 <c>ticks(n=18, majorEvery=2)</c> 同构。
    /// 量程变化时刻度数与数字个数同步缩放，保证「大刻度永远正对数字」。
    /// </summary>
    private void DrawTicks(ID2D1RenderTarget target, float spanRpm, float redlineRatio)
    {
        int numberIntervals = Math.Max(1, (int)Math.Round(spanRpm / 1000f));
        int tickCount = numberIntervals * 2;

        ID2D1SolidColorBrush minorBrush = GetBrush(target, TickMinorColor);
        ID2D1SolidColorBrush majorBrush = GetBrush(target, TickMajorColor);
        ID2D1SolidColorBrush redBrush = GetBrush(target, RedlineRed);

        for (int i = 0; i <= tickCount; i++)
        {
            float fraction = (float)i / tickCount;
            bool major = i % 2 == 0;
            float angle = StartAngleDeg + (SweepDeg * fraction);
            float length = major ? TickLenMajor : TickLenMinor;

            Vector2 from = PointOn(TickR, angle);
            Vector2 to = PointOn(TickR - length, angle);

            ID2D1SolidColorBrush brush = fraction >= redlineRatio
                ? redBrush
                : major ? majorBrush : minorBrush;

            target.DrawLine(from, to, brush, major ? 2.4f : 1.1f, FlatStroke(target));
        }
    }

    private void DrawNumbers(ID2D1RenderTarget target, float spanRpm, float redlineRatio)
    {
        int top = Math.Max(1, (int)Math.Round(spanRpm / 1000f));
        ID2D1SolidColorBrush normal = GetBrush(target, TickNumColor);
        ID2D1SolidColorBrush red = GetBrush(target, RedlineRed);

        for (int i = 0; i <= top; i++)
        {
            float fraction = (float)i / top;
            float angle = StartAngleDeg + (SweepDeg * fraction);
            Vector2 p = PointOn(NumR, angle);
            ID2D1SolidColorBrush brush = fraction >= redlineRatio ? red : normal;

            HudDraw.CenteredText(
                target,
                i.ToString(CultureInfo.InvariantCulture),
                _tickNumFormat,
                brush,
                p.X,
                p.Y,
                24f,
                18f);
        }
    }

    private void DrawStaticLabels(ID2D1RenderTarget target)
    {
        ID2D1SolidColorBrush faint = GetBrush(target, new Color4(1f, 1f, 1f, 0.40f));
        ID2D1SolidColorBrush softer = GetBrush(target, new Color4(1f, 1f, 1f, 0.45f));

        HudDraw.CenteredText(target, "×1000 r/min", _rpmLabelFormat, faint, Cx, Cy + RpmLabelDy, 120f, 14f);
        HudDraw.CenteredText(target, "GEAR", _gearLabelFormat, softer, Cx, Cy + GearLabelDy, 80f, 16f);
    }

    /// <summary>
    /// 增压弧：以 0 bar（量程 25% 处）为零位，负压向左画灰、正压向右画紫。
    /// 照原型从零位向两侧生长，而不是「从最左端画到当前值」—— 那样读不出负压到底多少。
    /// </summary>
    private void DrawBoostArc(ID2D1RenderTarget target, float boostValue)
    {
        // 底轨
        DrawArc(target, BoostR, BoostStartDeg, BoostEndDeg, TrackColor, BoostWidth, RoundStroke(target));

        float zeroAngle = BoostStartDeg + ((BoostEndDeg - BoostStartDeg) * BoostZeroFraction);
        Vector2 zeroPoint = PointOn(BoostR, zeroAngle);
        target.FillEllipse(new Ellipse(zeroPoint, 2.2f, 2.2f), GetBrush(target, new Color4(1f, 1f, 1f, 0.60f)));

        float fraction = Math.Clamp((boostValue - BoostMin) / BoostSpan, 0f, 1f);
        float valueAngle = BoostStartDeg + ((BoostEndDeg - BoostStartDeg) * fraction);

        if (boostValue < 0f)
        {
            DrawArc(target, BoostR, valueAngle, zeroAngle, BoostVacuumColor, BoostWidth, RoundStroke(target));
        }
        else if (boostValue > 0.02f)
        {
            DrawArc(target, BoostR, zeroAngle, valueAngle, BoostPositiveColor, BoostWidth, RoundStroke(target));
        }
    }

    private void DrawGearAndSpeed(ID2D1RenderTarget target, in ForzaDataPacket packet, bool hasPacket)
    {
        string gearText = hasPacket ? FormatGear(packet.GearNumber) : "N";
        string speedText = hasPacket ? HudDraw.Truncate(Math.Max(packet.SpeedKmh, 0f), 0) : "--";

        // 档位数字：换挡瞬间放大并带紫色辉光（原型 96 * (1 + flash*.12)）。
        // 放大用 Matrix3x2 绕档位中心缩放，不重建文字格式 —— 每帧新建 IDWriteTextFormat
        // 会持续分配，换挡那几帧恰好在热路径上。
        float gearScale = 1f + (_flash * GearFlashGrow);

        if (_flash > 0.05f)
        {
            // 辉光：同字再放大一档、透明度随 flash 衰减，画在正字下面
            Matrix3x2 caller = target.Transform;
            target.Transform = Matrix3x2.CreateScale(gearScale * 1.06f, new Vector2(Cx, Cy + GearNumberDy)) * caller;
            HudDraw.CenteredText(
                target,
                gearText,
                _gearNumberFormat,
                GetBrush(target, new Color4(HotPurple.R, HotPurple.G, HotPurple.B, 0.35f * _flash)),
                Cx,
                Cy + GearNumberDy,
                BaseWidth,
                140f);
            target.Transform = caller;

            DrawShiftArrow(target);
        }

        if (gearScale > 1.001f)
        {
            Matrix3x2 caller = target.Transform;
            target.Transform = Matrix3x2.CreateScale(gearScale, new Vector2(Cx, Cy + GearNumberDy)) * caller;
            HudDraw.CenteredText(target, gearText, _gearNumberFormat, GetBrush(target, White), Cx, Cy + GearNumberDy, BaseWidth, 140f);
            target.Transform = caller;
        }
        else
        {
            HudDraw.CenteredText(target, gearText, _gearNumberFormat, GetBrush(target, White), Cx, Cy + GearNumberDy, BaseWidth, 140f);
        }

        // 速度数字：中心定槽。比例字体直接 CenteredText 会随数值宽度左右抖，
        // 定槽后同一字数的两次读数落点完全一致（用户 2026-11-26 实机发现）。
        HudDraw.FixedDigits(
            target,
            speedText,
            _speedFormat,
            GetBrush(target, White),
            Cx,
            Cy + SpeedDy,
            SpeedSlotWidth,
            SpeedSlotHeight);
        HudDraw.CenteredText(
            target,
            "km/h",
            _kmhFormat,
            GetBrush(target, new Color4(1f, 1f, 1f, 0.50f)),
            Cx,
            Cy + KmhDy,
            80f,
            16f);

        // 增压读数在表盘下方（原型 y=338）。
        // 整行拆三段（BOOST / 定槽数字 / bar）按实测宽度拼起来再整组居中：
        // 数字段内部逐格落位，所以数值变化时只有数字本身在换，
        // BOOST 与 bar 不会跟着左右挪（用户 2026-11-26 要求「不跳动」）。
        float boostValue = hasPacket ? Math.Clamp(packet.BoostBar, BoostMin, BoostMax) : 0f;
        string boostDigits = HudDraw.Signed(boostValue, BoostDecimals);
        ID2D1SolidColorBrush boostBrush = GetBrush(
            target,
            boostValue < 0f ? BoostVacuumText : BoostPositiveText);

        float digitBlockWidth = 0f;
        foreach (float cell in _boostCells)
        {
            digitBlockWidth += cell;
        }

        float rowWidth = _boostPrefixWidth + _boostGapWidth + digitBlockWidth + _boostGapWidth + _boostSuffixWidth;
        float rowLeft = Cx - rowWidth / 2f;
        float digitCenterX = rowLeft + _boostPrefixWidth + _boostGapWidth + digitBlockWidth / 2f;

        HudDraw.CenteredText(
            target,
            "BOOST",
            _boostTextFormat,
            boostBrush,
            rowLeft + _boostPrefixWidth / 2f,
            BoostTextY,
            _boostPrefixWidth,
            18f);

        HudDraw.FixedCells(
            target,
            boostDigits,
            _boostTextFormat,
            boostBrush,
            digitCenterX,
            BoostTextY,
            _boostCells,
            18f);

        HudDraw.CenteredText(
            target,
            "bar",
            _boostTextFormat,
            boostBrush,
            rowLeft + rowWidth - (_boostSuffixWidth / 2f),
            BoostTextY,
            _boostSuffixWidth,
            18f);
    }

    /// <summary>换挡方向箭头：升挡朝上、降挡朝下，位置在档位数字右侧（原型 cx+52）。</summary>
    private void DrawShiftArrow(ID2D1RenderTarget target)
    {
        bool up = _shiftDirection > 0;
        float ax = Cx + 52f;
        float ay = Cy + GearNumberDy;
        Color4 color = up ? ShiftUpColor : ShiftDownColor;

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            if (up)
            {
                sink.BeginFigure(new Vector2(ax, ay - 8f), FigureBegin.Filled);
                sink.AddLine(new Vector2(ax - 6f, ay + 4f));
                sink.AddLine(new Vector2(ax + 6f, ay + 4f));
            }
            else
            {
                sink.BeginFigure(new Vector2(ax, ay + 8f), FigureBegin.Filled);
                sink.AddLine(new Vector2(ax - 6f, ay - 4f));
                sink.AddLine(new Vector2(ax + 6f, ay - 4f));
            }

            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        target.FillGeometry(geometry, GetBrush(target, new Color4(color.R, color.G, color.B, _flash)));
        geometry.Dispose();
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

    /// <summary>极坐标 → 直角坐标。0° 正右、顺时针（y 向下），与咬地鲨 / ES 同系。</summary>
    private static Vector2 PointOn(float radius, float angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        return new Vector2(
            Cx + (float)(radius * Math.Cos(radians)),
            Cy + (float)(radius * Math.Sin(radians)));
    }

    /// <summary>
    /// 画一段圆弧。起止角都是度，按 <see cref="PointOn"/> 的角度制；
    /// 起止大小决定顺/逆时针，跨角大于 180° 自动用 Large 弧。
    /// </summary>
    private void DrawArc(
        ID2D1RenderTarget target,
        float radius,
        float fromDeg,
        float toDeg,
        Color4 color,
        float width,
        ID2D1StrokeStyle? style)
    {
        float delta = toDeg - fromDeg;
        if (Math.Abs(delta) < 0.05f)
        {
            return;
        }

        Vector2 start = PointOn(radius, fromDeg);
        Vector2 end = PointOn(radius, toDeg);

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(start, FigureBegin.Hollow);
            sink.AddArc(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                RotationAngle = 0f,
                SweepDirection = delta > 0f ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                ArcSize = Math.Abs(delta) > 180f ? ArcSize.Large : ArcSize.Small,
            });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        target.DrawGeometry(geometry, GetBrush(target, color), width, style);
        geometry.Dispose();
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

    // ⚠️ 这两个 StrokeStyle 由 Factory 创建后长期缓存，不能包 using —— Dispose 掉下一帧就崩。
    private ID2D1StrokeStyle RoundStroke(ID2D1RenderTarget target)
    {
        _roundStroke ??= target.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
        });
        return _roundStroke;
    }

    private ID2D1StrokeStyle FlatStroke(ID2D1RenderTarget target)
    {
        _flatStroke ??= target.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
        });
        return _flatStroke;
    }

    public void Dispose()
    {
        foreach (ID2D1SolidColorBrush brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();

        _tickNumFormat?.Dispose();
        _rpmLabelFormat?.Dispose();
        _gearLabelFormat?.Dispose();
        _gearNumberFormat?.Dispose();
        _speedFormat?.Dispose();
        _kmhFormat?.Dispose();
        _boostTextFormat?.Dispose();

        _roundStroke?.Dispose();
        _flatStroke?.Dispose();
    }
}
