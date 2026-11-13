// 网格中的一个条目（文件或文件夹）。
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
    internal sealed class EntryItem
    {
        public string FullPath;
        public string Name;
        public bool IsFolder;
        public long SizeBytes;
        public DateTime CreatedAt;
        public DateTime ModifiedAt;
        public List<string> TagUids = new List<string>();
        public string CoverImagePath;
        public Rectangle Bounds;
        public bool IsSelected;
    }
}
