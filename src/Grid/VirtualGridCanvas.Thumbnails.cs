// 网格缩略图：内存/磁盘两级缓存、后台并发预加载、图片/视频封面解码。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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
                            try { proc.WaitForExit(1500); } catch { }
                            if (ms.Length > 64)
                            {
                                ms.Position = 0;
                                using (Image img = Image.FromStream(ms, false, false))
                                {
                                    return new Bitmap(img);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
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
            // For video files or .webp files, use FFmpeg first so we NEVER trigger broken Microsoft Store/AppX extension popups!
            if (isVideo || ext == ".webp")
            {
                Bitmap ffBmp = ExtractVideoCoverViaFfmpeg(path, maxDim);
                if (ffBmp != null) return ffBmp;
                return null;
            }
            // For standard images (.jpg, .png, .bmp, .gif, .ico), use pure GDI+ MemoryStream!
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    return new Bitmap(img);
                }
            }
            catch { }
            return null;
        }

        private volatile bool _dirtyThumbnails = false;
        private System.Windows.Forms.Timer _thumbInvalidateTimer;

        private void StartEagerDirectoryPreload(List<EntryItem> snapshot)
        {
            int gen = Interlocked.Increment(ref _preloadGeneration);
            if (snapshot == null || snapshot.Count == 0) return;

            // Sort workList so items near the current scroll position are decoded FIRST!
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
                int workerCount = Math.Min(12, workList.Count);
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
                    _thumbCache[item.FullPath] = bmp;
                    _dirtyThumbnails = true;
                }
            }
            catch { }
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
            catch { return null; }
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
                        using (MemoryStream ms = new MemoryStream(bytes))
                        using (Image img = Image.FromStream(ms, false, false))
                        {
                            return new Bitmap(img);
                        }
                    }
                }
            }
            catch { }
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
                    bmp.Save(cacheFile, item.IsFolder ? ImageFormat.Png : ImageFormat.Jpeg);
                }
            }
            catch { }
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
            _inFlight.Clear();
            Invalidate();
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

                Bitmap srcLoaded;
                using (Perf.Measure("thumb.decode"))
                {
                    srcLoaded = LoadAnyImageBitmap(path, targetSize);
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
            catch
            {
                return null;
            }
        }
    }
}
