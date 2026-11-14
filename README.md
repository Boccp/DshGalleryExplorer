# DSH Gallery Explorer v2.0 (DSH 画师素材库与归档文件管理器)

一款专为高分辨率画师图集、漫画单行本管理、视频素材筛选与 AI Agent 协同任务调度打造的 **4K 原生超清虚拟网格文件管理器**。  
采用纯原生 C# WinForms + GDI+ / WIC / WPF 混合加速架构，**100% 零第三方依赖单文件 `.exe`**，使用 Windows 自带 `csc.exe` **0.26 秒完成编译**。

- 构建与验证记录：[docs/BUILD_LOG.md](docs/BUILD_LOG.md)
- 架构设计说明书：[docs/architecture.md](docs/architecture.md)
- 性能基线与实测对比：[docs/perf-baseline.md](docs/perf-baseline.md)

---

## 一、 安装与更新方法

`DshGalleryExplorer` 是零外部 DLL 依赖的纯单文件程序（仅 `175 KB`）：

### 方式 A：使用统一同步脚本一键编译并更新本机（推荐）
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 deploy -Only DshGalleryExplorer -NoPause
```
脚本会自动编译并运行自检，随后将新版部署到 `%USERPROFILE%\.dsh\tools\dsh-gallery-explorer.exe` 与 `%LOCALAPPDATA%\Programs\DshGalleryExplorer\dsh-gallery-explorer.exe`（若桌面尚无快捷方式会自动创建 `DSH 画师图库管理器.lnk`）。原有的标签账本、缩略图缓存、快捷栏与各文件夹排序记忆全部无缝继承。

### 方式 B：直接下载 Release 单文件覆盖
从 GitHub Releases 下载 `dsh-gallery-explorer.exe`，直接覆盖 `%USERPROFILE%\.dsh\tools\dsh-gallery-explorer.exe` 即可双击使用。

---

## 二、 完整使用方法与快捷键速查

### 1. 网格浏览、导航与文件管理

| 操作 / 快捷键 | 功能说明 |
| :--- | :--- |
| **双击项目 / `Enter`** | **文件夹** → 进入该目录；**图片** → 内置超清看图器秒开；**视频** → 唤起 `ScreenboxLite` 逐帧截图（自动同步当前文件夹排序） |
| **`Backspace` / `Alt + ←` / 鼠标后退侧键 (`XButton1`)** | 返回上一历史目录，**精准恢复离开时的滚动条高度与选中焦点** |
| **`Alt + →` / 鼠标前进侧键 (`XButton2`)** | 前进到下一历史目录 |
| **`Alt + ↑` / 顶栏 `⬆ 上级`** | 返回父目录并自动高亮聚焦刚才离开的子文件夹 |
| **`Ctrl + 鼠标滚轮` / 底栏缩放条** | 实时无极缩放网格卡片尺寸（`128px – 640px`） |
| **空白处按住左键拖动** | 橡皮筋拉框多选（配合 `Ctrl` 加选 / `Shift` 区间连选 / `Ctrl + A` 全选） |
| **在已选项目上按住左键拖动** | 发起原生 `CF_HDROP` 拖拽：可拖入本窗口子文件夹移动，或直接拖到 PS / 微信 / 浏览器等外部软件 |
| **`Ctrl + C` / `Ctrl + X` / `Ctrl + V`** | 复制 / 剪切 / 粘贴文件或文件夹（跨盘自动保留四色标记） |
| **`Ctrl + Shift + C`** | 一键复制选中项的完整绝对路径到剪贴板 |
| **`F2`** / **`Ctrl + Shift + N`** / **`Delete`** | 重命名当前项 / 新建文件夹 / 删除选中项 |
| **顶栏排序按钮 / 右键「排序方式」** | 支持按 **名称（零分配自然数字序）/ 创建时间 / 修改时间 / 大小 / 标签颜色** 升序或降序，**每个文件夹独立自动记忆排序方式** |

### 2. 四色快捷标记（`Ctrl + Q / W / E / R`）与 Agent 账本同步

> 鼠标悬停在任意卡片上按住 `Ctrl + Q/W/E/R + 左键单击`（或先框选多项后直接按 `Ctrl + Q/W/E/R`），即可零延迟批量打标或取消标记：

| 快捷键 | 标记颜色 | 写入机制与用途 |
| :--- | :---: | :--- |
| **`Ctrl + Q`**（+ 左键） | 🔴 **红** (`#E81123`) | 双通道无损存储：① 写入 NTFS `:files` 数据流（**绝不修改原文件名，画师文件夹内零污染**）；② 实时同步更新集中账本 `_dsh_tags_ledger.json` 与 `tags_index\{红,橙,黄,绿,全部标记}.{txt,json}` |
| **`Ctrl + W`**（+ 左键） | 🟠 **橙** (`#F7630C`) | 同上，可在左侧边栏底部一键筛选某一种颜色或全部已标记项 |
| **`Ctrl + E`**（+ 左键） | 🟡 **黄** (`#FFB900`) | 同上，支持同一文件/文件夹叠加多个颜色胶囊标记 |
| **`Ctrl + R`**（+ 左键） | 🟢 **绿** (`#107C10`) | 同上，可通过 `python/dsh_file_tags.py` 供 AI Agent 读取任务队列并自动销项 |

### 3. 一键跨盘归档与原路复原（`Ctrl + T`）
- **`Ctrl + T`**（或 `Ctrl + T + 左键`）：
  - 在普通素材目录中触发：按原盘符目录结构（如 `F:\Openclow\画师A\作品1` → `V:\归档\F盘\Openclow\画师A\作品1`）一键后台跨卷移入归档库并保留标签；
  - 在归档库目录中再次触发：自动识别原盘符路径并**一键原路复原**回原素材库位置。
- 点击顶栏 **`📦 归档库`** 按钮或右键左侧栏可随时自定义归档库根目录。

### 4. 内置超清看图器与 `~` 键无损框选截图
双击任意图片进入内置看图器（34.8 MB 超大图缩放/截图重绘仅需 **1.5 ms/帧**）：
- **浏览与翻页**：鼠标滚轮以光标为中心缩放（最高 `24×`），按住左键拖拽平移，双击画面一键复位自适应窗口；`← / →`、`A / D`、`PageUp / PageDown` 或鼠标侧键快速翻页。
- **`~` 键无损框选截图**：
  1. 按左上角 **`~` 键** 进入截图模式（显示全屏十字准星与像素级放大镜 + 坐标/RGB 色值）；
  2. 左键拖拽拉出选框（支持 8 控制柄微调与选框平移，**右键单击可多级撤回上一步调整**）；
  3. **双击选框内部** 或 **再次按 `~` / `Enter`**：直接从内存中的**原始全分辨率位图**无损裁切导出 PNG 并自动写入系统剪贴板（顶栏可配置默认保存目录与是否自动落盘）。

### 5. 120fps 视频悬停极速预览
- 鼠标在网格中的视频卡片上悬停片刻，自动在光标旁弹出穿透式预览窗（不抢焦点、不挡滚轮与点击）：短视频走原生硬件直播，中长视频自动切换 **611 帧浓缩 5 秒 120fps 高帧率胶片预览**（二次悬停 0ms 秒开）。

---

## 三、 集中缓存、配置文件与 Python SDK

所有数据统一存放在 `%USERPROFILE%\.dsh\gallery_cache\`（可通过环境变量 `DSH_GALLERY_CACHE_DIR` 覆盖）：
- `thumbnails\`：320px 缩略图磁盘缓存
- `video_reels\`：视频悬停胶片 `.reel` 缓存
- `_dsh_tags_ledger.json` & `tags_index\`：四色标记集中账本与分色清单
- `_dsh_folder_sorts.json`：各文件夹独立排序记忆
- `_dsh_sidebar_shortcuts.json` & `_dsh_archive_root.txt`：左侧快捷栏与归档库路径配置
- `logs\gallery-yyyyMMdd.log`：异步运行日志（自动保留最近 7 天；命令行加 `--verbose` 或配置 `LogLevel=Debug` 开启调试级日志）
- `config.ini`（可选，按需新建）：
  ```ini
  DefaultStartDir=F:\Openclow\画师素材库
  DefaultArchiveDir=V:\归档
  ScreenboxPath=C:\Users\Administrator\AppData\Local\Programs\ScreenboxLite\ScreenboxLite.exe
  FfmpegPath=D:\_tools\ffmpeg.exe
  LogLevel=Info
  ```

### Python 标签联动 CLI（`python/dsh_file_tags.py`）
```powershell
# 查看指定颜色或全部已标记路径
python .\python\dsh_file_tags.py list --color 红

# 通过脚本给指定文件/文件夹打标或移除标记（自动同步 NTFS :files 与集中账本）
python .\python\dsh_file_tags.py add "F:\Openclow\画师素材库\@artist" 红
python .\python\dsh_file_tags.py remove "F:\Openclow\画师素材库\@artist" 红
```

---

## 四、 编译、自检、基准测试与 GitHub 同步命令

```powershell
# 1. 一键编译并运行隔离缓存自检（0.26s，验证路径规范化、自然序比较器与跨重启排序记忆）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test

# 2. 运行全链路无人值守性能基准测试（测试目录只读，bench 缓存完全隔离）
Start-Process -FilePath .\out\dsh-gallery-explorer.exe -ArgumentList '--bench "F:\QQ" "F:\Openclow\画师素材库\Pin"' -Wait -NoNewWindow

# 3. 双仓库同步工具（查看状态 / 快进拉取部署 / 编译发布到 GitHub）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 status
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 pull -Only DshGalleryExplorer
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 push -Only DshGalleryExplorer -Message "更新说明"
```

## 开源协议
MIT License
