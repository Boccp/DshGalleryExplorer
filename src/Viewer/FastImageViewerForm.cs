// 快速看图器：打开/翻页/关闭等基础行为。
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
    internal sealed partial class FastImageViewerForm : Form
    {
        private enum CropSelectionMode
        {
            None,
            DraggingNew,
            TwoClickBlueprint,
            ResizingOrMoving
        }

        [Flags]
        private enum CropHitZone
        {
            None = 0,
            Left = 1,
            Top = 2,
            Right = 4,
            Bottom = 8,
            Inside = 16
        }

        private List<string> _imageList;
        private int _currentIndex;
        private Bitmap _currentBitmap;

        // Interactive Tilde (~) Screenshot Mode State (ported from ScreenboxLite)
        private bool _isScreenshotModeActive = false;
        private CropSelectionMode _cropMode = CropSelectionMode.None;
        private CropHitZone _activeResizeZone = CropHitZone.None;
        private bool _hasActiveSelection = false;
        private RectangleF _currentSelectionRect = RectangleF.Empty;
        private PointF _selectionStartPoint;
        private PointF _mouseDownOverlayPoint;
        private PointF _resizeStartMousePoint;
        private RectangleF _resizeStartSelectionRect;
        private Point _currentMousePt = Point.Empty;

        // Shared persistent settings with ScreenboxLite (%AppData%\ScreenboxLite\settings.ini)
        private readonly string _settingsFilePath;
        private string _saveFolderPath;
        private RectangleF? _lastNormalizedCropRect = null;

        // Top Bar Controls (only shown in Screenshot Mode, floating or docked without shifting image geometry)
        private Panel _topSaveBarPanel;
        private Label _saveLocationLabel;

        // Toast & Saved Screenshot Preview Card State
        private string _toastText = "";
        private DateTime _toastUntil = DateTime.MinValue;
        private Bitmap _savedPreviewThumb = null;
        private string _savedPreviewFileName = "";
        private string _savedPreviewFullPath = "";
        private DateTime _savedPreviewUntil = DateTime.MinValue;
        private Rectangle _savedPreviewCardBounds = Rectangle.Empty;
        private System.Windows.Forms.Timer _uiOverlayTimer;
        private long _lastTildeTriggerTick = 0;
        private long _lastKeyRTriggerTick = 0;

        public FastImageViewerForm(string currentImagePath, List<string> orderedImagePaths = null)
        {
            Text = Path.GetFileName(currentImagePath) + " — 按 ~ 键截图，Left/Right 翻页，Esc 或双击退出";
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size((int)(wa.Width * 0.88), (int)(wa.Height * 0.90));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(12, 14, 18);
            ForeColor = Color.White;
            DoubleBuffered = true;
            KeyPreview = true;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _settingsFilePath = Path.Combine(appData, "ScreenboxLite", "settings.ini");
            string defaultPicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _saveFolderPath = Path.Combine(defaultPicDir, "Screenshots");
            LoadScreenshotSettings();
            try
            {
                if (!Directory.Exists(_saveFolderPath))
                    Directory.CreateDirectory(_saveFolderPath);
            }
            catch { }

            BuildScreenshotSaveBar();

            _uiOverlayTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _uiOverlayTimer.Tick += (s, e) =>
            {
                DateTime now = DateTime.UtcNow;
                bool needRedraw = false;
                if (!string.IsNullOrEmpty(_toastText) && now >= _toastUntil)
                {
                    _toastText = "";
                    needRedraw = true;
                }
                if (_savedPreviewThumb != null && now >= _savedPreviewUntil)
                {
                    Bitmap oldThumb = _savedPreviewThumb;
                    _savedPreviewThumb = null;
                    _savedPreviewCardBounds = Rectangle.Empty;
                    if (oldThumb != null) oldThumb.Dispose();
                    needRedraw = true;
                }
                if (needRedraw)
                {
                    Invalidate();
                }
                if (string.IsNullOrEmpty(_toastText) && _savedPreviewThumb == null)
                {
                    _uiOverlayTimer.Stop();
                }
            };

            _imageList = new List<string>();
            if (orderedImagePaths != null && orderedImagePaths.Count > 0)
            {
                _imageList.AddRange(orderedImagePaths);
            }
            else
            {
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
            }
            _currentIndex = Math.Max(0, _imageList.FindIndex(p => string.Equals(p, currentImagePath, StringComparison.OrdinalIgnoreCase)));
            if (_imageList.Count == 0) _imageList.Add(currentImagePath);
            LoadCurrent();
        }

        private RectangleF GetDisplayedImageRect()
        {
            if (_currentBitmap == null) return RectangleF.Empty;
            int topReserve = 48;
            int sideReserve = 20;
            int bottomReserve = 20;
            int stageX = sideReserve / 2;
            int stageY = topReserve;
            int cw = ClientSize.Width - sideReserve;
            int ch = ClientSize.Height - topReserve - bottomReserve;
            if (cw <= 0 || ch <= 0) return RectangleF.Empty;
            float scale = Math.Min((float)cw / _currentBitmap.Width, (float)ch / _currentBitmap.Height);
            float dw = Math.Max(1f, _currentBitmap.Width * scale);
            float dh = Math.Max(1f, _currentBitmap.Height * scale);
            float dx = stageX + (cw - dw) * 0.5f;
            float dy = stageY + (ch - dh) * 0.5f;
            return new RectangleF(dx, dy, dw, dh);
        }

        private void ShowToast(string message)
        {
            _toastText = message ?? "";
            _toastUntil = DateTime.UtcNow.AddSeconds(2.5);
            if (!_uiOverlayTimer.Enabled) _uiOverlayTimer.Start();
            Invalidate();
        }

        private void LoadCurrent()
        {
            if (_currentIndex < 0 || _currentIndex >= _imageList.Count) return;
            string p = _imageList[_currentIndex];
            Bitmap bmp;
            using (Perf.Measure("viewer.load"))
            {
                bmp = VirtualGridCanvas.LoadAnyImageBitmap(p, 3840);
            }
            Bitmap old = _currentBitmap;
            _currentBitmap = bmp;
            if (old != null) old.Dispose();
            Text = string.Format("[{0}/{1}] {2}  (按 ~ 键截图，←/→ 键或滚轮翻页，Esc 或双击关闭)", _currentIndex + 1, _imageList.Count, Path.GetFileName(p));
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta < 0 && _currentIndex + 1 < _imageList.Count) { _currentIndex++; LoadCurrent(); }
            else if (e.Delta > 0 && _currentIndex > 0) { _currentIndex--; LoadCurrent(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_isScreenshotModeActive) return;
            if (_savedPreviewThumb != null && _savedPreviewCardBounds.Contains(e.Location)) return;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN)
            {
                Keys keyCode = keyData & Keys.KeyCode;
                int scanCode = (int)((msg.LParam.ToInt64() >> 16) & 0xFF);
                // Physical tilde (~ / `) key is Keys.Oemtilde (0xC0) or ScanCode 0x29 even when Chinese IME returns VK_PROCESSKEY (229)
                if (keyCode == Keys.Oemtilde || scanCode == 0x29)
                {
                    HandleTildeShortcut();
                    return true;
                }
                if (_isScreenshotModeActive && (keyCode == Keys.R || scanCode == 0x13))
                {
                    RestoreLastCropSelection();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Oemtilde)
            {
                HandleTildeShortcut();
                e.Handled = true;
                return;
            }
            if (_isScreenshotModeActive && e.KeyCode == Keys.R)
            {
                RestoreLastCropSelection();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (_isScreenshotModeActive)
                {
                    HandleRightClickBackInScreenshotMode();
                    e.Handled = true;
                    return;
                }
                Close();
                return;
            }
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Space)
            {
                if (!_isScreenshotModeActive) Close();
            }
            else if ((e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) && _currentIndex + 1 < _imageList.Count)
            {
                _currentIndex++;
                LoadCurrent();
            }
            else if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) && _currentIndex > 0)
            {
                _currentIndex--;
                LoadCurrent();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiOverlayTimer != null) { _uiOverlayTimer.Stop(); _uiOverlayTimer.Dispose(); _uiOverlayTimer = null; }
                if (_currentBitmap != null) { _currentBitmap.Dispose(); _currentBitmap = null; }
                if (_savedPreviewThumb != null) { _savedPreviewThumb.Dispose(); _savedPreviewThumb = null; }
            }
            base.Dispose(disposing);
        }
    }
}
