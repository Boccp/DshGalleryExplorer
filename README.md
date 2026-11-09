# DSH Gallery Explorer (DSH 画师素材库与归档文件管理器 / 快捷分色标记工具)

一款专为高分辨率画师图集、漫画单行本管理与 AI Agent 协同任务调度打造的 **4K 原生超清虚拟网格文件管理器**。采用纯原生 C# WinForms + GDI+ 双缓冲虚拟化架构（单文件零外部依赖编译），并附带完整的 Python 侧 NTFS 数据流标记联动 SDK 与图库纯净度治理引擎。

---

## 核心特性

### 1. 4K 高分屏原生超清虚拟网格（60fps 零卡顿）
- **Per-Monitor V2 DPI 感知**：原生适配 2K/4K 高分屏缩放，字体、边框与缩略图边缘锐利清晰。
- **双级缩略图预取与集中式缓存**：内存 + 磁盘双层缓存（统一存放在 `%USERPROFILE%\.dsh\gallery_cache\thumbnails`，**绝不在画师素材文件夹内产生任何缓存文件、图标或 `desktop.ini` 污染**），支持视口上下 2000px 后台多线程预加载，滑动如丝般顺滑。
- **文件夹满幅封面 + 识别角标**：自动取文件夹内首张作品作为满幅封面，并在顶部绘制深金文件夹折耳与右下角半透明文件夹徽章，与单张散图一目了然区分。

### 2. 零延迟四色快捷键标记（`Ctrl + Q / W / E / R`）与 Agent 实时账本同步
- **`Ctrl + Q + 左键`**：切换 **🔴 红** 标记（`#E81123`）
- **`Ctrl + W + 左键`**：切换 **🟠 橙** 标记（`#F7630C`）
- **`Ctrl + E + 左键`**：切换 **🟡 黄** 标记（`#FFB900`）
- **`Ctrl + R + 左键`**：切换 **🟢 绿** 标记（`#107C10`）
- **双通道无损存储**：
  1. 写入 NTFS Alternate Data Stream（`:files` 数据流），**绝不篡改原始文件名或文件夹名**；
  2. 毫秒级同步更新集中账本 `_dsh_tags_ledger.json` 与分色索引目录 `tags_index/{红,橙,黄,绿,全部标记}.{txt,json}`，供 AI Agent 零延迟读取任务队列并在完成后自动销项。

### 3. 一键归档与原路复原（`Ctrl + T`）
- **`Ctrl + T + 左键`**（或选中后按 `Ctrl + T`）：将文件/文件夹一键移入归档库（`V:\归档`），若已在归档库中则自动原路复原回素材库。

### 4. 完整还原 Windows 资源管理器交互体验
- **导航与位置记忆**：进入子文件夹再后退（鼠标侧键 `XButton1`、`Backspace`、`Alt + Left`）时，精准恢复进入前的滚动条高度与选中项，绝不跳回页首。
- **长按左键橡皮筋框选 & 跨软件文件拖拽**：在空白处按住左键拉框多选；在已选项目上按住左键拖动即可发起原生 `CF_HDROP` 文件拖拽。
- **多维排序**：支持按名称（Win32 `StrCmpLogicalW` 自然数字排序）、修改时间、大小、标记颜色正序/倒序排列。

---

## 仓库文件清单

| 文件名 | 说明 |
| :--- | :--- |
| `dsh-gallery-explorer.cs` | 管理器完整 C# 单文件源码（虚拟网格画布、DPI 自适应、NTFS ADS 读写、集中式缩略图缓存、导航栈记忆） |
| `dsh-gallery-explorer.exe` | 已编译的 64 位原生可执行程序 |
| `dsh_file_tags.py` | Python 侧 NTFS `:files` 数据流读写、集中账本同步与 AI Agent 任务销项 SDK / CLI |
| `artist_purity_engine.py` | 画师图库纯净度治理引擎（语种白名单优选 `中>英>日`、dHash 感知哈希去重、跨页双页漫画自动切分、SQLite 台账） |
| `generate_folder_cover_icons.py` | 文件夹封面渲染辅助工具 |
| `SyncGalleryToGitHub.ps1` | 一键重新编译并同步推送到 GitHub 仓库与 Release 的自动化脚本 |

---

## 零依赖编译命令（PowerShell）

无需安装 Visual Studio，直接使用 Windows 内置 `.NET Framework 4.x` 的 `csc.exe` 即可在 1 秒内完成编译：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /out:"dsh-gallery-explorer.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "dsh-gallery-explorer.cs"
```

## 开源协议
MIT License
