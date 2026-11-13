// 四色标记：NTFS :files 数据流读写、集中账本与 tags_index 同步、账本文件监听、标签筛选视图。
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
        public static readonly TagSpec[] ORDERED_TAGS = new TagSpec[]
        {
            new TagSpec { Key = "red",    Name = "红", Color = Color.FromArgb(232, 17, 35),  Hex = "#E81123", Uid = "f7e0e137-2eb5-4fa4-a50d-ddd65df17c34" },
            new TagSpec { Key = "orange", Name = "橙", Color = Color.FromArgb(247, 99, 12),  Hex = "#F7630C", Uid = "c84a8131-c4de-47d9-9440-26e859d14b3d" },
            new TagSpec { Key = "yellow", Name = "黄", Color = Color.FromArgb(255, 185, 0),  Hex = "#FFB900", Uid = "d4b8d4bd-ceaf-4e58-ac61-a185fcf96c5d" },
            new TagSpec { Key = "green",  Name = "绿", Color = Color.FromArgb(16, 124, 16),  Hex = "#107C10", Uid = "79376daf-c44a-4fe4-aa3b-8b30baea453e" },
        };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        public static List<TagSpec> ResolveTagSpecs(List<string> uids)
        {
            List<TagSpec> res = new List<TagSpec>();
            if (uids == null || uids.Count == 0) return res;
            foreach (TagSpec s in ORDERED_TAGS)
            {
                if (uids.Contains(s.Uid)) res.Add(s);
            }
            return res;
        }

        private void ShowTaggedItemsView(TagSpec filterSpec)
        {
            _activeTagFilter = filterSpec != null ? filterSpec.Name : "ALL";
            _addressBox.Text = filterSpec != null ? "【标签筛选：" + filterSpec.Name + "】" : "【全部已标记文件与文件夹】";

            List<EntryItem> results = new List<EntryItem>();
            lock (_ledgerLock)
            {
                foreach (var kv in _ledgerMap)
                {
                    string path = kv.Key;
                    List<string> uids = kv.Value;
                    if (uids == null || uids.Count == 0) continue;
                    if (filterSpec != null && !uids.Contains(filterSpec.Uid)) continue;

                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    EntryItem item = new EntryItem
                    {
                        FullPath = path,
                        Name = Path.GetFileName(path),
                        IsFolder = isDir,
                        TagUids = new List<string>(uids),
                        CoverImagePath = isDir ? null : path
                    };
                    results.Add(item);
                }
            }
            _grid.SetItems(results);
            _statusLabel.Text = string.Format("筛选视图：共 {0} 个已标记项", results.Count);
        }

        private void OnHotkeyActionTriggered(List<EntryItem> items, int vk)
        {
            if (items == null || items.Count == 0) return;
            Point cursorClient = _grid.PointToClient(Cursor.Position);

            if (vk == (int)Keys.T)
            {
                ExecuteBatchArchiveToggle(items, cursorClient);
                return;
            }

            TagSpec spec = null;
            if (vk == (int)Keys.Q) spec = ORDERED_TAGS[0];
            else if (vk == (int)Keys.W) spec = ORDERED_TAGS[1];
            else if (vk == (int)Keys.E) spec = ORDERED_TAGS[2];
            else if (vk == (int)Keys.R) spec = ORDERED_TAGS[3];
            if (spec == null) return;

            // If ANY selected item lacks the tag, add it to all selected items; otherwise remove from all!
            bool shouldAdd = false;
            foreach (EntryItem it in items)
            {
                if (!it.TagUids.Contains(spec.Uid)) { shouldAdd = true; break; }
            }

            List<KeyValuePair<string, List<string>>> persistList = new List<KeyValuePair<string, List<string>>>();

            foreach (EntryItem item in items)
            {
                if (shouldAdd)
                {
                    if (!item.TagUids.Contains(spec.Uid)) item.TagUids.Add(spec.Uid);
                }
                else
                {
                    item.TagUids.Remove(spec.Uid);
                }

                lock (_ledgerLock)
                {
                    if (item.TagUids.Count == 0)
                        _ledgerMap.Remove(item.FullPath);
                    else
                        _ledgerMap[item.FullPath] = new List<string>(item.TagUids);
                }
                persistList.Add(new KeyValuePair<string, List<string>>(item.FullPath, new List<string>(item.TagUids)));
            }

            string title = shouldAdd
                ? string.Format("＋ 已标记【{0}】({1} 项)", spec.Name, items.Count)
                : string.Format("－ 已取消【{0}】标记 ({1} 项)", spec.Name, items.Count);
            string sub = items.Count == 1 ? items[0].Name : (items[0].Name + " 等 " + items.Count + " 项");
            _grid.ShowInstantHud(cursorClient, title, sub, spec.Color);
            OnSelectionChanged();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var kv in persistList)
                {
                    WriteAdsUids(kv.Key, kv.Value);
                }
                SaveLedgerToDisk();
            });
        }

        private List<string> GetTagsForPath(string fullPath)
        {
            lock (_ledgerLock)
            {
                List<string> uids;
                if (_ledgerMap.TryGetValue(fullPath, out uids) && uids != null && uids.Count > 0)
                    return new List<string>(uids);
            }
            return new List<string>();
        }

        private static List<string> ReadAdsUidsFromDisk(string targetPath)
        {
            List<string> list = new List<string>();
            string ads = targetPath + ":files";
            IntPtr h = CreateFileW(ads, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h != IntPtr.Zero && h != (IntPtr)(-1))
            {
                try
                {
                    byte[] buf = new byte[4096];
                    uint read;
                    if (ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero) && read > 0)
                    {
                        string s = Encoding.UTF8.GetString(buf, 0, (int)read);
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (s.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0)
                                list.Add(t.Uid);
                        }
                    }
                }
                finally { CloseHandle(h); }
            }
            return list;
        }

        private static void WriteAdsUids(string targetPath, List<string> uids)
        {
            uint origAttrs = GetFileAttributesW(targetPath);
            bool hadReadOnly = (origAttrs != 0xFFFFFFFF) && ((origAttrs & 1) != 0);
            if (hadReadOnly) SetFileAttributesW(targetPath, origAttrs & ~1u);

            try
            {
                string ads = targetPath + ":files";
                if (uids == null || uids.Count == 0)
                {
                    DeleteFileW(ads);
                    return;
                }
                StringBuilder sb = new StringBuilder("[");
                for (int i = 0; i < uids.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(uids[i]).Append("\"");
                }
                sb.Append("]");
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                IntPtr h = CreateFileW(ads, 0x40000000, 3, IntPtr.Zero, 2, 0x02000000, IntPtr.Zero);
                if (h != IntPtr.Zero && h != (IntPtr)(-1))
                {
                    try
                    {
                        uint written;
                        WriteFile(h, bytes, (uint)bytes.Length, out written, IntPtr.Zero);
                    }
                    finally { CloseHandle(h); }
                }
            }
            finally
            {
                if (hadReadOnly) SetFileAttributesW(targetPath, origAttrs);
            }
        }

        private void LoadLedgerFromDisk()
        {
            try
            {
                if (!File.Exists(LEDGER_PATH)) return;
                string json = File.ReadAllText(LEDGER_PATH, Encoding.UTF8);
                lock (_ledgerLock)
                {
                    _ledgerMap.Clear();
                    foreach (Match m in Regex.Matches(json, "\"([A-Za-z]:\\\\\\\\[^\"]+)\"\\s*:\\s*\\{[^\\}]*\"uids\"\\s*:\\s*\\[([^\\]]*)\\]"))
                    {
                        string rawPath = m.Groups[1].Value.Replace("\\\\", "\\");
                        string rawUids = m.Groups[2].Value;
                        List<string> uids = new List<string>();
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (rawUids.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0)
                                uids.Add(t.Uid);
                        }
                        if (uids.Count > 0 && (File.Exists(rawPath) || Directory.Exists(rawPath)))
                        {
                            _ledgerMap[rawPath] = uids;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn(ex, "加载标签账本失败: " + LEDGER_PATH); }
        }

        private static volatile bool _savingLedger = false;
        private static string TAG_INDEX_DIR { get { return AppPaths.TagIndexDir; } }

        private void SaveLedgerToDisk()
        {
            try
            {
                _savingLedger = true;
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n  \"items\": {");
                bool first = true;

                Dictionary<string, List<string>> byColor = new Dictionary<string, List<string>>();
                foreach (TagSpec t in ORDERED_TAGS) byColor[t.Name] = new List<string>();
                List<string> summaryLines = new List<string>();

                lock (_ledgerLock)
                {
                    foreach (KeyValuePair<string, List<string>> kv in _ledgerMap)
                    {
                        if (kv.Value == null || kv.Value.Count == 0) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        string escapedPath = kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        List<string> names = new List<string>();
                        List<string> rawNames = new List<string>();
                        List<string> uids = new List<string>();
                        foreach (TagSpec t in ORDERED_TAGS)
                        {
                            if (kv.Value.Contains(t.Uid))
                            {
                                names.Add("\"" + t.Name + "\"");
                                rawNames.Add(t.Name);
                                uids.Add("\"" + t.Uid + "\"");
                                byColor[t.Name].Add(kv.Key);
                            }
                        }
                        if (rawNames.Count > 0)
                        {
                            summaryLines.Add("[" + string.Join(",", rawNames.ToArray()) + "]\t" + kv.Key);
                        }
                        sb.Append("\n    \"").Append(escapedPath).Append("\": {\n");
                        sb.Append("      \"tags\": [").Append(string.Join(", ", names.ToArray())).Append("],\n");
                        sb.Append("      \"uids\": [").Append(string.Join(", ", uids.ToArray())).Append("],\n");
                        sb.Append("      \"updatedAt\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\"\n    }");
                    }
                }
                sb.Append("\n  }\n}\n");
                string dir = Path.GetDirectoryName(LEDGER_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string ledgerJson = sb.ToString();
                File.WriteAllText(LEDGER_PATH, ledgerJson, Encoding.UTF8);

                // Seamless zero-latency sync into F:\Openclow\画师素材库\_标签索引 for instant Agent lookup!
                try
                {
                    if (!Directory.Exists(TAG_INDEX_DIR)) Directory.CreateDirectory(TAG_INDEX_DIR);
                    File.WriteAllText(Path.Combine(TAG_INDEX_DIR, "全部标记.json"), ledgerJson, Encoding.UTF8);
                    summaryLines.Sort(StringComparer.OrdinalIgnoreCase);
                    File.WriteAllText(Path.Combine(TAG_INDEX_DIR, "全部标记.txt"), string.Join("\n", summaryLines.ToArray()) + (summaryLines.Count > 0 ? "\n" : ""), Encoding.UTF8);

                    foreach (TagSpec t in ORDERED_TAGS)
                    {
                        List<string> paths = byColor[t.Name];
                        paths.Sort(StringComparer.OrdinalIgnoreCase);
                        File.WriteAllText(Path.Combine(TAG_INDEX_DIR, t.Name + ".txt"), string.Join("\n", paths.ToArray()) + (paths.Count > 0 ? "\n" : ""), Encoding.UTF8);

                        StringBuilder csb = new StringBuilder();
                        csb.Append("{\n  \"tag\": \"").Append(t.Name).Append("\",\n  \"color\": \"").Append(t.Hex).Append("\",\n  \"count\": ").Append(paths.Count).Append(",\n  \"items\": [");
                        for (int i = 0; i < paths.Count; i++)
                        {
                            if (i > 0) csb.Append(",");
                            csb.Append("\n    \"").Append(paths[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                        }
                        csb.Append("\n  ]\n}\n");
                        File.WriteAllText(Path.Combine(TAG_INDEX_DIR, t.Name + ".json"), csb.ToString(), Encoding.UTF8);
                    }
                }
                catch (Exception ex) { Log.Warn(ex, "同步标签索引目录失败: " + TAG_INDEX_DIR); }
            }
            catch (Exception ex) { Log.Warn(ex, "保存标签账本失败: " + LEDGER_PATH); }
            finally
            {
                Thread.Sleep(80);
                _savingLedger = false;
            }
        }

        private void SetupLedgerWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(LEDGER_PATH);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                _ledgerWatcher = new FileSystemWatcher(dir, Path.GetFileName(LEDGER_PATH));
                _ledgerWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                _ledgerWatcher.Changed += (s, e) =>
                {
                    if (_savingLedger) return;
                    Thread.Sleep(60);
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            LoadLedgerFromDisk();
                            lock (_ledgerLock)
                            {
                                foreach (EntryItem item in _grid.Items)
                                {
                                    List<string> uids;
                                    item.TagUids = _ledgerMap.TryGetValue(item.FullPath, out uids) ? new List<string>(uids) : new List<string>();
                                }
                            }
                            _grid.Invalidate();
                        }));
                    }
                };
                _ledgerWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Debug(ex, "启动标签账本监听器: " + LEDGER_PATH); }
        }
    }
}
