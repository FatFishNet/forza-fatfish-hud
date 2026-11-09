using System.Globalization;
using System.Numerics;
using ForzaOverlay.App.Services;
using ForzaOverlay.App.Ui;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Learning;
using ForzaOverlay.Core.Telemetry;
using ForzaOverlay.Core.Visuals;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// GPU HUD 场景调度器：按布局与开关把各面板分派给对应的绘制方。
/// 经典仪表、轮胎、资源占用、换挡提示的绘制仍在本文内；
/// GHOST / SHIFT TREE / 仿NFS19 已拆到独立文件（<see cref="GhostPanel"/>、
/// <see cref="ShiftTreePanel"/>、<see cref="Nfs19Scene"/>）。
/// </summary>
public sealed class HudScene : IDisposable
{
    private readonly float _screenWidth;
    private readonly float _screenHeight;
    private readonly object _packetLock = new();
    private readonly object _viewportLock = new();
    private int _viewportX;
    private int _viewportY;
    private int _viewportWidth;
    private int _viewportHeight;

    private readonly IDWriteFactory? _dwrite;
    private readonly IDWriteFontCollection1? _customFonts;
    private readonly string? _customFamilyName;
    private readonly IDWriteTextFormat? _perfTitleFormat;
    private readonly IDWriteTextFormat? _perfLabelFormat;
    private readonly IDWriteTextFormat? _perfValueFormat;
    private readonly IDWriteTextFormat? _classicValueFormat;
    private readonly IDWriteTextFormat? _classicRpmFormat;
    private readonly IDWriteTextFormat? _classicSpeedFormat;
    private readonly IDWriteTextFormat? _classicGearFormat;
    private readonly IDWriteTextFormat? _classicLabelFormat;
    private readonly IDWriteTextFormat? _classicLabel15Format;
    private readonly IDWriteTextFormat? _classicUnit15Format;
    private readonly IDWriteTextFormat? _tireLabelFormat;
    private readonly IDWriteTextFormat? _tireTempFormat;
    private readonly IDWriteTextFormat? _tirePctLeftFormat;
    private readonly IDWriteTextFormat? _tirePctRightFormat;
    private readonly IDWriteTextFormat? _hintTitleFormat;
    private readonly IDWriteTextFormat? _hintSmallFormat;
    private Nfs19Scene? _nfs19Scene;

    /// <summary>
    /// 最后一次有效的最大转速。游戏在菜单 / 车库 / 暂停时不发该值（为 0），
    /// 用它顶住，避免量程与指针在进出菜单时跳变。
    /// </summary>
    private float _maxRpm = 8000f;

    /// <summary>
    /// 无增压车辆检测。自然吸气车同样会填 Boost 字段且数值漂移，
    /// NFS19 增压表归中、GHOST 显示 0.00 都依赖这里的判定结果。
    /// </summary>
    private readonly BoostPresence _boostPresence = new();

    private static readonly Color4[] PerfBarColors =
    [
        new(0x7C / 255f, 0xE6 / 255f, 0xA0 / 255f, 1f),
        new(0xFF / 255f, 0xD2 / 255f, 0x4D / 255f, 1f),
        new(0x4D / 255f, 0xDA / 255f, 0xFF / 255f, 1f),
        new(0xB3 / 255f, 0x88 / 255f, 0xFF / 255f, 1f),
    ];

    private readonly HudBrushCache _brushCache = new();
    private HudTextLayoutCache _textLayoutCache = null!;
    private readonly GearTextFormatter _gearFormatter = new();
    private ShiftTreePanel? _shiftTreePanel;
    private GhostPanel? _ghostPanel;

    private readonly ShiftPointLearner _shiftLearner = new(System.IO.Path.Combine(AppContext.BaseDirectory, "data"));
    private ForzaDataPacket _packet;
    private bool _hasPacket;
    private bool _firstRenderLogged;

    public HudScene(float screenWidth, float screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _viewportWidth = (int)screenWidth;
        _viewportHeight = (int)screenHeight;

        try
        {
            _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            _customFonts = LoadEmbeddedFonts(_dwrite);
            string fontPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "D-DIN-Bold.ttf");
            _customFamilyName = _customFonts is not null
                ? FindDdinFamily(_customFonts) ?? TryGetFamilyNameFromWpf(fontPath)
                : null;
            ForzaOverlay.Core.Logging.Diagnostics.Write(
                _customFonts is null
                    ? "D-DIN 字体未加载，GHOST 使用 Bahnschrift"
                    : $"D-DIN 字体集合已加载，family={_customFamilyName ?? "未找到"}");

            _perfTitleFormat = CreateFormat("Microsoft YaHei UI", 11f, FontWeight.SemiBold, TextAlignment.Leading);
            _perfLabelFormat = CreateFormat("Microsoft YaHei UI", 11f, FontWeight.Normal, TextAlignment.Leading);
            _perfValueFormat = CreateFormat("Microsoft YaHei UI", 11f, FontWeight.Normal, TextAlignment.Trailing);

            _classicValueFormat = CreateFormat("Microsoft YaHei UI", 19f, FontWeight.Bold, TextAlignment.Trailing);
            _classicRpmFormat = CreateFormat("Microsoft YaHei UI", 34f, FontWeight.Bold, TextAlignment.Center);
            _classicSpeedFormat = CreateFormat("Microsoft YaHei UI", 86f, FontWeight.Bold, TextAlignment.Trailing);
            _classicGearFormat = CreateFormat("Microsoft YaHei UI", 58f, FontWeight.Bold, TextAlignment.Center);
            _classicLabelFormat = CreateFormat("Microsoft YaHei UI", 12f, FontWeight.Normal, TextAlignment.Leading);
            _classicLabel15Format = CreateFormat("Microsoft YaHei UI", 15f, FontWeight.Bold, TextAlignment.Center);
            _classicUnit15Format = CreateFormat("Microsoft YaHei UI", 14f, FontWeight.Bold, TextAlignment.Leading);

            if (_classicUnit15Format is not null)
            {
                _classicUnit15Format.WordWrapping = WordWrapping.NoWrap;
            }

            _tireLabelFormat = CreateFormat("Microsoft YaHei UI", 14f, FontWeight.Normal, TextAlignment.Leading);
            _tireTempFormat = CreateFormat("Microsoft YaHei UI", 18f, FontWeight.Bold, TextAlignment.Leading);
            _tirePctLeftFormat = CreateFormat("Consolas", 14f, FontWeight.Bold, TextAlignment.Leading);
            _tirePctRightFormat = CreateFormat("Consolas", 14f, FontWeight.Bold, TextAlignment.Trailing);

            _hintTitleFormat = CreateFormat("Bahnschrift", 20f, FontWeight.Bold, TextAlignment.Center);
            _hintSmallFormat = CreateFormat("Microsoft YaHei UI", 9f, FontWeight.Normal, TextAlignment.Leading);

            _textLayoutCache = new HudTextLayoutCache(_dwrite);
            _shiftTreePanel = new ShiftTreePanel(_dwrite);
            _ghostPanel = new GhostPanel(_dwrite, _customFonts, _customFamilyName);
            _nfs19Scene = new Nfs19Scene(_dwrite);
        }
        catch (Exception ex)
        {
            ForzaOverlay.Core.Logging.Diagnostics.Write("GPU 场景初始化失败: " + ex);
        }
    }

    private static string? FindDdinFamily(IDWriteFontCollection1 collection)
    {
        string[] candidates = ["D-DIN", "D-DIN Bold", "D-DIN-Bold", "D-DIN Condensed"];

        foreach (string candidate in candidates)
        {
            if (collection.FindFamilyName(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryGetFamilyNameFromWpf(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            foreach (System.Windows.Media.FontFamily family in System.Windows.Media.Fonts.GetFontFamilies(path))
            {
                string source = family.Source;
                int hashIndex = source.LastIndexOf('#');
                return hashIndex >= 0 ? source[(hashIndex + 1)..] : source;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static IDWriteFontCollection1? LoadEmbeddedFonts(IDWriteFactory factory)
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "D-DIN-Bold.ttf");
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            using IDWriteFactory5 factory5 = factory.QueryInterface<IDWriteFactory5>();
            using IDWriteFontSetBuilder1 builder = factory5.CreateFontSetBuilder();
            using IDWriteFontFile fontFile = factory.CreateFontFileReference(path, null);
            builder.AddFontFile(fontFile);
            using IDWriteFontSet fontSet = builder.CreateFontSet();

            using IDWriteFactory3 factory3 = factory.QueryInterface<IDWriteFactory3>();
            return factory3.CreateFontCollectionFromFontSet(fontSet);
        }
        catch (Exception ex)
        {
            ForzaOverlay.Core.Logging.Diagnostics.Write("D-DIN 字体加载失败: " + ex.Message);
            return null;
        }
    }

    private IDWriteTextFormat? CreateFormat(string family, float size, FontWeight weight, TextAlignment alignment, FontStyle style = FontStyle.Normal, IDWriteFontCollection? collection = null)
    {
        if (_dwrite is null)
        {
            return null;
        }

        IDWriteTextFormat format = collection is not null
            ? _dwrite.CreateTextFormat(family, collection, weight, style, FontStretch.Normal, size, "zh-CN")
            : _dwrite.CreateTextFormat(family, null!, weight, style, FontStretch.Normal, size, "zh-CN");

        format.TextAlignment = alignment;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        return format;
    }

    public void Render(ID2D1RenderTarget target, double delta)
    {
        AppSettings? settings = AppServices.CurrentSettings;
        if (settings is null)
        {
            return;
        }

        if (!_firstRenderLogged)
        {
            _firstRenderLogged = true;
            ForzaOverlay.Core.Logging.Diagnostics.Write(
                $"GPU 场景首帧：样式={settings.InstrumentStyle} 仪表开关={settings.ShowPerformancePanel} 资源开关={settings.ShowPerfPanel}");
        }

        int viewportX;
        int viewportY;
        int viewportWidth;
        int viewportHeight;
        lock (_viewportLock)
        {
            viewportX = _viewportX;
            viewportY = _viewportY;
            viewportWidth = _viewportWidth;
            viewportHeight = _viewportHeight;
        }

        if (settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.ShiftTree)
        {
            _shiftTreePanel?.Draw(
                target,
                _brushCache,
                _gearFormatter,
                settings,
                _packet,
                _hasPacket,
                _shiftLearner,
                EffectiveMaxRpm(),
                viewportX,
                viewportY,
                viewportWidth,
                viewportHeight);
        }

        if (settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Classic)
        {
            DrawClassicPanel(target, settings, viewportX, viewportY, viewportWidth, viewportHeight);
        }

        if (settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Festival)
        {
            _ghostPanel?.Draw(
                target,
                _brushCache,
                _gearFormatter,
                settings,
                _packet,
                _hasPacket,
                _boostPresence.HasBoost,
                EffectiveMaxRpm(),
                _shiftLearner,
                viewportX,
                viewportY,
                viewportWidth,
                viewportHeight);
        }

        if (settings.ShowPerformancePanel && settings.InstrumentStyle == HudInstrumentStyle.Nfs19)
        {
            _nfs19Scene?.Draw(
                target,
                settings.ActiveLayout.Nfs19Layout,
                _packet,
                _hasPacket,
                _shiftLearner,
                EffectiveMaxRpm(),
                viewportX,
                viewportY,
                viewportWidth,
                viewportHeight);
        }

        if (settings.ShowTirePanel)
        {
            DrawTirePanel(target, settings, viewportX, viewportY, viewportWidth, viewportHeight);
        }

        if (settings.ShowPerfPanel)
        {
            DrawPerfPanel(target, settings, viewportX, viewportY, viewportWidth, viewportHeight);
        }

        if (settings.EnableRedlineCalibration && settings.ShiftHintStyle != HudShiftHintStyle.Off)
        {
            DrawShiftHint(target, settings, viewportX, viewportY, viewportWidth, viewportHeight);
        }

        target.Transform = Matrix3x2.Identity;
    }

    private void DrawClassicPanel(ID2D1RenderTarget target, AppSettings settings, int viewportX, int viewportY, int viewportWidth, int viewportHeight)
    {
        const float panelWidth = 820f;
        const float panelHeight = 150f;

        HudModuleLayout layout = settings.ActiveLayout.PerformanceLayout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.482f;
        float scale = Math.Clamp(widthRatio * viewportWidth / panelWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        double opacity = Math.Clamp(settings.BasicPanelBackgroundOpacity, 0, 100) / 100.0;

        ID2D1SolidColorBrush background = GetBrush(
            target,
            new Color4(0x2C / 255f, 0x34 / 255f, 0x40 / 255f, (float)opacity));
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(0, 0, panelWidth, panelHeight), 8f, 8f),
            background);

        ID2D1SolidColorBrush whiteBrush = GetBrush(target, new Color4(1f, 1f, 1f, 1f));
        ID2D1SolidColorBrush dimBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.52f));
        ID2D1SolidColorBrush trackBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.09f));

        float ratio = packetRatio();

        target.FillRectangle(new RawRectF(0f, 0f, panelWidth, 10f), trackBrush);

        double classicRedline = _hasPacket && settings.EnableRedlineCalibration
            ? _shiftLearner.GetRedlineRatio(_packet)
            : 0.90;
        System.Windows.Media.Color rpmColor = RpmColorScale.At(
            ratio,
            System.Windows.Media.Color.FromRgb(0x00, 0xC8, 0xE0),
            RpmColorScale.AmberColor,
            classicRedline);
        ID2D1SolidColorBrush rpmBrush = GetBrush(
            target,
            new Color4(rpmColor.R / 255f, rpmColor.G / 255f, rpmColor.B / 255f, 1f));

        if (ratio > 0.01f)
        {
            target.FillRectangle(new RawRectF(0f, 0f, ratio * panelWidth, 10f), rpmBrush);
        }

        string[] rowLabels = ["马力", "扭矩", "增压"];
        string[] rowUnits = ["千瓦", "牛米", "bar"];
        string[] rowValues =
        [
            _hasPacket ? Math.Max(0f, _packet.PowerKw).ToString("F0", CultureInfo.InvariantCulture) : "--",
            _hasPacket ? HudDraw.Truncate(Math.Max(0f, _packet.TorqueNm), 1) : "--",
            _hasPacket ? HudDraw.Truncate(_packet.BoostBar, 2) : "--",
        ];
        float[] rowY = [38f, 66f, 94f];

        for (int i = 0; i < 3; i++)
        {
            HudDraw.Text(target, rowLabels[i], _classicLabelFormat, dimBrush, 26f, rowY[i], 30f, 20f);
            HudDraw.Text(target, rowValues[i], _classicValueFormat, whiteBrush, 56f, rowY[i] - 2f, 68f, 24f);
            HudDraw.Text(target, rowUnits[i], _classicLabelFormat, dimBrush, 132f, rowY[i], 30f, 20f);
        }

        System.Drawing.RectangleF gearRect = new(184f, 30f, 90f, 100f);
        RoundedRectangle gearRounded = new(gearRect, 9f, 9f);
        ID2D1SolidColorBrush gearBack = GetBrush(target, new Color4(0f, 0.9f, 1f, 0.08f));
        ID2D1SolidColorBrush gearBorder = GetBrush(target, new Color4(0f, 0.9f, 1f, 0.42f));
        ID2D1SolidColorBrush gearLabelBrush = GetBrush(target, new Color4(0f, 0.9f, 1f, 0.92f));
        target.FillRoundedRectangle(gearRounded, gearBack);
        target.DrawRoundedRectangle(gearRounded, gearBorder, 1f);

        string gear = _hasPacket ? _gearFormatter.Format(_packet.GearNumber) : "--";
        HudDraw.Text(target, gear, _classicGearFormat, whiteBrush, 184f, 36f, 90f, 70f);
        HudDraw.Text(target, "档位", _classicLabel15Format, gearLabelBrush, 184f, 104f, 90f, 22f);

        string rpmText = _hasPacket ? Math.Round(_packet.CurrentEngineRpm).ToString("F0", CultureInfo.InvariantCulture) : "--";
        HudDraw.Text(target, rpmText, _classicRpmFormat, whiteBrush, 281f, 45f, 110f, 50f);
        HudDraw.Text(target, "转速", _classicLabel15Format, dimBrush, 281f, 97f, 110f, 22f);

        string speedText = _hasPacket ? Math.Round(_packet.SpeedKmh).ToString("F0", CultureInfo.InvariantCulture) : "--";
        HudDraw.Text(target, speedText, _classicSpeedFormat, whiteBrush, 400f, 28f, 163f, 104f);
        HudDraw.Text(target, "km/h", _classicUnit15Format, dimBrush, 570f, 98f, 40f, 22f);

        float[] pedalHeights =
        [
            _hasPacket ? (float)Math.Clamp(_packet.ClutchNormalized, 0f, 1f) * 77f : 0f,
            _hasPacket ? (float)Math.Clamp(_packet.ThrottleNormalized, 0f, 1f) * 77f : 0f,
            _hasPacket ? (float)Math.Clamp(_packet.BrakeNormalized, 0f, 1f) * 77f : 0f,
            _hasPacket ? (float)Math.Clamp(_packet.HandBrakeNormalized, 0f, 1f) * 77f : 0f,
        ];
        Color4[] pedalColors =
        [
            new(0xB3 / 255f, 0x88 / 255f, 0xFF / 255f, 0.75f),
            new(0x4F / 255f, 0xC3 / 255f, 0xF7 / 255f, 1f),
            new(0xF4 / 255f, 0x43 / 255f, 0x36 / 255f, 1f),
            new(0xFF / 255f, 0xEB / 255f, 0x3B / 255f, 1f),
        ];
        string[] pedalLabels = ["离合", "油门", "刹车", "手刹"];

        for (int i = 0; i < 4; i++)
        {
            float column = 622f + i * 36f;
            float barX = column + 6f;
            target.FillRoundedRectangle(
                new RoundedRectangle(new System.Drawing.RectangleF(barX, 29f, 18f, 77f), 5f, 5f),
                trackBrush);

            float height = pedalHeights[i];
            if (height > 0.5f)
            {
                ID2D1SolidColorBrush pedalBrush = GetBrush(target, pedalColors[i]);
                target.FillRoundedRectangle(
                    new RoundedRectangle(new System.Drawing.RectangleF(barX, 29f + 77f - height, 18f, height), 5f, 5f),
                    pedalBrush);
            }

            HudDraw.Text(target, pedalLabels[i], _classicLabelFormat, dimBrush, column, 112f, 30f, 18f);
        }

        target.Transform = Matrix3x2.Identity;
    }

    private void DrawTirePanel(ID2D1RenderTarget target, AppSettings settings, int viewportX, int viewportY, int viewportWidth, int viewportHeight)
    {
        const float panelWidth = 220f;
        const float panelHeight = 212f;

        HudModuleLayout layout = settings.ActiveLayout.TireLayout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.18f;
        float scale = Math.Clamp(widthRatio * viewportWidth / panelWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        double opacity = Math.Clamp(settings.BasicPanelBackgroundOpacity, 0, 100) / 100.0;
        ID2D1SolidColorBrush background = GetBrush(
            target,
            new Color4(0x2C / 255f, 0x34 / 255f, 0x40 / 255f, (float)opacity));
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(0, 0, panelWidth, panelHeight), 8f, 8f),
            background);

        ID2D1SolidColorBrush labelBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.5f));
        ID2D1SolidColorBrush whiteBrush = GetBrush(target, new Color4(1f, 1f, 1f, 1f));
        ID2D1SolidColorBrush tickBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.26f));
        ID2D1SolidColorBrush suspTrackBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.1f));

        DrawTireCorner(
            target, 19f, 34f, 19f, 9f, 50f, 7f, 57f, 77f, 51f, true,
            _hasPacket ? _packet.TireTempFrontLeft : float.NaN,
            _hasPacket ? _packet.TireCombinedSlipFrontLeft : 0f,
            _hasPacket ? _packet.NormalizedSuspensionTravelFrontLeft : 0f,
            labelBrush, whiteBrush, tickBrush, suspTrackBrush);

        DrawTireCorner(
            target, 169f, 34f, 124f, 9f, 155f, 7f, 153f, 109f, 51f, false,
            _hasPacket ? _packet.TireTempFrontRight : float.NaN,
            _hasPacket ? _packet.TireCombinedSlipFrontRight : 0f,
            _hasPacket ? _packet.NormalizedSuspensionTravelFrontRight : 0f,
            labelBrush, whiteBrush, tickBrush, suspTrackBrush);

        DrawTireCorner(
            target, 19f, 145f, 19f, 120f, 50f, 118f, 57f, 77f, 162f, true,
            _hasPacket ? _packet.TireTempRearLeft : float.NaN,
            _hasPacket ? _packet.TireCombinedSlipRearLeft : 0f,
            _hasPacket ? _packet.NormalizedSuspensionTravelRearLeft : 0f,
            labelBrush, whiteBrush, tickBrush, suspTrackBrush);

        DrawTireCorner(
            target, 169f, 145f, 124f, 120f, 155f, 118f, 153f, 109f, 162f, false,
            _hasPacket ? _packet.TireTempRearRight : float.NaN,
            _hasPacket ? _packet.TireCombinedSlipRearRight : 0f,
            _hasPacket ? _packet.NormalizedSuspensionTravelRearRight : 0f,
            labelBrush, whiteBrush, tickBrush, suspTrackBrush);

        target.Transform = Matrix3x2.Identity;
    }

    private void DrawTireCorner(
        ID2D1RenderTarget target,
        float blockX,
        float blockY,
        float labelX,
        float labelY,
        float tempX,
        float tempY,
        float suspX,
        float pctX,
        float pctY,
        bool percentLeft,
        float tempFahrenheit,
        float combinedSlip,
        float suspension,
        ID2D1SolidColorBrush labelBrush,
        ID2D1SolidColorBrush whiteBrush,
        ID2D1SolidColorBrush tickBrush,
        ID2D1SolidColorBrush suspTrackBrush)
    {
        bool hasData = _hasPacket && !float.IsNaN(tempFahrenheit);
        float tempCelsius = hasData ? (tempFahrenheit - 32f) * 5f / 9f : 0f;
        string tempText = hasData
            ? tempCelsius.ToString("F0", CultureInfo.InvariantCulture) + "℃"
            : "--";

        Color4 fillColor;
        Color4 borderColor;

        if (hasData)
        {
            TireVisualState state = TireVisualEvaluator.Evaluate(tempCelsius, combinedSlip);
            double alphaFactor = 1.0;
            if (state.Effect == TireGripEffect.Critical)
            {
                double wave = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1000.0 * 2.0 * Math.PI * 5.0);
                alphaFactor = 1.0 - 0.7 * wave * state.FlashIntensity;
            }

            fillColor = ToColor4(state.BaseColor, alphaFactor);
            borderColor = ToColor4(state.BorderColor, alphaFactor);
        }
        else
        {
            fillColor = new Color4(1f, 1f, 1f, 0x40 / 255f);
            borderColor = new Color4(1f, 1f, 1f, 0x99 / 255f);
        }

        HudDraw.Text(target, "胎温", _tireLabelFormat, labelBrush, labelX, labelY, 30f, 18f);
        HudDraw.Text(target, tempText, _tireTempFormat, whiteBrush, tempX, tempY, 70f, 24f);

        System.Drawing.RectangleF tireRect = new(blockX, blockY, 32f, 52f);
        RoundedRectangle tireRounded = new(tireRect, 9f, 9f);
        target.FillRoundedRectangle(tireRounded, GetBrush(target, fillColor));
        target.DrawRoundedRectangle(tireRounded, GetBrush(target, borderColor), 1f);

        for (int i = 0; i < 8; i++)
        {
            float lineY = blockY + 8f + i * 5f;
            target.FillRectangle(new RawRectF(blockX + 6f, lineY, blockX + 26f, lineY + 1f), tickBrush);
        }

        System.Drawing.RectangleF suspRect = new(suspX, blockY, 10f, 52f);
        target.FillRoundedRectangle(new RoundedRectangle(suspRect, 4f, 4f), suspTrackBrush);

        float ratio = _hasPacket ? (float)Math.Clamp(suspension, 0f, 1f) : 0f;
        if (ratio > 0.01f)
        {
            Color4 suspColor = ratio > 0.82f
                ? new Color4(1f, 0.69f, 0.13f, 1f)
                : new Color4(0.49f, 0.98f, 1f, 1f);
            float height = ratio * 52f;
            target.FillRoundedRectangle(
                new RoundedRectangle(new System.Drawing.RectangleF(suspX, blockY + 52f - height, 10f, height), 4f, 4f),
                GetBrush(target, suspColor));
        }

        string pctText = ((int)Math.Round(ratio * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        if (percentLeft)
        {
            HudDraw.Text(target, pctText, _tirePctLeftFormat, labelBrush, pctX, pctY, 34f, 18f);
        }
        else
        {
            HudDraw.Text(target, pctText, _tirePctRightFormat, labelBrush, pctX, pctY, 34f, 18f);
        }
    }

    private static Color4 ToColor4(TireColor color, double alphaFactor)
    {
        return new Color4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            (float)(color.A / 255.0 * alphaFactor));
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_viewportLock)
        {
            _viewportX = x;
            _viewportY = y;
            _viewportWidth = width;
            _viewportHeight = height;
        }
    }

    /// <summary>红区起点比例：开启红区校正时用学习值，否则沿用旧的固定阈值。</summary>
    private float GetRedlineRatio(AppSettings settings)
    {
        if (!settings.EnableRedlineCalibration || !_hasPacket)
        {
            return 0.867f;
        }

        return (float)_shiftLearner.GetRedlineRatio(_packet);
    }

    /// <summary>换挡提示：VTEC 模拟灯（10 颗横向灯）或 3 色圆灯；只画灯与文字，无背景/边框。</summary>
    private void DrawShiftHint(ID2D1RenderTarget target, AppSettings settings, int viewportX, int viewportY, int viewportWidth, int viewportHeight)
    {
        if (_hintTitleFormat is null || _hintSmallFormat is null || !_hasPacket)
        {
            return;
        }

        HudModuleLayout layout = settings.ActiveLayout.ShiftHintRgb3Layout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.15f;
        float scale = Math.Clamp(widthRatio * viewportWidth / (float)HudLayoutDefaults.ShiftHintWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        (double _, double best) = _shiftLearner.GetShiftHintRatios(_packet);
        double shiftRpm = Math.Clamp(best, 0.5, 1.0) * EffectiveMaxRpm();
        float ratio = shiftRpm > 1
            ? (float)Math.Clamp(_packet.CurrentEngineRpm / shiftRpm, 0f, 1.1f)
            : 0f;

        double maxRpm = EffectiveMaxRpm();
        double redlineRatio = _shiftLearner.GetRedlineRatio(_packet);
        double yellowRatio = Math.Max(0.30, redlineRatio - 0.10);
        (double cutRpm, _, bool learned) = _shiftLearner.GetCutInfo(_packet);
        double shiftHintRatio;
        if (learned)
        {
            shiftHintRatio = Math.Clamp((cutRpm - ((maxRpm - cutRpm) * 0.28)) / maxRpm, 0.30, 1.0);

            // 与红区至少拉开 3%，否则两颗红灯会几乎同时亮。
            shiftHintRatio = Math.Max(shiftHintRatio, redlineRatio + 0.03);
        }
        else
        {
            // 自动挡：红区从换挡点开始，第 4 颗灯放在红区之后一点。
            double? autoShift = _shiftLearner.GetShiftPointRpm(_packet);
            shiftHintRatio = autoShift is double autoShiftRpm && autoShiftRpm > 0
                ? Math.Clamp(autoShiftRpm * 1.02 / maxRpm, 0.30, 0.98)
                : Math.Min(0.98, redlineRatio + 0.03);
        }

        float rpmRatio = (float)Math.Clamp(_packet.CurrentEngineRpm / maxRpm, 0f, 1.1f);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        DrawRgb3Hint(target, rpmRatio, (float)yellowRatio, (float)redlineRatio, (float)shiftHintRatio);

        target.Transform = Matrix3x2.Identity;
    }

    private void DrawRgb3Hint(ID2D1RenderTarget target, float ratio, float yellowRatio, float redRatio, float shiftRatio)
    {
        ID2D1SolidColorBrush off = GetBrush(target, new Color4(1f, 1f, 1f, 0.10f));

        bool shiftOn = ratio >= shiftRatio;

        Color4[] colors =
        [
            new(0x36 / 255f, 0xE2 / 255f, 0x7B / 255f, 1f),
            new(1f, 0.69f, 0.13f, 1f),
            new(1f, 0.29f, 0.24f, 1f),
            new(1f, 0.29f, 0.24f, 1f),
        ];
        bool[] lit =
        [
            true,
            ratio >= yellowRatio,
            ratio >= redRatio,
            shiftOn,
        ];

        const float radius = 10f;
        const float gap = 9f;
        const float startX = 6.5f;
        const float y = 20f;

        for (int i = 0; i < 4; i++)
        {
            ID2D1SolidColorBrush brush = lit[i] ? GetBrush(target, colors[i]) : off;
            float x = startX + (i * ((radius * 2f) + gap)) + radius;
            target.FillEllipse(new Ellipse(new Vector2(x, y), radius, radius), brush);
        }
    }

    private void DrawPerfPanel(ID2D1RenderTarget target, AppSettings settings, int viewportX, int viewportY, int viewportWidth, int viewportHeight)
    {
        if (_perfTitleFormat is null || _perfLabelFormat is null || _perfValueFormat is null)
        {
            return;
        }

        const float panelWidth = 200f;
        const float panelHeight = 140f;

        HudModuleLayout layout = settings.ActiveLayout.PerfLayout;
        float widthRatio = layout.WidthRatio > 0 ? (float)layout.WidthRatio : 0.1042f;
        float scale = Math.Clamp(widthRatio * viewportWidth / panelWidth, 0.05f, 20f);
        float left = viewportX + (float)(layout.X * viewportWidth);
        float top = viewportY + (float)(layout.Y * viewportHeight);

        target.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);

        double opacity = Math.Clamp(settings.BasicPanelBackgroundOpacity, 0, 100) / 100.0;

        ID2D1SolidColorBrush background = GetBrush(
            target,
            new Color4(0x2C / 255f, 0x34 / 255f, 0x40 / 255f, (float)opacity));
        target.FillRoundedRectangle(
            new RoundedRectangle(new System.Drawing.RectangleF(0, 0, panelWidth, panelHeight), 8f, 8f),
            background);

        ID2D1SolidColorBrush titleBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.80f));
        ID2D1SolidColorBrush labelBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.67f));
        ID2D1SolidColorBrush valueBrush = GetBrush(target, new Color4(1f, 1f, 1f, 0.87f));
        ID2D1SolidColorBrush trackBrush = GetBrush(target, new Color4(0.2f, 0.2f, 0.2f, 0.35f));

        DrawCachedText(target, "HUD渲染资源占用", _perfTitleFormat, titleBrush, 10f, 8f, 180f, 14f);

        string[] labels = ["帧率", "CPU", "内存", "GPU", "显存"];
        string[] values =
        [
            PerformanceMonitor.Fps.ToString("F0", CultureInfo.InvariantCulture),
            PerformanceMonitor.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
            PerformanceMonitor.MemoryMb.ToString("F0", CultureInfo.InvariantCulture) + "MB",
            PerformanceMonitor.GpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
            PerformanceMonitor.VramMb.ToString("F0", CultureInfo.InvariantCulture) + "MB",
        ];
        float[] ratios =
        [
            (float)(PerformanceMonitor.Fps / 60.0),
            (float)(PerformanceMonitor.CpuPercent / 100.0),
            (float)(PerformanceMonitor.MemoryMb / 512.0),
            (float)(PerformanceMonitor.GpuPercent / 100.0),
        ];

        for (int i = 0; i < labels.Length; i++)
        {
            float y = 28f + i * 18f;
            DrawCachedText(target, labels[i], _perfLabelFormat, labelBrush, 10f, y, 30f, 14f);
            DrawCachedText(target, values[i], _perfValueFormat, valueBrush, 140f, y, 50f, 14f);

            if (i < PerfBarColors.Length)
            {
                const float barLeft = 44f;
                const float barWidth = 90f;
                target.FillRectangle(new RawRectF(barLeft, y + 4f, barLeft + barWidth, y + 10f), trackBrush);

                float fill = Math.Clamp(ratios[i], 0f, 1f) * barWidth;
                if (fill > 0.5f)
                {
                    ID2D1SolidColorBrush barBrush = GetBrush(target, PerfBarColors[i]);
                    target.FillRectangle(new RawRectF(barLeft, y + 4f, barLeft + fill, y + 10f), barBrush);
                }
            }
        }
    }

    public bool TryGetLatest(out ForzaDataPacket packet)
    {
        lock (_packetLock)
        {
            packet = _packet;
            return _hasPacket;
        }
    }

    private ID2D1SolidColorBrush GetBrush(ID2D1RenderTarget target, Color4 color)
    {
        return _brushCache.Get(target, color);
    }

    private void DrawCachedText(ID2D1RenderTarget target, string text, IDWriteTextFormat? format, ID2D1Brush brush, float x, float y, float width, float height)
    {
        _textLayoutCache.Draw(target, text, format, brush, x, y, width, height);
    }

    public bool PumpPacket()
    {
        if (AppServices.TryReadLatest(out TelemetryFrame frame))
        {
            lock (_packetLock)
            {
                _packet = frame.Packet;
                _hasPacket = true;
            }

            // 游戏在菜单 / 暂停 / 车库时不发有效量程（EngineMaxRpm 为 0），
            // 若直接回退到兜底 8000，进菜单后盘面量程会突变、指针也会顶格。
            // 这里只记住"最后一次有效值"，退出菜单后自动沿用真实量程。
            if (frame.Packet.EngineMaxRpm > 1f)
            {
                _maxRpm = frame.Packet.EngineMaxRpm;
            }

            _boostPresence.Update(frame.Packet, frame.ReceivedTickMs);

            // 红区校正关闭时不采集样本，避免产生用户没有要求的学习数据。
            if (AppServices.CurrentSettings?.EnableRedlineCalibration != false)
            {
                _shiftLearner.Update(frame.Packet, frame.ReceivedTickMs);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 当前使用的最大转速：优先取本次包的 EngineMaxRpm，无效则沿用上一次的有效值，
    /// 从未拿到过有效值时用 8000 兜底。所有面板统一走这里，不要各自回退。
    /// </summary>
    private float EffectiveMaxRpm()
    {
        return _hasPacket && _packet.EngineMaxRpm > 1f ? _packet.EngineMaxRpm : _maxRpm;
    }

    private float packetRatio()
    {
        if (!_hasPacket)
        {
            return 0f;
        }

        double maxRpm = EffectiveMaxRpm();
        return (float)Math.Clamp(_packet.CurrentEngineRpm / maxRpm, 0.0, 1.0);
    }


    public void Dispose()
    {
        _nfs19Scene?.Dispose();
        _shiftTreePanel?.Dispose();
        _ghostPanel?.Dispose();
        _textLayoutCache.Dispose();
        _brushCache.Dispose();
        _perfTitleFormat?.Dispose();
        _perfLabelFormat?.Dispose();
        _perfValueFormat?.Dispose();
        _classicValueFormat?.Dispose();
        _classicRpmFormat?.Dispose();
        _classicSpeedFormat?.Dispose();
        _classicGearFormat?.Dispose();
        _classicLabelFormat?.Dispose();
        _classicLabel15Format?.Dispose();
        _classicUnit15Format?.Dispose();
        _tireLabelFormat?.Dispose();
        _tireTempFormat?.Dispose();
        _tirePctLeftFormat?.Dispose();
        _tirePctRightFormat?.Dispose();
        _hintTitleFormat?.Dispose();
        _hintSmallFormat?.Dispose();
        _customFonts?.Dispose();
        _dwrite?.Dispose();
    }
}
