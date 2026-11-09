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

当前模块：行驶仪表 · 经典 820×150、行驶仪表 · GHOST LADDER 420×238、行驶仪表 · SHIFT TREE 820×320、行驶仪表 · 仿NFS19 601×416、轮胎与悬挂 220×212、HUD 渲染资源占用 200×140。
四种仪表样式由设置项 `InstrumentStyle` 四选一（经典 / GHOST LADDER / SHIFT TREE / 仿NFS19），各自独立保存布局。

---

## 4. 现有模块

| 模块 | GPU 绘制方法（`HudScene`） | 基础尺寸 | 布局属性 | 默认开关 |
|---|---|---|---|---|
| 行驶仪表 · 经典 | `DrawClassicPanel` | 820 × 150 | `PerformanceLayout` | 开 |
| 行驶仪表 · GHOST LADDER | `DrawGhostPanel` | 420 × 238 | `FestivalLayout` | 开 |
| 行驶仪表 · SHIFT TREE | `DrawShiftTree` | 820 × 320 | `ShiftTreeLayout` | 开 |
| 行驶仪表 · 仿NFS19 | `Nfs19Scene.Draw` | 601 × 416 | `Nfs19Layout` | 开 |
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

*本文只管 HUD；设置窗口的版式、控件与交互以 `UI统一标准.md` 为准。*
