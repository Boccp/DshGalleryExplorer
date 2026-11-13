// 看图器绘制：视口尺寸 32bppPArgb 缩放位图缓存（消除每帧大图重采样）+ LockBits 零 GetPixel 实时放大镜。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshGalleryExplorer
{
    internal sealed partial class FastImageViewerForm
    {
        // 视口级缩放缓存：仅在切换图片或窗口尺寸变化时执行一次 HighQualityBicubic，鼠标移动重绘降至 <1.5ms
        private Bitmap _scaledViewBitmap;
        private Bitmap _scaledViewSource;
        private int _scaledViewW;
        private int _scaledViewH;

        // 31x23 放大镜采样小位图复用
        private Bitmap _magSampleBmp;

        // 常驻 GDI 绘制资源
        private Font _tagFont;
        private Font _magFont;
        private Font _cardTitleFont;
        private Font _cardSubFont;
        private Font _toastFont;
        private SolidBrush _dimBrush;
        private SolidBrush _selFillBrush;
        private SolidBrush _bpFillBrush;
        private Pen _selBorderPen;
        private Pen _bpBorderPen;
        private Pen _handlePen;
        private Pen _aimPen;
        private SolidBrush _overlayDarkBg;
        private Pen _cyanBorderPen;
        private Pen _magCrossPen;
        private SolidBrush _cyanTextBrush;
        private SolidBrush _grayTextBrush;
        private StringFormat _ellipsisNoWrap;

        private void EnsureViewerPaintResources()
        {
            if (_tagFont != null) return;
            _tagFont = new Font("Consolas", 9.5f, FontStyle.Bold);
            _magFont = new Font("Consolas", 8.5f, FontStyle.Regular);
            _cardTitleFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _cardSubFont = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
            _toastFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

            _dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            _selFillBrush = new SolidBrush(Color.FromArgb(12, 0, 229, 255));
            _bpFillBrush = new SolidBrush(Color.FromArgb(22, 0, 229, 255));
            _selBorderPen = new Pen(Color.FromArgb(0, 229, 255), 2f);
            _bpBorderPen = new Pen(Color.FromArgb(0, 229, 255), 2f) { DashPattern = new[] { 4f, 3f } };
            _handlePen = new Pen(Color.FromArgb(0, 229, 255), 1.5f);
            _aimPen = new Pen(Color.FromArgb(165, 0, 229, 255), 1f) { DashPattern = new[] { 4f, 4f } };
            _overlayDarkBg = new SolidBrush(Color.FromArgb(238, 18, 22, 28));
            _cyanBorderPen = new Pen(Color.FromArgb(0, 229, 255), 1.4f);
            _magCrossPen = new Pen(Color.FromArgb(200, 0, 229, 255), 1f);
            _cyanTextBrush = new SolidBrush(Color.FromArgb(0, 229, 255));
            _grayTextBrush = new SolidBrush(Color.FromArgb(170, 175, 185));
            _ellipsisNoWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        }

        private void DisposeViewerPaintCache()
        {
            if (_scaledViewBitmap != null) { _scaledViewBitmap.Dispose(); _scaledViewBitmap = null; }
            _scaledViewSource = null;
            if (_magSampleBmp != null) { _magSampleBmp.Dispose(); _magSampleBmp = null; }

            if (_tagFont != null) { _tagFont.Dispose(); _tagFont = null; }
            if (_magFont != null) { _magFont.Dispose(); _magFont = null; }
            if (_cardTitleFont != null) { _cardTitleFont.Dispose(); _cardTitleFont = null; }
            if (_cardSubFont != null) { _cardSubFont.Dispose(); _cardSubFont = null; }
            if (_toastFont != null) { _toastFont.Dispose(); _toastFont = null; }
            if (_dimBrush != null) { _dimBrush.Dispose(); _dimBrush = null; }
            if (_selFillBrush != null) { _selFillBrush.Dispose(); _selFillBrush = null; }
            if (_bpFillBrush != null) { _bpFillBrush.Dispose(); _bpFillBrush = null; }
            if (_selBorderPen != null) { _selBorderPen.Dispose(); _selBorderPen = null; }
            if (_bpBorderPen != null) { _bpBorderPen.Dispose(); _bpBorderPen = null; }
            if (_handlePen != null) { _handlePen.Dispose(); _handlePen = null; }
            if (_aimPen != null) { _aimPen.Dispose(); _aimPen = null; }
            if (_overlayDarkBg != null) { _overlayDarkBg.Dispose(); _overlayDarkBg = null; }
            if (_cyanBorderPen != null) { _cyanBorderPen.Dispose(); _cyanBorderPen = null; }
            if (_magCrossPen != null) { _magCrossPen.Dispose(); _magCrossPen = null; }
            if (_cyanTextBrush != null) { _cyanTextBrush.Dispose(); _cyanTextBrush = null; }
            if (_grayTextBrush != null) { _grayTextBrush.Dispose(); _grayTextBrush = null; }
            if (_ellipsisNoWrap != null) { _ellipsisNoWrap.Dispose(); _ellipsisNoWrap = null; }
        }

        private Bitmap GetOrBuildScaledViewBitmap(int targetW, int targetH)
        {
            if (_scaledViewBitmap != null && ReferenceEquals(_scaledViewSource, _currentBitmap) &&
                _scaledViewW == targetW && _scaledViewH == targetH)
            {
                return _scaledViewBitmap;
            }
            if (_scaledViewBitmap != null)
            {
                _scaledViewBitmap.Dispose();
                _scaledViewBitmap = null;
            }
            Bitmap scaled = new Bitmap(targetW, targetH, PixelFormat.Format32bppPArgb);
            using (Graphics sg = Graphics.FromImage(scaled))
            {
                sg.CompositingMode = CompositingMode.SourceCopy;
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.SmoothingMode = SmoothingMode.None;
                sg.DrawImage(_currentBitmap, 0, 0, targetW, targetH);
            }
            _scaledViewBitmap = scaled;
            _scaledViewSource = _currentBitmap;
            _scaledViewW = targetW;
            _scaledViewH = targetH;
            return scaled;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (Perf.Measure("viewer.paint"))
            {
                PaintViewer(e);
            }
        }

        /// <summary>基准测试：把看图器画面渲染到任意 Graphics（与 OnPaint 同一路径）。</summary>
        internal void RenderForBench(Graphics g)
        {
            OnPaint(new PaintEventArgs(g, ClientRectangle));
        }

        /// <summary>基准测试：模拟进入截图模式并把鼠标放在指定位置（触发放大镜/十字线绘制）。</summary>
        internal void SimulateScreenshotModeForBench(Point mousePt)
        {
            _isScreenshotModeActive = true;
            _cropMode = CropSelectionMode.None;
            _currentMousePt = mousePt;
        }

        private void PaintViewer(PaintEventArgs e)
        {
            EnsureViewerPaintResources();
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_currentBitmap == null) return;

            RectangleF imgRect = GetDisplayedImageRect();
            if (imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            int drawX = (int)Math.Round(imgRect.X);
            int drawY = (int)Math.Round(imgRect.Y);
            int drawW = Math.Max(1, (int)Math.Round(imgRect.Width));
            int drawH = Math.Max(1, (int)Math.Round(imgRect.Height));

            Bitmap viewBmp = GetOrBuildScaledViewBitmap(drawW, drawH);
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.DrawImageUnscaled(viewBmp, drawX, drawY);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_isScreenshotModeActive)
            {
                bool hasBox = (_hasActiveSelection || _cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint || _cropMode == CropSelectionMode.ResizingOrMoving) &&
                              _currentSelectionRect.Width >= 2f && _currentSelectionRect.Height >= 2f;

                if (hasBox)
                {
                    RectangleF sel = _currentSelectionRect;
                    g.FillRectangle(_dimBrush, imgRect.Left, imgRect.Top, imgRect.Width, Math.Max(0f, sel.Top - imgRect.Top));
                    g.FillRectangle(_dimBrush, imgRect.Left, sel.Bottom, imgRect.Width, Math.Max(0f, imgRect.Bottom - sel.Bottom));
                    g.FillRectangle(_dimBrush, imgRect.Left, sel.Top, Math.Max(0f, sel.Left - imgRect.Left), sel.Height);
                    g.FillRectangle(_dimBrush, sel.Right, sel.Top, Math.Max(0f, imgRect.Right - sel.Right), sel.Height);

                    bool isBlueprint = (_cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint);
                    g.FillRectangle(isBlueprint ? _bpFillBrush : _selFillBrush, sel.X, sel.Y, sel.Width, sel.Height);
                    g.DrawRectangle(isBlueprint ? _bpBorderPen : _selBorderPen, sel.X, sel.Y, sel.Width, sel.Height);

                    if (!isBlueprint && _hasActiveSelection)
                    {
                        float cx = sel.Left + sel.Width * 0.5f;
                        float cy = sel.Top + sel.Height * 0.5f;
                        PointF[] pts = new[]
                        {
                            new PointF(sel.Left, sel.Top),
                            new PointF(cx, sel.Top),
                            new PointF(sel.Right, sel.Top),
                            new PointF(sel.Right, cy),
                            new PointF(sel.Right, sel.Bottom),
                            new PointF(cx, sel.Bottom),
                            new PointF(sel.Left, sel.Bottom),
                            new PointF(sel.Left, cy)
                        };
                        for (int i = 0; i < pts.Length; i++)
                        {
                            g.FillRectangle(Brushes.White, pts[i].X - 4f, pts[i].Y - 4f, 8f, 8f);
                            g.DrawRectangle(_handlePen, pts[i].X - 4f, pts[i].Y - 4f, 8f, 8f);
                        }
                    }

                    int realW = Math.Max(1, (int)Math.Round((sel.Width / imgRect.Width) * _currentBitmap.Width));
                    int realH = Math.Max(1, (int)Math.Round((sel.Height / imgRect.Height) * _currentBitmap.Height));
                    string sizeText = string.Format("{0} × {1} px", realW, realH);
                    SizeF ts = g.MeasureString(sizeText, _tagFont);
                    float tagW = ts.Width + 12f;
                    float tagH = ts.Height + 6f;
                    float tagX = ClampF(sel.Left, imgRect.Left, Math.Max(imgRect.Left, imgRect.Right - tagW));
                    float tagY = sel.Top - tagH - 6f;
                    if (tagY < 48f) tagY = sel.Top + 6f;

                    g.FillRectangle(_overlayDarkBg, tagX, tagY, tagW, tagH);
                    g.DrawRectangle(_cyanBorderPen, tagX, tagY, tagW, tagH);
                    g.DrawString(sizeText, _tagFont, Brushes.White, tagX + 6f, tagY + 3f);
                }

                if (_cropMode != CropSelectionMode.ResizingOrMoving && _currentMousePt.Y >= 44)
                {
                    g.DrawLine(_aimPen, 0, _currentMousePt.Y, ClientSize.Width, _currentMousePt.Y);
                    g.DrawLine(_aimPen, _currentMousePt.X, 42, _currentMousePt.X, ClientSize.Height);
                    DrawMagnifierOverlay(g, imgRect, _currentMousePt);
                }
            }

            if (_savedPreviewThumb != null)
            {
                int cardW = 360;
                int cardH = 66;
                int cardX = ClientSize.Width - cardW - 18;
                int cardY = ClientSize.Height - cardH - 18;
                _savedPreviewCardBounds = new Rectangle(cardX, cardY, cardW, cardH);

                g.FillRectangle(_overlayDarkBg, _savedPreviewCardBounds);
                g.DrawRectangle(_cyanBorderPen, _savedPreviewCardBounds);
                g.DrawImage(_savedPreviewThumb, cardX + 8, cardY + 7, 92, 52);
                g.DrawString("✔ 已自动保存截图（点击在文件夹中定位）", _cardTitleFont, _cyanTextBrush, cardX + 108, cardY + 8);
                g.DrawString(_savedPreviewFileName, _cardSubFont, Brushes.White, new RectangleF(cardX + 108, cardY + 28, cardW - 116, 18), _ellipsisNoWrap);
                g.DrawString(_savedPreviewFullPath, _cardSubFont, _grayTextBrush, new RectangleF(cardX + 108, cardY + 45, cardW - 116, 18), _ellipsisNoWrap);
            }
            else
            {
                _savedPreviewCardBounds = Rectangle.Empty;
            }

            if (!string.IsNullOrEmpty(_toastText) && DateTime.UtcNow < _toastUntil)
            {
                SizeF ts = g.MeasureString(_toastText, _toastFont);
                float tw = ts.Width + 28f;
                float th = ts.Height + 14f;
                float tx = (ClientSize.Width - tw) * 0.5f;
                float ty = ClientSize.Height - th - 26f;
                g.FillRectangle(_overlayDarkBg, tx, ty, tw, th);
                g.DrawRectangle(_cyanBorderPen, tx, ty, tw, th);
                g.DrawString(_toastText, _toastFont, Brushes.White, tx + 14f, ty + 7f);
            }
        }

        private void DrawMagnifierOverlay(Graphics g, RectangleF imgRect, Point mousePt)
        {
            if (_currentBitmap == null || imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            int bmpW = _currentBitmap.Width;
            int bmpH = _currentBitmap.Height;
            float normX = ClampF((mousePt.X - imgRect.Left) / imgRect.Width, 0f, 1f);
            float normY = ClampF((mousePt.Y - imgRect.Top) / imgRect.Height, 0f, 1f);
            int srcX = Math.Min(bmpW - 1, Math.Max(0, (int)Math.Round(normX * (bmpW - 1))));
            int srcY = Math.Min(bmpH - 1, Math.Max(0, (int)Math.Round(normY * (bmpH - 1))));

            int magW = 136;
            int magH = 142;
            int boxX = mousePt.X + 22;
            int boxY = mousePt.Y + 22;
            if (boxX + magW > ClientSize.Width - 12) boxX = mousePt.X - magW - 22;
            if (boxY + magH > ClientSize.Height - 12) boxY = mousePt.Y - magH - 22;
            if (boxY < 48) boxY = 48;

            g.FillRectangle(_overlayDarkBg, boxX, boxY, magW, magH);
            g.DrawRectangle(_cyanBorderPen, boxX, boxY, magW, magH);

            const int sampleW = 31;
            const int sampleH = 23;
            Rectangle zoomDest = new Rectangle(boxX + 6, boxY + 6, 124, 92);

            Color pxColor = SampleMagnifierPatchAndCenterColor(srcX, srcY, sampleW, sampleH);
            if (_magSampleBmp != null)
            {
                InterpolationMode prevInterp = g.InterpolationMode;
                PixelOffsetMode prevOffset = g.PixelOffsetMode;
                SmoothingMode prevSmooth = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_magSampleBmp, zoomDest, new Rectangle(0, 0, sampleW, sampleH), GraphicsUnit.Pixel);
                g.InterpolationMode = prevInterp;
                g.PixelOffsetMode = prevOffset;
                g.SmoothingMode = prevSmooth;
            }

            int cx = zoomDest.Left + zoomDest.Width / 2;
            int cy = zoomDest.Top + zoomDest.Height / 2;
            g.DrawLine(_magCrossPen, zoomDest.Left, cy, zoomDest.Right, cy);
            g.DrawLine(_magCrossPen, cx, zoomDest.Top, cx, zoomDest.Bottom);
            g.DrawRectangle(_magCrossPen, zoomDest);

            using (SolidBrush swatch = new SolidBrush(pxColor))
            {
                g.DrawString(string.Format("({0}, {1})", srcX, srcY), _magFont, Brushes.White, boxX + 6, boxY + 102);
                g.FillRectangle(swatch, boxX + 8, boxY + 121, 12, 12);
                g.DrawRectangle(Pens.LightGray, boxX + 8, boxY + 121, 12, 12);
                g.DrawString(string.Format("#{0:X2}{1:X2}{2:X2}", pxColor.R, pxColor.G, pxColor.B), _magFont, Brushes.LightGray, boxX + 25, boxY + 120);
            }
        }

        /// <summary>
        /// 仅通过 LockBits 锁定光标周围最多 31x23 像素的小矩形区域，一次性提取放大镜块与中心像素 RGB，
        /// 彻底消除 GetPixel 与对几十兆原图的整幅 GDI+ 扫描。
        /// </summary>
        private Color SampleMagnifierPatchAndCenterColor(int srcX, int srcY, int sampleW, int sampleH)
        {
            if (_magSampleBmp == null)
            {
                _magSampleBmp = new Bitmap(sampleW, sampleH, PixelFormat.Format32bppPArgb);
            }

            int bmpW = _currentBitmap.Width;
            int bmpH = _currentBitmap.Height;
            int reqLeft = srcX - sampleW / 2;
            int reqTop = srcY - sampleH / 2;
            int clipLeft = Math.Max(0, reqLeft);
            int clipTop = Math.Max(0, reqTop);
            int clipRight = Math.Min(bmpW, reqLeft + sampleW);
            int clipBottom = Math.Min(bmpH, reqTop + sampleH);
            int clipW = clipRight - clipLeft;
            int clipH = clipBottom - clipTop;

            Color center = Color.Black;
            if (clipW <= 0 || clipH <= 0) return center;

            BitmapData srcBd = null;
            BitmapData dstBd = null;
            try
            {
                srcBd = _currentBitmap.LockBits(new Rectangle(clipLeft, clipTop, clipW, clipH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                dstBd = _magSampleBmp.LockBits(new Rectangle(0, 0, sampleW, sampleH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);

                // 先把整个 31x23 目标清成不透明黑底（0xFF000000），边缘超出原图时自然呈黑边
                int[] blackRow = new int[sampleW];
                unchecked
                {
                    for (int x = 0; x < sampleW; x++) blackRow[x] = (int)0xFF000000;
                }
                for (int y = 0; y < sampleH; y++)
                {
                    Marshal.Copy(blackRow, 0, new IntPtr(dstBd.Scan0.ToInt64() + (long)y * dstBd.Stride), sampleW);
                }

                int dstOffsetX = clipLeft - reqLeft;
                int dstOffsetY = clipTop - reqTop;
                int[] rowBuf = new int[clipW];
                for (int r = 0; r < clipH; r++)
                {
                    IntPtr sRow = new IntPtr(srcBd.Scan0.ToInt64() + (long)r * srcBd.Stride);
                    IntPtr dRow = new IntPtr(dstBd.Scan0.ToInt64() + (long)(dstOffsetY + r) * dstBd.Stride + dstOffsetX * 4);
                    Marshal.Copy(sRow, rowBuf, 0, clipW);
                    Marshal.Copy(rowBuf, 0, dRow, clipW);
                }

                int cx = srcX - clipLeft;
                int cy = srcY - clipTop;
                if (cx >= 0 && cx < clipW && cy >= 0 && cy < clipH)
                {
                    int argb = Marshal.ReadInt32(new IntPtr(srcBd.Scan0.ToInt64() + (long)cy * srcBd.Stride + cx * 4));
                    int b = argb & 0xFF;
                    int g = (argb >> 8) & 0xFF;
                    int rCol = (argb >> 16) & 0xFF;
                    center = Color.FromArgb(255, rCol, g, b);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "放大镜采样异常");
            }
            finally
            {
                if (dstBd != null) _magSampleBmp.UnlockBits(dstBd);
                if (srcBd != null) _currentBitmap.UnlockBits(srcBd);
            }
            return center;
        }
    }
}
