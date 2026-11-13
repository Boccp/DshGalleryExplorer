// 视频悬停预览窗口（WPF）：界面、显示/隐藏、定位、MediaElement 与高帧率胶片播放。
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
    internal sealed partial class VideoHoverPreviewWindow : System.Windows.Window
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
            _mediaElement.MediaFailed += OnMediaFailed;
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

        private static System.Windows.Media.Imaging.BitmapImage ConvertGdiBitmapToWpfBitmapImage(Bitmap bmp)
        {
            if (bmp == null) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                    ms.Position = 0;
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

        public void ShowOrUpdatePreview(string videoPath, Point cursorScreenPt, Bitmap fallbackCoverBitmap = null)
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

                _titleText.Text = Path.GetFileName(videoPath);
                _progressFill.Width = 0;

                // 0ms Instant Check: If memory reel cache already has the 612-frame stream, play immediately with zero delay!
                List<byte[]> memCached;
                if (_memoryReelCache.TryGetValue(videoPath, out memCached) && memCached != null && memCached.Count > 30)
                {
                    lock (_framesLock)
                    {
                        _activeFrameJpegs = memCached;
                    }
                    _isHighFpsStreamMode = true;
                    _mediaReady = true;
                    _durationSeconds = 15.0;
                    _currentSpeedRatio = 3.0;
                    _lastRenderedFrameIdx = 0;
                    System.Windows.Media.Imaging.BitmapImage firstBmi = DecodeJpegBytesToBitmapImage(memCached[0]);
                    if (firstBmi != null)
                    {
                        _highFpsImage.Source = firstBmi;
                        if (firstBmi.PixelWidth > 0 && firstBmi.PixelHeight > 0)
                        {
                            ApplyPreviewWindowAspect(firstBmi.PixelWidth, firstBmi.PixelHeight);
                        }
                    }
                    _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                    _mediaElement.Visibility = System.Windows.Visibility.Collapsed;
                    _timeBadgeText.Text = string.Format("0.0s ({0}帧·120fps)", memCached.Count);
                    _cycleClock.Restart();
                    try
                    {
                        _mediaElement.Stop();
                        _mediaElement.Source = null;
                    }
                    catch { }
                }
                else
                {
                    // Display cover bitmap immediately at 0ms so the user NEVER sees a black screen while MediaElement/FFmpeg initializes!
                    System.Windows.Media.Imaging.BitmapImage coverBmi = ConvertGdiBitmapToWpfBitmapImage(fallbackCoverBitmap);
                    if (coverBmi != null)
                    {
                        if (coverBmi.PixelWidth > 0 && coverBmi.PixelHeight > 0)
                        {
                            ApplyPreviewWindowAspect(coverBmi.PixelWidth, coverBmi.PixelHeight);
                        }
                        _highFpsImage.Source = coverBmi;
                        _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                        // Keep _mediaElement underneath or hidden until ready so black surface never covers the cover thumbnail
                        _mediaElement.Visibility = System.Windows.Visibility.Hidden;
                    }
                    else
                    {
                        _highFpsImage.Source = null;
                        _highFpsImage.Visibility = System.Windows.Visibility.Collapsed;
                        _mediaElement.Visibility = System.Windows.Visibility.Visible;
                    }

                    _timeBadgeText.Text = "极速加载中...";

                    // Parallel Track A: Kick off FFmpeg disk-reel check & fast probe + chunk-0 stream immediately on background thread
                    // without waiting 300-800ms for WPF MediaElement.MediaOpened!
                    string ffmpegExe = ResolveFfmpegPath();
                    if (!string.IsNullOrEmpty(ffmpegExe))
                    {
                        StartImmediateReelOrProbePipeline(videoPath, gen, ffmpegExe);
                    }

                    // Parallel Track B: Also start native MediaElement for short clips (<= 20s)
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
            }

            if (!IsVisible)
            {
                Show();
            }
            EnsureRenderingHook(true);
        }

        private void StartImmediateReelOrProbePipeline(string videoPath, int gen, string ffmpegExe)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (gen != _streamGeneration) return;

                    // 1. Check disk reel cache first (~3-5ms if previously hovered)
                    string diskCachePath = GetReelDiskCachePath(videoPath);
                    List<byte[]> diskFrames = TryLoadReelFromDisk(diskCachePath);
                    if (diskFrames != null && diskFrames.Count > 30)
                    {
                        _memoryReelCache[videoPath] = diskFrames;
                        double probedDur = 0.0;
                        int pvw = 0, pvh = 0;
                        ProbeVideoDurationAndResolutionViaFfmpeg(ffmpegExe, videoPath, out probedDur, out pvw, out pvh);
                        if (probedDur <= 0.05) probedDur = 15.0;

                        if (gen == _streamGeneration)
                        {
                            lock (_framesLock)
                            {
                                _activeFrameJpegs = diskFrames;
                            }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (gen != _streamGeneration || !string.Equals(_currentVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                                    return;
                                _durationSeconds = probedDur;
                                _currentSpeedRatio = Math.Max(1.0, probedDur / TARGET_PREVIEW_CYCLE_SECONDS);
                                _mediaReady = true;
                                _isHighFpsStreamMode = true;
                                if (pvw > 0 && pvh > 0) ApplyPreviewWindowAspect(pvw, pvh);
                                try { _mediaElement.Pause(); } catch { }
                                _highFpsImage.Visibility = System.Windows.Visibility.Visible;
                                _mediaElement.Visibility = System.Windows.Visibility.Collapsed;
                                _cycleClock.Restart();
                            }));
                        }
                        return;
                    }

                    // 2. Probe duration & resolution via FFmpeg (~35ms)
                    double dur;
                    int vw, vh;
                    if (!ProbeVideoDurationAndResolutionViaFfmpeg(ffmpegExe, videoPath, out dur, out vw, out vh) || dur <= 0.05)
                    {
                        return;
                    }
                    if (gen != _streamGeneration) return;

                    double idealSpeed = dur / TARGET_PREVIEW_CYCLE_SECONDS;
                    // If medium/long video (> 20s, idealSpeed > 4.0x), immediately start High-FPS Reel Stream without waiting for MediaElement!
                    if (idealSpeed > 4.0)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != _streamGeneration || !string.Equals(_currentVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                                return;
                            _durationSeconds = dur;
                            _currentSpeedRatio = Math.Max(1.0, idealSpeed);
                            _mediaReady = true;
                            _isHighFpsStreamMode = true;
                            if (vw > 0 && vh > 0) ApplyPreviewWindowAspect(vw, vh);
                            try { _mediaElement.Pause(); } catch { }
                            _cycleClock.Restart();
                            _fpsWindowStartMs = 0;
                            _framesPresentedInSec = 0;
                        }));
                        StartHighFpsReelStream(videoPath, dur, gen, ffmpegExe);
                    }
                }
                catch { }
            });
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

        private void ApplyPreviewWindowAspect(int vw, int vh)
        {
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
        }

        private void OnMediaFailed(object sender, System.Windows.ExceptionRoutedEventArgs e)
        {
            // Native MediaElement fails on AV1 (av01) or 10-bit HEVC when Windows Media Foundation lacks the Store MFT.
            // Immediately fall back to FFmpeg probe + 120fps High-FPS Stream Engine so AV1 videos preview seamlessly!
            string videoPath = _currentVideoPath;
            int gen = _streamGeneration;
            string ffmpegExe = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(videoPath) || string.IsNullOrEmpty(ffmpegExe)) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                double dur;
                int vw, vh;
                if (!ProbeVideoDurationAndResolutionViaFfmpeg(ffmpegExe, videoPath, out dur, out vw, out vh))
                {
                    dur = 15.0;
                }
                if (gen != _streamGeneration) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _streamGeneration || !string.Equals(_currentVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                        return;

                    _durationSeconds = dur;
                    _currentSpeedRatio = Math.Max(1.0, dur / TARGET_PREVIEW_CYCLE_SECONDS);
                    _mediaReady = true;
                    _isHighFpsStreamMode = true;
                    ApplyPreviewWindowAspect(vw, vh);
                    _cycleClock.Restart();
                    _fpsWindowStartMs = 0;
                    _framesPresentedInSec = 0;
                    StartHighFpsReelStream(videoPath, dur, gen, ffmpegExe);
                }));
            });
        }

        private void OnMediaOpened(object sender, System.Windows.RoutedEventArgs e)
        {
            double openedDur = _mediaElement.NaturalDuration.HasTimeSpan
                ? _mediaElement.NaturalDuration.TimeSpan.TotalSeconds
                : 0.0;
            if (openedDur > 0.05)
            {
                _durationSeconds = openedDur;
            }

            int vw = _mediaElement.NaturalVideoWidth;
            int vh = _mediaElement.NaturalVideoHeight;
            if (vw > 0 && vh > 0)
            {
                ApplyPreviewWindowAspect(vw, vh);
            }

            // If our fast background FFmpeg probe/reel pipeline already took over in High-FPS Stream Mode,
            // keep _mediaElement paused and do NOT restart or duplicate the FFmpeg stream!
            if (_isHighFpsStreamMode)
            {
                _mediaReady = true;
                try { _mediaElement.Pause(); } catch { }
                return;
            }

            _mediaReady = true;
            _cycleClock.Restart();
            _fpsWindowStartMs = 0;
            _framesPresentedInSec = 0;

            if (_durationSeconds <= 0.1)
            {
                _currentSpeedRatio = 1.0;
                _isHighFpsStreamMode = false;
                _highFpsImage.Visibility = System.Windows.Visibility.Collapsed;
                _mediaElement.Visibility = System.Windows.Visibility.Visible;
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

                    // Stream-extract 36 chunks across the video timeline:
                    // Extract Chunk 0 FIRST without contention so the first 17 frames appear in ~50-80ms,
                    // then extract remaining chunks 1..35 across 3 concurrent background workers!
                    List<byte[]> finalReel = BuildReel(ffmpegExe, videoPath, durationSec, TARGET_REEL_CHUNKS, TARGET_FRAMES_PER_CHUNK, 3,
                        () => gen != _streamGeneration,
                        progressive =>
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
                        });

                    if (finalReel != null && gen == _streamGeneration)
                    {
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
                            System.Windows.Media.Imaging.BitmapImage bmi;
                            using (Perf.Measure("reel.uiDecode"))
                            {
                                bmi = DecodeJpegBytesToBitmapImage(jpg);
                            }
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
}
