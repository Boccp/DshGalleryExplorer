// 归档：归档库路径配置与 Ctrl+T 一键归档 / 原路复原。
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
        private void LoadArchiveRootFromDisk()
        {
            try
            {
                if (File.Exists(ARCHIVE_CONFIG_PATH))
                {
                    string saved = (File.ReadAllText(ARCHIVE_CONFIG_PATH, Encoding.UTF8) ?? "").Trim();
                    if (!string.IsNullOrEmpty(saved))
                    {
                        _archiveRoot = saved;
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "读取归档库配置: " + ARCHIVE_CONFIG_PATH); }
        }

        private void SaveArchiveRootToDisk()
        {
            try
            {
                string dir = Path.GetDirectoryName(ARCHIVE_CONFIG_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ARCHIVE_CONFIG_PATH, _archiveRoot, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn(ex, "保存归档库配置失败: " + ARCHIVE_CONFIG_PATH); }
        }

        private void UpdateArchiveRootUI()
        {
            if (_btnArchiveConfig != null) _btnArchiveConfig.Text = "📦 归档库: " + _archiveRoot;
            if (_legendLabel != null)
            {
                _legendLabel.Text = "Ctrl+Q/W/E/R [红/橙/黄/绿]  |  Ctrl+T [归档 " + _archiveRoot + "]  |  长按左键 [框选/拖拽]  |  右键 [菜单/排序]";
            }
            if (_miArchiveToggle != null)
            {
                _miArchiveToggle.Text = "📦 移入 / 复原 " + _archiveRoot + " (Ctrl+T)";
            }
        }

        private void PromptCustomArchiveRoot()
        {
            using (PathEditorDialog dlg = new PathEditorDialog("自定义归档库路径 (Ctrl+T 归档目标目录)", "", _archiveRoot, false))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.EditedPath))
                {
                    string oldArchive = _archiveRoot;
                    string newArchive = dlg.EditedPath.Trim();
                    try { newArchive = Path.GetFullPath(newArchive).TrimEnd('\\', '/'); } catch { }
                    if (newArchive.Length == 2 && newArchive[1] == ':') newArchive += "\\";

                    _archiveRoot = newArchive;
                    SaveArchiveRootToDisk();
                    try
                    {
                        if (!Directory.Exists(_archiveRoot)) Directory.CreateDirectory(_archiveRoot);
                    }
                    catch { }

                    // 同步更新左侧快捷栏中的归档库项
                    bool updatedShortcut = false;
                    foreach (SidebarShortcutEntry sc in _customShortcuts)
                    {
                        if (sc != null && !sc.IsSeparator && string.IsNullOrEmpty(sc.TagFilterKey))
                        {
                            if (string.Equals(sc.TargetPath, oldArchive, StringComparison.OrdinalIgnoreCase) ||
                                (sc.Label != null && sc.Label.IndexOf("归档库", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                sc.TargetPath = _archiveRoot;
                                sc.Label = "📦 归档库 (" + _archiveRoot + ")";
                                updatedShortcut = true;
                            }
                        }
                    }
                    if (!updatedShortcut)
                    {
                        _customShortcuts.Insert(Math.Min(1, _customShortcuts.Count), new SidebarShortcutEntry
                        {
                            Label = "📦 归档库 (" + _archiveRoot + ")",
                            TargetPath = _archiveRoot
                        });
                    }
                    SaveShortcutsToDisk();
                    RebuildSidebarItems();
                    UpdateArchiveRootUI();

                    if (_grid != null)
                    {
                        Point pt = _grid.PointToClient(Cursor.Position);
                        _grid.ShowInstantHud(pt, "📦 归档库路径已更新", _archiveRoot, Color.FromArgb(0, 153, 188));
                    }
                }
            }
        }

        private void ExecuteBatchArchiveToggle(List<EntryItem> items, Point cursorClient)
        {
            try
            {
                if (!Directory.Exists(_archiveRoot)) Directory.CreateDirectory(_archiveRoot);
            }
            catch
            {
                PromptCustomArchiveRoot();
                if (!Directory.Exists(_archiveRoot)) return;
            }

            string normArchivePrefix = Path.GetFullPath(_archiveRoot).TrimEnd('\\') + "\\";
            List<EntryItem> removedItems = new List<EntryItem>();
            string lastDest = "";
            bool isRestore = false;

            foreach (EntryItem item in items)
            {
                string fullP = Path.GetFullPath(item.FullPath);
                List<string> uidsCopy = new List<string>(item.TagUids);

                if (fullP.StartsWith(normArchivePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = fullP.Substring(normArchivePrefix.Length);
                    if (rel.Length >= 3 && rel[1] == '盘' && rel[2] == '\\')
                    {
                        char driveLetter = char.ToUpperInvariant(rel[0]);
                        string origPath = driveLetter + ":\\" + rel.Substring(3);
                        removedItems.Add(item);
                        lastDest = origPath;
                        isRestore = true;
                        ThreadPool.QueueUserWorkItem(_ => MoveFileOrDirectoryCrossVolume(fullP, origPath, uidsCopy));
                    }
                }
                else if (fullP.Length >= 3 && fullP[1] == ':' && fullP[2] == '\\')
                {
                    char driveLetter = char.ToUpperInvariant(fullP[0]);
                    string rel = fullP.Substring(3);
                    string destPath = Path.Combine(_archiveRoot, driveLetter + "盘", rel);
                    removedItems.Add(item);
                    lastDest = destPath;
                    isRestore = false;
                    ThreadPool.QueueUserWorkItem(_ => MoveFileOrDirectoryCrossVolume(fullP, destPath, uidsCopy));
                }
            }

            if (removedItems.Count > 0)
            {
                _grid.RemoveItems(removedItems);
                string title = isRestore
                    ? string.Format("↩️ 已复原 {0} 项至原位置", removedItems.Count)
                    : string.Format("📦 已移入 {0} ({1} 项)", _archiveRoot, removedItems.Count);
                _grid.ShowInstantHud(cursorClient, title, lastDest, Color.FromArgb(0, 153, 188));
            }
        }
    }
}
