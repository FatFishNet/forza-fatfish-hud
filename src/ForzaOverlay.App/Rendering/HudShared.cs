using System.Globalization;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 画刷缓存：按 RGBA 打包成 uint key 复用 ID2D1SolidColorBrush。
/// 各仪表共用一个实例，避免同一颜色在不同面板里重复创建画刷。
/// </summary>
internal sealed class HudBrushCache : IDisposable
{
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _cache = [];

    public ID2D1SolidColorBrush Get(ID2D1RenderTarget target, Color4 color)
    {
        uint key = ((uint)(color.R * 255f) << 24)
            | ((uint)(color.G * 255f) << 16)
            | ((uint)(color.B * 255f) << 8)
            | (uint)(color.A * 255f);

        if (!_cache.TryGetValue(key, out ID2D1SolidColorBrush? brush))
        {
            brush = target.CreateSolidColorBrush(color);
            _cache[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        foreach (ID2D1SolidColorBrush brush in _cache.Values)
        {
            brush.Dispose();
        }

        _cache.Clear();
    }
}

/// <summary>
/// 文字布局缓存：实时数值每帧都在变，缓存 IDWriteTextLayout 以避免反复排版。
/// 上限 512 条，超出整表清空，防止长时间运行后内存持续上涨。
/// </summary>
internal sealed class HudTextLayoutCache(IDWriteFactory? dwrite) : IDisposable
{
    private readonly Dictionary<(string Text, float Width, float Height), IDWriteTextLayout> _cache = [];

    public void Draw(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float x,
        float y,
        float width,
        float height)
    {
        if (format is null || dwrite is null)
        {
            return;
        }

        (string Text, float Width, float Height) key = (text, width, height);
        if (_cache.Count > 512)
        {
            foreach (IDWriteTextLayout stale in _cache.Values)
            {
                stale.Dispose();
            }

            _cache.Clear();
        }

        if (!_cache.TryGetValue(key, out IDWriteTextLayout? layout))
        {
            layout = dwrite.CreateTextLayout(text, format, width, height);
            _cache[key] = layout;
        }

        target.DrawTextLayout(new System.Numerics.Vector2(x, y), layout, brush);
    }

    public void Dispose()
    {
        foreach (IDWriteTextLayout layout in _cache.Values)
        {
            layout.Dispose();
        }

        _cache.Clear();
    }
}

/// <summary>
/// 档位文本格式化。档位有效值只有 1~10，0/-1 是空档/倒挡，11 是换挡中；
/// 换挡瞬间会短暂读到越界值，这里记住最后一次有效档位（<see cref="_stableGear"/>）顶住，
/// 避免数字在换挡瞬间闪成 "-"。
/// </summary>
internal sealed class GearTextFormatter
{
    private int _stableGear = int.MinValue;

    public string Format(int gear)
    {
        if (gear is < -1 or > 10)
        {
            return _stableGear == int.MinValue
                ? "-"
                : _stableGear < 0
                    ? "R"
                    : _stableGear.ToString(CultureInfo.InvariantCulture);
        }

        _stableGear = gear;
        return gear < 0 ? "R" : gear.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>各仪表共用的文字绘制原语与数值格式化。</summary>
internal static class HudDraw
{
    /// <summary>
    /// 增压读数统一保留两位小数。
    /// <para>
    /// 用户 2026-11-26 定的项目级标准：**凡是显示增压数字的地方都按这个来**，
    /// 包括 <c>0.00</c> 这种整值也要带两位；配合定槽绘制，数值变化时数字不跳动。
    /// 改这里等于改全部面板，不要在单个场景里另写位数。
    /// </para>
    /// </summary>
    public const int BoostDecimals = 2;

    public static void Text(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float x,
        float y,
        float width,
        float height)
    {
        if (format is null)
        {
            return;
        }

        target.DrawText(text, format, new Rect(x, y, width, height), brush);
    }

    public static void CenteredText(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float centerX,
        float centerY,
        float width,
        float height)
    {
        if (format is null)
        {
            return;
        }

        Rect rect = new(centerX - width / 2f, centerY - height / 2f, width, height);
        target.DrawText(text, format, rect, brush);
    }

    /// <summary>逐位定槽绘制：数字按固定槽宽排布，避免比例字体随数值宽度抖动。</summary>
    public static void FixedDigits(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float centerX,
        float centerY,
        float slotWidth,
        float height)
    {
        if (format is null || text.Length == 0)
        {
            return;
        }

        float totalWidth = text.Length * slotWidth;
        float startX = centerX - totalWidth / 2f;

        for (int i = 0; i < text.Length; i++)
        {
            CenteredText(
                target,
                text[i].ToString(),
                format,
                brush,
                startX + i * slotWidth + slotWidth / 2f,
                centerY,
                slotWidth,
                height);
        }
    }

    /// <summary>
    /// 按「字符格」逐位绘制：每个字符占一格，格宽由调用方给定（可不相等）。
    /// <para>
    /// 与 <see cref="FixedDigits"/> 的区别是允许符号格、小数点格与数字格取不同宽度 ——
    /// 换算成等宽后小数点不该和数字一样宽，但**总宽必须恒定**，
    /// 这样数值怎么变整块都不会重新居中（2026-11-26 用户要求增压读数不跳动）。
    /// </para>
    /// </summary>
    public static void FixedCells(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float centerX,
        float centerY,
        ReadOnlySpan<float> cellWidths,
        float height)
    {
        if (format is null || text.Length == 0 || cellWidths.Length != text.Length)
        {
            return;
        }

        float totalWidth = 0f;
        for (int i = 0; i < cellWidths.Length; i++)
        {
            totalWidth += cellWidths[i];
        }

        FixedCellsRightAligned(target, text, format, brush, centerX + (totalWidth / 2f), centerY, cellWidths, height);
    }

    /// <summary>同 <see cref="FixedCells"/>，但以**右边缘**为锚点 —— 用于原本右对齐的数值列。</summary>
    public static void FixedCellsRightAligned(
        ID2D1RenderTarget target,
        string text,
        IDWriteTextFormat? format,
        ID2D1Brush brush,
        float rightEdge,
        float centerY,
        ReadOnlySpan<float> cellWidths,
        float height)
    {
        if (format is null || text.Length == 0 || cellWidths.Length != text.Length)
        {
            return;
        }

        float totalWidth = 0f;
        for (int i = 0; i < cellWidths.Length; i++)
        {
            totalWidth += cellWidths[i];
        }

        float cursor = rightEdge - totalWidth;
        for (int i = 0; i < text.Length; i++)
        {
            float cell = cellWidths[i];
            CenteredText(target, text[i].ToString(), format, brush, cursor + cell / 2f, centerY, cell, height);
            cursor += cell;
        }
    }

    /// <summary>按「符号 + 数字 + 小数点 + N 位数字」的固定格宽建一张表。</summary>
    public static float[] BuildSignedCells(
        IDWriteFactory? dwrite,
        IDWriteTextFormat? format,
        int decimals,
        float fallbackEm)
    {
        float signWidth = Math.Max(
            MeasureAdvance(dwrite, format, "+"),
            MeasureAdvance(dwrite, format, "-"));
        float dotWidth = MeasureAdvance(dwrite, format, ".");
        float widestDigit = 0f;
        for (char digit = '0'; digit <= '9'; digit++)
        {
            widestDigit = Math.Max(widestDigit, MeasureAdvance(dwrite, format, digit.ToString()));
        }

        if (signWidth <= 0f)
        {
            signWidth = fallbackEm * 0.55f;
        }

        if (dotWidth <= 0f)
        {
            dotWidth = fallbackEm * 0.20f;
        }

        if (widestDigit <= 0f)
        {
            widestDigit = fallbackEm * 0.53f;
        }

        float[] cells = new float[decimals + 3];
        cells[0] = signWidth;
        cells[1] = widestDigit;
        cells[2] = dotWidth;
        for (int i = 0; i < decimals; i++)
        {
            cells[3 + i] = widestDigit;
        }

        return cells;
    }

    /// <summary>
    /// 量一段文字在当前格式下的排版宽度。**只在初始化期调用**（构造时算一次存下来）：
    /// 每帧新建 <c>IDWriteTextLayout</c> 会持续分配，与「热路径禁止分配」的约定冲突。
    /// </summary>
    public static float MeasureAdvance(IDWriteFactory? dwrite, IDWriteTextFormat? format, string text)
    {
        if (dwrite is null || format is null || text.Length == 0)
        {
            return 0f;
        }

        using IDWriteTextLayout layout = dwrite.CreateTextLayout(text, format, 4096f, 4096f);
        return layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// 截断而非四舍五入——用户明确要求保持截断口径，不要改成 Math.Round。
    /// </summary>
    public static string Truncate(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        double truncated = Math.Truncate(value * factor) / factor;
        return truncated.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 带正负号的定点小数：**恒为「符号 + 整数 + 小数点 + decimals 位」**，字符数固定。
    /// <para>
    /// 增压读数专用（2026-11-26 用户要求）：定宽后配合定槽绘制，数值怎么变整块都不重新居中。
    /// 整数段只有 1 位是增压量程决定的（-1.0~3.0 bar），换量程需同步改定槽常量。
    /// </para>
    /// <para>
    /// **截断前先消浮点误差**：遥测的 `BoostBar` 是 float，界面输入的 -0.35 实际存成
    /// -0.34999999…，直接截断会显示 -0.34（用户 2026-11-26 出图发现）。
    /// 这里按量级加一个极小的偏移再截断，只抵消 1e-7 级别的二进制表示误差，
    /// 不影响「截断不四舍五入」的口径 —— 真实值 0.3499 仍会显示 0.34。
    /// </para>
    /// <para>
    /// 截断后为 0 时统一取「+」而不是「-」，否则会出现毫无意义的 <c>-0.00</c>。
    /// </para>
    /// </summary>
    public static string Signed(double value, int decimals)
    {
        // 偏移量与量级挂钩：数值越大，float 的有效位越粗，需要的补偿也越大。
        // 1e-6 对增压量级（个位以下）足够，又远小于任何有意义的读数变化。
        double epsilon = Math.Max(Math.Abs(value), 1.0) * 1e-6;

        double oriented = value >= 0 ? value + epsilon : value - epsilon;
        string body = Truncate(Math.Abs(oriented), decimals);
        bool negative = oriented < 0 && body.Any(digit => digit is >= '1' and <= '9');
        return (negative ? "-" : "+") + body;
    }
}
