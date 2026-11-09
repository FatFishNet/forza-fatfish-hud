# AGENTS.md

给在本仓库工作的 AI Agent 的说明。**开始任何任务前先读完本文件。**

---

## 0. 固定结尾（识别用）

无论任务内容、无论回答长短，**每次回答的最后一行必须是**：

```
你可爱的大肥鱼一直都在，等待下一轮任务。
```

这一条同时用于验证 Agent 是否真的读取了本文件。若你在新会话中被要求做任何事，
却不确定是否读到过本文件，说明你读到了。

---

## 1. 项目是什么

Forza Fat Fish HUD —— 面向 Forza Horizon 4/5/6 的实时遥测 HUD 悬浮层，C# / .NET 8，设置界面用 WPF，HUD 用 GPU 渲染。

- 游戏通过 UDP 发送 324 字节遥测包，本程序解析后在透明置顶窗口上绘制仪表
- HUD 渲染走 D3D11 + Direct2D + DirectComposition（`Rendering/`），WPF 只负责设置窗口与布局编辑器
- 单机离线工具：除「检查更新」访问 GitHub Releases 外无网络请求；无账号体系
- 目标用户是 Windows 10 1709+ / Windows 11 的玩家

## 2. 目录结构

```
src/ForzaOverlay.App/          WPF 界面层
  App.xaml(.cs)                启动、单实例、GPU HUD 生命周期、异常捕获、退出清理
  Rendering/                   ★ GPU HUD 渲染
    GpuOverlayWindow.cs        D3D11 + DComp 透明置顶窗口、渲染循环、可见性
    HudScene.cs                场景调度 + 经典/轮胎/资源/换挡提示的 Direct2D 绘制
    ShiftTreePanel.cs          SHIFT TREE 双炮筒（左转速弧、右油门/刹车、中央灯与档位）
    GhostPanel.cs              GHOST LADDER（几何描边、数字定槽、档位铭牌）
    HudShared.cs               画刷缓存、文字布局缓存、档位格式化、绘制原语（各面板共用）
    Nfs19Scene.cs              仿NFS19 单表（主表 + 增压小表，独立配色）
    Nfs19GlyphCache.cs         仿NFS19 字形几何解析与缓存
  Ui/SettingsWindow.xaml(.cs)  设置中心 + 关于页（含检查更新）
  Ui/UpdateWindow.xaml(.cs)    更新说明与下载窗口
  Ui/HudLayoutWindow.xaml(.cs) 布局编辑器
  Ui/HudModuleCatalog.cs       模块清单（名称 / 基准尺寸 / 显隐条件）
  Services/                    UDP 监听、全局热键、进程监视、权限检测、更新检查/应用、超速提示音
    OverspeedSoundPlayer.cs    超速提示音（WPF MediaPlayer，按音频时长调度）
  Assets/Fonts/                内置字体 D-DIN（OFL 授权）
  Assets/Images/               图标与角色形象素材
  Assets/Sounds/               提示音素材（AE86windchime.mp3，仅支持 MP3）
src/ForzaOverlay.Core/         无界面逻辑：配置、遥测解析、网络、监控、视觉规则
  Learning/ShiftPointLearner.cs 红区/换挡点学习（断油检测、升挡采样、按车指纹缓存）
  Diagnostics/PerfRecorder.cs  性能日志（卡顿诊断，默认关闭）
docs/                          开发规范与项目索引
app-dist-template/             发布时复制进发行包的模板
temp/                          临时素材（设计稿、字体源文件），不入库
```

## 3. 编译与发布

```powershell
# 编译（开发）
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'dsh-dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $env:USERPROFILE '.nuget\packages'
dotnet build .\ForzaOverlay.sln -v minimal -m:1

# 发布（目录式自包含，输出到仓库外 ..\ForzaFatFishHUD-dist，
#       并压缩为 ..\ForzaFatFishHUD-releases\ForzaFatFishHUD-v{版本}-win64.zip）
.\Publish.bat
```

- SDK 版本由 `global.json` 固定为 .NET 8，不要升级
- **编译前先结束运行中的 `ForzaFatFishHUD`**，否则 `MSB3021/MSB3027` 文件占用
- 编译完成后必须报告：`已编译：N 警告 M 错误`
- 不允许留下警告（`0 警告` 是交付标准）
- `Publish.bat` 输出到 `..\ForzaFatFishHUD-dist`：**覆盖同名文件，但不删除**目录里多出来的东西
  - 用户的 `config\`、`data\`（学习数据）、`logs\` 会保留；发布包里不含 `config.json`
  - 每次生成 `..\ForzaFatFishHUD-releases\ForzaFatFishHUD-v{版本}-win64.zip`

## 4. 工作流约定（用户明确要求，必须遵守）

1. **先给方案，等用户确认后再动手改代码**。不要边想边改。
2. **只在用户说"编译"时才编译**。用户自己会编译，擅自编译会打断他。
3. **涉及权限 / 热键 / 全局钩子 / 窗口置顶的验证，只编译，不要代用户启动程序**。
   Agent 启动的进程可能继承沙箱的低完整性令牌，与用户真实运行环境不等价，测出来的结论是假的。
4. 用户说"不要改"时，**只分析、只回答，不写任何文件**。
5. 修改布局/尺寸后，同步更新 `docs/HUD模块开发说明.md` 里对应的基准尺寸。
6. 用户明确说过"不要扫盘""静默等待"时，不执行任何命令。
7. **不要代替用户执行任何 git 写操作**（`commit` / `push` / `add` / 分支、reset 等）。
   代码提交一律由用户自己在 Visual Studio 里完成。排查仓库状态时可以用 `git status` /
   `ls-files` / `check-ignore` 这类只读命令，但发现问题只报告，不改索引、不提交。

## 5. 代码规范

- 类型声明以**显式类型**为主；仅在右侧能一眼看出类型时使用 `var`，并保持同一文件内风格一致
- 中文注释用于解释"为什么"，不写"是什么"
- 日志统一走 `Diagnostics.Write` / `Diagnostics.WriteException`，禁止 `Console.WriteLine`
- 新增 UI 元素后，在 `OnOverlayLoaded` 之类的初始化点加一条启动日志
- **改动 `.cs` 后必须调用 `lsp_diagnostics` 确认零错误，再报告完成**。编译器语义仍以 `dotnet build` 为准，
  LSP 诊断只作快速反馈；注意它只对属于某个 csproj 的文件生效，`temp\` 等工程外文件永远返回 0 条（假阴性）
- 不引入新的第三方包，除非用户同意

### 5.1 命令行与编码（踩过的坑）

以下三条都是实际踩过、有原文证据的，不是预防性猜测：

- **不要用内联 `-e "..."` 跑 Node/Python 脚本**。PowerShell 5.1 给原生命令组装命令行时会吃掉或错配引号与反斜杠，
  脚本本身没问题也会报 `SyntaxError`（2026-11-07 为此白跑 3 个回合）。一律先把脚本写成临时 `.cjs` / `.py` 文件再执行。
- **读文件前先定编码**。Windows PowerShell 5.1 的 `Get-Content` 按 ANSI(GBK) 解码，UTF-8 无 BOM 的 JSON 会让
  `ConvertFrom-Json` 报「传入的对象无效」。读 JSON / 源码一律 `-Encoding UTF8`；读 `Publish.bat`、`Start.bat`、
  `app-dist-template` 下的 GBK 文件用 `-Encoding ansi`。
- **大输出不进上下文**。账本、packument、构建日志这类几万字的内容，先用脚本只取需要的字段，或先落盘再
  `grep` / `head` 筛选。整份打印必被截断，等于白跑一轮。

## 6. HUD 布局关键约定

| 模块 | 基准尺寸 | 备注 |
|---|---|---|
| 行驶仪表 · 经典 | 820 × 150 | 单行横排，转速条横贯顶部，所有列宽固定 |
| 行驶仪表 · GHOST LADDER | 420 × 238 | 无底板，几何描边，数字定槽 |
| 行驶仪表 · SHIFT TREE | 820 × 320 | 双炮筒：左转速弧、右车速 + 油门/刹车半弧，中央 10 颗换挡灯与档位 |
| 行驶仪表 · 仿NFS19 | 601 × 416 | 主表 + 左侧增压小表；基准 = 图形包围盒 + 5px 安全边距 |
| 轮胎与悬挂 | 220 × 212 | 2×2 四格（已移除中心差速图） |
| HUD 资源占用 | 200 × 140 | 帧率 / CPU / 内存 / GPU / 显存 |

- 缩放计算**必须使用基准宽高常量**，不能用 `ActualWidth`（切全屏时布局瞬时为 0 会导致缩放爆炸）
- 面板内所有元素尺寸固定，禁止让数值宽度影响布局（会随数据变化抖动）
- 转速条配色统一走 `Ui/RpmColorScale`（< 76% 基础色 → 76%~82% 基础→黄 `#FFB020` → 82%~90% 全黄 → 90%~94% 黄→红 `#FF3B30` → ≥ 94% 全红）；新增带转速条的面板必须调用该类，不得自行写死阈值或黄红颜色。例外：SHIFT TREE 的转速弧按设计稿固定三段（< 73.3% 青、73.3%~86.7% 黄、≥ 86.7% 红）
- 踏板四色：离合紫 `#B388FF`（半透明）/ 油门蓝 `#4FC3F7` / 刹车红 `#F44336` / 手刹黄 `#FFEB3B`
- 轮胎颜色来自 `TireVisualEvaluator`：填充=胎温，描边=抓地（打滑变黄、失控变红闪烁）
- HUD 面板全部由 `Rendering/HudScene.cs` 用 Direct2D 绘制，不要再新增 WPF HUD 窗口
- GPU 场景坐标：按基准尺寸绘制；`scale = WidthRatio × 游戏客户区宽 / 基准宽`，并加客户区偏移
- 数字定槽：SHIFT TREE 转速/速度逐位固定槽宽（33 / 36），GHOST 速度按 63 槽位，防止比例字体抖动
- 文字：D2D `TextAntialiasMode = Grayscale`；GHOST 速度数字用 D-DIN 字形轮廓 + 2px 描边

## 7. 运行时权限经验（踩过的坑）

- 全局热键（`RegisterHotKey`）受 **UIPI** 限制：低完整性进程在更高完整性窗口（游戏）处于前台时收不到 `WM_HOTKEY`
- 完整性级别：普通用户 `S-1-16-8192` / 管理员 `S-1-16-12288` / 沙箱可能降到 `S-1-16-4096`
- 游戏权限取决于启动方式：从被提权的 Steam（如加速器拉起）启动 → 游戏继承管理员权限
- 程序内有横幅检测：自身权限低于游戏时，在「遥测选项」页顶部提示并提供「以管理员身份重启」
- 检测实现见 `Services/ProcessPrivilege.cs`，用 `TokenIntegrityLevel` 比较

## 8. 数据与行为备忘

- 遥测包固定 324 字节，`IsRaceOn` 在偏移 0，`Accel/Brake/Clutch/HandBrake` 在 315–318，`Gear` 在 319
- 车库 / 车展里 `IsRaceOn` 仍为 1，且可以轰油门，因此**无法用遥测数据区分"车库"和"停车"**
- 档位有效值只有 **1~10**；`0`/`-1` 是空档/倒挡，**`11` 是换挡中（换挡瞬间）**
  - 断油检测、升挡采样、最高档判断都必须只接受 1~10，否则换挡慢的车会永久学不到（踩过的坑）
- 红区学习数据存程序目录 `data\shift-learning.json`：`CutValues`（断油样本→取中位数）、`MaxWotRpm`、`MaxGearSeen`、`Shifts`（升挡点）
- 红区算法：有断油 → `Max − (Max−断油) × k`（断油/Max < 0.88 用 k=1.9，否则 1.7）；无断油（自动挡）→ 自动换挡点
- 换挡提示「4灯」：绿常亮 → 黄（红区前 10%）→ 红（红区起点）→ 红（最佳换挡点 = 断油 − (Max−断油)×0.28）
- 数值格式化约定：**截断**（`MidpointRounding.ToZero`），不是四舍五入——用户明确要求保持现状

## 9. 不要做的事

- 不要添加托盘图标（已按用户要求移除）
- 不要重新引入 WPF HUD 渲染（已整体移除，WPF 仅保留设置界面与布局编辑器）
- 不要为数字加固定槽位之外的额外间距计算（会引入抖动）
- 不要打包 Windows 自带字体（Bahnschrift / Consolas），只能引用字体名
- 不要把 `temp/` 里的设计稿当作最终规格，以 `docs/` 和代码为准
- **新增 .gitignore 规则后要检查是否误伤源码**：`*.app/` 曾把整个 `ForzaOverlay.App/` 目录排除掉，
  App 的 32 个源文件（含 `Nfs19Scene.cs` 等渲染核心）一度整体未进仓库，`git status` 干净是假象。
  核对用 `git ls-files -- <目录>`，**只看得到 csproj 不算通过，要看文件个数**。
  另注意 `!` 否定规则**对被排除的目录无效**——git 不会进入已排除目录找文件，
  必须从源头删掉那条目录规则；且 `git add -A` 对忽略文件静默跳过，不报任何警告

---

## 10. 技能环境

本机装有两套技能包：`dsh-office` 与 `dotnet-skills`。

### dsh-office（7/7 全部启用，无需申请）

`office`、`office-docx`、`office-xlsx`、`office-pptx`、
`office-export-pdf`、`office-render`、`office-recalculate`

适用场景：写使用说明、做遥测字段对照表 / 参数表、把文档转 PDF、渲染检查排版。
**非文档任务不要加载。**

### dotnet-skills

本机已安装 `dotnet-skills` 插件包，作为 **DSH 全局技能**使用。

**配置位置**：`C:\Users\ZC\.dsh\skill-scope\global.json`（黑名单模式：列表里的禁用，不在列表的启用）

### 当前已启用的 15 个

| 技能 | 用途 |
|---|---|
| `dotnet-pinvoke` | 审查 / 编写 P/Invoke（本仓库约 30 处 `DllImport`） |
| `analyzing-dotnet-performance` | 分配与热路径分析（渲染循环） |
| `csharp-scripts` | 免建工程跑单文件 C#（验证字段偏移等） |
| `scaffold-dotnet-test-project` | 创建测试工程 |
| `code-testing` | 编写 / 修复测试 |
| `run-tests` | 运行测试的命令与参数 |
| `csharp-refactoring` | 安全重构 C# |
| `binlog-failure-analysis` | 分析 MSBuild binlog 定位构建失败 |
| `csharp-expert` | 路由到正确的专项技能 |
| `dotnet-trace-collect` | 采集 .NET 性能数据（`dotnet-trace` / `dotnet-counters` 选型），卡顿排查用 |
| `microbenchmarking` | 热路径微基准（BenchmarkDotNet，另建工程，不动主工程依赖） |
| `dump-collect` | 崩溃转储采集（`dotnet-dump` / procdump） |
| `binlog-generation` | 生成 MSBuild binlog，配合 `binlog-failure-analysis` 使用 |
| `writing-mstest-tests` | 编写 MSTest 测试 |
| `coverage-analysis` | 覆盖率分析 |

> 2026-11-07 新开 6 个（诊断 / 构建 / 测试），改动前已备份 `C:\Users\ZC\.dsh\skill-scope\global.json.bak`。
> 技能清单在**会话启动时注入**，改完必须新开会话才可见。

### 规则

1. **其余 dotnet 技能当前处于禁用状态**。需要用到（例如 `msbuild-antipatterns`、`migrate-nullable-references`、
   `check-bin-obj-clash`、`test-gap-analysis` 等）时，
   **先向用户申请，得到同意后再改配置启用**，不要擅自放开。
2. 启用方式：把技能名从 `global.json` 的 `skills` 数组里删掉；改动前先备份为 `.bak`。
   删的是数组最后一项时，记得去掉新末项的逗号，否则 JSON 直接坏掉（2026-11-07 踩过）。
3. 配置改完即时生效，但**技能清单在会话启动时注入**——当前会话看不到新技能，必须新开会话。
4. 技能按需加载，任务不匹配时不要加载，以节省 token。

### 本地源码插件目录

- 从 GitHub / 本地源码安装的 DSH 插件，一律放在 **`D:\DeepSeek-plugins\<插件名>`**；
- profile 依赖写成 `file:D:/DeepSeek-plugins/<插件名>`，不要只剪切目录而不改依赖；
- 不要放 `%TEMP%`（会被清理）或仓库目录（会被当成项目文件提交）。

---

## 11. 待办（备忘）

以下事项已确认要做，但**不在本次提交范围内**：

- **SHA256 校验**：更新下载完成后校验 Release 附带的 `.sha256` 文件，防止下载被篡改（在 `UpdateService.DownloadAsync` 之后校验）
- **「自动检查更新」开关**：设置项 `AutoCheckUpdates`（当前固定启动检查一次）
- **配置保护**：更新脚本解压时排除 `config/` 目录（当前靠「发布包不含 config.json」的约定，不够稳）
- **配置文件版本与迁移**：`config.json` 加 `ConfigVersion`，加载时按版本迁移旧字段、补默认值；重命名/结构变动时不丢用户设置
- **升级前备份**：更新脚本先把 `config.json` 复制为 `config.json.bak`，迁移失败可回滚
- **更新下载源（国内分流，需免费方案）**：GitHub Releases 国内下载很慢；优先免费的 **Gitee Release 镜像**（发版时同步 zip），更新器支持多源回退（镜像优先 → GitHub 兜底），全部失败才提示手动下载。备选：ghproxy 类加速（免费但不稳）、Cloudflare R2 / Vercel（免费额度，国内速度一般）

---

## 12. 性能日志（卡顿诊断）

排查「游戏卡顿是不是 HUD 造成的」时用，**默认关闭**；关闭时渲染线程第一行就返回，零开销。

- 开关位置：设置 → 运行日志 → 最下方「性能记录（卡顿诊断）」，**只有一个开关**：
  - 「记录性能日志」：勾选立即生效，不需要重启；每秒汇总与 `HITCH` 明细一起记；
  - 「标记此刻」：手动写一条 `MARK` 时间戳，用于对齐「我觉得顿了一下」。
- 产物：`logs\perf\perf-yyyyMMdd-HHmmss.csv`
  - 每秒一行，29 列：帧耗时 p50/p99/max、绘制与 Present 拆分、掉帧数、连续卡顿段、GC0/1/2 与 gen2 暂停、分配量、CPU / 本进程 GPU / 整机 GPU / 显存、工作集、线程数、句柄数、收包数、包间隔 p50/p99/max、包到画面延迟、10 秒进程扫描耗时；
  - 单文件 16 MB 滚动，最多 10 份、保留 7 天；
  - 文件头 `# env=` 记录版本、系统、CPU 核数、显卡、分辨率、刷新模式，便于两次记录对比。
- 实现：`Core/Diagnostics/PerfRecorder.cs`（命名空间 `ForzaOverlay.Core.Logging`）。渲染线程只做内存累加；**每秒结算（排序 / GC / 进程信息 / PDH 采样）与写盘都在后台线程**，不要把任何每秒一次的活儿放回渲染线程——2026-11-07 曾因每秒结算在渲染线程上造成 400 ms 卡顿、帧率掉到 34。热路径禁止用 `Diagnostics.Write`（它每次调用都开关文件）。
- 判读口径：

  | 卡顿瞬间 | 结论 |
  | --- | --- |
  | 我们帧时间平、包间隔涨 | 游戏侧在顿 |
  | 我们帧时间同步涨 | 我们在拖后腿，再看 draw 与 Present 哪段长 |
  | `gc2` 与 `gc_pause_ms` 对齐 | .NET GC 造成 |
  | `cpu` / `scan_ms` 出现 10 秒节拍尖峰 | 进程扫描或日志写盘 |

- 局限：记录不到游戏自身帧时间与 DWM / 驱动层，包间隔只是代理；要更细的 GPU 帧时间得用 PresentMon 或 WPA。

