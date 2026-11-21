using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForzaOverlay.Tools.Snapshot;

/// <summary>
/// PNG 输出。用 WPF 自带的 <see cref="PngBitmapEncoder"/>，不引入新的 NuGet 包。
/// <para>
/// 输入是 D3D11 读回的 BGRA 排列（每像素 4 字节）。因为渲染目标用的是
/// <c>AlphaMode.Premultiplied</c>，这里必须先还原成**直通 alpha** 再交给 PNG，
/// 否则 tools 里的 <c>--flatten</c> 会把半透明像素算两遍、颜色偏暗。
/// </para>
/// </summary>
internal static class PngWriter
{
    public static void Save(string path, byte[] bgra, int width, int height)
    {
        byte[] straight = Unpremultiply(bgra);

        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            straight,
            width * 4);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        using System.IO.FileStream stream = new(path, System.IO.FileMode.Create);
        encoder.Save(stream);
    }

    /// <summary>
    /// 预乘 → 直通：每个颜色分量除以 alpha。
    /// 全透明像素（a=0）的颜色无从还原，直接给 0（PNG 里也不可见）。
    /// </summary>
    private static byte[] Unpremultiply(byte[] bgra)
    {
        byte[] output = new byte[bgra.Length];

        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i];
            byte g = bgra[i + 1];
            byte r = bgra[i + 2];
            byte a = bgra[i + 3];

            if (a == 0)
            {
                output[i] = 0;
                output[i + 1] = 0;
                output[i + 2] = 0;
                output[i + 3] = 0;
                continue;
            }

            if (a == 255)
            {
                output[i] = b;
                output[i + 1] = g;
                output[i + 2] = r;
                output[i + 3] = 255;
                continue;
            }

            float scale = 255f / a;
            output[i] = Clamp(b * scale);
            output[i + 1] = Clamp(g * scale);
            output[i + 2] = Clamp(r * scale);
            output[i + 3] = a;
        }

        return output;
    }

    private static byte Clamp(float value)
    {
        if (value <= 0f)
        {
            return 0;
        }

        return value >= 255f ? (byte)255 : (byte)value;
    }
}
