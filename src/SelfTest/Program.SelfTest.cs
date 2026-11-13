// --self-test-sort：文件夹排序记忆自检（返回 0 表示通过）。
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
    internal static partial class Program
    {
        public static int RunSortSelfTest()
        {
            string k1 = MainWindow.NormalizeFolderKey(@"F:\Openclow\画师素材库\");
            string k2 = MainWindow.NormalizeFolderKey(@"f:\OPENCLOW\画师素材库");
            string k3 = MainWindow.NormalizeFolderKey(@"F:/Openclow/画师素材库///");
            if (k1 != k2 || k2 != k3 || k1 != @"f:\openclow\画师素材库") return 10;

            string u1 = MainWindow.NormalizeFolderKey(@"F:\Openclow\users_test\x01_artist\");
            string u2 = MainWindow.NormalizeFolderKey(@"f:\openclow\users_test\x01_artist");
            if (u1 != u2 || u1 != @"f:\openclow\users_test\x01_artist") return 12;

            string root1 = MainWindow.NormalizeFolderKey(@"F:\");
            string root2 = MainWindow.NormalizeFolderKey(@"f:");
            if (root1 != root2 || root1 != @"f:\") return 11;

            string dirA = Directory.Exists(@"F:\Openclow\画师素材库") ? @"F:\Openclow\画师素材库" : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string dirB = Directory.Exists(@"F:\DeepSeek H") ? @"F:\DeepSeek H" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string backup = null;
            string sortPath = AppPaths.FolderSortsPath;
            if (File.Exists(sortPath)) backup = File.ReadAllText(sortPath, Encoding.UTF8);

            try
            {
                using (MainWindow w1 = new MainWindow(dirA))
                {
                    w1.NavigateTo(dirA + "\\", false);
                    w1.ApplySortForTest(MainWindow.SortField.ModifiedTime, true);
                    w1.NavigateTo(dirB, true);
                    w1.ApplySortForTest(MainWindow.SortField.Size, false);
                    w1.NavigateTo(dirA.ToLowerInvariant(), true);
                    if (!w1.VerifyCurrentSortForTest(MainWindow.SortField.ModifiedTime, true)) return 20;
                }

                // New instance (simulating full app restart)
                using (MainWindow w2 = new MainWindow(dirB + "\\"))
                {
                    if (!w2.VerifyCurrentSortForTest(MainWindow.SortField.Size, false)) return 21;
                    w2.NavigateTo(dirA, true);
                    if (!w2.VerifyCurrentSortForTest(MainWindow.SortField.ModifiedTime, true)) return 22;
                }
            }
            finally
            {
                if (backup != null) File.WriteAllText(sortPath, backup, new UTF8Encoding(false));
                else if (File.Exists(sortPath)) File.Delete(sortPath);
            }
            return 0;
        }
    }
}
