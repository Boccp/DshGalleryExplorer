// 视频预览胶片：ffmpeg 探测/抽帧、内存与磁盘缓存、JPEG 解码。
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
    internal sealed partial class VideoHoverPreviewWindow
    {
        private static readonly ConcurrentDictionary<string, List<byte[]>> _memoryReelCache =
            new ConcurrentDictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        private static string REEL_DISK_CACHE_DIR { get { return AppPaths.VideoReelCacheDir; } }
        private static readonly string[] FFMPEG_CANDIDATE_PATHS = new[]
        {
            @"D:\_tools\ffmpeg.exe",
            @"D:\JianyingPro\11.5.0.14471\ffmpeg.exe",
            @"D:\JianyingPro\11.4.2.14459\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"JianyingPro\Apps\10.7.0.14095\ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe"),
            @"C:\Users\Administrator\AppData\Local\hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe",
            @"C:\Users\Administrator\Desktop\处理图片和视频\ffmpeg.exe",
            @"F:\剪映\9.9.0.13784\ffmpeg.exe"
        };

        internal static string ResolveFfmpegPath()
        {
            foreach (string p in FFMPEG_CANDIDATE_PATHS)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        internal static bool ProbeVideoDurationAndResolutionViaFfmpeg(string ffmpegExe, string videoPath, out double durationSec, out int width, out int height)
        {
            durationSec = 0.0;
            width = 0;
            height = 0;
            try
            {
                if (string.IsNullOrEmpty(ffmpegExe) || !File.Exists(ffmpegExe) || string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                    return false;

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = "-hide_banner -i \"" + videoPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    string err = proc.StandardError.ReadToEnd();
                    try { proc.WaitForExit(1500); } catch { }

                    Match mDur = Regex.Match(err ?? "", @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (mDur.Success)
                    {
                        int hh = int.Parse(mDur.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        int mm = int.Parse(mDur.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double ss = double.Parse(mDur.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                        durationSec = hh * 3600.0 + mm * 60.0 + ss;
                    }

                    Match mRes = Regex.Match(err ?? "", @"Video:.*?\s(\d{2,5})x(\d{2,5})[\s,]");
                    if (mRes.Success)
                    {
                        width = int.Parse(mRes.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        height = int.Parse(mRes.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return durationSec > 0.05;
                }
            }
            catch { }
            return false;
        }

        internal static string GetReelDiskCachePath(string videoPath)
        {
            try
            {
                FileInfo fi = new FileInfo(videoPath);
                string key = string.Format("{0}|{1}|{2}|v2_612", videoPath.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc.Ticks);
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return Path.Combine(REEL_DISK_CACHE_DIR, sb.ToString() + ".reel");
                }
            }
            catch { return null; }
        }

        private static List<byte[]> TryLoadReelFromDisk(string cacheFile)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFile) || !File.Exists(cacheFile)) return null;
                byte[] all = File.ReadAllBytes(cacheFile);
                if (all.Length < 8) return null;
                int count = BitConverter.ToInt32(all, 0);
                if (count <= 0 || count > 2000) return null;
                List<byte[]> frames = new List<byte[]>(count);
                int pos = 4;
                for (int i = 0; i < count; i++)
                {
                    if (pos + 4 > all.Length) break;
                    int len = BitConverter.ToInt32(all, pos);
                    pos += 4;
                    if (len <= 0 || pos + len > all.Length) break;
                    byte[] f = new byte[len];
                    Buffer.BlockCopy(all, pos, f, 0, len);
                    frames.Add(f);
                    pos += len;
                }
                return frames.Count > 30 ? frames : null;
            }
            catch { return null; }
        }

        private static void SaveReelToDisk(string cacheFile, List<byte[]> frames)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFile) || frames == null || frames.Count < 30) return;
                string tmp = cacheFile + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(frames.Count);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        byte[] f = frames[i];
                        if (f == null || f.Length == 0) continue;
                        bw.Write(f.Length);
                        bw.Write(f);
                    }
                }
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
                File.Move(tmp, cacheFile);
            }
            catch { }
        }

        internal static List<byte[]> ParseConcatenatedJpegs(byte[] raw, int length)
        {
            List<byte[]> result = new List<byte[]>();
            if (raw == null || length < 4) return result;

            int frameStart = -1;
            for (int i = 0; i < length - 1; i++)
            {
                if (raw[i] == 0xFF && raw[i + 1] == 0xD8)
                {
                    if (frameStart < 0)
                    {
                        frameStart = i;
                    }
                }
                else if (raw[i] == 0xFF && raw[i + 1] == 0xD9 && frameStart >= 0)
                {
                    int frameLen = (i + 2) - frameStart;
                    if (frameLen > 128)
                    {
                        byte[] jpg = new byte[frameLen];
                        Buffer.BlockCopy(raw, frameStart, jpg, 0, frameLen);
                        result.Add(jpg);
                    }
                    frameStart = -1;
                    i++;
                }
            }
            return result;
        }

        /// <summary>
        /// 抽取“chunkCount 段 × framesPerChunk 帧”的预览胶片：先无竞争抽取第 0 段（让首帧尽快出现），
        /// 再用 workerCount 个并发 ffmpeg 进程抽取其余段。
        /// isCancelled() 为 true 时尽快退出并返回 null；onProgress 在每段完成后收到“按时间顺序拼好的当前全部帧”。
        /// 第 0 段抛出的异常向调用方传播（与重构前一致）。
        /// </summary>
        internal static List<byte[]> BuildReel(string ffmpegExe, string videoPath, double durationSec, int chunkCount, int framesPerChunk, int workerCount,
            Func<bool> isCancelled, Action<List<byte[]>> onProgress)
        {
            double usableDur = Math.Max(1.0, durationSec * 0.985);
            List<byte[]>[] chunkResults = new List<byte[]>[chunkCount];

            Action<int> extractSingleChunk = (int cIdx) =>
            {
                if (isCancelled()) return;
                double seekSec = (cIdx * usableDur) / chunkCount;
                List<byte[]> parsed = ExtractReelChunk(ffmpegExe, videoPath, seekSec, framesPerChunk, isCancelled);
                if (parsed != null) chunkResults[cIdx] = parsed;

                if (!isCancelled())
                {
                    List<byte[]> progressive = new List<byte[]>(chunkCount * framesPerChunk);
                    for (int k = 0; k < chunkCount; k++)
                    {
                        if (chunkResults[k] != null && chunkResults[k].Count > 0)
                        {
                            progressive.AddRange(chunkResults[k]);
                        }
                    }
                    if (progressive.Count > 0 && onProgress != null)
                    {
                        onProgress(progressive);
                    }
                }
            };

            // Priority Step 1: Extract Chunk 0 immediately with zero process contention!
            extractSingleChunk(0);
            if (isCancelled()) return null;

            // Priority Step 2: Extract remaining chunks across N concurrent workers
            int nextChunk = 1;
            int completedWorkers = 0;
            ManualResetEvent allDone = new ManualResetEvent(false);

            for (int w = 0; w < workerCount; w++)
            {
                ThreadPool.QueueUserWorkItem(__ =>
                {
                    try
                    {
                        while (true)
                        {
                            if (isCancelled()) break;
                            int cIdx = Interlocked.Increment(ref nextChunk) - 1;
                            if (cIdx >= chunkCount) break;
                            extractSingleChunk(cIdx);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (Interlocked.Increment(ref completedWorkers) == workerCount)
                        {
                            try { allDone.Set(); } catch { }
                        }
                    }
                });
            }

            allDone.WaitOne(20000);
            allDone.Close();

            if (isCancelled()) return null;

            List<byte[]> finalReel = new List<byte[]>(chunkCount * framesPerChunk);
            for (int k = 0; k < chunkCount; k++)
            {
                if (chunkResults[k] != null && chunkResults[k].Count > 0)
                {
                    finalReel.AddRange(chunkResults[k]);
                }
            }
            return finalReel;
        }

        /// <summary>
        /// 从 seekSec 处用一个 ffmpeg 进程连续抽取 framesPerChunk 帧（540px 宽 MJPEG），返回拆分后的 JPEG 列表。
        /// 进程启动失败返回 null；isCancelled() 为 true 时立即杀掉进程。
        /// </summary>
        internal static List<byte[]> ExtractReelChunk(string ffmpegExe, string videoPath, double seekSec, int framesPerChunk, Func<bool> isCancelled)
        {
            string args = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -noaccurate_seek -threads 2 -flags2 fast -skip_loop_filter all -ss {0:0.00} -i \"{1}\" -frames:v {2} -an -vf \"scale=540:-2:flags=fast_bilinear\" -pix_fmt yuvj420p -strict -2 -q:v 4 -f mjpeg pipe:1",
                seekSec, videoPath, framesPerChunk);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };

            using (Perf.Measure("reel.chunk"))
            using (Process proc = Process.Start(psi))
            {
                if (proc == null) return null;
                using (MemoryStream ms = new MemoryStream(262144))
                {
                    byte[] buf = new byte[32768];
                    int read;
                    while ((read = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (isCancelled())
                        {
                            try { proc.Kill(); } catch { }
                            break;
                        }
                        ms.Write(buf, 0, read);
                    }
                    try { proc.WaitForExit(1500); } catch { }
                    byte[] rawBytes = ms.GetBuffer();
                    int rawLen = (int)ms.Length;
                    return ParseConcatenatedJpegs(rawBytes, rawLen);
                }
            }
        }

        internal static System.Windows.Media.Imaging.BitmapImage DecodeJpegBytesToBitmapImage(byte[] jpegBytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpegBytes, false))
                {
                    System.Windows.Media.Imaging.BitmapImage bmi = new System.Windows.Media.Imaging.BitmapImage();
                    bmi.BeginInit();
                    bmi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmi.StreamSource = ms;
                    bmi.EndInit();
                    bmi.Freeze();
                    return bmi;
                }
            }
            catch { return null; }
        }
    }
}
