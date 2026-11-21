# AGENTS.md

给在本仓库工作的 AI Agent 的说明。**开始任何任务前先读完本文件。**

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
    AltezzaScene.cs            咬地鲨三表集群（转速 + 双单位速度 + 三小表，基准 1054×722）
    AltezzaGlyphCache.cs       LEXUS 字标轮廓几何缓存（FillMode.Alternate 保镂空）
    EsScene.cs                 ES 数字转速盘（单表 + 中央车速/档位，基准 500×500）
    BoostPresence.cs           无增压车检测（自吸车的 Boost 字段会漂移，不能直接当增压）
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
tools/                         开发期验证工作流（不进发行包）
  snapshot/                    离屏快照：把 GPU HUD 渲染成 PNG，独立 csproj 不进 .sln
  measure.py 等 8 个脚本      量图 / 验色 / 差异 / 裁剪 / 抠图 / 描图 / 渲染原型
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
- `Publish.bat` 输出到 `..\ForzaFatFishHUD-dist`：**先 `rd /s /q` 整个删掉再重建**（2026-11-22 核对源码确认，此前本文写成「覆盖但不删除」是错的）
  - 所以 `..\ForzaFatFishHUD-dist\config\config.json`、`data\`（学习数据）、`logs\` 都会被删；**发布前若要保留用户数据，必须先备份**
  - 发布包里本身不含 `config.json` / `data` / `logs`（`dotnet publish` 只带 `Assets`，配置是运行时生成的）
  - 每次生成 `..\ForzaFatFishHUD-releases\ForzaFatFishHUD-v{版本}-win64.zip`

## 4. 工作流约定（用户明确要求，必须遵守）

1. **先列未决项，等你拍板，再动手改代码**。不要边想边改。
   接到大功能需求时，第一份产出是「必须你决定的 N 个问题」（范围 / 尺寸 / 命名 / 数据源 / 默认值），
   不是「我准备改哪几个文件」；问题尽量一轮问完，点头之前不写一行代码。
   动手之后能自己查的（API 签名、既有约定）就自己查，不拿查得到的回来问。
2. **只在用户说"编译"时才编译**。用户自己会编译，擅自编译会打断他。
3. **涉及权限 / 热键 / 全局钩子 / 窗口置顶的验证，只编译，不要代用户启动程序**。
   Agent 启动的进程可能继承沙箱的低完整性令牌，与用户真实运行环境不等价，测出来的结论是假的。
4. 用户说"不要改"时，**只分析、只回答，不写任何文件**。
5. 修改布局/尺寸后，同步更新 `docs/HUD模块开发说明.md` 里对应的基准尺寸。
6. 用户明确说过"不要扫盘""静默等待"时，不执行任何命令。
7. **不要代替用户执行任何 git 写操作**（`commit` / `push` / `add` / 分支、reset 等）。
   代码提交一律由用户自己在 Visual Studio 里完成。排查仓库状态时可以用 `git status` /
   `ls-files` / `check-ignore` 这类只读命令，但发现问题只报告，不改索引、不提交。
8. **跨项目只读**：本项目只是 DSH 工作区根目录下的一个项目（同级还有 `design` 咬地鲨面板、
   `ForzaOverlay - 副本` 旧代码备份、参考代码仓库等）。
   整个工作区都可以读，**但除当前所在项目外一律不得修改**。即使获得完全权限，
   也不主动去改其他项目的文件；需要引用别处的素材时，读出来用即可。
   注意 `ForzaOverlay - 副本` 是用户翻查旧代码用的备份，属于重要资产，不要当冗余清理、也不要提议删除。
9. **`temp/` 是代码备份区兼参考素材区**：改大文件前先备份为 `temp\<文件名>.bak`（用户认可此做法）；
   里面也放着用户给的参考素材（设计稿、面板原型、FH6 数据表 xlsx、配色/图标预览图）。
   `.gitignore:124` 的 `**/temp/` 已忽略该目录，备份不会误入仓库。
   **不要删除 `temp/` 里的东西，也不要主动提议清理用户的文件或目录。**

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

- **不要用内联 `-e "..."` 跑 Node/Python 脚本**。PowerShell 给原生命令组装命令行时会吃掉或错配引号与反斜杠，
  脚本本身没问题也会报 `SyntaxError`（2026-11-07 为此白跑 3 个回合）。一律先把脚本写成临时 `.cjs` / `.py` 文件再执行。
  本机实际是 **PowerShell 7.6.6**，问题比 5.1 轻，但习惯照旧 —— 内联引号仍不可靠。
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
| 行驶仪表 · 咬地鲨 | 1054 × 722 | 三表集群（转速表 + 双单位速度表 + 三小表）；基准 = 图形包围盒 + 5px，内部坐标按原型 1440×720 写，靠 ContentShift 平移 |
| 行驶仪表 · ES | 500 × 500 | 单表数字转速盘 + 中央车速/档位；内部坐标按原型 1000×1000 写，缩放系数 **0.47**（不是 0.5，原型 `.wrap` 只占 94%）；圆心固定 250；点阵青→红两态（例外：不走 RpmColorScale） |
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
  - **`MaxGearSeen` 曾漏掉这层判断**（2026-11-22 修）：12 辆车实测全部记成 11，换挡提示的判断永远走不到正常分支；`Load` 时顺手把存量数据里越界的值归零
- 红区学习数据存程序目录 `data\shift-learning.json`：`CutValues`（断油样本→取中位数）、`MaxWotRpm`、`MaxGearSeen`、`Shifts`（升挡点）
- 红区算法（2026-11-22 **回退为断油优先**）：**断油优先** —— 学到断油（手动挡）就用 `Max − (Max−断油) × k` 往前推，断油/Max < 0.88 用 k=1.9，否则 1.7，结果只跟断油走、采集再多也不漂；**学不到断油才用升挡中位数**（自动挡游戏在红区前就升档，碰不到限速器）；两者都没有时用 `AutoRedlineRatio = 0.90`，全部夹在 0.50~0.98
  - **绝对不要拿升挡点当手动挡的红区**（2026-11-21 试过，2026-11-22 用户实机发现回退）：玩家是照换挡提示换挡的，提示点 = 断油 × 0.98，升挡样本天然挤在断油下方，拿它当红区等于把红区顶到断油附近 → 红区带被压窄，且升挡点会随驾驶习惯一路走高，表现为「**红区逐步变窄**」。实测 4221 升挡点首末差 +425 转、红区从 86.8% 爬到 92.4%，断油公式则稳在 90.0%
  - 红区被抬高还会连带弄坏第 4 颗换挡灯：`max(断油−Δ×0.28, 红区+0.03)` 一旦超过断油比例，灯在断油前永远不亮
- **饱和断油值（≥ 99% Max）在读取时也要过滤**：采样侧有 `CutSaturationRatio` 挡，但修复前学进去的存量值还在（实测 car 1124 断油 = 7960 = 99.5% max，套公式会得到 98% 的报废红区）。`GetCutRpmInfo` 里视为「没学到」，自动回落升挡中位数
- **断油检测的门槛必须分档位/分动力，不能用一套**（2026-11-21 修，4~5 档低动力车原本永远学不到）：
  - **车速检查**：断油只切断推力、不断惯性，低档短齿比一个断油周期内车速仍涨 1.7~2.6 km/h。
    所以档位 ≤3 用 5 km/h 容差，≥4 用 1.5 km/h。**这一条曾把低档车样本全丢掉**
  - **锯齿幅度**：`maxRpm < 7500` 用 **45 rpm**（原 60）。小马力车复油后转速回升软，锯齿常只 45~60
  - **贴峰值容差**：`amplitude × 1.5`，不要写死 `CutExtensionRpm × 2`（=80）。
    高动力车锯齿 100 rpm，谷底时 `|rpm-max|` 就有 100，写死 80 会丢掉一半样本
  - **油门门槛** 0.85（原 0.90）：手柄扳机有死区，很难稳定压住 90%
  - 防误判的真正主力是**转速平台 `drift ≤ 40`** 与 **`falls ≥ 3`**，不是车速或幅度阈值
- **自动挡不会进入断油**：游戏在红区前就升档，转速碰不到限速器。
  自动挡 + 低动力车走的是「升挡点中位数」路径，别去调断油阈值救它
- 换挡提示「4灯」：绿常亮 → 黄（红区前 10%）→ 红（红区起点）→ 红（最佳换挡点 = 断油 − (Max−断油)×0.28）
- 数值格式化约定：**截断**（`MidpointRounding.ToZero`），不是四舍五入——用户明确要求保持现状
- **`EngineMaxRpm` 在菜单 / 车库 / 暂停时游戏不发（为 0）**。它是转速表量程的来源，
  必须沿用最后一次有效值，不能兜底 8000 —— 否则进出菜单时量程与指针会跳变。
  正因如此，指针比例 clamp 到 1.0 在物理上永不触发（不是「超转要夹住」，是量程本就够）

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

- 本机装有 `dsh-office`（文档类）与 `dotnet-skills`（.NET 类）两套技能包
- **技能按需加载**：任务不匹配就不要加载，省 token
- 技能清单在**会话启动时注入**，改配置后要新开会话才可见

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

---

## 13. tools/ 验证工作流（改绘制代码必须走）

改 HUD 绘制后，**不要靠启动游戏看效果**。用 `tools/` 出一张图、量一遍，全程不用开游戏。

### 链路

```
C# 绘制代码 → snapshot 出 PNG → measure/colors/diff/crop 分析
```

### snapshot（离屏快照，本项目专属）

```powershell
dotnet run --project tools/snapshot --style altezza --width 1054 --height 722 -o temp\snapshots\a.png
```

- 独立工程，**不进 `.sln`**，主程序的编译与发布完全不受影响
- 靠 `InternalsVisibleTo("snapshot")` 访问 `AltezzaScene` / `Nfs19Scene` 等 internal 类；
  **不要把场景类改成 public** 来图省事
- 数据全部是确定值（`--rpm` `--speed` `--gear` `--boost`），同一条命令永远出同一张图，这样才能对比
- **视口必须等于输出画布**：`scale = WidthRatio × 视口宽 / 基准宽`，视口更大时会放大裁切。
  两者相等时 `WidthRatio=1` 使 `scale` 恰好为 1，输出像素坐标直接等于设计坐标
- 目前只支持 `altezza` / `nfs19` / `es`；经典 / GHOST / SHIFT TREE 写在 `HudScene` 内部、依赖 `AppServices`，暂未接入

### Python 脚本

全部用 DSH 自带 Python 跑（本机无需另装）。路径在 DSH 运行时目录下，以 `dependencies\python\python.exe` 结尾：

```
%USERPROFILE%\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe
```

PATH 里的 `WindowsApps\python.exe` 是商店占位符，**不能用**。

| 脚本 | 用途 |
|---|---|
| `measure.py` | `radial` / `ray` / `arc` / `blobs`，输出 JSON，`--ref-r` 声明基准 |
| `colors.py` | 主色 / `--candidates` 验证已知色值 |
| `diff.py` | 差异定位（输出框，不是评分） |
| `crop.py` | 批量裁剪 / 拼接 + 坐标标注 |
| `extract.py` / `trace.py` | 抠图 / 位图转 SVG path |
| `render.py` | 渲染 HTML 原型（需完全权限，会起浏览器） |

**两条测量原则：**

- **配色用主色分析（`colors.py`），不要用像素差率** —— 3px 的色差在整体差异率里占比极低，但视觉上很刺眼
- **比对前两张图必须尺寸一致**，否则会 scaled 或差异率虚高，没有参考价值

### ⚠️ HUD 是透明背景，分析前必须 `--flatten`

所有脚本都支持 `--flatten '#000000'`（默认黑底）。**直接用 `.convert('RGB')` 会把透明区当成深色前景**，
`measure.py` 的 `lum<70` 掩膜会因此把整片背景判成前景，数齿、找小表全失效。

**不要启发式猜测 alpha 是否预乘** —— 暗色图会被误判成预乘导致颜色偏亮；需要时显式给 `--premultiplied`。

### 文档

完整用法见 `tools/README.md`（含三表各自的圆心坐标）。

---

## 14. HUD 绘制易错点（编译器查不出，只能比对图发现）

### 角度制有两套，勿混用

- **咬地鲨 / ES** 按 SVG 制：`0° = 正右`，y 轴向下，`PointOn` 用 `cos/sin`
- **仿NFS19 / SHIFT TREE**：`0° = 正上`，顺时针，用 `sin/-cos`

混用会让指针整体偏向错误方向，而代码完全合法、编译零错误。

### ⚠️ `Matrix3x2` 是行向量约定，`A * B` 表示「先 A 后 B」

这是 2026-11-21 写 ES 时**连续踩了 5 轮**的坑，编译器查不出、运行不报错，只是画面少了东西。

- **绕某点旋转**必须写 `T(-P) * R * T(+P)`。写成 `T(+P) * R * T(-P)` 会把对象甩到画布外
  —— ES 的指针因此整根消失，用像素扫描（找 `#FF4D00`）才确认是 0 个像素，靠眼睛只会以为「没画」
- **沿弧排文字**：字顶要朝圆心时旋转角是 `θ = a - 90°`（联立「字顶 (0,-1) 旋转后 = (sinθ,-cosθ)」
  与「朝圆心方向 (-cos a, -sin a)」解得）。写成 `a + 90°` 得到的是背离圆心的倒字
- 验证方式：**不要反复改数字盲试**。先写出局部坐标 → 变换后方向的推导，再改一次；
  或者直接对生成图做像素统计（数目标颜色像素、算质心角度）来判定是否真的画上了

### ⚠️ 缩放系数与画布坐标要分开算

把原型的 `viewBox` 尺寸搬进新基准时，**半径/字号乘缩放系数，圆心不乘**。

- ES 的原型是 1000×1000、对外 500×500，但 `es.html` 里 `.wrap { width: min(94vmin,1000px) }`
  只占 94%，所以真实缩放系数是 **0.47** 而不是 0.5
- 圆心必须写死 `BaseWidth * 0.5` = 250。写成 `500 * 0.47` 得到 235，
  整个表盘偏左上、右侧溢出画布（实测包围盒中心跑到 (234.5, 234.5) 才发现）
- **判定方法**：量成图的**非透明像素包围盒中心**与**外径**，跟原型同参数图比对；
  两者不一致说明系数或圆心算错了，不要靠肉眼「看起来差不多」

### HTML 原型必须真的渲染出来比对

`temp/*.html` 原型可以用 `tools/render.py --shot` 截图（需完全权限）。
**不要只读源码推算几何** —— 原型里 `.wrap`、`filter: blur()`、CSS 缩放都会改变实际尺寸，
源码上的 `r438` 不等于渲染出来的半径。ES 就是因为没渲染原型、
按字面 0.5 换算，整体大了 6.3% 还不知道，返工两轮才靠出图比对发现。

### ⚠️ `WriteAllLines` / `Set-Content` 会写出 CRLF，破坏项目的 LF 规范

`.cs` / `.md` / `.py` 一律 **LF + 无 BOM**。PowerShell 的
`[System.IO.File]::WriteAllLines()` 和 `Set-Content` 在 Windows 上**默认写 CRLF**。

用脚本批量改完 `.cs` 后必须检查一次：

```powershell
$b = [System.IO.File]::ReadAllBytes($path); $t = [System.Text.Encoding]::UTF8.GetString($b)
"CRLF=$(([regex]::Matches($t,"`r`n")).Count) 裸LF=$(([regex]::Matches($t,"(?<!`r)`n")).Count)"
```

发现全 CRLF 就 `Replace("`r`n","`n")` 后重写（`UTF8Encoding($false)` 保无 BOM）。
**2026-11-21 写 EsScene.cs 时踩过**：文件能编译、诊断也干净，
但同目录 `AltezzaScene.cs` / `Nfs19Scene.cs` 都是纯 LF，只有新文件是 CRLF。

例外：`AGENTS.md` 本身是全 CRLF、`.ps1` 需 UTF-8 BOM。

### D2D 画刷 / 几何缓存禁用 `using`

`GetXxx(target)` 返回的是缓存对象。用 `using` 包住返回值会在帧末把它 Dispose 掉 ——
**编译不报错，下一帧崩**。

### SVG → Direct2D 的文字差异必须补偿

- `IDWriteTextLayout1.SetCharacterSpacing` 的单位**与文档不符**。文档写「em 的千分之一」，
  但按 C# 换算实测会宽一倍以上。咬地鲨在 22 号字下实测标定：**1 单位 ≈ 0.844px**。
  **系数随字号变化，改字号必须重新标定**
- SVG 的 `y` 是**基线**位置；`HudDraw.CenteredText` 走 `ParagraphAlignment.Center` 按**行高**居中。
  同一公式换算的 y 会差约半个行高 —— 咬地鲨的 `x1000r/min` 实测偏低 8px，
  用 `TachoUnitBaselineFix` 补偿
- 走旋转矩阵的 `DrawDialText`（刻度数字、km/h、MPH）没有这个问题，只有走 `CenteredText` 的才会偏

### 搬运大段绘制代码后逐行比对 `CreateFormat`

编译器查不出 `FontWeight.SemiBold` 写成 `Bold`、`TextAlignment` 写错这类问题，**必须逐行比对备份文件**。

### Boost 负值是真实物理量

FH6 的 `Boost` 字段是「PSI above atmospheric」，**负值 = 进气歧管真空**，不是脏数据。
除面板设计明确要求归零（如咬地鲨小表无负刻度），一律显示原值。

### 有数字刻度的主表允许爆表

超出量程时指针继续顺时针转出表盘，**只夹下界 0 不夹上界** —— 玩家喜欢这种效果。
现实中重度改装车确有可转两圈以上的表，**指针绕回属正常现象，不是 bug**。
涡轮/电压/水温这类有物理上限的小表仍保持 clamp。

### 测指针角度：红色像素会互相污染

用「找红色像素算角度」的方式量指针时有两个坑，两次都导致过误判：

1. **转速表的红区刻度也是红色**，会被算进指针，测出完全无关的角度
2. **两表的指针会互相干扰** —— 速度指针伸向左侧时会落进转速表的圆内，
   用「圆心 + 半径」圈定无效；用连通域取「最长红块」也无效（速度指针总是更长）

**正确做法**：用 `snapshot --preset extremes` 出图后**直接目视判断**，
再用理论公式核对（`角度 = 起角 + ratio × 扫掠`，折回 360° 计）。
不要相信仅靠颜色阈值算出的角度。

### 边界值必须用预设批量验证

`tools/snapshot` 支持 `--preset extremes`，一次渲染 8 个边界用例
（速度 0/999、档位 R/10、增压 ±3、转速 0/满）。改完绘制代码先跑一遍这个，
比逐个手敲参数快，也不会漏掉边界。

### 「默认位置」必须问清映射，不要自己取中点

水温表曾经把「C–H 刻度中点」当成 100℃ 直接写死，实际是错的 ——
用户给的定义是 **C = 0℃、H = 180℃**，100℃ 应在 `90 + (100/180)×180 = 190°`，不是 180°。

**规则**：装饰表／无量纲表盘只要涉及具体数值，先问清 **两端各代表多少**，
再由公式推导角度；不要用「中点 = 常见值」去猜。推导式写成常量（如
`TempAngleAtDefault`），以后改端点只需改温度定义，不用重新算角度。