// 看图器 ~ 键截图模式（移植自 ScreenboxLite）：保存栏、设置共享、裁剪框交互与保存。
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
        private void BuildScreenshotSaveBar()
        {
            _topSaveBarPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = Color.FromArgb(22, 42, 48),
                Visible = false
            };

            Label titleLbl = new Label
            {
                Text = "📷 默认截图保存位置：",
                ForeColor = Color.FromArgb(0, 229, 255),
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 11)
            };
            _topSaveBarPanel.Controls.Add(titleLbl);

            _saveLocationLabel = new Label
            {
                Text = _saveFolderPath,
                ForeColor = Color.FromArgb(215, 235, 242),
                BackColor = Color.FromArgb(18, 28, 34),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(168, 8),
                Size = new Size(340, 26)
            };
            _topSaveBarPanel.Controls.Add(_saveLocationLabel);

            Label tipLbl = new Label
            {
                Text = "[~截图模式] 左键拖拽或双击两点定框  |  按 [R] 恢复上次裁剪框  |  再按 [~] 保存截图  |  右键/Esc 逐级回退",
                ForeColor = Color.FromArgb(190, 225, 240),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular),
                AutoSize = true,
                Location = new Point(520, 12)
            };
            _topSaveBarPanel.Controls.Add(tipLbl);

            Button openDirBtn = new Button
            {
                Text = "打开目录",
                Size = new Size(76, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 52, 60),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.8f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            openDirBtn.FlatAppearance.BorderColor = Color.FromArgb(0, 180, 210);
            openDirBtn.Click += (s, e) =>
            {
                try
                {
                    if (!Directory.Exists(_saveFolderPath)) Directory.CreateDirectory(_saveFolderPath);
                    Process.Start("explorer.exe", _saveFolderPath);
                }
                catch (Exception ex) { Log.Warn(ex, "打开截图保存目录失败"); }
                Focus();
            };

            Button changeDirBtn = new Button
            {
                Text = "更改目录",
                Size = new Size(76, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 52, 60),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.8f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            changeDirBtn.FlatAppearance.BorderColor = Color.FromArgb(0, 180, 210);
            changeDirBtn.Click += (s, e) =>
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择截图默认保存目录";
                    dlg.SelectedPath = _saveFolderPath;
                    if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                    {
                        _saveFolderPath = dlg.SelectedPath;
                        _saveLocationLabel.Text = _saveFolderPath;
                        SaveScreenshotSettings();
                        ShowToast("已更新截图保存目录: " + _saveFolderPath);
                    }
                }
                Focus();
            };

            _topSaveBarPanel.Resize += (s, e) =>
            {
                openDirBtn.Location = new Point(_topSaveBarPanel.ClientSize.Width - 90, 8);
                changeDirBtn.Location = new Point(_topSaveBarPanel.ClientSize.Width - 172, 8);
                tipLbl.MaximumSize = new Size(Math.Max(100, changeDirBtn.Left - tipLbl.Left - 12), 24);
            };
            openDirBtn.Location = new Point(_topSaveBarPanel.ClientSize.Width - 90, 8);
            changeDirBtn.Location = new Point(_topSaveBarPanel.ClientSize.Width - 172, 8);

            _topSaveBarPanel.Controls.Add(changeDirBtn);
            _topSaveBarPanel.Controls.Add(openDirBtn);
            Controls.Add(_topSaveBarPanel);
        }

        private void LoadScreenshotSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath)) return;
                string[] lines = File.ReadAllLines(_settingsFilePath);
                foreach (string rawLine in lines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    int sep = rawLine.IndexOf('=');
                    if (sep <= 0) continue;
                    string key = rawLine.Substring(0, sep).Trim();
                    string val = rawLine.Substring(sep + 1).Trim();
                    if (string.Equals(key, "SaveFolderPath", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(val)) _saveFolderPath = val;
                    }
                    else if (string.Equals(key, "LastCrop", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = val.Split(',');
                        float cx, cy, cw, ch;
                        if (parts.Length == 4 &&
                            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cx) &&
                            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cy) &&
                            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cw) &&
                            float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ch) &&
                            cw > 0.001f && ch > 0.001f)
                        {
                            _lastNormalizedCropRect = new RectangleF(
                                ClampF(cx, 0f, 1f),
                                ClampF(cy, 0f, 1f),
                                ClampF(cw, 0.001f, 1f),
                                ClampF(ch, 0.001f, 1f));
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "读取看图器截图配置"); }
        }

        private void SaveScreenshotSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_settingsFilePath))
                {
                    foreach (string rawLine in File.ReadAllLines(_settingsFilePath))
                    {
                        if (string.IsNullOrWhiteSpace(rawLine)) continue;
                        int sep = rawLine.IndexOf('=');
                        if (sep <= 0) continue;
                        dict[rawLine.Substring(0, sep).Trim()] = rawLine.Substring(sep + 1).Trim();
                    }
                }

                string lastCropStr = "";
                if (_lastNormalizedCropRect.HasValue)
                {
                    RectangleF c = _lastNormalizedCropRect.Value;
                    lastCropStr = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.######},{1:0.######},{2:0.######},{3:0.######}",
                        c.X, c.Y, c.Width, c.Height);
                }

                dict["LastCrop"] = lastCropStr;
                dict["SaveFolderPath"] = _saveFolderPath ?? "";

                List<string> outLines = new List<string>();
                foreach (var kv in dict)
                {
                    outLines.Add(kv.Key + "=" + kv.Value);
                }
                File.WriteAllLines(_settingsFilePath, outLines.ToArray());
            }
            catch (Exception ex) { Log.Debug(ex, "读取看图器截图配置"); }
        }

        private static float ClampF(float val, float min, float max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private static RectangleF ClampRectToBounds(RectangleF r, RectangleF bounds)
        {
            float x1 = ClampF(r.Left, bounds.Left, bounds.Right);
            float y1 = ClampF(r.Top, bounds.Top, bounds.Bottom);
            float x2 = ClampF(r.Right, bounds.Left, bounds.Right);
            float y2 = ClampF(r.Bottom, bounds.Top, bounds.Bottom);
            return new RectangleF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        private void EnterScreenshotMode()
        {
            if (_currentBitmap == null) return;
            LoadScreenshotSettings();
            _saveLocationLabel.Text = _saveFolderPath;
            _isScreenshotModeActive = true;
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _currentSelectionRect = RectangleF.Empty;
            _topSaveBarPanel.Visible = true;
            _currentMousePt = PointToClient(Cursor.Position);
            Cursor = Cursors.Cross;
            ShowToast("已进入截图模式：左键拖拽或单击两点框选，按 R 恢复上次选框，再按 ~ 保存（无选框则保存全图）");
            Invalidate();
        }

        private void ExitScreenshotMode()
        {
            _isScreenshotModeActive = false;
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _currentSelectionRect = RectangleF.Empty;
            _topSaveBarPanel.Visible = false;
            Capture = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        private void HandleTildeShortcut()
        {
            long now = Environment.TickCount;
            if (Math.Abs(now - _lastTildeTriggerTick) < 120) return;
            _lastTildeTriggerTick = now;

            if (_currentBitmap == null)
            {
                ShowToast("当前没有可截图的图像");
                return;
            }

            if (!_isScreenshotModeActive)
            {
                EnterScreenshotMode();
            }
            else
            {
                RectangleF imgRect = GetDisplayedImageRect();
                if ((_hasActiveSelection || _cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint) &&
                    _currentSelectionRect.Width >= 4f && _currentSelectionRect.Height >= 4f &&
                    imgRect.Width > 0f && imgRect.Height > 0f)
                {
                    RectangleF clampedSel = ClampRectToBounds(_currentSelectionRect, imgRect);
                    float normX = ClampF((clampedSel.X - imgRect.X) / imgRect.Width, 0f, 1f);
                    float normY = ClampF((clampedSel.Y - imgRect.Y) / imgRect.Height, 0f, 1f);
                    float normW = ClampF(clampedSel.Width / imgRect.Width, 0.001f, 1f - normX);
                    float normH = ClampF(clampedSel.Height / imgRect.Height, 0.001f, 1f - normY);
                    RectangleF normCrop = new RectangleF(normX, normY, normW, normH);
                    _lastNormalizedCropRect = normCrop;
                    SaveScreenshotSettings();
                    SaveScreenshotAndPreview(normCrop);
                }
                else
                {
                    SaveScreenshotAndPreview(null);
                }
                ExitScreenshotMode();
            }
        }

        private void RestoreLastCropSelection()
        {
            if (!_isScreenshotModeActive) return;
            long now = Environment.TickCount;
            if (Math.Abs(now - _lastKeyRTriggerTick) < 120) return;
            _lastKeyRTriggerTick = now;

            if (!_lastNormalizedCropRect.HasValue)
            {
                ShowToast("暂无上次裁剪框记录，请先框选一次");
                return;
            }

            RectangleF imgRect = GetDisplayedImageRect();
            if (imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            RectangleF norm = _lastNormalizedCropRect.Value;
            float left = imgRect.X + norm.X * imgRect.Width;
            float top = imgRect.Y + norm.Y * imgRect.Height;
            float width = norm.Width * imgRect.Width;
            float height = norm.Height * imgRect.Height;

            RectangleF restored = ClampRectToBounds(new RectangleF(left, top, width, height), imgRect);
            if (restored.Width < 4f || restored.Height < 4f) return;

            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = true;
            _currentSelectionRect = restored;
            UpdateCursorForZone(HitTestSelectionZone(_currentMousePt));
            ShowToast("已恢复上次裁剪框范围（可拖动边框/内部微调，按 ~ 确认裁剪保存）");
            Invalidate();
        }

        private void SaveScreenshotAndPreview(RectangleF? normalizedCrop)
        {
            try
            {
                if (_currentBitmap == null) return;
                if (!Directory.Exists(_saveFolderPath))
                    Directory.CreateDirectory(_saveFolderPath);

                int srcW = _currentBitmap.Width;
                int srcH = _currentBitmap.Height;
                Rectangle cropPx = new Rectangle(0, 0, srcW, srcH);

                if (normalizedCrop.HasValue)
                {
                    RectangleF c = normalizedCrop.Value;
                    int x = (int)Math.Round(ClampF(c.X, 0f, 1f) * srcW);
                    int y = (int)Math.Round(ClampF(c.Y, 0f, 1f) * srcH);
                    int w = (int)Math.Round(ClampF(c.Width, 0.001f, 1f) * srcW);
                    int h = (int)Math.Round(ClampF(c.Height, 0.001f, 1f) * srcH);
                    if (x < 0) x = 0;
                    if (y < 0) y = 0;
                    if (x + w > srcW) w = srcW - x;
                    if (y + h > srcH) h = srcH - y;
                    if (w >= 2 && h >= 2)
                    {
                        cropPx = new Rectangle(x, y, w, h);
                    }
                }

                string srcName = (_currentIndex >= 0 && _currentIndex < _imageList.Count)
                    ? Path.GetFileNameWithoutExtension(_imageList[_currentIndex])
                    : "Image";
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    srcName = srcName.Replace(invalid, '_');
                }
                string suffix = normalizedCrop.HasValue ? "_crop" : "_full";
                string fileName = string.Format("{0}_{1:yyyyMMdd_HHmmss_fff}{2}.png", srcName, DateTime.Now, suffix);
                string fullSavePath = Path.Combine(_saveFolderPath, fileName);

                using (Bitmap outBmp = new Bitmap(cropPx.Width, cropPx.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(_currentBitmap, new Rectangle(0, 0, cropPx.Width, cropPx.Height), cropPx, GraphicsUnit.Pixel);
                    }
                    outBmp.Save(fullSavePath, ImageFormat.Png);

                    // Create thumbnail for bottom-right preview banner
                    int thumbW = 92;
                    int thumbH = 52;
                    Bitmap previewThumb = new Bitmap(thumbW, thumbH, PixelFormat.Format32bppArgb);
                    using (Graphics tg = Graphics.FromImage(previewThumb))
                    {
                        tg.Clear(Color.Black);
                        tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        float s = Math.Min((float)thumbW / outBmp.Width, (float)thumbH / outBmp.Height);
                        int dw = Math.Max(1, (int)Math.Round(outBmp.Width * s));
                        int dh = Math.Max(1, (int)Math.Round(outBmp.Height * s));
                        tg.DrawImage(outBmp, new Rectangle((thumbW - dw) / 2, (thumbH - dh) / 2, dw, dh));
                    }

                    Bitmap oldThumb = _savedPreviewThumb;
                    _savedPreviewThumb = previewThumb;
                    if (oldThumb != null) oldThumb.Dispose();
                    _savedPreviewFileName = fileName;
                    _savedPreviewFullPath = fullSavePath;
                    _savedPreviewUntil = DateTime.UtcNow.AddSeconds(4.0);
                }

                ShowToast(string.Format("✔ 已保存截图 ({0}×{1}): {2}", cropPx.Width, cropPx.Height, fileName));
                if (!_uiOverlayTimer.Enabled) _uiOverlayTimer.Start();
                Invalidate();
            }
            catch (Exception ex)
            {
                ShowToast("截图保存失败: " + ex.Message);
            }
        }

        private CropHitZone HitTestSelectionZone(PointF pt)
        {
            if (!_hasActiveSelection || _currentSelectionRect.Width < 4f || _currentSelectionRect.Height < 4f)
                return CropHitZone.None;

            const float tol = 8f;
            RectangleF r = _currentSelectionRect;
            bool nearLeft = Math.Abs(pt.X - r.Left) <= tol && pt.Y >= r.Top - tol && pt.Y <= r.Bottom + tol;
            bool nearRight = Math.Abs(pt.X - r.Right) <= tol && pt.Y >= r.Top - tol && pt.Y <= r.Bottom + tol;
            bool nearTop = Math.Abs(pt.Y - r.Top) <= tol && pt.X >= r.Left - tol && pt.X <= r.Right + tol;
            bool nearBottom = Math.Abs(pt.Y - r.Bottom) <= tol && pt.X >= r.Left - tol && pt.X <= r.Right + tol;

            CropHitZone zone = CropHitZone.None;
            if (nearLeft) zone |= CropHitZone.Left;
            else if (nearRight) zone |= CropHitZone.Right;

            if (nearTop) zone |= CropHitZone.Top;
            else if (nearBottom) zone |= CropHitZone.Bottom;

            if (zone != CropHitZone.None) return zone;
            if (r.Contains(pt)) return CropHitZone.Inside;
            return CropHitZone.None;
        }

        private void UpdateCursorForZone(CropHitZone zone)
        {
            if (!_isScreenshotModeActive)
            {
                Cursor = Cursors.Default;
                return;
            }
            if (_cropMode == CropSelectionMode.None && _hasActiveSelection)
            {
                if ((zone == (CropHitZone.Left | CropHitZone.Top)) || (zone == (CropHitZone.Right | CropHitZone.Bottom)))
                {
                    Cursor = Cursors.SizeNWSE;
                    return;
                }
                if ((zone == (CropHitZone.Right | CropHitZone.Top)) || (zone == (CropHitZone.Left | CropHitZone.Bottom)))
                {
                    Cursor = Cursors.SizeNESW;
                    return;
                }
                if ((zone & (CropHitZone.Left | CropHitZone.Right)) != 0)
                {
                    Cursor = Cursors.SizeWE;
                    return;
                }
                if ((zone & (CropHitZone.Top | CropHitZone.Bottom)) != 0)
                {
                    Cursor = Cursors.SizeNS;
                    return;
                }
                if (zone == CropHitZone.Inside)
                {
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }
            Cursor = Cursors.Cross;
        }

        private void CancelCurrentSelection()
        {
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _currentSelectionRect = RectangleF.Empty;
            Capture = false;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        private void HandleRightClickBackInScreenshotMode()
        {
            if (_cropMode == CropSelectionMode.ResizingOrMoving)
            {
                _cropMode = CropSelectionMode.None;
                _activeResizeZone = CropHitZone.None;
                _currentSelectionRect = _resizeStartSelectionRect;
                _hasActiveSelection = true;
                Capture = false;
                Invalidate();
                return;
            }
            if (_cropMode != CropSelectionMode.None || _hasActiveSelection)
            {
                CancelCurrentSelection();
                ShowToast("已清除当前裁剪选框（再按右键或 Esc 退出截图模式）");
                return;
            }
            ExitScreenshotMode();
            ShowToast("已退出截图模式");
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // Check if user clicked on the saved screenshot preview card in bottom-right corner
            if (e.Button == MouseButtons.Left && _savedPreviewThumb != null && _savedPreviewCardBounds.Contains(e.Location))
            {
                if (!string.IsNullOrEmpty(_savedPreviewFullPath) && File.Exists(_savedPreviewFullPath))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + _savedPreviewFullPath + "\""); }
                    catch (Exception ex) { Log.Warn(ex, "资源管理器定位截图失败"); }
                }
                return;
            }

            if (!_isScreenshotModeActive) return;

            if (e.Button == MouseButtons.Right)
            {
                HandleRightClickBackInScreenshotMode();
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            RectangleF imgRect = GetDisplayedImageRect();
            if (imgRect.Width <= 0f || imgRect.Height <= 0f) return;

            PointF clampedPos = new PointF(
                ClampF(e.X, imgRect.Left, imgRect.Right),
                ClampF(e.Y, imgRect.Top, imgRect.Bottom));
            _mouseDownOverlayPoint = clampedPos;

            // Case 1: TwoClickBlueprint -> Second click lands the crop box
            if (_cropMode == CropSelectionMode.TwoClickBlueprint)
            {
                float left = Math.Min(_selectionStartPoint.X, clampedPos.X);
                float top = Math.Min(_selectionStartPoint.Y, clampedPos.Y);
                float width = Math.Abs(clampedPos.X - _selectionStartPoint.X);
                float height = Math.Abs(clampedPos.Y - _selectionStartPoint.Y);

                _cropMode = CropSelectionMode.None;
                Capture = false;

                if (width >= 4f && height >= 4f)
                {
                    _currentSelectionRect = ClampRectToBounds(new RectangleF(left, top, width, height), imgRect);
                    _hasActiveSelection = true;
                    ShowToast("裁剪框已落地：可拖动边框微调，或按 ~ 键确认裁剪保存");
                }
                else
                {
                    CancelCurrentSelection();
                }
                Invalidate();
                return;
            }

            // Case 2: Existing crop box -> check if resizing or moving
            if (_hasActiveSelection && _cropMode == CropSelectionMode.None)
            {
                CropHitZone hitZone = HitTestSelectionZone(clampedPos);
                if (hitZone != CropHitZone.None)
                {
                    _cropMode = CropSelectionMode.ResizingOrMoving;
                    _activeResizeZone = hitZone;
                    _resizeStartMousePoint = clampedPos;
                    _resizeStartSelectionRect = _currentSelectionRect;
                    Capture = true;
                    Invalidate();
                    return;
                }
            }

            // Case 3: Start new selection
            _cropMode = CropSelectionMode.DraggingNew;
            _hasActiveSelection = false;
            _selectionStartPoint = clampedPos;
            _currentSelectionRect = new RectangleF(clampedPos.X, clampedPos.Y, 0f, 0f);
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _currentMousePt = e.Location;

            if (_savedPreviewThumb != null && _savedPreviewCardBounds.Contains(e.Location))
            {
                Cursor = Cursors.Hand;
                return;
            }

            if (!_isScreenshotModeActive) return;

            RectangleF imgRect = GetDisplayedImageRect();
            PointF clampedPos = new PointF(
                ClampF(e.X, imgRect.Left, imgRect.Right),
                ClampF(e.Y, imgRect.Top, imgRect.Bottom));

            if (_cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint)
            {
                float left = Math.Min(_selectionStartPoint.X, clampedPos.X);
                float top = Math.Min(_selectionStartPoint.Y, clampedPos.Y);
                float width = Math.Abs(clampedPos.X - _selectionStartPoint.X);
                float height = Math.Abs(clampedPos.Y - _selectionStartPoint.Y);
                _currentSelectionRect = ClampRectToBounds(new RectangleF(left, top, width, height), imgRect);
            }
            else if (_cropMode == CropSelectionMode.ResizingOrMoving)
            {
                float dx = clampedPos.X - _resizeStartMousePoint.X;
                float dy = clampedPos.Y - _resizeStartMousePoint.Y;
                RectangleF r = _resizeStartSelectionRect;

                if (_activeResizeZone == CropHitZone.Inside)
                {
                    float newLeft = ClampF(r.Left + dx, imgRect.Left, Math.Max(imgRect.Left, imgRect.Right - r.Width));
                    float newTop = ClampF(r.Top + dy, imgRect.Top, Math.Max(imgRect.Top, imgRect.Bottom - r.Height));
                    _currentSelectionRect = new RectangleF(newLeft, newTop, r.Width, r.Height);
                }
                else
                {
                    float left = r.Left;
                    float right = r.Right;
                    float top = r.Top;
                    float bottom = r.Bottom;

                    if ((_activeResizeZone & CropHitZone.Left) != 0) left = ClampF(left + dx, imgRect.Left, right - 4f);
                    if ((_activeResizeZone & CropHitZone.Right) != 0) right = ClampF(right + dx, left + 4f, imgRect.Right);
                    if ((_activeResizeZone & CropHitZone.Top) != 0) top = ClampF(top + dy, imgRect.Top, bottom - 4f);
                    if ((_activeResizeZone & CropHitZone.Bottom) != 0) bottom = ClampF(bottom + dy, top + 4f, imgRect.Bottom);

                    _currentSelectionRect = new RectangleF(left, top, Math.Max(4f, right - left), Math.Max(4f, bottom - top));
                }
            }
            else
            {
                UpdateCursorForZone(HitTestSelectionZone(clampedPos));
            }

            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_isScreenshotModeActive || e.Button != MouseButtons.Left) return;

            RectangleF imgRect = GetDisplayedImageRect();
            PointF clampedPos = new PointF(
                ClampF(e.X, imgRect.Left, imgRect.Right),
                ClampF(e.Y, imgRect.Top, imgRect.Bottom));

            if (_cropMode == CropSelectionMode.DraggingNew)
            {
                float dragDist = (float)Math.Sqrt(
                    Math.Pow(clampedPos.X - _mouseDownOverlayPoint.X, 2) +
                    Math.Pow(clampedPos.Y - _mouseDownOverlayPoint.Y, 2));

                if (dragDist < 5f)
                {
                    // Short click -> transition into TwoClickBlueprint mode
                    _cropMode = CropSelectionMode.TwoClickBlueprint;
                    Capture = false;
                }
                else
                {
                    _cropMode = CropSelectionMode.None;
                    Capture = false;
                    if (_currentSelectionRect.Width >= 4f && _currentSelectionRect.Height >= 4f)
                    {
                        _hasActiveSelection = true;
                        ShowToast("裁剪框已就绪：可拖动边框微调，按 ~ 键保存截图");
                    }
                    else
                    {
                        CancelCurrentSelection();
                    }
                }
            }
            else if (_cropMode == CropSelectionMode.ResizingOrMoving)
            {
                _cropMode = CropSelectionMode.None;
                _activeResizeZone = CropHitZone.None;
                _hasActiveSelection = true;
                Capture = false;
            }

            UpdateCursorForZone(HitTestSelectionZone(clampedPos));
            Invalidate();
        }
    }
}
