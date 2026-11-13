// GDI+ 双缓冲虚拟网格：字段、构造、属性、条目设置、选择与布局。
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
    internal sealed partial class VirtualGridCanvas : Control
    {
        public event Action<EntryItem> ItemOpened;
        public event Action<EntryItem, Point> ItemHovered;
        public event Action SelectionChanged;
        public event Action<List<EntryItem>, int> HotkeyActionTriggered;
        public event Action<int> TileSizeChanged;
        public event Action NavigateBackRequested;
        public event Action NavigateForwardRequested;
        public event Action<EntryItem, Point> ContextMenuRequested;
        public event Action<List<string>, string, bool> ItemsDroppedIntoFolder;

        private List<EntryItem> _items = new List<EntryItem>();
        private int _focusedIndex = -1;
        private int _anchorIndex = -1;
        private int _hoverIndex = -1;
        private int _dropTargetFolderIndex = -1;
        private int _tileSize = 260;
        private int _scrollOffset = 0;
        private int _totalContentHeight = 0;
        private int _columnsCount = 1;

        // Scrollbar drag state
        private bool _isDraggingScrollbar = false;
        private int _dragStartY = 0;
        private int _dragStartScroll = 0;
        private Rectangle _scrollThumbRect = Rectangle.Empty;

        // Rubber-band marquee selection & item drag-and-drop state
        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartContentPt = Point.Empty;
        private Point _marqueeCurrentContentPt = Point.Empty;
        private Keys _marqueeModifiers = Keys.None;
        private HashSet<int> _preMarqueeSelection = new HashSet<int>();

        private bool _isPendingItemDrag = false;
        private Point _itemDragDownClientPt = Point.Empty;
        private int _itemDragDownIndex = -1;
        private bool _deferSingleSelectOnMouseUp = false;

        // Persistent memory + disk thumbnail cache + eager preloader
        private readonly ConcurrentDictionary<string, Bitmap> _thumbCache = new ConcurrentDictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private int _preloadGeneration = 0;
        private static string DISK_CACHE_DIR { get { return AppPaths.ThumbnailCacheDir; } }

        private string _hudTitle = "";
        private string _hudSub = "";
        private Color _hudColor = Color.FromArgb(232, 17, 35);
        private Point _hudPoint = Point.Empty;
        private DateTime _hudUntil = DateTime.MinValue;
        private System.Windows.Forms.Timer _hudTimer;
        private System.Windows.Forms.Timer _autoScrollTimer;
        private int _autoScrollSpeed = 0;

        public int ScrollOffset
        {
            get { return _scrollOffset; }
            set
            {
                _scrollOffset = value;
                ClampScroll();
                Invalidate();
            }
        }

        public int TileSize
        {
            get { return _tileSize; }
            set
            {
                int clamped = Math.Max(96, Math.Min(768, value));
                if (_tileSize != clamped)
                {
                    _tileSize = clamped;
                    RecalculateLayout();
                    Invalidate();
                    if (TileSizeChanged != null) TileSizeChanged(_tileSize);
                }
            }
        }

        public EntryItem FocusedItem
        {
            get { return (_focusedIndex >= 0 && _focusedIndex < _items.Count) ? _items[_focusedIndex] : null; }
        }

        public List<EntryItem> SelectedItems
        {
            get
            {
                List<EntryItem> res = new List<EntryItem>();
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].IsSelected) res.Add(_items[i]);
                }
                return res;
            }
        }

        public List<EntryItem> Items
        {
            get { return _items; }
        }

        public VirtualGridCanvas()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true
            );
            DoubleBuffered = true;
            AllowDrop = true;
            BackColor = Color.FromArgb(18, 20, 26);
            ForeColor = Color.FromArgb(235, 238, 245);

            try { if (!Directory.Exists(DISK_CACHE_DIR)) Directory.CreateDirectory(DISK_CACHE_DIR); } catch { }

            _hudTimer = new System.Windows.Forms.Timer();
            _hudTimer.Interval = 50;
            _hudTimer.Tick += (s, e) =>
            {
                if (DateTime.UtcNow >= _hudUntil)
                {
                    _hudTimer.Stop();
                    Invalidate();
                }
            };

            _autoScrollTimer = new System.Windows.Forms.Timer();
            _autoScrollTimer.Interval = 8;
            _autoScrollTimer.Tick += (s, e) =>
            {
                if (_isMarqueeSelecting && _autoScrollSpeed != 0)
                {
                    _scrollOffset += _autoScrollSpeed;
                    ClampScroll();
                    Point clientPt = PointToClient(Cursor.Position);
                    _marqueeCurrentContentPt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
                    UpdateMarqueeSelection();
                    Invalidate();
                }
            };

            _thumbInvalidateTimer = new System.Windows.Forms.Timer();
            _thumbInvalidateTimer.Interval = 16;
            _thumbInvalidateTimer.Tick += (s, e) =>
            {
                if (_dirtyThumbnails)
                {
                    _dirtyThumbnails = false;
                    Invalidate();
                }
            };
            _thumbInvalidateTimer.Start();
        }

        public void SetItems(List<EntryItem> items, int restoreScrollOffset = 0, string restoreSelectedPath = null)
        {
            _items = items ?? new List<EntryItem>();
            _focusedIndex = -1;
            _anchorIndex = -1;

            if (!string.IsNullOrEmpty(restoreSelectedPath))
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i].FullPath, restoreSelectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _items[i].IsSelected = true;
                        _focusedIndex = i;
                        _anchorIndex = i;
                        break;
                    }
                }
            }
            if (_focusedIndex < 0 && _items.Count > 0)
            {
                _items[0].IsSelected = true;
                _focusedIndex = 0;
                _anchorIndex = 0;
            }

            _hoverIndex = -1;
            _dropTargetFolderIndex = -1;
            _isMarqueeSelecting = false;
            _isPendingItemDrag = false;

            RecalculateLayout();
            _scrollOffset = restoreScrollOffset;
            ClampScroll();
            if (restoreScrollOffset == 0 && _focusedIndex > 0)
            {
                EnsureIndexVisible(_focusedIndex);
            }
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();

            // Eagerly preload ALL items in the current directory across 8 background workers so scrolling down has 0 lag!
            StartEagerDirectoryPreload(_items);
        }

        public void SelectAll()
        {
            for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = true;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void InvertSelection()
        {
            for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = !_items[i].IsSelected;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void ClearSelection()
        {
            for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = false;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        private void SelectSingleOnly(int idx)
        {
            for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = (i == idx);
            _focusedIndex = idx;
            _anchorIndex = idx;
            if (SelectionChanged != null) SelectionChanged();
        }

        public void RemoveItems(IEnumerable<EntryItem> toRemove)
        {
            HashSet<EntryItem> set = new HashSet<EntryItem>(toRemove);
            int firstRemovedIdx = -1;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (set.Contains(_items[i]))
                {
                    firstRemovedIdx = i;
                    _items.RemoveAt(i);
                }
            }
            if (_items.Count > 0 && SelectedItems.Count == 0)
            {
                int nextSel = Math.Min(_items.Count - 1, Math.Max(0, firstRemovedIdx));
                _items[nextSel].IsSelected = true;
                _focusedIndex = nextSel;
                _anchorIndex = nextSel;
            }
            RecalculateLayout();
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void EnsureIndexVisible(int idx)
        {
            if (idx < 0 || idx >= _items.Count) return;
            Rectangle b = _items[idx].Bounds;
            int padY = 18;
            if (b.Top - padY < _scrollOffset)
            {
                _scrollOffset = Math.Max(0, b.Top - padY);
            }
            else if (b.Bottom + padY > _scrollOffset + ClientSize.Height)
            {
                _scrollOffset = b.Bottom + padY - ClientSize.Height;
            }
            ClampScroll();
        }

        public void ShowInstantHud(Point clientPt, string title, string sub, Color accent)
        {
            _hudPoint = clientPt;
            _hudTitle = title ?? "";
            _hudSub = sub ?? "";
            _hudColor = accent;
            _hudUntil = DateTime.UtcNow.AddMilliseconds(950);
            _hudTimer.Start();
            Invalidate();
        }

        public void RecalculateLayout()
        {
            int scrollbarReserve = 22;
            int minPad = 18;
            int minGap = 14;
            int usableW = Math.Max(1, ClientSize.Width - scrollbarReserve);
            int cardW = _tileSize;
            int cardH = _tileSize + 44;
            int cols = Math.Max(1, (usableW - minPad * 2 + minGap) / (cardW + minGap));
            _columnsCount = cols;

            int gapX = minGap;
            int startX = minPad;
            if (cols > 1 && _items.Count >= cols)
            {
                int remaining = usableW - cols * cardW;
                int slotGap = Math.Max(minGap, remaining / (cols + 1));
                gapX = slotGap;
                startX = (usableW - (cols * cardW + (cols - 1) * gapX)) / 2;
            }
            else if (cols == 1)
            {
                startX = Math.Max(minPad, (usableW - cardW) / 2);
            }

            int gapY = 16;
            int padY = 18;
            for (int i = 0; i < _items.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = startX + c * (cardW + gapX);
                int y = padY + r * (cardH + gapY);
                _items[i].Bounds = new Rectangle(x, y, cardW, cardH);
            }

            int rows = (_items.Count + cols - 1) / cols;
            _totalContentHeight = padY * 2 + rows * cardH + Math.Max(0, rows - 1) * gapY;
            ClampScroll();
        }

        private void ClampScroll()
        {
            int maxScroll = Math.Max(0, _totalContentHeight - ClientSize.Height);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;
            if (_scrollOffset < 0) _scrollOffset = 0;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateLayout();
        }
    }
}
