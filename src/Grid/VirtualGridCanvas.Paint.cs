// 网格绘制：预合成 32bppPArgb 圆角卡片/角标/标签胶囊模板 + 按 TileSize 预缩放缩略图 1:1 极速贴图。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace DshGalleryExplorer
{
    internal sealed partial class VirtualGridCanvas
    {
        // ── 常驻 GDI 资源（避免每帧分配） ──
        private Font _nameFont;
        private Font _badgeFont;
        private Font _videoExtFont;
        private Font _hudTitleFont;
        private Font _hudSubFont;
        private SolidBrush _textBrush;
        private SolidBrush _placeholderBg;
        private SolidBrush _marqueeFill;
        private Pen _marqueePen;
        private SolidBrush _scrollTrackBrush;
        private SolidBrush _scrollThumbBrush;
        private SolidBrush _scrollThumbDragBrush;
        private SolidBrush _hudBgBrush;
        private StringFormat _titleFormat;

        // ── 按当前 _tileSize 预合成的卡片模板（Format32bppRgb 不透明极速直拷，含四色标记外环）与角标缓存 ──
        private int _cachedCardTileSize = -1;
        private readonly Dictionary<long, Bitmap> _cardTemplates = new Dictionary<long, Bitmap>();
        private Bitmap _badgeCheckBmp;
        private Bitmap _badgeFolderBmp;
        private readonly Dictionary<string, Bitmap> _tagPillBmps = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private readonly Dictionary<string, Bitmap> _videoBadgeBmps = new Dictionary<string, Bitmap>(StringComparer.Ordinal);

        /// <summary>把整个网格绘制到任意 Graphics（基准测试离屏渲染用，与 OnPaint 完全同一路径）。</summary>
        internal void RenderForBench(Graphics g)
        {
            OnPaint(new PaintEventArgs(g, ClientRectangle));
        }

        private void InvalidateTileSurfaceCaches()
        {
            ClearScaledThumbnailCache();
            DisposeCardTemplates();
            _cachedCardTileSize = -1;
        }

        private void EnsurePaintResources()
        {
            if (_nameFont == null)
            {
                _nameFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
                _badgeFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
                _videoExtFont = new Font("Consolas", 8.8f, FontStyle.Bold);
                _hudTitleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
                _hudSubFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
                _textBrush = new SolidBrush(Color.FromArgb(232, 236, 242));
                _placeholderBg = new SolidBrush(Color.FromArgb(34, 38, 48));
                _marqueeFill = new SolidBrush(Color.FromArgb(65, 0, 130, 255));
                _marqueePen = new Pen(Color.FromArgb(220, 80, 175, 255), 1.8f);
                _scrollTrackBrush = new SolidBrush(Color.FromArgb(26, 29, 38));
                _scrollThumbBrush = new SolidBrush(Color.FromArgb(120, 132, 158));
                _scrollThumbDragBrush = new SolidBrush(Color.FromArgb(100, 175, 255));
                _hudBgBrush = new SolidBrush(Color.FromArgb(240, 20, 22, 28));
                _titleFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
            }

            if (_badgeCheckBmp == null)
            {
                _badgeCheckBmp = new Bitmap(24, 24, PixelFormat.Format32bppPArgb);
                using (Graphics cg = Graphics.FromImage(_badgeCheckBmp))
                using (SolidBrush checkFill = new SolidBrush(Color.FromArgb(0, 125, 250)))
                using (Pen checkPen = new Pen(Color.White, 1.8f))
                {
                    cg.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(1, 1, 22, 22);
                    DrawRoundedRect(cg, r, 5, checkFill, checkPen);
                    cg.DrawLines(checkPen, new[]
                    {
                        new Point(r.X + 5, r.Y + 11),
                        new Point(r.X + 9, r.Y + 16),
                        new Point(r.X + 17, r.Y + 6)
                    });
                }

                _badgeFolderBmp = new Bitmap(44, 34, PixelFormat.Format32bppPArgb);
                using (Graphics fg = Graphics.FromImage(_badgeFolderBmp))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(225, 20, 22, 28)))
                using (Pen pen = new Pen(Color.FromArgb(255, 200, 55), 1.8f))
                using (SolidBrush fgBrush = new SolidBrush(Color.FromArgb(255, 205, 60)))
                {
                    fg.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(1, 1, 42, 32);
                    DrawRoundedRect(fg, r, 7, bg, pen);
                    fg.FillRectangle(fgBrush, r.X + 8, r.Y + 7, 10, 4);
                    fg.FillRectangle(fgBrush, r.X + 8, r.Y + 10, 26, 15);
                }
            }

            if (_cachedCardTileSize != _tileSize)
            {
                DisposeCardTemplates();
                _cachedCardTileSize = _tileSize;
            }
        }

        private Bitmap GetCardTemplateBitmap(int stateKind, Color tagRingColor)
        {
            // stateKind: 0=Normal, 1=Hover, 2=Selected, 3=DropTarget
            long key = ((long)stateKind << 32) | (uint)tagRingColor.ToArgb();
            Bitmap bmp;
            if (!_cardTemplates.TryGetValue(key, out bmp) || bmp == null)
            {
                int cardW = _tileSize;
                int cardH = _tileSize + 44;
                Color fill = Color.FromArgb(26, 29, 37);
                Color borderColor = Color.Empty;
                float borderW = 0f;
                if (stateKind == 1) fill = Color.FromArgb(36, 40, 52);
                else if (stateKind == 2) { fill = Color.FromArgb(38, 62, 102); borderColor = Color.FromArgb(88, 175, 255); borderW = 2.5f; }
                else if (stateKind == 3) { fill = Color.FromArgb(30, 88, 65); borderColor = Color.FromArgb(40, 220, 120); borderW = 3.0f; }

                // Format32bppRgb 不带 Alpha 通道，GDI+ DrawImageUnscaled 直接走不透明扫描线拷贝，消除全卡片 Alpha 混合开销
                bmp = new Bitmap(cardW + 4, cardH + 4, PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(BackColor);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(2, 2, cardW, cardH);
                    using (SolidBrush b = new SolidBrush(fill))
                    using (Pen p = borderW > 0f ? new Pen(borderColor, borderW) : null)
                    {
                        DrawRoundedRect(g, r, 10, b, p);
                    }
                    if (tagRingColor != Color.Empty)
                    {
                        using (Pen tp = new Pen(tagRingColor, 3.5f))
                        {
                            DrawRoundedRect(g, r, 10, null, tp);
                        }
                    }
                }
                _cardTemplates[key] = bmp;
            }
            return bmp;
        }

        private Bitmap GetTagPillBitmap(TagSpec spec)
        {
            Bitmap bmp;
            if (!_tagPillBmps.TryGetValue(spec.Uid, out bmp) || bmp == null)
            {
                bmp = new Bitmap(48, 28, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                using (SolidBrush pb = new SolidBrush(spec.Color))
                using (Pen wp = new Pen(Color.White, 1.6f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    DrawRoundedRect(g, new Rectangle(1, 1, 46, 26), 6, pb, wp);
                    g.DrawString("●" + spec.Name, _badgeFont, Brushes.White, new PointF(5, 4));
                }
                _tagPillBmps[spec.Uid] = bmp;
            }
            return bmp;
        }

        private Bitmap GetVideoBadgeBitmap(string vExt)
        {
            Bitmap bmp;
            if (!_videoBadgeBmps.TryGetValue(vExt, out bmp) || bmp == null)
            {
                int bw = Math.Max(54, 24 + vExt.Length * 8);
                int bh = 26;
                bmp = new Bitmap(bw + 2, bh + 2, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                using (SolidBrush videoBadgeBg = new SolidBrush(Color.FromArgb(230, 14, 18, 26)))
                using (Pen videoBadgePen = new Pen(Color.FromArgb(0, 215, 255), 1.8f))
                using (SolidBrush videoBadgeFg = new SolidBrush(Color.FromArgb(0, 229, 255)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    Rectangle box = new Rectangle(1, 1, bw, bh);
                    DrawRoundedRect(g, box, 6, videoBadgeBg, videoBadgePen);
                    Point[] tri = new[]
                    {
                        new Point(box.X + 8, box.Y + 6),
                        new Point(box.X + 8, box.Bottom - 6),
                        new Point(box.X + 18, box.Y + bh / 2)
                    };
                    g.FillPolygon(videoBadgeFg, tri);
                    g.DrawString(vExt, _videoExtFont, videoBadgeFg, new PointF(box.X + 21, box.Y + 5));
                }
                _videoBadgeBmps[vExt] = bmp;
            }
            return bmp;
        }

        private Bitmap GetOrCreateScaledThumb(string fullPath, Bitmap src320, int thumbDim)
        {
            if (thumbDim <= 0) return src320;
            if (src320.Width == thumbDim && src320.Height == thumbDim && src320.PixelFormat == PixelFormat.Format32bppPArgb)
            {
                return src320;
            }
            if (_scaledThumbSize != thumbDim)
            {
                ClearScaledThumbnailCache();
                _scaledThumbSize = thumbDim;
            }
            Bitmap scaled;
            if (_scaledThumbCache.TryGetValue(fullPath, out scaled) && scaled != null)
            {
                return scaled;
            }
            // 超大目录滚动时限制预缩放缓存上限，防止无界增长
            if (_scaledThumbCache.Count > 900)
            {
                ClearScaledThumbnailCache();
            }
            scaled = ScalePArgbFast(src320, thumbDim);
            if (!_scaledThumbCache.TryAdd(fullPath, scaled))
            {
                scaled.Dispose();
                _scaledThumbCache.TryGetValue(fullPath, out scaled);
            }
            return scaled ?? src320;
        }

        /// <summary>
        /// 无锁多核定点双线性缩放（Format32bppPArgb 预乘空间精确插值），消除滚动首遇新瓦片时 GDI+ DrawImage 的 4~9ms/张阻塞。
        /// </summary>
        private static Bitmap ScalePArgbFast(Bitmap src, int dstDim)
        {
            int sw = src.Width;
            int sh = src.Height;
            int[] srcPx = new int[sw * sh];
            BitmapData sbd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (sbd.Stride == sw * 4)
                {
                    System.Runtime.InteropServices.Marshal.Copy(sbd.Scan0, srcPx, 0, srcPx.Length);
                }
                else
                {
                    for (int y = 0; y < sh; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(sbd.Scan0.ToInt64() + (long)y * sbd.Stride), srcPx, y * sw, sw);
                    }
                }
            }
            finally { src.UnlockBits(sbd); }

            int[] dstPx = new int[dstDim * dstDim];
            int xRatio = ((sw - 1) << 16) / Math.Max(1, dstDim - 1);
            int yRatio = ((sh - 1) << 16) / Math.Max(1, dstDim - 1);

            System.Threading.Tasks.Parallel.For(0, dstDim, dy =>
            {
                int syFixed = dy * yRatio;
                int sy = syFixed >> 16;
                int fy = (syFixed >> 8) & 0xFF;
                int ify = 256 - fy;
                int sy1 = sy + 1 < sh ? sy + 1 : sy;
                int row0 = sy * sw;
                int row1 = sy1 * sw;
                int dRow = dy * dstDim;

                int sxFixed = 0;
                for (int dx = 0; dx < dstDim; dx++)
                {
                    int sx = sxFixed >> 16;
                    int fx = (sxFixed >> 8) & 0xFF;
                    int ifx = 256 - fx;
                    int sx1 = sx + 1 < sw ? sx + 1 : sx;

                    uint c00 = (uint)srcPx[row0 + sx];
                    uint c10 = (uint)srcPx[row0 + sx1];
                    uint c01 = (uint)srcPx[row1 + sx];
                    uint c11 = (uint)srcPx[row1 + sx1];

                    uint w00 = (uint)(ifx * ify);
                    uint w10 = (uint)(fx * ify);
                    uint w01 = (uint)(ifx * fy);
                    uint w11 = (uint)(fx * fy);

                    uint b = ((c00 & 0xFFu) * w00 + (c10 & 0xFFu) * w10 + (c01 & 0xFFu) * w01 + (c11 & 0xFFu) * w11 + 32768u) >> 16;
                    uint gg = (((c00 >> 8) & 0xFFu) * w00 + ((c10 >> 8) & 0xFFu) * w10 + ((c01 >> 8) & 0xFFu) * w01 + ((c11 >> 8) & 0xFFu) * w11 + 32768u) >> 16;
                    uint r = (((c00 >> 16) & 0xFFu) * w00 + ((c10 >> 16) & 0xFFu) * w10 + ((c01 >> 16) & 0xFFu) * w01 + ((c11 >> 16) & 0xFFu) * w11 + 32768u) >> 16;
                    uint a = (((c00 >> 24) & 0xFFu) * w00 + ((c10 >> 24) & 0xFFu) * w10 + ((c01 >> 24) & 0xFFu) * w01 + ((c11 >> 24) & 0xFFu) * w11 + 32768u) >> 16;

                    dstPx[dRow + dx] = (int)(b | (gg << 8) | (r << 16) | (a << 24));
                    sxFixed += xRatio;
                }
            });

            Bitmap scaled = new Bitmap(dstDim, dstDim, PixelFormat.Format32bppPArgb);
            BitmapData dbd = scaled.LockBits(new Rectangle(0, 0, dstDim, dstDim), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (dbd.Stride == dstDim * 4)
                {
                    System.Runtime.InteropServices.Marshal.Copy(dstPx, 0, dbd.Scan0, dstPx.Length);
                }
                else
                {
                    for (int y = 0; y < dstDim; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(dstPx, y * dstDim, new IntPtr(dbd.Scan0.ToInt64() + (long)y * dbd.Stride), dstDim);
                    }
                }
            }
            finally { scaled.UnlockBits(dbd); }
            return scaled;
        }

        private void DisposeCardTemplates()
        {
            foreach (var kv in _cardTemplates) { if (kv.Value != null) kv.Value.Dispose(); }
            _cardTemplates.Clear();
        }

        private void DisposePaintResources()
        {
            DisposeCardTemplates();
            if (_badgeCheckBmp != null) { _badgeCheckBmp.Dispose(); _badgeCheckBmp = null; }
            if (_badgeFolderBmp != null) { _badgeFolderBmp.Dispose(); _badgeFolderBmp = null; }
            foreach (var kv in _tagPillBmps) { if (kv.Value != null) kv.Value.Dispose(); }
            _tagPillBmps.Clear();
            foreach (var kv in _videoBadgeBmps) { if (kv.Value != null) kv.Value.Dispose(); }
            _videoBadgeBmps.Clear();

            if (_nameFont != null) { _nameFont.Dispose(); _nameFont = null; }
            if (_badgeFont != null) { _badgeFont.Dispose(); _badgeFont = null; }
            if (_videoExtFont != null) { _videoExtFont.Dispose(); _videoExtFont = null; }
            if (_hudTitleFont != null) { _hudTitleFont.Dispose(); _hudTitleFont = null; }
            if (_hudSubFont != null) { _hudSubFont.Dispose(); _hudSubFont = null; }
            if (_textBrush != null) { _textBrush.Dispose(); _textBrush = null; }
            if (_placeholderBg != null) { _placeholderBg.Dispose(); _placeholderBg = null; }
            if (_marqueeFill != null) { _marqueeFill.Dispose(); _marqueeFill = null; }
            if (_marqueePen != null) { _marqueePen.Dispose(); _marqueePen = null; }
            if (_scrollTrackBrush != null) { _scrollTrackBrush.Dispose(); _scrollTrackBrush = null; }
            if (_scrollThumbBrush != null) { _scrollThumbBrush.Dispose(); _scrollThumbBrush = null; }
            if (_scrollThumbDragBrush != null) { _scrollThumbDragBrush.Dispose(); _scrollThumbDragBrush = null; }
            if (_hudBgBrush != null) { _hudBgBrush.Dispose(); _hudBgBrush = null; }
            if (_titleFormat != null) { _titleFormat.Dispose(); _titleFormat = null; }
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
            EnsurePaintResources();
            Graphics g = e.Graphics;
            // 主循环采用 1:1 32bppPArgb 位图直贴，关闭全局矢量抗锯齿与重插值以触发 GDI+ SIMD memcpy 快路径
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
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

            for (int i = startIdx; i < endIdx; i++)
            {
                EntryItem item = _items[i];
                Rectangle b = item.Bounds;
                if (b.Bottom < viewTop || b.Top > viewBot) continue;

                Rectangle drawRect = new Rectangle(b.X, b.Y - _scrollOffset, b.Width, b.Height);
                bool isSel = item.IsSelected;
                bool isHover = (i == _hoverIndex);
                bool isDropTarget = (i == _dropTargetFolderIndex);

                int stateKind = isDropTarget ? 3 : (isSel ? 2 : (isHover ? 1 : 0));
                List<TagSpec> activeSpecs = (item.TagUids != null && item.TagUids.Count > 0)
                    ? MainWindow.ResolveTagSpecs(item.TagUids)
                    : null;
                Color ringColor = (activeSpecs != null && activeSpecs.Count > 0) ? activeSpecs[0].Color : Color.Empty;

                Bitmap cardBmp = GetCardTemplateBitmap(stateKind, ringColor);
                g.DrawImageUnscaled(cardBmp, drawRect.X - 2, drawRect.Y - 2);

                int thumbDim = drawRect.Width - 12;
                Rectangle thumbRect = new Rectangle(drawRect.X + 6, drawRect.Y + 6, thumbDim, thumbDim);
                Bitmap bmp;
                if (_thumbCache.TryGetValue(item.FullPath, out bmp) && bmp != null)
                {
                    Bitmap scaled = GetOrCreateScaledThumb(item.FullPath, bmp, thumbDim);
                    g.DrawImageUnscaled(scaled, thumbRect.X, thumbRect.Y);
                }
                else
                {
                    EnsureThumbnailRequested(item);
                    g.FillRectangle(_placeholderBg, thumbRect);
                }

                if (isSel)
                {
                    g.DrawImageUnscaled(_badgeCheckBmp, thumbRect.Right - 29, thumbRect.Y + 5);
                }

                if (item.IsFolder)
                {
                    g.DrawImageUnscaled(_badgeFolderBmp, thumbRect.Right - 49, thumbRect.Bottom - 39);
                }
                else
                {
                    string ext = Path.GetExtension(item.FullPath);
                    if (IsVideoExtension(ext))
                    {
                        string vExt = (ext ?? "").TrimStart('.').ToUpperInvariant();
                        if (string.IsNullOrEmpty(vExt)) vExt = "VIDEO";
                        Bitmap vb = GetVideoBadgeBitmap(vExt);
                        g.DrawImageUnscaled(vb, thumbRect.Right - (vb.Width - 2) - 7, thumbRect.Bottom - (vb.Height - 2) - 7);
                    }
                }

                if (activeSpecs != null && activeSpecs.Count > 0)
                {
                    int px = thumbRect.X + 6;
                    int py = thumbRect.Y + 6;
                    for (int ti = 0; ti < activeSpecs.Count; ti++)
                    {
                        g.DrawImageUnscaled(GetTagPillBitmap(activeSpecs[ti]), px - 1, py - 1);
                        px += 50;
                    }
                }

                Rectangle titleRect = new Rectangle(drawRect.X + 6, thumbRect.Bottom + 6, drawRect.Width - 12, 32);
                g.DrawString(item.Name, _nameFont, _textBrush, titleRect, _titleFormat);
            }

            if (_isMarqueeSelecting)
            {
                Rectangle contentBox = GetMarqueeContentRect();
                Rectangle screenBox = new Rectangle(contentBox.X, contentBox.Y - _scrollOffset, contentBox.Width, contentBox.Height);
                if (screenBox.Width > 1 || screenBox.Height > 1)
                {
                    g.FillRectangle(_marqueeFill, screenBox);
                    g.DrawRectangle(_marqueePen, screenBox);
                }
            }

            if (_totalContentHeight > ClientSize.Height && ClientSize.Height > 40)
            {
                float ratio = (float)ClientSize.Height / _totalContentHeight;
                int barH = Math.Max(54, (int)(ClientSize.Height * ratio));
                int maxScroll = _totalContentHeight - ClientSize.Height;
                int barY = (int)((float)_scrollOffset / maxScroll * (ClientSize.Height - barH));
                _scrollThumbRect = new Rectangle(ClientSize.Width - 18, barY, 14, barH);
                g.FillRectangle(_scrollTrackBrush, ClientSize.Width - 20, 0, 20, ClientSize.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawRoundedRect(g, _scrollThumbRect, 6, _isDraggingScrollbar ? _scrollThumbDragBrush : _scrollThumbBrush, null);
                g.SmoothingMode = SmoothingMode.None;
            }
            else
            {
                _scrollThumbRect = Rectangle.Empty;
            }

            if (DateTime.UtcNow < _hudUntil && !string.IsNullOrEmpty(_hudTitle))
            {
                int hw = 390, hh = 52;
                int hx = Math.Max(12, Math.Min(_hudPoint.X - 30, ClientSize.Width - hw - 12));
                int hy = Math.Max(12, _hudPoint.Y - hh - 14);
                Rectangle hr = new Rectangle(hx, hy, hw, hh);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen hp = new Pen(_hudColor, 2.2f))
                using (SolidBrush hbar = new SolidBrush(_hudColor))
                {
                    DrawRoundedRect(g, hr, 8, _hudBgBrush, hp);
                    g.FillRectangle(hbar, hx + 2, hy + 2, 8, hh - 4);
                    g.DrawString(_hudTitle, _hudTitleFont, Brushes.White, new PointF(hx + 16, hy + 6));
                    g.DrawString(_hudSub, _hudSubFont, Brushes.LightGray, new PointF(hx + 16, hy + 28));
                }
            }
        }
    }
}
