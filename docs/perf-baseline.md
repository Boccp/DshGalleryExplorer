# 性能基线与优化对比

测量工具：`out\dsh-gallery-explorer.exe --bench <目录...>`（见 `src/Bench/BenchRunner.cs`）。
测试机：24 逻辑 CPU，Windows（NT 6.2 兼容报告），x64。测试目录（只读使用）：

- `F:\QQ`（213 项，图片 + 37 个 mp4 + 杂项）
- `F:\Openclow\画师素材库`（48 个画师文件夹，测文件夹封面）
- `F:\Openclow\画师素材库\Pin`（8398 项，大目录排序压力）
- `F:\Openclow\画师素材库\照片`（2011 项）
- `F:\Openclow\画师素材库\@jiraiya`（855 项）

bench 所有缓存写入独立目录（默认 exe 同级 `bench_cache\`，每次清空），不触碰用户真实缓存与账本。
同一台机器两次基线之间有 ±20% 的噪声（后台负载不同），对比时看数量级。原始报告见 `docs/bench/`。

## 基线（阶段 A，未做任何优化）

完整报告：[baseline-run1.md](bench/baseline-run1.md)、[baseline-run2.md](bench/baseline-run2.md)

| 指标 | 基线 run1 | 基线 run2 | 说明 |
| :--- | ---: | ---: | :--- |
| Pin(8398) 枚举+名称排序 总 ms | 428.6 | 431.1 | 其中纯排序 406 ms（`NaturalCompare` 每次比较都 `Regex.Split`） |
| Pin(8398) 按标签排序 总 ms | 539.7 | 556.2 | 标签相同则回落到自然序比较 |
| Pin(8398) 按修改时间 总 ms | 77.5 | 85.6 | 不走自然序时很快 |
| 缩略图单线程冷加载 jpg avg / p95 ms | 72.7 / 146 | 86.7 / 222 | 解码 ≈ 41–48 ms + HighQualityBicubic 缩放 ≈ 29–34 ms |
| 缩略图并发冷加载 202 项 墙钟 | 12.7 s（15.9 项/s） | 12.3 s（16.4 项/s） | 12 个线程几乎没有加速：GDI+ 解码/缩放存在进程级串行化 |
| 缩略图磁盘缓存命中 202 项 墙钟 | 253 ms | 259 ms | |
| 网格绘制 1920x1080 avg ms/帧 | 65–74 | 80–88 | 与 tile 大小几乎无关 → 成本按像素面积：AA 圆角卡片填充 + 每帧缩放缩略图 |
| 网格绘制 3840x2160 avg ms/帧 | 185–303 | 218–334 | 4K 下只有 3–5 fps |
| 看图器 34.8MB JPG 每帧绘制 | 1725 ms | 2034 ms | 每次 OnPaint 都用 HighQualityBicubic 从原图缩放 |
| 看图器截图模式（鼠标移动重绘）每帧 | 1720 ms | 2236 ms | 截图模式下每次鼠标移动都要重绘 → 严重卡顿 |
| 视频胶片 首帧 ms（3 个样本） | 88–134 | 160–293 | |
| 视频胶片 全部 612 帧 ms | 1983–3003 | 2429–3826 | 36 个 ffmpeg 进程，3 并发 |
| 胶片 JPEG 单帧解码（UI 线程）avg ms | 1.21–1.89 | 1.56–2.46 | 120fps 预算 8.3 ms，UI 线程占用 15–30% |

> run1 使用 ffmpeg `F:\剪映\9.9.0.13784\ffmpeg.exe`（当时 hermes 目录不可访问），run2 使用 `%LOCALAPPDATA%\hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe`。

## 优化后对比

（阶段 C 完成后填写）
