// 视频悬停预览的触发与隐藏。
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
                    Bitmap coverBmp = _grid != null ? _grid.TryGetCachedThumbnail(_pendingHoverVideoPath) : null;
                    _videoHoverPopup.ShowOrUpdatePreview(_pendingHoverVideoPath, cursorScreenPt, coverBmp);
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
    }
}
