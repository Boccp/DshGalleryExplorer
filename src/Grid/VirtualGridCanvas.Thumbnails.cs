// 网格缩略图：内存/磁盘两级缓存、WIC 多核无锁解码、视频/WebP FFmpeg 抽帧。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DshGalleryExplorer
{
    internal sealed partial class VirtualGridCanvas
    {
        private static readonly string[] IMAGE_EXTS = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".ico" };
        public static readonly string[] VIDEO_EXTS = new[] {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp",
            ".m2ts", ".mts", ".vob", ".ogv", ".rm", ".rmvb", ".asf", ".f4v", ".divx", ".xvid", ".dv", ".3g2", ".mxf"
        };

        public static bool IsVideoExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            return Array.IndexOf(VIDEO_EXTS, ext.ToLowerInvariant()) >= 0;
        }

        public static bool IsSupportedMediaExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            string low = ext.ToLowerInvariant();
            return Array.IndexOf(IMAGE_EXTS, low) >= 0 || Array.IndexOf(VIDEO_EXTS, low) >= 0;
        }

        public static Bitmap ExtractVideoCoverViaFfmpeg(string videoPath, int maxDim)
        {
            try
            {
                string ffmpegExe = VideoHoverPreviewWindow.ResolveFfmpegPath();
                if (string.IsNullOrEmpty(ffmpegExe) || !File.Exists(ffmpegExe)) return null;

                bool isVideo = IsVideoExtension(Path.GetExtension(videoPath));
                string args = isVideo
                    ? string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -noaccurate_seek -ss 0.5 -i \"{0}\" -frames:v 1 -an -vf \"scale={1}:-2:flags=fast_bilinear\" -pix_fmt yuvj420p -strict -2 -q:v 4 -f image2pipe -vcodec mjpeg pipe:1",
                        videoPath, Math.Max(160, maxDim))
                    : string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -i \"{0}\" -frames:v 1 -vf \"scale='min({1},iw)':-2:flags=fast_bilinear\" -pix_fmt yuvj420p -strict -2 -q:v 3 -f image2pipe -vcodec mjpeg pipe:1",
                        videoPath, Math.Max(160, maxDim));

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        using (MemoryStream ms = new MemoryStream(65536))
                        {
                            byte[] buf = new byte[16384];
                            int read;
                            while ((read = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, read);
                            }
                            try { proc.WaitForExit(1500); } catch (Exception ex) { Log.Debug(ex, "等待 ffmpeg 封面退出"); }
                            if (ms.Length > 64)
                            {
                                Bitmap wicBmp = TryDecodeStreamViaWicToPArgb(ms.GetBuffer(), (int)ms.Length, 0);
                                if (wicBmp != null) return wicBmp;

                                ms.Position = 0;
                                using (Image img = Image.FromStream(ms, false, false))
                                {
                                    return CloneToPArgb(img);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "ffmpeg 提取封面失败: " + videoPath); }
            return null;
        }

        public static Bitmap LoadAnyImageBitmap(string path, int maxDim)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isVideo = IsVideoExtension(ext);
            bool isImage = Array.IndexOf(IMAGE_EXTS, ext) >= 0;
            if (!isImage && !isVideo)
            {
                return null;
            }
            // 视频与 .webp 仍走 FFmpeg，绝不触发缺失微软商店解码器弹窗
            if (isVideo || ext == ".webp")
            {
                return ExtractVideoCoverViaFfmpeg(path, maxDim);
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                // maxDim >= 3840（看图器原图浏览与截图裁剪）保留 100% 原始像素分辨率；缩略图（< 3840）走 WIC 解码期降采样
                int wicMax = maxDim >= 3840 ? 0 : maxDim;
                Bitmap wicBmp = TryDecodeStreamViaWicToPArgb(bytes, bytes.Length, wicMax);
                if (wicBmp != null) return wicBmp;

                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    return CloneToPArgb(img);
                }
            }
            catch (Exception ex) { Log.Debug(ex, "解码图片失败: " + path); }
            return null;
        }

        /// <summary>
        /// 使用原生 WIC 将内存图像流解码为 GDI+ 最快直绘格式 Format32bppPArgb（无 GDI+ 全局锁，多核线性扩展）。
        /// maxDim &gt; 0 时若原图超过 maxDim，直接由 WIC 在解码阶段按比例降采样（JPEG IDCT 1/2, 1/4, 1/8 硬件加速）。
        /// </summary>
        private static Bitmap TryDecodeStreamViaWicToPArgb(byte[] bytes, int length, int maxDim)
        {
            try
            {
                int origW = 0, origH = 0;
                if (maxDim > 0)
                {
                    using (MemoryStream headerMs = new MemoryStream(bytes, 0, length, false))
                    {
                        BitmapDecoder dec = BitmapDecoder.Create(
                            headerMs,
                            BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                            BitmapCacheOption.None);
                        if (dec.Frames.Count > 0)
                        {
                            origW = dec.Frames[0].PixelWidth;
                            origH = dec.Frames[0].PixelHeight;
                        }
                    }
                }

                BitmapImage bmi = new BitmapImage();
                using (MemoryStream ms = new MemoryStream(bytes, 0, length, false))
                {
                    bmi.BeginInit();
                    bmi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmi.CacheOption = BitmapCacheOption.OnLoad;
                    bmi.StreamSource = ms;
                    if (maxDim > 0 && origW > 0 && origH > 0 && (origW > maxDim || origH > maxDim))
                    {
                        if (origW >= origH) bmi.DecodePixelWidth = maxDim;
                        else bmi.DecodePixelHeight = maxDim;
                    }
                    bmi.EndInit();
                }

                return CopyBitmapSourceToPArgb(bmi);
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap CopyBitmapSourceToPArgb(BitmapSource src)
        {
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return null;
            BitmapSource pbgra = src.Format == System.Windows.Media.PixelFormats.Pbgra32
                ? src
                : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Pbgra32, null, 0);

            int w = pbgra.PixelWidth;
            int h = pbgra.PixelHeight;
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData bd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                pbgra.CopyPixels(Int32Rect.Empty, bd.Scan0, bd.Stride * h, bd.Stride);
            }
            finally
            {
                dst.UnlockBits(bd);
            }
            return dst;
        }

        private static Bitmap CloneToPArgb(Image src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return dst;
        }

        private volatile bool _dirtyThumbnails = false;
        private System.Windows.Forms.Timer _thumbInvalidateTimer;

        private void StartEagerDirectoryPreload(List<EntryItem> snapshot)
        {
            int gen = Interlocked.Increment(ref _preloadGeneration);
            if (snapshot == null || snapshot.Count == 0) return;

            int curScroll = _scrollOffset;
            List<EntryItem> workList = new List<EntryItem>(snapshot);
            workList.Sort((a, b) =>
            {
                int da = Math.Abs(a.Bounds.Top - curScroll);
                int db = Math.Abs(b.Bounds.Top - curScroll);
                return da.CompareTo(db);
            });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                int nextIdx = 0;
                int workerCount = Math.Min(Math.Max(4, Environment.ProcessorCount / 2), workList.Count);
                for (int w = 0; w < workerCount; w++)
                {
                    ThreadPool.QueueUserWorkItem(__ =>
                    {
                        while (true)
                        {
                            if (gen != _preloadGeneration) break;
                            int idx = Interlocked.Increment(ref nextIdx) - 1;
                            if (idx < 0 || idx >= workList.Count) break;
                            EntryItem it = workList[idx];
                            if (it == null) continue;
                            if (_thumbCache.ContainsKey(it.FullPath)) continue;
                            DecodeAndCacheItemSync(it);
                        }
                    });
                }
            });
        }

        private void DecodeAndCacheItemSync(EntryItem item)
        {
            if (_thumbCache.ContainsKey(item.FullPath)) return;
            if (!_inFlight.TryAdd(item.FullPath, 1)) return;
            Perf.Scope perfScope = Perf.Measure("thumb.item");
            try
            {
                Bitmap bmp = TryLoadFromDiskCache(item);
                if (bmp == null)
                {
                    string imgPath = item.IsFolder ? item.CoverImagePath : item.FullPath;
                    if (string.IsNullOrEmpty(imgPath) && item.IsFolder)
                    {
                        imgPath = MainWindow.FindFirstImageInFolder(item.FullPath);
                        item.CoverImagePath = imgPath;
                    }
                    if (!string.IsNullOrEmpty(imgPath))
                    {
                        bmp = DecodeThumbnailBitmap(imgPath, item.IsFolder, 320);
                        if (bmp != null)
                        {
                            SaveToDiskCache(item, bmp);
                        }
                    }
                }
                if (bmp != null)
                {
                    if (!_thumbCache.TryAdd(item.FullPath, bmp))
                    {
                        bmp.Dispose();
                    }
                    else
                    {
                        _dirtyThumbnails = true;
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "缩略图流水线异常: " + item.FullPath); }
            finally
            {
                byte dummy;
                _inFlight.TryRemove(item.FullPath, out dummy);
                perfScope.Dispose();
            }
        }

        /// <summary>当前内存缩略图缓存数量（基准测试用）。</summary>
        internal int CachedThumbnailCount { get { return _thumbCache.Count; } }

        private static string GetDiskCacheFilePath(EntryItem item)
        {
            try
            {
                string key = string.Format("{0}|{1}", item.FullPath.ToLowerInvariant(), item.IsFolder ? "D" : "F");
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return Path.Combine(DISK_CACHE_DIR, sb.ToString() + (item.IsFolder ? ".png" : ".jpg"));
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "计算缩略图缓存路径");
                return null;
            }
        }

        internal static Bitmap TryLoadFromDiskCache(EntryItem item)
        {
            try
            {
                string cacheFile = GetDiskCacheFilePath(item);
                if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
                {
                    using (Perf.Measure("thumb.diskLoad"))
                    {
                        byte[] bytes = File.ReadAllBytes(cacheFile);
                        Bitmap wic = TryDecodeStreamViaWicToPArgb(bytes, bytes.Length, 0);
                        if (wic != null) return wic;

                        using (MemoryStream ms = new MemoryStream(bytes))
                        using (Image img = Image.FromStream(ms, false, false))
                        {
                            return CloneToPArgb(img);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "读取磁盘缩略图缓存失败: " + item.FullPath); }
            return null;
        }

        internal static void SaveToDiskCache(EntryItem item, Bitmap bmp)
        {
            try
            {
                string cacheFile = GetDiskCacheFilePath(item);
                if (string.IsNullOrEmpty(cacheFile) || File.Exists(cacheFile)) return;
                using (Perf.Measure("thumb.diskSave"))
                {
                    string tmp = cacheFile + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
                    bmp.Save(tmp, item.IsFolder ? ImageFormat.Png : ImageFormat.Jpeg);
                    if (!File.Exists(cacheFile))
                    {
                        try { File.Move(tmp, cacheFile); }
                        catch { try { File.Delete(tmp); } catch { } }
                    }
                    else
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "写入磁盘缩略图缓存失败: " + item.FullPath); }
        }

        public Bitmap TryGetCachedThumbnail(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            Bitmap bmp;
            if (_thumbCache.TryGetValue(fullPath, out bmp)) return bmp;
            return null;
        }

        public void ClearThumbnailCache()
        {
            foreach (var kv in _thumbCache)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _thumbCache.Clear();
            ClearScaledThumbnailCache();
            _inFlight.Clear();
            Invalidate();
        }

        private void ClearScaledThumbnailCache()
        {
            foreach (var kv in _scaledThumbCache)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _scaledThumbCache.Clear();
        }

        private void EnsureThumbnailRequested(EntryItem item)
        {
            if (_thumbCache.ContainsKey(item.FullPath)) return;
            ThreadPool.QueueUserWorkItem(_ => DecodeAndCacheItemSync(item));
        }

        internal static Bitmap DecodeThumbnailBitmap(string path, bool isFolder, int targetSize)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!IsSupportedMediaExtension(ext)) return null;

                // 快路径：非文件夹普通图片直接用原生 WIC 解码+缩放+居中拷贝至 Format32bppPArgb，全程零 GDI+ 全局锁！
                if (!isFolder && !IsVideoExtension(ext) && ext != ".webp")
                {
                    Bitmap fastWic = TryDecodeCenteredThumbnailViaWic(path, targetSize);
                    if (fastWic != null) return fastWic;
                }

                // 文件夹封面（需要 Cover 裁切比例）：WIC 仅解码到短边略大于 targetSize 的小图，避免全尺寸解码
                int decodeCap = isFolder ? targetSize * 2 : targetSize;
                Bitmap srcLoaded;
                using (Perf.Measure("thumb.decode"))
                {
                    srcLoaded = LoadAnyImageBitmap(path, decodeCap);
                }
                using (Bitmap src = srcLoaded)
                using (Perf.Measure("thumb.scale"))
                {
                    if (src == null) return null;
                    int w = src.Width;
                    int h = src.Height;
                    if (w <= 0 || h <= 0) return null;

                    Bitmap canvas = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        if (isFolder)
                        {
                            using (SolidBrush tabFill = new SolidBrush(Color.FromArgb(245, 179, 36)))
                            using (Pen tabPen = new Pen(Color.FromArgb(186, 122, 12), 2.5f))
                            using (SolidBrush cardBg = new SolidBrush(Color.FromArgb(30, 32, 38)))
                            using (Pen rimPen = new Pen(Color.FromArgb(245, 185, 45), 3.5f))
                            {
                                DrawRoundedRect(g, new Rectangle(10, 4, 132, 34), 10, tabFill, tabPen);
                                DrawRoundedRect(g, new Rectangle(5, 16, targetSize - 10, targetSize - 21), 16, cardBg, rimPen);
                            }

                            Rectangle imgBox = new Rectangle(10, 21, targetSize - 20, targetSize - 31);
                            DrawCoverCroppedImage(g, src, imgBox);
                        }
                        else
                        {
                            float scale = Math.Min((float)targetSize / w, (float)targetSize / h);
                            int dw = Math.Max(1, (int)Math.Round(w * scale));
                            int dh = Math.Max(1, (int)Math.Round(h * scale));
                            int dx = (targetSize - dw) / 2;
                            int dy = (targetSize - dh) / 2;
                            g.DrawImage(src, new Rectangle(dx, dy, dw, dh), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                        }
                    }
                    return canvas;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "生成缩略图失败: " + path);
                return null;
            }
        }

        private static Bitmap TryDecodeCenteredThumbnailViaWic(string path, int targetSize)
        {
            try
            {
                byte[] bytes;
                BitmapImage bmi = new BitmapImage();
                using (Perf.Measure("thumb.decode"))
                {
                    bytes = File.ReadAllBytes(path);
                    int origW = 0, origH = 0;
                    using (MemoryStream hms = new MemoryStream(bytes, false))
                    {
                        BitmapDecoder dec = BitmapDecoder.Create(
                            hms,
                            BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                            BitmapCacheOption.None);
                        if (dec.Frames.Count > 0)
                        {
                            origW = dec.Frames[0].PixelWidth;
                            origH = dec.Frames[0].PixelHeight;
                        }
                    }
                    if (origW <= 0 || origH <= 0) return null;

                    using (MemoryStream ms = new MemoryStream(bytes, false))
                    {
                        bmi.BeginInit();
                        bmi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bmi.CacheOption = BitmapCacheOption.OnLoad;
                        bmi.StreamSource = ms;
                        if (origW >= origH)
                        {
                            if (origW > targetSize) bmi.DecodePixelWidth = targetSize;
                        }
                        else
                        {
                            if (origH > targetSize) bmi.DecodePixelHeight = targetSize;
                        }
                        bmi.EndInit();
                    }
                }

                using (Perf.Measure("thumb.scale"))
                {
                    BitmapSource pbgra = bmi.Format == System.Windows.Media.PixelFormats.Pbgra32
                        ? (BitmapSource)bmi
                        : new FormatConvertedBitmap(bmi, System.Windows.Media.PixelFormats.Pbgra32, null, 0);

                    int w = pbgra.PixelWidth;
                    int h = pbgra.PixelHeight;
                    if (w <= 0 || h <= 0) return null;

                    // 若原图小于 targetSize，按原比例放大填充到 targetSize（与旧逻辑一致）
                    if (w < targetSize && h < targetSize)
                    {
                        double s = Math.Min((double)targetSize / w, (double)targetSize / h);
                        TransformedBitmap tb = new TransformedBitmap(pbgra, new System.Windows.Media.ScaleTransform(s, s));
                        pbgra = tb.Format == System.Windows.Media.PixelFormats.Pbgra32
                            ? (BitmapSource)tb
                            : new FormatConvertedBitmap(tb, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                        w = pbgra.PixelWidth;
                        h = pbgra.PixelHeight;
                    }

                    int copyW = Math.Min(targetSize, w);
                    int copyH = Math.Min(targetSize, h);
                    int dx = (targetSize - copyW) / 2;
                    int dy = (targetSize - copyH) / 2;

                    Bitmap canvas = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppPArgb);
                    BitmapData bd = canvas.LockBits(new Rectangle(0, 0, targetSize, targetSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        IntPtr destPtr = new IntPtr(bd.Scan0.ToInt64() + (long)dy * bd.Stride + dx * 4);
                        int bufSize = (copyH - 1) * bd.Stride + copyW * 4;
                        pbgra.CopyPixels(new Int32Rect(0, 0, copyW, copyH), destPtr, bufSize, bd.Stride);
                    }
                    finally
                    {
                        canvas.UnlockBits(bd);
                    }
                    return canvas;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
