// 视频预览胶片：ffmpeg 探测/抽帧（17帧极速首包 + 11段×54帧低进程开销并发抽帧）、后台预解码冻结 BitmapSource、内存与磁盘缓存。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DshGalleryExplorer
{
    internal sealed partial class VideoHoverPreviewWindow
    {
        private const int MAX_MEMORY_CACHED_REELS = 24;
        private static readonly ConcurrentDictionary<string, List<BitmapSource>> _memoryDecodedReelCache =
            new ConcurrentDictionary<string, List<BitmapSource>>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> _memoryReelOrder = new ConcurrentQueue<string>();

        private static string REEL_DISK_CACHE_DIR { get { return AppPaths.VideoReelCacheDir; } }

        internal static string ResolveFfmpegPath()
        {
            return AppPaths.ResolveFfmpegPath();
        }

        private static void CacheDecodedReelInMemory(string videoPath, List<BitmapSource> frames)
        {
            if (string.IsNullOrEmpty(videoPath) || frames == null || frames.Count <= 30) return;
            if (_memoryDecodedReelCache.TryAdd(videoPath, frames))
            {
                _memoryReelOrder.Enqueue(videoPath);
                while (_memoryDecodedReelCache.Count > MAX_MEMORY_CACHED_REELS)
                {
                    string oldest;
                    if (!_memoryReelOrder.TryDequeue(out oldest)) break;
                    List<BitmapSource> removed;
                    _memoryDecodedReelCache.TryRemove(oldest, out removed);
                }
            }
            else
            {
                _memoryDecodedReelCache[videoPath] = frames;
            }
        }

        internal static List<BitmapSource> DecodeReelParallel(List<byte[]> jpegs)
        {
            if (jpegs == null || jpegs.Count == 0) return new List<BitmapSource>();
            BitmapSource[] arr = new BitmapSource[jpegs.Count];
            Parallel.For(0, jpegs.Count, i =>
            {
                byte[] b = jpegs[i];
                if (b != null && b.Length > 0)
                {
                    arr[i] = DecodeJpegBytesToBitmapImage(b);
                }
            });
            List<BitmapSource> res = new List<BitmapSource>(jpegs.Count);
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null) res.Add(arr[i]);
            }
            return res;
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
                    try { proc.WaitForExit(1500); } catch (Exception ex) { Log.Debug(ex, "等待 ffmpeg probe 退出"); }

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
            catch (Exception ex) { Log.Debug(ex, "ffmpeg 探测视频失败: " + videoPath); }
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
            catch (Exception ex)
            {
                Log.Debug(ex, "计算视频胶片缓存路径");
                return null;
            }
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
            catch (Exception ex)
            {
                Log.Debug(ex, "读取磁盘胶片缓存失败: " + cacheFile);
                return null;
            }
        }

        private static void SaveReelToDisk(string cacheFile, List<byte[]> frames)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFile) || frames == null || frames.Count < 30) return;
                string dir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = cacheFile + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
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
            catch (Exception ex) { Log.Debug(ex, "写入磁盘胶片缓存失败: " + cacheFile); }
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
        /// 优化版默认胶片抽取：第 0 段单发 17 帧保证极速首帧（~90ms），其余 11 段 × 54 帧由 4 并发进程抽取，
        /// 将 ffmpeg 进程创建与解复用开销从 36 次降至 12 次，并在后台线程同步解码为冻结 BitmapSource。
        /// </summary>
        internal static List<byte[]> BuildReelOptimized(string ffmpegExe, string videoPath, double durationSec,
            Func<bool> isCancelled, Action<List<BitmapSource>> onDecodedProgress, out List<BitmapSource> finalDecoded)
        {
            const int tailChunks = 11;
            const int starterFrames = 17;
            const int tailFramesPerChunk = 54;
            const int workerCount = 4;
            int totalSlots = 1 + tailChunks;

            double usableDur = Math.Max(1.0, durationSec * 0.985);
            List<byte[]>[] rawChunks = new List<byte[]>[totalSlots];
            List<BitmapSource>[] decChunks = new List<BitmapSource>[totalSlots];

            Action<int, int, double> runSlot = (int slot, int frameCount, double seekSec) =>
            {
                if (isCancelled()) return;
                List<byte[]> parsed = ExtractReelChunk(ffmpegExe, videoPath, seekSec, frameCount, isCancelled);
                if (parsed == null || parsed.Count == 0 || isCancelled()) return;

                List<BitmapSource> dec = new List<BitmapSource>(parsed.Count);
                for (int i = 0; i < parsed.Count; i++)
                {
                    if (isCancelled()) return;
                    BitmapImage bmi = DecodeJpegBytesToBitmapImage(parsed[i]);
                    if (bmi != null) dec.Add(bmi);
                }
                rawChunks[slot] = parsed;
                decChunks[slot] = dec;

                if (!isCancelled() && onDecodedProgress != null)
                {
                    List<BitmapSource> prog = new List<BitmapSource>(620);
                    for (int k = 0; k < totalSlots; k++)
                    {
                        if (decChunks[k] != null && decChunks[k].Count > 0) prog.AddRange(decChunks[k]);
                    }
                    if (prog.Count > 0) onDecodedProgress(prog);
                }
            };

            // 1. 零竞争抽取第 0 段 17 帧，确保首帧延迟最低
            runSlot(0, starterFrames, 0.0);
            if (isCancelled())
            {
                finalDecoded = null;
                return null;
            }

            // 2. 4 并发进程抽取后续 11 段 × 54 帧（总进程数从 36 减至 12）
            int nextSlot = 1;
            int completedWorkers = 0;
            using (ManualResetEvent allDone = new ManualResetEvent(false))
            {
                for (int w = 0; w < workerCount; w++)
                {
                    ThreadPool.QueueUserWorkItem(__ =>
                    {
                        try
                        {
                            while (true)
                            {
                                if (isCancelled()) break;
                                int s = Interlocked.Increment(ref nextSlot) - 1;
                                if (s >= totalSlots) break;
                                double seekSec = (s * usableDur) / totalSlots;
                                runSlot(s, tailFramesPerChunk, seekSec);
                            }
                        }
                        catch (Exception ex) { Log.Debug(ex, "后台胶片分段抽帧异常"); }
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
            }

            if (isCancelled())
            {
                finalDecoded = null;
                return null;
            }

            List<byte[]> finalRaw = new List<byte[]>(620);
            finalDecoded = new List<BitmapSource>(620);
            for (int k = 0; k < totalSlots; k++)
            {
                if (rawChunks[k] != null) finalRaw.AddRange(rawChunks[k]);
                if (decChunks[k] != null) finalDecoded.AddRange(decChunks[k]);
            }
            return finalRaw;
        }

        /// <summary>通用分段抽取接口（保留供基准测试对比不同 chunkCount × framesPerChunk × workerCount 参数）。</summary>
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

            extractSingleChunk(0);
            if (isCancelled()) return null;

            int nextChunk = 1;
            int completedWorkers = 0;
            using (ManualResetEvent allDone = new ManualResetEvent(false))
            {
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
                        catch (Exception ex) { Log.Debug(ex, "BuildReel 分段异常"); }
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
            }

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

        internal static BitmapImage DecodeJpegBytesToBitmapImage(byte[] jpegBytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpegBytes, false))
                {
                    BitmapImage bmi = new BitmapImage();
                    bmi.BeginInit();
                    bmi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmi.CacheOption = BitmapCacheOption.OnLoad;
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
