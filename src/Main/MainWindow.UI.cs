// 主窗口界面构建：顶栏、底栏、侧边栏控件、网格、右键菜单、状态栏文字。
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
        private void BuildUI()
        {
            using (Graphics g = CreateGraphics()) { _dpiScale = Math.Max(1.0f, g.DpiX / 96.0f); }

            // Top Navigation & Shortcut Bar
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(58),
                BackColor = Color.FromArgb(24, 27, 35),
                Padding = new Padding(S(10), S(8), S(10), S(8))
            };

            Button btnBack = CreateNavButton("← 后退", S(74), S(38), (sender, e) => NavigateBack());
            btnBack.Location = new Point(S(10), S(10));

            Button btnForward = CreateNavButton("→ 前进", S(74), S(38), (sender, e) => NavigateForward());
            btnForward.Location = new Point(S(90), S(10));

            Button btnUp = CreateNavButton("↑ 上级", S(74), S(38), (sender, e) => NavigateUp());
            btnUp.Location = new Point(S(170), S(10));

            Button btnRefresh = CreateNavButton("⟳ 刷新", S(74), S(38), (sender, e) => RefreshCurrentDirectory(_grid.ScrollOffset, _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null));
            btnRefresh.Location = new Point(S(250), S(10));

            _addressBox = new TextBox
            {
                Location = new Point(S(332), S(13)),
                Width = S(390),
                Height = S(34),
                Font = new Font("Microsoft YaHei UI", 10.5f),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _addressBox.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string target = _addressBox.Text.Trim();
                    if (Directory.Exists(target)) NavigateTo(target, true);
                }
            };

            _btnSortMode = CreateNavButton("⇅ 排序: 名称", S(112), S(38), (sender, e) => CycleSortField());
            _btnSortMode.Location = new Point(S(730), S(10));

            _btnSortDir = CreateNavButton("↑ 升序", S(74), S(38), (sender, e) => ToggleSortDirection());
            _btnSortDir.Location = new Point(S(848), S(10));

            _btnArchiveConfig = CreateNavButton("📦 归档库: " + _archiveRoot, S(176), S(38), (sender, e) => PromptCustomArchiveRoot());
            _btnArchiveConfig.Location = new Point(S(928), S(10));

            _legendLabel = new Label
            {
                Text = "Ctrl+Q/W/E/R [红/橙/黄/绿]  |  Ctrl+T [归档 " + _archiveRoot + "]  |  长按左键 [框选/拖拽]  |  右键 [菜单/排序]",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 205, 70),
                Location = new Point(S(1112), S(18))
            };

            topBar.Controls.AddRange(new Control[] { btnBack, btnForward, btnUp, btnRefresh, _addressBox, _btnSortMode, _btnSortDir, _btnArchiveConfig, _legendLabel });

            // Bottom Status & Zoom Bar
            Panel bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(46),
                BackColor = Color.FromArgb(24, 27, 35)
            };

            _statusLabel = new Label
            {
                Location = new Point(S(14), S(12)),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.8f),
                ForeColor = Color.FromArgb(190, 198, 212),
                Text = "就绪"
            };

            int defaultTile = S(260);
            Button btnZoomOut = CreateNavButton("－ 缩小", S(74), S(32), (sender, e) => { _grid.TileSize -= 32; });
            btnZoomOut.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnZoomOut.Location = new Point(ClientSize.Width - S(430), S(7));

            _zoomSlider = new TrackBar
            {
                Minimum = 96,
                Maximum = 768,
                Value = Math.Min(768, defaultTile),
                TickStyle = TickStyle.None,
                AutoSize = false,
                Width = S(220),
                Height = S(34),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(ClientSize.Width - S(350), S(6))
            };

            Button btnZoomIn = CreateNavButton("＋ 放大", S(74), S(32), (sender, e) => { _grid.TileSize += 32; });
            btnZoomIn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnZoomIn.Location = new Point(ClientSize.Width - S(124), S(7));

            _zoomLabel = new Label
            {
                Text = "网格尺寸: " + defaultTile + "px",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 228, 242),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(ClientSize.Width - S(580), S(12))
            };
            _zoomSlider.ValueChanged += (sender, e) =>
            {
                _grid.TileSize = _zoomSlider.Value;
                _zoomLabel.Text = "网格尺寸: " + _grid.TileSize + "px";
            };

            bottomBar.Controls.Add(_statusLabel);
            bottomBar.Controls.Add(_zoomLabel);
            bottomBar.Controls.Add(btnZoomOut);
            bottomBar.Controls.Add(_zoomSlider);
            bottomBar.Controls.Add(btnZoomIn);

            // Left Quick-Access & Tag Filter Sidebar (with Add / Remove shortcut buttons & drag-drop)
            Panel leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = S(248),
                BackColor = Color.FromArgb(22, 24, 32),
                Padding = new Padding(S(8))
            };

            Panel sidebarHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(34),
                BackColor = Color.FromArgb(22, 24, 32)
            };

            Label sidebarTitle = new Label
            {
                Text = "📌 快捷路径",
                Location = new Point(S(2), S(6)),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(180, 190, 210)
            };

            Button btnAddShortcut = CreateNavButton("＋添加", S(52), S(26), (sender, e) => AddCurrentOrSelectedFolderToShortcuts());
            btnAddShortcut.Location = new Point(S(86), S(3));
            btnAddShortcut.Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);

            Button btnRemoveShortcut = CreateNavButton("－移除", S(52), S(26), (sender, e) => RemoveSelectedSidebarShortcut());
            btnRemoveShortcut.Location = new Point(S(141), S(3));
            btnRemoveShortcut.Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);

            Button btnGearShortcut = CreateNavButton("⚙", S(34), S(26), (sender, e) =>
            {
                if (_sidebarContextMenu != null)
                {
                    _sidebarContextMenu.Show(sidebarHeader.PointToScreen(new Point(S(196), S(29))));
                }
            });
            btnGearShortcut.Location = new Point(S(196), S(3));
            btnGearShortcut.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold);

            sidebarHeader.Controls.Add(sidebarTitle);
            sidebarHeader.Controls.Add(btnAddShortcut);
            sidebarHeader.Controls.Add(btnRemoveShortcut);
            sidebarHeader.Controls.Add(btnGearShortcut);

            _sidebarList = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 24, 32),
                ForeColor = Color.FromArgb(225, 230, 240),
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 10f),
                ItemHeight = S(34),
                AllowDrop = true
            };
            _sidebarList.SelectedIndexChanged += OnSidebarSelectionChanged;
            _sidebarList.MouseDown += (sender, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    int hit = _sidebarList.IndexFromPoint(e.Location);
                    if (hit >= 0 && hit < _sidebarList.Items.Count)
                    {
                        _sidebarList.SelectedIndex = hit;
                    }
                    if (_sidebarContextMenu != null)
                    {
                        _sidebarContextMenu.Show(_sidebarList.PointToScreen(e.Location));
                    }
                }
            };
            _sidebarList.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    RemoveSelectedSidebarShortcut();
                    e.Handled = true;
                }
            };
            _sidebarList.DragEnter += (sender, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Link;
                else e.Effect = DragDropEffects.None;
            };
            _sidebarList.DragDrop += (sender, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped == null) return;
                bool added = false;
                foreach (string p in dropped)
                {
                    string folder = Directory.Exists(p) ? p : (File.Exists(p) ? Path.GetDirectoryName(p) : null);
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    {
                        if (AddShortcutPathInternal(folder)) added = true;
                    }
                }
                if (added)
                {
                    SaveShortcutsToDisk();
                    RebuildSidebarItems();
                }
            };

            BuildSidebarContextMenu();
            RebuildSidebarItems();

            leftPanel.Controls.Add(_sidebarList);
            leftPanel.Controls.Add(sidebarHeader);

            _grid = new VirtualGridCanvas
            {
                Dock = DockStyle.Fill,
                TileSize = defaultTile
            };
            _grid.ItemOpened += OnItemOpened;
            _grid.ItemHovered += OnItemHovered;
            _grid.SelectionChanged += OnSelectionChanged;
            _grid.HotkeyActionTriggered += OnHotkeyActionTriggered;
            _grid.NavigateBackRequested += NavigateBack;
            _grid.NavigateForwardRequested += NavigateForward;
            _grid.ContextMenuRequested += OnContextMenuRequested;
            _grid.ItemsDroppedIntoFolder += OnItemsDroppedIntoFolder;
            _grid.TileSizeChanged += (newSize) =>
            {
                _zoomSlider.Value = Math.Max(_zoomSlider.Minimum, Math.Min(_zoomSlider.Maximum, newSize));
                _zoomLabel.Text = "网格尺寸: " + newSize + "px";
            };

            _videoHoverDebounceTimer = new System.Windows.Forms.Timer { Interval = 35 };
            _videoHoverDebounceTimer.Tick += (s, e) =>
            {
                _videoHoverDebounceTimer.Stop();
                if (string.IsNullOrEmpty(_pendingHoverVideoPath) || !File.Exists(_pendingHoverVideoPath))
                {
                    HideVideoHoverPreview();
                    return;
                }
                if (_videoHoverPopup == null)
                {
                    _videoHoverPopup = new VideoHoverPreviewWindow();
                }
                Bitmap coverBmp = _grid != null ? _grid.TryGetCachedThumbnail(_pendingHoverVideoPath) : null;
                _videoHoverPopup.ShowOrUpdatePreview(_pendingHoverVideoPath, _lastHoverScreenPt, coverBmp);
            };
            Shown += (s, e) =>
            {
                try
                {
                    if (_videoHoverPopup == null)
                    {
                        _videoHoverPopup = new VideoHoverPreviewWindow();
                    }
                }
                catch { }
            };
            Deactivate += (s, e) => HideVideoHoverPreview();
            FormClosing += (s, e) =>
            {
                try
                {
                    HideVideoHoverPreview();
                    if (_videoHoverPopup != null) { _videoHoverPopup.Close(); _videoHoverPopup = null; }
                }
                catch { }
            };

            Controls.Add(_grid);
            Controls.Add(leftPanel);
            Controls.Add(topBar);
            Controls.Add(bottomBar);
        }

        private void BuildContextMenu()
        {
            _contextMenu = new ContextMenuStrip
            {
                Font = new Font("Microsoft YaHei UI", 9.8f),
                BackColor = Color.FromArgb(28, 31, 40),
                ForeColor = Color.White,
                ShowImageMargin = false
            };

            _contextMenu.Items.Add("📂 打开 / 查看 (Enter)", null, (s, e) =>
            {
                if (_grid.FocusedItem != null) OnItemOpened(_grid.FocusedItem);
            });
            _contextMenu.Items.Add("📌 固定到左侧快捷路径", null, (s, e) =>
            {
                AddCurrentOrSelectedFolderToShortcuts();
            });
            _contextMenu.Items.Add("🧭 在 Windows 资源管理器中定位", null, (s, e) =>
            {
                EntryItem it = _grid.FocusedItem;
                string p = it != null ? it.FullPath : _currentDirectory;
                try { Process.Start("explorer.exe", "/select,\"" + p + "\""); } catch { }
            });
            _contextMenu.Items.Add(new ToolStripSeparator());

            // Sort submenu directly in right-click menu!
            ToolStripMenuItem sortMenu = new ToolStripMenuItem("⇅ 排序方式 (Sort By)");
            sortMenu.DropDown.BackColor = Color.FromArgb(28, 31, 40);
            sortMenu.DropDown.ForeColor = Color.White;
            sortMenu.DropDownOpening += (s, e) => UpdateSortButtonsUI();

            _miRememberCurrentFolderSort = new ToolStripMenuItem("📌 记忆此文件夹排序设置 (自动保存当前目录排序)", null, (s, e) =>
            {
                if (_isUpdatingSortUi) return;
                ToggleRememberCurrentFolderSort();
            });
            sortMenu.DropDownItems.Add(_miRememberCurrentFolderSort);

            _miAutoSyncSystemSort = new ToolStripMenuItem("🔄 未记忆文件夹自动跟随系统资源管理器排序 (开关)", null, (s, e) =>
            {
                if (_isUpdatingSortUi) return;
                _autoSyncSystemFolderSort = !_autoSyncSystemFolderSort;
                SaveFolderSortsToDisk();
                if (_autoSyncSystemFolderSort && !IsCurrentFolderSortRemembered())
                {
                    TryApplyExplorerSortForDirectory(_currentDirectory);
                }
                UpdateSortButtonsUI();
                RefreshCurrentDirectory(_grid.ScrollOffset, _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null);
            });
            sortMenu.DropDownItems.Add(_miAutoSyncSystemSort);
            sortMenu.DropDownItems.Add(new ToolStripSeparator());

            _miSortByName = new ToolStripMenuItem("🔤 按名称排序 (自然序)", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(SortField.Name, _sortDescending, true); });
            _miSortByCreated = new ToolStripMenuItem("📅 按创建时间排序", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(SortField.CreatedTime, _sortDescending, true); });
            _miSortByModified = new ToolStripMenuItem("🕒 按修改时间排序", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(SortField.ModifiedTime, _sortDescending, true); });
            _miSortBySize = new ToolStripMenuItem("📏 按文件大小排序", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(SortField.Size, _sortDescending, true); });
            _miSortByTag = new ToolStripMenuItem("🏷️ 按标签颜色优先排序 (红>橙>黄>绿)", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(SortField.Tag, _sortDescending, true); });
            sortMenu.DropDownItems.AddRange(new ToolStripItem[] { _miSortByName, _miSortByCreated, _miSortByModified, _miSortBySize, _miSortByTag });

            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            _miSortAsc = new ToolStripMenuItem("↑ 升序排列 (Ascending)", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(_sortField, false, true); });
            _miSortDesc = new ToolStripMenuItem("↓ 降序排列 (Descending)", null, (s, e) => { if (!_isUpdatingSortUi) ApplySort(_sortField, true, true); });
            sortMenu.DropDownItems.Add(_miSortAsc);
            sortMenu.DropDownItems.Add(_miSortDesc);

            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            _miSortFoldersFirst = new ToolStripMenuItem("📁 文件夹始终置顶 (取消勾选则混合排列)", null, (s, e) =>
            {
                if (_isUpdatingSortUi) return;
                _sortFoldersFirst = !_sortFoldersFirst;
                _defaultSortFoldersFirst = _sortFoldersFirst;
                PersistCurrentFolderSort();
                UpdateSortButtonsUI();
                RefreshCurrentDirectory(_grid.ScrollOffset, _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null);
            });
            sortMenu.DropDownItems.Add(_miSortFoldersFirst);
            _contextMenu.Items.Add(sortMenu);
            _contextMenu.Items.Add(new ToolStripSeparator());

            _contextMenu.Items.Add("🔴 切换【红】标记 (Ctrl+Q)", null, (s, e) => TriggerTagOnSelection((int)Keys.Q));
            _contextMenu.Items.Add("🟠 切换【橙】标记 (Ctrl+W)", null, (s, e) => TriggerTagOnSelection((int)Keys.W));
            _contextMenu.Items.Add("🟡 切换【黄】标记 (Ctrl+E)", null, (s, e) => TriggerTagOnSelection((int)Keys.E));
            _contextMenu.Items.Add("🟢 切换【绿】标记 (Ctrl+R)", null, (s, e) => TriggerTagOnSelection((int)Keys.R));
            _miArchiveToggle = new ToolStripMenuItem("📦 移入 / 复原 " + _archiveRoot + " (Ctrl+T)", null, (s, e) => TriggerTagOnSelection((int)Keys.T));
            _contextMenu.Items.Add(_miArchiveToggle);
            _contextMenu.Items.Add("⚙️ 自定义归档库路径...", null, (s, e) => PromptCustomArchiveRoot());
            _contextMenu.Items.Add(new ToolStripSeparator());

            _contextMenu.Items.Add("✂️ 剪切选中项 (Ctrl+X)", null, (s, e) => CopyOrCutSelectionToClipboard(true));
            _contextMenu.Items.Add("📋 复制选中项 (Ctrl+C)", null, (s, e) => CopyOrCutSelectionToClipboard(false));
            _contextMenu.Items.Add("📥 粘贴到当前目录 (Ctrl+V)", null, (s, e) => PasteFromClipboardToCurrentDirectory());
            _contextMenu.Items.Add("🔗 复制完整路径 (Ctrl+Shift+C)", null, (s, e) => CopySelectedPathsToClipboard());
            _contextMenu.Items.Add(new ToolStripSeparator());

            _contextMenu.Items.Add("✏️ 重命名 (F2)", null, (s, e) => RenameFocusedItem());
            _contextMenu.Items.Add("➕ 新建文件夹 (Ctrl+Shift+N)", null, (s, e) => CreateNewFolderInCurrentDirectory());
            _contextMenu.Items.Add("🗑️ 删除选中项 (Delete)", null, (s, e) => DeleteSelectedItems());
            _contextMenu.Items.Add(new ToolStripSeparator());

            _contextMenu.Items.Add("☑️ 全选 (Ctrl+A)", null, (s, e) => _grid.SelectAll());
            _contextMenu.Items.Add("🔄 反选 (Ctrl+I)", null, (s, e) => _grid.InvertSelection());
        }

        private void TriggerTagOnSelection(int vk)
        {
            List<EntryItem> sel = _grid.SelectedItems;
            if (sel.Count > 0)
            {
                OnHotkeyActionTriggered(sel, vk);
                _grid.Invalidate();
            }
        }

        private void OnContextMenuRequested(EntryItem item, Point clientPt)
        {
            UpdateSortButtonsUI();
            Point screenPt = _grid.PointToScreen(clientPt);
            _contextMenu.Show(screenPt);
        }

        private static Button CreateNavButton(string text, int width, int height, EventHandler onClick)
        {
            Button b = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 39, 50),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9.2f)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(58, 64, 80);
            b.Click += onClick;
            return b;
        }

        private void OnSelectionChanged()
        {
            List<EntryItem> sel = _grid.SelectedItems;
            if (sel.Count == 0)
            {
                _statusLabel.Text = string.Format("当前目录: {0}  |  共 {1} 个项目", _currentDirectory, _grid.Items.Count);
            }
            else if (sel.Count == 1)
            {
                EntryItem item = sel[0];
                List<TagSpec> specs = ResolveTagSpecs(item.TagUids);
                string tagStr = specs.Count > 0
                    ? "  |  标记: " + string.Join(", ", specs.ConvertAll(s => "●" + s.Name).ToArray())
                    : "";
                _statusLabel.Text = string.Format("选中 1 项: {0}{1}  |  共 {2} 项", item.Name, tagStr, _grid.Items.Count);
            }
            else
            {
                _statusLabel.Text = string.Format("已多选 {0} 个项目 (可按 Ctrl+Q/W/E/R 批量打标，Ctrl+T 批量归档，或长按左键拖拽)  |  共 {1} 项", sel.Count, _grid.Items.Count);
            }
        }
    }
}
