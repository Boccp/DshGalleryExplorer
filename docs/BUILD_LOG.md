# DSH Gallery Explorer 构建记录与验证台账

本文档记录每次重要版本的构建环境、模块变更、编译产物、自动化自检以及真实素材库目录的基准测试结果，便于后续维护与跨设备复现。

---

## v2.0.2 构建与开发记录（2026-11-16，Commit `805e8f4`，2026-11-18 补录文档并复验）

### 1. 变更摘要
1. **内置看图器 `~` 框选截图支持 `Tab` 键水平镜像翻转（`src/Viewer/FastImageViewerForm.Screenshot.cs`、`src/Viewer/FastImageViewerForm.cs`）**：
   - 在 `~` 截图模式下按 **`Tab` 键** 可一键切换水平镜像翻转（`_shotFlipHorizontal`），再次按 `Tab` 恢复原向。
   - **预览、选框与导出 PNG 完全一致**：镜像状态下全屏预览画面、局部像素放大镜、拖拽选框坐标映射以及最终从原始全分辨率位图无损裁切出的 PNG（`RotateFlip(RotateFlipType.RotateNoneFlipX)`）保持严格左右对称一致，HUD 同步显示 `[已水平翻转 | Tab 复原]` 状态提示。
2. **双仓库自动同步脚本代理与凭据增强（`tools/RepoSync.ps1`）**：
   - 自动按「进程环境变量 → `git config http.proxy` → Windows 系统 Internet Settings (`ProxyEnable` / `ProxyServer`)」三级探测代理，并自动复用 Git Credential Manager / `gh auth` 令牌上传 GitHub Release 产物。

### 2. 构建产物与自检结果
| 产物路径 | 大小 | 编译器 / 警告 (`/warn:4`) | `--self-test-sort` 结果 |
| :--- | ---: | :--- | :--- |
| `out\dsh-gallery-explorer.exe` | `177.5 KB` | `csc.exe` C# 5 / `0` 警告 | **PASS (`exit 0`)** |

---

## v2.0.1 构建与开发记录（2026-11-16，Commit `1a73853`）

### 1. 变更摘要
1. **四色标签筛选/标签排序视频播放列表无缝接力 `ScreenboxLite`（`src/Main/MainWindow.Open.cs`）**：
   - 双击视频唤起 `ScreenboxLite` 时，除常规目录传递 `DSH_GALLERY_SORT_SPEC`（如 `Name:Asc`、`ModifiedTime:Desc`）外，当用户处于**按标签颜色排序（`SortField.Tag`）**或**左侧四色标签筛选视图（`_activeTagFilter != null`）**时，自动将当前视图中的有序视频完整路径清单写入 `%USERPROFILE%\.dsh\gallery_cache\_dsh_active_video_playlist.txt`，并通过环境变量 `DSH_GALLERY_PLAYLIST_FILE` 传递给 `ScreenboxLite`（v2.0.3+），使 `ScreenboxLite` 内 `PageUp / PageDown` 切换上下视频严格遵循当前筛选/标签顺序。
2. **Windows 受控文件夹访问（Controlled Folder Access）自检兼容（`build.ps1`、`tools/RepoSync.ps1`）**：
   - 执行 `--self-test-sort` 前先将临时测试二进制复制至 `%TEMP%` 隔离路径下运行并自动清理，避免在受策略保护的工作区目录中直接派生测试子进程被拦截。

### 2. 构建产物与自检结果
| 产物路径 | 大小 | 编译器 / 警告 (`/warn:4`) | `--self-test-sort` 结果 |
| :--- | ---: | :--- | :--- |
| `out\dsh-gallery-explorer.exe` | `176.0 KB` | `csc.exe` C# 5 / `0` 警告 | **PASS (`exit 0`)** |

---

## v2.0.0 构建记录（2026-11-14）

### 1. 构建环境与工具链
- **操作系统**：Windows x64（24 逻辑 CPU）
- **编译器**：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（C# 5，`/target:winexe /platform:anycpu /optimize+ /warn:4`）
- **系统程序集引用**：`System.dll`、`System.Core.dll`、`System.Drawing.dll`、`System.Windows.Forms.dll`、`PresentationCore.dll`、`PresentationFramework.dll`、`WindowsBase.dll`、`System.Xaml.dll`（100% Windows 内置，零外部 NuGet / DLL 依赖）
- **源码规模**：`src\` 下 **30 个模块化 `.cs` 文件**（由 v1 单文件 `6,800` 行重构拆分）
- **构建命令**：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
  ```

### 2. 构建产物与自检结果
| 产物路径 | 大小 | 编译耗时 | 编译警告 (`/warn:4`) | `--self-test-sort` 结果 |
| :--- | ---: | ---: | ---: | :--- |
| `out\dsh-gallery-explorer.exe` | `175.0 KB` | `0.26 s` | `0` | **PASS (`exit 0`)** |

**`--self-test-sort` 覆盖项与捕获修复记录**：
1. 盘符根目录、斜杠混用、大小写与特殊转义字符文件夹键名规范化（`NormalizeFolderKey`）。
2. 零分配自然序比较器（`NaturalCompare`）严格弱序验证：数值段大小比较、大小写无关、前导零确定性次序（`"p_1" != "p_01"` 且反对称）、30 位超长数字（超过 `ulong.MaxValue`）无溢出比较。
3. 临时隔离缓存目录（`AppPaths.OverrideCacheRoot`）跨实例持久化记忆测试：在重构隔离自检过程中发现并修复了 v1 `LoadFolderSortsFromDisk` 中正则 `([^}]*)` 将外层 `"folders": {` 与字典序首个文件夹条目吞并丢弃的潜伏缺陷（改为 `([^{}]*)`）。

### 3. 五个真实素材目录 `--bench` 实测汇总（v1 基线 vs v2.0.0）
只读测试集：`F:\QQ`（213 项）、`F:\Openclow\画师素材库`（48 个画师目录）、`Pin`（8,398 项）、`照片`（2,011 项）、`@jiraiya`（855 项）。原始完整报告见 [baseline-run1.md](bench/baseline-run1.md)、[baseline-run2.md](bench/baseline-run2.md)、[optimized-run1.md](bench/optimized-run1.md)、[optimized-run2.md](bench/optimized-run2.md) 及汇总表 [perf-baseline.md](perf-baseline.md)。

- **`Pin (8,398 项)` 自然序名称排序**：纯排序从 `406.0 ms` 降至 **`5.75 ms`**（**70.6× 提速**），目录枚举+排序总耗时从 `431.1 ms` 降至 **`31.4 ms`**（**13.7×**）。
- **202 项真实并发缩略图冷加载**：从 `12.5 s`（`16 项/s`，受制于 `gdiplus.dll` 全局锁）降至 **`0.58 s`（`347.5 项/s`，WIC 多核无锁并发，21.5× 提速）**；磁盘缓存热加载从 `256 ms` 降至 **`53.8 ms`（`3,756 项/s`）**。
- **虚拟网格离屏渲染帧率**：
  - `1920×1080`（`128 / 260 / 420 / 640 px` 四档）：从 `65–88 ms/帧` 降至 **`7.95–10.50 ms/帧`（`95–126 fps`）**。
  - `3840×2160` 4K（`128 / 260 / 420 / 640 px` 四档）：从 `185–334 ms/帧`（`3–5 fps`）降至 **`21.3–25.9 ms/帧`（`39–47 fps`，`p50` 达 `58 fps`）**。
- **内置看图器 `34.8 MB` 大图渲染**：普通模式与 `~` 截图十字准星/放大镜重绘从 `1,720–2,236 ms/帧` 降至 **`1.43–1.77 ms/帧`（`560–700 fps`，提速 1200×+）**，同时保持原图 100% 原始分辨率无损裁切。
- **视频悬停 611 帧胶片抽取与播放**：全量抽帧耗时从 `1,983–3,826 ms`（未解码）降至 **`775–1,149 ms`（含全量后台预解码冻结）**，WPF UI 线程每帧 JPEG 解码耗时从 `1.68 ms` 降至 **`0.00 ms`**。
