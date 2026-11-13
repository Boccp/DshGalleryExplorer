using System;
using System.Collections.Generic;
using System.IO;

namespace DshGalleryExplorer
{
    /// <summary>
    /// 路径与外部工具集中解析。
    /// 默认缓存根目录为 %USERPROFILE%\.dsh\gallery_cache，支持通过环境变量 DSH_GALLERY_CACHE_DIR、
    /// 配置文件 %USERPROFILE%\.dsh\gallery_cache\config.ini 或 --bench 参数覆盖。
    /// </summary>
    internal static partial class AppPaths
    {
        private static string _cacheRoot;
        private static Dictionary<string, string> _config;
        private static readonly object _cfgLock = new object();

        public static string CacheRoot
        {
            get
            {
                if (_cacheRoot == null) _cacheRoot = ResolveDefaultCacheRoot();
                return _cacheRoot;
            }
        }

        public static void OverrideCacheRoot(string dir)
        {
            _cacheRoot = dir;
            lock (_cfgLock) { _config = null; }
        }

        private static string ResolveDefaultCacheRoot()
        {
            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache");
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_GALLERY_CACHE_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

                string ini = Path.Combine(defaultDir, "config.ini");
                if (File.Exists(ini))
                {
                    foreach (string raw in File.ReadAllLines(ini))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), "CacheDir", StringComparison.OrdinalIgnoreCase))
                        {
                            string val = line.Substring(eq + 1).Trim();
                            if (!string.IsNullOrEmpty(val)) return val;
                        }
                    }
                }
            }
            catch { /* 读取早期配置失败时使用默认缓存目录 */ }
            return defaultDir;
        }

        public static string GetConfig(string key)
        {
            lock (_cfgLock)
            {
                if (_config == null)
                {
                    _config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        string ini = Path.Combine(CacheRoot, "config.ini");
                        if (File.Exists(ini))
                        {
                            foreach (string raw in File.ReadAllLines(ini))
                            {
                                string line = (raw ?? "").Trim();
                                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                                int eq = line.IndexOf('=');
                                if (eq > 0)
                                {
                                    _config[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.Debug(ex, "读取 config.ini"); }
                }
                string v;
                return _config.TryGetValue(key, out v) ? v : null;
            }
        }

        public static string LogDir { get { return Path.Combine(CacheRoot, "logs"); } }
        public static string ThumbnailCacheDir { get { return Path.Combine(CacheRoot, "thumbnails"); } }
        public static string VideoReelCacheDir { get { return Path.Combine(CacheRoot, "video_reels"); } }
        public static string LedgerPath { get { return Path.Combine(CacheRoot, "_dsh_tags_ledger.json"); } }
        public static string TagIndexDir { get { return Path.Combine(CacheRoot, "tags_index"); } }
        public static string FolderSortsPath { get { return Path.Combine(CacheRoot, "_dsh_folder_sorts.json"); } }
        public static string SidebarShortcutsPath { get { return Path.Combine(CacheRoot, "_dsh_sidebar_shortcuts.json"); } }
        public static string ArchiveRootConfigPath { get { return Path.Combine(CacheRoot, "_dsh_archive_root.txt"); } }

        public static string ResolveDefaultArchiveRoot()
        {
            string cfg = GetConfig("DefaultArchiveDir");
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            if (Directory.Exists(@"V:\")) return @"V:\归档";
            if (Directory.Exists(@"D:\")) return @"D:\归档";
            return @"C:\归档";
        }

        public static string ResolveDefaultStartDirectory(string initialPath)
        {
            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)) return initialPath;
            string cfg = GetConfig("DefaultStartDir");
            if (!string.IsNullOrWhiteSpace(cfg) && Directory.Exists(cfg)) return cfg;
            if (Directory.Exists(@"F:\Openclow\画师素材库")) return @"F:\Openclow\画师素材库";
            if (Directory.Exists(@"F:\")) return @"F:\";
            if (Directory.Exists(@"D:\")) return @"D:\";
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        public static string ResolveScreenboxLitePath()
        {
            string cfg = GetConfig("ScreenboxPath");
            if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg)) return cfg;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                Path.Combine(localAppData, @"Programs\ScreenboxLite\ScreenboxLite.exe"),
                @"F:\QQ\ScreenboxLite_Release\ScreenboxLite.exe"
            };
            foreach (string p in candidates)
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            return null;
        }

        public static string ResolveFfmpegPath()
        {
            string cfg = GetConfig("FfmpegPath");
            if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg)) return cfg;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string[] candidates = new[]
            {
                @"D:\_tools\ffmpeg.exe",
                Path.Combine(localAppData, @"hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe"),
                @"D:\JianyingPro\11.5.0.14471\ffmpeg.exe",
                @"D:\JianyingPro\11.4.2.14459\ffmpeg.exe",
                Path.Combine(localAppData, @"JianyingPro\Apps\10.7.0.14095\ffmpeg.exe"),
                Path.Combine(desktop, @"处理图片和视频\ffmpeg.exe"),
                @"F:\剪映\9.9.0.13784\ffmpeg.exe"
            };
            foreach (string p in candidates)
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            return null;
        }
    }
}
