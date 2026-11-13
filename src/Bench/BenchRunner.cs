using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshGalleryExplorer
{
    /// <summary>
    /// --bench 命令行基准测试：无需人工操作，跑完即退出。
    /// 用法：dsh-gallery-explorer.exe --bench &lt;目录1&gt; [目录2 ...] [--label 名称] [--report 文件]
    ///        [--cache 目录] [--keep-cache] [--max-thumbs N] [--frames N] [--max-videos N] [--no-video]
    /// - 所有缓存写入独立的 bench 缓存目录（默认 exe 同级 bench_cache\，每次运行前清空），绝不触碰用户真实缓存/账本；
    /// - 测试目录只读使用；
    /// - 报告输出到控制台（若有父控制台）、日志与 Markdown 报告文件。
    /// </summary>
    internal static class BenchRunner
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private static readonly StringBuilder _md = new StringBuilder();
        private static readonly string[] BENCH_IMAGE_EXTS = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".ico" };

        private sealed class Options
        {
            public List<string> Dirs = new List<string>();
            public string CacheRoot;
            public string ReportPath;
            public string Label = "run";
            public int MaxThumbs = 240;
            public int Frames = 40;
            public int MaxVideos = 3;
            public bool Video = true;
            public bool KeepCache;
        }

        private static void Line(string s)
        {
            _md.AppendLine(s);
            try { Console.WriteLine(s); } catch { /* 无控制台时忽略 */ }
        }

        private static string F(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }

        public static int Run(string[] args)
        {
            try { AttachConsole(-1); } catch { /* 双击运行时没有父控制台 */ }

            Options o = new Options();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--cache" && i + 1 < args.Length) o.CacheRoot = args[++i];
                else if (a == "--report" && i + 1 < args.Length) o.ReportPath = args[++i];
                else if (a == "--label" && i + 1 < args.Length) o.Label = args[++i];
                else if (a == "--max-thumbs" && i + 1 < args.Length) o.MaxThumbs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--frames" && i + 1 < args.Length) o.Frames = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--max-videos" && i + 1 < args.Length) o.MaxVideos = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--no-video") o.Video = false;
                else if (a == "--keep-cache") o.KeepCache = true;
                else o.Dirs.Add(a);
            }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(o.CacheRoot)) o.CacheRoot = Path.Combine(exeDir, "bench_cache");
            if (!o.KeepCache && Directory.Exists(o.CacheRoot))
            {
                try { Directory.Delete(o.CacheRoot, true); } catch (Exception ex) { Console.WriteLine("清理 bench 缓存失败: " + ex.Message); }
            }
            Directory.CreateDirectory(o.CacheRoot);
            AppPaths.OverrideCacheRoot(o.CacheRoot);
            Log.Init(AppPaths.LogDir, false);
            if (string.IsNullOrEmpty(o.ReportPath))
            {
                o.ReportPath = Path.Combine(exeDir, "bench-report-" + o.Label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            }

            int rc = 0;
            try
            {
                rc = RunCore(o);
            }
            catch (Exception ex)
            {
                Line("**基准测试异常终止**: " + ex);
                Log.Error(ex, "bench 异常终止");
                rc = 2;
            }

            Line("");
            Line("## 原始计时汇总 (Perf)");
            Line("");
            Line(Perf.Report());
            try
            {
                File.WriteAllText(o.ReportPath, _md.ToString(), new UTF8Encoding(false));
                Console.WriteLine("报告已写入: " + o.ReportPath);
            }
            catch (Exception ex) { Console.WriteLine("写报告失败: " + ex.Message); }
            Log.Info("[bench] 完成，报告: " + o.ReportPath + Environment.NewLine + _md);
            Log.Flush(2000);
            return rc;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunCore(Options o)
        {
            Line("# DSH Gallery Explorer 基准测试报告 (" + o.Label + ")");
            Line("");
            Line("- 时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("- 机器: " + Environment.MachineName + "，逻辑 CPU " + Environment.ProcessorCount + "，" + Environment.OSVersion + "，" + (Environment.Is64BitProcess ? "x64" : "x86"));
            Line("- bench 缓存目录: `" + AppPaths.CacheRoot + "`");
            string ffmpeg = VideoHoverPreviewWindow.ResolveFfmpegPath();
            Line("- ffmpeg: " + (ffmpeg ?? "(未找到)"));
            Line("- 测试目录: " + string.Join("，", o.Dirs.ToArray()));
            Line("");

            List<string> dirs = new List<string>();
            foreach (string d in o.Dirs)
            {
                if (Directory.Exists(d)) dirs.Add(d);
                else Line("> 目录不存在，跳过: " + d);
            }
            if (dirs.Count == 0)
            {
                Line("没有可用的测试目录。");
                return 1;
            }

            Section("1. 目录枚举 + 排序 + 账本匹配", () => BenchEnumerate(dirs));

            List<EntryItem> thumbItems = CollectThumbItems(dirs, o.MaxThumbs);
            List<EntryItem> okItems = null;
            Section("2. 缩略图冷/热加载", () => { okItems = BenchThumbnails(thumbItems); });
            if (okItems == null || okItems.Count == 0) okItems = thumbItems;
            Section("3. 网格绘制（离屏渲染）", () => BenchGridPaint(okItems, o.Frames));
            Section("4. 看图器绘制", () => BenchViewer(thumbItems, o.Frames));
            if (o.Video)
            {
                if (string.IsNullOrEmpty(ffmpeg)) Line("> 未找到 ffmpeg，跳过视频测试");
                else Section("5. 视频悬停预览胶片", () => BenchVideo(dirs, ffmpeg, o.MaxVideos));
            }
            return 0;
        }

        private static void Section(string title, Action body)
        {
            Line("## " + title);
            Line("");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Line("> 本节异常: " + ex.GetType().Name + ": " + ex.Message);
                Log.Warn(ex, "bench 小节异常: " + title);
            }
            Line("");
            Line("_本节耗时 " + F1(sw.Elapsed.TotalSeconds) + " s_");
            Line("");
        }

        // ───────────────────────── 1. 目录枚举 ─────────────────────────

        private static Dictionary<string, List<string>> BuildSyntheticLedger(string dir)
        {
            // 模拟真实账本：目录内每 5 个条目打一个标签，外加 20000 条其他路径
            Dictionary<string, List<string>> ledger = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] all = Directory.GetFileSystemEntries(dir);
                for (int i = 0; i < all.Length; i += 5)
                {
                    ledger[all[i]] = new List<string> { MainWindow.ORDERED_TAGS[i % MainWindow.ORDERED_TAGS.Length].Uid };
                }
            }
            catch (Exception ex) { Log.Warn(ex, "bench 构造模拟账本"); }
            for (int i = 0; i < 20000; i++)
            {
                ledger[@"Z:\bench\fake\path_" + i + ".jpg"] = new List<string> { MainWindow.ORDERED_TAGS[i % 4].Uid };
            }
            return ledger;
        }

        private static void BenchEnumerate(List<string> dirs)
        {
            const int RUNS = 5;
            Line("每项运行 " + RUNS + " 次取平均（首次单列，可能含磁盘冷读）。账本为模拟账本（目录内每 5 项 1 个标签 + 2 万条无关路径）。");
            Line("");
            Line("| 目录 | 条目数 | 首次 GetFileSystemInfos ms | GetFileSystemInfos avg ms | 名称排序 总 ms | 其中排序 ms | 修改时间 总 ms | 大小 总 ms | 标签 总 ms |");
            Line("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (string dir in dirs)
            {
                Stopwatch sw = Stopwatch.StartNew();
                int count = new DirectoryInfo(dir).GetFileSystemInfos().Length;
                double firstMs = sw.Elapsed.TotalMilliseconds;
                double rawSum = 0;
                for (int r = 0; r < RUNS; r++)
                {
                    sw.Restart();
                    new DirectoryInfo(dir).GetFileSystemInfos();
                    rawSum += sw.Elapsed.TotalMilliseconds;
                }

                Dictionary<string, List<string>> ledger = BuildSyntheticLedger(dir);
                Func<string, List<string>> lookup = p =>
                {
                    List<string> u;
                    if (ledger.TryGetValue(p, out u) && u != null && u.Count > 0) return new List<string>(u);
                    return new List<string>();
                };

                MainWindow.SortField[] fields = new[] { MainWindow.SortField.Name, MainWindow.SortField.ModifiedTime, MainWindow.SortField.Size, MainWindow.SortField.Tag };
                double[] totals = new double[fields.Length];
                double nameSortMs = 0;
                int items = 0;
                for (int fi = 0; fi < fields.Length; fi++)
                {
                    Perf.Reset("dir.");
                    double sum = 0;
                    for (int r = 0; r < RUNS; r++)
                    {
                        List<EntryItem> list = new List<EntryItem>();
                        sw.Restart();
                        MainWindow.FillDirectoryEntries(list, dir, @"V:\归档", fields[fi], false, true, lookup);
                        sum += sw.Elapsed.TotalMilliseconds;
                        items = list.Count;
                    }
                    totals[fi] = sum / RUNS;
                    if (fields[fi] == MainWindow.SortField.Name)
                    {
                        Perf.Summary s = Perf.Get("dir.sort");
                        nameSortMs = s != null ? s.AvgMs : -1;
                    }
                }
                Line(string.Format("| `{0}` | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |",
                    dir, items + " / " + count, F(firstMs), F(rawSum / RUNS), F(totals[0]), nameSortMs >= 0 ? F(nameSortMs) : "-", F(totals[1]), F(totals[2]), F(totals[3])));
            }
        }

        // ───────────────────────── 2. 缩略图 ─────────────────────────

        private static List<EntryItem> CollectThumbItems(List<string> dirs, int maxThumbs)
        {
            List<EntryItem> result = new List<EntryItem>();
            int perDir = Math.Max(10, maxThumbs / Math.Max(1, dirs.Count));
            int folders = 0, videos = 0;
            foreach (string dir in dirs)
            {
                List<EntryItem> list = new List<EntryItem>();
                try
                {
                    MainWindow.FillDirectoryEntries(list, dir, @"V:\归档", MainWindow.SortField.Name, false, true, p => new List<string>());
                }
                catch (Exception ex) { Log.Warn(ex, "bench 枚举 " + dir); continue; }
                int taken = 0;
                foreach (EntryItem it in list)
                {
                    if (taken >= perDir) break;
                    string ext = Path.GetExtension(it.FullPath).ToLowerInvariant();
                    if (it.IsFolder)
                    {
                        if (folders >= 24) continue;
                        folders++;
                    }
                    else if (VirtualGridCanvas.IsVideoExtension(ext))
                    {
                        if (videos >= 8) continue;
                        videos++;
                    }
                    else if (Array.IndexOf(BENCH_IMAGE_EXTS, ext) < 0)
                    {
                        continue;
                    }
                    result.Add(it);
                    taken++;
                }
            }
            return result;
        }

        private static string KindOf(EntryItem it)
        {
            if (it.IsFolder) return "folder";
            string ext = Path.GetExtension(it.FullPath).ToLowerInvariant();
            if (VirtualGridCanvas.IsVideoExtension(ext)) return "video";
            if (ext == ".webp") return "webp";
            if (ext == ".png") return "png";
            return "jpg/other";
        }

        private static List<EntryItem> CloneItems(List<EntryItem> src)
        {
            List<EntryItem> list = new List<EntryItem>(src.Count);
            foreach (EntryItem it in src)
            {
                list.Add(new EntryItem
                {
                    FullPath = it.FullPath,
                    Name = it.Name,
                    IsFolder = it.IsFolder,
                    SizeBytes = it.SizeBytes,
                    CreatedAt = it.CreatedAt,
                    ModifiedAt = it.ModifiedAt,
                    TagUids = new List<string>(it.TagUids),
                    CoverImagePath = it.IsFolder ? null : it.CoverImagePath
                });
            }
            return list;
        }

        private static List<EntryItem> BenchThumbnails(List<EntryItem> items)
        {
            Dictionary<string, int> kindCount = new Dictionary<string, int>();
            foreach (EntryItem it in items)
            {
                string k = KindOf(it);
                int c; kindCount.TryGetValue(k, out c); kindCount[k] = c + 1;
            }
            StringBuilder kinds = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in kindCount) kinds.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            Line("样本 " + items.Count + " 项（" + kinds.ToString().Trim() + "）。缩略图目标尺寸 320px（与程序一致）。");
            Line("");

            // 2a. 单线程冷加载（无磁盘缓存）：逐项测量 封面查找 + 解码 + 缩放 + 写磁盘缓存
            Perf.Reset("thumb.");
            List<EntryItem> ok = new List<EntryItem>();
            foreach (EntryItem src in CloneItems(items))
            {
                string kind = KindOf(src);
                Stopwatch sw = Stopwatch.StartNew();
                string imgPath = src.IsFolder ? MainWindow.FindFirstImageInFolder(src.FullPath) : src.FullPath;
                double coverMs = sw.Elapsed.TotalMilliseconds;
                if (src.IsFolder) Perf.Record("bench.folderCoverLookup", coverMs);
                Bitmap bmp = string.IsNullOrEmpty(imgPath) ? null : VirtualGridCanvas.DecodeThumbnailBitmap(imgPath, src.IsFolder, 320);
                if (bmp != null)
                {
                    VirtualGridCanvas.SaveToDiskCache(src, bmp);
                    bmp.Dispose();
                    ok.Add(src);
                }
                Perf.Record("bench.thumbCold." + kind, sw.Elapsed.TotalMilliseconds);
            }

            // 2b. 单线程磁盘缓存命中（相当于重启程序后的“热”加载）
            foreach (EntryItem src in ok)
            {
                Stopwatch sw = Stopwatch.StartNew();
                Bitmap b = VirtualGridCanvas.TryLoadFromDiskCache(src);
                if (b != null) b.Dispose();
                Perf.Record("bench.thumbWarmDisk." + KindOf(src), sw.Elapsed.TotalMilliseconds);
            }

            Line("**单线程逐项耗时（ms）**");
            Line("");
            Line("| 阶段 | count | avg | p50 | p95 | max |");
            Line("| :--- | ---: | ---: | ---: | ---: | ---: |");
            foreach (Perf.Summary s in Perf.Snapshot())
            {
                if (!s.Name.StartsWith("thumb.") && !s.Name.StartsWith("bench.thumb") && !s.Name.StartsWith("bench.folderCover")) continue;
                Line(string.Format("| {0} | {1} | {2} | {3} | {4} | {5} |", s.Name, s.Count, F(s.AvgMs), F(s.P50Ms), F(s.P95Ms), F(s.MaxMs)));
            }
            Line("");

            // 2c. 程序真实流水线：VirtualGridCanvas.SetItems 触发的并发预加载，冷（无磁盘缓存）/热（有磁盘缓存）
            Line("**程序真实并发预加载流水线（VirtualGridCanvas.SetItems → 全部缩略图进入内存）**");
            Line("");
            Line("| 场景 | 项数 | 墙钟 ms | 吞吐 项/s |");
            Line("| :--- | ---: | ---: | ---: |");
            try { Directory.Delete(AppPaths.ThumbnailCacheDir, true); } catch (Exception ex) { Log.Debug(ex, "bench 清缩略图缓存"); }
            RunPipeline("冷：无磁盘缓存", ok);
            RunPipeline("热：磁盘缓存命中", ok);
            return ok;
        }

        private static void RunPipeline(string label, List<EntryItem> ok)
        {
            using (VirtualGridCanvas canvas = new VirtualGridCanvas())
            {
                canvas.Size = new Size(1920, 1080);
                canvas.TileSize = 260;
                List<EntryItem> list = CloneItems(ok);
                Stopwatch sw = Stopwatch.StartNew();
                canvas.SetItems(list);
                int expected = list.Count;
                while (canvas.CachedThumbnailCount < expected && sw.Elapsed.TotalSeconds < 180)
                {
                    Thread.Sleep(5);
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                Line(string.Format("| {0} | {1}/{2} | {3} | {4} |", label, canvas.CachedThumbnailCount, expected, F(ms), F1(canvas.CachedThumbnailCount * 1000.0 / Math.Max(1, ms))));
                canvas.ClearThumbnailCache();
            }
        }

        // ───────────────────────── 3. 网格绘制 ─────────────────────────

        private static void BenchGridPaint(List<EntryItem> okItems, int frames)
        {
            List<EntryItem> items = CloneItems(okItems);
            // 让绘制覆盖真实分支：每 5 项一个标签、每 7 项一个双标签、少量选中
            for (int i = 0; i < items.Count; i++)
            {
                if (i % 5 == 0) items[i].TagUids.Add(MainWindow.ORDERED_TAGS[i % 4].Uid);
                if (i % 7 == 0) items[i].TagUids.Add(MainWindow.ORDERED_TAGS[(i + 1) % 4].Uid);
            }
            Size[] viewports = new[] { new Size(1920, 1080), new Size(3840, 2160) };
            int[] tiles = new[] { 128, 260, 420, 640 };

            using (VirtualGridCanvas canvas = new VirtualGridCanvas())
            {
                canvas.Size = viewports[0];
                canvas.SetItems(items);
                Stopwatch wait = Stopwatch.StartNew();
                while (canvas.CachedThumbnailCount < items.Count && wait.Elapsed.TotalSeconds < 120) Thread.Sleep(5);
                for (int i = 0; i < items.Count; i += 9) items[i].IsSelected = true;
                Line("网格项 " + items.Count + "，缩略图已全部在内存（" + canvas.CachedThumbnailCount + "），每种组合渲染 " + frames + " 帧（滚动位置变化），离屏 32bppPArgb 位图。");
                Line("");
                Line("| 视口 | tile px | 可见项≈ | avg ms/帧 | p95 ms | max ms | 等效 fps |");
                Line("| :--- | ---: | ---: | ---: | ---: | ---: | ---: |");
                foreach (Size vp in viewports)
                {
                    canvas.Size = vp;
                    using (Bitmap target = new Bitmap(vp.Width, vp.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(target))
                    {
                        foreach (int tile in tiles)
                        {
                            canvas.TileSize = tile;
                            string name = "bench.paint." + vp.Width + "x" + vp.Height + ".t" + tile;
                            canvas.ScrollOffset = 0;
                            canvas.RenderForBench(g); // 预热
                            int visible = CountVisible(canvas, vp.Height);
                            for (int f = 0; f < frames; f++)
                            {
                                canvas.ScrollOffset = (f * 997) % 100000;
                                Stopwatch sw = Stopwatch.StartNew();
                                canvas.RenderForBench(g);
                                Perf.Record(name, sw.Elapsed.TotalMilliseconds);
                            }
                            Perf.Summary s = Perf.Get(name);
                            Line(string.Format("| {0}x{1} | {2} | {3} | {4} | {5} | {6} | {7} |", vp.Width, vp.Height, tile, visible,
                                F(s.AvgMs), F(s.P95Ms), F(s.MaxMs), F1(1000.0 / Math.Max(0.01, s.AvgMs))));
                        }
                    }
                }
                canvas.ClearThumbnailCache();
            }
        }

        private static int CountVisible(VirtualGridCanvas canvas, int viewH)
        {
            int n = 0;
            int top = canvas.ScrollOffset;
            foreach (EntryItem it in canvas.Items)
            {
                if (it.Bounds.Bottom >= top && it.Bounds.Top <= top + viewH) n++;
            }
            return n;
        }

        // ───────────────────────── 4. 看图器 ─────────────────────────

        private static void BenchViewer(List<EntryItem> items, int frames)
        {
            frames = Math.Min(frames, 40);
            string largest = null;
            long largestLen = -1;
            List<string> imgs = new List<string>();
            foreach (EntryItem it in items)
            {
                if (it.IsFolder) continue;
                string ext = Path.GetExtension(it.FullPath).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                imgs.Add(it.FullPath);
                long len = 0;
                try { len = new FileInfo(it.FullPath).Length; } catch (Exception ex) { Log.Debug(ex, "bench 取文件大小"); }
                if (len > largestLen) { largestLen = len; largest = it.FullPath; }
            }
            if (largest == null)
            {
                Line("没有可用的 jpg/png 样本。");
                return;
            }
            Perf.Reset("viewer.");
            using (FastImageViewerForm viewer = new FastImageViewerForm(largest, imgs))
            {
                viewer.ClientSize = new Size(1800, 1000);
                Perf.Summary load = Perf.Get("viewer.load");
                Line("样本：`" + largest + "`（" + F1(largestLen / 1048576.0) + " MB），窗口客户区 1800x1000。");
                Line("");
                Line("- 打开（解码）耗时: " + (load != null ? F(load.AvgMs) : "-") + " ms");
                using (Bitmap target = new Bitmap(1800, 1000, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(target))
                {
                    viewer.RenderForBench(g);
                    for (int f = 0; f < frames; f++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        viewer.RenderForBench(g);
                        Perf.Record("bench.viewer.paintNormal", sw.Elapsed.TotalMilliseconds);
                    }
                    for (int f = 0; f < frames; f++)
                    {
                        viewer.SimulateScreenshotModeForBench(new Point(200 + (f * 37) % 1400, 120 + (f * 53) % 800));
                        Stopwatch sw = Stopwatch.StartNew();
                        viewer.RenderForBench(g);
                        Perf.Record("bench.viewer.paintScreenshotMode", sw.Elapsed.TotalMilliseconds);
                    }
                }
                Perf.Summary n = Perf.Get("bench.viewer.paintNormal");
                Perf.Summary m = Perf.Get("bench.viewer.paintScreenshotMode");
                Line("- 普通模式每帧: avg " + F(n.AvgMs) + " / p95 " + F(n.P95Ms) + " ms");
                Line("- 截图模式（十字线+放大镜，鼠标移动即重绘）每帧: avg " + F(m.AvgMs) + " / p95 " + F(m.P95Ms) + " ms");
            }

            // 翻页：逐张打开耗时
            int pages = Math.Min(20, imgs.Count);
            for (int i = 0; i < pages; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                Bitmap b = VirtualGridCanvas.LoadAnyImageBitmap(imgs[i], 3840);
                if (b != null) b.Dispose();
                Perf.Record("bench.viewer.pageLoad", sw.Elapsed.TotalMilliseconds);
            }
            Perf.Summary pl = Perf.Get("bench.viewer.pageLoad");
            if (pl != null) Line("- 翻页（解码原图）" + pl.Count + " 张: avg " + F(pl.AvgMs) + " / p95 " + F(pl.P95Ms) + " / max " + F(pl.MaxMs) + " ms");
        }

        // ───────────────────────── 5. 视频 ─────────────────────────

        private static List<string> PickVideos(List<string> dirs, int max)
        {
            List<FileInfo> vids = new List<FileInfo>();
            foreach (string d in dirs)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    {
                        if (VirtualGridCanvas.IsVideoExtension(Path.GetExtension(f))) vids.Add(new FileInfo(f));
                    }
                }
                catch (Exception ex) { Log.Warn(ex, "bench 枚举视频 " + d); }
            }
            vids.Sort((a, b) => a.Length.CompareTo(b.Length));
            List<string> picked = new List<string>();
            if (vids.Count == 0) return picked;
            // 取 最小 / 中位 / 最大 …
            int[] idx = max <= 1 ? new[] { vids.Count / 2 } : new[] { 0, vids.Count / 2, vids.Count - 1 };
            foreach (int i in idx)
            {
                if (picked.Count >= max) break;
                string p = vids[Math.Max(0, Math.Min(vids.Count - 1, i))].FullName;
                if (!picked.Contains(p)) picked.Add(p);
            }
            return picked;
        }

        private static void BenchVideo(List<string> dirs, string ffmpeg, int maxVideos)
        {
            List<string> videos = PickVideos(dirs, maxVideos);
            if (videos.Count == 0)
            {
                Line("测试目录中没有视频。");
                return;
            }
            Line("策略 = v2 优化版当前实现：第 0 段 17 帧极速首包 + 11 段 × 54 帧（4 并发 ffmpeg 进程，总进程数从 36 降至 12），后台同步解码为冻结 BitmapSource，UI 线程直接赋值（零 JPEG 解码）。");
            Line("");
            Line("| 视频 | 大小 MB | 时长 s | 分辨率 | probe ms | 首帧(含解码) ms | 全部抽帧+后台解码 ms | 磁盘缓存热载全量解码 ms | 帧数 | 平均帧 KB | UI 线程解码 ms |");
            Line("| :--- | ---: | ---: | :---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (string v in videos)
            {
                BenchOneVideo(v, ffmpeg);
            }
            BenchVideoExtras(videos, ffmpeg);
        }

        private static void BenchOneVideo(string video, string ffmpeg)
        {
            Stopwatch sw = Stopwatch.StartNew();
            double dur; int vw, vh;
            bool okProbe = VideoHoverPreviewWindow.ProbeVideoDurationAndResolutionViaFfmpeg(ffmpeg, video, out dur, out vw, out vh);
            double probeMs = sw.Elapsed.TotalMilliseconds;
            if (!okProbe) dur = 15.0;

            double firstMs = -1;
            List<System.Windows.Media.Imaging.BitmapSource> decoded;
            sw.Restart();
            List<byte[]> reel = VideoHoverPreviewWindow.BuildReelOptimized(ffmpeg, video, dur, () => false, progressiveDecoded =>
            {
                if (firstMs < 0) firstMs = sw.Elapsed.TotalMilliseconds;
            }, out decoded);
            double totalMs = sw.Elapsed.TotalMilliseconds;
            int frames = decoded != null ? decoded.Count : 0;
            long bytes = 0;
            if (reel != null) foreach (byte[] b in reel) bytes += b.Length;

            // 测量并行解码（模拟磁盘 .reel 命中后的后台全量解码耗时）
            double warmDecodeMs = -1;
            if (reel != null && reel.Count > 0)
            {
                sw.Restart();
                VideoHoverPreviewWindow.DecodeReelParallel(reel);
                warmDecodeMs = sw.Elapsed.TotalMilliseconds;
            }

            long len = new FileInfo(video).Length;
            Line(string.Format("| `{0}` | {1} | {2} | {3}x{4} | {5} | {6} | {7} | {8} | {9} | {10} | 0.00 (已后台冻结) |",
                Path.GetFileName(video), F1(len / 1048576.0), F1(dur), vw, vh, F(probeMs), F(firstMs), F(totalMs),
                warmDecodeMs >= 0 ? F(warmDecodeMs) : "-", frames,
                F1(frames > 0 ? bytes / 1024.0 / frames : 0)));
        }

        private static void BenchVideoExtras(List<string> videos, string ffmpeg)
        {
            Line("");
            Line("### 同机对比：v1 旧版策略（36 段 × 17 帧 × 3 并发进程 + UI 线程逐帧 JPEG 解码）");
            Line("");
            Line("| 视频 | 旧版首帧(未解码) ms | 旧版全部抽帧 ms | 帧数 | 旧版 UI 线程 JPEG 解码 avg ms | p95 ms |");
            Line("| :--- | ---: | ---: | ---: | ---: | ---: |");
            foreach (string video in videos)
            {
                double dur; int vw, vh;
                if (!VideoHoverPreviewWindow.ProbeVideoDurationAndResolutionViaFfmpeg(ffmpeg, video, out dur, out vw, out vh)) dur = 15.0;
                double firstMs = -1;
                Stopwatch sw = Stopwatch.StartNew();
                List<byte[]> reel = VideoHoverPreviewWindow.BuildReel(ffmpeg, video, dur, 36, 17, 3, () => false, progressive =>
                {
                    if (firstMs < 0) firstMs = sw.Elapsed.TotalMilliseconds;
                });
                double totalMs = sw.Elapsed.TotalMilliseconds;
                string decName = "bench.v1JpegDecode." + Path.GetFileName(video);
                if (reel != null)
                {
                    foreach (byte[] b in reel)
                    {
                        Stopwatch d = Stopwatch.StartNew();
                        VideoHoverPreviewWindow.DecodeJpegBytesToBitmapImage(b);
                        Perf.Record(decName, d.Elapsed.TotalMilliseconds);
                    }
                }
                Perf.Summary ds = Perf.Get(decName);
                Line(string.Format("| `{0}` | {1} | {2} | {3} | {4} | {5} |",
                    Path.GetFileName(video), F(firstMs), F(totalMs), reel != null ? reel.Count : 0,
                    ds != null ? F(ds.AvgMs) : "-", ds != null ? F(ds.P95Ms) : "-"));
            }
        }
    }
}
