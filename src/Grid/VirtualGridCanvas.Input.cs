// 网格交互：滚轮缩放/滚动、框选、拖放、Ctrl+Q/W/E/R/T 打标与归档、键盘导航。
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
    }
}
