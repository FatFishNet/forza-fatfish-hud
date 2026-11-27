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
/// 漂移锁链（FH5 风格）单表渲染器：渐变转速弧 + 红区斜纹 + 中央档位/车速 + 底部增压弧。
/// 几何来自原型 <c>temp/漂移锁链.html</c>（同批 6 款里的第 5 款）。
/// <para>
/// 原型画布 380×380、圆心 (190,184)、R=140。增压读数已上移 20px，图形包围盒 (45,41)–(338,319) = 294×279，
/// 按项目惯例四边各加 5px 后对外基准取 <b>304×289</b>，
/// 内部坐标仍是原型原值，靠 <see cref="ShiftX"/> / <see cref="ShiftY"/> 平移吸收。
/// </para>
/// <para>
/// 角度制跟咬地鲨 / ES / 极简弧环同系（SVG 制）：<c>0° = 正右</c>、y 轴向下、<see cref="PointOn"/> 用 <c>cos/sin</c>。
/// 不要和仿NFS19 / SHIFT TREE 的「0° 正上、sin/-cos」混用 —— 混了弧整体偏，编译却零错误。
/// </para>
/// <para>
/// 配色是 <see cref="Ui.RpmColorScale"/> 的**第五个例外**（前四个：SHIFT TREE、咬地鲨、ES、极简弧环）：
/// 原型规定转速弧从紫 <c>hsl(268)</c> 线性过渡到青 <c>hsl(185)</c>，红区画斜纹，不走黄→红三段。
/// </para>
/// </summary>
internal sealed class DriftChainScene : IDisposable
{
    // ── 基准与坐标 ──

    /// <summary>对外基准宽：原型包围盒 294 + 左右各 5px 安全边距。</summary>
    internal const float BaseWidth = 304f;

    /// <summary>
    /// 对外基准高：增压读数上移 20px 后图形变矮，包围盒降到 279，
    /// 加上下各 5px 后取 <b>289</b>（原 315，2026-11-26 用户拍板收紧）。
    /// </summary>
    internal const float BaseHeight = 289f;

    /// <summary>
    /// 内容平移量。x 沿用原型包围盒左沿 45 减 5px 安全边距（= 40）；
    /// y 因增压读数上移 20px、包围盒变为 41..319，减 5px 后是 <c>-36</c>（原 -35）。
    /// </summary>
    private const float ShiftX = -40f;
    private const float ShiftY = -36f;

    /// <summary>原型圆心 (190,184) 平移到基准坐标后落在 (150,148)。</summary>
    private const float Cx = 190f + ShiftX;
    private const float Cy = 184f + ShiftY;

    // ── 弧与刻度（原型值，1:1 不缩放）──

    /// <summary>转速主弧半径（原型 R=140）。比极简弧环的 148 小 8。</summary>
    private const float RadialR = 140f;

    /// <summary>主弧线宽（原型 9）。比极简弧环的 6 明显更粗。</summary>
    private const float RadialWidth = 9f;

    /// <summary>刻度起点半径（原型 128 = R-12）。</summary>
    private const float TickR = 128f;

    private const float TickLenMajor = 9f;
    private const float TickLenMinor = 4f;

    /// <summary>数字圈半径（原型 106）。</summary>
    private const float NumR = 106f;

    /// <summary>转速弧的渐变分段数（原型 N=48）。分得越细过渡越顺，但每段都是独立弧。</summary>
    private const int GradientSegments = 48;

    /// <summary>红区斜纹的角度步长（原型 0.035 弧度）。</summary>
    private const float HatchStepRad = 0.035f;

    /// <summary>红区斜纹伸出主弧两端的长度（原型 R±9）。</summary>
    private const float HatchExtend = 9f;

    /// <summary>表盘起始角：135°（左下），与原型 <c>A0 = Math.PI * 0.75</c> 一致。</summary>
    private const float StartAngleDeg = 135f;

    /// <summary>扫掠角 270°，与原型 <c>SW = Math.PI * 1.5</c> 一致。</summary>
    private const float SweepDeg = 270f;

    // ── 增压弧（原型 b0/b1 = .33π/.67π，半径 108，比极简弧环的 128 小 20）──

    private const float BoostR = 108f;
    private const float BoostWidth = 4f;
    private const float BoostStartDeg = 59.4f;
    private const float BoostEndDeg = 120.6f;

    /// <summary>量程下限：进气歧管真空（负值是真实物理量，不是脏数据）。</summary>
    private const float BoostMin = -1.0f;

    /// <summary>量程上限。</summary>
    private const float BoostMax = 3.0f;

    private const float BoostSpan = BoostMax - BoostMin;

    /// <summary>0 bar 在量程上的位置 = 0.25，真空段只占 1/4。</summary>
    private const float BoostZeroFraction = (0f - BoostMin) / BoostSpan;

    // ── 文字位置（相对圆心的原型偏移，注意与极简弧环不同）──

    private const float RpmLabelDy = -76f;
    private const float GearNumberDy = -2f;
    private const float SpeedDy = 62f;
    private const float KmhDy = 88f;

    /// <summary>
    /// 增压读数基线。原型在 y=336，**2026-11-26 用户要求整体上移 20px**（→ 316），
    /// 让 BOOST 那行贴近表盘下沿、不至于贴到画布底边。
    /// </summary>
    private const float BoostTextY = 316f + ShiftY;

    // ── 字号（原型值）──

    private const float TickNumSize = 12f;
    private const float RpmLabelSize = 9f;
    private const float GearNumberSize = 84f;
    private const float SpeedSize = 44f;
    private const float KmhSize = 11f;
    private const float BoostTextSize = 12f;

    /// <summary>
    /// 速度数字的定槽宽度。**必须定槽** —— Rajdhani 是比例字体，
    /// 直接居中绘制时不同数字占宽不同，整行会随读数持续左右抖（用户 2026-11-26 实机提过）。
    /// <para>
    /// 44px 字号比极简弧环的 46 小一档，实测单字进位按比例缩放：23 × 44/46 ≈ 22。
    /// </para>
    /// </summary>
    private const float SpeedSlotWidth = 22f;

    /// <summary>速度行高度：定槽绘制时给足行高，避免「8」的下缘被裁。</summary>
    private const float SpeedSlotHeight = 58f;

    /// <summary>换挡闪光时档位数字的放大倍率（原型 <c>1 + flash * .1</c>，比极简弧环的 .12 略小）。</summary>
    private const float GearFlashGrow = 0.10f;

    /// <summary>闪光衰减速度（原型 <c>flash -= dt * 3.2</c>，约 0.31 秒衰减完）。</summary>
    private const float FlashDecayPerSecond = 3.2f;

    // ── 配色 ──

    private static readonly Color4 White = new(1f, 1f, 1f, 1f);

    /// <summary>转速弧起点色（原型 <c>hsl(268,90%,64%)</c> = 紫）。</summary>
    private static readonly Color4 GradientStart = Hsl(268f, 0.90f, 0.64f);

    /// <summary>转速弧终点色（原型 <c>hsl(185,90%,64%)</c> = 青）。</summary>
    private static readonly Color4 GradientEnd = Hsl(185f, 0.90f, 0.64f);

    /// <summary>红区斜纹（原型 <c>rgba(255,80,120,.55)</c>）。</summary>
    private static readonly Color4 HatchRed = new(0xFF / 255f, 80 / 255f, 120 / 255f, 0.55f);

    /// <summary>
    /// 红区**刻度线**的红（原型 <c>ticks()</c> 里写死 <c>#ff3b5c</c>）。
    /// 注意与 <see cref="RedlineRed"/> 不是同一个色值 —— 原型的刻度与数字用了两种红。
    /// </summary>
    private static readonly Color4 TickRedlineBase = new(0xFF / 255f, 0x3B / 255f, 0x5C / 255f, 1f);

    /// <summary>红区**刻度数字**的红（原型 <c>#ff5078</c>），与刻度的红不同。</summary>
    private static readonly Color4 RedlineRed = new(0xFF / 255f, 0x50 / 255f, 0x78 / 255f, 1f);

    private static readonly Color4 TrackColor = new(1f, 1f, 1f, 0.08f);

    /// <summary>
    /// 刻度颜色。原型 <c>ticks()</c> 里颜色之外还乘一层全局透明度（大 0.95 / 小 0.40），
    /// 所以实际不透明度是 <c>0.75×0.95</c> 与 <c>0.50×0.40</c> —— 这里直接乘好写死，
    /// 否则刻度会明显比原型亮一档（第一版就踩了这个，出图比对才发现）。
    /// </summary>
    private static readonly Color4 TickMinorColor = new(1f, 1f, 1f, 0.50f * 0.40f);
    private static readonly Color4 TickMajorColor = new(1f, 1f, 1f, 0.75f * 0.95f);
    private static readonly Color4 TickNumColor = new(1f, 1f, 1f, 0.60f);

    /// <summary>增压真空段（负压）灰阶。</summary>
    private static readonly Color4 BoostVacuumColor = new(0x8A / 255f, 0x93 / 255f, 0xA8 / 255f, 1f);

    /// <summary>增压正压段青。</summary>
    private static readonly Color4 BoostPositiveColor = new(0x4D / 255f, 0xE3 / 255f, 0xFF / 255f, 1f);

    /// <summary>负压时的读数文字颜色（原型 <c>#9aa0b5</c>）。</summary>
    private static readonly Color4 BoostVacuumText = new(0x9A / 255f, 0xA0 / 255f, 0xB5 / 255f, 0.85f);

    /// <summary>正压时的读数文字颜色（原型 <c>#7df9ff</c>）。</summary>
    private static readonly Color4 BoostPositiveText = new(0x7D / 255f, 0xF9 / 255f, 0xFF / 255f, 0.85f);

    /// <summary>换挡辉光青（原型 <c>#4de3ff</c>）。</summary>
    private static readonly Color4 ShiftGlow = new(0x4D / 255f, 0xE3 / 255f, 0xFF / 255f, 1f);

    /// <summary>升挡箭头用渐变起点紫，与弧色系呼应。</summary>
    private static readonly Color4 ShiftUpColor = GradientStart;

    /// <summary>降挡箭头灰。</summary>
    private static readonly Color4 ShiftDownColor = new(0x8B / 255f, 0x90 / 255f, 0xA5 / 255f, 1f);

    private readonly IDWriteFactory? _dwrite;

    /// <summary>内置字体集合（含 Rajdhani）；为 null 时回退系统字体栈。</summary>
    private readonly IDWriteFontCollection? _fonts;

    /// <summary>解析出的 Rajdhani 家族名，构造时算一次，避免每帧重找。</summary>
    private readonly string _family;

    private IDWriteTextFormat? _tickNumFormat;
    private IDWriteTextFormat? _rpmLabelFormat;
    private IDWriteTextFormat? _gearNumberFormat;
    private IDWriteTextFormat? _speedFormat;
    private IDWriteTextFormat? _kmhFormat;
    private IDWriteTextFormat? _boostTextFormat;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];

    /// <summary>
    /// 渐变弧**完整段**的几何缓存（48 个槽位，未用到的保持 null）。
    /// <para>
    /// 每段的起止角是常量（见 <see cref="DrawValueArc"/>），只有跨越当前转速的
    /// 那一段才每帧现建。缓存之前每帧建 48 个原生 PathGeometry，
    /// 2 小时累计约 2300 万个，实测把工作集抬高 73.6MB（2026-11-27）。
    /// </para>
    /// </summary>
    private readonly ID2D1PathGeometry?[] _segmentGeometries = new ID2D1PathGeometry?[GradientSegments];

    /// <summary>缓存所属的 D2D 工厂（记底层 COM 指针，不用包装对象 —— 见 GetSegmentGeometry）。</summary>
    private IntPtr _segmentFactoryPointer;

    private ID2D1StrokeStyle? _roundStroke;
    private ID2D1StrokeStyle? _flatStroke;

    /// <summary>换挡闪光强度 0~1，随帧衰减。驱动档位放大 + 辉光 + 方向箭头。</summary>
    private float _flash;

    /// <summary>本次闪光是升挡（+1）还是降挡（-1）。</summary>
    private int _shiftDirection = 1;

    /// <summary>上一帧档位，用于判断换挡；<c>int.MinValue</c> 表示还没有效样本。</summary>
    private int _lastGear = int.MinValue;

    private string _stableGearText = "N";

    /// <summary>
    /// 增压数字的字符格宽度（符号 / 数字 / 小数点 / 两位数字），构造期按字体实测填。
    /// 与极简弧环同一套逻辑：总宽恒定，数值怎么变都不会重新居中。
    /// </summary>
    private float[] _boostCells = [];

    /// <summary>「BOOST」「bar」两段与中间空格的实测宽度，构造期算一次。</summary>
    private float _boostPrefixWidth;
    private float _boostSuffixWidth;
    private float _boostGapWidth;

    internal DriftChainScene(IDWriteFactory? dwrite, IDWriteFontCollection? customFonts)
    {
        _dwrite = dwrite;
        _fonts = customFonts;
        _family = FindFamilyName(customFonts) ?? "Rajdhani";

        _tickNumFormat = CreateFormat(TickNumSize, FontWeight.Bold);
        _rpmLabelFormat = CreateFormat(RpmLabelSize, FontWeight.SemiBold);
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

        // 原型字体栈是 Rajdhani → Noto Sans SC → sans-serif。项目内置 Rajdhani（OFL）。
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
    /// 与极简弧环同一套做法：数字格取 0~9 里最宽的、符号格取 +/- 里较宽的、小数点单独一格。
    /// </summary>
    private void MeasureBoostRow()
    {
        _boostPrefixWidth = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, "BOOST");
        _boostSuffixWidth = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, "bar");

        // 空格宽度量不到（某些字体的 space 无墨迹、度量返回 0）时按字号兜底，
        // 否则 BOOST 与数字会贴在一起。
        float gap = HudDraw.MeasureAdvance(_dwrite, _boostTextFormat, " ");
        _boostGapWidth = gap > 0.5f ? gap : BoostTextSize * 0.34f;

        _boostCells = HudDraw.BuildSignedCells(_dwrite, _boostTextFormat, HudDraw.BoostDecimals, BoostTextSize);
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
        float redlineRatio = ResolveRedlineRatio(learner, packet, hasPacket, maxRpm, spanRpm, useLearnedRedline);

        float rpmRatio = hasPacket ? Math.Clamp(packet.CurrentEngineRpm / spanRpm, 0f, 1f) : 0f;
        float boostValue = hasPacket ? Math.Clamp(packet.BoostBar, BoostMin, BoostMax) : 0f;

        AdvanceFlash(hasPacket ? packet.GearNumber : int.MinValue, deltaSeconds);

        Matrix3x2 caller = target.Transform;
        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        DrawTrack(target);
        DrawValueArc(target, rpmRatio);
        DrawRedlineHatch(target, redlineRatio);
        DrawTicks(target, spanRpm, redlineRatio);
        DrawNumbers(target, spanRpm, redlineRatio);
        DrawStaticLabels(target);
        DrawBoostArc(target, boostValue);
        DrawGearAndSpeed(target, packet, hasPacket);
        DrawBoostText(target, boostValue, hasPacket);

        target.Transform = caller;
    }

    /// <summary>
    /// 转速量程：按千转向上取整（7500→8 千转），夹在 4~16 之间。
    /// 原型写死 9000，但接遥测必须自适应 —— 否则 8 千转的车刻度只走到 88%，
    /// 而 16 千转的车会爆表。与 ES / 咬地鲨 / 极简弧环各自维护一份。
    /// </summary>
    private static float ResolveTachoSpan(float maxRpm)
    {
        int thousands = (int)Math.Ceiling(Math.Max(maxRpm, 1000f) / 1000f);
        thousands = Math.Clamp(thousands, 4, 16);
        return thousands * 1000f;
    }

    /// <summary>
    /// 红区起点比例（**占表盘量程 spanRpm**，可直接当弧比例用）。
    /// <para>
    /// learner 返回的是「占 maxRpm」的比例，必须先乘 maxRpm 换成真实转速，再除以表盘量程 spanRpm。
    /// 曾直接把 learner 的返回值当弧比例 —— 两个分母不同（maxRpm 通常是整千，spanRpm 是
    /// 千转向上取整，如 9500 → 10000），红区会被抬高 (span−max)/max ≈ 5%。
    /// 实测 9500 转车：learner 给 0.86（=8170 转），表盘却画在 86%（=8600 转），
    /// 表现为「红区起点偏右、红区带比咬地鲨窄」。
    /// 2026-11-28 用户实机对比发现，与 ES / 极简弧环 同一个 bug，只有咬地鲨写对了。
    /// </para>
    /// </summary>
    private static float ResolveRedlineRatio(
        ShiftPointLearner learner,
        in ForzaDataPacket packet,
        bool hasPacket,
        float maxRpm,
        float spanRpm,
        bool useLearnedRedline)
    {
        if (!hasPacket || !useLearnedRedline || spanRpm <= 1f)
        {
            return 0.75f;
        }

        double ratio = Math.Clamp(learner.GetRedlineRatio(packet), 0.5, 1.0);
        return Math.Clamp((float)ratio * maxRpm, 0f, spanRpm) / spanRpm;
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

        if (_flash > 0f)
        {
            _flash = Math.Max(0f, _flash - ((float)deltaSeconds * FlashDecayPerSecond));
        }
    }

    /// <summary>底轨：整条 270° 的暗色圆环，压在渐变弧下面。</summary>
    private void DrawTrack(ID2D1RenderTarget target)
    {
        DrawArc(target, RadialR, StartAngleDeg, StartAngleDeg + SweepDeg, TrackColor, RadialWidth, RoundStroke(target));
    }

    /// <summary>
    /// 值弧：原型把整条弧切成 48 段，每段独立取色，色相从 268（紫）线性过渡到 185（青）。
    /// <para>
    /// **不能用渐变画刷** —— 原型是沿弧长的分段线性插值，而线性渐变画刷是沿一条直线插值，
    /// 画在圆弧上会出现「同角度不同色」的偏移。逐段画是唯一能 1:1 还原的做法。
    /// </para>
    /// <para>
    /// 每段末端 +0.006 弧度是原型原值，用来盖住相邻段之间的抗锯齿缝隙，否则弧上会出现细黑纹。
    /// </para>
    /// </summary>
    private void DrawValueArc(ID2D1RenderTarget target, float rpmRatio)
    {
        ID2D1StrokeStyle stroke = FlatStroke(target);

        for (int i = 0; i < GradientSegments; i++)
        {
            float from = (float)i / GradientSegments;
            if (from > rpmRatio)
            {
                break;
            }

            float to = Math.Min((float)(i + 1) / GradientSegments, rpmRatio);

            // 色相插值位置按**整段中点**取，这样每一段的颜色是它自己的代表色，
            // 整条弧看起来才是均匀的紫→青过渡（按起点取会让尾段偏紫）。
            float hueAt = (from + to) * 0.5f;
            Color4 color = LerpHsl(GradientStart, GradientEnd, hueAt);

            // 完整段（to 没有被子句截断）的起止角是常量，几何可以缓存复用；
            // 只有跨过 rpmRatio 的那 1 段每帧现建 —— 它是唯一随转速变化的段。
            // 全部现建的话每帧要建 48 个原生 PathGeometry，2 小时就是两千多万个，
            // 会把 D2D 分配器拖到持续抬高工作集（2026-11-27 实测首轮 +73.6MB）。
            bool complete = to >= (float)(i + 1) / GradientSegments;
            ID2D1PathGeometry geometry = complete
                ? GetSegmentGeometry(target, i)
                : BuildArcGeometry(
                    target.Factory,
                    RadialR,
                    StartAngleDeg + (SweepDeg * from),
                    StartAngleDeg + (SweepDeg * to) + RadToDeg(0.006f));

            target.DrawGeometry(geometry, GetBrush(target, color), RadialWidth, stroke);

            // 缓存的那段留着；现建的用完必须还掉，否则原生对象泄漏。
            if (!complete)
            {
                geometry.Dispose();
            }
        }
    }

    /// <summary>红区斜纹：从红区起点到表盘末端，每 0.035 弧度画一根短横线，跨在主弧两侧。</summary>
    private void DrawRedlineHatch(ID2D1RenderTarget target, float redlineRatio)
    {
        ID2D1SolidColorBrush brush = GetBrush(target, HatchRed);
        float from = StartAngleDeg + (SweepDeg * redlineRatio);
        float to = StartAngleDeg + SweepDeg;

        for (float a = from; a < to; a += RadToDeg(HatchStepRad))
        {
            Vector2 inner = PointOn(RadialR - HatchExtend, a);
            Vector2 outer = PointOn(RadialR + HatchExtend, a);
            target.DrawLine(inner, outer, brush, 2f, FlatStroke(target));
        }
    }

    /// <summary>
    /// 刻度：每个数字间隔打两个刻度（一个长一个短）；红区内的刻度染色。
    /// <para>
    /// **刻度数必须跟着量程走，不能写死 18**。原型写死 <c>ticks(n=18)</c> 是因为它把量程
    /// 也写死成 9000（18 间隔 = 9 个数字，正好 2 倍）。本项目量程要自适应，
    /// 写死 18 就会出现「8 千转的车刻度比数字多一格、逐级错开」——
    /// 2026-11-26 用户实机截图发现（4/5/6 那几格歪）。
    /// 与 <see cref="RingScene"/> 的修法一致：<c>tickCount = 数字间隔数 × 2</c>，
    /// 保证大刻度（i % 2 == 0）恒落在 <c>k / top</c> 上，与数字同角。
    /// </para>
    /// </summary>
    private void DrawTicks(ID2D1RenderTarget target, float spanRpm, float redlineRatio)
    {
        ID2D1SolidColorBrush minorBrush = GetBrush(target, TickMinorColor);
        ID2D1SolidColorBrush majorBrush = GetBrush(target, TickMajorColor);

        // 红区刻度同样要乘 0.95 / 0.40 那层透明度，随大/小刻度分别取。
        ID2D1SolidColorBrush redMajor = GetBrush(target, WithAlpha(TickRedlineBase, 0.95f));
        ID2D1SolidColorBrush redMinor = GetBrush(target, WithAlpha(TickRedlineBase, 0.40f));

        // 数字间隔数 = 千转数，与 DrawNumbers 里的 top 同源，两处必须用同一个算式。
        int numberIntervals = Math.Max(1, (int)Math.Round(spanRpm / 1000f));
        int steps = numberIntervals * 2;
        const int majorEvery = 2;

        for (int i = 0; i <= steps; i++)
        {
            float fraction = (float)i / steps;
            bool major = i % majorEvery == 0;
            float angle = StartAngleDeg + (SweepDeg * fraction);
            float length = major ? TickLenMajor : TickLenMinor;

            Vector2 from = PointOn(TickR, angle);
            Vector2 to = PointOn(TickR - length, angle);

            ID2D1SolidColorBrush brush = fraction >= redlineRatio
                ? (major ? redMajor : redMinor)
                : major ? majorBrush : minorBrush;

            target.DrawLine(from, to, brush, major ? 2.4f : 1.1f, FlatStroke(target));
        }
    }

    /// <summary>刻度数字 0..千转数，红区起点的数字染色。</summary>
    private void DrawNumbers(ID2D1RenderTarget target, float spanRpm, float redlineRatio)
    {
        int top = Math.Max(1, (int)Math.Round(spanRpm / 1000f));
        ID2D1SolidColorBrush normal = GetBrush(target, TickNumColor);

        // 原型对所有数字统一 `alpha: .6`，红区数字也一样 —— 只是换了色相，不是不透明。
        ID2D1SolidColorBrush red = GetBrush(target, WithAlpha(RedlineRed, 0.60f));

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

    /// <summary>
    /// 静态标签。**本款没有 GEAR 字样** —— 原型只有「×1000 r/min」，
    /// 档位数字直接压在圆心上方，与极简弧环（有 GEAR 标签）不同，不要照抄过去。
    /// </summary>
    private void DrawStaticLabels(ID2D1RenderTarget target)
    {
        ID2D1SolidColorBrush faint = GetBrush(target, new Color4(1f, 1f, 1f, 0.40f));
        HudDraw.CenteredText(target, "×1000 r/min", _rpmLabelFormat, faint, Cx, Cy + RpmLabelDy, 120f, 14f);
    }

    /// <summary>
    /// 增压弧：以 0 bar（量程 25% 处）为零位，负压向左画灰、正压向右画青。
    /// 半径 108、角度 59.4°~120.6°，都比极简弧环（128 / 57.6°~122.4°）窄一圈。
    /// </summary>
    private void DrawBoostArc(ID2D1RenderTarget target, float boostValue)
    {
        DrawArc(target, BoostR, BoostStartDeg, BoostEndDeg, new Color4(1f, 1f, 1f, 0.10f), BoostWidth, RoundStroke(target));

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

    /// <summary>中央档位（带换挡放大/辉光/方向箭头）与车速（定槽）。</summary>
    private void DrawGearAndSpeed(ID2D1RenderTarget target, in ForzaDataPacket packet, bool hasPacket)
    {
        string gearText = hasPacket ? FormatGear(packet.GearNumber) : "N";
        float gearScale = 1f + (_flash * GearFlashGrow);

        // 放大用 Matrix3x2 绕档位中心缩放，不重建文字格式 —— 每帧新建 IDWriteTextFormat
        // 会持续分配，换挡那几帧恰好在热路径上（与极简弧环同一套做法）。
        if (_flash > 0.05f)
        {
            // 辉光：同字再放大一档、透明度随 flash 衰减，画在正字下面。
            // D2D 没有 shadowBlur，只能用「更大 + 更低透明度」伪造。
            Matrix3x2 caller = target.Transform;
            target.Transform = Matrix3x2.CreateScale(gearScale * 1.06f, new Vector2(Cx, Cy + GearNumberDy)) * caller;
            HudDraw.CenteredText(
                target,
                gearText,
                _gearNumberFormat,
                GetBrush(target, new Color4(ShiftGlow.R, ShiftGlow.G, ShiftGlow.B, 0.35f * _flash)),
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

        string speedText = hasPacket ? HudDraw.Truncate(Math.Max(packet.SpeedKmh, 0f), 0) : "--";

        // 速度数字：中心定槽。Rajdhani 是比例字体，直接居中会随读数宽度左右抖。
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

    /// <summary>
    /// 增压读数：`BOOST +1.20 bar`，三段（BOOST / 定槽数字 / bar）按实测宽度拼接后整组居中。
    /// 数字段内部逐格落位，所以数值变化时只有数字本身在换，两侧不跟着左右挪。
    /// </summary>
    private void DrawBoostText(ID2D1RenderTarget target, float boostValue, bool hasPacket)
    {
        // 没有数据包时显示 0.00（与极简弧环、GHOST 一致），不显示 "--"。
        string digits = HudDraw.Signed(hasPacket ? boostValue : 0f, HudDraw.BoostDecimals);
        ID2D1SolidColorBrush brush = GetBrush(
            target,
            boostValue < 0f ? BoostVacuumText : BoostPositiveText);

        float digitBlockWidth = 0f;
        foreach (float cell in _boostCells)
        {
            digitBlockWidth += cell;
        }

        float rowWidth = _boostPrefixWidth + _boostGapWidth + digitBlockWidth + _boostGapWidth + _boostSuffixWidth;
        float rowLeft = Cx - (rowWidth / 2f);
        float digitCenterX = rowLeft + _boostPrefixWidth + _boostGapWidth + (digitBlockWidth / 2f);

        HudDraw.CenteredText(
            target,
            "BOOST",
            _boostTextFormat,
            brush,
            rowLeft + (_boostPrefixWidth / 2f),
            BoostTextY,
            _boostPrefixWidth,
            18f);

        HudDraw.FixedCells(
            target,
            digits,
            _boostTextFormat,
            brush,
            digitCenterX,
            BoostTextY,
            _boostCells,
            18f);

        HudDraw.CenteredText(
            target,
            "bar",
            _boostTextFormat,
            brush,
            rowLeft + rowWidth - (_boostSuffixWidth / 2f),
            BoostTextY,
            _boostSuffixWidth,
            18f);
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

    /// <summary>极坐标 → 直角坐标。0° 正右、顺时针（y 向下），与咬地鲨 / ES / 极简弧环同系。</summary>
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
        ID2D1StrokeStyle stroke)
    {
        if (Math.Abs(toDeg - fromDeg) < 0.05f)
        {
            return;
        }

        using ID2D1PathGeometry geometry = BuildArcGeometry(target.Factory, radius, fromDeg, toDeg);
        target.DrawGeometry(geometry, GetBrush(target, color), width, stroke);
    }

    /// <summary>
    /// 只建几何、不画。抽出来是为了让渐变弧的**完整段**能复用缓存几何
    /// （它们的起止角是常量，见 <see cref="DrawValueArc"/>）。
    /// </summary>
    private static ID2D1PathGeometry BuildArcGeometry(
        ID2D1Factory factory,
        float radius,
        float fromDeg,
        float toDeg)
    {
        bool clockwise = toDeg >= fromDeg;
        float span = Math.Abs(toDeg - fromDeg);
        SweepDirection direction = clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;

        Vector2 start = PointOn(radius, fromDeg);
        Vector2 end = PointOn(radius, toDeg);

        ID2D1PathGeometry geometry = factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(start, FigureBegin.Hollow);
            sink.AddArc(new ArcSegment
            {
                Point = end,
                Size = new Vortice.Mathematics.Size(radius, radius),
                RotationAngle = 0f,
                SweepDirection = direction,
                ArcSize = span > 180f ? ArcSize.Large : ArcSize.Small,
            });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return geometry;
    }

    /// <summary>
    /// 取第 <paramref name="index"/> 段渐变弧的**完整段**几何（角度恒定，可缓存）。
    /// <para>
    /// 几何由 <c>ID2D1Factory</c> 创建，是设备无关对象，可以跨渲染帧复用；
    /// 但工厂一旦换实例（重建 D2D 设备）旧几何就不能再用，所以这里记住工厂的
    /// **底层 COM 指针**，变了就整批作废重建。
    /// </para>
    /// <para>
    /// ⚠️ 必须比 <see cref="IntPtr"/> 而不是包装对象：`target.Factory` 每次访问都
    /// 新建一个 COM 包装实例（实测 `ReferenceEquals` 恒为 false，哈希相同、NativePointer 相同），
    /// 拿包装对象做身份判断会导致缓存每帧被清空重建，等于没缓存
    /// （2026-11-27 实测：改完分配量一点没降，就是这个原因）。
    /// </para>
    /// </summary>
    private ID2D1PathGeometry GetSegmentGeometry(ID2D1RenderTarget target, int index)
    {
        ID2D1Factory factory = target.Factory;
        IntPtr factoryPointer = factory.NativePointer;
        if (_segmentFactoryPointer != factoryPointer)
        {
            DisposeSegmentGeometries();
            _segmentFactoryPointer = factoryPointer;
        }

        ID2D1PathGeometry? cached = _segmentGeometries[index];
        if (cached is null)
        {
            float from = (float)index / GradientSegments;
            float to = (float)(index + 1) / GradientSegments;
            cached = BuildArcGeometry(
                factory,
                RadialR,
                StartAngleDeg + (SweepDeg * from),
                StartAngleDeg + (SweepDeg * to) + RadToDeg(0.006f));
            _segmentGeometries[index] = cached;
        }

        return cached;
    }

    private void DisposeSegmentGeometries()
    {
        for (int i = 0; i < _segmentGeometries.Length; i++)
        {
            _segmentGeometries[i]?.Dispose();
            _segmentGeometries[i] = null;
        }
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

    private ID2D1StrokeStyle RoundStroke(ID2D1RenderTarget target)
    {
        return _roundStroke ??= target.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
    }

    private ID2D1StrokeStyle FlatStroke(ID2D1RenderTarget target)
    {
        return _flatStroke ??= target.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            LineJoin = LineJoin.Miter,
        });
    }

    // ── HSL 工具（原型的 hsl() 色值要按同一套公式还原，不能手填近似 RGB）──

    /// <summary>HSL → RGB，h 为 0~360 度。</summary>
    private static Color4 Hsl(float h, float s, float l)
    {
        float c = (1f - Math.Abs((2f * l) - 1f)) * s;
        float hp = h / 60f;
        float x = c * (1f - Math.Abs((hp % 2f) - 1f));

        (float r, float g, float b) = hp switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x),
        };

        float m = l - (c / 2f);
        return new Color4(r + m, g + m, b + m, 1f);
    }

    /// <summary>两个 HSL 颜色之间按比例插值 —— 用色相插值而不是 RGB 插值，否则中段会发灰。</summary>
    private static Color4 LerpHsl(Color4 from, Color4 to, float t)
    {
        (float h1, float s1, float l1) = ToHsl(from);
        (float h2, float s2, float l2) = ToHsl(to);

        // 色相走最短弧
        float dh = h2 - h1;
        if (dh > 180f)
        {
            dh -= 360f;
        }
        else if (dh < -180f)
        {
            dh += 360f;
        }

        float h = (h1 + (dh * t) + 360f) % 360f;
        return Hsl(h, s1 + ((s2 - s1) * t), l1 + ((l2 - l1) * t));
    }

    /// <summary>RGB → HSL，h 为 0~360 度。</summary>
    private static (float H, float S, float L) ToHsl(Color4 c)
    {
        float r = c.R, g = c.G, b = c.B;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float l = (max + min) / 2f;
        float d = max - min;

        if (d < 1e-6f)
        {
            return (0f, 0f, l);
        }

        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);

        float h;
        if (max == r)
        {
            h = ((g - b) / d) % 6f;
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2f;
        }
        else
        {
            h = ((r - g) / d) + 4f;
        }

        h *= 60f;
        if (h < 0f)
        {
            h += 360f;
        }

        return (h, s, l);
    }

    /// <summary>返回同色但换一个 alpha 的颜色副本（原型在颜色之外还乘一层透明度）。</summary>
    private static Color4 WithAlpha(Color4 color, float alpha)
    {
        return new Color4(color.R, color.G, color.B, alpha);
    }

    private static float RadToDeg(float radians)
    {
        return radians * 180f / (float)Math.PI;
    }

    public void Dispose()
    {
        _tickNumFormat?.Dispose();
        _rpmLabelFormat?.Dispose();
        _gearNumberFormat?.Dispose();
        _speedFormat?.Dispose();
        _kmhFormat?.Dispose();
        _boostTextFormat?.Dispose();
        _roundStroke?.Dispose();
        _flatStroke?.Dispose();

        DisposeSegmentGeometries();
        _segmentFactoryPointer = IntPtr.Zero;

        foreach (ID2D1SolidColorBrush brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();
    }
}
