using System;
using System.IO;

namespace DshGalleryExplorer
{
    /// <summary>
    /// 路径集中管理（阶段 A：缓存根目录及其派生目录）。
    /// 缓存根目录默认 %USERPROFILE%\.dsh\gallery_cache，可被环境变量 DSH_GALLERY_CACHE_DIR
    /// 或 --bench 模式覆盖（基准测试使用独立缓存目录，绝不污染用户真实缓存与账本）。
    /// </summary>
    internal static partial class AppPaths
    {
        private static string _cacheRoot;

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
        }

        private static string ResolveDefaultCacheRoot()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_GALLERY_CACHE_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            }
            catch { /* 读取环境变量失败时使用默认路径 */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache");
        }

        public static string LogDir { get { return Path.Combine(CacheRoot, "logs"); } }
        public static string ThumbnailCacheDir { get { return Path.Combine(CacheRoot, "thumbnails"); } }
        public static string VideoReelCacheDir { get { return Path.Combine(CacheRoot, "video_reels"); } }
        public static string LedgerPath { get { return Path.Combine(CacheRoot, "_dsh_tags_ledger.json"); } }
        public static string TagIndexDir { get { return Path.Combine(CacheRoot, "tags_index"); } }
        public static string FolderSortsPath { get { return Path.Combine(CacheRoot, "_dsh_folder_sorts.json"); } }
        public static string SidebarShortcutsPath { get { return Path.Combine(CacheRoot, "_dsh_sidebar_shortcuts.json"); } }
        public static string ArchiveRootConfigPath { get { return Path.Combine(CacheRoot, "_dsh_archive_root.txt"); } }
    }
}
