using System.Windows.Media;

namespace ForzaOverlay.App.Ui;

/// <summary>
/// 转速条统一配色：低于 <see cref="AmberStartRatio"/> 保持基础色，
/// 到 <see cref="AmberFullRatio"/> 到全黄并保持到 <see cref="RedStartRatio"/>，
/// 之后渐变到红，<see cref="RedFullRatio"/> 到全红。
/// 新增带转速条的面板一律调用本类，不要自己写阈值和颜色。
/// </summary>
public static class RpmColorScale
{
    public const double AmberStartRatio = 0.76;
    public const double AmberFullRatio = 0.82;
    public const double RedStartRatio = 0.90;
    public const double RedFullRatio = 0.94;

    public static readonly Color AmberColor = Color.FromRgb(0xFF, 0xB0, 0x20);
    public static readonly Color AmberDarkColor = Color.FromRgb(0xE0, 0x8A, 0x00);
    public static readonly Color RedColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>按转速比返回进度条颜色，黄使用标准 <see cref="AmberColor"/>。</summary>
    public static Color At(double ratio, Color baseColor)
    {
        return At(ratio, baseColor, AmberColor);
    }

    /// <summary>按转速比返回进度条颜色；渐变条左右两端各传各的基础色和黄色。</summary>
    public static Color At(double ratio, Color baseColor, Color amberColor)
    {
        return At(ratio, baseColor, amberColor, RedStartRatio);
    }

    /// <summary>
    /// 按学习到的红线起点比例着色：红区前 10% 为黄区（最佳扭矩区间），
    /// 进入红区立即变红；黄区与红区都不做渐变过渡。
    /// </summary>
    public static Color At(double ratio, Color baseColor, Color amberColor, double redlineRatio)
    {
        redlineRatio = Math.Clamp(redlineRatio, 0.45, 0.99);
        double yellowStart = Math.Max(0.25, redlineRatio - 0.10);

        ratio = Math.Clamp(ratio, 0.0, 1.0);

        if (ratio < yellowStart)
        {
            return baseColor;
        }

        if (ratio < redlineRatio)
        {
            return amberColor;
        }

        return RedColor;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }
}
