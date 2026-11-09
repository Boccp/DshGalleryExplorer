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
    internal sealed class TagSpec
    {
        public string Key;
        public string Name;
        public Color Color;
        public string Hex;
        public string Uid;
    }

    internal sealed class EntryItem
    {
        public string FullPath;
        public string Name;
        public bool IsFolder;
        public long SizeBytes;
        public DateTime CreatedAt;
        public DateTime ModifiedAt;
        public List<string> TagUids = new List<string>();
        public string CoverImagePath;
        public Rectangle Bounds;
        public bool IsSelected;
    }

    internal sealed class RenameDialog : Form
    {
        private TextBox _box;
        public string NewName { get { return _box.Text.Trim(); } }

        public RenameDialog(string currentName)
        {
            Text = "重命名";
            Size = new Size(460, 165);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 27, 35);
            ForeColor = Color.White;

            Label lbl = new Label
            {
                Text = "请输入新名称:",
                Location = new Point(16, 16),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };

            _box = new TextBox
            {
                Text = currentName,
                Location = new Point(16, 44),
                Width = 410,
                Font = new Font("Microsoft YaHei UI", 10.5f),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            int dot = currentName.LastIndexOf('.');
            if (dot > 0)
            {
                _box.SelectionStart = 0;
                _box.SelectionLength = dot;
            }
            else
            {
                _box.SelectAll();
            }

            Button btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(250, 84),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White
            };
            Button btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(342, 84),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 50, 62),
                ForeColor = Color.White
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lbl, _box, btnOk, btnCancel });
        }
    }

    internal sealed class VirtualGridCanvas : Control
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
        private static readonly string DISK_CACHE_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\thumbnails");

        private string _hudTitle = "";
        private string _hudSub = "";
        private Color _hudColor = Color.FromArgb(232, 17, 35);
        private Point _hudPoint = Point.Empty;
        private DateTime _hudUntil = DateTime.MinValue;
        private System.Windows.Forms.Timer _hudTimer;
        private System.Windows.Forms.Timer _autoScrollTimer;
        private int _autoScrollSpeed = 0;

        private static readonly string[] IMAGE_EXTS = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".ico" };
        public static readonly string[] VIDEO_EXTS = new[] {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp",
            ".m2ts", ".mts", ".vob", ".ogv", ".rm", ".rmvb", ".asf", ".f4v", ".divx", ".xvid", ".dv", ".3g2", ".mxf"
        };

        public static bool IsVideoExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            return Array.IndexOf(VIDEO_EXTS, ext.ToLowerInvariant()) >= 0;
        }

        public static bool IsSupportedMediaExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            string low = ext.ToLowerInvariant();
            return Array.IndexOf(IMAGE_EXTS, low) >= 0 || Array.IndexOf(VIDEO_EXTS, low) >= 0;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(
            [In] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        public static Bitmap ExtractVideoCoverViaFfmpeg(string videoPath, int maxDim)
        {
            try
            {
                string ffmpegExe = VideoHoverPreviewWindow.ResolveFfmpegPath();
                if (string.IsNullOrEmpty(ffmpegExe) || !File.Exists(ffmpegExe)) return null;

                bool isVideo = IsVideoExtension(Path.GetExtension(videoPath));
                string args = isVideo
                    ? string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -noaccurate_seek -ss 0.5 -i \"{0}\" -frames:v 1 -an -vf \"scale={1}:-2:flags=fast_bilinear\" -q:v 4 -f image2pipe -vcodec mjpeg pipe:1",
                        videoPath, Math.Max(160, maxDim))
                    : string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -i \"{0}\" -frames:v 1 -vf \"scale='min({1},iw)':-2:flags=fast_bilinear\" -q:v 3 -f image2pipe -vcodec mjpeg pipe:1",
                        videoPath, Math.Max(160, maxDim));

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        using (MemoryStream ms = new MemoryStream(65536))
                        {
                            byte[] buf = new byte[16384];
                            int read;
                            while ((read = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, read);
                            }
                            try { proc.WaitForExit(1500); } catch { }
                            if (ms.Length > 64)
                            {
                                ms.Position = 0;
                                using (Image img = Image.FromStream(ms, false, false))
                                {
                                    return new Bitmap(img);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static Bitmap LoadAnyImageBitmap(string path, int maxDim)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isVideo = IsVideoExtension(ext);
            bool isImage = Array.IndexOf(IMAGE_EXTS, ext) >= 0;
            if (!isImage && !isVideo)
            {
                return null;
            }
            // For video files or .webp files, use FFmpeg first so we NEVER trigger broken Microsoft Store/AppX extension popups!
            if (isVideo || ext == ".webp")
            {
                Bitmap ffBmp = ExtractVideoCoverViaFfmpeg(path, maxDim);
                if (ffBmp != null) return ffBmp;
                return null;
            }
            // For standard images (.jpg, .png, .bmp, .gif, .ico), use pure GDI+ MemoryStream!
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    return new Bitmap(img);
                }
            }
            catch { }
            return null;
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

        private volatile bool _dirtyThumbnails = false;
        private System.Windows.Forms.Timer _thumbInvalidateTimer;

        private void StartEagerDirectoryPreload(List<EntryItem> snapshot)
        {
            int gen = Interlocked.Increment(ref _preloadGeneration);
            if (snapshot == null || snapshot.Count == 0) return;

            // Sort workList so items near the current scroll position are decoded FIRST!
            int curScroll = _scrollOffset;
            List<EntryItem> workList = new List<EntryItem>(snapshot);
            workList.Sort((a, b) =>
            {
                int da = Math.Abs(a.Bounds.Top - curScroll);
                int db = Math.Abs(b.Bounds.Top - curScroll);
                return da.CompareTo(db);
            });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                int nextIdx = 0;
                int workerCount = Math.Min(12, workList.Count);
                for (int w = 0; w < workerCount; w++)
                {
                    ThreadPool.QueueUserWorkItem(__ =>
                    {
                        while (true)
                        {
                            if (gen != _preloadGeneration) break;
                            int idx = Interlocked.Increment(ref nextIdx) - 1;
                            if (idx < 0 || idx >= workList.Count) break;
                            EntryItem it = workList[idx];
                            if (it == null) continue;
                            if (_thumbCache.ContainsKey(it.FullPath)) continue;
                            DecodeAndCacheItemSync(it);
                        }
                    });
                }
            });
        }

        private void DecodeAndCacheItemSync(EntryItem item)
        {
            if (_thumbCache.ContainsKey(item.FullPath)) return;
            if (!_inFlight.TryAdd(item.FullPath, 1)) return;
            try
            {
                Bitmap bmp = TryLoadFromDiskCache(item);
                if (bmp == null)
                {
                    string imgPath = item.IsFolder ? item.CoverImagePath : item.FullPath;
                    if (string.IsNullOrEmpty(imgPath) && item.IsFolder)
                    {
                        imgPath = MainWindow.FindFirstImageInFolder(item.FullPath);
                        item.CoverImagePath = imgPath;
                    }
                    if (!string.IsNullOrEmpty(imgPath))
                    {
                        bmp = DecodeThumbnailBitmap(imgPath, item.IsFolder, 320);
                        if (bmp != null)
                        {
                            SaveToDiskCache(item, bmp);
                        }
                    }
                }
                if (bmp != null)
                {
                    _thumbCache[item.FullPath] = bmp;
                    _dirtyThumbnails = true;
                }
            }
            catch { }
            finally
            {
                byte dummy;
                _inFlight.TryRemove(item.FullPath, out dummy);
            }
        }

        private static string GetDiskCacheFilePath(EntryItem item)
        {
            try
            {
                string key = string.Format("{0}|{1}", item.FullPath.ToLowerInvariant(), item.IsFolder ? "D" : "F");
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return Path.Combine(DISK_CACHE_DIR, sb.ToString() + (item.IsFolder ? ".png" : ".jpg"));
                }
            }
            catch { return null; }
        }

        private static Bitmap TryLoadFromDiskCache(EntryItem item)
        {
            try
            {
                string cacheFile = GetDiskCacheFilePath(item);
                if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
                {
                    byte[] bytes = File.ReadAllBytes(cacheFile);
                    using (MemoryStream ms = new MemoryStream(bytes))
                    using (Image img = Image.FromStream(ms, false, false))
                    {
                        return new Bitmap(img);
                    }
                }
            }
            catch { }
            return null;
        }

        private static void SaveToDiskCache(EntryItem item, Bitmap bmp)
        {
            try
            {
                string cacheFile = GetDiskCacheFilePath(item);
                if (string.IsNullOrEmpty(cacheFile) || File.Exists(cacheFile)) return;
                bmp.Save(cacheFile, item.IsFolder ? ImageFormat.Png : ImageFormat.Jpeg);
            }
            catch { }
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

        public void ClearThumbnailCache()
        {
            foreach (var kv in _thumbCache)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _thumbCache.Clear();
            _inFlight.Clear();
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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                int anchorIdx = HitTestIndex(e.Location);
                if (anchorIdx < 0 && _focusedIndex >= 0 && _focusedIndex < _items.Count)
                {
                    anchorIdx = _focusedIndex;
                }
                if (anchorIdx < 0)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].Bounds.Bottom >= _scrollOffset) { anchorIdx = i; break; }
                    }
                }
                float relRatioY = 0.0f;
                int anchorScreenY = e.Y;
                if (anchorIdx >= 0 && anchorIdx < _items.Count)
                {
                    Rectangle oldB = _items[anchorIdx].Bounds;
                    anchorScreenY = oldB.Top - _scrollOffset;
                    relRatioY = oldB.Height > 0 ? (float)(e.Y - anchorScreenY) / oldB.Height : 0.0f;
                }

                int step = Math.Max(16, _tileSize / 8);
                int newSize = Math.Max(96, Math.Min(768, _tileSize + (e.Delta > 0 ? step : -step)));
                if (newSize != _tileSize)
                {
                    _tileSize = newSize;
                    RecalculateLayout();
                    if (anchorIdx >= 0 && anchorIdx < _items.Count)
                    {
                        Rectangle newB = _items[anchorIdx].Bounds;
                        int targetScreenY = (int)(e.Y - relRatioY * newB.Height);
                        _scrollOffset = newB.Top - targetScreenY;
                        ClampScroll();
                    }
                    Invalidate();
                    if (TileSizeChanged != null) TileSizeChanged(_tileSize);
                }
                return;
            }

            int scrollStep = Math.Max(80, (_tileSize + 44) / 2);
            _scrollOffset -= (e.Delta > 0 ? scrollStep : -scrollStep);
            ClampScroll();
            int idxAfterScroll = HitTestIndex(e.Location);
            if (idxAfterScroll != _hoverIndex)
            {
                _hoverIndex = idxAfterScroll;
            }
            if (ItemHovered != null)
            {
                EntryItem hItem = (idxAfterScroll >= 0 && idxAfterScroll < _items.Count) ? _items[idxAfterScroll] : null;
                ItemHovered(hItem, PointToScreen(e.Location));
            }
            Invalidate();
        }

        private int HitTestIndex(Point clientPt)
        {
            Point contentPt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Bounds.Contains(contentPt)) return i;
            }
            return -1;
        }

        private Rectangle GetMarqueeContentRect()
        {
            int x1 = Math.Min(_marqueeStartContentPt.X, _marqueeCurrentContentPt.X);
            int y1 = Math.Min(_marqueeStartContentPt.Y, _marqueeCurrentContentPt.Y);
            int x2 = Math.Max(_marqueeStartContentPt.X, _marqueeCurrentContentPt.X);
            int y2 = Math.Max(_marqueeStartContentPt.Y, _marqueeCurrentContentPt.Y);
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        private void UpdateMarqueeSelection()
        {
            Rectangle box = GetMarqueeContentRect();
            bool isCtrl = (_marqueeModifiers & Keys.Control) == Keys.Control;
            bool isShift = (_marqueeModifiers & Keys.Shift) == Keys.Shift;

            for (int i = 0; i < _items.Count; i++)
            {
                bool hit = _items[i].Bounds.IntersectsWith(box);
                bool wasSel = _preMarqueeSelection.Contains(i);
                if (isCtrl)
                {
                    // Differential / XOR toggle selection when holding Ctrl + Marquee!
                    _items[i].IsSelected = hit ? !wasSel : wasSel;
                }
                else if (isShift)
                {
                    // Additive selection when holding Shift + Marquee!
                    _items[i].IsSelected = hit || wasSel;
                }
                else
                {
                    _items[i].IsSelected = hit;
                }
            }
            if (SelectionChanged != null) SelectionChanged();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDraggingScrollbar)
            {
                int trackH = Math.Max(1, ClientSize.Height - _scrollThumbRect.Height);
                int maxScroll = Math.Max(0, _totalContentHeight - ClientSize.Height);
                int dy = e.Y - _dragStartY;
                _scrollOffset = _dragStartScroll + (int)((double)dy / trackH * maxScroll);
                ClampScroll();
                Invalidate();
                return;
            }

            if (_isMarqueeSelecting)
            {
                _marqueeCurrentContentPt = new Point(e.X, e.Y + _scrollOffset);
                if (e.Y < 28) { _autoScrollSpeed = -28; _autoScrollTimer.Start(); }
                else if (e.Y > ClientSize.Height - 28) { _autoScrollSpeed = 28; _autoScrollTimer.Start(); }
                else { _autoScrollSpeed = 0; _autoScrollTimer.Stop(); }

                UpdateMarqueeSelection();
                Invalidate();
                return;
            }

            if (_isPendingItemDrag && e.Button == MouseButtons.Left)
            {
                int dx = Math.Abs(e.X - _itemDragDownClientPt.X);
                int dy = Math.Abs(e.Y - _itemDragDownClientPt.Y);
                if (dx >= 6 || dy >= 6)
                {
                    _isPendingItemDrag = false;
                    _deferSingleSelectOnMouseUp = false;
                    StartNativeFileDragDrop();
                    return;
                }
            }

            if (e.X >= ClientSize.Width - 22 && _totalContentHeight > ClientSize.Height)
            {
                Cursor = Cursors.Hand;
                if (_hoverIndex != -1)
                {
                    _hoverIndex = -1;
                    Invalidate();
                }
                if (ItemHovered != null) ItemHovered(null, PointToScreen(e.Location));
                return;
            }
            int idx = HitTestIndex(e.Location);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            if (ItemHovered != null)
            {
                EntryItem hItem = (idx >= 0 && idx < _items.Count) ? _items[idx] : null;
                ItemHovered(hItem, PointToScreen(e.Location));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
            if (ItemHovered != null) ItemHovered(null, Cursor.Position);
        }

        private void StartNativeFileDragDrop()
        {
            List<EntryItem> sel = SelectedItems;
            if (sel.Count == 0 && _itemDragDownIndex >= 0 && _itemDragDownIndex < _items.Count)
            {
                SelectSingleOnly(_itemDragDownIndex);
                sel = SelectedItems;
            }
            if (sel.Count == 0) return;

            StringCollection files = new StringCollection();
            foreach (EntryItem it in sel)
            {
                files.Add(it.FullPath);
            }
            DataObject data = new DataObject();
            data.SetFileDropList(files);
            DoDragDrop(data, DragDropEffects.Move | DragDropEffects.Copy);
            _dropTargetFolderIndex = -1;
            Invalidate();
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            base.OnDragOver(drgevent);
            if (!drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                drgevent.Effect = DragDropEffects.None;
                return;
            }
            Point clientPt = PointToClient(new Point(drgevent.X, drgevent.Y));
            int idx = HitTestIndex(clientPt);
            int newTarget = (idx >= 0 && _items[idx].IsFolder) ? idx : -1;
            if (newTarget != _dropTargetFolderIndex)
            {
                _dropTargetFolderIndex = newTarget;
                Invalidate();
            }
            bool isCopy = (drgevent.KeyState & 8) == 8; // MK_CONTROL = 8
            drgevent.Effect = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (_dropTargetFolderIndex != -1)
            {
                _dropTargetFolderIndex = -1;
                Invalidate();
            }
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            int targetIdx = _dropTargetFolderIndex;
            _dropTargetFolderIndex = -1;
            Invalidate();

            if (!drgevent.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] dropped = drgevent.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || dropped.Length == 0) return;

            string targetFolder = null;
            if (targetIdx >= 0 && targetIdx < _items.Count && _items[targetIdx].IsFolder)
            {
                targetFolder = _items[targetIdx].FullPath;
            }
            bool isCopy = (drgevent.KeyState & 8) == 8;
            if (ItemsDroppedIntoFolder != null)
            {
                ItemsDroppedIntoFolder(new List<string>(dropped), targetFolder, isCopy);
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsKeyPhysicallyDown(Keys k)
        {
            return (GetAsyncKeyState((int)k) & 0x8000) != 0;
        }

        private int GetActiveTagOrArchiveKey()
        {
            if ((ModifierKeys & Keys.Control) != Keys.Control) return 0;
            if (IsKeyPhysicallyDown(Keys.Q)) return (int)Keys.Q;
            if (IsKeyPhysicallyDown(Keys.W)) return (int)Keys.W;
            if (IsKeyPhysicallyDown(Keys.E)) return (int)Keys.E;
            if (IsKeyPhysicallyDown(Keys.R)) return (int)Keys.R;
            if (IsKeyPhysicallyDown(Keys.T)) return (int)Keys.T;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            // Mouse side buttons (Back / Forward)
            if (e.Button == MouseButtons.XButton1)
            {
                if (NavigateBackRequested != null) NavigateBackRequested();
                return;
            }
            if (e.Button == MouseButtons.XButton2)
            {
                if (NavigateForwardRequested != null) NavigateForwardRequested();
                return;
            }

            // Scrollbar track click / drag
            if (e.Button == MouseButtons.Left && e.X >= ClientSize.Width - 22 && _totalContentHeight > ClientSize.Height)
            {
                if (_scrollThumbRect.Contains(e.Location))
                {
                    _isDraggingScrollbar = true;
                    _dragStartY = e.Y;
                    _dragStartScroll = _scrollOffset;
                }
                else
                {
                    int maxScroll = Math.Max(0, _totalContentHeight - ClientSize.Height);
                    double ratio = Math.Max(0.0, Math.Min(1.0, (double)(e.Y - _scrollThumbRect.Height / 2) / Math.Max(1, ClientSize.Height - _scrollThumbRect.Height)));
                    _scrollOffset = (int)(ratio * maxScroll);
                    ClampScroll();
                    _isDraggingScrollbar = true;
                    _dragStartY = e.Y;
                    _dragStartScroll = _scrollOffset;
                    Invalidate();
                }
                return;
            }

            int idx = HitTestIndex(e.Location);

            // Right Click: select item on MouseDown, then pop up ContextMenu on MouseUp!
            if (e.Button == MouseButtons.Right)
            {
                if (idx >= 0)
                {
                    if (!_items[idx].IsSelected)
                    {
                        SelectSingleOnly(idx);
                    }
                    else
                    {
                        _focusedIndex = idx;
                    }
                    Invalidate();
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // Check if Ctrl + Q/W/E/R/T + Left Click (Tag or Archive)
            int hk = GetActiveTagOrArchiveKey();
            if (hk != 0 && idx >= 0)
            {
                EntryItem clickedItem = _items[idx];
                List<EntryItem> targets;
                // If user clicked an item that is part of a multi-selection, apply tag/archive to ALL selected items!
                if (clickedItem.IsSelected && SelectedItems.Count > 1)
                {
                    targets = SelectedItems;
                }
                else
                {
                    SelectSingleOnly(idx);
                    targets = new List<EntryItem> { clickedItem };
                }
                if (HotkeyActionTriggered != null) HotkeyActionTriggered(targets, hk);
                Invalidate();
                return;
            }

            // Left Click on Empty Space -> Start Rubber-Band Marquee Selection!
            if (idx < 0)
            {
                _isMarqueeSelecting = true;
                _marqueeModifiers = ModifierKeys;
                _marqueeStartContentPt = new Point(e.X, e.Y + _scrollOffset);
                _marqueeCurrentContentPt = _marqueeStartContentPt;
                _preMarqueeSelection.Clear();

                bool keepExisting = ((ModifierKeys & Keys.Control) == Keys.Control) || ((ModifierKeys & Keys.Shift) == Keys.Shift);
                for (int i = 0; i < _items.Count; i++)
                {
                    if (keepExisting && _items[i].IsSelected)
                        _preMarqueeSelection.Add(i);
                    else if (!keepExisting)
                        _items[i].IsSelected = false;
                }
                Invalidate();
                if (SelectionChanged != null) SelectionChanged();
                return;
            }

            // Left Click on an Item -> Handle Ctrl (differential toggle), Shift (range), or Drag-and-Drop preparation!
            bool isCtrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;

            if (isShift && _anchorIndex >= 0 && _anchorIndex < _items.Count)
            {
                int start = Math.Min(_anchorIndex, idx);
                int end = Math.Max(_anchorIndex, idx);
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!isCtrl) _items[i].IsSelected = false;
                    if (i >= start && i <= end) _items[i].IsSelected = true;
                }
                _focusedIndex = idx;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged();
            }
            else if (isCtrl)
            {
                // Differential toggle selection!
                _items[idx].IsSelected = !_items[idx].IsSelected;
                _focusedIndex = idx;
                _anchorIndex = idx;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged();
            }
            else
            {
                if (_items[idx].IsSelected && SelectedItems.Count > 1)
                {
                    // Keep multi-selection intact on MouseDown in case user is starting a multi-item drag!
                    // If user releases mouse without dragging, OnMouseUp will reduce to single selection.
                    _deferSingleSelectOnMouseUp = true;
                    _focusedIndex = idx;
                }
                else
                {
                    SelectSingleOnly(idx);
                    _deferSingleSelectOnMouseUp = false;
                }
                _isPendingItemDrag = true;
                _itemDragDownClientPt = e.Location;
                _itemDragDownIndex = idx;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                int rIdx = HitTestIndex(e.Location);
                EntryItem rItem = (rIdx >= 0 && rIdx < _items.Count) ? _items[rIdx] : null;
                if (ContextMenuRequested != null) ContextMenuRequested(rItem, e.Location);
                return;
            }
            if (_isDraggingScrollbar)
            {
                _isDraggingScrollbar = false;
                Invalidate();
            }
            if (_isMarqueeSelecting)
            {
                _isMarqueeSelecting = false;
                _autoScrollSpeed = 0;
                _autoScrollTimer.Stop();
                Invalidate();
            }
            if (_isPendingItemDrag)
            {
                _isPendingItemDrag = false;
                if (_deferSingleSelectOnMouseUp && _itemDragDownIndex >= 0 && _itemDragDownIndex < _items.Count)
                {
                    SelectSingleOnly(_itemDragDownIndex);
                    Invalidate();
                }
                _deferSingleSelectOnMouseUp = false;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (GetActiveTagOrArchiveKey() != 0) return;
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None) return;
            int idx = HitTestIndex(e.Location);
            if (idx >= 0 && ItemOpened != null)
            {
                ItemOpened(_items[idx]);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                case Keys.Enter:
                case Keys.Back:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0) return;

            if (e.Alt && e.KeyCode == Keys.Left)
            {
                if (NavigateBackRequested != null) NavigateBackRequested();
                e.Handled = true;
                return;
            }
            if (e.Alt && e.KeyCode == Keys.Right)
            {
                if (NavigateForwardRequested != null) NavigateForwardRequested();
                e.Handled = true;
                return;
            }

            // Also support pressing Ctrl + Q / W / E / R / T directly on keyboard for all currently selected items!
            if (e.Control && !e.Alt && !e.Shift)
            {
                if (e.KeyCode == Keys.Q || e.KeyCode == Keys.W || e.KeyCode == Keys.E || e.KeyCode == Keys.R || e.KeyCode == Keys.T)
                {
                    List<EntryItem> sel = SelectedItems;
                    if (sel.Count > 0 && HotkeyActionTriggered != null)
                    {
                        HotkeyActionTriggered(sel, (int)e.KeyCode);
                        Invalidate();
                        e.Handled = true;
                    }
                    return;
                }
                if (e.KeyCode == Keys.A)
                {
                    SelectAll();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.I)
                {
                    InvertSelection();
                    e.Handled = true;
                    return;
                }
            }

            int nextIdx = _focusedIndex < 0 ? 0 : _focusedIndex;
            bool moved = false;

            switch (e.KeyCode)
            {
                case Keys.Left:
                    if (nextIdx > 0) { nextIdx--; moved = true; }
                    break;
                case Keys.Right:
                    if (nextIdx + 1 < _items.Count) { nextIdx++; moved = true; }
                    break;
                case Keys.Up:
                    if (nextIdx - _columnsCount >= 0) { nextIdx -= _columnsCount; moved = true; }
                    break;
                case Keys.Down:
                    if (nextIdx + _columnsCount < _items.Count) { nextIdx += _columnsCount; moved = true; }
                    else if (_items.Count > 0) { nextIdx = _items.Count - 1; moved = true; }
                    break;
                case Keys.Home:
                    nextIdx = 0; moved = true;
                    break;
                case Keys.End:
                    nextIdx = _items.Count - 1; moved = true;
                    break;
                case Keys.PageUp:
                    int pageRowsUp = Math.Max(1, ClientSize.Height / Math.Max(1, _tileSize + 60));
                    nextIdx = Math.Max(0, nextIdx - pageRowsUp * _columnsCount);
                    moved = true;
                    break;
                case Keys.PageDown:
                    int pageRowsDown = Math.Max(1, ClientSize.Height / Math.Max(1, _tileSize + 60));
                    nextIdx = Math.Min(_items.Count - 1, nextIdx + pageRowsDown * _columnsCount);
                    moved = true;
                    break;
                case Keys.Enter:
                    if (_focusedIndex >= 0 && _focusedIndex < _items.Count && ItemOpened != null)
                    {
                        ItemOpened(_items[_focusedIndex]);
                        e.Handled = true;
                    }
                    return;
            }

            if (moved)
            {
                if (e.Shift && _anchorIndex >= 0 && _anchorIndex < _items.Count)
                {
                    int s = Math.Min(_anchorIndex, nextIdx);
                    int end = Math.Max(_anchorIndex, nextIdx);
                    for (int i = 0; i < _items.Count; i++)
                        _items[i].IsSelected = (i >= s && i <= end);
                    _focusedIndex = nextIdx;
                    if (SelectionChanged != null) SelectionChanged();
                }
                else
                {
                    SelectSingleOnly(nextIdx);
                }
                EnsureIndexVisible(nextIdx);
                Invalidate();
                e.Handled = true;
            }
        }

        private void EnsureThumbnailRequested(EntryItem item)
        {
            if (_thumbCache.ContainsKey(item.FullPath)) return;
            ThreadPool.QueueUserWorkItem(_ => DecodeAndCacheItemSync(item));
        }

        private static Bitmap DecodeThumbnailBitmap(string path, bool isFolder, int targetSize)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!IsSupportedMediaExtension(ext)) return null;

                using (Bitmap src = LoadAnyImageBitmap(path, targetSize))
                {
                    if (src == null) return null;
                    int w = src.Width;
                    int h = src.Height;
                    if (w <= 0 || h <= 0) return null;

                    Bitmap canvas = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        if (isFolder)
                        {
                            using (SolidBrush tabFill = new SolidBrush(Color.FromArgb(245, 179, 36)))
                            using (Pen tabPen = new Pen(Color.FromArgb(186, 122, 12), 2.5f))
                            using (SolidBrush cardBg = new SolidBrush(Color.FromArgb(30, 32, 38)))
                            using (Pen rimPen = new Pen(Color.FromArgb(245, 185, 45), 3.5f))
                            {
                                DrawRoundedRect(g, new Rectangle(10, 4, 132, 34), 10, tabFill, tabPen);
                                DrawRoundedRect(g, new Rectangle(5, 16, targetSize - 10, targetSize - 21), 16, cardBg, rimPen);
                            }

                            Rectangle imgBox = new Rectangle(10, 21, targetSize - 20, targetSize - 31);
                            DrawCoverCroppedImage(g, src, imgBox);
                        }
                        else
                        {
                            float scale = Math.Min((float)targetSize / w, (float)targetSize / h);
                            int dw = Math.Max(1, (int)Math.Round(w * scale));
                            int dh = Math.Max(1, (int)Math.Round(h * scale));
                            int dx = (targetSize - dw) / 2;
                            int dy = (targetSize - dh) / 2;
                            g.DrawImage(src, new Rectangle(dx, dy, dw, dh), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                        }
                    }
                    return canvas;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void DrawCoverCroppedImage(Graphics g, Image src, Rectangle destBox)
        {
            int w = src.Width;
            int h = src.Height;
            float scale = Math.Max((float)destBox.Width / w, (float)destBox.Height / h);
            int sw = Math.Min(w, (int)Math.Round(destBox.Width / scale));
            int sh = Math.Min(h, (int)Math.Round(destBox.Height / scale));
            int sx = (w - sw) / 2;
            int sy = (int)((h - sh) * 0.20f);
            g.DrawImage(src, destBox, new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
        }

        private static void DrawRoundedRect(Graphics g, Rectangle r, int radius, Brush fill, Pen border)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                if (fill != null) g.FillPath(fill, path);
                if (border != null) g.DrawPath(border, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            int viewTop = _scrollOffset;
            int viewBot = _scrollOffset + ClientSize.Height;

            int rowStride = (_tileSize + 44) + 16;
            int firstVisibleRow = Math.Max(0, (viewTop - 18) / Math.Max(1, rowStride));
            int lastVisibleRow = Math.Max(0, (viewBot - 18) / Math.Max(1, rowStride)) + 1;
            int startIdx = Math.Max(0, firstVisibleRow * _columnsCount);
            int endIdx = Math.Min(_items.Count, (lastVisibleRow + 1) * _columnsCount);

            using (Font nameFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular))
            using (Font badgeFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (Font videoExtFont = new Font("Consolas", 8.8f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(232, 236, 242)))
            using (SolidBrush cardNormalBg = new SolidBrush(Color.FromArgb(26, 29, 37)))
            using (SolidBrush cardHoverBg = new SolidBrush(Color.FromArgb(36, 40, 52)))
            using (SolidBrush cardSelBg = new SolidBrush(Color.FromArgb(38, 62, 102)))
            using (SolidBrush cardDropBg = new SolidBrush(Color.FromArgb(30, 88, 65)))
            using (SolidBrush placeholderBg = new SolidBrush(Color.FromArgb(34, 38, 48)))
            using (SolidBrush checkFill = new SolidBrush(Color.FromArgb(0, 125, 250)))
            using (Pen checkPen = new Pen(Color.White, 1.8f))
            using (SolidBrush folderBadgeBg = new SolidBrush(Color.FromArgb(225, 20, 22, 28)))
            using (Pen folderBadgePen = new Pen(Color.FromArgb(255, 200, 55), 1.8f))
            using (SolidBrush folderBadgeFg = new SolidBrush(Color.FromArgb(255, 205, 60)))
            using (SolidBrush videoBadgeBg = new SolidBrush(Color.FromArgb(230, 14, 18, 26)))
            using (Pen videoBadgePen = new Pen(Color.FromArgb(0, 215, 255), 1.8f))
            using (SolidBrush videoBadgeFg = new SolidBrush(Color.FromArgb(0, 229, 255)))
            using (Pen pillWhitePen = new Pen(Color.White, 1.6f))
            using (Pen selPen = new Pen(Color.FromArgb(88, 175, 255), 2.5f))
            using (Pen dropPen = new Pen(Color.FromArgb(40, 220, 120), 3.0f))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                for (int i = startIdx; i < endIdx; i++)
                {
                    EntryItem item = _items[i];
                    Rectangle b = item.Bounds;
                    if (b.Bottom < viewTop || b.Top > viewBot) continue;

                    Rectangle drawRect = new Rectangle(b.X, b.Y - _scrollOffset, b.Width, b.Height);
                    bool isSel = item.IsSelected;
                    bool isHover = (i == _hoverIndex);
                    bool isDropTarget = (i == _dropTargetFolderIndex);

                    Brush bg = isDropTarget ? cardDropBg : (isSel ? cardSelBg : (isHover ? cardHoverBg : cardNormalBg));
                    Pen outerPen = isDropTarget ? dropPen : (isSel ? selPen : null);
                    DrawRoundedRect(g, drawRect, 10, bg, outerPen);

                    Rectangle thumbRect = new Rectangle(drawRect.X + 6, drawRect.Y + 6, drawRect.Width - 12, drawRect.Width - 12);
                    Bitmap bmp;
                    if (_thumbCache.TryGetValue(item.FullPath, out bmp) && bmp != null)
                    {
                        g.DrawImage(bmp, thumbRect);
                    }
                    else
                    {
                        EnsureThumbnailRequested(item);
                        g.FillRectangle(placeholderBg, thumbRect);
                    }

                    // Multi-select checkmark badge at top-right when selected!
                    if (isSel)
                    {
                        Rectangle chkRect = new Rectangle(thumbRect.Right - 28, thumbRect.Y + 6, 22, 22);
                        DrawRoundedRect(g, chkRect, 5, checkFill, checkPen);
                        g.DrawLines(checkPen, new Point[] {
                            new Point(chkRect.X + 5, chkRect.Y + 11),
                            new Point(chkRect.X + 9, chkRect.Y + 16),
                            new Point(chkRect.X + 17, chkRect.Y + 6)
                        });
                    }

                    // Bottom-right folder icon badge or video play badge
                    if (item.IsFolder)
                    {
                        int bw = 42, bh = 32;
                        Rectangle badgeBox = new Rectangle(thumbRect.Right - bw - 6, thumbRect.Bottom - bh - 6, bw, bh);
                        DrawRoundedRect(g, badgeBox, 7, folderBadgeBg, folderBadgePen);
                        g.FillRectangle(folderBadgeFg, badgeBox.X + 8, badgeBox.Y + 7, 10, 4);
                        g.FillRectangle(folderBadgeFg, badgeBox.X + 8, badgeBox.Y + 10, 26, 15);
                    }
                    else if (IsVideoExtension(Path.GetExtension(item.FullPath)))
                    {
                        string vExt = (Path.GetExtension(item.FullPath) ?? "").TrimStart('.').ToUpperInvariant();
                        if (string.IsNullOrEmpty(vExt)) vExt = "VIDEO";
                        int bw = Math.Max(54, 24 + vExt.Length * 8);
                        int bh = 26;
                        Rectangle badgeBox = new Rectangle(thumbRect.Right - bw - 6, thumbRect.Bottom - bh - 6, bw, bh);
                        DrawRoundedRect(g, badgeBox, 6, videoBadgeBg, videoBadgePen);
                        Point[] tri = new Point[]
                        {
                            new Point(badgeBox.X + 8, badgeBox.Y + 6),
                            new Point(badgeBox.X + 8, badgeBox.Bottom - 6),
                            new Point(badgeBox.X + 18, badgeBox.Y + bh / 2)
                        };
                        g.FillPolygon(videoBadgeFg, tri);
                        g.DrawString(vExt, videoExtFont, videoBadgeFg, new PointF(badgeBox.X + 21, badgeBox.Y + 5));
                    }

                    // ── NATIVE 0ms TAG RENDERING (Red / Orange / Yellow / Green) ──
                    if (item.TagUids != null && item.TagUids.Count > 0)
                    {
                        List<TagSpec> activeSpecs = MainWindow.ResolveTagSpecs(item.TagUids);
                        if (activeSpecs.Count > 0)
                        {
                            using (Pen tagBorderPen = new Pen(activeSpecs[0].Color, 3.5f))
                            {
                                DrawRoundedRect(g, drawRect, 10, null, tagBorderPen);
                            }

                            int px = thumbRect.X + 6;
                            int py = thumbRect.Y + 6;
                            foreach (TagSpec spec in activeSpecs)
                            {
                                Rectangle pill = new Rectangle(px, py, 46, 26);
                                using (SolidBrush pb = new SolidBrush(spec.Color))
                                {
                                    DrawRoundedRect(g, pill, 6, pb, pillWhitePen);
                                }
                                g.DrawString("●" + spec.Name, badgeFont, Brushes.White, new PointF(px + 4, py + 3));
                                px += 50;
                            }
                        }
                    }

                    Rectangle titleRect = new Rectangle(drawRect.X + 6, thumbRect.Bottom + 6, drawRect.Width - 12, 32);
                    g.DrawString(item.Name, nameFont, textBrush, titleRect, sf);
                }
            }

            // Draw Rubber-Band Marquee Selection Box if active!
            if (_isMarqueeSelecting)
            {
                Rectangle contentBox = GetMarqueeContentRect();
                Rectangle screenBox = new Rectangle(contentBox.X, contentBox.Y - _scrollOffset, contentBox.Width, contentBox.Height);
                if (screenBox.Width > 1 || screenBox.Height > 1)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(65, 0, 130, 255)))
                    using (Pen border = new Pen(Color.FromArgb(220, 80, 175, 255), 1.8f))
                    {
                        g.FillRectangle(fill, screenBox);
                        g.DrawRectangle(border, screenBox);
                    }
                }
            }

            // Interactive 16px wide scrollbar on right edge
            if (_totalContentHeight > ClientSize.Height && ClientSize.Height > 40)
            {
                float ratio = (float)ClientSize.Height / _totalContentHeight;
                int barH = Math.Max(54, (int)(ClientSize.Height * ratio));
                int maxScroll = _totalContentHeight - ClientSize.Height;
                int barY = (int)((float)_scrollOffset / maxScroll * (ClientSize.Height - barH));
                _scrollThumbRect = new Rectangle(ClientSize.Width - 18, barY, 14, barH);
                using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(26, 29, 38)))
                using (SolidBrush thumbBrush = new SolidBrush(_isDraggingScrollbar ? Color.FromArgb(100, 175, 255) : Color.FromArgb(120, 132, 158)))
                {
                    g.FillRectangle(trackBrush, ClientSize.Width - 20, 0, 20, ClientSize.Height);
                    DrawRoundedRect(g, _scrollThumbRect, 6, thumbBrush, null);
                }
            }
            else
            {
                _scrollThumbRect = Rectangle.Empty;
            }

            // Instant in-canvas HUD feedback
            if (DateTime.UtcNow < _hudUntil && !string.IsNullOrEmpty(_hudTitle))
            {
                int hw = 390, hh = 52;
                int hx = Math.Max(12, Math.Min(_hudPoint.X - 30, ClientSize.Width - hw - 12));
                int hy = Math.Max(12, _hudPoint.Y - hh - 14);
                Rectangle hr = new Rectangle(hx, hy, hw, hh);
                using (SolidBrush hbg = new SolidBrush(Color.FromArgb(240, 20, 22, 28)))
                using (Pen hp = new Pen(_hudColor, 2.2f))
                using (SolidBrush hbar = new SolidBrush(_hudColor))
                using (Font hf1 = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                using (Font hf2 = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
                {
                    DrawRoundedRect(g, hr, 8, hbg, hp);
                    g.FillRectangle(hbar, hx + 2, hy + 2, 8, hh - 4);
                    g.DrawString(_hudTitle, hf1, Brushes.White, new PointF(hx + 16, hy + 6));
                    g.DrawString(_hudSub, hf2, Brushes.LightGray, new PointF(hx + 16, hy + 28));
                }
            }
        }
    }

    internal sealed class FastImageViewerForm : Form
    {
        private List<string> _imageList;
        private int _currentIndex;
        private Bitmap _currentBitmap;

        public FastImageViewerForm(string currentImagePath)
        {
            Text = Path.GetFileName(currentImagePath) + " — 按 Left/Right 翻页，Esc 或双击退出";
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size((int)(wa.Width * 0.88), (int)(wa.Height * 0.90));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(12, 14, 18);
            ForeColor = Color.White;
            DoubleBuffered = true;
            KeyPreview = true;

            _imageList = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(currentImagePath);
                string[] all = Directory.GetFiles(dir);
                Array.Sort(all, (a, b) => MainWindow.NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
                string[] exts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
                foreach (string f in all)
                {
                    if (Array.IndexOf(exts, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                        _imageList.Add(f);
                }
            }
            catch { }
            _currentIndex = Math.Max(0, _imageList.IndexOf(currentImagePath));
            if (_imageList.Count == 0) _imageList.Add(currentImagePath);
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            if (_currentIndex < 0 || _currentIndex >= _imageList.Count) return;
            string p = _imageList[_currentIndex];
            Bitmap bmp = VirtualGridCanvas.LoadAnyImageBitmap(p, 2560);
            Bitmap old = _currentBitmap;
            _currentBitmap = bmp;
            if (old != null) old.Dispose();
            Text = string.Format("[{0}/{1}] {2}  (←/→ 键或滚轮翻页，Esc 或双击关闭)", _currentIndex + 1, _imageList.Count, Path.GetFileName(p));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_currentBitmap == null) return;
            int cw = ClientSize.Width - 20;
            int ch = ClientSize.Height - 20;
            if (cw <= 0 || ch <= 0) return;
            float scale = Math.Min((float)cw / _currentBitmap.Width, (float)ch / _currentBitmap.Height);
            int dw = Math.Max(1, (int)(_currentBitmap.Width * scale));
            int dh = Math.Max(1, (int)(_currentBitmap.Height * scale));
            int dx = (ClientSize.Width - dw) / 2;
            int dy = (ClientSize.Height - dh) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_currentBitmap, new Rectangle(dx, dy, dw, dh));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta < 0 && _currentIndex + 1 < _imageList.Count) { _currentIndex++; LoadCurrent(); }
            else if (e.Delta > 0 && _currentIndex > 0) { _currentIndex--; LoadCurrent(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back || e.KeyCode == Keys.Space) Close();
            else if ((e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) && _currentIndex + 1 < _imageList.Count) { _currentIndex++; LoadCurrent(); }
            else if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) && _currentIndex > 0) { _currentIndex--; LoadCurrent(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _currentBitmap != null) { _currentBitmap.Dispose(); _currentBitmap = null; }
            base.Dispose(disposing);
        }
    }

    internal sealed class VideoHoverPreviewWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.Border _outerBorder;
        private readonly System.Windows.Controls.MediaElement _mediaElement;
        private readonly System.Windows.Controls.Image _highFpsImage;
        private readonly System.Windows.Controls.TextBlock _titleText;
        private readonly System.Windows.Controls.TextBlock _timeBadgeText;
        private readonly System.Windows.Controls.Border _progressFill;
        private readonly System.Windows.Controls.Grid _progressTrack;

        private string _currentVideoPath = null;
        private double _durationSeconds = 0.0;
        private double _currentSpeedRatio = 1.0;
        private bool _isHighFpsStreamMode = false;
        private bool _mediaReady = false;
        private bool _renderingHooked = false;
        private readonly Stopwatch _cycleClock = new Stopwatch();

        // High-FPS (~120fps / 600 frames per 5s) frame stream state
        private int _streamGeneration = 0;
        private readonly object _framesLock = new object();
        private List<byte[]> _activeFrameJpegs = new List<byte[]>();
        private int _lastRenderedFrameIdx = -1;
        private int _framesPresentedInSec = 0;
        private int _measuredFps = 120;
        private long _fpsWindowStartMs = 0;
        private Point _lastAppliedCursorPt = new Point(-99999, -99999);
        private double _cachedDpiX = 0.0;
        private double _cachedDpiY = 0.0;

        private static readonly ConcurrentDictionary<string, List<byte[]>> _memoryReelCache =
            new ConcurrentDictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly string REEL_DISK_CACHE_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\video_reels");
        private static readonly string[] FFMPEG_CANDIDATE_PATHS = new[]
        {
            @"D:\_tools\ffmpeg.exe",
            @"D:\JianyingPro\11.5.0.14471\ffmpeg.exe",
            @"D:\JianyingPro\11.4.2.14459\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"JianyingPro\Apps\10.7.0.14095\ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe"),
            @"C:\Users\Administrator\AppData\Local\hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe",
            @"C:\Users\Administrator\Desktop\处理图片和视频\ffmpeg.exe",
            @"F:\剪映\9.9.0.13784\ffmpeg.exe"
        };

        private const double TARGET_PREVIEW_CYCLE_SECONDS = 5.0;
        private const int TARGET_REEL_CHUNKS = 36;
        private const int TARGET_FRAMES_PER_CHUNK = 17; // 36 * 17 = 612 distinct frames across 5s (~122.4 fps)
        private const int BASE_WIDTH = 440;
        private const int BASE_VIDEO_HEIGHT = 248;
        private const int HEADER_FOOTER_HEIGHT = 44;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        internal static string ResolveFfmpegPath()
        {
            foreach (string p in FFMPEG_CANDIDATE_PATHS)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public VideoHoverPreviewWindow()
        {
            try { if (!Directory.Exists(REEL_DISK_CACHE_DIR)) Directory.CreateDirectory(REEL_DISK_CACHE_DIR); } catch { }

            WindowStyle = System.Windows.WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            IsHitTestVisible = false;
            Focusable = false;
            Width = BASE_WIDTH;
            Height = BASE_VIDEO_HEIGHT + HEADER_FOOTER_HEIGHT;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 16, 22));

            _outerBorder = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 16, 22)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 215, 255)),
                BorderThickness = new System.Windows.Thickness(1.8),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            System.Windows.Controls.Grid rootGrid = new System.Windows.Controls.Grid();
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(24) });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(20) });

            // Row 0: Header (Filename + Speed & FPS badge)
            System.Windows.Controls.Border headerBar = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 25, 34)),
                Padding = new System.Windows.Thickness(8, 2, 8, 2)
            };
            System.Windows.Controls.DockPanel headerDock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            _timeBadgeText = new System.Windows.Controls.TextBlock
            {
                Text = "5.0s 120FPS 极速预览",
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 10.8,
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(6, 0, 0, 0)
            };
            System.Windows.Controls.DockPanel.SetDock(_timeBadgeText, System.Windows.Controls.Dock.Right);
            headerDock.Children.Add(_timeBadgeText);

            _titleText = new System.Windows.Controls.TextBlock
            {
                Text = "",
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = 11.2,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            };
            headerDock.Children.Add(_titleText);
            headerBar.Child = headerDock;
            System.Windows.Controls.Grid.SetRow(headerBar, 0);
            rootGrid.Children.Add(headerBar);

            // Row 1: Dual-Engine Hardware Video Stage (MediaElement for short clips + HighFpsImage for 612-frame 120fps long videos)
            System.Windows.Controls.Grid videoStage = new System.Windows.Controls.Grid
            {
                Background = System.Windows.Media.Brushes.Black,
                ClipToBounds = true
            };
            _mediaElement = new System.Windows.Controls.MediaElement
            {
                LoadedBehavior = System.Windows.Controls.MediaState.Manual,
                UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
                Stretch = System.Windows.Media.Stretch.Uniform,
                IsMuted = true,
                Volume = 0.0,
                ScrubbingEnabled = false,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(_mediaElement, System.Windows.Media.BitmapScalingMode.Linear);
            _mediaElement.MediaOpened += OnMediaOpened;
            _mediaElement.MediaEnded += OnMediaEnded;
            videoStage.Children.Add(_mediaElement);

            _highFpsImage = new System.Windows.Controls.Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Visibility = System.Windows.Visibility.Collapsed
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(_highFpsImage, System.Windows.Media.BitmapScalingMode.Linear);
            videoStage.Children.Add(_highFpsImage);

            System.Windows.Controls.Grid.SetRow(videoStage, 1);
            rootGrid.Children.Add(videoStage);

            // Row 2: Progress Bar Footer
            System.Windows.Controls.Border footerBar = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 23, 31)),
                Padding = new System.Windows.Thickness(8, 6, 8, 6)
            };
            _progressTrack = new System.Windows.Controls.Grid
            {
                Height = 6,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 52, 68)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            _progressFill = new System.Windows.Controls.Border
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = 0,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 220, 255))
            };
            _progressTrack.Children.Add(_progressFill);
            footerBar.Child = _progressTrack;
            System.Windows.Controls.Grid.SetRow(footerBar, 2);
            rootGrid.Children.Add(footerBar);

            _outerBorder.Child = rootGrid;
            Content = _outerBorder;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, -20);
                // WS_EX_TRANSPARENT (0x20) | WS_EX_TOOLWINDOW (0x80) | WS_EX_NOACTIVATE (0x08000000)
                SetWindowLong(hwnd, -20, exStyle | 0x00000020 | 0x00000080 | 0x08000000);
            }
            catch { }
        }

        public string CurrentVideoPath { get { return _currentVideoPath; } }

        public void ShowOrUpdatePreview(string videoPath, Point cursorScreenPt)
        {
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                HidePreview();
                return;
            }

            UpdatePopupPosition(cursorScreenPt, false);

            if (!string.Equals(_currentVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                int gen = Interlocked.Increment(ref _streamGeneration);
                _currentVideoPath = videoPath;
                _mediaReady = false;
                _durationSeconds = 0.0;
                _isHighFpsStreamMode = false;
                _lastRenderedFrameIdx = -1;
                _framesPresentedInSec = 0;
                _fpsWindowStartMs = 0;
                lock (_framesLock)
                {
                    _activeFrameJpegs = new List<byte[]>();
                }
                _highFpsImage.Source = null;
                _highFpsImage.Visibility = System.Windows.Visibility.Collapsed;
                _mediaElement.Visibility = System.Windows.Visibility.Visible;

                _titleText.Text = Path.GetFileName(videoPath);
                _timeBadgeText.Text = "加载中...";
                _progressFill.Width = 0;

                try
                {
                    _mediaElement.Stop();
                    _mediaElement.ScrubbingEnabled = false;
                    _mediaElement.SpeedRatio = 1.0;
                    _mediaElement.Source = new Uri(videoPath, UriKind.Absolute);
                    _mediaElement.Play();
                }
                catch { }
            }

            if (!IsVisible)
            {
                Show();
            }
            EnsureRenderingHook(true);
        }

        public void HidePreview()
        {
            Interlocked.Increment(ref _streamGeneration);
            EnsureRenderingHook(false);
            _cycleClock.Stop();
            if (_currentVideoPath != null)
            {
                _currentVideoPath = null;
                _mediaReady = false;
                _isHighFpsStreamMode = false;
                _lastRenderedFrameIdx = -1;
                try
                {
                    _mediaElement.Stop();
                    _mediaElement.Source = null;
                    _highFpsImage.Source = null;
                }
                catch { }
            }
            if (IsVisible)
            {
                Hide();
            }
        }

        public void UpdatePopupPosition(Point cursorScreenPt, bool force = false)
        {
            if (!force &&
                Math.Abs(cursorScreenPt.X - _lastAppliedCursorPt.X) < 2 &&
                Math.Abs(cursorScreenPt.Y - _lastAppliedCursorPt.Y) < 2)
            {
                return;
            }
            _lastAppliedCursorPt = cursorScreenPt;

            try
            {
                if (_cachedDpiX <= 0.0 || _cachedDpiY <= 0.0)
                {
                    System.Windows.PresentationSource src = System.Windows.PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null)
                    {
                        _cachedDpiX = src.CompositionTarget.TransformToDevice.M11;
                        _cachedDpiY = src.CompositionTarget.TransformToDevice.M22;
                    }
                    else
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        {
                            _cachedDpiX = g.DpiX / 96.0;
                            _cachedDpiY = g.DpiY / 96.0;
                        }
                    }
                }

                double dpiX = _cachedDpiX > 0 ? _cachedDpiX : 1.0;
                double dpiY = _cachedDpiY > 0 ? _cachedDpiY : 1.0;

                Rectangle wa = Screen.FromPoint(cursorScreenPt).WorkingArea;
                int winPxW = (int)Math.Round(Width * dpiX);
                int winPxH = (int)Math.Round(Height * dpiY);

                int offsetX = (int)Math.Round(22 * dpiX);
                int offsetY = (int)Math.Round(18 * dpiY);

                int targetPxX = cursorScreenPt.X + offsetX;
                int targetPxY = cursorScreenPt.Y + offsetY;

                if (targetPxX + winPxW > wa.Right - 8)
                {
                    targetPxX = cursorScreenPt.X - winPxW - offsetX;
                }
                if (targetPxY + winPxH > wa.Bottom - 8)
                {
                    targetPxY = wa.Bottom - winPxH - 8;
                }
                if (targetPxX < wa.Left + 8) targetPxX = wa.Left + 8;
                if (targetPxY < wa.Top + 8) targetPxY = wa.Top + 8;

                Left = targetPxX / dpiX;
                Top = targetPxY / dpiY;
            }
            catch { }
        }

        private void OnMediaOpened(object sender, System.Windows.RoutedEventArgs e)
        {
            _mediaReady = true;
            _durationSeconds = _mediaElement.NaturalDuration.HasTimeSpan
                ? _mediaElement.NaturalDuration.TimeSpan.TotalSeconds
                : 0.0;

            int vw = _mediaElement.NaturalVideoWidth;
            int vh = _mediaElement.NaturalVideoHeight;
            if (vw > 0 && vh > 0)
            {
                double aspect = (double)vw / vh;
                if (aspect >= 1.0)
                {
                    Width = 440;
                    int stageH = Math.Max(156, Math.Min(310, (int)Math.Round(440.0 / aspect)));
                    Height = stageH + HEADER_FOOTER_HEIGHT;
                }
                else
                {
                    int stageH = 320;
                    int stageW = Math.Max(220, Math.Min(390, (int)Math.Round(stageH * aspect)));
                    Width = stageW;
                    Height = stageH + HEADER_FOOTER_HEIGHT;
                }
                UpdatePopupPosition(System.Windows.Forms.Cursor.Position, true);
            }

            _cycleClock.Restart();
            _fpsWindowStartMs = 0;
            _framesPresentedInSec = 0;

            if (_durationSeconds <= 0.1)
            {
                _currentSpeedRatio = 1.0;
                _isHighFpsStreamMode = false;
                return;
            }

            double idealSpeed = _durationSeconds / TARGET_PREVIEW_CYCLE_SECONDS;
            _currentSpeedRatio = Math.Max(1.0, idealSpeed);

            // For short videos (<= 20s, speed <= 4.0x), native MediaElement hardware decode delivers 60-120fps directly.
            // For medium/long videos (> 20s, e.g. 10-minute video = 120x speed), MediaElement either caps at 4-8x or stalls on seeks (~20fps).
            // Switch to our 612-frame High-FPS Stream Engine so 5 seconds displays ~612 distinct frames at 120fps!
            string ffmpegExe = ResolveFfmpegPath();
            if (idealSpeed <= 4.0 || string.IsNullOrEmpty(ffmpegExe))
            {
                _isHighFpsStreamMode = false;
                _highFpsImage.Visibility = System.Windows.Visibility.Collapsed;
                _mediaElement.Visibility = System.Windows.Visibility.Visible;
                try
                {
                    _mediaElement.ScrubbingEnabled = false;
                    _mediaElement.Position = TimeSpan.Zero;
                    _mediaElement.SpeedRatio = Math.Min(8.0, _currentSpeedRatio);
                    _mediaElement.Play();
                }
                catch { }
            }
            else
            {
                _isHighFpsStreamMode = true;
                try
                {
                    _mediaElement.Pause();
                }
                catch { }
                StartHighFpsReelStream(_currentVideoPath, _durationSeconds, _streamGeneration, ffmpegExe);
            }
        }

        private static string GetReelDiskCachePath(string videoPath)
        {
            try
            {
                FileInfo fi = new FileInfo(videoPath);
                string key = string.Format("{0}|{1}|{2}|v2_612", videoPath.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc.Ticks);
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return Path.Combine(REEL_DISK_CACHE_DIR, sb.ToString() + ".reel");
                }
            }
            catch { return null; }
        }

        private static List<byte[]> TryLoadReelFromDisk(string cacheFile)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFile) || !File.Exists(cacheFile)) return null;
                byte[] all = File.ReadAllBytes(cacheFile);
                if (all.Length < 8) return null;
                int count = BitConverter.ToInt32(all, 0);
                if (count <= 0 || count > 2000) return null;
                List<byte[]> frames = new List<byte[]>(count);
                int pos = 4;
                for (int i = 0; i < count; i++)
                {
                    if (pos + 4 > all.Length) break;
                    int len = BitConverter.ToInt32(all, pos);
                    pos += 4;
                    if (len <= 0 || pos + len > all.Length) break;
                    byte[] f = new byte[len];
                    Buffer.BlockCopy(all, pos, f, 0, len);
                    frames.Add(f);
                    pos += len;
                }
                return frames.Count > 30 ? frames : null;
            }
            catch { return null; }
        }

        private static void SaveReelToDisk(string cacheFile, List<byte[]> frames)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFile) || frames == null || frames.Count < 30) return;
                string tmp = cacheFile + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(frames.Count);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        byte[] f = frames[i];
                        if (f == null || f.Length == 0) continue;
                        bw.Write(f.Length);
                        bw.Write(f);
                    }
                }
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
                File.Move(tmp, cacheFile);
            }
            catch { }
        }

        private static List<byte[]> ParseConcatenatedJpegs(byte[] raw, int length)
        {
            List<byte[]> result = new List<byte[]>();
            if (raw == null || length < 4) return result;

            int frameStart = -1;
            for (int i = 0; i < length - 1; i++)
            {
                if (raw[i] == 0xFF && raw[i + 1] == 0xD8)
                {
                    if (frameStart < 0)
                    {
                        frameStart = i;
                    }
                }
                else if (raw[i] == 0xFF && raw[i + 1] == 0xD9 && frameStart >= 0)
                {
                    int frameLen = (i + 2) - frameStart;
                    if (frameLen > 128)
                    {
                        byte[] jpg = new byte[frameLen];
                        Buffer.BlockCopy(raw, frameStart, jpg, 0, frameLen);
                        result.Add(jpg);
                    }
                    frameStart = -1;
                    i++;
                }
            }
            return result;
        }

        private void StartHighFpsReelStream(string videoPath, double durationSec, int gen, string ffmpegExe)
        {
            // 1. Check instant memory cache
            List<byte[]> cached;
            if (_memoryReelCache.TryGetValue(videoPath, out cached) && cached != null && cached.Count > 30)
            {
                lock (_framesLock)
                {
                    _activeFrameJpegs = cached;
                }
                _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                _mediaElement.Visibility = System.Windows.Visibility.Collapsed;
                _cycleClock.Restart();
                return;
            }

            // 2. Check disk cache or stream-extract 612 frames (36 micro-bursts x 17 frames) in background
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string diskCachePath = GetReelDiskCachePath(videoPath);
                    List<byte[]> diskFrames = TryLoadReelFromDisk(diskCachePath);
                    if (diskFrames != null && diskFrames.Count > 30)
                    {
                        _memoryReelCache[videoPath] = diskFrames;
                        if (gen == _streamGeneration)
                        {
                            lock (_framesLock)
                            {
                                _activeFrameJpegs = diskFrames;
                            }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (gen == _streamGeneration)
                                {
                                    _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                                    _mediaElement.Visibility = System.Windows.Visibility.Collapsed;
                                    _cycleClock.Restart();
                                }
                            }));
                        }
                        return;
                    }

                    // Stream-extract 36 chunks across the video timeline concurrently (4 workers in parallel, ordered assembly)
                    int chunkCount = TARGET_REEL_CHUNKS;
                    int framesPerChunk = TARGET_FRAMES_PER_CHUNK;
                    double usableDur = Math.Max(1.0, durationSec * 0.985);
                    List<byte[]>[] chunkResults = new List<byte[]>[chunkCount];
                    int nextChunk = 0;
                    int completedChunks = 0;
                    ManualResetEvent allDone = new ManualResetEvent(false);
                    int workerCount = 4;

                    for (int w = 0; w < workerCount; w++)
                    {
                        ThreadPool.QueueUserWorkItem(__ =>
                        {
                            try
                            {
                                while (true)
                                {
                                    if (gen != _streamGeneration) break;
                                    int cIdx = Interlocked.Increment(ref nextChunk) - 1;
                                    if (cIdx >= chunkCount) break;

                                    double seekSec = (cIdx * usableDur) / chunkCount;
                                    string args = string.Format(
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        "-hide_banner -loglevel error -noaccurate_seek -threads 2 -flags2 fast -skip_loop_filter all -ss {0:0.00} -i \"{1}\" -frames:v {2} -an -vf \"scale=540:-2:flags=fast_bilinear\" -q:v 4 -f mjpeg pipe:1",
                                        seekSec, videoPath, framesPerChunk);

                                    ProcessStartInfo psi = new ProcessStartInfo
                                    {
                                        FileName = ffmpegExe,
                                        Arguments = args,
                                        UseShellExecute = false,
                                        CreateNoWindow = true,
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = false
                                    };

                                    using (Process proc = Process.Start(psi))
                                    {
                                        if (proc != null)
                                        {
                                            using (MemoryStream ms = new MemoryStream(262144))
                                            {
                                                byte[] buf = new byte[32768];
                                                int read;
                                                while ((read = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                                                {
                                                    if (gen != _streamGeneration)
                                                    {
                                                        try { proc.Kill(); } catch { }
                                                        break;
                                                    }
                                                    ms.Write(buf, 0, read);
                                                }
                                                try { proc.WaitForExit(1500); } catch { }
                                                byte[] rawBytes = ms.GetBuffer();
                                                int rawLen = (int)ms.Length;
                                                List<byte[]> parsed = ParseConcatenatedJpegs(rawBytes, rawLen);
                                                chunkResults[cIdx] = parsed;
                                            }
                                        }
                                    }

                                    // Progressive assembly so playback starts immediately at 120fps after the first chunk (~120ms)!
                                    if (gen == _streamGeneration)
                                    {
                                        List<byte[]> progressive = new List<byte[]>(chunkCount * framesPerChunk);
                                        for (int k = 0; k < chunkCount; k++)
                                        {
                                            if (chunkResults[k] != null && chunkResults[k].Count > 0)
                                            {
                                                progressive.AddRange(chunkResults[k]);
                                            }
                                        }
                                        if (progressive.Count > 0)
                                        {
                                            bool firstSwitch = false;
                                            lock (_framesLock)
                                            {
                                                firstSwitch = (_activeFrameJpegs.Count == 0);
                                                _activeFrameJpegs = progressive;
                                            }
                                            if (firstSwitch)
                                            {
                                                Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    if (gen == _streamGeneration)
                                                    {
                                                        _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                                                        _mediaElement.Visibility = System.Windows.Visibility.Collapsed;
                                                        _cycleClock.Restart();
                                                    }
                                                }));
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                if (Interlocked.Increment(ref completedChunks) == workerCount)
                                {
                                    try { allDone.Set(); } catch { }
                                }
                            }
                        });
                    }

                    allDone.WaitOne(20000);
                    allDone.Close();

                    if (gen == _streamGeneration)
                    {
                        List<byte[]> finalReel = new List<byte[]>(chunkCount * framesPerChunk);
                        for (int k = 0; k < chunkCount; k++)
                        {
                            if (chunkResults[k] != null && chunkResults[k].Count > 0)
                            {
                                finalReel.AddRange(chunkResults[k]);
                            }
                        }
                        if (finalReel.Count > 30)
                        {
                            _memoryReelCache[videoPath] = finalReel;
                            lock (_framesLock)
                            {
                                _activeFrameJpegs = finalReel;
                            }
                            SaveReelToDisk(diskCachePath, finalReel);
                        }
                    }
                }
                catch { }
            });
        }

        private void OnMediaEnded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!_mediaReady || _currentVideoPath == null) return;
            try
            {
                _cycleClock.Restart();
                _mediaElement.Position = TimeSpan.Zero;
                if (!_isHighFpsStreamMode)
                {
                    _mediaElement.Play();
                }
            }
            catch { }
        }

        private void EnsureRenderingHook(bool attach)
        {
            if (attach && !_renderingHooked)
            {
                System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
                _renderingHooked = true;
            }
            else if (!attach && _renderingHooked)
            {
                System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering;
                _renderingHooked = false;
            }
        }

        private static System.Windows.Media.Imaging.BitmapImage DecodeJpegBytesToBitmapImage(byte[] jpegBytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpegBytes, false))
                {
                    System.Windows.Media.Imaging.BitmapImage bmi = new System.Windows.Media.Imaging.BitmapImage();
                    bmi.BeginInit();
                    bmi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmi.StreamSource = ms;
                    bmi.EndInit();
                    bmi.Freeze();
                    return bmi;
                }
            }
            catch { return null; }
        }

        private void OnCompositionRendering(object sender, EventArgs e)
        {
            if (!_mediaReady || !IsVisible || _currentVideoPath == null || _durationSeconds <= 0.05) return;

            double posSec = 0.0;
            int totalReelFrames = 0;

            if (_isHighFpsStreamMode)
            {
                List<byte[]> framesSnapshot;
                lock (_framesLock)
                {
                    framesSnapshot = _activeFrameJpegs;
                }
                totalReelFrames = framesSnapshot != null ? framesSnapshot.Count : 0;

                if (totalReelFrames > 0)
                {
                    long elapsedMs = _cycleClock.ElapsedMilliseconds;
                    long cycleMs = (long)(TARGET_PREVIEW_CYCLE_SECONDS * 1000.0);
                    double ratio = (elapsedMs % cycleMs) / (double)cycleMs;
                    posSec = ratio * _durationSeconds;

                    int targetIdx = (int)(ratio * totalReelFrames);
                    if (targetIdx >= totalReelFrames) targetIdx = totalReelFrames - 1;
                    if (targetIdx < 0) targetIdx = 0;

                    if (targetIdx != _lastRenderedFrameIdx)
                    {
                        _lastRenderedFrameIdx = targetIdx;
                        byte[] jpg = framesSnapshot[targetIdx];
                        if (jpg != null && jpg.Length > 0)
                        {
                            System.Windows.Media.Imaging.BitmapImage bmi = DecodeJpegBytesToBitmapImage(jpg);
                            if (bmi != null)
                            {
                                _highFpsImage.Source = bmi;
                                _framesPresentedInSec++;
                            }
                        }
                    }

                    if (elapsedMs - _fpsWindowStartMs >= 500)
                    {
                        long dt = Math.Max(1, elapsedMs - _fpsWindowStartMs);
                        _measuredFps = Math.Max(60, Math.Min(120, (int)Math.Round(_framesPresentedInSec * 1000.0 / dt)));
                        _framesPresentedInSec = 0;
                        _fpsWindowStartMs = elapsedMs;
                    }
                }
            }
            else
            {
                try
                {
                    posSec = _mediaElement.Position.TotalSeconds;
                    if (posSec >= _durationSeconds - 0.04)
                    {
                        _cycleClock.Restart();
                        _mediaElement.Position = TimeSpan.Zero;
                        _mediaElement.Play();
                        posSec = 0.0;
                    }
                }
                catch { }
            }

            double progressRatio = Math.Max(0.0, Math.Min(1.0, posSec / _durationSeconds));
            double trackW = _progressTrack.ActualWidth;
            if (trackW > 0)
            {
                _progressFill.Width = trackW * progressRatio;
            }

            if (_isHighFpsStreamMode && totalReelFrames > 0)
            {
                _timeBadgeText.Text = string.Format("{0:0.0}s / {1:0.0}s ({2}帧·120fps)", posSec, _durationSeconds, totalReelFrames);
            }
            else
            {
                _timeBadgeText.Text = string.Format("{0:0.0}s / {1:0.0}s ({2:0.#}x)", posSec, _durationSeconds, _currentSpeedRatio);
            }
        }
    }

    internal sealed class MainWindow : Form
    {
        public static readonly TagSpec[] ORDERED_TAGS = new TagSpec[]
        {
            new TagSpec { Key = "red",    Name = "红", Color = Color.FromArgb(232, 17, 35),  Hex = "#E81123", Uid = "f7e0e137-2eb5-4fa4-a50d-ddd65df17c34" },
            new TagSpec { Key = "orange", Name = "橙", Color = Color.FromArgb(247, 99, 12),  Hex = "#F7630C", Uid = "c84a8131-c4de-47d9-9440-26e859d14b3d" },
            new TagSpec { Key = "yellow", Name = "黄", Color = Color.FromArgb(255, 185, 0),  Hex = "#FFB900", Uid = "d4b8d4bd-ceaf-4e58-ac61-a185fcf96c5d" },
            new TagSpec { Key = "green",  Name = "绿", Color = Color.FromArgb(16, 124, 16),  Hex = "#107C10", Uid = "79376daf-c44a-4fe4-aa3b-8b30baea453e" },
        };

        private const string ARCHIVE_ROOT = @"V:\归档";
        private static readonly string LEDGER_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\_dsh_tags_ledger.json");
        private static readonly string SHORTCUTS_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\_dsh_sidebar_shortcuts.json");

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
        private readonly Stack<NavHistoryEntry> _backStack = new Stack<NavHistoryEntry>();
        private readonly Stack<NavHistoryEntry> _forwardStack = new Stack<NavHistoryEntry>();
        private readonly List<SidebarShortcutEntry> _customShortcuts = new List<SidebarShortcutEntry>();
        private ContextMenuStrip _sidebarContextMenu;

        private enum SortField { Name, CreatedTime, ModifiedTime, Size, Tag }
        private SortField _sortField = SortField.Name;
        private bool _sortDescending = false;
        private bool _sortFoldersFirst = true;
        private bool _autoSyncSystemFolderSort = true;
        private Button _btnSortMode;
        private Button _btnSortDir;

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
        private static readonly string[] SCREENBOX_CANDIDATE_PATHS = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ScreenboxLite\ScreenboxLite.exe"),
            @"C:\Users\Administrator\AppData\Local\Programs\ScreenboxLite\ScreenboxLite.exe",
            @"F:\QQ\ScreenboxLite_Release\ScreenboxLite.exe"
        };

        private static string ResolveScreenboxLitePath()
        {
            foreach (string p in SCREENBOX_CANDIDATE_PATHS)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        public static List<TagSpec> ResolveTagSpecs(List<string> uids)
        {
            List<TagSpec> res = new List<TagSpec>();
            if (uids == null || uids.Count == 0) return res;
            foreach (TagSpec s in ORDERED_TAGS)
            {
                if (uids.Contains(s.Uid)) res.Add(s);
            }
            return res;
        }

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
            catch { }

            LoadLedgerFromDisk();
            LoadShortcutsFromDisk();
            BuildUI();
            BuildContextMenu();
            SetupLedgerWatcher();

            string startDir = !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)
                ? initialPath
                : (Directory.Exists(@"F:\Openclow\画师素材库")
                    ? @"F:\Openclow\画师素材库"
                    : (Directory.Exists(@"F:\")
                        ? @"F:\"
                        : (Directory.Exists(@"D:\") ? @"D:\" : Environment.GetFolderPath(Environment.SpecialFolder.Desktop))));
            NavigateTo(startDir, false);
        }

        private float _dpiScale = 1.0f;
        private int S(int v) { return (int)Math.Round(v * _dpiScale); }

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

            Label legend = new Label
            {
                Text = "Ctrl+Q/W/E/R [红/橙/黄/绿]  |  Ctrl+T [归档 V:\\归档]  |  长按左键 [框选/拖拽]  |  右键 [菜单/排序]",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 205, 70),
                Location = new Point(S(932), S(18))
            };

            topBar.Controls.AddRange(new Control[] { btnBack, btnForward, btnUp, btnRefresh, _addressBox, _btnSortMode, _btnSortDir, legend });

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

            Button btnAddShortcut = CreateNavButton("＋添加", S(62), S(26), (sender, e) => AddCurrentOrSelectedFolderToShortcuts());
            btnAddShortcut.Location = new Point(S(102), S(3));
            btnAddShortcut.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);

            Button btnRemoveShortcut = CreateNavButton("－移除", S(62), S(26), (sender, e) => RemoveSelectedSidebarShortcut());
            btnRemoveShortcut.Location = new Point(S(168), S(3));
            btnRemoveShortcut.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);

            sidebarHeader.Controls.Add(sidebarTitle);
            sidebarHeader.Controls.Add(btnAddShortcut);
            sidebarHeader.Controls.Add(btnRemoveShortcut);

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

            _videoHoverDebounceTimer = new System.Windows.Forms.Timer { Interval = 85 };
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
                _videoHoverPopup.ShowOrUpdatePreview(_pendingHoverVideoPath, _lastHoverScreenPt);
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
            sortMenu.DropDownItems.Add("🔄 自动跟随系统文件夹当前排序 (开关)", null, (s, e) =>
            {
                _autoSyncSystemFolderSort = !_autoSyncSystemFolderSort;
                if (_autoSyncSystemFolderSort) TryApplyExplorerSortForDirectory(_currentDirectory);
                RefreshCurrentDirectory(_grid.ScrollOffset, _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null);
            });
            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            sortMenu.DropDownItems.Add("🔤 按名称排序 (自然序)", null, (s, e) => ApplySort(SortField.Name, _sortDescending, true));
            sortMenu.DropDownItems.Add("📅 按创建时间排序", null, (s, e) => ApplySort(SortField.CreatedTime, _sortDescending, true));
            sortMenu.DropDownItems.Add("🕒 按修改时间排序", null, (s, e) => ApplySort(SortField.ModifiedTime, _sortDescending, true));
            sortMenu.DropDownItems.Add("📏 按文件大小排序", null, (s, e) => ApplySort(SortField.Size, _sortDescending, true));
            sortMenu.DropDownItems.Add("🏷️ 按标签颜色优先排序 (红>橙>黄>绿)", null, (s, e) => ApplySort(SortField.Tag, _sortDescending, true));
            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            sortMenu.DropDownItems.Add("↑ 升序排列 (Ascending)", null, (s, e) => ApplySort(_sortField, false, true));
            sortMenu.DropDownItems.Add("↓ 降序排列 (Descending)", null, (s, e) => ApplySort(_sortField, true, true));
            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            sortMenu.DropDownItems.Add("📁 切换「文件夹始终置顶 / 混合排列」", null, (s, e) =>
            {
                _sortFoldersFirst = !_sortFoldersFirst;
                RefreshCurrentDirectory(_grid.ScrollOffset, _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null);
            });
            _contextMenu.Items.Add(sortMenu);
            _contextMenu.Items.Add(new ToolStripSeparator());

            _contextMenu.Items.Add("🔴 切换【红】标记 (Ctrl+Q)", null, (s, e) => TriggerTagOnSelection((int)Keys.Q));
            _contextMenu.Items.Add("🟠 切换【橙】标记 (Ctrl+W)", null, (s, e) => TriggerTagOnSelection((int)Keys.W));
            _contextMenu.Items.Add("🟡 切换【黄】标记 (Ctrl+E)", null, (s, e) => TriggerTagOnSelection((int)Keys.E));
            _contextMenu.Items.Add("🟢 切换【绿】标记 (Ctrl+R)", null, (s, e) => TriggerTagOnSelection((int)Keys.R));
            _contextMenu.Items.Add("📦 移入 / 复原 V:\\归档 (Ctrl+T)", null, (s, e) => TriggerTagOnSelection((int)Keys.T));
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
            Point screenPt = _grid.PointToScreen(clientPt);
            _contextMenu.Show(screenPt);
        }

        private void CycleSortField()
        {
            SortField next = SortField.Name;
            if (_sortField == SortField.Name) next = SortField.CreatedTime;
            else if (_sortField == SortField.CreatedTime) next = SortField.ModifiedTime;
            else if (_sortField == SortField.ModifiedTime) next = SortField.Size;
            else if (_sortField == SortField.Size) next = SortField.Tag;
            else next = SortField.Name;
            ApplySort(next, _sortDescending, true);
        }

        private void ToggleSortDirection()
        {
            ApplySort(_sortField, !_sortDescending, true);
        }

        private void UpdateSortButtonsUI()
        {
            string fieldLabel = "名称";
            if (_sortField == SortField.CreatedTime) fieldLabel = "创建时间";
            else if (_sortField == SortField.ModifiedTime) fieldLabel = "修改时间";
            else if (_sortField == SortField.Size) fieldLabel = "大小";
            else if (_sortField == SortField.Tag) fieldLabel = "标签";

            if (_btnSortMode != null) _btnSortMode.Text = "⇅ 排序: " + fieldLabel;
            if (_btnSortDir != null) _btnSortDir.Text = _sortDescending ? "↓ 降序" : "↑ 升序";
        }

        private void ApplySort(SortField field, bool descending, bool refreshNow = true)
        {
            _sortField = field;
            _sortDescending = descending;
            UpdateSortButtonsUI();

            if (refreshNow)
            {
                string curSel = _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null;
                RefreshCurrentDirectory(0, curSel);
            }
        }

        private static string TryDetectExplorerSortColumns(string targetDir)
        {
            if (string.IsNullOrEmpty(targetDir)) return null;
            string normTarget = targetDir.Trim().TrimEnd('\\', '/');
            if (normTarget.Length == 2 && normTarget[1] == ':') normTarget += "\\";
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return null;

                int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null));
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object win = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;
                        object doc = win.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, win, null);
                        if (doc == null) continue;
                        object folder = doc.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, doc, null);
                        if (folder == null) continue;
                        object self = folder.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
                        if (self == null) continue;
                        string folderPath = self.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
                        if (string.IsNullOrEmpty(folderPath)) continue;
                        string normFolder = folderPath.Trim().TrimEnd('\\', '/');
                        if (normFolder.Length == 2 && normFolder[1] == ':') normFolder += "\\";
                        if (!string.Equals(normFolder, normTarget, StringComparison.OrdinalIgnoreCase)) continue;

                        string sortCols = doc.GetType().InvokeMember("SortColumns", System.Reflection.BindingFlags.GetProperty, null, doc, null) as string;
                        if (!string.IsNullOrWhiteSpace(sortCols)) return sortCols.Trim();
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private bool TryApplyExplorerSortForDirectory(string dir)
        {
            string spec = TryDetectExplorerSortColumns(dir);
            if (string.IsNullOrWhiteSpace(spec)) return false;

            if (spec.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
            {
                spec = spec.Substring(5);
            }
            int semi = spec.IndexOf(';');
            string primary = (semi >= 0 ? spec.Substring(0, semi) : spec).Trim();
            if (string.IsNullOrEmpty(primary)) return false;

            bool desc = false;
            string propName = primary;
            if (primary.StartsWith("-"))
            {
                desc = true;
                propName = primary.Substring(1).Trim();
            }
            else if (primary.StartsWith("+"))
            {
                desc = false;
                propName = primary.Substring(1).Trim();
            }

            SortField detectedField = SortField.Name;
            if (propName.IndexOf("DateCreated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.CreatedTime;
            }
            else if (propName.IndexOf("DateModified", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("System.Date", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("DateAccessed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.ModifiedTime;
            }
            else if (propName.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.Size;
            }
            else
            {
                detectedField = SortField.Name;
            }

            _sortField = detectedField;
            _sortDescending = desc;
            UpdateSortButtonsUI();
            return true;
        }

        private int CompareEntries(EntryItem a, EntryItem b)
        {
            if (_sortFoldersFirst && a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            int cmp = 0;
            switch (_sortField)
            {
                case SortField.CreatedTime:
                    cmp = a.CreatedAt.CompareTo(b.CreatedAt);
                    break;
                case SortField.ModifiedTime:
                    cmp = a.ModifiedAt.CompareTo(b.ModifiedAt);
                    break;
                case SortField.Size:
                    cmp = a.SizeBytes.CompareTo(b.SizeBytes);
                    break;
                case SortField.Tag:
                    int rankA = GetPrimaryTagRank(a.TagUids);
                    int rankB = GetPrimaryTagRank(b.TagUids);
                    cmp = rankA.CompareTo(rankB);
                    break;
            }

            if (cmp == 0)
            {
                cmp = NaturalCompare(a.Name, b.Name);
            }
            return _sortDescending ? -cmp : cmp;
        }

        private static int GetPrimaryTagRank(List<string> uids)
        {
            if (uids == null || uids.Count == 0) return 99;
            for (int i = 0; i < ORDERED_TAGS.Length; i++)
            {
                if (uids.Contains(ORDERED_TAGS[i].Uid)) return i;
            }
            return 99;
        }

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

        private List<SidebarShortcutEntry> GetDefaultSidebarShortcuts()
        {
            return new List<SidebarShortcutEntry>
            {
                new SidebarShortcutEntry { Label = "🎨 画师素材库 (F:\\Openclow)", TargetPath = @"F:\Openclow\画师素材库" },
                new SidebarShortcutEntry { Label = "📦 归档库 (V:\\归档)", TargetPath = ARCHIVE_ROOT },
                new SidebarShortcutEntry { Label = "📁 DeepSeek H 工作区", TargetPath = @"F:\DeepSeek H" },
                new SidebarShortcutEntry { Label = "💻 桌面 (Desktop)", TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new SidebarShortcutEntry { Label = "💿 C:\\ 盘", TargetPath = @"C:\" },
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
            _sidebarContextMenu.Items.Add("✏️ 重命名此快捷路径", null, (s, e) => RenameSelectedSidebarShortcut());
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
                if (string.Equals(entry.TargetPath, ARCHIVE_ROOT, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(ARCHIVE_ROOT))
                {
                    try { Directory.CreateDirectory(ARCHIVE_ROOT); } catch { }
                }
                if (Directory.Exists(entry.TargetPath))
                {
                    NavigateTo(entry.TargetPath, true);
                }
            }
        }

        private void ShowTaggedItemsView(TagSpec filterSpec)
        {
            _activeTagFilter = filterSpec != null ? filterSpec.Name : "ALL";
            _addressBox.Text = filterSpec != null ? "【标签筛选：" + filterSpec.Name + "】" : "【全部已标记文件与文件夹】";

            List<EntryItem> results = new List<EntryItem>();
            lock (_ledgerLock)
            {
                foreach (var kv in _ledgerMap)
                {
                    string path = kv.Key;
                    List<string> uids = kv.Value;
                    if (uids == null || uids.Count == 0) continue;
                    if (filterSpec != null && !uids.Contains(filterSpec.Uid)) continue;

                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    EntryItem item = new EntryItem
                    {
                        FullPath = path,
                        Name = Path.GetFileName(path),
                        IsFolder = isDir,
                        TagUids = new List<string>(uids),
                        CoverImagePath = isDir ? null : path
                    };
                    results.Add(item);
                }
            }
            _grid.SetItems(results);
            _statusLabel.Text = string.Format("筛选视图：共 {0} 个已标记项", results.Count);
        }

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

            if (_autoSyncSystemFolderSort)
            {
                TryApplyExplorerSortForDirectory(_currentDirectory);
            }

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

        private static readonly Regex NATURAL_RE = new Regex(@"(\d+)", RegexOptions.Compiled);

        public static int NaturalCompare(string a, string b)
        {
            string[] pa = NATURAL_RE.Split(a ?? "");
            string[] pb = NATURAL_RE.Split(b ?? "");
            int len = Math.Min(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                if (pa[i] == pb[i]) continue;
                long na, nb;
                if (long.TryParse(pa[i], out na) && long.TryParse(pb[i], out nb))
                {
                    int cmp = na.CompareTo(nb);
                    if (cmp != 0) return cmp;
                }
                int scmp = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
                if (scmp != 0) return scmp;
            }
            return pa.Length.CompareTo(pb.Length);
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
            try
            {
                DirectoryInfo di = new DirectoryInfo(_currentDirectory);
                bool needStat = (_sortField == SortField.CreatedTime || _sortField == SortField.ModifiedTime || _sortField == SortField.Size);
                FileSystemInfo[] entries = di.GetFileSystemInfos();

                foreach (FileSystemInfo fsi in entries)
                {
                    string name = fsi.Name;
                    if (name.StartsWith(".") || name == "_标签索引") continue;
                    bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;

                    if (isDir)
                    {
                        if ((fsi.Attributes & FileAttributes.Hidden) != 0 && fsi.FullName != "V:\\归档") continue;
                        list.Add(new EntryItem
                        {
                            FullPath = fsi.FullName,
                            Name = name,
                            IsFolder = true,
                            CreatedAt = needStat ? fsi.CreationTimeUtc : DateTime.MinValue,
                            ModifiedAt = needStat ? fsi.LastWriteTimeUtc : DateTime.MinValue,
                            TagUids = GetTagsForPath(fsi.FullName),
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
                            TagUids = GetTagsForPath(fsi.FullName),
                            CoverImagePath = isMedia ? fsi.FullName : null
                        });
                    }
                }
                list.Sort(CompareEntries);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "读取目录出错: " + ex.Message;
            }

            _grid.SetItems(list, restoreScrollOffset, restoreSelectedPath);
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

        private void OnItemHovered(EntryItem item, Point cursorScreenPt)
        {
            _lastHoverScreenPt = cursorScreenPt;
            if (item == null || item.IsFolder || !VirtualGridCanvas.IsVideoExtension(Path.GetExtension(item.FullPath)))
            {
                HideVideoHoverPreview();
                return;
            }

            if (_videoHoverPopup != null && _videoHoverPopup.IsVisible &&
                string.Equals(_videoHoverPopup.CurrentVideoPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _videoHoverPopup.UpdatePopupPosition(cursorScreenPt);
                return;
            }

            if (!string.Equals(_pendingHoverVideoPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _pendingHoverVideoPath = item.FullPath;
                if (_videoHoverPopup != null && _videoHoverPopup.IsVisible)
                {
                    // Immediately switch to the newly hovered video without flicker
                    _videoHoverDebounceTimer.Stop();
                    _videoHoverPopup.ShowOrUpdatePreview(_pendingHoverVideoPath, cursorScreenPt);
                }
                else
                {
                    _videoHoverDebounceTimer.Stop();
                    _videoHoverDebounceTimer.Start();
                }
            }
        }

        private void HideVideoHoverPreview()
        {
            _pendingHoverVideoPath = null;
            if (_videoHoverDebounceTimer != null) _videoHoverDebounceTimer.Stop();
            if (_videoHoverPopup != null && _videoHoverPopup.IsVisible)
            {
                _videoHoverPopup.HidePreview();
            }
        }

        private void OnItemOpened(EntryItem item)
        {
            HideVideoHoverPreview();
            if (item == null) return;
            if (item.IsFolder)
            {
                NavigateTo(item.FullPath, true);
            }
            else
            {
                string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
                if (Array.IndexOf(IMAGE_EXTS, ext) >= 0)
                {
                    FastImageViewerForm viewer = new FastImageViewerForm(item.FullPath);
                    viewer.Show(this);
                }
                else if (VirtualGridCanvas.IsVideoExtension(ext))
                {
                    try
                    {
                        string screenboxExe = ResolveScreenboxLitePath();
                        if (!string.IsNullOrEmpty(screenboxExe))
                        {
                            string sortProp = "System.ItemNameDisplay";
                            if (_sortField == SortField.CreatedTime) sortProp = "System.DateCreated";
                            else if (_sortField == SortField.ModifiedTime) sortProp = "System.DateModified";
                            else if (_sortField == SortField.Size) sortProp = "System.Size";
                            string sortSpec = "prop:" + (_sortDescending ? "-" : "") + sortProp + ";";

                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = screenboxExe,
                                Arguments = "\"" + item.FullPath + "\"",
                                UseShellExecute = false
                            };
                            psi.EnvironmentVariables["DSH_GALLERY_SORT_SPEC"] = sortSpec;
                            Process.Start(psi);
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                    }
                    catch { }
                }
            }
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

        private void OnHotkeyActionTriggered(List<EntryItem> items, int vk)
        {
            if (items == null || items.Count == 0) return;
            Point cursorClient = _grid.PointToClient(Cursor.Position);

            if (vk == (int)Keys.T)
            {
                ExecuteBatchArchiveToggle(items, cursorClient);
                return;
            }

            TagSpec spec = null;
            if (vk == (int)Keys.Q) spec = ORDERED_TAGS[0];
            else if (vk == (int)Keys.W) spec = ORDERED_TAGS[1];
            else if (vk == (int)Keys.E) spec = ORDERED_TAGS[2];
            else if (vk == (int)Keys.R) spec = ORDERED_TAGS[3];
            if (spec == null) return;

            // If ANY selected item lacks the tag, add it to all selected items; otherwise remove from all!
            bool shouldAdd = false;
            foreach (EntryItem it in items)
            {
                if (!it.TagUids.Contains(spec.Uid)) { shouldAdd = true; break; }
            }

            List<KeyValuePair<string, List<string>>> persistList = new List<KeyValuePair<string, List<string>>>();

            foreach (EntryItem item in items)
            {
                if (shouldAdd)
                {
                    if (!item.TagUids.Contains(spec.Uid)) item.TagUids.Add(spec.Uid);
                }
                else
                {
                    item.TagUids.Remove(spec.Uid);
                }

                lock (_ledgerLock)
                {
                    if (item.TagUids.Count == 0)
                        _ledgerMap.Remove(item.FullPath);
                    else
                        _ledgerMap[item.FullPath] = new List<string>(item.TagUids);
                }
                persistList.Add(new KeyValuePair<string, List<string>>(item.FullPath, new List<string>(item.TagUids)));
            }

            string title = shouldAdd
                ? string.Format("＋ 已标记【{0}】({1} 项)", spec.Name, items.Count)
                : string.Format("－ 已取消【{0}】标记 ({1} 项)", spec.Name, items.Count);
            string sub = items.Count == 1 ? items[0].Name : (items[0].Name + " 等 " + items.Count + " 项");
            _grid.ShowInstantHud(cursorClient, title, sub, spec.Color);
            OnSelectionChanged();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var kv in persistList)
                {
                    WriteAdsUids(kv.Key, kv.Value);
                }
                SaveLedgerToDisk();
            });
        }

        private void ExecuteBatchArchiveToggle(List<EntryItem> items, Point cursorClient)
        {
            string normArchivePrefix = Path.GetFullPath(ARCHIVE_ROOT).TrimEnd('\\') + "\\";
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
                    string destPath = Path.Combine(ARCHIVE_ROOT, driveLetter + "盘", rel);
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
                    : string.Format("📦 已移入 V:\\归档 ({0} 项)", removedItems.Count);
                _grid.ShowInstantHud(cursorClient, title, lastDest, Color.FromArgb(0, 153, 188));
            }
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

        private List<string> GetTagsForPath(string fullPath)
        {
            lock (_ledgerLock)
            {
                List<string> uids;
                if (_ledgerMap.TryGetValue(fullPath, out uids) && uids != null && uids.Count > 0)
                    return new List<string>(uids);
            }
            return new List<string>();
        }

        private static List<string> ReadAdsUidsFromDisk(string targetPath)
        {
            List<string> list = new List<string>();
            string ads = targetPath + ":files";
            IntPtr h = CreateFileW(ads, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h != IntPtr.Zero && h != (IntPtr)(-1))
            {
                try
                {
                    byte[] buf = new byte[4096];
                    uint read;
                    if (ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero) && read > 0)
                    {
                        string s = Encoding.UTF8.GetString(buf, 0, (int)read);
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (s.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0)
                                list.Add(t.Uid);
                        }
                    }
                }
                finally { CloseHandle(h); }
            }
            return list;
        }

        private static void WriteAdsUids(string targetPath, List<string> uids)
        {
            uint origAttrs = GetFileAttributesW(targetPath);
            bool hadReadOnly = (origAttrs != 0xFFFFFFFF) && ((origAttrs & 1) != 0);
            if (hadReadOnly) SetFileAttributesW(targetPath, origAttrs & ~1u);

            try
            {
                string ads = targetPath + ":files";
                if (uids == null || uids.Count == 0)
                {
                    DeleteFileW(ads);
                    return;
                }
                StringBuilder sb = new StringBuilder("[");
                for (int i = 0; i < uids.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(uids[i]).Append("\"");
                }
                sb.Append("]");
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                IntPtr h = CreateFileW(ads, 0x40000000, 3, IntPtr.Zero, 2, 0x02000000, IntPtr.Zero);
                if (h != IntPtr.Zero && h != (IntPtr)(-1))
                {
                    try
                    {
                        uint written;
                        WriteFile(h, bytes, (uint)bytes.Length, out written, IntPtr.Zero);
                    }
                    finally { CloseHandle(h); }
                }
            }
            finally
            {
                if (hadReadOnly) SetFileAttributesW(targetPath, origAttrs);
            }
        }

        private void LoadLedgerFromDisk()
        {
            try
            {
                if (!File.Exists(LEDGER_PATH)) return;
                string json = File.ReadAllText(LEDGER_PATH, Encoding.UTF8);
                lock (_ledgerLock)
                {
                    _ledgerMap.Clear();
                    foreach (Match m in Regex.Matches(json, "\"([A-Za-z]:\\\\\\\\[^\"]+)\"\\s*:\\s*\\{[^\\}]*\"uids\"\\s*:\\s*\\[([^\\]]*)\\]"))
                    {
                        string rawPath = m.Groups[1].Value.Replace("\\\\", "\\");
                        string rawUids = m.Groups[2].Value;
                        List<string> uids = new List<string>();
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (rawUids.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0)
                                uids.Add(t.Uid);
                        }
                        if (uids.Count > 0 && (File.Exists(rawPath) || Directory.Exists(rawPath)))
                        {
                            _ledgerMap[rawPath] = uids;
                        }
                    }
                }
            }
            catch { }
        }

        private static volatile bool _savingLedger = false;
        private static readonly string TAG_INDEX_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\tags_index");

        private void SaveLedgerToDisk()
        {
            try
            {
                _savingLedger = true;
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n  \"items\": {");
                bool first = true;

                Dictionary<string, List<string>> byColor = new Dictionary<string, List<string>>();
                foreach (TagSpec t in ORDERED_TAGS) byColor[t.Name] = new List<string>();
                List<string> summaryLines = new List<string>();

                lock (_ledgerLock)
                {
                    foreach (KeyValuePair<string, List<string>> kv in _ledgerMap)
                    {
                        if (kv.Value == null || kv.Value.Count == 0) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        string escapedPath = kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        List<string> names = new List<string>();
                        List<string> rawNames = new List<string>();
                        List<string> uids = new List<string>();
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (kv.Value.Contains(t.Uid))
                            {
                                names.Add("\"" + t.Name + "\"");
                                rawNames.Add(t.Name);
                                uids.Add("\"" + t.Uid + "\"");
                                byColor[t.Name].Add(kv.Key);
                            }
                        }
                        if (rawNames.Count > 0)
                        {
                            summaryLines.Add("[" + string.Join(",", rawNames.ToArray()) + "]\t" + kv.Key);
                        }
                        sb.Append("\n    \"").Append(escapedPath).Append("\": {\n");
                        sb.Append("      \"tags\": [").Append(string.Join(", ", names.ToArray())).Append("],\n");
                        sb.Append("      \"uids\": [").Append(string.Join(", ", uids.ToArray())).Append("],\n");
                        sb.Append("      \"updatedAt\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\"\n    }");
                    }
                }
                sb.Append("\n  }\n}\n");
                string dir = Path.GetDirectoryName(LEDGER_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string ledgerJson = sb.ToString();
                File.WriteAllText(LEDGER_PATH, ledgerJson, Encoding.UTF8);

                // Seamless zero-latency sync into F:\Openclow\画师素材库\_标签索引 for instant Agent lookup!
                try
                {
                    if (!Directory.Exists(TAG_INDEX_DIR)) Directory.CreateDirectory(TAG_INDEX_DIR);
                    File.WriteAllText(Path.Combine(TAG_INDEX_DIR, "全部标记.json"), ledgerJson, Encoding.UTF8);
                    summaryLines.Sort(StringComparer.OrdinalIgnoreCase);
                    File.WriteAllText(Path.Combine(TAG_INDEX_DIR, "全部标记.txt"), string.Join("\n", summaryLines.ToArray()) + (summaryLines.Count > 0 ? "\n" : ""), Encoding.UTF8);

                    foreach (TagSpec t in ORDERED_TAGS)
                    {
                        List<string> paths = byColor[t.Name];
                        paths.Sort(StringComparer.OrdinalIgnoreCase);
                        File.WriteAllText(Path.Combine(TAG_INDEX_DIR, t.Name + ".txt"), string.Join("\n", paths.ToArray()) + (paths.Count > 0 ? "\n" : ""), Encoding.UTF8);

                        StringBuilder csb = new StringBuilder();
                        csb.Append("{\n  \"tag\": \"").Append(t.Name).Append("\",\n  \"color\": \"").Append(t.Hex).Append("\",\n  \"count\": ").Append(paths.Count).Append(",\n  \"items\": [");
                        for (int i = 0; i < paths.Count; i++)
                        {
                            if (i > 0) csb.Append(",");
                            csb.Append("\n    \"").Append(paths[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                        }
                        csb.Append("\n  ]\n}\n");
                        File.WriteAllText(Path.Combine(TAG_INDEX_DIR, t.Name + ".json"), csb.ToString(), Encoding.UTF8);
                    }
                }
                catch { }
            }
            catch { }
            finally
            {
                Thread.Sleep(80);
                _savingLedger = false;
            }
        }

        private void SetupLedgerWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(LEDGER_PATH);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                _ledgerWatcher = new FileSystemWatcher(dir, Path.GetFileName(LEDGER_PATH));
                _ledgerWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                _ledgerWatcher.Changed += (s, e) =>
                {
                    if (_savingLedger) return;
                    Thread.Sleep(60);
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            LoadLedgerFromDisk();
                            lock (_ledgerLock)
                            {
                                foreach (EntryItem item in _grid.Items)
                                {
                                    List<string> uids;
                                    item.TagUids = _ledgerMap.TryGetValue(item.FullPath, out uids) ? new List<string>(uids) : new List<string>();
                                }
                            }
                            _grid.Invalidate();
                        }));
                    }
                };
                _ledgerWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_addressBox.Focused) return;

            if (e.KeyCode == Keys.F5)
            {
                int curScroll = _grid.ScrollOffset;
                string curSel = _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null;
                _grid.ClearThumbnailCache();
                RefreshCurrentDirectory(curScroll, curSel);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F2)
            {
                RenameFocusedItem();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedItems();
                e.Handled = true;
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.N)
            {
                CreateNewFolderInCurrentDirectory();
                e.Handled = true;
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.C)
            {
                CopySelectedPathsToClipboard();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                CopyOrCutSelectionToClipboard(false);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.X)
            {
                CopyOrCutSelectionToClipboard(true);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.V)
            {
                PasteFromClipboardToCurrentDirectory();
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Left)
            {
                NavigateBack();
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Right)
            {
                NavigateForward();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Back)
            {
                NavigateBack();
                e.Handled = true;
            }
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [STAThread]
        public static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); }
            catch
            {
                try { SetProcessDpiAwareness(2); }
                catch { try { SetProcessDPIAware(); } catch { } }
            }

            try { timeBeginPeriod(1); } catch { }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string initPath = args != null && args.Length > 0 ? args[0] : null;
                Application.Run(new MainWindow(initPath));
            }
            finally
            {
                try { timeEndPeriod(1); } catch { }
            }
        }
    }
}
