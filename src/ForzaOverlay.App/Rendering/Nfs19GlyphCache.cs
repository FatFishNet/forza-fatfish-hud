using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 仿NFS19 仪表：主转速表 + 左侧增压小表。
/// 几何与字形全部来自最终版原型 <c>temp/panel-speedo-nfs19</c>，坐标按原型画布 640×479 书写。
/// 字形是手绘 SVG 路径（描边中心线），不是字体：运行时解析成几何后缓存，用 DrawGeometry 描边。
/// </summary>
internal sealed class Nfs19GlyphCache
{
    /// <summary>刻度标签字形（dialGlyphs），字箱 20×16。</summary>
    private static readonly Dictionary<char, string> DialGlyphPaths = new()
    {
        ['0'] = "M4 2 H16 Q19 2 19 5 V13 Q19 16 16 16 H4 Q1 16 1 13 V5 Q1 2 4 2 Z",
        ['1'] = "M6 5 L12 2 V16",
        ['2'] = "M2 3 Q2 2 5 2 H16 Q19 2 19 5 V7 Q19 9 16 9 H5 Q2 9 2 12 V16 H19",
        ['3'] = "M2 2 H16 Q19 2 19 5 V6 Q19 9 15 9 H7 M15 9 Q19 9 19 12 V13 Q19 16 16 16 H2",
        ['4'] = "M15 16 V2 L2 11 H20",
        ['5'] = "M19 2 H2 V9 H16 Q19 9 19 12 V13 Q19 16 16 16 H2",
        ['6'] = "M18 2 H5 Q2 2 2 5 V13 Q2 16 5 16 H16 Q19 16 19 13 V12 Q19 9 16 9 H2",
        ['7'] = "M1 2 H20 L9 16",
        ['8'] = "M5 2 H15 Q19 2 19 5 Q19 9 15 9 H5 Q1 9 1 5 Q1 2 5 2 Z M5 9 H15 Q19 9 19 12 Q19 16 15 16 H5 Q1 16 1 12 Q1 9 5 9 Z",
        ['9'] = "M19 9 H5 Q2 9 2 6 V5 Q2 2 5 2 H16 Q19 2 19 5 V13 Q19 16 16 16 H3",
    };

    /// <summary>七段数字的单段路径（时速与档位用），段面 30×44。</summary>
    private static readonly Dictionary<char, string> SegmentPaths = new()
    {
        ['a'] = "M4 1 L24 1 L21 4 L7 4 Z",
        ['b'] = "M25 2 L25 21 L22 19 L22 6 Z",
        ['c'] = "M25 23 L25 42 L22 38 L22 25 Z",
        ['d'] = "M4 43 L24 43 L21 40 L7 40 Z",
        ['e'] = "M3 23 L6 25 L6 38 L3 42 Z",
        ['f'] = "M3 2 L6 6 L6 19 L3 21 Z",
        ['g'] = "M4 22 L7 20.5 L21 20.5 L24 22 L21 23.5 L7 23.5 Z",
    };

    /// <summary>字符 → 需要点亮的段。</summary>
    private static readonly Dictionary<char, string> SegmentMap = new()
    {
        ['0'] = "abcdef",
        ['1'] = "bc",
        ['2'] = "abged",
        ['3'] = "abgcd",
        ['4'] = "fgbc",
        ['5'] = "afgcd",
        ['6'] = "afgecd",
        ['7'] = "abc",
        ['8'] = "abcdefg",
        ['9'] = "abfgcd",
        ['N'] = "abcef",
        ['R'] = "abefg",
        ['-'] = "g",
        [' '] = string.Empty,
    };

    /// <summary>
    /// 档位按墨宽排版的左右边界（段面坐标）：'1' 只占右侧两段，按整格排会与 '0' 之间空一大截。
    /// </summary>
    private static readonly Dictionary<char, (float Left, float Right)> GearInk = new()
    {
        ['1'] = (22f, 25f),
        ['-'] = (3f, 24f),
    };

    private static readonly (float Left, float Right) DefaultInk = (3f, 25f);

    private readonly Dictionary<char, ID2D1PathGeometry?> _dialGeometryCache = [];
    private readonly Dictionary<char, ID2D1PathGeometry?> _segmentGeometryCache = [];

    internal static (float Left, float Right) InkOf(char ch)
    {
        return GearInk.TryGetValue(ch, out (float Left, float Right) ink) ? ink : DefaultInk;
    }

    internal static string SegmentsOf(char ch)
    {
        return SegmentMap.TryGetValue(ch, out string? segments) ? segments : string.Empty;
    }

    internal ID2D1PathGeometry? DialGlyph(ID2D1RenderTarget target, char ch)
    {
        if (_dialGeometryCache.TryGetValue(ch, out ID2D1PathGeometry? cached))
        {
            return cached;
        }

        ID2D1PathGeometry? geometry = DialGlyphPaths.TryGetValue(ch, out string? path)
            ? BuildGeometry(target, path)
            : null;
        _dialGeometryCache[ch] = geometry;
        return geometry;
    }

    internal ID2D1PathGeometry? Segment(ID2D1RenderTarget target, char segment)
    {
        if (_segmentGeometryCache.TryGetValue(segment, out ID2D1PathGeometry? cached))
        {
            return cached;
        }

        ID2D1PathGeometry? geometry = SegmentPaths.TryGetValue(segment, out string? path)
            ? BuildGeometry(target, path, filled: true)
            : null;
        _segmentGeometryCache[segment] = geometry;
        return geometry;
    }

    /// <summary>
    /// 解析 SVG path 的最小指令集：M/L/H/V/Q/Z（原型只用到这五个，含隐式重复与负数、小数）。
    /// 纯相对坐标的 a/b/c 等指令原型没有使用，故不实现。
    /// <para>
    /// <paramref name="filled"/> 决定图形能否被填充：刻度标签是描边字形（Hollow），
    /// 七段数字是实心块（Filled）。用 Hollow 的路径 <c>FillGeometry</c> 什么都画不出来。
    /// </para>
    /// </summary>
    private static ID2D1PathGeometry? BuildGeometry(ID2D1RenderTarget target, string pathData, bool filled = false)
    {
        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();

        FigureBegin beginMode = filled ? FigureBegin.Filled : FigureBegin.Hollow;
        List<float> numbers = [];
        char command = '\0';
        int index = 0;
        bool figureOpen = false;
        Vector2 current = Vector2.Zero;
        Vector2 start = Vector2.Zero;

        while (index < pathData.Length)
        {
            char ch = pathData[index];

            if (char.IsLetter(ch))
            {
                command = ch;
                index++;
                if (command is 'Z' or 'z')
                {
                    if (figureOpen)
                    {
                        sink.EndFigure(FigureEnd.Closed);
                        figureOpen = false;
                    }

                    current = start;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch) || ch == ',')
            {
                index++;
                continue;
            }

            numbers.Clear();
            if (!TryReadNumber(pathData, ref index, out float first))
            {
                index++;
                continue;
            }

            numbers.Add(first);

            if (command is 'H' or 'h' or 'V' or 'v')
            {
                if (!TryReadNumber(pathData, ref index, out float second))
                {
                    second = 0f;
                }

                numbers.Add(second);
            }
            else if (command is 'M' or 'm' or 'L' or 'l')
            {
                if (!TryReadNumber(pathData, ref index, out float second))
                {
                    second = 0f;
                }

                numbers.Add(second);
            }
            else if (command is 'Q' or 'q')
            {
                if (TryReadNumber(pathData, ref index, out float second))
                {
                    numbers.Add(second);
                }

                if (TryReadNumber(pathData, ref index, out float third))
                {
                    numbers.Add(third);
                }

                if (TryReadNumber(pathData, ref index, out float fourth))
                {
                    numbers.Add(fourth);
                }
            }

            switch (command)
            {
                case 'M':
                case 'm':
                {
                    Vector2 point = command == 'm'
                        ? current + new Vector2(numbers[0], numbers[1])
                        : new Vector2(numbers[0], numbers[1]);

                    if (figureOpen)
                    {
                        sink.EndFigure(FigureEnd.Open);
                    }

                    sink.BeginFigure(point, beginMode);
                    figureOpen = true;
                    current = point;
                    start = point;
                    // 后续同命令的坐标对按 L 处理
                    command = command == 'm' ? 'l' : 'L';
                    break;
                }

                case 'L':
                case 'l':
                {
                    Vector2 point = command == 'l'
                        ? current + new Vector2(numbers[0], numbers[1])
                        : new Vector2(numbers[0], numbers[1]);
                    EnsureFigure(sink, ref figureOpen, current, beginMode);
                    sink.AddLine(point);
                    current = point;
                    break;
                }

                case 'H':
                case 'h':
                {
                    float x = command == 'h' ? current.X + numbers[0] : numbers[0];
                    Vector2 point = new(x, current.Y);
                    EnsureFigure(sink, ref figureOpen, current, beginMode);
                    sink.AddLine(point);
                    current = point;
                    break;
                }

                case 'V':
                case 'v':
                {
                    float y = command == 'v' ? current.Y + numbers[0] : numbers[0];
                    Vector2 point = new(current.X, y);
                    EnsureFigure(sink, ref figureOpen, current, beginMode);
                    sink.AddLine(point);
                    current = point;
                    break;
                }

                case 'Q':
                case 'q':
                {
                    if (numbers.Count < 4)
                    {
                        break;
                    }

                    Vector2 control = command == 'q'
                        ? current + new Vector2(numbers[0], numbers[1])
                        : new Vector2(numbers[0], numbers[1]);
                    Vector2 point = command == 'q'
                        ? current + new Vector2(numbers[2], numbers[3])
                        : new Vector2(numbers[2], numbers[3]);
                    EnsureFigure(sink, ref figureOpen, current, beginMode);
                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = control, Point2 = point });
                    current = point;
                    break;
                }
            }
        }

        if (figureOpen)
        {
            sink.EndFigure(FigureEnd.Open);
        }

        sink.Close();
        return geometry;
    }

    private static void EnsureFigure(ID2D1GeometrySink sink, ref bool figureOpen, Vector2 current, FigureBegin beginMode)
    {
        if (figureOpen)
        {
            return;
        }

        sink.BeginFigure(current, beginMode);
        figureOpen = true;
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
}
