// 文件操作：剪贴板复制/剪切/粘贴、拖放移动、重命名、新建文件夹、删除、跨卷移动。
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
        private void CopyOrCutSelectionToClipboard(bool isCut)
        {
            List<EntryItem> sel = _grid.SelectedItems;
            if (sel.Count == 0) return;
            StringCollection paths = new StringCollection();
            foreach (EntryItem it in sel) paths.Add(it.FullPath);

            DataObject data = new DataObject();
            data.SetFileDropList(paths);
            // Set Preferred DropEffect (2 = Move/Cut, 5 = Copy)
            byte[] moveEffect = new byte[] { isCut ? (byte)2 : (byte)5, 0, 0, 0 };
            MemoryStream ms = new MemoryStream(moveEffect);
            data.SetData("Preferred DropEffect", ms);
            Clipboard.SetDataObject(data, true);
            _clipboardIsCut = isCut;

            Point pt = _grid.PointToClient(Cursor.Position);
            _grid.ShowInstantHud(pt, isCut ? "✂️ 已剪切 " + sel.Count + " 项" : "📋 已复制 " + sel.Count + " 项", "可按 Ctrl+V 粘贴", Color.FromArgb(0, 120, 212));
        }

        private void CopySelectedPathsToClipboard()
        {
            List<EntryItem> sel = _grid.SelectedItems;
            if (sel.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sel.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                sb.Append(sel[i].FullPath);
            }
            Clipboard.SetText(sb.ToString());
            Point pt = _grid.PointToClient(Cursor.Position);
            _grid.ShowInstantHud(pt, "🔗 已复制 " + sel.Count + " 个路径", sel[0].FullPath, Color.FromArgb(0, 120, 212));
        }

        private void PasteFromClipboardToCurrentDirectory()
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return;
                StringCollection files = Clipboard.GetFileDropList();
                if (files == null || files.Count == 0) return;
                List<string> srcList = new List<string>();
                foreach (string f in files) srcList.Add(f);
                OnItemsDroppedIntoFolder(srcList, _currentDirectory, !_clipboardIsCut);
                _clipboardIsCut = false;
            }
            catch { }
        }

        private void OnItemsDroppedIntoFolder(List<string> sourcePaths, string targetFolder, bool isCopy)
        {
            if (string.IsNullOrEmpty(targetFolder)) targetFolder = _currentDirectory;
            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) return;

            int movedCount = 0;
            foreach (string src in sourcePaths)
            {
                if (string.IsNullOrEmpty(src)) continue;
                string fullSrc = Path.GetFullPath(src);
                string parentDir = Path.GetDirectoryName(fullSrc);
                if (!isCopy && string.Equals(parentDir, Path.GetFullPath(targetFolder), StringComparison.OrdinalIgnoreCase))
                    continue; // Dropped onto same directory

                string dest = Path.Combine(targetFolder, Path.GetFileName(fullSrc));
                if (string.Equals(fullSrc, dest, StringComparison.OrdinalIgnoreCase)) continue;
                if (targetFolder.StartsWith(fullSrc.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;

                List<string> uids = GetTagsForPath(fullSrc);
                if (isCopy)
                {
                    if (Directory.Exists(fullSrc)) CopyDirectoryRecursive(fullSrc, dest);
                    else if (File.Exists(fullSrc)) File.Copy(fullSrc, dest, true);
                    if (uids.Count > 0) WriteAdsUids(dest, uids);
                }
                else
                {
                    MoveFileOrDirectoryCrossVolume(fullSrc, dest, uids);
                }
                movedCount++;
            }

            if (movedCount > 0)
            {
                RefreshCurrentDirectory(_grid.ScrollOffset, null);
                Point pt = _grid.PointToClient(Cursor.Position);
                _grid.ShowInstantHud(pt, (isCopy ? "📥 已复制 " : "📂 已移动 ") + movedCount + " 项", "目标: " + Path.GetFileName(targetFolder), Color.FromArgb(40, 180, 100));
            }
        }

        private void RenameFocusedItem()
        {
            EntryItem item = _grid.FocusedItem;
            if (item == null) return;
            using (RenameDialog dlg = new RenameDialog(item.Name))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = dlg.NewName;
                    if (string.IsNullOrEmpty(newName) || newName == item.Name) return;
                    string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath), newName);
                    try
                    {
                        List<string> uids = new List<string>(item.TagUids);
                        if (item.IsFolder) Directory.Move(item.FullPath, newPath);
                        else File.Move(item.FullPath, newPath);

                        if (item.IsFolder)
                        {
                            string oldKey = NormalizeFolderKey(item.FullPath);
                            string newKey = NormalizeFolderKey(newPath);
                            bool sortMoved = false;
                            lock (_folderSortsLock)
                            {
                                FolderSortSetting st;
                                if (!string.IsNullOrEmpty(oldKey) && _folderSorts.TryGetValue(oldKey, out st))
                                {
                                    _folderSorts.Remove(oldKey);
                                    if (!string.IsNullOrEmpty(newKey)) _folderSorts[newKey] = st;
                                    sortMoved = true;
                                }
                            }
                            if (sortMoved) SaveFolderSortsToDisk();
                        }

                        lock (_ledgerLock)
                        {
                            _ledgerMap.Remove(item.FullPath);
                            if (uids.Count > 0) _ledgerMap[newPath] = uids;
                        }
                        SaveLedgerToDisk();
                        RefreshCurrentDirectory(_grid.ScrollOffset, newPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "重命名失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void CreateNewFolderInCurrentDirectory()
        {
            if (string.IsNullOrEmpty(_currentDirectory) || !Directory.Exists(_currentDirectory)) return;
            using (RenameDialog dlg = new RenameDialog("新建文件夹"))
            {
                dlg.Text = "新建文件夹";
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.NewName))
                {
                    string newDir = Path.Combine(_currentDirectory, dlg.NewName);
                    try
                    {
                        Directory.CreateDirectory(newDir);
                        RefreshCurrentDirectory(_grid.ScrollOffset, newDir);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "创建文件夹失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void DeleteSelectedItems()
        {
            List<EntryItem> sel = _grid.SelectedItems;
            if (sel.Count == 0) return;

            string msg = sel.Count == 1
                ? "确定要删除 \"" + sel[0].Name + "\" 吗？\n(提示：如果只是想归档保存，请取消并按 Ctrl + T + 左键)"
                : string.Format("确定要删除选中的 {0} 个项目吗？\n(提示：如果只是想归档保存，请取消并按 Ctrl + T + 左键)", sel.Count);

            if (MessageBox.Show(this, msg, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _grid.RemoveItems(sel);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (EntryItem it in sel)
                {
                    try
                    {
                        lock (_ledgerLock) { _ledgerMap.Remove(it.FullPath); }
                        ClearReadOnlySystemRecursive(it.FullPath);
                        if (Directory.Exists(it.FullPath)) Directory.Delete(it.FullPath, true);
                        else if (File.Exists(it.FullPath)) File.Delete(it.FullPath);
                    }
                    catch { }
                }
                SaveLedgerToDisk();
            });
        }

        private static void ClearReadOnlySystemRecursive(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    uint attr = GetFileAttributesW(path);
                    if (attr != 0xFFFFFFFF && (attr & 0x05u) != 0)
                    {
                        SetFileAttributesW(path, attr & ~0x05u);
                    }
                    foreach (string f in Directory.GetFiles(path))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    foreach (string d in Directory.GetDirectories(path))
                    {
                        ClearReadOnlySystemRecursive(d);
                    }
                }
                else if (File.Exists(path))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                }
            }
            catch { }
        }

        private void MoveFileOrDirectoryCrossVolume(string src, string dest, List<string> uids)
        {
            try
            {
                string destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                lock (_ledgerLock)
                {
                    _ledgerMap.Remove(src);
                    if (uids != null && uids.Count > 0) _ledgerMap[dest] = new List<string>(uids);
                }

                ClearReadOnlySystemRecursive(src);

                if (Directory.Exists(src))
                {
                    try
                    {
                        Directory.Move(src, dest);
                    }
                    catch
                    {
                        CopyDirectoryRecursive(src, dest);
                        ClearReadOnlySystemRecursive(src);
                        Directory.Delete(src, true);
                    }
                    string oldKey = NormalizeFolderKey(src);
                    string newKey = NormalizeFolderKey(dest);
                    bool sortMoved = false;
                    lock (_folderSortsLock)
                    {
                        FolderSortSetting st;
                        if (!string.IsNullOrEmpty(oldKey) && _folderSorts.TryGetValue(oldKey, out st))
                        {
                            _folderSorts.Remove(oldKey);
                            if (!string.IsNullOrEmpty(newKey)) _folderSorts[newKey] = st;
                            sortMoved = true;
                        }
                    }
                    if (sortMoved) SaveFolderSortsToDisk();
                }
                else if (File.Exists(src))
                {
                    try
                    {
                        File.Move(src, dest);
                    }
                    catch
                    {
                        File.Copy(src, dest, true);
                        File.Delete(src);
                    }
                }
                if (uids != null && uids.Count > 0) WriteAdsUids(dest, uids);
                SaveLedgerToDisk();
            }
            catch { }
        }

        private void CopyDirectoryRecursive(string srcDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string f in Directory.GetFiles(srcDir))
            {
                string df = Path.Combine(destDir, Path.GetFileName(f));
                File.Copy(f, df, true);
                List<string> u = ReadAdsUidsFromDisk(f);
                if (u.Count > 0) WriteAdsUids(df, u);
            }
            foreach (string d in Directory.GetDirectories(srcDir))
            {
                CopyDirectoryRecursive(d, Path.Combine(destDir, Path.GetFileName(d)));
            }
        }
    }
}
