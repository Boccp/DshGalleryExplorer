// 主窗口：字段、嵌套类型与构造函数。其余职责按关注点拆在 MainWindow.*.cs。
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
    internal sealed partial class MainWindow : Form
    {
        private string _archiveRoot = AppPaths.ResolveDefaultArchiveRoot();
        private static string ARCHIVE_CONFIG_PATH { get { return AppPaths.ArchiveRootConfigPath; } }
        private static string LEDGER_PATH { get { return AppPaths.LedgerPath; } }
        private static string SHORTCUTS_PATH { get { return AppPaths.SidebarShortcutsPath; } }
        private static string FOLDER_SORTS_PATH { get { return AppPaths.FolderSortsPath; } }

        private sealed class SidebarShortcutEntry
        {
            public string Label;
            public string TargetPath;
            public bool IsSeparator;
            public string TagFilterKey; // null for directory shortcut, "red"/"orange"/"yellow"/"green"/"ALL" for tag filters
            public override string ToString() { return Label ?? ""; }
        }

        private sealed class NavHistoryEntry
        {
            public string DirectoryPath;
            public int ScrollOffset;
            public string SelectedItemPath;
        }

        private readonly Dictionary<string, List<string>> _ledgerMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NavHistoryEntry> _dirViewMemory = new Dictionary<string, NavHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FolderSortSetting> _folderSorts = new Dictionary<string, FolderSortSetting>(StringComparer.OrdinalIgnoreCase);
        private readonly object _folderSortsLock = new object();
        private readonly Stack<NavHistoryEntry> _backStack = new Stack<NavHistoryEntry>();
        private readonly Stack<NavHistoryEntry> _forwardStack = new Stack<NavHistoryEntry>();
        private readonly List<SidebarShortcutEntry> _customShortcuts = new List<SidebarShortcutEntry>();
        private ContextMenuStrip _sidebarContextMenu;

        private SortField _sortField = SortField.Name;
        private bool _sortDescending = false;
        private bool _sortFoldersFirst = true;
        private SortField _defaultSortField = SortField.Name;
        private bool _defaultSortDescending = false;
        private bool _defaultSortFoldersFirst = true;
        private bool _rememberFolderSortByDefault = true;
        private bool _autoSyncSystemFolderSort = true;
        private bool _isUpdatingSortUi = false;
        private Button _btnSortMode;
        private Button _btnSortDir;
        private Button _btnArchiveConfig;
        private Label _legendLabel;
        private ToolStripMenuItem _miArchiveToggle;
        private ToolStripMenuItem _miRememberCurrentFolderSort;
        private ToolStripMenuItem _miAutoSyncSystemSort;
        private ToolStripMenuItem _miSortByName;
        private ToolStripMenuItem _miSortByCreated;
        private ToolStripMenuItem _miSortByModified;
        private ToolStripMenuItem _miSortBySize;
        private ToolStripMenuItem _miSortByTag;
        private ToolStripMenuItem _miSortAsc;
        private ToolStripMenuItem _miSortDesc;
        private ToolStripMenuItem _miSortFoldersFirst;

        private string _currentDirectory = @"F:\Openclow\画师素材库";
        private string _activeTagFilter = null;
        private bool _isInitialized = false;
        private bool _clipboardIsCut = false;

        private TextBox _addressBox;
        private Label _statusLabel;
        private Label _zoomLabel;
        private TrackBar _zoomSlider;
        private ListBox _sidebarList;
        private VirtualGridCanvas _grid;
        private ContextMenuStrip _contextMenu;
        private FileSystemWatcher _ledgerWatcher;
        private readonly object _ledgerLock = new object();

        // Video hover preview state
        private VideoHoverPreviewWindow _videoHoverPopup;
        private System.Windows.Forms.Timer _videoHoverDebounceTimer;
        private string _pendingHoverVideoPath;
        private Point _lastHoverScreenPt;

        private static readonly string[] IMAGE_EXTS = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
        public MainWindow(string initialPath)
        {
            float dpiScale = 1.0f;
            using (Graphics g = CreateGraphics())
            {
                dpiScale = g.DpiX / 96.0f;
            }
            Text = "DSH 画师素材库与归档文件管理器 (4K 高分屏原生超清版)";
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(workArea.Width - 60, (int)(1640 * dpiScale)), Math.Min(workArea.Height - 60, (int)(980 * dpiScale)));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 20, 26);
            ForeColor = Color.White;
            KeyPreview = true;

            try
            {
                string dshExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\DeepSeek Harness\DeepSeek Harness.exe");
                if (File.Exists(dshExe)) Icon = Icon.ExtractAssociatedIcon(dshExe);
            }
            catch (Exception ex) { Log.Debug(ex, "提取窗口图标"); }

            LoadLedgerFromDisk();
            LoadFolderSortsFromDisk();
            LoadArchiveRootFromDisk();
            LoadShortcutsFromDisk();
            BuildUI();
            BuildContextMenu();
            UpdateArchiveRootUI();
            SetupLedgerWatcher();

            string startDir = AppPaths.ResolveDefaultStartDirectory(initialPath);
            NavigateTo(startDir, false);
        }

        private float _dpiScale = 1.0f;
        private int S(int v) { return (int)Math.Round(v * _dpiScale); }
    }
}
