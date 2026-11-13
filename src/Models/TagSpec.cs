// 四色标签定义（颜色、名称、UID）。UID 是与 Python SDK / Agent 共享的外部契约，不可修改。
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
    internal sealed class TagSpec
    {
        public string Key;
        public string Name;
        public Color Color;
        public string Hex;
        public string Uid;
    }
}
