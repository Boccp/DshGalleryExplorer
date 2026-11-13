# DSH Gallery Explorer v2 (DSH 画师素材库与归档文件管理器)

一款专为高分辨率画师图集、漫画单行本管理、视频素材筛选与 AI Agent 协同任务调度打造的 **4K 原生超清虚拟网格文件管理器**。
采用纯原生 C# WinForms + GDI+ / WIC / WPF 混合硬件加速架构，零第三方 NuGet 依赖，使用 Windows 自带 `csc.exe` **0.3 秒完成编译**。

---

## v2.0 核心特性与实测性能突破

完整架构说明见 [docs/architecture.md](docs/architecture.md)，实测基准数据见 [docs/perf-baseline.md](docs/perf-baseline.md)。

### 1. 4K 高分屏超清虚拟网格与 WIC 多核无锁缩略图引擎
- **Per-Monitor V2 DPI 原生适配**：适配 2K/4K 高分屏缩放，字体、圆角卡片与缩略图边缘锐利清晰。
- **WIC 原生无锁并发解码**：采用 Windows Imaging Component 文件头尺寸探测 + JPEG IDCT 解码期硬件降采样 + 多核定点双线性缩放，202 项冷启动缩略图加载从 **12.5 秒降至 0.58 秒（提速 21 倍）**，磁盘缓存热加载仅需 **54 ms（3750+ 项/秒）**。
- **不透明卡片模板与 1:1 极速贴图**：预渲染 `Format32bppRgb` 卡片与四色外环模板，1080p 全缩放档位稳定 **95–126 fps**，4K (`3840×2160`) 视口稳定 **39–47 fps（p50 达 58 fps）**，相比 v1 提速 **8–12 倍**。
- **素材库 100% 零污染**：缩略图、视频胶片、标签账本、排序记忆与日志全部集中在 `%USERPROFILE%\.dsh\gallery_cache`，绝不在画师素材文件夹内产生任何缓存或 `desktop.ini` 文件。

### 2. 内置无损原图看图器与 `~` 键极速框选截图
- **视口级缓存 + 局部 `LockBits` 放大镜**：34.8 MB 超清大图在 `~` 键框选截图模式下移动鼠标重绘仅需 **1.4 ms/帧（~700 fps）**，彻底消除旧版每帧 `2,000 ms` 的全图重采样卡顿；确定裁剪时直接从全分辨率原图无损导出。
- 支持滚轮缩放、拖拽平移、左右翻页、右键多级撤回选区、双击自动复制到剪贴板并落盘保存。

### 3. 120fps 视频悬停预览与 ScreenboxLite 无缝接力
- 鼠标悬停视频自动弹出穿透式预览窗：短视频走原生硬件播放，中长视频自动切换 **17 帧极速首包 + 11×54 帧并发胶片流**，后台预解码冻结 `BitmapSource`，UI 线程 **0.00 ms 解码开销**。
- 双击视频直接唤起 `ScreenboxLite` v2（原生 `libmpv` 引擎）进入逐帧精准截图工作流。

### 4. 零延迟四色快捷键标记（`Ctrl + Q / W / E / R`）与一键归档（`Ctrl + T`）
- `Ctrl + Q / W / E / R` 切换 **红 / 橙 / 黄 / 绿** 四色标记，同时写入 NTFS `:files` 数据流（不改文件名）并实时同步 `_dsh_tags_ledger.json` 与 `tags_index\`，与 `python/dsh_file_tags.py` 无缝互通。
- `Ctrl + T` 一键在素材库与归档库（默认 `V:\归档`，可自定义）之间按原盘符路径双向移入/复原。
- **零分配自然序比较器**：8,398 项大目录自然数字排序纯算法耗时从 **406 ms 降至 ~6 ms（提速 65 倍）**，并支持按文件夹独立记忆排序方式与进入/后退滚动位置记忆。

---

## 目录结构

| 路径 | 说明 |
| :--- | :--- |
| `src/` | 模块化 C# 源码（30 个文件：Core / Diagnostics / Models / Main / Grid / Viewer / Video / Dialogs / SelfTest / Bench） |
| `build.ps1` | 一键编译到 `out\dsh-gallery-explorer.exe` 并支持 `-Test` 运行自动化自检 |
| `python/` | Python 侧 NTFS `:files` 标签同步 SDK (`dsh_file_tags.py`) 与画师图库纯净度治理引擎 (`artist_purity_engine.py`) |
| `docs/` | 架构设计文档 (`architecture.md`)、性能基线对比 (`perf-baseline.md`) 与自动基准测试报告 (`bench/`) |

---

## 编译、自检与性能基准命令

无需安装 Visual Studio 或额外 SDK，直接在 PowerShell 中运行：

```powershell
# 1. 编译并执行自动化排序/配置隔离自检
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test

# 2. 运行全链路无人值守性能基准测试（只读扫描测试目录，缓存完全隔离）
Start-Process -FilePath .\out\dsh-gallery-explorer.exe -ArgumentList '--bench "F:\QQ" "F:\Openclow\画师素材库\Pin"' -Wait -NoNewWindow
```

### 可选配置文件 (`%USERPROFILE%\.dsh\gallery_cache\config.ini`)

```ini
# 所有项均为可选，未配置时自动探测本机路径
DefaultStartDir=F:\Openclow\画师素材库
DefaultArchiveDir=V:\归档
ScreenboxPath=C:\Users\Administrator\AppData\Local\Programs\ScreenboxLite\ScreenboxLite.exe
FfmpegPath=D:\_tools\ffmpeg.exe
LogLevel=Info
```

## 开源协议
MIT License
