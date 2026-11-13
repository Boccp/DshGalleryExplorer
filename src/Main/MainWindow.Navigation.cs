// 导航：进入/后退/前进/上级、位置记忆、目录枚举刷新、文件夹封面查找。
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
    internal sealed partial class MainWindow
    {
        private NavHistoryEntry CaptureCurrentViewState()
        {
            return new NavHistoryEntry
            {
                DirectoryPath = _currentDirectory,
                ScrollOffset = _grid != null ? _grid.ScrollOffset : 0,
                SelectedItemPath = (_grid != null && _grid.FocusedItem != null) ? _grid.FocusedItem.FullPath : null
            };
        }

        public void NavigateTo(string folderPath, bool recordHistory, int? explicitScrollOffset = null, string explicitSelectedPath = null)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;
            string full = Path.GetFullPath(folderPath);

            if (_isInitialized && !string.IsNullOrEmpty(_currentDirectory))
            {
                NavHistoryEntry snap = CaptureCurrentViewState();
                _dirViewMemory[_currentDirectory] = snap;
                if (recordHistory && !string.Equals(_currentDirectory, full, StringComparison.OrdinalIgnoreCase))
                {
                    _backStack.Push(snap);
                    _forwardStack.Clear();
                }
            }

            _currentDirectory = full;
            _activeTagFilter = null;
            _addressBox.Text = _currentDirectory;
            _isInitialized = true;

            RestoreOrDetermineSortForDirectory(_currentDirectory);

            int restoreScroll = 0;
            string restoreSel = explicitSelectedPath;

            if (explicitScrollOffset.HasValue)
            {
                restoreScroll = explicitScrollOffset.Value;
            }
            else
            {
                NavHistoryEntry mem;
                if (_dirViewMemory.TryGetValue(full, out mem) && mem != null)
                {
                    restoreScroll = mem.ScrollOffset;
                    if (string.IsNullOrEmpty(restoreSel)) restoreSel = mem.SelectedItemPath;
                }
            }

            RefreshCurrentDirectory(restoreScroll, restoreSel);
        }

        private void NavigateBack()
        {
            while (_backStack.Count > 0)
            {
                NavHistoryEntry prev = _backStack.Pop();
                if (prev != null && Directory.Exists(prev.DirectoryPath))
                {
                    NavHistoryEntry curSnap = CaptureCurrentViewState();
                    _dirViewMemory[_currentDirectory] = curSnap;
                    _forwardStack.Push(curSnap);
                    string sel = prev.SelectedItemPath;
                    if (string.IsNullOrEmpty(sel) || _currentDirectory.StartsWith(prev.DirectoryPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        sel = _currentDirectory;
                    }
                    NavigateTo(prev.DirectoryPath, false, prev.ScrollOffset, sel);
                    break;
                }
            }
        }

        private void NavigateForward()
        {
            while (_forwardStack.Count > 0)
            {
                NavHistoryEntry next = _forwardStack.Pop();
                if (next != null && Directory.Exists(next.DirectoryPath))
                {
                    NavHistoryEntry curSnap = CaptureCurrentViewState();
                    _dirViewMemory[_currentDirectory] = curSnap;
                    _backStack.Push(curSnap);
                    NavigateTo(next.DirectoryPath, false, next.ScrollOffset, next.SelectedItemPath);
                    break;
                }
            }
        }

        private void NavigateUp()
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(_currentDirectory);
                if (parent != null)
                {
                    string childWeJustLeft = _currentDirectory;
                    NavHistoryEntry mem;
                    int? savedParentScroll = null;
                    if (_dirViewMemory.TryGetValue(parent.FullName, out mem) && mem != null)
                    {
                        savedParentScroll = mem.ScrollOffset;
                    }
                    NavigateTo(parent.FullName, true, savedParentScroll, childWeJustLeft);
                }
            }
            catch { }
        }

        public void RefreshCurrentDirectory(int restoreScrollOffset = 0, string restoreSelectedPath = null)
        {
            HideVideoHoverPreview();
            if (!string.IsNullOrEmpty(_activeTagFilter))
            {
                TagSpec spec = null;
                foreach (var t in ORDERED_TAGS) if (t.Name == _activeTagFilter) spec = t;
                ShowTaggedItemsView(spec);
                return;
            }

            List<EntryItem> list = new List<EntryItem>();
            Perf.Scope perfScope = Perf.Measure("dir.enumerateSort");
            try
            {
                FillDirectoryEntries(list, _currentDirectory, _archiveRoot, _sortField, _sortDescending, _sortFoldersFirst, GetTagsForPath);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "读取目录出错: " + ex.Message;
            }
            perfScope.Dispose();

            _grid.SetItems(list, restoreScrollOffset, restoreSelectedPath);
        }

        /// <summary>
        /// 枚举目录 → 过滤隐藏/系统/缓存文件 → 匹配账本标签 → 排序，结果追加到 list。
        /// 静态纯函数，供 RefreshCurrentDirectory 与 --bench 共用；异常向调用方抛出（list 保留已填充部分）。
        /// </summary>
        internal static void FillDirectoryEntries(List<EntryItem> list, string directory, string archiveRoot,
            SortField sortField, bool sortDescending, bool sortFoldersFirst, Func<string, List<string>> tagLookup)
        {
            DirectoryInfo di = new DirectoryInfo(directory);
            bool needStat = (sortField == SortField.CreatedTime || sortField == SortField.ModifiedTime || sortField == SortField.Size);
            FileSystemInfo[] entries = di.GetFileSystemInfos();

            foreach (FileSystemInfo fsi in entries)
            {
                string name = fsi.Name;
                if (name.StartsWith(".") || name == "_标签索引") continue;
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;

                if (isDir)
                {
                    if ((fsi.Attributes & FileAttributes.Hidden) != 0 && !string.Equals(fsi.FullName, archiveRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new EntryItem
                    {
                        FullPath = fsi.FullName,
                        Name = name,
                        IsFolder = true,
                        CreatedAt = needStat ? fsi.CreationTimeUtc : DateTime.MinValue,
                        ModifiedAt = needStat ? fsi.LastWriteTimeUtc : DateTime.MinValue,
                        TagUids = tagLookup(fsi.FullName),
                        CoverImagePath = null
                    });
                }
                else
                {
                    if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("_dsh_") || name.StartsWith("_library_")) continue;
                    FileInfo fi = fsi as FileInfo;
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    bool isMedia = Array.IndexOf(IMAGE_EXTS, ext) >= 0 || VirtualGridCanvas.IsVideoExtension(ext);
                    list.Add(new EntryItem
                    {
                        FullPath = fsi.FullName,
                        Name = name,
                        IsFolder = false,
                        SizeBytes = (needStat && fi != null) ? fi.Length : 0L,
                        CreatedAt = needStat ? fsi.CreationTimeUtc : DateTime.MinValue,
                        ModifiedAt = needStat ? fsi.LastWriteTimeUtc : DateTime.MinValue,
                        TagUids = tagLookup(fsi.FullName),
                        CoverImagePath = isMedia ? fsi.FullName : null
                    });
                }
            }
            using (Perf.Measure("dir.sort"))
            {
                list.Sort((a, b) => CompareEntries(a, b, sortField, sortDescending, sortFoldersFirst));
            }
        }

        public static string FindFirstImageInFolder(string folderPath, int depth = 0)
        {
            if (depth > 2) return null;
            try
            {
                string leafName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
                if (string.Equals(leafName, "Windows", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "Program Files", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "ProgramData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "AppData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "WinSxS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leafName, "SystemApps", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string[] files = Directory.GetFiles(folderPath);
                Array.Sort(files, (a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
                string firstVideoFallback = null;
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith(".")) continue;
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (Array.IndexOf(IMAGE_EXTS, ext) >= 0) return f;
                    if (firstVideoFallback == null && VirtualGridCanvas.IsVideoExtension(ext)) firstVideoFallback = f;
                }
                if (firstVideoFallback != null) return firstVideoFallback;
                string[] subdirs = Directory.GetDirectories(folderPath);
                Array.Sort(subdirs, (a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
                foreach (string sub in subdirs)
                {
                    if (Path.GetFileName(sub).StartsWith(".")) continue;
                    string subImg = FindFirstImageInFolder(sub, depth + 1);
                    if (subImg != null) return subImg;
                }
            }
            catch { }
            return null;
        }
    }
}
