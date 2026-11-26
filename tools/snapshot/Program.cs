using ForzaOverlay.App.Rendering;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Learning;
using ForzaOverlay.Core.Telemetry;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ForzaOverlay.Tools.Snapshot;

/// <summary>
/// 把某一种仪表渲染到离屏 D3D11 纹理，读回内存后存成 PNG。
/// <para>
/// 为什么要有它：HUD 的正确性（齿环渐变、字标镂空、指针出界）编译器查不出来，
/// 只能看图。没有这个工具时看图必须启动游戏，改成「改一行 → 出一张图 → 量一遍」。
/// </para>
/// <para>
/// 技术路线（不新增 NuGet 包）：D3D11 纹理 → 复制到 staging → Map 读回 → PNG。
/// 未用 Vortice.WIC，因为那需要新增包。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        SnapshotOptions options;
        try
        {
            options = SnapshotOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            SnapshotOptions.PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            SnapshotOptions.PrintUsage();
            return 0;
        }

        try
        {
            Run(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("快照失败: " + ex);
            return 1;
        }
    }

    private static void Run(SnapshotOptions options)
    {
        if (!string.IsNullOrEmpty(options.Preset))
        {
            RunPreset(options);
            return;
        }

        RenderOne(options, options.BuildPacket(), options.OutputPath);
    }

    /// <summary>
    /// 预设批量模式：把所有边界用例各渲一张图，输出到同一目录。
    /// 这样一次就能覆盖速度极值、档位边界、增压正负端等容易画错的情况。
    /// </summary>
    private static void RunPreset(SnapshotOptions options)
    {
        System.IO.Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine($"预设 {options.Preset}：共 {SnapshotOptions.ExtremesCases.Count} 个用例 → {options.OutputDirectory}");

        foreach (SnapshotCase item in SnapshotOptions.ExtremesCases)
        {
            string path = System.IO.Path.Combine(options.OutputDirectory, item.Name + ".png");
            Console.WriteLine($"  {item.Name}  {item.Description}");
            RenderOne(options, SnapshotOptions.BuildPacket(item, options), path);
        }

        Console.WriteLine("全部完成。可用 tools/measure.py 或 read_image 逐张检查。");
    }

    private static void RenderOne(SnapshotOptions options, ForzaOverlay.Core.Telemetry.ForzaDataPacket packet, string outputPath)
    {
        int width = options.Width;
        int height = options.Height;

        // 视口就是输出画布本身：这样 widthRatio=1 时 scale 恰好等于 1，
        // 面板按基准尺寸 1:1 落在画布上，量出来的像素直接就是设计坐标。
        int viewportWidth = width;
        int viewportHeight = height;

        using ID3D11Device device = CreateDevice();

        Texture2DDescription colorDesc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        using ID3D11Texture2D colorTexture = device.CreateTexture2D(colorDesc);

        using ID2D1Factory d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>();
        using IDXGISurface surface = colorTexture.QueryInterface<IDXGISurface>();
        using ID2D1RenderTarget target = d2dFactory.CreateDxgiSurfaceRenderTarget(
            surface,
            new RenderTargetProperties
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied),
                Type = RenderTargetType.Hardware,
            });

        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        Vortice.DirectWrite.IDWriteFactory dwrite =
            Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>(
                Vortice.DirectWrite.FactoryType.Shared);
        try
        {
            target.BeginDraw();
            target.Clear(new Color4(0f, 0f, 0f, 0f));
            DrawScene(target, options, dwrite, viewportWidth, viewportHeight, packet);
            target.EndDraw();
        }
        finally
        {
            dwrite.Dispose();
        }

        byte[] bgra = ReadBack(device, colorTexture, width, height);
        PngWriter.Save(outputPath, bgra, width, height);
        Console.WriteLine($"已输出 {outputPath}  ({width}x{height})");
    }

    private static ID3D11Device CreateDevice()
    {
        Vortice.Direct3D.FeatureLevel[] levels =
        [
            (Vortice.Direct3D.FeatureLevel)45312, // 11.1
            (Vortice.Direct3D.FeatureLevel)45056, // 11.0
        ];

        var result = D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context);

        context?.Dispose();

        if (result.Failure || device is null)
        {
            throw new InvalidOperationException("D3D11CreateDevice 失败: " + result);
        }

        return device;
    }

    private static void DrawScene(
        ID2D1RenderTarget target,
        SnapshotOptions options,
        Vortice.DirectWrite.IDWriteFactory dwrite,
        int viewportWidth,
        int viewportHeight,
        ForzaDataPacket packet)
    {
        // 布局参数直接铺满视口，避免快照图里带位置偏移干扰测量
        HudModuleLayout layout = new() { X = 0, Y = 0, WidthRatio = 1.0 };

        // 学习器只读不写：传一个临时目录，避免污染程序目录下的 data\
        ShiftPointLearner learner = new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forza-snapshot-learner"));

        // 增压是否「存在」由调用方决定：预设里 -3bar 也是有效值（真空），
        // 只有显式给 --boost none 时才当作无增压车辆
        bool hasBoost = !options.NoBoost;

        // 内置字体集合（D-DIN + Orbitron + Rajdhani）：RingScene 靠它找 Rajdhani。
        // 快照工具的 bin 目录下有 Assets/Fonts（主程序 csproj 的复制项会带过来）。
        Vortice.DirectWrite.IDWriteFontCollection1? customFonts = LoadEmbeddedFonts(dwrite);

        switch (options.Style)
        {
            case "altezza":
            {
                using AltezzaScene scene = new(dwrite);
                scene.Draw(
                    target,
                    layout,
                    packet,
                    hasPacket: true,
                    learner,
                    options.MaxRpm,
                    hasBoost: options.Boost is not null,
                    useLearnedRedline: false,
                    viewportX: 0,
                    viewportY: 0,
                    viewportWidth: viewportWidth,
                    viewportHeight: viewportHeight);
                break;
            }

            case "nfs19":
            {
                using Nfs19Scene scene = new(dwrite);
                scene.Draw(
                    target,
                    layout,
                    packet,
                    hasPacket: true,
                    learner,
                    options.MaxRpm,
                    viewportX: 0,
                    viewportY: 0,
                    viewportWidth: viewportWidth,
                    viewportHeight: viewportHeight);
                break;
            }

            case "es":
            {
                using EsScene scene = new(dwrite);
                scene.Draw(
                    target,
                    layout,
                    packet,
                    hasPacket: true,
                    learner,
                    options.MaxRpm,
                    useLearnedRedline: false,
                    viewportX: 0,
                    viewportY: 0,
                    viewportWidth: viewportWidth,
                    viewportHeight: viewportHeight);
                break;
            }

            case "ring":
            {
                using RingScene scene = new(dwrite, customFonts);
                scene.Draw(
                    target,
                    layout,
                    packet,
                    hasPacket: true,
                    learner,
                    options.MaxRpm,
                    useLearnedRedline: false,
                    viewportX: 0,
                    viewportY: 0,
                    viewportWidth: viewportWidth,
                    viewportHeight: viewportHeight,
                    deltaSeconds: 0);
                break;
            }

            case "driftchain":
            {
                using DriftChainScene scene = new(dwrite, customFonts);
                scene.Draw(
                    target,
                    layout,
                    packet,
                    hasPacket: true,
                    learner,
                    options.MaxRpm,
                    useLearnedRedline: false,
                    viewportX: 0,
                    viewportY: 0,
                    viewportWidth: viewportWidth,
                    viewportHeight: viewportHeight,
                    deltaSeconds: 0);
                break;
            }

            default:
                throw new ArgumentException($"不支持的样式：{options.Style}（可用：altezza / nfs19 / es / ring / driftchain）");
        }
    }

    /// <summary>
    /// 把内置字体装进一个自定义集合，供 RingScene 找 Rajdhani。
    /// 与主程序 <c>HudScene.LoadEmbeddedFonts</c> 同一套做法；找不到字体文件时返回 null，
    /// 场景会回退到系统字体栈（不会崩，只是字形和原型对不上）。
    /// </summary>
    private static Vortice.DirectWrite.IDWriteFontCollection1? LoadEmbeddedFonts(
        Vortice.DirectWrite.IDWriteFactory factory)
    {
        try
        {
            string[] files = ["D-DIN-Bold.ttf", "Orbitron-Bold.ttf", "Rajdhani-SemiBold.ttf"];
            string dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Fonts");

            using Vortice.DirectWrite.IDWriteFactory5 factory5 =
                factory.QueryInterface<Vortice.DirectWrite.IDWriteFactory5>();
            using Vortice.DirectWrite.IDWriteFontSetBuilder1 builder = factory5.CreateFontSetBuilder();

            int added = 0;
            foreach (string name in files)
            {
                string path = System.IO.Path.Combine(dir, name);
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                using Vortice.DirectWrite.IDWriteFontFile fontFile = factory.CreateFontFileReference(path, null);
                builder.AddFontFile(fontFile);
                added++;
            }

            if (added == 0)
            {
                return null;
            }

            using Vortice.DirectWrite.IDWriteFontSet fontSet = builder.CreateFontSet();
            using Vortice.DirectWrite.IDWriteFactory3 factory3 =
                factory.QueryInterface<Vortice.DirectWrite.IDWriteFactory3>();
            return factory3.CreateFontCollectionFromFontSet(fontSet);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("内置字体加载失败（将回退系统字体）: " + ex.Message);
            return null;
        }
    }

    /// <summary>把渲染结果从显存读回 CPU 内存（BGRA 排列，每像素 4 字节）。</summary>
    private static byte[] ReadBack(ID3D11Device device, ID3D11Texture2D source, int width, int height)
    {
        Texture2DDescription stagingDesc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using ID3D11Texture2D staging = device.CreateTexture2D(stagingDesc);

        // ImmediateContext 是设备自己的对象，不要 Dispose，否则会把设备的上下文释放掉
        ID3D11DeviceContext context = device.ImmediateContext;
        context.CopyResource(staging, source);

        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            byte[] pixels = new byte[width * height * 4];
            int rowBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                IntPtr srcRow = mapped.DataPointer + (y * (int)mapped.RowPitch);
                System.Runtime.InteropServices.Marshal.Copy(srcRow, pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
