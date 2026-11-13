// 打开条目：文件夹进入、图片用内置看图器、视频交给 ScreenboxLite（带 DSH_GALLERY_SORT_SPEC）。
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
        private static readonly string[] SCREENBOX_CANDIDATE_PATHS = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ScreenboxLite\ScreenboxLite.exe"),
            @"C:\Users\Administrator\AppData\Local\Programs\ScreenboxLite\ScreenboxLite.exe",
            @"F:\QQ\ScreenboxLite_Release\ScreenboxLite.exe"
        };

        private static string ResolveScreenboxLitePath()
        {
            foreach (string p in SCREENBOX_CANDIDATE_PATHS)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void OnItemOpened(EntryItem item)
        {
            HideVideoHoverPreview();
            if (item == null) return;
            if (item.IsFolder)
            {
                NavigateTo(item.FullPath, true);
            }
            else
            {
                string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
                if (Array.IndexOf(IMAGE_EXTS, ext) >= 0)
                {
                    List<string> orderedImgs = new List<string>();
                    if (_grid != null && _grid.Items != null)
                    {
                        foreach (EntryItem it in _grid.Items)
                        {
                            if (it != null && !it.IsFolder && Array.IndexOf(IMAGE_EXTS, Path.GetExtension(it.FullPath).ToLowerInvariant()) >= 0)
                                orderedImgs.Add(it.FullPath);
                        }
                    }
                    FastImageViewerForm viewer = new FastImageViewerForm(item.FullPath, orderedImgs);
                    viewer.Show(this);
                }
                else if (VirtualGridCanvas.IsVideoExtension(ext))
                {
                    try
                    {
                        string screenboxExe = ResolveScreenboxLitePath();
                        if (!string.IsNullOrEmpty(screenboxExe))
                        {
                            string sortProp = "System.ItemNameDisplay";
                            if (_sortField == SortField.CreatedTime) sortProp = "System.DateCreated";
                            else if (_sortField == SortField.ModifiedTime) sortProp = "System.DateModified";
                            else if (_sortField == SortField.Size) sortProp = "System.Size";
                            string sortSpec = "prop:" + (_sortDescending ? "-" : "") + sortProp + ";";

                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = screenboxExe,
                                Arguments = "\"" + item.FullPath + "\"",
                                UseShellExecute = false
                            };
                            psi.EnvironmentVariables["DSH_GALLERY_SORT_SPEC"] = sortSpec;
                            Process.Start(psi);
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                    }
                    catch { }
                }
            }
        }
    }
}
