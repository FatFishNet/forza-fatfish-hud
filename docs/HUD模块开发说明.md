# HUD 模块开发说明

范围：**游戏内叠加层**的模块开发与视觉规范。当前 HUD 由 GPU 渲染（`Rendering/`），WPF 只负责设置窗口与布局编辑器。

> **本文的刚性边界**：渲染模型、布局模型、模块登记流程、性能预算、数值约定——这些必须遵守。
> **本文的柔性部分**：字号、面板尺寸、颜色浓淡——按实际观感调整，调整后回写第 3 节表格即可，不需要事先确认。

---

## 1. 渲染模型

```
游戏 UDP 包（约 60/秒）
   ↓ UdpTelemetryListener
   ↓ TelemetryPacketParser（长度必须 324，否则丢弃）
   ↓ BoundedChannel 容量 1，DropOldest
   ↓ AppServices.TryReadLatest（取最新一帧，旧帧直接丢弃）
   ↓ GpuOverlayWindow 渲染循环（D3D11 + Direct2D + DirectComposition）
   ↓ 刷新率节流（60 FPS / 30 FPS / 以游戏发包为准）
   ↓ HudScene 绘制全部 HUD 面板
   ↓ Present + Commit（DComp 合成到透明置顶窗口）
```

三条硬规则：

1. **只取最新帧**，不做队列积压——渲染慢不会导致数据越拖越旧
2. **HUD 隐藏时不绘制**，但渲染循环仍通过 `IdleTick` 持续读包，保证能重新判定显示条件
3. **数据驱动**：没有新包时「以发包为准」模式不重复提交，节省资源

### 1.1 渲染层职责

| 文件 | 职责 |
|---|---|
| `Rendering/GpuOverlayWindow.cs` | Win32 弹出窗口、D3D11/DXGI/DComp 初始化、渲染循环、Present、显隐（`SetVisible`） |
| `Rendering/HudScene.cs` | Direct2D 绘制全部面板、刷子/几何/文字布局缓存、D-DIN 字形轮廓 |

- 交换链：`CreateSwapChainForComposition`，FlipSequential，预乘 Alpha
- 窗口：`WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`
- 绘制目标：`ID2D1Factory1.CreateDxgiSurfaceRenderTarget`（D2D 1.0，硬件）
- 文字：`TextAntialiasMode = Grayscale`（透明背景上比 ClearType 清晰）

---

## 2. HUD 视觉规范

### 颜色（仅限叠加层）

| 用途 | 值 |
|---|---|
| 面板底色 | `#2C3440`，alpha 由「基础面板背景透明度」设置控制（默认 30%） |
| 标签文字 | `#AAFFFFFF`（约 67% 白） |
| 数值文字 | `#FFFFFF` / `#DDFFFFFF` |
| 次要文字 | `#88FFFFFF` / `#99FFFFFF` |
| 刻度、分隔线 | `#55FFFFFF` / `#59FFFFFF` |
| 条状背景 | `#22FFFFFF` / `#26FFFFFF` / `#33FFFFFF` |
| 悬挂填充 | `#7CF9FF`（正常）/ `#FFB020`（超过 82%） |
| 帧率条 | `#7CE6A0` |
| CPU 条 | `#FFD24D` |
| 内存条 | `#4DDAFF` |
| GPU 条 | `#B388FF` |

RGB 纯白黑为主，彩色只用于**数据本身**（条、指针、胎温），不做装饰。

### 字号

字号**不是固定值**，按「一眼可读性」分三级。级别由「阅读频率 × 注视距离 × 是否需要余光辨认」决定，不是由面板美观决定。

| 级别 | 字号范围 | 字重 | 适用 |
|---|---|---|---|
| **A 一眼可读** | 26–40 | Bold | 驾驶中频繁扫视、必须余光可辨的数字：档位、时速、圈速 |
| **B 快速扫读** | 18–26 | Bold | 需要主动看一眼才读的数字：转速、扭矩、马力、增压 |
| **C 标签与单位** | 10–14 | Normal | 单位、字段名、刻度、小标注 |

规则：

1. **数字优先放大，标签不必同步放大**——A 级数值配 C 级标签是常态，对比越强越易读
2. 同一面板内各级字号差距应 ≥ 1.5 倍，否则层级消失、扫读变慢
3. 修改字号后回写下面的表格，保持文档与实际一致

### 2.1 当前各模块字号（GPU 场景实际值）

| 模块 / 字段 | 级别 | 当前字号 | 字体 |
|---|---|---|---|
| SHIFT TREE · 档位 | A | 124 | Bahnschrift Bold |
| SHIFT TREE · 车速 | A | 63 | Bahnschrift Bold |
| SHIFT TREE · 转速 | A | 58 | Bahnschrift Bold |
| SHIFT TREE · RPM/KM/H 标签 | C | 18 | Bahnschrift SemiBold |
| 经典 · 速度 | A | 86 | Microsoft YaHei UI Bold |
| 经典 · 档位 | A | 58 | Microsoft YaHei UI Bold |
| 经典 · 转速 | B | 34 | Microsoft YaHei UI Bold |
| 经典 · 马力/扭矩/增压 | B | 19 | Microsoft YaHei UI Bold |
| 经典 · 标签/单位 | C | 12 / 15 | Microsoft YaHei UI |
| GHOST · 车速 | A | 118（字形轮廓） | D-DIN Bold |
| GHOST · 档位 | A | 50 | D-DIN Bold |
| GHOST · km/h / BOOST / bar | C | 9–13 | Bahnschrift / Consolas |
| 轮胎面板 · 胎温 | B | 18 | Microsoft YaHei UI Bold |
| 轮胎面板 · 标签/百分比 | C | 14 | Microsoft YaHei UI / Consolas |
| HUD 渲染资源占用 · 标题/数值 | C | 11 | Microsoft YaHei UI |
| NFS19 · 刻度标签 | C | 11.7（字形轮廓，缩放 0.98×1.05） | 内置手绘 SVG 路径 |
| NFS19 · RPM 标签 | C | 17.82 | Microsoft YaHei UI Bold |
| NFS19 · x1000 装饰 | C | 13.77 | Microsoft YaHei UI Bold |
| NFS19 · km/h | C | 17.82（与 RPM 同号） | Microsoft YaHei UI Bold |
| NFS19 · BOOST 标签 | C | 15 | Microsoft YaHei UI Bold |
| NFS19 · −/+ 符号 | B | 28 | Microsoft YaHei UI Bold |
| NFS19 · 时速 | A | 七段字形（scale 1.0545，段外扩 0.3） | 内置七段 SVG 路径 |
| NFS19 · 档位 | A | 七段字形（scale 1.02，不外扩） | 内置七段 SVG 路径 |

### 2.2 字体策略

- **数字字体**：SHIFT TREE / 经典用 Bahnschrift（系统字体，只引用名字，不打包）；GHOST 用内置 **D-DIN**（`Assets/Fonts/D-DIN-Bold.ttf`，OFL 授权）
- **中文字体**：Microsoft YaHei UI（系统字体，只引用名字）
- **等宽数字**：SHIFT TREE 的转速/速度采用**逐位固定槽宽**（33 / 36），GHOST 车速按 63 槽位，防止比例字体读数抖动
- **D-DIN 加载**：`HudScene` 用 `IDWriteFactory5` 自定义字体集合加载，加载失败自动回退 Bahnschrift
- **字体必须随程序分发**：内置字体放 `Assets/Fonts/`，并在 `Assets/Fonts/LICENSE.txt` 记录来源与授权
- **不要打包 Windows 自带字体**（Bahnschrift / Consolas），只能引用字体名

### 2.3 描边与阴影

- GHOST 车速数字：`GetGlyphRunOutline` 生成字形轮廓 → `DrawGeometry` 2px 描边（`#9904070B` 半透明）+ `FillGeometry` 白色填充
- 档位数字：同样用字形轮廓 + 斜切（-12°）
- 阴影已移除（用户要求）；需要时用偏移填充模拟

### 2.4 面板

- 背景：纯色 `#2C3440` + 透明度设置，圆角 **8**
- 内边距 **8–12**，行距 4
- 不描边、不加投影（GHOST 档位板除外，有切角与青色描边）

---

## 3. 布局模型

所有模块位置以**归一化坐标**存储，与实际分辨率无关：

| 字段 | 含义 | 范围 |
|---|---|---|
| `X` | 左上角横坐标 ÷ 画面宽 | 0–1 |
| `Y` | 左上角纵坐标 ÷ 画面高 | 0–1 |
| `WidthRatio` | 模块宽 ÷ 画面宽 | 0.03–1.0 |

- 高度不单独存，按模块**基础宽高比**换算，保证缩放不变形
- 每种画面比例（16:9、21:9、32:9、48:9、16:10、自定义）各存一套布局
- 绘制时以**游戏客户区**为基准：`scale = WidthRatio × 客户区宽 / 基准宽`，并加上客户区左上角偏移

当前模块：行驶仪表 · 经典 820×150、GHOST LADDER 420×238、SHIFT TREE 820×320、仿NFS19 601×416、咬地鲨 1054×722、ES 500×500、极简弧环 312×330、漂移锁链 304×289、轮胎与悬挂 220×212、HUD 渲染资源占用 200×140。
八种仪表样式由设置项 `InstrumentStyle` 八选一（经典 / GHOST LADDER / SHIFT TREE / 仿NFS19 / 咬地鲨 / ES / 极简弧环 / 漂移锁链），各自独立保存布局。

---

## 4. 现有模块

| 模块 | GPU 绘制方法（`HudScene`） | 基础尺寸 | 布局属性 | 默认开关 |
|---|---|---|---|---|
| 行驶仪表 · 经典 | `DrawClassicPanel` | 820 × 150 | `PerformanceLayout` | 开 |
| 行驶仪表 · GHOST LADDER | `DrawGhostPanel` | 420 × 238 | `FestivalLayout` | 开 |
| 行驶仪表 · SHIFT TREE | `DrawShiftTree` | 820 × 320 | `ShiftTreeLayout` | 开 |
| 行驶仪表 · 仿NFS19 | `Nfs19Scene.Draw` | 601 × 416 | `Nfs19Layout` | 开 |
| 行驶仪表 · 咬地鲨 | `AltezzaScene.Draw` | 1054 × 722 | `AltezzaLayout` | 开 |
| 行驶仪表 · ES | `EsScene.Draw` | 500 × 500 | `EsLayout` | 开 |
| 行驶仪表 · 极简弧环 | `RingScene.Draw` | 312 × 330 | `RingLayout` | 开 |
| 轮胎与悬挂面板 | `DrawTirePanel` | 220 × 212 | `TireLayout` | 开 |
| HUD 渲染资源占用 | `DrawPerfPanel` | 200 × 140 | `PerfLayout` | 关 |

---

## 5. 新增一个 HUD 模块

五处改动，缺一不可。

### ① 配置模型

`Core/Configuration/HudLayoutProfile.cs`：

```csharp
public HudModuleLayout BoostLayout { get; set; } = HudLayoutDefaults.BoostDefault();
```

`AppSettings.cs` 加开关（可开关的模块才需要）：

```csharp
public bool ShowBoostPanel { get; set; }
```

### ② 默认尺寸与位置

`Core/Configuration/HudLayoutDefaults.cs`：

```csharp
public const double BoostWidth = 240;
public const double BoostHeight = 96;

public static HudModuleLayout BoostDefault()
{
    return new HudModuleLayout { X = 0.0125, Y = 0.15, WidthRatio = 0.1250 };
}
```

### ③ 登记到模块清单

`App/Ui/HudModuleCatalog.cs`：

```csharp
new()
{
    Name = "增压表",
    BaseWidth = HudLayoutDefaults.BoostWidth,
    BaseHeight = HudLayoutDefaults.BoostHeight,
    AccentColor = "#FFB39DDB",
    FillColor = "#33B39DDB",
    IsVisible = static settings => settings.ShowBoostPanel,
    Read = static profile => profile.BoostLayout,
    Write = static (profile, layout) => profile.BoostLayout = layout,
},
```

登记后**布局编辑器自动出现该模块的拖拽框**，不需要改编辑器代码。

### ④ GPU 绘制（`Rendering/HudScene.cs`）

1. `Render` 中按开关调用：
   ```csharp
   if (settings.ShowBoostPanel)
   {
       DrawBoostPanel(target, settings, viewportX, viewportY, viewportWidth, viewportHeight);
   }
   ```

2. 新增绘制方法，**必须**遵循：
   - 读 `settings.ActiveLayout.BoostLayout`（X/Y/WidthRatio）
   - `scale = WidthRatio × viewportWidth / BaseWidth`，`left = viewportX + X × viewportWidth`，`top = viewportY + Y × viewportHeight`
   - `target.Transform = Matrix3x2.CreateScale(scale) × Matrix3x2.CreateTranslation(left, top)`
   - 绘制结束后 `target.Transform = Matrix3x2.Identity`
   - 刷子通过 `GetBrush(target, color)` 缓存，不要每帧 `CreateSolidColorBrush`
   - 静态几何用 `DrawArc(..., cache: true)` / `_arcCache` 缓存
   - 每帧文字用 `DrawText` / `DrawCachedText`（文字不变的用后者）

### ⑤ 设置界面开关

`SettingsWindow.xaml` 的「HUD 模块」分区加一行，
`SettingsWindow.xaml.cs` 的加载与保存各加一行：

```csharp
BoostSwitch.IsChecked = _settings.ShowBoostPanel;
_settings.ShowBoostPanel = BoostSwitch.IsChecked == true;
```

### ⑥ 字体（模块使用自定义字体时）1. 字体文件放入 `src/ForzaOverlay.App/Assets/Fonts/`，csproj 配置 `CopyToOutputDirectory`
2. 通过 `CreateFormat(..., collection)` 用自定义字体集合加载，失败回退系统字体
3. 在 `Assets/Fonts/LICENSE.txt` 记录字体名、来源、许可证、日期、是否子集化
4. 确认字体随 `Publish.bat` 进入发布包

### ⑦ 转速条配色（面板带转速条时）

转速条颜色一律调用 `App/Ui/RpmColorScale`，不要自己写阈值或黄红颜色：

```csharp
System.Windows.Media.Color color = RpmColorScale.At(ratio, baseColor);
```

- `< 76%` 保持基础色，`76%~82%` 渐变到黄 `#FFB020`，`82%~90%` 保持全黄，`90%~94%` 渐变到红 `#FF3B30`，`≥ 94%` 全红
- 分段条按格中心比例 `(i + 0.5) / 段数` 取色
- 改阈值只改 `RpmColorScale`
- **例外**：SHIFT TREE 的转速弧按设计稿固定三段（< 73.3% 青、73.3%~86.7% 黄、≥ 86.7% 红）
- **例外**：仿NFS19 使用设计稿自带配色（刻度 `#C4EDFF`、红区 `#FF3A2F`、字形 `#BDE8FF`、指针热渐变），**不接 `RpmColorScale`**——该面板整体按参考图还原，属于独立视觉体系
- **例外**：咬地鲨按实车三色制（奶油白 `#EBD2B2` / 金 `#D5AD4A` / 红 `#D91A24`），红色只用于指针与转速红区，同样**不接 `RpmColorScale`**

---

## 5.1 仿NFS19（独立视觉体系）

原型：`temp/panel-speedo-nfs19/`（**最终版，不再改动**）。字形与几何全部内嵌为 C# 常量，不读外部 JSON。

### 字形

- **不是字体**：手绘 SVG 路径（描边中心线），运行时解析成 `ID2D1PathGeometry` 后缓存，用 `DrawGeometry` + round cap/join 描边
- 刻度标签字形：`Nfs19GlyphCache.DialGlyphPaths`（10 个，字箱 20×16，advance 23，描边 4.4）
- 时速/档位七段：`Nfs19GlyphCache.SegmentPaths`（a–g 七段）+ `SegmentMap` 段映射
- **时速加粗**：亮段在 `FillGeometry` 之后再叠一笔同色 `SpeedSegmentInflate = 0.3` 描边（段面坐标，两侧各外扩 0.15，段厚 3 → 3.3）。七段最近缝隙为 1.387 段面单位，描边两侧各外扩一半，所以 **`SpeedSegmentInflate` 上限约 0.8**：0.6 时缝隙只剩 0.79（几乎粘连）、0.3 时回到 1.09。**档位不加粗**（保持 3.0）
- 档位按**墨宽**排版（`GearInk`：`'1'` 占 22–25、`'-'` 占 3–24，gap 4），否则「1 档」与「10 档」会左右跳
- SVG 解析器 `BuildGeometry` 只支持 M/L/H/V/Q/Z（原型仅用到这五个）

### 刻度与标签

- 主表：圆心 (236,237)、扫掠 235°→486°（251°）
- **大刻度每 1000 转、小刻度每 500 转**，按转速值定位，不用等分格数——量程非整千时等分会让刻度与标签错位
- 标签是**千转整数**（`0 1 2 … N`），配合盘面中央 `x1000` 读作「×1000 转」；`x1000` 是**必须保留的装饰**
- 红色：刻度值 ≥ 红区起点即转 `#FF3A2F`

### 增压小表

- 整组 `translate(-80,-74) scale(1.44)`，中心 (128,296)、半径 58、±120°，0 在正上
- 16 等分 / 每 4 格一主格；两端各 2 格标红
- 指针角度 `-120 + boost×120`，数据取 `BoostBar`（已含 psi→bar 换算）

### 数据映射

| 显示项 | 来源 | 兜底 |
|---|---|---|
| 转速量程 | `EngineMaxRpm`（> 1 才用） | 8000 |
| 指针角度 | `235 + clamp(CurrentEngineRpm / maxRpm, 0, 1) × 251` | 0° |
| 红区起点 | `ShiftPointLearner.GetRedlineRatio` | `0.75 × maxRpm` |
| 时速 | `Math.Round(SpeedKmh)`，clamp 0–999，三位补零 | 全灭 |
| 档位 | `-1→R`、`0→N`、`1~10` 数字、`11→保持上一帧` | `N` |
| 增压 | `BoostBar`，clamp −1..1 | 0 |

### 已知限制

- 辉光（原型用 `feGaussianBlur`）尚未实现：当前只有 `ID2D1RenderTarget`、无 `ID2D1DeviceContext`，`CreateEffect` 不可用；需用叠层描边近似
- 几何缓存按 `(maxRpm, redlineRpm)` 分档，换车时重建一次

---

## 5.2 咬地鲨三表（独立视觉体系）

原型：`temp/panel-cluster-altezza/`（HTML 演示 + `lexus-wordmark.svg` 字标）。几何全部内嵌为 C# 常量，不读外部文件。

### 基准尺寸与坐标

- 原型画布 **1440×720**，图形实际包围盒 **1043.4×711.4**（左界转速表左沿、右界速度表右沿、上下界速度表上下沿；所有文字都在圆内，不越界）
- 对外基准 = 包围盒四边各加 5px 后向上取整 → **1054×722**
- 内部坐标仍按原型 1440×720 书写，用 `ContentShiftX = -194.1`、`ContentShiftY = 0.7` 平移吸收差异，让内容左上角落到 `(5,5)`
- 平移必须放在 `Scale` **之前**相乘（设计坐标单位跟缩放走），与仿NFS19 同一套矩阵顺序

### 图层顺序（不能改）

```
转速表盘 → 档位液晶 → 速度表盘（齿环 → 银盘 → 双圈数字/圆点 → 单位 → LEXUS）
→ 三小表（盘 + 刻度 + 数字 + 图案 + 轴心）
→ 三小表指针 → 转速长指针 → 速度长指针
```

**指针必须压在全部表盘之上**，否则银盘与小表底盘会把针身拦腰盖住（原型里标了两遍的坑）。
轴心暗底与外环留在表盘层、指针之前画，这样深色粗外环不会把针身遮断一段。

### 几何要点

| 部件 | 常量 |
|---|---|
| 转速表 | 圆心 (471.1, 412.9)、R=272、0 位 126.5°、扫掠 157.1° |
| 速度表 | 圆心 (886.8, 360)、R=354.2、0 位 139.3°、扫掠 267.1° |
| 银盘 | `rDisk = 0.680R`，外沿再接 90 颗梯形齿（`0.680R → 0.7292R`，齿宽占周期 41%） |
| 外圈 MPH | 0–220，每 20 一个数字（字号 46）；数字之间每 10 mph 一个 Ø16 奶油白圆点 |
| 内圈 km/h | 0–360，每 20 一个金色数字（字号 36）；数字之间每 10 km/h 一个 Ø5 金点 |
| 小表 | R=84.8，0 在正下方、顺时针扫 180°；三表圆心 (890, 221.1) / (1027.7, 358.1) / (889.6, 496.2) |
| 档位液晶 | 中心 (468.5, 560)、100×94，双格；1~9/N/R 只占右格，两位档位时左格才亮 |
| LEXUS 字标 | 中心 (754.2, 362.3)、151.9×21.4，描摹轮廓，宽高比 7.10 系统字体排不出来 |

- 齿环与银盘共用一条 `userSpaceOnUse` 渐变轴（`(723.0,138.4) → (1050.6,581.6)`）。若齿各自按百分比取渐变，每颗齿会独立映射整条渐变、深浅不一
- LEXUS 字标用 `FillMode.Alternate` 填充，否则 E 的内部镂空会被填死
- 文字顺时针旋转 `angle + 90°`；底部缺口里的两个单位标签改用 `angle - 90°`（字头朝外）

### 转速表刻度与指针（2026-11-14 定稿）

| 项 | 规格 |
|---|---|
| 笔帽 | **平头**（`FlatStroke`），不是圆头 |
| 刻度步进 | 每 250 转一根，千转为大刻度（`i % 4 == 0`） |
| 小刻度 | 宽 2.4，`0.960R → 内端`，长度 ×0.88（缩短 12%） |
| 大刻度 | 宽 **6.48**（5.4 × 1.2）、长度 **×0.7**（缩短 30%） |
| 指针长度 | **恰好扫到大刻度一半**，由公式推导 |

**为什么用平头**：圆头笔帽会在刻度两端各鼓出半个线宽，大刻度加粗后尤其明显，
红区那几根看起来像一串圆珠（用户原话「糖葫芦」），平头才是一条直线。

**缩短必须只动内端**：外端固定 `0.960R` 贴住盘沿，把内端往外推。
两端都缩会让刻度脱离盘沿、看起来悬空。

**指针长度不要写死数字**，用推导值
`(大刻度内端 + TachoTickOut) / 2` —— 以后改刻度长度时指针会自动对齐。
当前值：大刻度 `0.8816R~0.960R`，一半处 `0.9208R`。

### 数据映射

| 显示项 | 来源 | 兜底 |
|---|---|---|
| 转速量程 | `EffectiveMaxRpm()`，按千转向上取整（7500→8、8200→9），夹在 4–16 千转 | 8000 |
| 指针角度 | `126.5 + clamp(CurrentEngineRpm / spanRpm, 0, 1) × 157.1` | 0° |
| 红区起点 | `ShiftPointLearner.GetRedlineRatio`（需开启红区校正）；关闭时回退固定 6.0 千转 | `0.75 × spanRpm` |
| 速度指针 | `SpeedKmh / 1.609344` 落在 0–220 mph 的 267.1° 扫掠上 | 0 |
| 档位 | `-1→R`、`0→N`、`1~10` 数字、`11→保持上一帧` | `N` |
| 增压小表 | `BoostBar`，clamp 0–3；`BoostPresence` 判定为自吸车时归零 | 0 |
| 水温小表 | 由速度联动推导：`TempAngleAtDefault + (mphRatio × 267.1) / 5` | 190°（100℃） |

### 装饰小表（无数据源）

遥测包只有速度、转速、档位、增压、油量、四轮胎温，**没有电压与水温**。两块表按用户要求保留为装饰表：

**电压表**（纯装饰）

- 静止基准 13.8V，每 2.2–6.8 秒换一个随机目标值，用 180 ms 时间常数平滑追上去
- **不要改成每帧随机抖动**——那样看起来像仪表坏了

**水温表**（与速度联动，2026-11-14 定稿）

- 刻度定义：**C = 0℃、H = 180℃**，线性映射到 90°~270°（C 在正下方、H 在正上方）
- 默认停在 **100℃**。角度换算：`90 + (100 − 0) / (180 − 0) × 180 = 190°`
  —— 注意**不是 180°**，180° 是 90℃ 的中点
- 联动：速度表每顺时针转 **5°**，水温表转 **1°**；减速回摆，**允许超出 C~H 量程**
  公式：`tempAngle = TempAngleAtDefault + (mphRatio × 267.1) / 5`
- 实现要点：走 `DrawSubNeedleAtAngle` 直接按角度画，**不能**用 `DrawSubNeedle`
  （那里的 `ratio` 被夹在 0~1，超转会被截断）
- 端点温度写成常量 `TempMinCelsius` / `TempMaxCelsius` / `TempDefaultCelsius`，
  角度由 `TempAngleAtDefault` 公式推导 —— 改温度定义不用重算角度

### 已知限制

- 原型用 `feGaussianBlur` 做柔光；当前只有 `ID2D1RenderTarget`、无 `ID2D1DeviceContext`，`CreateEffect` 不可用，本轮未实现辉光
- 齿环用 90 段合批成一条几何，只建一次；表盘渐变画刷按表缓存，每帧零分配

---

## 5.3 ES 数字转速盘（独立视觉体系）

原型：`temp/es.html`（单文件 HTML 演示）。几何全部内嵌为 C# 常量，不读外部文件。
代码：`Rendering/EsScene.cs`。

### 基准尺寸与坐标

- 原型画布 **1000×1000**，圆心 (500,500)
- 对外基准 **500×500**
- **缩放系数是 0.47，不是 0.5**：原型 `es.html` 里 `.wrap { width: min(94vmin, 1000px) }`，
  SVG 只占画布的 94%，所以 viewBox 里的 r438 实际画在 r411.7 上。
  系数 = 0.94（wrap）× 0.5（1000→500）= **0.47**
  - 实测验证：原型 1000px 图上金属环描边跨 r393~430，与 (438±21)×0.94 吻合
  - 按 0.5 换会整体大 6.3%
- **圆心固定在 (250,250)，不要写 `500 × Scale`**
  - 后者得到 235，整个表盘偏左上 15px 且右侧溢出（实测圆心跑到 (234.5,234.5)）
  - `Scale` 只作用于半径与字号，**不作用于画布坐标**
- 与咬地鲨不同：**不需要 `ContentShift` 平移**

### 角度制（与咬地鲨同系，勿与仿NFS19/SHIFT TREE 混用）

`0° = 正右`、y 轴向下、顺时针；`PointOn` 用 `cos/sin`。

### 核心映射

```
angle = 90 + rpm_ratio × sweepDeg
sweepDeg = (spanRpm / 1000) × 36
```

- 转速 0 在 **90°（正下方）**，每千转 **36°**
- `spanRpm` 由 `ResolveTachoSpan` 决定：按千转向上取整、夹在 4~16
  - 6800 → 7 千转（252°）；8000 → 8 千转（288°）；9000 → 9 千转（324°）
- **注意 `ThousandDeg = 36°` 与 `TickStepDeg = 18°` 是两个不同的量**：
  刻度每 18° 一根、数字每千转 36°。混用会让 0~8 全部挤在半圈里（踩过一次）

### 图层顺序

```
盘面 → 刻度 → 点阵 → 红区带 → 转速数字 → 紫带 → 中央信息 → 指针
```

指针必须最后画，压在全部表盘元素之上；轴心盖画在旋转之外、不随指针转。

### 几何常量（基准 500×500，均为原型值 × 0.47）

| 项 | 原型值 | 500 基准 |
|---|---|---|
| 金属外环 | r438，描边 42 | r205.9，描边 19.7 |
| 盘面 | r409 | r192.2 |
| 刻度 | 长 412→460 / 短 424→450 | 长 193.6→216.2 / 短 199.3→211.5 |
| 点阵 | 6 层 r342~406（层距 12.8），点半径 1.7 | r160.7~190.8，点半径 0.80 |
| 转速数字 | 环半径 354，字号 78 | r166.4，字号 36.7 |
| 红区带 | r438 描边 42 | 同外环 |
| 紫带 Sport S | 26°~82°，文字在带中线 | 文字半径 = `BezelR` |
| 车速 | y357，字号 158 | y167.8，字号 74.3 |
| km/h | y462，字号 30 | y217.1，字号 14.1 |
| 档位 | y610，字号 96 | y286.7，字号 45.1 |
| 指针 | (520,500)→(888,500)，轴心 r11 | (244.4,250)→(417.4,250)，轴心 5.2 |

> **点半径别用 CSS 里的 2.1**。实测原型 1000px 图点的中位直径 3.19px，
> 反推 viewBox 半径 = 1.70；CSS 的 r=2.1 含描边与抗锯齿会偏大，照抄会糊成一团。

### ⚠️ 点阵配色是明确例外，不走 `RpmColorScale`

点阵只有**两态**：正常青 `#1fb9cc`（不透明度 0.55）→ 红区 `#ff3b18`（0.92）。

**不经过黄色**，与 §2 视觉规范的三段式不同。这是用户 2026-11-21 明确要求的，
属于继 SHIFT TREE、咬地鲨之后的**第三个例外**。看到这里不要「顺手改成规范」。

### ⚠️ 三处已知技术细节

1. **辉光用多层描边伪造**：原型靠 `feGaussianBlur(70px)`，本项目无 `ID2D1DeviceContext`，
   所以用递增透明度的宽描边叠加。效果是「边缘发亮」而不是「弥散光晕」，用户已接受。
   **外扩量必须收在画布内**：柔光若按 CSS 的 r452 再加粗描边会冲到 r248、几乎顶满 500 画布，
   整幅图比原型大 8.9%（实测踩过）。现在每层只外扩 2.5px、描边用 `BezelWidth × 0.6`。
2. **`Matrix3x2` 是行向量约定**：`A * B` 表示「先 A 后 B」，绕点旋转必须写
   `T(-C) * R * T(+C)`。写反了指针会被甩出画布，**编译零错误、运行不报错，只是画面少了东西**。
3. **沿弧排文字的角度是推导出来的**，不是试出来的：字顶要朝圆心，解得 `θ = a - 90°`。
   若写成 `a + 90°` 会得到背离圆心的倒字。

### ⚠️ 金属环渐变要沿对角线外扩

原型 `bezel` 渐变是 `x1=0 y1=0 → x2=1 y2=1`，**相对整个包围盒**。
直接拿它给细圆环描边用，环带只覆盖渐变中段的一小截，画出来是近乎纯色的环、没有金属明暗。
必须把渐变轴沿对角线拉长到环带两端之外（各外扩一个环宽），环带才从左上亮部吃到右下暗部。

### ⚠️ 点阵透明度按实测取，别照抄 CSS 的 0.55

原型点阵区青色像素峰值是 `(89,174,187)`，接近 `#1fb9cc` 满值。
用 0.55 会明显偏暗、点几乎看不见；本项目用 **0.85**。

### 档位与数值

- 档位文案复用 `GearTextFormatter` 规则：`-1→R`、`0→N`、`1~10` 原样、`11` 保持上一帧
- 车速走 `HudDraw.Truncate`（项目统一口径：截断，不四舍五入）
- 红区接 `ShiftPointLearner.GetRedlineRatio`，关闭红区校正时回退 `spanRpm × 0.75`

### 已知限制

- 原型 `Sport S` 用 `textPath` 沿弧排；这里是逐字旋转摆放，字距按角度均分近似，
  极长文本会与弧线有细微偏离
- 点阵几何按 `(spanRpm, redlineRatio)` 缓存，换车或红区更新时重建一次

---

## 5.4 极简弧环（FH4 极简语言）

原型 `temp/极简弧环.html`（从 `design/temp/地平线HUD仪表.html` 拆出，只保留 draw1）。

### 基准与坐标

| 项 | 原型值 | 本项目 |
|---|---|---|
| 画布 | 380 × 380 | **312 × 330**（图形包围盒 302×320 + 四边 5px） |
| 圆心 | (190, 176) | (156, 156) —— 即原型坐标 + `ContentShift(-34, -20)` |
| 主弧半径 | 148 | 148（**1:1，不缩放**） |
| 主弧线宽 | 6 | 6 |
| 增压弧半径 | 128 | 128 |

内部坐标直接写原型值，靠 `ShiftX = -34` / `ShiftY = -20` 平移吸收；
**不要**给半径乘任何系数 —— 这是唯一一套 1:1 搬过来的表，量图像时像素即设计坐标。

### 角度制

与咬地鲨 / ES 同系（SVG 制）：`0° = 正右`、y 轴向下、`PointOn` 用 `cos/sin`。
起始角 **135°**（左下），扫掠 **270°**，与原型 `A0 = π*0.75`、`SW = π*1.5` 一致。
**不要**用仿NFS19 / SHIFT TREE 的 `sin/-cos`（0° 正上），混了整条弧会偏。

### 图层顺序

内侧淡圈 → 底轨 → 值弧（含红区辉光）→ 刻度 → 数字 → 静态标签 → 增压弧 → 档位/车速。

### 配色（`RpmColorScale` 的第四个例外）

原型规定超红区后整条弧变紫 `#c86bff` 并呼吸辉光，不走黄→红：

| 状态 | 颜色 |
|---|---|
| 正常 | 白 `#ffffff` |
| 超红区（`rpmRatio > redlineRatio`） | 紫 `#c86bff` + 多层描边伪造辉光 |
| 红区起点标记点、红区刻度与数字 | 红 `#ff3b5c` |
| 增压真空段（负压） | 灰 `#7a8194` |
| 增压正压段 | 紫 `#9d6bff` |

已有三个例外（SHIFT TREE、咬地鲨、ES），这是第四个，**不要**给这套表套 `RpmColorScale`。

### 量程与红区

- 转速量程按千转向上取整、夹 4~16（与 ES / 咬地鲨各自维护一份）
- 刻度数 = 数字间隔数 × 2，保证「大刻度永远正对数字」
- 红区接 `ShiftPointLearner.GetRedlineRatio`；关闭校正或没有包时回退 **75%**
- 红区起点在弧上画一个 2.5px 红点

### 增压弧

- 量程 **-1.0 ~ 3.0 bar**（0 落在 25% 处，`BoostZeroFraction = 0.25`）
- **负值是真实物理量**（进气歧管真空），必须画出来，不要 clamp 到 0
- 从零位向两侧生长：负压往左画灰、正压往右画紫
- **不判断有无增压车**（用户明确要求）：始终按 `packet.BoostBar` 画

### 增压读数格式（2026-11-26 定的项目级标准）

- **恒两位小数**，`0` 也显示成 `+0.00`；**正数显式带 `+`**（`+0.42` / `-0.35` / `+3.00`），字符数恒为 5
- **整行三段（`BOOST` / 数字 / `bar`）按实测宽度拼接后整组居中**，数字段内部逐格定槽：
  符号格取 `+`/`-` 里较宽者、数字格取 0~9 里最宽者、小数点单独一格。
  这样数值怎么变，数字块总宽恒定，`BOOST` 与 `bar` 不会跟着左右挪
- 三个文字块的宽度在**构造期**用 `HudDraw.MeasureAdvance` 量一次存起来，**每帧不再排版**（热路径禁止分配）
- 格式化一律走 `HudDraw.Signed()`：内部先消 float 浮点误差再截断。
  遥测 `BoostBar` 是 float，`-0.35` 实际存成 `-0.34999999…`，直接截断会显示 `-0.34`（出图实测发现）
- 同一标准适用于 GHOST（`Consolas` 本身等宽，只需换格式函数）与经典仪表「增压」行（右对齐列逐格落位）

### 字体

- 内置 **Rajdhani SemiBold**（OFL 1.1），跟 D-DIN 一起装进同一个自定义字体集合
- 原型 CSS 里还引了 Orbitron，但 `draw1` 从头到尾没用过它 —— 所有文字都是默认 Rajdhani
- 字体文件在 `Assets/Fonts/`，`HudScene.LoadEmbeddedFonts` 一次性加载三个文件

### 换挡闪光

检测档位变化（只认 1~10）→ `_flash = 1`，按 `3.2/s` 衰减（约 0.31 秒）：

- 档位数字放大 `96 × (1 + flash × 0.12)`
- 底下垫一层放大 1.06 倍的紫色半透明字当辉光
- 档位右侧画方向箭头（升挡朝上紫、降挡朝下灰）

**放大必须用 `Matrix3x2.CreateScale(scale, center)`**，不要每帧重建 `IDWriteTextFormat`
——换挡那几帧恰好在渲染热路径上，持续分配会拖帧。

### 已知限制

- 原型用 CSS `shadowBlur` 做柔光，D2D 没有模糊，用「更宽 + 更低透明度」的多层描边伪造
- 呼吸辉光的相位取 `Environment.TickCount64`，快照工具是静态图，看不到闪烁

---

## 5.5 漂移锁链（FH5 风格）

与 §5.4 极简弧环**同批不同款**（原稿 6 款里的第 5 款），原型 `temp/漂移锁链.html`。
两者结构相似但细节差很多，**不要照抄极简弧环的参数**。

### 基准与坐标

| 项 | 值 | 与极简弧环的差异 |
|---|---|---|
| 原型画布 | 380 × 380 | 相同 |
| 圆心 | (190, **184**) | 极简弧环是 176 |
| 主弧半径 | **140** | 极简弧环是 148 |
| 主弧线宽 | **9** | 极简弧环是 6 |
| 图形包围盒 | **294 × 279** | 增压读数上移 20px 后变矮 |
| **对外基准** | **304 × 289** | 包围盒 + 四边 5px |
| ContentShift | **(-40, -36)** | 圆心落在 (150, 148) |
| 增压弧半径 | **108** | 极简弧环是 128 |
| 增压弧角度 | **59.4° ~ 120.6°** | 极简弧环是 57.6° ~ 122.4° |

**内部坐标仍是原型原值 1:1，不缩放**，靠 `ContentShift` 平移吸收。

### 转速弧是逐段插值，不能用渐变画刷

原型把整条弧切成 **48 段**，每段独立取色，色相从 **268（紫）线性过渡到 185（青）**：

```js
const hue = lerp(268, 185, f0);
ring(..., `hsl(${hue},90%,64%)`, "butt");
```

- **不能用线性渐变画刷** —— 渐变画刷沿直线插值，画在圆弧上会出现「同角度不同色」的偏移。
  逐段画是唯一能 1:1 还原的做法
- 每段末端 **+0.006 弧度**是原型原值，用来盖住相邻段的抗锯齿缝隙，否则弧上出现细黑纹
- 色相按**整段中点**取（不是起点），否则尾段会偏紫
- 用 `butt` 端帽而不是 `round`，否则每段两端会鼓出来

### 红区用斜纹，不是改色

从红区起点到 270° 末端，每 **0.035 弧度**画一根短横线，跨在主弧两侧（`R ± 9`），
颜色 `rgba(255,80,120,.55)`、线宽 2。

### 刻度与数字的透明度陷阱

原型 `ticks()` 里除了颜色**还乘了一层全局透明度**：

| 元素 | 原型颜色 | 乘的 alpha | 实际 |
|---|---|---|---|
| 小刻度 | `rgba(255,255,255,.5)` | ×0.40 | **0.20** |
| 大刻度 | `rgba(255,255,255,.75)` | ×0.95 | **0.7125** |
| 红区刻度 | `#ff3b5c` | ×0.40 / ×0.95 | 随大小刻度 |
| 刻度数字 | `#ffffff` / 红区 `#ff5078` | ×0.60 | — |

**第一版直接用了 .5/.75 没乘，出图比对发现刻度明显比原型亮一档。**
注意红区刻度的红是 `#ff3b5c`，红区数字的红是 `#ff5078` —— 原型用了两种红，不是同一个。

### 与极简弧环的其它差异

- **没有 `GEAR` 字样**：原型只有「×1000 r/min」，档位数字直接压在圆心上方
- 换挡放大是 `84 × (1 + flash × 0.1)`（极简弧环 96 / 0.12）
- 换挡辉光是**青** `#4de3ff`（极简弧环是紫 `#c86bff`）
- 速度数字 `44px`、槽宽 **22**（极简弧环 46px / 23）
- 增压正压段是**青** `#4de3ff`（极简弧环是紫 `#9d6bff`）

### 量程与红区

- 转速量程按千转向上取整、夹 4~16（**不是**原型写死的 9000）—— 原型固定量程会让
  8 千转的车刻度只走到 88%
- **刻度数必须跟着量程走**：`tickCount = 千转数 × 2`（大刻度每 2 格一条），
  **不能照抄原型的 `ticks(n=18)`**。原型 n=18 是配死量程 9000（18 间隔 = 9 个数字 ×2）的，
  量程一自适应就会出现「刻度比数字多一格、逐级错开」。
  2026-11-26 用户实机截图发现（4/5/6 那几格歪，8 千转量程下偏差最大 **+14.93°**）。
  修法与极简弧环一致，改完 4000/6000/8000/9000/16000 五个量程实测最大偏差 **0.86°**（≈25 像素圆周的 0.4px）
- 红区接 `ShiftPointLearner.GetRedlineRatio`；关闭校正或没有包时回退 **75%**
- 增压量程 -1.0 ~ 3.0 bar，0 位在 25% 处，与极简弧环一致

### 验证记录

- 圆心实测 **(149.5, 148.5)**，设计值 (150, 148)，误差 <1px（原型 (190,184) + `ContentShift(-40,-36)`）
- 红区斜纹实测起始 fraction **0.747**，设计 0.75
- 数字染色：0~6 白、7~9 红（红区比例 0.75 时 6 号应为白，符合预期）
- **刻度/数字对齐**（`temp/_chain_tickalign.py` 按角度分桶量墨迹）：
  修前 8000 量程最大偏差 **+14.93°**（数字 4 与最近大刻度），修后五个量程
  4000 / 6000 / 8000 / 9000 / 16000 最大偏差均为 **0.86°**，大刻度等距偏差 ≤0.45°

---

## 6. 停用一个模块

不要直接删，注释保留并标注原因：

- `HudScene.Render` 里的绘制调用注释掉
- `HudModuleCatalog` 的条目注释掉
- 设置页开关、`AppSettings` 字段保留（反序列化兼容）

---

## 7. 数值显示约定

| 规则 | 说明 |
|---|---|
| 两位截断 | 最多两位小数，第三位起向零截断，用 `HudDraw.Truncate` |
| 阈值常量化 | 一律写成 `public const`，不在方法里写魔数 |
| 负值钳制 | 马力、扭矩在收油瞬间钳到 0（`Math.Max(0f, ...)`） |
| 档位规则 | 只接受 `-1 ~ 10`；`-1` 显示 `R`，`0` 显示 `0`，脏数据保持上一有效值 |
| 单位换算 | 放在 `ForzaDataPacket` 的派生属性里（`SpeedKmh`、`BoostBar`），界面只取结果 |
| 颜色阈值 | 胎温分档、临界闪烁等判定集中在 `TireVisualEvaluator`，场景只取结果 |

---

## 8. 性能预算

| 指标 | 目标 |
|---|---|
| CPU | < 1% |
| GPU | < 2% |
| 显存 | < 100 MB |
| 内存 | < 300 MB（实测通常 < 200 MB） |

实现约束：

- 不在渲染循环里创建 D2D 资源（刷子/几何/文字布局已缓存）
- 隐藏状态不绘制，仅 `IdleTick` 读包
- 「以发包为准」模式只在有新包时绘制
- 静态内容（外圈/轨道/红区/资源面板文字）不每帧重建

---

## 9. 自测清单

1. 编译 0 警告 0 错误
2. 布局编辑器能看到新模块，可拖动、可缩放，保存后重进位置保持
3. 关闭开关后 HUD 上模块消失，编辑器里也隐藏
4. 切换画面比例，各模块按各自配置还原
5. 游戏内数值随驾驶变化，不出现恒 0 或乱跳
6. 停车、暂停、切出桌面时显隐行为符合预期
7. 60 FPS / 30 FPS / 以发包为准 三种刷新率表现正常
8. 检查 `logs\run-*.log` 无异常，收包速率稳定
9. 若使用自定义字体：字体随发布包分发，在未安装该字体的机器上显示正常；`LICENSE.txt` 记录完整

---

## 10. 不开游戏验证绘制（tools/）

改 HUD 绘制后，**不要靠启动游戏看效果**。链路：C# 绘制代码 → snapshot 出 PNG → `measure` / `colors` / `diff` / `crop` 分析。

**工具链完整用法见 [`tools/README.md`](../tools/README.md)**（含各脚本参数、三表圆心坐标、已知局限）。
这里只列做 HUD 绘制时**必须记住的三条**：

```powershell
# 单张：按基准尺寸 1:1 渲染，输出像素坐标等于设计坐标
dotnet run --project tools/snapshot --style altezza --width 1054 --height 722 -o temp\snapshots\a.png

# 边界批量：一次跑 8 个极端用例（速度 0/999、档位 R/10、增压 ±3、转速 0/满）
dotnet run --project tools/snapshot --preset extremes --style altezza --width 1054 --height 722
```

- **视口必须等于输出画布**：`scale = WidthRatio × 视口宽 / 基准宽`，视口更大时会放大并裁切；
  相等时 `scale` 恰为 1，输出像素才等于设计坐标
- **`tools/snapshot` 独立 csproj、不进 `.sln`**，靠 `InternalsVisibleTo("snapshot")` 访问
  `AltezzaScene` / `Nfs19Scene` / `EsScene` / `RingScene`，**不要把场景类改成 public** 来图省事；
  经典 / GHOST / SHIFT TREE 写在 `HudScene` 内、依赖 `AppServices`，暂未接入
- **改完绘制代码先跑 `--preset extremes`**，比逐个手敲参数快，也不会漏掉边界

### 三个必踩的坑

**① 透明背景必须 `--flatten`**

HUD 是透明背景 + alpha。直接用 `.convert('RGB')` 会把透明区当成深色前景，
`measure.py` 的 `lum<70` 掩膜会因此把整片背景判成前景，数齿、找小表全失效。
所有脚本默认 `--flatten '#000000'`，**不要启发式猜测是否预乘**，需要时显式给 `--premultiplied`。

**② 量文字要按颜色通道过滤**

用亮度阈值量文字时，彩色元素（红指针、金刻度）也会被算进墨迹，造成「文字被拉宽」的假象。
量奶油白/金文字应按通道过滤（`r>150 & g>110 & b>60`），或先排除红色。
另外**测量窗口要给足余量**，贴边会量出假的「宽度 = 窗口宽」。

**③ 指针角度不要靠颜色阈值算**

转速表的红区刻度也是红色，会被算进指针；两表指针还会互相干扰
（速度指针伸向左侧时落进转速表的圆内）。**改用目视 + 理论公式核对**：
`角度 = 起角 + ratio × 扫掠`（折回 360° 计）。

---

*本文只管 HUD；设置窗口的版式、控件与交互以 [`UI统一标准.md`](UI统一标准.md) 为准。*
