namespace ForzaOverlay.Core.Visuals;

public enum TireGripEffect
{
    None,
    Warning,
    Critical,
}

public readonly record struct TireColor(byte A, byte R, byte G, byte B);

public readonly record struct TireVisualState(
    TireColor BaseColor,
    TireColor BorderColor,
    TireGripEffect Effect,
    double FlashIntensity);

public static class TireVisualEvaluator
{
    public const double ColdMaxCelsius = 52;
    public const double NormalMaxCelsius = 85;
    public const double OverheatMaxCelsius = 113;
    public const double TempBufferCelsius = 3;

    public const double GripWarningSlip = 0.75;
    public const double LoseControlSlip = 1.0;
    public const double SlipBuffer = 0.05;

    private static readonly TireColor ColdColor = new(0xFF, 0x4F, 0xC3, 0xF7);
    private static readonly TireColor OptimalColor = new(0x00, 0x00, 0x00, 0x00);
    private static readonly TireColor HotColor = new(0xFF, 0xFF, 0xEB, 0x3B);
    private static readonly TireColor OverheatColor = new(0xFF, 0xF4, 0x43, 0x36);
    private static readonly TireColor WarningBorderColor = new(0xFF, 0xFF, 0xEB, 0x3B);
    private static readonly TireColor CriticalColor = new(0xFF, 0xF4, 0x43, 0x36);
    private static readonly TireColor WhiteBorderColor = new(0x99, 0xFF, 0xFF, 0xFF);

    public static TireVisualState Evaluate(float tireTempCelsius, float combinedSlip)
    {
        double temp = tireTempCelsius;
        double slip = Math.Abs((double)combinedSlip);

        TireColor tempColor = GetTemperatureColor(temp);
        double severity = Clamp01((slip - (LoseControlSlip - SlipBuffer)) / (2 * SlipBuffer));
        TireColor baseColor = LerpColor(tempColor, CriticalColor, severity);

        (TireColor border, TireGripEffect effect, double flashIntensity) = GetSlipEffect(slip);

        return new TireVisualState(baseColor, border, effect, flashIntensity);
    }

    private static TireColor GetTemperatureColor(double temp)
    {
        double coldStart = ColdMaxCelsius - TempBufferCelsius;
        double coldEnd = ColdMaxCelsius + TempBufferCelsius;
        double hotStart = NormalMaxCelsius - TempBufferCelsius;
        double hotEnd = NormalMaxCelsius + TempBufferCelsius;
        double overStart = OverheatMaxCelsius - TempBufferCelsius;
        double overEnd = OverheatMaxCelsius + TempBufferCelsius;

        if (temp <= coldStart)
        {
            return ColdColor;
        }

        if (temp < coldEnd)
        {
            return LerpColor(ColdColor, OptimalColor, (temp - coldStart) / (2 * TempBufferCelsius));
        }

        if (temp <= hotStart)
        {
            return OptimalColor;
        }

        if (temp < hotEnd)
        {
            return LerpColor(OptimalColor, HotColor, (temp - hotStart) / (2 * TempBufferCelsius));
        }

        if (temp <= overStart)
        {
            return HotColor;
        }

        if (temp < overEnd)
        {
            return LerpColor(HotColor, OverheatColor, (temp - overStart) / (2 * TempBufferCelsius));
        }

        return OverheatColor;
    }

    private static (TireColor Border, TireGripEffect Effect, double FlashIntensity) GetSlipEffect(double slip)
    {
        double warnStart = GripWarningSlip - SlipBuffer;
        double warnEnd = GripWarningSlip + SlipBuffer;
        double critStart = LoseControlSlip - SlipBuffer;

        if (slip <= warnStart)
        {
            return (WhiteBorderColor, TireGripEffect.None, 0.0);
        }

        if (slip < warnEnd)
        {
            double fade = (slip - warnStart) / SlipBuffer;
            return (LerpColor(WhiteBorderColor, WarningBorderColor, fade), TireGripEffect.Warning, 0.0);
        }

        if (slip < critStart)
        {
            return (WarningBorderColor, TireGripEffect.Warning, 0.0);
        }

        double transition = Clamp01((slip - critStart) / (2 * SlipBuffer));
        TireColor border = LerpColor(WarningBorderColor, CriticalColor, transition);

        if (slip < LoseControlSlip)
        {
            return (border, TireGripEffect.Warning, 0.0);
        }

        return (border, TireGripEffect.Critical, transition);
    }

    private static TireColor LerpColor(TireColor from, TireColor to, double t)
    {
        t = Clamp01(t);
        return new TireColor(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static double Clamp01(double value)
    {
        if (value < 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }
}
