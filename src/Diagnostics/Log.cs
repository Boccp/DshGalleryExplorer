using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace DshGalleryExplorer
{
    /// <summary>
    /// 轻量异步日志：调用方只做字符串拼接 + 入队（无锁 ConcurrentQueue），
    /// 由后台线程批量追加写入 %USERPROFILE%\.dsh\gallery_cache\logs\gallery-yyyyMMdd.log。
    /// - 自动只保留最近 7 天日志；
    /// - 同一“上下文 + 异常类型”超过 50 次后自动抑制，避免缩略图批量失败时刷爆日志；
    /// - Debug 级别默认关闭（config.ini 中 LogLevel=Debug 或环境变量 DSH_GALLERY_LOG=debug 开启）。
    /// 任何日志异常都不会向调用方抛出。
    /// </summary>
    internal static class Log
    {
        private const int MAX_QUEUE = 20000;
        private const int MAX_REPEAT_PER_KEY = 50;
        private const int KEEP_DAYS = 7;

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly ConcurrentDictionary<string, int> _repeatCounter = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static readonly ManualResetEvent _drained = new ManualResetEvent(true);
        private static readonly object _initLock = new object();
        private static Thread _writer;
        private static volatile bool _initialized;
        private static volatile bool _debugEnabled;
        private static string _logDir;
        private static int _queued;

        public static bool IsDebugEnabled { get { return _debugEnabled; } }

        public static string LogDirectory { get { EnsureInit(); return _logDir; } }

        public static void Init(string logDir, bool debugEnabled)
        {
            lock (_initLock)
            {
                _logDir = logDir;
                _debugEnabled = debugEnabled;
                if (_writer == null)
                {
                    _writer = new Thread(WriterLoop) { IsBackground = true, Name = "DshLogWriter", Priority = ThreadPriority.BelowNormal };
                    _writer.Start();
                }
                _initialized = true;
            }
            ThreadPool.QueueUserWorkItem(_ => CleanupOldLogs());
        }

        private static void EnsureInit()
        {
            if (_initialized) return;
            bool debug = false;
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_GALLERY_LOG");
                debug = !string.IsNullOrEmpty(env) && env.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* 读取环境变量失败时按默认 Info 级别 */ }
            Init(AppPaths.LogDir, debug);
        }

        public static void SetDebugEnabled(bool enabled) { _debugEnabled = enabled; }

        public static void Debug(string message)
        {
            if (!_debugEnabled) return;
            Enqueue("DEBUG", message, null);
        }

        public static void Debug(Exception ex, string context)
        {
            if (!_debugEnabled) return;
            Enqueue("DEBUG", context, ex);
        }

        public static void Info(string message)
        {
            Enqueue("INFO ", message, null);
        }

        public static void Warn(string message)
        {
            Enqueue("WARN ", message, null);
        }

        public static void Warn(Exception ex, string context)
        {
            Enqueue("WARN ", context, ex);
        }

        public static void Error(Exception ex, string context)
        {
            Enqueue("ERROR", context, ex);
        }

        private static void Enqueue(string level, string message, Exception ex)
        {
            try
            {
                EnsureInit();
                if (ex != null || level != "INFO ")
                {
                    // 同类日志抑制：key = 级别 + 上下文 + 异常类型
                    string key = level + "|" + (message ?? "") + "|" + (ex != null ? ex.GetType().FullName : "");
                    int n = _repeatCounter.AddOrUpdate(key, 1, (k, v) => v + 1);
                    if (n > MAX_REPEAT_PER_KEY) return;
                    if (n == MAX_REPEAT_PER_KEY) message = (message ?? "") + "（同类日志已达 " + MAX_REPEAT_PER_KEY + " 条，本次运行后续不再记录）";
                }
                if (Interlocked.Increment(ref _queued) > MAX_QUEUE)
                {
                    Interlocked.Decrement(ref _queued);
                    return;
                }

                StringBuilder sb = new StringBuilder(128);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level).Append("] [T").Append(Thread.CurrentThread.ManagedThreadId).Append("] ");
                sb.Append(message ?? "");
                if (ex != null)
                {
                    sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    if (_debugEnabled || level == "ERROR") sb.Append(Environment.NewLine).Append(ex.StackTrace);
                }
                string line = sb.ToString();
                _drained.Reset();
                _queue.Enqueue(line);
                _signal.Set();
            }
            catch { /* 日志系统自身绝不抛异常 */ }
        }

        /// <summary>等待队列写完（退出前调用）。</summary>
        public static void Flush(int timeoutMs)
        {
            try
            {
                if (!_initialized) return;
                _signal.Set();
                _drained.WaitOne(timeoutMs);
            }
            catch { /* 退出阶段忽略 */ }
        }

        private static void WriterLoop()
        {
            StringBuilder batch = new StringBuilder(8192);
            while (true)
            {
                try
                {
                    _signal.WaitOne(1000);
                    batch.Length = 0;
                    string line;
                    while (_queue.TryDequeue(out line))
                    {
                        Interlocked.Decrement(ref _queued);
                        batch.Append(line).Append(Environment.NewLine);
                    }
                    if (batch.Length > 0) WriteBatch(batch.ToString());
                    if (_queue.IsEmpty) _drained.Set();
                }
                catch
                {
                    // 写盘失败（磁盘满/权限不足）时丢弃本批，避免后台线程退出
                    _drained.Set();
                    Thread.Sleep(200);
                }
            }
        }

        private static void WriteBatch(string text)
        {
            string dir = _logDir;
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "gallery-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            // FileShare.ReadWrite：允许多开实例同时追加、允许外部 tail 查看
            using (FileStream fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 8192))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                string dir = _logDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                DateTime cutoff = DateTime.Now.Date.AddDays(-(KEEP_DAYS - 1));
                foreach (string f in Directory.GetFiles(dir, "gallery-*.log"))
                {
                    try
                    {
                        string stamp = Path.GetFileNameWithoutExtension(f).Substring("gallery-".Length);
                        DateTime d;
                        bool parsed = DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out d);
                        if (!parsed) d = File.GetLastWriteTime(f).Date;
                        if (d < cutoff) File.Delete(f);
                    }
                    catch { /* 文件被占用时下次启动再清理 */ }
                }
            }
            catch { /* 清理失败不影响主流程 */ }
        }
    }
}
