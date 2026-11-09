using System.Numerics;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Learning;
using ForzaOverlay.Core.Telemetry;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 行驶仪表 · SHIFT TREE（820×320）：左转速弧、右车速 + 油门/刹车半弧，中央 10 颗换挡灯与档位。
/// 从 HudScene 拆出，几何常量与绘制逻辑原样保留。
/// </summary>
internal sealed class ShiftTreePanel : IDisposable
{
    private const float BaseWidth = 820f;
    private const float ArcCenterXLeft = 146f;
    private const float ArcCenterXRight = 674f;
    private const float ArcCenterY = 160f;
    private const float ArcRadius = 106f;
    private const float RimRadius = 122f;
    private const float StartAngle = 135f;
    private const float FullSweep = 270f;
    private const float HalfSweep = 135f;
    private const int LedCount = 10;
    private const float LedSize = 13.6f;
    private const float LedGap = 10f;

    private readonly Dictionary<ArcKey, ID2D1PathGeometry> _arcCache = [];
    private readonly IDWriteTextFormat? _unitFormat;
    private readonly IDWriteTextFormat? _valueFormat;
    private readonly IDWriteTextFormat? _speedFormat;
    private readonly IDWriteTextFormat? _gearFormat;

    private readonly record struct ArcKey(float CenterX, float CenterY, float Radius, float StartAngle, float Sweep);

    internal ShiftTreePanel(IDWriteFactory? dwrite)
    {
        if (dwrite is null)
        {
            return;
        }

        _unitFormat = CreateFormat(dwrite, 18f, FontWeight.SemiBold);
        _valueFormat = CreateFormat(dwrite, 58f, FontWeight.Bold);
        _speedFormat = CreateFormat(dwrite, 63f, FontWeight.Bold);
        _gearFormat = CreateFormat(dwrite, 124f, FontWeight.Bold);
    }

    private static IDWriteTextFormat? CreateFormat(IDWriteFactory dwrite, float size, FontWeight weight)
    {
        IDWriteTextFormat format = dwrite.CreateTextFormat(
            "Bahnschrift", null!, weight, FontStyle.Normal, FontStretch.Normal, size, "zh-CN");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        return format;
    }

    internal void Draw(
        ID2D1RenderTarget target,
        HudBrushCache brushes,
        GearTextFormatter gearFormatter,
        AppSettings settings,
        in ForzaDataPacket packet,
        bool hasPacket,
        ShiftPointLearner learner,
        float effectiveMaxRpm,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        HudModuleLayout layout = settings.ActiveLayout.ShiftTreeLayout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.46f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        float ratio = PacketRatio(packet, hasPacket, effectiveMaxRpm);
        float redline = GetRedlineRatio(settings, packet, hasPacket, learner);

        DrawGauge(target, brushes, ArcCenterXLeft, ArcCenterY, ratio, throttle: null, brake: null, redline);
        DrawGauge(target, brushes, ArcCenterXRight, ArcCenterY, ratio, PacketThrottle(packet, hasPacket), PacketBrake(packet, hasPacket), 0.867f);
        DrawLeds(target, brushes, settings, packet, hasPacket, learner, ratio);
        DrawTexts(target, brushes, packet, hasPacket, gearFormatter);

        target.Transform = Matrix3x2.Identity;
    }

    private static float PacketRatio(in ForzaDataPacket packet, bool hasPacket, float effectiveMaxRpm)
    {
        if (!hasPacket)
        {
            return 0f;
        }

        return (float)Math.Clamp(packet.CurrentEngineRpm / effectiveMaxRpm, 0.0, 1.0);
    }

    private static float? PacketThrottle(in ForzaDataPacket packet, bool hasPacket)
    {
        return hasPacket ? (float)Math.Clamp(packet.ThrottleNormalized, 0f, 1f) : 0f;
    }

    private static float? PacketBrake(in ForzaDataPacket packet, bool hasPacket)
    {
        return hasPacket ? (float)Math.Clamp(packet.BrakeNormalized, 0f, 1f) : 0f;
    }

    /// <summary>红区起点比例：开启红区校正时用学习值，否则沿用旧的固定阈值。</summary>
    private static float GetRedlineRatio(AppSettings settings, in ForzaDataPacket packet, bool hasPacket, ShiftPointLearner learner)
    {
        if (!settings.EnableRedlineCalibration || !hasPacket)
        {
            return 0.867f;
        }

        return (float)learner.GetRedlineRatio(packet);
    }

    private void DrawGauge(
        ID2D1RenderTarget target,
        HudBrushCache brushes,
        float centerX,
        float centerY,
        float ratio,
        float? throttle,
        float? brake,
        float redline)
    {
        Vector2 center = new(centerX, centerY);

        ID2D1SolidColorBrush rimBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.15f));
        ID2D1SolidColorBrush trackBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.09f));
        ID2D1SolidColorBrush zoneBrush = brushes.Get(target, new Color4(1f, 0.29f, 0.24f, 0.30f));
        ID2D1SolidColorBrush tickBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.14f));
        ID2D1SolidColorBrush tickMajorBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.30f));

        DrawArc(target, center, RimRadius, StartAngle, FullSweep, rimBrush, 2f, cache: true);
        float warn = Math.Max(0.40f, redline - 0.10f);
        DrawArc(target, center, ArcRadius, StartAngle, FullSweep, trackBrush, 10f, cache: true);
        if (throttle is null)
        {
            // 红区只属于转速仪表；右侧仪表是油门/刹车进度，不画红区。
            DrawArc(target, center, ArcRadius, StartAngle + FullSweep * redline, FullSweep * (1f - redline), zoneBrush, 10f, cache: true);
        }

        DrawTicks(target, center, tickBrush, tickMajorBrush);

        if (throttle is null)
        {
            ID2D1SolidColorBrush fillBrush = brushes.Get(target, GetArcColor(ratio, redline, warn));
            DrawArc(target, center, ArcRadius, StartAngle, FullSweep * ratio, fillBrush, 10f);
        }
        else
        {
            float throttleValue = throttle ?? 0f;
            float brakeValue = brake ?? 0f;
            ID2D1SolidColorBrush throttleBrush = brushes.Get(target, new Color4(0.31f, 0.76f, 0.97f, 1f));
            ID2D1SolidColorBrush brakeBrush = brushes.Get(target, new Color4(1f, 0.29f, 0.24f, 1f));

            DrawArc(target, center, ArcRadius, StartAngle, HalfSweep * throttleValue, throttleBrush, 10f);
            DrawArc(target, center, ArcRadius, 45f, -HalfSweep * brakeValue, brakeBrush, 10f);
        }
    }

    private static Color4 GetArcColor(float ratio, float redline, float warn)
    {
        if (ratio >= redline)
        {
            return new Color4(1f, 0.29f, 0.24f, 1f);
        }

        if (ratio >= warn)
        {
            return new Color4(1f, 0.69f, 0.13f, 1f);
        }

        return new Color4(0f, 0.9f, 1f, 1f);
    }

    private static void DrawTicks(ID2D1RenderTarget target, Vector2 center, ID2D1Brush tickBrush, ID2D1Brush tickMajorBrush)
    {
        for (int i = 0; i <= 8; i++)
        {
            double angle = (StartAngle + i * 33.75) * Math.PI / 180.0;
            bool major = i % 2 == 0;
            double inner = major ? 112 : 114;
            Vector2 from = new(
                center.X + (float)(inner * Math.Cos(angle)),
                center.Y + (float)(inner * Math.Sin(angle)));
            Vector2 to = new(
                center.X + (float)(120 * Math.Cos(angle)),
                center.Y + (float)(120 * Math.Sin(angle)));

            target.DrawLine(from, to, major ? tickMajorBrush : tickBrush, major ? 1.4f : 1f);
        }
    }

    private void DrawArc(
        ID2D1RenderTarget target,
        Vector2 center,
        float radius,
        float startAngle,
        float sweep,
        ID2D1Brush brush,
        float thickness,
        bool cache = false)
    {
        if (Math.Abs(sweep) < 0.1f)
        {
            return;
        }

        ID2D1PathGeometry geometry;
        bool owned = false;

        if (cache)
        {
            // 转速弧的 sweep 每帧都在变；量化到 0.5° 并限制缓存数量，避免无限增长。
            ArcKey key = new(
                center.X,
                center.Y,
                radius,
                MathF.Round(startAngle * 2f) / 2f,
                MathF.Round(sweep * 2f) / 2f);
            if (_arcCache.Count > 400)
            {
                foreach (ID2D1PathGeometry stale in _arcCache.Values)
                {
                    stale.Dispose();
                }

                _arcCache.Clear();
            }

            if (!_arcCache.TryGetValue(key, out ID2D1PathGeometry? cached))
            {
                cached = BuildArcGeometry(target, center, radius, startAngle, sweep);
                _arcCache[key] = cached;
            }

            geometry = cached;
        }
        else
        {
            geometry = BuildArcGeometry(target, center, radius, startAngle, sweep);
            owned = true;
        }

        target.DrawGeometry(geometry, brush, thickness);

        if (owned)
        {
            geometry.Dispose();
        }
    }

    private static ID2D1PathGeometry BuildArcGeometry(ID2D1RenderTarget target, Vector2 center, float radius, float startAngle, float sweep)
    {
        Vector2 start = PointOnCircle(center, radius, startAngle);
        Vector2 end = PointOnCircle(center, radius, startAngle + sweep);

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(start, FigureBegin.Hollow);
            sink.AddArc(new ArcSegment(
                end,
                new Size(Math.Abs(radius), Math.Abs(radius)),
                0f,
                sweep > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                Math.Abs(sweep) > 180f ? ArcSize.Large : ArcSize.Small));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return geometry;
    }

    private static Vector2 PointOnCircle(Vector2 center, float radius, float angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        return new Vector2(
            center.X + (float)(radius * Math.Cos(radians)),
            center.Y + (float)(radius * Math.Sin(radians)));
    }

    private static void DrawLeds(
        ID2D1RenderTarget target,
        HudBrushCache brushes,
        AppSettings settings,
        in ForzaDataPacket packet,
        bool hasPacket,
        ShiftPointLearner learner,
        float ratio)
    {
        ID2D1SolidColorBrush offBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.10f));
        ID2D1SolidColorBrush greenBrush = brushes.Get(target, new Color4(0x36 / 255f, 0xE2 / 255f, 0x7B / 255f, 1f));
        ID2D1SolidColorBrush amberBrush = brushes.Get(target, new Color4(1f, 0.69f, 0.13f, 1f));
        ID2D1SolidColorBrush redBrush = brushes.Get(target, new Color4(1f, 0.29f, 0.24f, 1f));

        bool learned = settings.EnableRedlineCalibration
            && hasPacket
            && learner.GetCutInfo(packet).Learned;

        if (!learned)
        {
            // 未学习：保持原来的比例点亮（1-6 绿、7-8 黄、9-10 红）。
            int lit = Math.Clamp((int)Math.Ceiling((ratio * LedCount) - 1e-6), 0, LedCount);
            float totalWidth = (LedCount * LedSize) + ((LedCount - 1) * LedGap);
            float startX = (BaseWidth - totalWidth) / 2f;
            float y = 56f + (LedSize / 2f);

            for (int i = 0; i < LedCount; i++)
            {
                float x = startX + (i * (LedSize + LedGap)) + (LedSize / 2f);
                ID2D1Brush brush = i >= lit
                    ? offBrush
                    : i < 6 ? greenBrush : i < 8 ? amberBrush : redBrush;

                target.FillEllipse(new Ellipse(new Vector2(x, y), LedSize / 2f, LedSize / 2f), brush);
            }

            return;
        }

        // 学习后：绿 1-6、黄 7-8 覆盖「黄区起点 → 红区起点」，9 = 红区起点，10 = 换挡提示点。
        double maxRpm = packet.EngineMaxRpm > 1 ? packet.EngineMaxRpm : 8000;
        double redlineRatio = learner.GetRedlineRatio(packet);
        double yellowRatio = Math.Max(0.30, redlineRatio - 0.10);
        (double cutRpm, _, _) = learner.GetCutInfo(packet);
        double shiftHintRatio = Math.Clamp((cutRpm - ((maxRpm - cutRpm) * 0.28)) / maxRpm, 0.30, 1.0);
        shiftHintRatio = Math.Max(shiftHintRatio, redlineRatio + 0.005);

        double[] thresholds = new double[LedCount];
        for (int i = 0; i < 6; i++)
        {
            thresholds[i] = yellowRatio * (i + 1) / 6.0;
        }

        thresholds[6] = yellowRatio;
        thresholds[7] = (yellowRatio + redlineRatio) / 2.0;
        thresholds[8] = redlineRatio;
        thresholds[9] = shiftHintRatio;

        float learnedTotalWidth = (LedCount * LedSize) + ((LedCount - 1) * LedGap);
        float learnedStartX = (BaseWidth - learnedTotalWidth) / 2f;
        float learnedY = 56f + (LedSize / 2f);

        for (int i = 0; i < LedCount; i++)
        {
            bool on = ratio >= thresholds[i];
            float x = learnedStartX + (i * (LedSize + LedGap)) + (LedSize / 2f);

            ID2D1Brush brush = on
                ? (i < 6 ? greenBrush : i < 8 ? amberBrush : redBrush)
                : offBrush;

            target.FillEllipse(new Ellipse(new Vector2(x, learnedY), LedSize / 2f, LedSize / 2f), brush);
        }
    }

    private void DrawTexts(
        ID2D1RenderTarget target,
        HudBrushCache brushes,
        in ForzaDataPacket packet,
        bool hasPacket,
        GearTextFormatter gearFormatter)
    {
        ID2D1SolidColorBrush whiteBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 1f));
        ID2D1SolidColorBrush dimBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.52f));

        float rpm = hasPacket ? (float)Math.Round(packet.CurrentEngineRpm) : 0f;
        float speed = hasPacket ? (float)Math.Round(packet.SpeedKmh) : 0f;
        string gear = hasPacket ? gearFormatter.Format(packet.GearNumber) : "--";

        HudDraw.CenteredText(target, "RPM", _unitFormat, dimBrush, ArcCenterXLeft, ArcCenterY - 42f, 200f, 24f);
        HudDraw.FixedDigits(
            target,
            rpm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            _valueFormat,
            whiteBrush,
            ArcCenterXLeft,
            ArcCenterY + 22f,
            33f,
            80f);

        HudDraw.CenteredText(target, "KM/H", _unitFormat, dimBrush, ArcCenterXRight, ArcCenterY - 42f, 200f, 24f);
        HudDraw.FixedDigits(
            target,
            speed.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            _speedFormat,
            whiteBrush,
            ArcCenterXRight,
            ArcCenterY + 24f,
            36f,
            90f);

        HudDraw.CenteredText(target, gear, _gearFormat, whiteBrush, BaseWidth / 2f, ArcCenterY, 300f, 160f);
    }

    public void Dispose()
    {
        foreach (ID2D1PathGeometry geometry in _arcCache.Values)
        {
            geometry.Dispose();
        }

        _arcCache.Clear();
        _unitFormat?.Dispose();
        _valueFormat?.Dispose();
        _speedFormat?.Dispose();
        _gearFormat?.Dispose();
    }
}
