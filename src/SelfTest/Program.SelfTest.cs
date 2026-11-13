// --self-test-sort：文件夹排序记忆与零分配自然序比较器自检（返回 0 表示全部通过）。
using System;
using System.IO;
using System.Text;

namespace DshGalleryExplorer
{
    internal static partial class Program
    {
        public static int RunSortSelfTest()
        {
            // 1. 路径键规范化自检
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

            // 2. 零分配自然序比较器自检（数值比较、大小写无关、前导零严格弱序、超过 ulong 上限的超长数字）
            if (MainWindow.NaturalCompare("img_2.jpg", "img_10.jpg") >= 0) return 13;
            if (MainWindow.NaturalCompare("IMG_10.jpg", "img_10.jpg") != 0) return 14;
            if (MainWindow.NaturalCompare("p_1.png", "p_01.png") == 0) return 15;
            if (MainWindow.NaturalCompare("p_1.png", "p_01.png") != -MainWindow.NaturalCompare("p_01.png", "p_1.png")) return 16;
            if (MainWindow.NaturalCompare(
                "twitter_999999999999999999999999999998.jpg",
                "twitter_999999999999999999999999999999.jpg") >= 0) return 17;

            // 3. 隔离缓存目录（绝不触碰用户真实 gallery_cache）验证跨实例排序记忆
            string tempCache = Path.Combine(Path.GetTempPath(), "dsh_selftest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempCache);
            AppPaths.OverrideCacheRoot(tempCache);

            // 使用轻量小目录，避免自检遍历上千子目录
            string dirA = Path.Combine(tempCache, "dirA");
            string dirB = Path.Combine(tempCache, "dirB");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

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

                // 新实例（模拟程序完整重启）
                using (MainWindow w2 = new MainWindow(dirB + "\\"))
                {
                    if (!w2.VerifyCurrentSortForTest(MainWindow.SortField.Size, false)) return 21;
                    w2.NavigateTo(dirA, true);
                    if (!w2.VerifyCurrentSortForTest(MainWindow.SortField.ModifiedTime, true)) return 22;
                }
            }
            finally
            {
                Log.Flush(500);
                try { Directory.Delete(tempCache, true); } catch { }
            }
            return 0;
        }
    }
}
