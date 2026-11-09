using System.Globalization;
using System.Numerics;
using ForzaOverlay.App.Ui;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Learning;
using ForzaOverlay.Core.Telemetry;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 行驶仪表 · GHOST LADDER（420×238）：无底板，几何描边，数字定槽。
/// 从 HudScene 拆出；档位铭牌、数字字形缓存的持有者也在本类。
/// </summary>
internal sealed class GhostPanel : IDisposable
{
    private const float BaseWidth = 420f;

    private readonly IDWriteFontCollection1? _customFonts;
    private readonly string? _customFamilyName;
    private readonly Dictionary<char, ID2D1PathGeometry> _digitGeometryCache = [];
    private IDWriteFontFace? _ghostFontFace;
    private ID2D1PathGeometry? _gearPlatePath;

    private readonly IDWriteTextFormat? _ghostGearValueFormat;
    private readonly IDWriteTextFormat? _ghostGearLabelFormat;
    private readonly IDWriteTextFormat? _ghostSpeedFormat;
    private readonly IDWriteTextFormat? _ghostUnitFormat;
    private readonly IDWriteTextFormat? _ghostSmallFormat;
    private readonly IDWriteTextFormat? _ghostBoostValueFormat;
    private readonly IDWriteTextFormat? _ghostBoostUnitFormat;

    internal GhostPanel(IDWriteFactory? dwrite, IDWriteFontCollection1? customFonts, string? customFamilyName)
    {
        if (dwrite is null)
        {
            return;
        }

        _customFonts = customFonts;
        _customFamilyName = customFamilyName;

        string ghostFamily = customFamilyName ?? "Bahnschrift";

        if (customFonts is not null && customFamilyName is not null)
        {
            IDWriteFontFamily fontFamily = customFonts.GetFontFamily(0);
            using IDWriteFont font = fontFamily.GetFirstMatchingFont(FontWeight.Bold, FontStretch.Normal, FontStyle.Normal);
            _ghostFontFace = font.CreateFontFace();
            fontFamily.Dispose();
        }

        _ghostGearValueFormat = CreateFormat(dwrite, ghostFamily, 50f, FontWeight.Bold, TextAlignment.Center, FontStyle.Italic, customFonts);
        _ghostGearLabelFormat = CreateFormat(dwrite, "Bahnschrift", 9f, FontWeight.SemiBold, TextAlignment.Leading, FontStyle.Normal, null);
        _ghostSpeedFormat = CreateFormat(dwrite, ghostFamily, 119f, FontWeight.Bold, TextAlignment.Center, FontStyle.Normal, customFonts);

        if (_ghostSpeedFormat is not null)
        {
            _ghostSpeedFormat.ParagraphAlignment = ParagraphAlignment.Far;
        }

        _ghostUnitFormat = CreateFormat(dwrite, "Bahnschrift", 13f, FontWeight.SemiBold, TextAlignment.Trailing, FontStyle.Normal, null);
        _ghostSmallFormat = CreateFormat(dwrite, "Bahnschrift", 9f, FontWeight.SemiBold, TextAlignment.Leading, FontStyle.Normal, null);
        _ghostBoostValueFormat = CreateFormat(dwrite, "Consolas", 13f, FontWeight.Bold, TextAlignment.Trailing, FontStyle.Normal, null);
        _ghostBoostUnitFormat = CreateFormat(dwrite, "Bahnschrift", 11f, FontWeight.Normal, TextAlignment.Leading, FontStyle.Normal, null);
    }

    private static IDWriteTextFormat? CreateFormat(
        IDWriteFactory dwrite,
        string family,
        float size,
        FontWeight weight,
        TextAlignment alignment,
        FontStyle style,
        IDWriteFontCollection? collection)
    {
        IDWriteTextFormat format = collection is not null
            ? dwrite.CreateTextFormat(family, collection, weight, style, FontStretch.Normal, size, "zh-CN")
            : dwrite.CreateTextFormat(family, null!, weight, style, FontStretch.Normal, size, "zh-CN");

        format.TextAlignment = alignment;
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
        bool hasBoost,
        float effectiveMaxRpm,
        ShiftPointLearner learner,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        HudModuleLayout layout = settings.ActiveLayout.FestivalLayout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.2888f;
        float scale = Math.Clamp(widthRatio * viewportWidth / BaseWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        ID2D1SolidColorBrush whiteBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 1f));
        ID2D1SolidColorBrush dimBrush = brushes.Get(target, new Color4(1f, 1f, 1f, 0.6f));
        ID2D1SolidColorBrush shadowBrush = brushes.Get(target, new Color4(0f, 0f, 0f, 0.9f));

        ID2D1SolidColorBrush glowOuter = brushes.Get(target, new Color4(0f, 0.9f, 1f, 0.12f));
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(-8f, 142f, 104f, 104f), 12f, 12f),
            glowOuter);

        ID2D1SolidColorBrush glowInner = brushes.Get(target, new Color4(0f, 0.9f, 1f, 0.18f));
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(-3f, 147f, 94f, 94f), 9f, 9f),
            glowInner);

        if (_gearPlatePath is null)
        {
            _gearPlatePath = BuildGearPlatePath(target);
        }

        ID2D1SolidColorBrush plateBrush = brushes.Get(target, new Color4(0.012f, 0.031f, 0.059f, 0.86f));
        ID2D1SolidColorBrush plateBorder = brushes.Get(target, new Color4(0f, 0.9f, 1f, 0.6f));
        target.FillGeometry(_gearPlatePath, plateBrush);
        target.DrawGeometry(_gearPlatePath, plateBorder, 1f);

        ID2D1SolidColorBrush cyanBrush = brushes.Get(target, new Color4(0f, 0.9f, 1f, 1f));
        HudDraw.Text(target, "GEAR", _ghostGearLabelFormat, cyanBrush, 16f, 160f, 60f, 14f);

        string gear = hasPacket ? gearFormatter.Format(packet.GearNumber) : "--";
        Matrix3x2 gearBaseTransform = target.Transform;
        Matrix3x2 gearSkew = Matrix3x2.CreateTranslation(-44f, -194f)
            * Matrix3x2.CreateSkew(-12f * MathF.PI / 180f, 0f)
            * Matrix3x2.CreateTranslation(44f, 194f);
        target.Transform = gearSkew * gearBaseTransform;
        HudDraw.Text(target, gear, _ghostGearValueFormat, shadowBrush, 0f, 160f, 88f, 88f);
        HudDraw.Text(target, gear, _ghostGearValueFormat, whiteBrush, 0f, 158f, 88f, 88f);
        target.Transform = gearBaseTransform;

        if (hasPacket)
        {
            string speedText = Math.Clamp((int)Math.Round(packet.SpeedKmh), 0, 999).ToString(CultureInfo.InvariantCulture);
            float frameRight = 102f + 276f;

            Matrix3x2 baseTransform = target.Transform;
            Matrix3x2 skew = Matrix3x2.CreateTranslation(-102f, -8f)
                * Matrix3x2.CreateSkew(-12f * MathF.PI / 180f, 0f)
                * Matrix3x2.CreateTranslation(102f, 8f);
            target.Transform = skew * baseTransform;

            const float slotWidth = 63f;
            ID2D1SolidColorBrush outlineBrush = brushes.Get(
                target,
                new Color4(0x04 / 255f, 0x07 / 255f, 0x0B / 255f, 0.3f));

            for (int i = 0; i < speedText.Length; i++)
            {
                int slotFromRight = speedText.Length - 1 - i;
                float center = frameRight - slotFromRight * slotWidth - slotWidth / 2f;
                string digit = speedText[i].ToString();
                ID2D1PathGeometry? digitGeometry = GetDigitGeometry(target, speedText[i]);

                if (digitGeometry is null)
                {
                    HudDraw.Text(target, digit, _ghostSpeedFormat, whiteBrush, center - slotWidth / 2f, 0f, slotWidth, 191f);
                    continue;
                }

                Rect bounds = digitGeometry.GetBounds();
                float translateX = center - ((bounds.Left + bounds.Right) / 2f);
                float translateY = 167f - bounds.Bottom;

                Matrix3x2 current = target.Transform;
                Matrix3x2 local = Matrix3x2.CreateTranslation(translateX, translateY);

                target.Transform = local * current;
                target.DrawGeometry(digitGeometry, outlineBrush, 2f);
                target.FillGeometry(digitGeometry, whiteBrush);

                target.Transform = current;
            }

            target.Transform = baseTransform;
        }

        HudDraw.Text(target, "km/h", _ghostUnitFormat, whiteBrush, 342f, 160f, 46f, 18f);

        float ratio = hasPacket ? (float)Math.Clamp(packet.CurrentEngineRpm / effectiveMaxRpm, 0.0, 1.0) : 0f;
        int lit = Math.Clamp((int)Math.Round(ratio * 30.0), 0, 30);

        for (int i = 0; i < 30; i++)
        {
            Color4 color;
            if (i < lit)
            {
                double segmentRedline = hasPacket && settings.EnableRedlineCalibration
                    ? learner.GetRedlineRatio(packet)
                    : 0.90;
                System.Windows.Media.Color rpmColor = RpmColorScale.At(
                    (i + 0.5) / 30.0,
                    System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF),
                    RpmColorScale.AmberColor,
                    segmentRedline);
                color = new Color4(rpmColor.R / 255f, rpmColor.G / 255f, rpmColor.B / 255f, 1f);
            }
            else
            {
                color = new Color4(0x6B / 255f, 0x96 / 255f, 0xA8 / 255f, 0xB8 / 255f);
            }

            float segmentX = 102f + i * 10f;
            ID2D1SolidColorBrush segmentBrush = brushes.Get(target, color);

            if (i < lit)
            {
                Color4 glowColor = new(color.R, color.G, color.B, 0.28f);
                target.FillRoundedRectangle(
                    new RoundedRectangle(new System.Drawing.RectangleF(segmentX - 1f, 181f, 9f, 26f), 1f, 1f),
                    brushes.Get(target, glowColor));
            }

            target.FillRoundedRectangle(
                new RoundedRectangle(new System.Drawing.RectangleF(segmentX, 182f, 7f, 24f), 1f, 1f),
                segmentBrush);
        }

        HudDraw.Text(target, "BOOST", _ghostSmallFormat, whiteBrush, 106f, 219f, 44f, 16f);

        RoundedRectangle boostTrack = new(new System.Drawing.RectangleF(158f, 222f, 157f, 8f), 1f, 1f);
        target.FillRoundedRectangle(boostTrack, brushes.Get(target, new Color4(0f, 0f, 0f, 0.4f)));
        target.DrawRoundedRectangle(boostTrack, brushes.Get(target, new Color4(0f, 0.9f, 1f, 0.6f)), 1f);

        float boostBar = hasPacket && hasBoost ? packet.BoostBar : 0f;
        float boostRatio = hasPacket ? (float)Math.Min(Math.Abs(boostBar) / 2.0, 1.0) : 0f;
        if (boostRatio > 0.01f)
        {
            Color4 boostColor = boostBar < 0
                ? new Color4(0x96 / 255f, 0xA8 / 255f, 0xB8 / 255f, 0.7f)
                : new Color4(0f, 0.9f, 1f, 1f);

            target.FillRoundedRectangle(
                new RoundedRectangle(new System.Drawing.RectangleF(158f, 222f, 157f * boostRatio, 8f), 1f, 1f),
                brushes.Get(target, boostColor));
        }

        string boostText = hasPacket ? HudDraw.Truncate(boostBar, 2) : "--";
        HudDraw.Text(target, boostText, _ghostBoostValueFormat, whiteBrush, 335f, 216f, 44f, 18f);
        HudDraw.Text(target, "bar", _ghostBoostUnitFormat, dimBrush, 383f, 216f, 26f, 18f);

        target.Transform = Matrix3x2.Identity;
    }

    private unsafe ID2D1PathGeometry? GetDigitGeometry(ID2D1RenderTarget target, char digit)
    {
        if (_ghostFontFace is null)
        {
            return null;
        }

        if (_digitGeometryCache.TryGetValue(digit, out ID2D1PathGeometry? cached))
        {
            return cached;
        }

        uint codePoint = digit;
        ushort glyphIndex;
        _ghostFontFace.GetGlyphIndices(&codePoint, 1, &glyphIndex);

        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            ushort[] glyphs = [glyphIndex];
            float[] advances = [119f];
            _ghostFontFace.GetGlyphRunOutline(119f, glyphs, advances, null, false, false, sink);
            sink.Close();
        }

        _digitGeometryCache[digit] = geometry;
        return geometry;
    }

    private static ID2D1PathGeometry BuildGearPlatePath(ID2D1RenderTarget target)
    {
        ID2D1PathGeometry geometry = target.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(12f, 150f), FigureBegin.Filled);
            sink.AddLine(new Vector2(88f, 150f));
            sink.AddLine(new Vector2(88f, 226f));
            sink.AddLine(new Vector2(76f, 238f));
            sink.AddLine(new Vector2(0f, 238f));
            sink.AddLine(new Vector2(0f, 162f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        return geometry;
    }

    public void Dispose()
    {
        foreach (ID2D1PathGeometry geometry in _digitGeometryCache.Values)
        {
            geometry.Dispose();
        }

        _digitGeometryCache.Clear();
        _ghostFontFace?.Dispose();
        _gearPlatePath?.Dispose();
        _gearPlatePath = null;
        _ghostGearValueFormat?.Dispose();
        _ghostGearLabelFormat?.Dispose();
        _ghostSpeedFormat?.Dispose();
        _ghostUnitFormat?.Dispose();
        _ghostSmallFormat?.Dispose();
        _ghostBoostValueFormat?.Dispose();
        _ghostBoostUnitFormat?.Dispose();
    }
}
