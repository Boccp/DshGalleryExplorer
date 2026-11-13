// 左侧快捷路径栏：加载/保存/增删改/排序、标签筛选入口。
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
        private List<SidebarShortcutEntry> GetDefaultSidebarShortcuts()
        {
            return new List<SidebarShortcutEntry>
            {
                new SidebarShortcutEntry { Label = "🎨 画师素材库 (F:\\Openclow)", TargetPath = @"F:\Openclow\画师素材库" },
                new SidebarShortcutEntry { Label = "📦 归档库 (" + _archiveRoot + ")", TargetPath = _archiveRoot },
                new SidebarShortcutEntry { Label = "📁 DeepSeek H 工作区", TargetPath = @"F:\DeepSeek H" },
                new SidebarShortcutEntry { Label = "💻 桌面 (Desktop)", TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new SidebarShortcutEntry { Label = "💿 C:\\ 盘", TargetPath = @"C:\" },
                new SidebarShortcutEntry { Label = "💿 D:\\ 盘", TargetPath = @"D:\" },
                new SidebarShortcutEntry { Label = "💿 E:\\ 盘", TargetPath = @"E:\" },
                new SidebarShortcutEntry { Label = "💿 F:\\ 盘", TargetPath = @"F:\" },
                new SidebarShortcutEntry { Label = "💿 V:\\ 盘", TargetPath = @"V:\" }
            };
        }

        private void LoadShortcutsFromDisk()
        {
            _customShortcuts.Clear();
            try
            {
                if (File.Exists(SHORTCUTS_PATH))
                {
                    string[] lines = File.ReadAllLines(SHORTCUTS_PATH, Encoding.UTF8);
                    foreach (string raw in lines)
                    {
                        string line = (raw ?? "").Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                        int tab = line.IndexOf('\t');
                        if (tab > 0)
                        {
                            string lbl = line.Substring(0, tab).Trim();
                            string path = line.Substring(tab + 1).Trim();
                            if (!string.IsNullOrEmpty(path))
                            {
                                _customShortcuts.Add(new SidebarShortcutEntry { Label = lbl, TargetPath = path });
                            }
                        }
                    }
                }
            }
            catch { }

            if (_customShortcuts.Count == 0)
            {
                _customShortcuts.AddRange(GetDefaultSidebarShortcuts());
                SaveShortcutsToDisk();
            }
        }

        private void SaveShortcutsToDisk()
        {
            try
            {
                string dir = Path.GetDirectoryName(SHORTCUTS_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                List<string> lines = new List<string>();
                foreach (SidebarShortcutEntry entry in _customShortcuts)
                {
                    if (entry == null || entry.IsSeparator || !string.IsNullOrEmpty(entry.TagFilterKey) || string.IsNullOrEmpty(entry.TargetPath)) continue;
                    lines.Add((entry.Label ?? Path.GetFileName(entry.TargetPath)) + "\t" + entry.TargetPath);
                }
                File.WriteAllLines(SHORTCUTS_PATH, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        private void RebuildSidebarItems()
        {
            if (_sidebarList == null) return;
            _suppressSidebarChange = true;
            try
            {
                _sidebarList.BeginUpdate();
                _sidebarList.Items.Clear();
                foreach (SidebarShortcutEntry sc in _customShortcuts)
                {
                    _sidebarList.Items.Add(sc);
                }
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "──────────────", IsSeparator = true });
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "🔴 筛选：全部【红】标记", TagFilterKey = "red" });
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "🟠 筛选：全部【橙】标记", TagFilterKey = "orange" });
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "🟡 筛选：全部【黄】标记", TagFilterKey = "yellow" });
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "🟢 筛选：全部【绿】标记", TagFilterKey = "green" });
                _sidebarList.Items.Add(new SidebarShortcutEntry { Label = "✨ 显示全部已标记项", TagFilterKey = "ALL" });
            }
            finally
            {
                _sidebarList.EndUpdate();
                _suppressSidebarChange = false;
            }
        }

        private void BuildSidebarContextMenu()
        {
            _sidebarContextMenu = new ContextMenuStrip
            {
                Font = new Font("Microsoft YaHei UI", 9.5f),
                BackColor = Color.FromArgb(28, 31, 40),
                ForeColor = Color.White,
                ShowImageMargin = false
            };
            _sidebarContextMenu.Items.Add("➕ 将当前目录添加到快捷路径", null, (s, e) =>
            {
                if (AddShortcutPathInternal(_currentDirectory))
                {
                    SaveShortcutsToDisk();
                    RebuildSidebarItems();
                }
            });
            _sidebarContextMenu.Items.Add("📂 浏览选择文件夹添加到快捷路径...", null, (s, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.SelectedPath = _currentDirectory;
                    fbd.Description = "选择要固定到左侧快捷路径栏的文件夹：";
                    if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
                    {
                        if (AddShortcutPathInternal(fbd.SelectedPath))
                        {
                            SaveShortcutsToDisk();
                            RebuildSidebarItems();
                        }
                    }
                }
            });
            _sidebarContextMenu.Items.Add("📦 自定义归档库路径 (Ctrl+T 目标)...", null, (s, e) => PromptCustomArchiveRoot());
            _sidebarContextMenu.Items.Add(new ToolStripSeparator());
            _sidebarContextMenu.Items.Add("✏️ 编辑此快捷路径 (名称与路径)...", null, (s, e) => EditSelectedSidebarShortcut());
            _sidebarContextMenu.Items.Add("✏️ 仅重命名此快捷路径", null, (s, e) => RenameSelectedSidebarShortcut());
            _sidebarContextMenu.Items.Add("⬆️ 上移此快捷路径", null, (s, e) => MoveSelectedSidebarShortcut(-1));
            _sidebarContextMenu.Items.Add("⬇️ 下移此快捷路径", null, (s, e) => MoveSelectedSidebarShortcut(1));
            _sidebarContextMenu.Items.Add("➖ 移除选中的快捷路径 (Delete)", null, (s, e) => RemoveSelectedSidebarShortcut());
            _sidebarContextMenu.Items.Add(new ToolStripSeparator());
            _sidebarContextMenu.Items.Add("🔄 恢复默认快捷路径列表", null, (s, e) =>
            {
                _customShortcuts.Clear();
                _customShortcuts.AddRange(GetDefaultSidebarShortcuts());
                SaveShortcutsToDisk();
                RebuildSidebarItems();
            });
        }

        private bool AddShortcutPathInternal(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return false;
            string full = Path.GetFullPath(folderPath);
            foreach (SidebarShortcutEntry existing in _customShortcuts)
            {
                if (string.Equals(existing.TargetPath, full, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            string leaf = Path.GetFileName(full.TrimEnd('\\'));
            if (string.IsNullOrEmpty(leaf)) leaf = full;
            string label = "📌 " + leaf;
            _customShortcuts.Add(new SidebarShortcutEntry { Label = label, TargetPath = full });
            if (_grid != null)
            {
                Point pt = _grid.PointToClient(System.Windows.Forms.Cursor.Position);
                _grid.ShowInstantHud(pt, "📌 已添加快捷路径: " + leaf, full, Color.FromArgb(0, 153, 255));
            }
            return true;
        }

        private void AddCurrentOrSelectedFolderToShortcuts()
        {
            string target = _currentDirectory;
            if (_grid != null && _grid.FocusedItem != null && _grid.FocusedItem.IsFolder)
            {
                target = _grid.FocusedItem.FullPath;
            }
            if (AddShortcutPathInternal(target))
            {
                SaveShortcutsToDisk();
                RebuildSidebarItems();
            }
            else if (_grid != null)
            {
                Point pt = _grid.PointToClient(System.Windows.Forms.Cursor.Position);
                _grid.ShowInstantHud(pt, "ℹ️ 该路径已在左侧快捷列表中", target ?? "", Color.FromArgb(100, 140, 200));
            }
        }

        private void RemoveSelectedSidebarShortcut()
        {
            if (_sidebarList == null) return;
            SidebarShortcutEntry sel = _sidebarList.SelectedItem as SidebarShortcutEntry;
            if (sel == null || sel.IsSeparator || !string.IsNullOrEmpty(sel.TagFilterKey))
            {
                if (_grid != null)
                {
                    Point pt = _grid.PointToClient(System.Windows.Forms.Cursor.Position);
                    _grid.ShowInstantHud(pt, "ℹ️ 请先在左侧点击选中一条快捷路径再移除", "标签筛选固定项不可移除", Color.FromArgb(200, 120, 40));
                }
                return;
            }
            _customShortcuts.Remove(sel);
            SaveShortcutsToDisk();
            RebuildSidebarItems();
            if (_grid != null)
            {
                Point pt = _grid.PointToClient(System.Windows.Forms.Cursor.Position);
                _grid.ShowInstantHud(pt, "➖ 已移除快捷路径", sel.Label, Color.FromArgb(220, 80, 60));
            }
        }

        private void EditSelectedSidebarShortcut()
        {
            if (_sidebarList == null) return;
            SidebarShortcutEntry sel = _sidebarList.SelectedItem as SidebarShortcutEntry;
            if (sel == null || sel.IsSeparator || !string.IsNullOrEmpty(sel.TagFilterKey)) return;
            bool isArchiveShortcut = string.Equals(sel.TargetPath, _archiveRoot, StringComparison.OrdinalIgnoreCase) ||
                                     (sel.Label != null && sel.Label.IndexOf("归档库", StringComparison.OrdinalIgnoreCase) >= 0);
            using (PathEditorDialog dlg = new PathEditorDialog("编辑快捷路径", sel.Label, sel.TargetPath, true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.EditedPath))
                {
                    sel.Label = !string.IsNullOrEmpty(dlg.EditedLabel) ? dlg.EditedLabel : ("📌 " + Path.GetFileName(dlg.EditedPath.TrimEnd('\\', '/')));
                    sel.TargetPath = dlg.EditedPath.Trim();
                    if (isArchiveShortcut)
                    {
                        _archiveRoot = sel.TargetPath;
                        SaveArchiveRootToDisk();
                        UpdateArchiveRootUI();
                    }
                    SaveShortcutsToDisk();
                    RebuildSidebarItems();
                }
            }
        }

        private void RenameSelectedSidebarShortcut()
        {
            if (_sidebarList == null) return;
            SidebarShortcutEntry sel = _sidebarList.SelectedItem as SidebarShortcutEntry;
            if (sel == null || sel.IsSeparator || !string.IsNullOrEmpty(sel.TagFilterKey)) return;
            using (RenameDialog dlg = new RenameDialog(sel.Label))
            {
                dlg.Text = "重命名快捷路径";
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.NewName))
                {
                    sel.Label = dlg.NewName;
                    SaveShortcutsToDisk();
                    RebuildSidebarItems();
                }
            }
        }

        private void MoveSelectedSidebarShortcut(int delta)
        {
            if (_sidebarList == null) return;
            SidebarShortcutEntry sel = _sidebarList.SelectedItem as SidebarShortcutEntry;
            if (sel == null || sel.IsSeparator || !string.IsNullOrEmpty(sel.TagFilterKey)) return;
            int idx = _customShortcuts.IndexOf(sel);
            if (idx < 0) return;
            int nextIdx = idx + delta;
            if (nextIdx < 0 || nextIdx >= _customShortcuts.Count) return;
            _customShortcuts.RemoveAt(idx);
            _customShortcuts.Insert(nextIdx, sel);
            SaveShortcutsToDisk();
            RebuildSidebarItems();
            _sidebarList.SelectedIndex = nextIdx;
        }

        private bool _suppressSidebarChange = false;

        private void OnSidebarSelectionChanged(object sender, EventArgs e)
        {
            if (_suppressSidebarChange) return;
            SidebarShortcutEntry entry = _sidebarList.SelectedItem as SidebarShortcutEntry;
            if (entry == null || entry.IsSeparator) return;

            if (!string.IsNullOrEmpty(entry.TagFilterKey))
            {
                if (entry.TagFilterKey == "ALL")
                {
                    ShowTaggedItemsView(null);
                }
                else
                {
                    foreach (TagSpec spec in ORDERED_TAGS)
                    {
                        if (string.Equals(spec.Key, entry.TagFilterKey, StringComparison.OrdinalIgnoreCase))
                        {
                            ShowTaggedItemsView(spec);
                            break;
                        }
                    }
                }
                return;
            }

            if (!string.IsNullOrEmpty(entry.TargetPath))
            {
                if (string.Equals(entry.TargetPath, _archiveRoot, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(_archiveRoot))
                {
                    try { Directory.CreateDirectory(_archiveRoot); } catch { }
                }
                if (Directory.Exists(entry.TargetPath))
                {
                    NavigateTo(entry.TargetPath, true);
                }
                else if (string.Equals(entry.TargetPath, _archiveRoot, StringComparison.OrdinalIgnoreCase) ||
                         (entry.Label != null && entry.Label.IndexOf("归档库", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (MessageBox.Show(this, "当前归档库路径 \"" + entry.TargetPath + "\" 不存在，是否立即自定义新的归档库路径？", "归档库路径配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        PromptCustomArchiveRoot();
                    }
                }
            }
        }
    }
}
