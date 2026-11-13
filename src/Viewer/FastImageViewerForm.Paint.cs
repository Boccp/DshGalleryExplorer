// 看图器绘制：图像、截图模式遮罩/选框/放大镜、保存预览卡片、Toast。
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
    internal sealed partial class FastImageViewerForm
    {
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
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_currentBitmap == null) return;

            RectangleF imgRect = GetDisplayedImageRect();
            if (imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_currentBitmap, imgRect.X, imgRect.Y, imgRect.Width, imgRect.Height);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Screenshot Mode Overlay: Dim outside selection, crosshair, selection box, 8 handles, size tag, and live magnifier
            if (_isScreenshotModeActive)
            {
                bool hasBox = (_hasActiveSelection || _cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint || _cropMode == CropSelectionMode.ResizingOrMoving) &&
                              _currentSelectionRect.Width >= 2f && _currentSelectionRect.Height >= 2f;

                if (hasBox)
                {
                    RectangleF sel = _currentSelectionRect;
                    using (SolidBrush dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    {
                        // Dim 4 regions around selection inside image
                        g.FillRectangle(dimBrush, imgRect.Left, imgRect.Top, imgRect.Width, Math.Max(0f, sel.Top - imgRect.Top));
                        g.FillRectangle(dimBrush, imgRect.Left, sel.Bottom, imgRect.Width, Math.Max(0f, imgRect.Bottom - sel.Bottom));
                        g.FillRectangle(dimBrush, imgRect.Left, sel.Top, Math.Max(0f, sel.Left - imgRect.Left), sel.Height);
                        g.FillRectangle(dimBrush, sel.Right, sel.Top, Math.Max(0f, imgRect.Right - sel.Right), sel.Height);
                    }

                    bool isBlueprint = (_cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint);
                    using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(isBlueprint ? 22 : 12, 0, 229, 255)))
                    {
                        g.FillRectangle(fillBrush, sel.X, sel.Y, sel.Width, sel.Height);
                    }
                    using (Pen borderPen = new Pen(Color.FromArgb(0, 229, 255), 2f))
                    {
                        if (isBlueprint) borderPen.DashPattern = new float[] { 4f, 3f };
                        g.DrawRectangle(borderPen, sel.X, sel.Y, sel.Width, sel.Height);
                    }

                    // 8 Resize Handles
                    if (!isBlueprint && _hasActiveSelection)
                    {
                        float cx = sel.Left + sel.Width * 0.5f;
                        float cy = sel.Top + sel.Height * 0.5f;
                        PointF[] pts = new PointF[]
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
                        using (SolidBrush hFill = new SolidBrush(Color.White))
                        using (Pen hPen = new Pen(Color.FromArgb(0, 229, 255), 1.5f))
                        {
                            for (int i = 0; i < pts.Length; i++)
                            {
                                g.FillRectangle(hFill, pts[i].X - 4f, pts[i].Y - 4f, 8f, 8f);
                                g.DrawRectangle(hPen, pts[i].X - 4f, pts[i].Y - 4f, 8f, 8f);
                            }
                        }
                    }

                    // Real Image Pixel Dimensions Tag
                    int realW = Math.Max(1, (int)Math.Round((sel.Width / imgRect.Width) * _currentBitmap.Width));
                    int realH = Math.Max(1, (int)Math.Round((sel.Height / imgRect.Height) * _currentBitmap.Height));
                    string sizeText = string.Format("{0} × {1} px", realW, realH);
                    using (Font tagFont = new Font("Consolas", 9.5f, FontStyle.Bold))
                    {
                        SizeF ts = g.MeasureString(sizeText, tagFont);
                        float tagW = ts.Width + 12f;
                        float tagH = ts.Height + 6f;
                        float tagX = ClampF(sel.Left, imgRect.Left, Math.Max(imgRect.Left, imgRect.Right - tagW));
                        float tagY = sel.Top - tagH - 6f;
                        if (tagY < 48f) tagY = sel.Top + 6f;

                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(230, 18, 22, 28)))
                        using (Pen bp = new Pen(Color.FromArgb(0, 229, 255), 1f))
                        {
                            g.FillRectangle(bg, tagX, tagY, tagW, tagH);
                            g.DrawRectangle(bp, tagX, tagY, tagW, tagH);
                        }
                        g.DrawString(sizeText, tagFont, Brushes.White, tagX + 6f, tagY + 3f);
                    }
                }

                // Aiming Crosshair Lines & Live Magnifier when aiming or dragging
                if (_cropMode != CropSelectionMode.ResizingOrMoving && _currentMousePt.Y >= 44)
                {
                    using (Pen aimPen = new Pen(Color.FromArgb(165, 0, 229, 255), 1f))
                    {
                        aimPen.DashPattern = new float[] { 4f, 4f };
                        g.DrawLine(aimPen, 0, _currentMousePt.Y, ClientSize.Width, _currentMousePt.Y);
                        g.DrawLine(aimPen, _currentMousePt.X, 42, _currentMousePt.X, ClientSize.Height);
                    }

                    // Draw Live 4x Magnifier Card near cursor
                    DrawMagnifierOverlay(g, imgRect, _currentMousePt);
                }
            }

            // Saved Screenshot Preview Card (bottom-right)
            if (_savedPreviewThumb != null)
            {
                int cardW = 360;
                int cardH = 66;
                int cardX = ClientSize.Width - cardW - 18;
                int cardY = ClientSize.Height - cardH - 18;
                _savedPreviewCardBounds = new Rectangle(cardX, cardY, cardW, cardH);

                using (SolidBrush bg = new SolidBrush(Color.FromArgb(245, 24, 28, 34)))
                using (Pen bp = new Pen(Color.FromArgb(0, 229, 255), 1.5f))
                {
                    g.FillRectangle(bg, _savedPreviewCardBounds);
                    g.DrawRectangle(bp, _savedPreviewCardBounds);
                }
                g.DrawImage(_savedPreviewThumb, cardX + 8, cardY + 7, 92, 52);
                using (Pen tp = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
                {
                    g.DrawRectangle(tp, cardX + 8, cardY + 7, 92, 52);
                }
                using (Font fTitle = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                using (Font fSub = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular))
                using (SolidBrush cyanBrush = new SolidBrush(Color.FromArgb(0, 229, 255)))
                using (SolidBrush grayBrush = new SolidBrush(Color.FromArgb(170, 175, 185)))
                {
                    g.DrawString("✔ 已自动保存截图（点击在文件夹中定位）", fTitle, cyanBrush, cardX + 108, cardY + 8);
                    StringFormat sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    g.DrawString(_savedPreviewFileName, fSub, Brushes.White, new RectangleF(cardX + 108, cardY + 28, cardW - 116, 18), sf);
                    g.DrawString(_savedPreviewFullPath, fSub, grayBrush, new RectangleF(cardX + 108, cardY + 45, cardW - 116, 18), sf);
                }
            }
            else
            {
                _savedPreviewCardBounds = Rectangle.Empty;
            }

            // Status Toast Notification (bottom-center)
            if (!string.IsNullOrEmpty(_toastText) && DateTime.UtcNow < _toastUntil)
            {
                using (Font toastFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(_toastText, toastFont);
                    float tw = ts.Width + 28f;
                    float th = ts.Height + 14f;
                    float tx = (ClientSize.Width - tw) * 0.5f;
                    float ty = ClientSize.Height - th - 26f;
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(232, 18, 24, 30)))
                    using (Pen bp = new Pen(Color.FromArgb(0, 229, 255), 1.4f))
                    {
                        g.FillRectangle(bg, tx, ty, tw, th);
                        g.DrawRectangle(bp, tx, ty, tw, th);
                    }
                    g.DrawString(_toastText, toastFont, Brushes.White, tx + 14f, ty + 7f);
                }
            }
        }

        private void DrawMagnifierOverlay(Graphics g, RectangleF imgRect, Point mousePt)
        {
            if (_currentBitmap == null || imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            float normX = ClampF((mousePt.X - imgRect.Left) / imgRect.Width, 0f, 1f);
            float normY = ClampF((mousePt.Y - imgRect.Top) / imgRect.Height, 0f, 1f);
            int srcX = Math.Min(_currentBitmap.Width - 1, Math.Max(0, (int)Math.Round(normX * (_currentBitmap.Width - 1))));
            int srcY = Math.Min(_currentBitmap.Height - 1, Math.Max(0, (int)Math.Round(normY * (_currentBitmap.Height - 1))));

            int magW = 136;
            int magH = 142;
            int boxX = mousePt.X + 22;
            int boxY = mousePt.Y + 22;
            if (boxX + magW > ClientSize.Width - 12) boxX = mousePt.X - magW - 22;
            if (boxY + magH > ClientSize.Height - 12) boxY = mousePt.Y - magH - 22;
            if (boxY < 48) boxY = 48;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(242, 18, 20, 24)))
            using (Pen bp = new Pen(Color.FromArgb(0, 229, 255), 1.5f))
            {
                g.FillRectangle(bg, boxX, boxY, magW, magH);
                g.DrawRectangle(bp, boxX, boxY, magW, magH);
            }

            Rectangle zoomDest = new Rectangle(boxX + 6, boxY + 6, 124, 92);
            int sampleW = 31;
            int sampleH = 23;
            Rectangle sampleSrc = new Rectangle(srcX - sampleW / 2, srcY - sampleH / 2, sampleW, sampleH);

            GraphicsState state = g.Save();
            g.SetClip(zoomDest);
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_currentBitmap, zoomDest, sampleSrc, GraphicsUnit.Pixel);
            g.Restore(state);

            using (Pen chPen = new Pen(Color.FromArgb(200, 0, 229, 255), 1f))
            {
                int cx = zoomDest.Left + zoomDest.Width / 2;
                int cy = zoomDest.Top + zoomDest.Height / 2;
                g.DrawLine(chPen, zoomDest.Left, cy, zoomDest.Right, cy);
                g.DrawLine(chPen, cx, zoomDest.Top, cx, zoomDest.Bottom);
                g.DrawRectangle(chPen, zoomDest);
            }

            Color pxColor = Color.Black;
            try { pxColor = _currentBitmap.GetPixel(srcX, srcY); } catch { }

            using (Font f = new Font("Consolas", 8.5f, FontStyle.Regular))
            using (SolidBrush swatch = new SolidBrush(pxColor))
            using (Pen sp = new Pen(Color.FromArgb(160, 255, 255, 255), 1f))
            {
                g.DrawString(string.Format("({0}, {1})", srcX, srcY), f, Brushes.White, boxX + 6, boxY + 102);
                g.FillRectangle(swatch, boxX + 8, boxY + 121, 12, 12);
                g.DrawRectangle(sp, boxX + 8, boxY + 121, 12, 12);
                g.DrawString(string.Format("#{0:X2}{1:X2}{2:X2}", pxColor.R, pxColor.G, pxColor.B), f, Brushes.LightGray, boxX + 25, boxY + 120);
            }
        }
    }
}
