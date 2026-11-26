# tools — ForzaOverlay 的验证工作流

把「改一行代码 → 启动游戏 → 用眼看」变成「改一行 → 出一张图 → 量一遍」。

## 完整链路

```
C# 绘制代码
     │
     ▼  snapshot （本项目专属，C#）
  渲染 PNG
     │
     ├── measure.py   量几何（数齿 / 找分界 / 定位小表）
     ├── colors.py    取色（验证三色制）
     ├── diff.py      找差异（改前 vs 改后）
     ├── crop.py      放大看细节
     ├── extract.py   抠图
     ├── trace.py     位图 → SVG path
     └── render.py    渲染 HTML 原型（需完全权限）
```

## 一句话上手

```bash
# 1. 出图（1:1 按基准尺寸，不缩放）
dotnet run --project tools/snapshot --style altezza --width 1054 --height 722 -o temp/snapshots/a.png

# 2. 看图
#    直接把 PNG 读进来目视

# 3. 量图
python tools/measure.py arc temp/snapshots/a.png --cx 692.7 --cy 360.7 \
       --ref-r 354.2 --r0 0.68 --r1 0.745 --percentile 70
```

## 需要什么

**全部用 DSH 自带的 Python 跑**，本机不需要另装 Python：

```
C:\Users\ZC\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe
```

PATH 里那个 `WindowsApps\python.exe` 是商店占位符，**不能用**。

| 脚本 | 权限 |
|---|---|
| `measure.py` `colors.py` `diff.py` `crop.py` `extract.py` `trace.py` | `workspace-write` 即可 |
| `render.py` | **需要完全权限**（要起浏览器，命名管道会被拦） |
| `snapshot`（C#） | `workspace-write` 即可，但要能跑 `dotnet run` |

`render.py` 报 `platform_channel.cc: 拒绝访问` 就是权限不够，直接告诉用户开完全权限，不要换方式硬闯。

---

## ⚠️ 本项目最大的坑：HUD 是透明背景

**任何图像分析之前必须先处理 alpha**，否则结果全是错的。这不是预防性提醒，`measure.py` 的 `lum<70` 掩膜会把整片透明区判成「深色前景」，数齿、找小表直接失效。

两个办法：

1. **交给脚本处理**（推荐）：所有脚本都支持 `--flatten '#000000'`，默认就是黑底合成
2. **看图时不必处理**：`read_image` 之类能正确显示透明图

```bash
# 默认：透明区按黑色算
python tools/colors.py a.png --top 6

# 换成白底，看清哪些是「透明」
python tools/colors.py a.png --top 6 --flatten '#FFFFFF'

# 输入本身是预乘 alpha 时才要这个（快照工具输出的是直通，不需要）
python tools/colors.py a.png --premultiplied
```

**不要靠猜判断是否预乘。** 暗色图（RGB 全部小于 alpha）会被启发式误判成预乘，导致颜色偏亮。需要时显式给开关。

---

## snapshot —— 离屏快照（本项目专属）

```bash
dotnet run --project tools/snapshot --style <altezza|nfs19|es|ring|driftchain> -o <输出.png> [参数]
```

**工程约束**：独立 csproj、**不进 `.sln`**，主程序的编译与发布完全不受影响。
它靠 `InternalsVisibleTo("snapshot")` 访问 `AltezzaScene` / `Nfs19Scene` / `EsScene` / `RingScene` / `DriftChainScene`
等 internal 类 —— **不要把场景类改成 public 来图省事**。
经典 / GHOST / SHIFT TREE 的绘制写在 `HudScene` 内部、依赖 `AppServices`，暂未接入。

### 为什么视口等于画布

`scale = WidthRatio × 视口宽 / 基准宽`。如果视口比画布大，面板会被放大并裁切。
snapshot 把**视口设成和输出画布一致**，于是 `WidthRatio=1` 时 `scale` 恰好为 1，
**输出图像的像素坐标就是设计坐标**，量出来的数可以直接对回 C# 常量。

### 参数

| 参数 | 说明 | 默认 |
|---|---|---|
| `--style` | `altezza` / `nfs19` / `es` / `ring` / `driftchain` | `altezza` |
| `--preset` | `extremes`：一次渲染 8 个边界用例 | 无 |
| `-o, --output` | 输出 PNG | 必填 |
| `--width` / `--height` | 输出尺寸 | 1100 × 760 |
| `--rpm` | 当前转速 | 6500 |
| `--max-rpm` | 最大转速 | 8000 |
| `--speed` | 时速（km/h） | 200 |
| `--gear` | 档位：-1=R、0=N、1~10、11=换挡中 | 4 |
| `--boost` | 增压 bar，可负；`none` 表示不指定 | 1.2 |
| `--throttle` / `--brake` | 0–255 | 200 / 0 |

数据全部是**确定值**，不用随机数或正弦动画 —— 同一个命令永远出同一张图，这样才能对比。

### 常用场景

```bash
# 基准尺寸 1:1，用来量几何
dotnet run --project tools/snapshot --style altezza --width 1054 --height 722 -o a.png

# 10 档 + 高速：验证档位双格与指针爆表
dotnet run --project tools/snapshot --style altezza --rpm 7800 --speed 500 --gear 10 -o fast.png

# 负压（进气真空）：验证增压小表归零策略
dotnet run --project tools/snapshot --style altezza --boost -0.4 -o vac.png

# 边界批量：一次跑 8 个极端用例（速度 0/999、档位 R/10、增压 ±3、转速 0/满）
dotnet run --project tools/snapshot --preset extremes --style ring --width 312 --height 330
```

改完绘制代码先跑一遍 `--preset extremes`，比逐个手敲参数快，也不会漏掉边界。

---

## measure.py —— 量图

四个子命令，输出统一 JSON。

```bash
# 按半径做圆周统计：找银盘 / 齿环 / 数字环的分界
python tools/measure.py radial 图.png --cx 313.5 --cy 319 --ref-r 211.2 --r0 0.9 --r1 1.4

# 单条射线逐像素：定精确的径向分界点
python tools/measure.py ray 图.png --cx 313.5 --cy 319 --ref-r 211.2 --angle 45 --r0 0.95 --r1 1.15

# 角向数齿 / 量刻度节拍
python tools/measure.py arc 图.png --cx 313.5 --cy 319 --ref-r 211.2 --r0 1.045 --r1 1.058 --sweep 9

# 连通域：找小表、数字块、圆点
python tools/measure.py blobs 图.png --ref-r 211.2 --mask "lum<70" --min-area 3000
```

### 三个关键约定

**`--ref-r` 基本必填。** 所有径向结果同时给 `r_px` 和 `r_rel`（= px / ref-r）。
本项目有多个基准（转速表 R=272、速度表 R=354.2、小表 R=84.8），**混用会造成严重错误**。
统一在 `--ref-r` 里声明基准，输出就带上了 `rel`，不用心算。

**掩膜是迷你 DSL。** `--mask "lum>150,warm>20,sat<100"`，逗号分隔、全部同时满足。
可用通道：`lum` / `sat` / `warm` / `r` / `g` / `b`。注意 `blobs` 支持 `--mask`，`arc` **不支持**。

**`arc` 的阈值要按图调。** 默认 `--percentile 50` 在暗盘上会严重欠检（实测只得 1 颗）。
咬地鲨齿环实测最佳值是 **`--percentile 70`**，得 89 颗（实际 90，漏 1 属正常）。

### 三表要分别传圆心

一张图里有多个圆心，`measure.py` 一次只能给一个，所以要跑多次。
咬地鲨在**基准尺寸 1054×722** 下的圆心（= 设计坐标 − `ContentShift(-194.1, 0.7)`）：

| 表 | 圆心 | ref-r | 依据 |
|---|---|---|---|
| 转速表 | (277.0, 413.6) | 272 | 设计值推导 |
| 速度表 | (692.7, 360.7) | 354.2 | 设计值推导 |
| BOOST 小表 | (695.9, 221.8) | 84.8 | 设计值推导（**用 blobs 实测会被指针干扰，偏 9px**） |
| 电压小表 | (833.6, 358.8) | 84.8 | ✅ blobs 实测 (833, 358.5)，误差 <1px |
| 水温小表 | (695.5, 496.9) | 84.8 | ✅ blobs 实测 (695, 496.5)，误差 <1px |

电压与水温两个小表的实测值与推导值吻合到 1px 以内，
**这同时验证了 `ContentShift` 平移计算是正确的**。

> 用 `blobs` 反推圆心只在不被指针遮挡时可靠：BOOST 表有指针穿过，
> 连通域被切断，质心会偏；水温表的指针也在，但因为遮挡位置对称、偏差很小。
> **要精确圆心，以设计值为准，不要用 blobs 反推。**

## colors.py —— 取色

```bash
# 区域主色（带占比）
python tools/colors.py 图.png --region 300,200,400,300 --top 8

# 验证已知色值对不对
python tools/colors.py 图.png --candidates "#EBD2B2,#D5AD4A,#D91A24" --ignore-dark 60
```

`--candidates` 报告每个候选色的命中像素数。**实测咬地鲨三色全部精确命中**：
奶油白 16548 px、金色 7366 px、红色 5760 px（1054×722 基准图）。

`--ignore-dark 40` 用来滤掉黑背景，否则背景会淹没调色板。

## diff.py —— 找差异

```bash
python tools/diff.py 改前.png 改后.png --grid 8 --top 10
python tools/diff.py a.png b.png --heatmap 差异.png
```

输出**定位**（网格坐标 + 差异框），不是评分。
看框在哪，别追分数。

## crop.py —— 裁剪拼接

比单张裁剪好用的是**多块拼一张图**，带坐标标注。

```bash
# 单块放大
python tools/crop.py 图.png --region 114,310,263,332 --scale 4 -o 字标.png

# 一次切多块，2x2 拼接 + 标注坐标（看多个小表首选）
python tools/crop.py 图.png --region 230,110,400,290 --region 350,240,520,410 \
                         --scale 2 --grid 2x2 --label -o 四表.png

# 以某点为圆心的方窗
python tools/crop.py 图.png --around 692.7,360.7 --size 200 --scale 2 -o 中心.png
```

`--resample nearest` 可以看原始像素（放大不插值），用于判断抗锯齿。

## extract.py —— 抠图

```bash
python tools/extract.py 图.png --region 114,310,263,332 --auto-bg --tolerance 45 --largest -o 字标.png
```

- `--auto-bg`：四边中位色当背景，通常不用再调
- `--largest`：只留最大连通块，去掉零碎噪点
- `--feather 1`：边缘羽化；要硬边给 0

## trace.py —— 描图

位图 → SVG path。

```bash
python tools/trace.py 参考图.png --region 114,310,263,332 --scale 6 --threshold 88 -o 字标.svg
```

- `--scale 6`：原图太小时先 LANCZOS 放大，边缘更干净
- `--eps 0.9`：RDP 容差，单位是放大后的像素
- **`fill-rule: evenodd` 必须保留**，否则字母内部镂空会被填死

## render.py —— 渲染 HTML

```bash
python tools/render.py page.html --shot out.png --width 1440 --height 720
python tools/render.py page.html --svg out.svg --svg-id cluster --wait 2500
```

用于渲染设计稿 HTML 做对照。**ForzaOverlay 本身是 C# 绘制，不走这条**。

---

## 已知局限

- `arc` 数齿会漏检 1–3 个（齿尖抗锯齿会让最细处断档）。咬地鲨实测 89/90。
  **要精确数齿，用生成的 C# 源码数，不要从截图数。**
- `blobs` 用纯 Python 走栈，大区域会慢；加 `--region` 限定范围。
- snapshot 支持 `altezza` / `nfs19` / `es` / `ring` / `driftchain` 五种样式；经典 / GHOST / SHIFT TREE 的绘制代码写在 `HudScene` 内部、依赖 `AppServices.CurrentSettings`，暂未接入。

## 两条测量原则

- **配色用主色分析（`colors.py`），不要用像素差率** —— 3px 的色差在整体差异率里占比极低，
  但视觉上很刺眼，看差率永远看不出配色错了。
- **比对前两张图必须尺寸一致**，否则会被 scaled 或差异率虚高，没有参考价值。

另外量文字墨迹时：彩色元素（红指针、金刻度）会被亮度阈值算进墨迹，造成「文字被拉宽」的假象；
按颜色通道过滤（如 `r>150 & g>110 & b>60`）或先排除红色，测量窗口也要给足余量。

## 与 design 项目的关系

这套工具来自 `D:\workspace\design\_tools`。本项目复制后做了三处改动：

1. 删掉 `validate-altezza.cjs`（校验的是 design 项目的 HTML，路径跨项目）
2. 删掉 `trace-lexus.py`（与 `trace.py` 重复，源图在 design 项目）
3. **新增 `imgload.py`**：统一处理透明背景合成，四个脚本共用

**design 项目里那份保持原样，不要反向同步**，两边的需求不同。
