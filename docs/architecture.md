# DshGalleryExplorer v2 架构说明

## 1. 模块化目录结构

原 `6,800` 行单文件 `dsh-gallery-explorer.cs` 已按单一职责拆分为 `src\` 下 **30 个 C# 源文件**（兼容 Windows 内置 `.NET Framework 4.x` `csc.exe` C# 5 零外部依赖秒级编译）：

```text
DshGalleryExplorer/
├── build.ps1                          # 一键编译 + --self-test-sort 自动化自检脚本
├── version.txt                        # 语义化版本号 (2.0.0)
├── python/                            # Python 侧 NTFS :files 标签读写 SDK 与纯净度治理工具
│   ├── dsh_file_tags.py
│   ├── artist_purity_engine.py
│   └── generate_folder_cover_icons.py
├── docs/
│   ├── architecture.md                # 本文件：模块边界、外部契约与性能优化设计
│   ├── perf-baseline.md               # v1 基线与 v2 优化前后实测对比表
│   └── bench/                         # --bench 自动生成的原始 Markdown 报告
└── src/
    ├── Program.cs                     # 入口：Per-Monitor V2 DPI、CLI 分发 (--self-test-sort / --bench / --verbose)
    ├── Core/
    │   └── AppPaths.cs                # 集中路径与外部工具解析（支持 config.ini 与环境变量覆盖）
    ├── Diagnostics/
    │   ├── Log.cs                     # 无锁异步滚动日志（7 天自动保留、重复异常抑制）
    │   └── Perf.cs                    # 零开销高精度计时聚合器（count / avg / p50 / p95 / max）
    ├── Models/
    │   ├── EntryItem.cs               # 文件/文件夹网格条目数据模型
    │   └── TagSpec.cs                 # 红/橙/黄/绿四色标记规格与固定 UID
    ├── Main/
    │   ├── MainWindow.cs              # 主窗口状态与生命周期
    │   ├── MainWindow.UI.cs           # 顶栏、地址栏、状态栏与右键菜单构建
    │   ├── MainWindow.Navigation.cs   # 目录前进/后退/上级、滚动位置与焦点记忆、封面查找
    │   ├── MainWindow.Sort.cs         # 零分配自然序比较器、按文件夹记忆排序、资源管理器排序同步
    │   ├── MainWindow.Tags.cs         # NTFS :files ADS 流读写、集中账本与 tags_index 实时同步
    │   ├── MainWindow.Archive.cs      # Ctrl+T 一键跨盘归档与原路复原
    │   ├── MainWindow.FileOps.cs      # 复制/剪切/粘贴、跨软件 CF_HDROP 拖放、重命名、跨卷移动
    │   ├── MainWindow.Sidebar.cs      # 左侧自定义快捷路径与四色标签筛选入口
    │   ├── MainWindow.Open.cs         # 双击路由：内置看图器 / ScreenboxLite 视频接力 / 系统打开
    │   ├── MainWindow.Keyboard.cs     # 全局快捷键与输入焦点保护
    │   └── MainWindow.VideoHover.cs   # 网格悬停去抖与视频预览弹窗调度
    ├── Grid/
    │   ├── VirtualGridCanvas.cs       # 4K 双缓冲虚拟网格核心状态、GDI 资源缓存与 Dispose
    │   ├── VirtualGridCanvas.Paint.cs # 32bppPArgb 预渲染卡片模板与 1:1 无缩放极速合成
    │   ├── VirtualGridCanvas.Thumbnails.cs # WIC 原生多线程无锁 DCT 缩略图解码 + 内存/磁盘双级缓存
    │   └── VirtualGridCanvas.Input.cs # 橡皮筋框选、滚轮缩放、热键标记与瞬态 HUD 提示
    ├── Viewer/
    │   ├── FastImageViewerForm.cs     # 内置大图看图器状态与翻页逻辑
    │   ├── FastImageViewerForm.Paint.cs # 视口级预缩放位图缓存（消除每帧大图重采样）
    │   └── FastImageViewerForm.Screenshot.cs # ~ 键精准框选截图、LockBits 局部像素采样放大镜、右键多级撤回
    ├── Video/
    │   ├── VideoHoverPreviewWindow.cs # WPF 穿透悬浮预览窗（短视频 MediaElement + 长视频 120fps 胶片双引擎）
    │   └── VideoHoverPreviewWindow.Reel.cs # 17帧首包 + 11×54帧低进程开销抽帧 + 后台预解码冻结 BitmapSource
    ├── Dialogs/
    │   ├── PathEditorDialog.cs        # 快捷路径与归档库编辑对话框
    │   └── RenameDialog.cs            # 重命名与新建文件夹对话框
    ├── SelfTest/
    │   └── Program.SelfTest.cs        # 隔离缓存目录的文件夹排序记忆与自然序比较器自检
    └── Bench/
        └── BenchRunner.cs             # 无人值守全链路性能基准套件 (--bench)
```

---

## 2. 严格保持不变的外部契约

1. **素材库零污染**：所有缩略图缓存、视频胶片缓存、账本、排序记忆与日志均集中存放在 `%USERPROFILE%\.dsh\gallery_cache\`，绝不在画师素材目录内创建任何隐藏文件或 `desktop.ini`。
2. **双通道四色标签契约**（与 `python/dsh_file_tags.py` 完全互通）：
   - NTFS Alternate Data Stream：`<文件或文件夹路径>:files`，UTF-8 JSON 数组存储固定 UID：
     - 红 `f7e0e137-2eb5-4fa4-a50d-ddd65df17c34` (`Ctrl+Q`)
     - 橙 `c84a8131-c4de-47d9-9440-26e859d14b3d` (`Ctrl+W`)
     - 黄 `d4b8d4bd-ceaf-4e58-ac61-a185fcf96c5d` (`Ctrl+E`)
     - 绿 `79376daf-c44a-4fe4-aa3b-8b30baea453e` (`Ctrl+R`)
   - 集中账本：`%USERPROFILE%\.dsh\gallery_cache\_dsh_tags_ledger.json` 及 `tags_index\{红,橙,黄,绿,全部标记}.{txt,json}`。
3. **个性化配置与记忆文件**：
   - `_dsh_folder_sorts.json`（各文件夹独立排序记忆）
   - `_dsh_sidebar_shortcuts.json`（左侧快捷栏）
   - `_dsh_archive_root.txt`（`Ctrl+T` 归档根目录）
   - 可选 `config.ini`（支持自定义 `CacheDir`、`DefaultStartDir`、`DefaultArchiveDir`、`ScreenboxPath`、`FfmpegPath`、`LogLevel=Debug`）。

---

## 3. 五大核心性能重构原理

1. **零分配自然序比较器（`MainWindow.Sort.cs`）**：用单遍原位字符扫描替代每次比较都执行的 `Regex.Split(s, "([0-9]+)")`，同时支持超过 `ulong.MaxValue` 的任意长度数字段比较，消除大目录 `O(N log N)` 排序中的海量短命字符串与正则分配。
2. **WIC 原生无锁并发缩略图流水线（`VirtualGridCanvas.Thumbnails.cs`）**：用 Windows Imaging Component（`BitmapDecoder` 仅读文件头尺寸 + `BitmapImage.DecodePixelWidth/Height` JPEG DCT 硬件降采样 + `LockBits` 直接拷入 `Format32bppPArgb`）替代持有进程级全局锁的 `gdiplus.dll` `Image.FromStream` 全图解码，彻底释放多核并发吞吐。
3. **预乘 Alpha 卡片模板与 1:1 位块传输（`VirtualGridCanvas.Paint.cs`）**：按当前 `TileSize` 预渲染圆角卡片底板、选中边框、四色标记环与徽章，并将可见缩略图按槽位尺寸缓存在 `Format32bppPArgb` 位图中，`OnPaint` 直接调用 `DrawImageUnscaled` 1:1 贴图，消除 4K 视口下每帧全屏抗锯齿 `GraphicsPath` 填充与 CPU 图像重采样。
4. **看图器视口级缓存与局部 `LockBits` 放大镜（`FastImageViewerForm.*.cs`）**：仅在切图或窗口尺寸变化时生成一次 `HighQualityBicubic` 视口缓存 `_scaledViewBitmap`，同时保留原始全分辨率 `_currentBitmap` 用于无损裁切保存；放大镜仅用 `LockBits` 锁定光标周围 `31×23` 像素区域，彻底消除 `~` 截图模式下鼠标移动时的全图重采样卡顿。
5. **12 进程分段胶片与后台预解码冻结帧（`VideoHoverPreviewWindow.*.cs`）**：第 0 段单发抽取 17 帧保证极速首包，后续合并为 `11 段 × 54 帧`（4 并发进程，将 `ffmpeg` 进程创建开销减少 67%），并在后台线程直接解码为 `Freeze()` 后的 `BitmapImage`；WPF UI 线程 `CompositionTarget.Rendering` 仅做对象引用赋值，UI 线程 JPEG 解码耗时降为 `0.00 ms`。
