# AGENTS.md

给在本仓库工作的 AI Agent 的说明。**开始任何任务前先读完本文件。**

详细规格分别见 `docs/HUD模块开发说明.md`（绘制）、`docs/UI统一标准.md`（界面）、
`docs/项目地图.md`（结构索引）、`tools/README.md`（验证工具链）。**本文件只留结论与红线。**

---

## 1. 项目是什么

Forza Fat Fish HUD —— 面向 Forza Horizon 4/5/6 的实时遥测 HUD 悬浮层，C# / .NET 8，设置界面 WPF，HUD 走 GPU。

- 游戏 UDP 发 324 字节遥测包，本程序解析后在透明置顶窗口绘制仪表
- HUD 渲染走 D3D11 + Direct2D + DirectComposition（`Rendering/`），WPF 只管设置窗口与布局编辑器
- 单机离线：除「检查更新」访问 GitHub Releases 外无网络请求；无账号
- 目标：Windows 10 1709+ / Windows 11 64 位

## 2. 目录结构

```
src/ForzaOverlay.App/          WPF 界面层
  App.xaml(.cs)                启动、单实例、GPU HUD 生命周期、异常捕获、退出清理
  Rendering/                   ★ GPU HUD
    GpuOverlayWindow.cs        D3D11 + DComp 透明置顶窗口、渲染循环、可见性
    HudScene.cs                场景调度 + 经典/轮胎/资源/换挡提示绘制
    ShiftTreePanel.cs / GhostPanel.cs        SHIFT TREE / GHOST LADDER
    HudShared.cs               画刷/文字布局缓存、档位格式化、绘制与定槽原语
    Nfs19Scene.cs / Nfs19GlyphCache.cs       仿NFS19 主表 + 增压小表 / 字形几何
    AltezzaScene.cs / AltezzaGlyphCache.cs   咬地鲨三表 1054×722 / LEXUS 字标轮廓
    EsScene.cs / RingScene.cs / DriftChainScene.cs   ES 500×500 / 极简弧环 312×330 / 漂移锁链 304×289
    BoostPresence.cs           无增压车检测（自吸车的 Boost 字段会漂移）
  Ui/                          SettingsWindow / UpdateWindow / ConfirmWindow /
                               HudLayoutWindow / HudModuleCatalog
  Services/                    AppServices（组合根）、AppStartupValidator、GlobalHotkeyService、
                               HotkeyGesture、HudVisibilityRules、PerformanceMonitor、
                               ProcessPrivilege、UpdateService、UpdateApplier、OverspeedSoundPlayer
  Assets/                      Fonts（D-DIN / Orbitron / Rajdhani，均 OFL）、Images、Sounds
src/ForzaOverlay.Core/         无界面逻辑
  Learning/ShiftPointLearner.cs 红区/换挡点学习
  Configuration/ Telemetry/ Networking/ Monitoring/ State/ Visuals/ Aggregation/
  Diagnostics/                 Diagnostics.cs + PerfRecorder.cs（命名空间都是 Core.Logging）
docs/                          开发规范与项目索引
app-dist-template/             发布时复制进发行包的模板
tools/                         开发期验证工作流（不进发行包）
  snapshot/                    离屏快照，独立 csproj 不进 .sln
  measure/colors/diff/crop/extract/trace/render.py + imgload.py
temp/                          代码备份区兼素材区，不入库
```

## 3. 编译与发布

```powershell
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'dsh-dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $env:USERPROFILE '.nuget\packages'
dotnet build .\ForzaOverlay.sln -v minimal -m:1
.\Publish.bat
```

- SDK 由 `global.json` 固定 .NET 8，不要升级
- **编译前先结束运行中的 `ForzaFatFishHUD`**，否则 MSB3021/MSB3027 文件占用
- 编译后必须报告 `已编译：N 警告 M 错误`；**0 警告是交付标准**
- `Publish.bat` 会 `rd /s /q` 整个 `..\ForzaFatFishHUD-dist` 再重建：
  `config\`、`data\`（学习数据）、`logs\` 全删，**发布前要保留就先备份**
- 产物：`..\ForzaFatFishHUD-dist` + `..\ForzaFatFishHUD-releases\ForzaFatFishHUD-v{版本}-win64.zip`

## 4. 工作流约定（用户明确要求，必须遵守）

1. **先列未决项、等拍板，点头前不写一行代码。** 接到需求先给「必须你决定的 N 个问题」，
   一轮问完；能自己查的（API 签名、既有约定）自己查，不拿查得到的回来问。
2. **只在用户说「编译」时才编译。**
3. **涉及权限 / 热键 / 全局钩子 / 置顶的验证只编译，不代用户启动程序** ——
   Agent 起的进程可能继承沙箱低完整性令牌，结论是假的。
4. 用户说「不要改」时**只分析、只回答，不写任何文件**。
5. 改布局/尺寸后同步更新 `docs/HUD模块开发说明.md` 的基准尺寸。
6. 用户说过「不要扫盘」「静默等待」时不执行任何命令。
7. **不代做任何 git 写操作**（add / commit / push / 分支 / reset）。查状态可用 `git status` /
   `ls-files` / `check-ignore` 等只读命令，发现问题只报告。
8. **跨项目只读**：本仓库是 DSH 工作区下的一个项目（同级有 `design`、`ForzaOverlay - 副本`、
   参考代码仓库等）。整个工作区可读，**本项目之外一律不改**。
   `ForzaOverlay - 副本` 是重要资产，不要当冗余清理、不要提议删除。
9. **`temp/` 是备份区兼素材区**：改大文件前先备份 `temp\<文件名>.bak`；
   `.gitignore` 的 `**/temp/` 已忽略。**不要删 `temp/` 里的东西，也不要提议清理。**

## 5. 代码规范

- 类型声明以**显式类型**为主；仅右侧一眼可见时用 `var`，同文件内保持一致
- 中文注释解释「为什么」，不写「是什么」
- 日志走 `Diagnostics.Write` / `WriteException`，禁止 `Console.WriteLine`
- 新增 UI 元素后在初始化点加一条启动日志（`Diagnostics.Write`）
- **改 `.cs` 后必须跑 `lsp_diagnostics` 零错误再报告**；LSP 只是快速反馈，语义以 `dotnet build` 为准。
  它只对属于某个 csproj 的文件生效，`temp\` 等工程外文件永远返回 0 条（假阴性）
- 不引入新第三方包，除非用户同意
- **`.cs` / `.md` / `.py` 一律 LF + 无 BOM**；例外：`AGENTS.md` 全 CRLF、`.ps1` 需 BOM、
  `Publish.bat` / `Start.bat` 是 GBK + CRLF。`Set-Content` / `WriteAllLines` 默认写 CRLF，改完要验：
  ```powershell
  $b=[System.IO.File]::ReadAllBytes($p); $t=[System.Text.Encoding]::UTF8.GetString($b)
  "CRLF=$(([regex]::Matches($t,"`r`n")).Count) bareLF=$(([regex]::Matches($t,"(?<!`r)`n")).Count)"
  ```

### 5.1 命令行与编码

- **不要用内联 `-e "..."` 跑 Node/Python 脚本**：PowerShell 组装原生命令行会吃掉/错配引号，
  脚本没问题也报 `SyntaxError`。一律先写成临时 `.cjs` / `.py` 文件再执行。
- **读文件前先定编码**：读 JSON / 源码用 `-Encoding UTF8`；`Publish.bat`、`Start.bat`、
  `app-dist-template` 下的文件用 `-Encoding ansi`（GBK）。
- **大输出不进上下文**：账本、构建日志这类几万字内容先落盘再 `grep` / `head` 筛选。

## 6. HUD 布局关键约定

| 模块 | 基准尺寸 | 备注 |
|---|---|---|
| 行驶仪表 · 经典 | 820 × 150 | 单行横排，转速条横贯顶部，列宽固定 |
| 行驶仪表 · GHOST LADDER | 420 × 238 | 无底板，几何描边，数字定槽 |
| 行驶仪表 · SHIFT TREE | 820 × 320 | 双炮筒 + 中央 10 颗换挡灯与档位 |
| 行驶仪表 · 仿NFS19 | 601 × 416 | 主表 + 左侧增压小表；基准 = 包围盒 + 5px |
| 行驶仪表 · 咬地鲨 | 1054 × 722 | 三表集群；内部按原型 1440×720 写，靠 ContentShift 平移 |
| 行驶仪表 · ES | 500 × 500 | 原型 1000×1000，缩放系数 **0.47**（不是 0.5）；圆心固定 250 |
| 行驶仪表 · 极简弧环 | 312 × 330 | 内部坐标 = 原型 **1:1**，靠 ContentShift(-34,-20) 平移 |
| 行驶仪表 · 漂移锁链 | 304 × 289 | 原型 1:1 + ContentShift(-40,-36)；弧紫(268)→青(185) 逐段插值，红区斜纹 |
| 轮胎与悬挂 | 220 × 212 | 2×2 四格（已移除中心差速图） |
| HUD 资源占用 | 200 × 140 | 帧率 / CPU / 内存 / GPU / 显存 |

- 缩放**必须用基准宽高常量**，不能用 `ActualWidth`（切全屏瞬时为 0 会导致缩放爆炸）
  - ⚠️ **Y 只定位顶边，不定位底边**：下边缘 = `Y + WR × (视口宽/视口高) × 基准高/基准宽`。
    新模块的默认 Y 必须按这个式子倒推后写进注释，别凭感觉取整
- 面板内元素尺寸固定，禁止让数值宽度影响布局（会抖动）
- 转速条配色统一走 `Ui/RpmColorScale`（<76% 基础色 → 76~82% 基础→黄 `#FFB020` → 82~90% 全黄
  → 90~94% 黄→红 `#FF3B30` → ≥94% 全红）。新增带转速条的面板必须调用它。
  **五个例外**（不走 RpmColorScale）：SHIFT TREE 固定三段（73.3% / 86.7%）、咬地鲨三色制、
  ES 点阵青→红两态、极简弧环超红区转紫 `#c86bff`、漂移锁链紫→青渐变 + 红区斜纹
- 踏板四色：离合紫 `#B388FF`（半透明）/ 油门蓝 `#4FC3F7` / 刹车红 `#F44336` / 手刹黄 `#FFEB3B`
- 轮胎颜色来自 `TireVisualEvaluator`：填充=胎温，描边=抓地（打滑黄、失控红闪）
- HUD 面板全部由 `Rendering/HudScene.cs` 用 Direct2D 绘制，**不要再新增 WPF HUD 窗口**
- GPU 场景坐标：`scale = WidthRatio × 客户区宽 / 基准宽`，再加客户区偏移
- 数字定槽：SHIFT TREE 逐位 33 / 36，GHOST 速度 63 槽位，极简弧环速度 23 槽位，漂移锁链速度 22 槽位
- **增压数值显示标准（全项目，2026-11-26 用户定）**：两位小数（`0.00` 也显示）、正数带 `+`、
  字符数恒为 5、逐格定槽防跳动。位数常量唯一来源 `HudDraw.BoostDecimals`；
  格式化走 `HudDraw.Signed()`（内部先消 float 误差再截断 —— `-0.35` 存成 `-0.34999999…`，
  直接截断会变成 `-0.34`）；格宽用 `HudDraw.BuildSignedCells()`。
  已有四处：极简弧环、漂移锁链、GHOST、经典仪表「增压」行
- 文字：D2D `TextAntialiasMode = Grayscale`；GHOST 速度数字用 D-DIN 字形轮廓 + 2px 描边

## 7. 运行时权限经验

- 全局热键 `RegisterHotKey` 受 **UIPI** 限制：低完整性进程在更高完整性窗口（游戏）前台时收不到 `WM_HOTKEY`
- 完整性级别：普通用户 `S-1-16-8192` / 管理员 `S-1-16-12288` / 沙箱可能降到 `S-1-16-4096`
- 游戏从被提权的 Steam（加速器）启动会继承管理员权限
- 程序内有横幅：自身权限低于游戏时在「遥测选项」页顶部提示并提供「以管理员身份重启」
- 实现见 `Services/ProcessPrivilege.cs`，用 `TokenIntegrityLevel` 比较

## 8. 数据与行为备忘

- 遥测包固定 324 字节：`IsRaceOn` 偏移 0，`Accel/Brake/Clutch/HandBrake` 315–318，`Gear` 319
- 车库 / 车展里 `IsRaceOn` 仍为 1 且可轰油门 → **无法用遥测区分「车库」和「停车」**
- **档位有效值只有 1~10**；`0`/`-1` 是空档/倒挡，**`11` 是换挡中**
  - 断油检测、升挡采样、最高档判断都只接受 1~10，否则换挡慢的车永久学不到
  - `MaxGearSeen` 曾漏掉这层判断（12 辆车全记成 11），`Load` 时归零越界值
- 学习数据存 `data\shift-learning.json`：`CutValues`、`MaxWotRpm`、`MaxGearSeen`、`Shifts`
- **车辆档案键 `BuildKey` 不许含 `EngineIdleRpm`**（2026-11-28 修）：怠速是**会漂移的实时读数**
  （冷车/进菜单读到 1011，开起来回落到 800），拿它当键会让**同一辆车分裂成两个档案**、
  写入时来回切换 —— 实测 car 3886 同时存在 `...|9000|1011|...`（空）和 `...|9000|800|...`（有数据），
  表现为「红区忽大忽小、玩久了变小」。键只能放静态属性：
  `CarOrdinal` / `CarClass` / `CarPerformanceIndex` / `EngineMaxRpm`（取整后稳定）/ `DrivetrainType` / `NumCylinders`
- **升挡样本池 = 全局最近 10 次换挡，不分档位**（2026-11-28 用户拍板）：
  - **不要按档位设配额**（原来的 `MaxShiftSamplesPerGear = 5` 已删）：一辆车跑起来根本落不到低档，
    实测 955 条样本里 1~3 档占 48.4%、7~10 档只占 15.9%，单场会话 9→10 档常常只有 3 次。
    按档配额会让低档位占满、高档位长期只有 1~2 条，红区跟着当前档位来回跳
  - **不要用「同档位 60 转内视为重复」去重**：实车同档换挡点极差才 80 转左右，
    这条会把正常波动全当重复丢掉 → 每档永远只剩 1 条 → 「最近 3 条」实际是「3 个档位各 1 条」
  - **不做偏离检测 / 不做主导簇**（用户明确否决）：换调校常常只改换挡逻辑、共用同一款发动机，
    换挡转速差很小，判不出来；真换调校时转速差可能也很小，硬判会误伤。
    改成纯粹按时间滚动 —— 新样本进来、最老的自动挤掉，不需要任何「是否换调校」的判断
- **红区 = 断油优先**（2026-11-22 定）：学到断油就用 `Max − (Max−断油) × k`，
  断油/Max < 0.88 用 k=1.9 否则 1.7，只跟断油走、采集再多也不漂；
  学不到断油（自动挡，游戏在红区前就升档）才用最近 3 条升挡中位数；都没有用 0.90；夹 0.50~0.98
  - **绝不要拿升挡点当手动挡红区**：玩家照换挡提示（断油×0.98）换挡，升挡样本天然挤在断油下方，
    会让红区随驾驶习惯逐步变窄、第 4 颗换挡灯在断油前永不亮
- **`GetRedlineRatio()` 的契约是「占 `maxRpm`」，不是「占表盘量程」**（2026-11-28 修）：
  单表盘的弧/刻度按 **`spanRpm`（千转向上取整，9500 → 10000）** 画，两者分母不同。
  直接把 learner 的返回值当弧比例用，红区会被抬高 `(span−max)/max`（9500 转车 = **4.3 个百分点**），
  表现为「红区起点偏右、红区带变窄」。**ES / 极简弧环 / 漂移锁链 三个仪表都踩过这个坑**，
  而咬地鲨一直是对的。正确写法只有一种：
  ```csharp
  Math.Clamp((float)ratio * maxRpm, 0f, spanRpm) / spanRpm   // 先换 rpm，再夹量程，最后除回比例
  ```
  新写单表时**照抄 `AltezzaScene.ResolveRedline`**，不要自己再推一遍。
  排查手法：从实机截图做**像素扫描**量红区斜纹起点的弧长比例，与 `ratio × max ÷ span` 对照
  —— 9500 转车实测 86.00% vs 应有 81.70%，差值就是那 4.3pp
- **饱和断油值（≥99% Max）读取时也要过滤**：存量脏值（car 1124 = 7960 = 99.5% max）
  套公式会得到 98% 的报废红区。`GetCutRpmInfo` 视为「没学到」，回落升挡中位数
- **`MaxWotRpm` 被污染会让某辆车永久学不到**：饱和过滤 `rpm >= maxRpm×0.99` 丢窗口且不更新
  `MaxWotRpm`，而候选门槛要求 `windowMax >= MaxWotRpm×0.99`。一旦 `MaxWotRpm` 被强行降档拖满表
  （1124 实测 7999.995），两个 0.99 门槛**数值上恰好相等** → 样本 100% 被拒，且只增不减、永久落盘。
  排查先算两个门槛，相等就是死锁；**这不是「老数据太多」**（坏档案只有 1 条 CutValues）。
  出口：设置页「清空自学习数据」（`ShiftPointLearner.ClearAll()`）或直接删 json。
  清档后只正常升挡（6600 转、`MaxWotRpm` 仅 86%）**学不到断油是正常的**
- **断油检测门槛必须分档**（2026-11-21 修，4~5 档低动力车原本永远学不到）：
  - 车速容差：≤3 档 5 km/h、≥4 档 1.5 km/h（断油只切推力不断惯性，低档一个周期仍涨 1.7~2.6）
  - 锯齿幅度：`maxRpm < 7500` 用 **45 rpm**（小马力车复油回升软）
  - 贴峰值容差 `amplitude × 1.5`，不要写死 `CutExtensionRpm × 2`（=80）
  - 油门门槛 0.85（手柄扳机有死区）；防误判主力是**转速平台 `drift ≤ 40`** 与 **`falls ≥ 3`**
- **自动挡不会进入断油**：走「升挡点中位数」路径，别去调断油阈值救它
- 换挡提示 4 灯：绿常亮 → 黄（红区前 10%）→ 红（红区起点）→ 红（最佳点 = 断油 − Δ×0.28）
- 数值格式化：**截断**（`MidpointRounding.ToZero`），不是四舍五入
- **`EngineMaxRpm` 在菜单/车库/暂停时游戏不发（为 0）**，必须沿用最后一次有效值，不能兜底 8000，
  否则进出菜单时量程与指针会跳变。因此指针 clamp 到 1.0 物理上永不触发

## 9. 不要做的事

- 不加托盘图标（已按要求移除）
- 不重新引入 WPF HUD 渲染
- 不在定槽之外再加额外间距计算（会引入抖动）
- 不打包 Windows 自带字体（Bahnschrift / Consolas），只能引用字体名
- 不把 `temp/` 设计稿当最终规格，以 `docs/` 和代码为准
- **新增 `.gitignore` 规则后要查是否误伤源码**：`*.app/` 曾把整个 `ForzaOverlay.App/` 排除，
  32 个源文件一度未进仓库而 `git status` 干净。核对用 `git ls-files -- <目录>`，
  **要看文件个数，不能只看得到 csproj**。另注意 `!` 否定规则对被排除目录无效

## 10. 技能环境

- 本机装有 `dsh-office`（文档类）与 `dotnet-skills`（.NET 类）两套技能包
- **按需加载**，任务不匹配就不加载
- 技能清单在会话启动时注入，改配置后要新开会话才可见

## 11. 待办（已确认要做，不在本次提交范围）

- **SHA256 校验**：下载完成后校验 Release 附带的 `.sha256`（在 `UpdateService.DownloadAsync` 之后）
- **「自动检查更新」开关**：设置项 `AutoCheckUpdates`（当前固定启动检查一次）
- **配置保护**：更新脚本解压时排除 `config/`
- **配置版本与迁移**：`config.json` 加 `ConfigVersion`，按版本迁移旧字段
- **升级前备份**：更新脚本先复制 `config.json.bak`，迁移失败可回滚
- **更新下载源国内分流**：优先 Gitee Release 镜像（免费），多源回退（镜像优先 → GitHub 兜底），
  全失败才提示手动下载。备选 ghproxy / Cloudflare R2 / Vercel

## 12. 性能日志（卡顿诊断，默认关闭）

关闭时渲染线程第一行就返回，零开销。开关在设置 → 运行日志 → 最下方；另有「标记此刻」写 `MARK`。

- 产物 `logs\perf\perf-yyyyMMdd-HHmmss.csv`：每秒一行 29 列（帧耗时 p50/p99/max、draw/Present 拆分、
  掉帧、GC、分配量、CPU/本进程 GPU/整机 GPU/显存、工作集、线程/句柄、收包与包间隔、包到画面延迟、
  扫描耗时）。16 MB 滚动，10 份，保留 7 天。文件头 `# env=` 记录版本/系统/CPU/显卡/分辨率/刷新模式
- 实现 `Core/Diagnostics/PerfRecorder.cs`（命名空间 `Core.Logging`）。渲染线程只累加内存，
  **每秒结算与写盘都在后台线程** —— 曾因把每秒结算放回渲染线程造成 400 ms 卡顿。
  热路径禁止 `Diagnostics.Write`（每次调用都开关文件）
- 判读：帧时间平/包间隔涨 = 游戏侧顿；两者同步涨 = 我们在拖后腿，再看 draw 与 Present；
  `gc2` 与 `gc_pause_ms` 对齐 = GC；`cpu`/`scan_ms` 出现 10 秒节拍尖峰 = 进程扫描或写盘
- **枚举 GPU 性能计数器非常贵，必须门控 + 缓存 + 降频**：`new PerformanceCounterCategory(...)`
  再 `GetInstanceNames()` 每次分配 353 KB（`GPU Engine` 本机 575 个实例），
  单个 `NextValue()` **134.6 KB** —— 遍历一遍全部实例就是 **77 MB**。
  2026-11-28 实测 perf 日志抓到 `alloc_kb` 每 2 秒一次 84 MB 尖峰（全落在奇数秒），
  C# 基准定位到 `PerformanceMonitor.RefreshSystemGpu` 的遍历循环：每秒 42 MB、约 150 GB/小时。
  **垃圾全在 Gen0，所以 `ws_mb` 和工作集看不出问题 —— 只有 `alloc_kb` 列能看见**；
  不要因为「内存没涨」就认为没问题。三道闸门缺一不可：
  ① 实例名 30 秒节流重扫；② 没人看时整段跳过（`SetGpuReadingsNeeded`，
  注意 `CaptureSystemGpu` 这条路**最容易漏门控** —— 它由 PerfRecorder 回调，
  容易只检查了面板开关就忘了它）；③ 整机 GPU 汇总 10 秒采一次即可，别 2 秒
- **门控关掉时要连计数器一起 `Dispose`**：只设标志位不释放，`SystemGpuCounters` 里
  几百个 `PerformanceCounter` 会一直挂着（实测建 575 个 +171 个句柄）
- 局限：测不到游戏自身帧时间与 DWM/驱动层，包间隔只是代理；要更细得用 PresentMon 或 WPA

## 13. tools/ 验证工作流（改绘制代码必须走）

**改 HUD 绘制不要靠开游戏看效果**，出图 + 量图闭环。完整用法见 `tools/README.md`。

```powershell
dotnet run --project tools/snapshot --style ring --width 312 --height 330 -o temp\snapshots\r.png
dotnet run --project tools/snapshot --preset extremes --style altezza --width 1054 --height 722
```

- `tools/snapshot` 独立 csproj、**不进 `.sln`**，主程序编译发布不受影响
- 靠 `InternalsVisibleTo("snapshot")` 访问 internal 类，**不要把场景类改 public 图省事**
- 数据全部确定值（`--rpm` `--speed` `--gear` `--boost`），同命令永远同图才能对比
- **视口必须等于输出画布**：视口更大时会放大裁切，相等时 `scale` 才恰为 1
- 支持 `altezza` / `nfs19` / `es` / `ring` / `driftchain`；经典 / GHOST / SHIFT TREE 在 `HudScene` 内、依赖
  `AppServices`，暂未接入
- Python 用 DSH 自带解释器（`%USERPROFILE%\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe`），
  PATH 里的 `WindowsApps\python.exe` 是商店占位符不能用
- **配色用主色分析（`colors.py`），不要用像素差率**；**比对前两张图必须尺寸一致**
- **透明背景分析前必须 `--flatten`**，不要启发式猜 alpha 是否预乘（需要时显式 `--premultiplied`）

## 14. HUD 绘制易错点（编译器查不出，只能比对图发现）

完整推导见 `docs/HUD模块开发说明.md` 与 `tools/README.md`，这里只列结论。

- **角度制两套勿混**：咬地鲨 / ES / 极简弧环 / 漂移锁链用 `cos/sin`（0°=正右）；仿NFS19 / SHIFT TREE 用
  `sin/-cos`（0°=正上）。混了指针整体偏，编译零错误
- **原型写死值是配它那份固定量程的，别当常量搬**：量程自适应后，量程派生的数量（刻度、标签、分段、红区位置）都按实际量程现算
- **`Matrix3x2` 行向量约定**：`A * B` = 先 A 后 B。绕点旋转必须 `T(-P) * R * T(+P)`，写反会把对象
  甩出画布（ES 指针曾整根消失，扫描才发现 0 像素）；沿弧排文字字顶朝圆心用 `θ = a - 90°`
- **缩放系数与画布坐标分开算**：半径/字号乘系数，**圆心不乘**。ES 真实系数 0.47（`.wrap` 只占 94%），
  圆心写死 `BaseWidth * 0.5`；判定靠量**非透明像素包围盒中心与外径**，不要靠肉眼
- **HTML 原型必须渲染出来比对**（`tools/render.py --shot`），不要只读源码推算几何
- **D2D 画刷 / 几何缓存禁用 `using`**：`GetXxx(target)` 返回缓存对象，包住会在帧末 Dispose，
  编译不报错、下一帧崩
- **每帧新建原生几何会抬高工作集**：D2D 的 `PathGeometry` 是 COM 对象，逐帧建-销不算泄漏
  （句柄不涨），但高频会让分配器碎片化、工作集抬到一档就不回落。分段绘制的稳定段应按
  角度缓存几何（`ShiftTreePanel._arcCache` 是现成范例），只现建真正随数值变化的那一段
- **判 COM 包装对象身份要比 `NativePointer`，不能用 `ReferenceEquals`**：`target.Factory`
  每次访问都新建包装实例（引用不同、`GetHashCode` 与 `NativePointer` 相同）。
  拿引用判断「工厂换没换」会导致缓存每帧被清空重建，性能一点不改善
- **SVG → D2D 文字补偿**：`SetCharacterSpacing` 单位与文档不符（咬地鲨 22 号字实测 1 单位 ≈ 0.844px，
  随字号变，改字号要重标）；SVG 的 `y` 是基线而 `CenteredText` 按行高居中，差约半个行高
  （`TachoUnitBaselineFix`）；走旋转矩阵的 `DrawDialText` 不偏
- **搬运大段绘制代码后逐行比对 `CreateFormat`**：`SemiBold` 写成 `Bold`、`TextAlignment` 写错，
  编译器一律查不出
- **`Boost` 负值是真实物理量**（进气歧管真空），除面板明确要求归零（咬地鲨小表无负刻度）一律显示原值；
  有数字刻度的主表**只夹下界 0、不夹上界**，指针绕回属正常。涡轮/电压/水温小表仍 clamp
- **测指针角度不要靠颜色阈值**：红区刻度也是红色会被算进指针，两表指针还会互相干扰。
  用 `--preset extremes` 出图目视 + `角度 = 起角 + ratio × 扫掠`（折回 360°）核对
- **「默认位置」先问清映射再推导**：水温表曾把 C–H 中点当 100℃ 写死，实际 C=0℃ / H=180℃。
  装饰表涉及数值时先问两端含义，推导式写成常量