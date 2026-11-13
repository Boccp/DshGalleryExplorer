// 主窗口全局快捷键（F5/F2/Delete/Ctrl+C/X/V/Ctrl+Shift+C/N/Alt+←→/Backspace）。
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
    internal sealed partial class MainWindow
    {
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
}
