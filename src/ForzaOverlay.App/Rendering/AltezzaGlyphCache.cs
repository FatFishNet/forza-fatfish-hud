using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 咬地鲨三表：LEXUS 字标轮廓缓存。
/// <para>
/// 字标是从参考图描摹出来的矢量轮廓（<c>temp/panel-cluster-altezza/lexus-wordmark.svg</c>），
/// 不是字体——宽高比 7.10 远超任何系统字体，撑字距也排不出来。
/// 路径含两个闭合轮廓（外轮廓 + 字母镂空），必须用 <see cref="FillMode.Alternate"/> 填充，
/// 否则 E 的内部镂空会被填死。
/// </para>
/// <para>
/// 路径只用 M / L / Z 三种绝对指令，解析器只需支持这三个，不要照搬
/// <see cref="Nfs19GlyphCache"/> 那套完整 SVG 解析。
/// </para>
/// </summary>
internal sealed class AltezzaGlyphCache : IDisposable
{
    /// <summary>字标原始坐标系尺寸（描摹时的像素空间）。</summary>
    internal const float BoxWidth = 1000f;

    internal const float BoxHeight = 140.8f;

    private const string WordmarkPath =
        "M4.5 0L55.4 3.4L59.4 103.5L168 109.2L179.9 98.4L174.2 63.3L184.4 29.4L217.8 2.8" +
        "L422.5 1.7L482.5 44.7L540.2 2.8L627.3 0.6L633.5 98.4L670.2 110.3L724.5 105.8" +
        "L730.8 5.7L781.1 1.7L777.7 122.7L794.7 106.9L944.6 101.8L936.1 89.9L803.7 80.9" +
        "L794.1 69L795.2 40.7L819.6 8.5L851.2 0.6L983.6 0.6L991 7.9L983.6 31.1L860.3 34.5" +
        "L849.5 48.6L971.2 56L1000 84.8L1000 110.9L975.7 134L937.2 139.7L605.8 137.4" +
        "L580.3 102.9L575.2 11.9L509 67.9L588.8 130.7L587.7 138.6L522.1 136.3L471.2 95.6" +
        "L413.5 134L386.3 140.8L31.1 136.3L0 105.2L4.5 0Z" +
        "M370.5 14.1L362.6 33.4L237 37.9L235.9 54.9L352.4 53.7L362 64.5L352.4 83.1L238.1 84.3" +
        "L235.3 105.2L385.2 113.7L442.3 70.1L370.5 14.1Z";

    private static List<Vector2[]>? _figures;

    private ID2D1PathGeometry? _wordmark;

    /// <summary>字标几何（只建一次，之后每帧复用）。</summary>
    internal ID2D1PathGeometry? Wordmark(ID2D1RenderTarget target)
    {
        if (_wordmark is not null)
        {
            return _wordmark;
        }

        _figures ??= ParseFigures(WordmarkPath);

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            // 镂空靠奇偶规则：两条闭合轮廓在同一几何里，Alternate 才能挖空 E 的内部
            sink.SetFillMode(FillMode.Alternate);

            foreach (Vector2[] figure in _figures)
            {
                if (figure.Length < 3)
                {
                    continue;
                }

                sink.BeginFigure(figure[0], FigureBegin.Filled);
                for (int i = 1; i < figure.Length; i++)
                {
                    sink.AddLine(figure[i]);
                }

                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        _wordmark = geometry;
        return geometry;
    }

    /// <summary>把 path 数据切成若干闭合轮廓，每个轮廓是一串点。</summary>
    private static List<Vector2[]> ParseFigures(string path)
    {
        List<Vector2[]> figures = [];
        List<Vector2> points = [];
        int index = 0;

        while (index < path.Length)
        {
            char command = path[index];

            if (command is 'M' or 'L')
            {
                index++;
                if (!TryReadNumber(path, ref index, out float x)
                    || !TryReadNumber(path, ref index, out float y))
                {
                    continue;
                }

                // M 开启新轮廓，后续同命令的坐标对等价于 L
                if (command == 'M' && points.Count > 0)
                {
                    figures.Add(points.ToArray());
                    points.Clear();
                }

                points.Add(new Vector2(x, y));
            }
            else if (command is 'Z' or 'z')
            {
                index++;
                if (points.Count > 1)
                {
                    figures.Add(points.ToArray());
                }

                points.Clear();
            }
            else
            {
                index++;
            }
        }

        if (points.Count > 1)
        {
            figures.Add(points.ToArray());
        }

        return figures;
    }

    private static bool TryReadNumber(string text, ref int index, out float value)
    {
        value = 0f;
        while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
        {
            index++;
        }

        int begin = index;
        if (index < text.Length && (text[index] == '-' || text[index] == '+'))
        {
            index++;
        }

        bool sawDigit = false;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
            sawDigit = true;
        }

        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
                sawDigit = true;
            }
        }

        if (!sawDigit)
        {
            return false;
        }

        return float.TryParse(
            text.AsSpan(begin, index - begin),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    public void Dispose()
    {
        _wordmark?.Dispose();
        _wordmark = null;
    }
}
