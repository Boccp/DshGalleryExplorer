// 程序入口：DPI 感知、命令行模式（--self-test-sort / --bench）与主窗口启动。
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
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); }
            catch
            {
                try { SetProcessDpiAwareness(2); }
                catch { try { SetProcessDPIAware(); } catch { } }
            }

            if (args != null && args.Length > 0 && string.Equals(args[0], "--self-test-sort", StringComparison.OrdinalIgnoreCase))
            {
                return RunSortSelfTest();
            }

            if (args != null && args.Length > 0 && string.Equals(args[0], "--bench", StringComparison.OrdinalIgnoreCase))
            {
                return BenchRunner.Run(args);
            }

            bool verbose = false;
            string initPath = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--verbose", StringComparison.OrdinalIgnoreCase)) verbose = true;
                    else if (initPath == null) initPath = args[i];
                }
            }
            string cfgLog = AppPaths.GetConfig("LogLevel");
            if (!string.IsNullOrEmpty(cfgLog) && string.Equals(cfgLog.Trim(), "Debug", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }
            Log.Init(AppPaths.LogDir, verbose);
            Log.Info("DSH Gallery Explorer 启动，参数: " + string.Join(" ", args ?? new string[0]));
            try { timeBeginPeriod(1); } catch { }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainWindow(initPath));
                return 0;
            }
            finally
            {
                try { timeEndPeriod(1); } catch { }
                Log.Info("退出，本次运行性能汇总：" + Environment.NewLine + Perf.Report());
                Log.Flush(1500);
            }
        }
    }
}
