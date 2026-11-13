// 网格绘制：卡片、缩略图、角标、标签、框选框、滚动条、HUD。
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
    internal sealed partial class VirtualGridCanvas
    {
        /// <summary>把整个网格绘制到任意 Graphics（基准测试离屏渲染用，与 OnPaint 完全同一路径）。</summary>
        internal void RenderForBench(Graphics g)
        {
            OnPaint(new PaintEventArgs(g, ClientRectangle));
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
            using (Perf.Measure("grid.paint"))
            {
                PaintGrid(e);
            }
        }

        private void PaintGrid(PaintEventArgs e)
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
}
