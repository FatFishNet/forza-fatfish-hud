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
    /// 截断而非四舍五入——用户明确要求保持截断口径，不要改成 Math.Round。
    /// </summary>
    public static string Truncate(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        double truncated = Math.Truncate(value * factor) / factor;
        return truncated.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
