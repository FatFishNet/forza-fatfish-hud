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
    ShiftTreeScene.cs          全部 HUD 面板的 Direct2D 绘制（经典/GHOST/SHIFT TREE/轮胎/资源）
  Ui/SettingsWindow.xaml(.cs)  设置中心 + 关于页（含检查更新）
  Ui/UpdateWindow.xaml(.cs)    更新说明与下载窗口
  Ui/HudLayoutWindow.xaml(.cs) 布局编辑器
  Ui/HudModuleCatalog.cs       模块清单（名称 / 基准尺寸 / 显隐条件）
  Services/                    UDP 监听、全局热键、进程监视、权限检测、更新检查/应用
  Assets/Fonts/                内置字体 D-DIN（OFL 授权）
  Assets/Images/               图标与角色形象素材
src/ForzaOverlay.Core/         无界面逻辑：配置、遥测解析、网络、监控、视觉规则
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

## 4. 工作流约定（用户明确要求，必须遵守）

1. **先给方案，等用户确认后再动手改代码**。不要边想边改。
2. **只在用户说"编译"时才编译**。用户自己会编译，擅自编译会打断他。
3. **涉及权限 / 热键 / 全局钩子 / 窗口置顶的验证，只编译，不要代用户启动程序**。
   Agent 启动的进程可能继承沙箱的低完整性令牌，与用户真实运行环境不等价，测出来的结论是假的。
4. 用户说"不要改"时，**只分析、只回答，不写任何文件**。
5. 修改布局/尺寸后，同步更新 `docs/HUD模块开发说明.md` 里对应的基准尺寸。
6. 用户明确说过"不要扫盘""静默等待"时，不执行任何命令。

## 5. 代码规范

- 类型声明以**显式类型**为主；仅在右侧能一眼看出类型时使用 `var`，并保持同一文件内风格一致
- 中文注释用于解释"为什么"，不写"是什么"
- 日志统一走 `Diagnostics.Write` / `Diagnostics.WriteException`，禁止 `Console.WriteLine`
- 新增 UI 元素后，在 `OnOverlayLoaded` 之类的初始化点加一条启动日志
- 不引入新的第三方包，除非用户同意

## 6. HUD 布局关键约定

| 模块 | 基准尺寸 | 备注 |
|---|---|---|
| 行驶仪表 · 经典 | 820 × 150 | 单行横排，转速条横贯顶部，所有列宽固定 |
| 行驶仪表 · GHOST LADDER | 420 × 238 | 无底板，几何描边，数字定槽 |
| 行驶仪表 · SHIFT TREE | 820 × 320 | 双炮筒：左转速弧、右车速 + 油门/刹车半弧，中央 10 颗换挡灯与档位 |
| 轮胎与悬挂 | 220 × 212 | 2×2 四格（已移除中心差速图） |
| HUD 资源占用 | 200 × 140 | 帧率 / CPU / 内存 / GPU / 显存 |

- 缩放计算**必须使用基准宽高常量**，不能用 `ActualWidth`（切全屏时布局瞬时为 0 会导致缩放爆炸）
- 面板内所有元素尺寸固定，禁止让数值宽度影响布局（会随数据变化抖动）
- 转速条配色统一走 `Ui/RpmColorScale`（< 76% 基础色 → 76%~82% 基础→黄 `#FFB020` → 82%~90% 全黄 → 90%~94% 黄→红 `#FF3B30` → ≥ 94% 全红）；新增带转速条的面板必须调用该类，不得自行写死阈值或黄红颜色。例外：SHIFT TREE 的转速弧按设计稿固定三段（< 73.3% 青、73.3%~86.7% 黄、≥ 86.7% 红）
- 踏板四色：离合紫 `#B388FF`（半透明）/ 油门蓝 `#4FC3F7` / 刹车红 `#F44336` / 手刹黄 `#FFEB3B`
- 轮胎颜色来自 `TireVisualEvaluator`：填充=胎温，描边=抓地（打滑变黄、失控变红闪烁）
- HUD 面板全部由 `Rendering/ShiftTreeScene.cs` 用 Direct2D 绘制，不要再新增 WPF HUD 窗口
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
- 档位只接受 `-1 ~ 10`，超出范围的值视为换挡瞬间的脏数据，直接丢弃
- 数值格式化约定：**截断**（`MidpointRounding.ToZero`），不是四舍五入——用户明确要求保持现状

## 9. 不要做的事

- 不要添加托盘图标（已按用户要求移除）
- 不要重新引入 WPF HUD 渲染（已整体移除，WPF 仅保留设置界面与布局编辑器）
- 不要为数字加固定槽位之外的额外间距计算（会引入抖动）
- 不要打包 Windows 自带字体（Bahnschrift / Consolas），只能引用字体名
- 不要把 `temp/` 里的设计稿当作最终规格，以 `docs/` 和代码为准

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

### 当前已启用的 9 个

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

### 规则

1. **其余 dotnet 技能当前处于禁用状态**。需要用到（例如 `microbenchmarking`、`dotnet-trace-collect`、
   `coverage-analysis`、`msbuild-antipatterns`、`migrate-nullable-references` 等）时，
   **先向用户申请，得到同意后再改配置启用**，不要擅自放开。
2. 启用方式：把技能名从 `global.json` 的 `skills` 数组里删掉；改动前先备份为 `.bak`。
3. 改完技能目录会热重载，但**新会话**最稳妥。
4. 技能按需加载，任务不匹配时不要加载，以节省 token。

---

## 11. 待办（备忘）

以下事项已确认要做，但**不在本次提交范围内**：

- **SHA256 校验**：更新下载完成后校验 Release 附带的 `.sha256` 文件，防止下载被篡改（在 `UpdateService.DownloadAsync` 之后校验）
- **「自动检查更新」开关**：设置项 `AutoCheckUpdates`（当前固定启动检查一次）
- **README 截图**：`docs/images/hud-screenshot.png` 占位待补
- **GitHub Release v1.0.0 发布后实测自动更新流程**

