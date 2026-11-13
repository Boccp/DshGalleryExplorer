// 排序：字段/方向/文件夹置顶、按文件夹记忆排序、跟随资源管理器排序、自然序比较。
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
        public enum SortField { Name, CreatedTime, ModifiedTime, Size, Tag }

        internal sealed class FolderSortSetting
        {
            public SortField Field = SortField.Name;
            public bool Descending = false;
            public bool FoldersFirst = true;
            public bool Remember = true;
        }

        public static string NormalizeFolderKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                string raw = path.Trim().Replace('/', '\\');
                if (raw.Length == 2 && raw[1] == ':')
                    raw += "\\";
                string full = Path.GetFullPath(raw);
                string trimmed = full.TrimEnd('\\', '/');
                if (trimmed.Length == 2 && trimmed[1] == ':')
                    return (trimmed + "\\").ToLowerInvariant();
                return trimmed.ToLowerInvariant();
            }
            catch
            {
                return path.Trim().TrimEnd('\\', '/').ToLowerInvariant();
            }
        }

        private bool IsCurrentFolderSortRemembered()
        {
            string key = NormalizeFolderKey(_currentDirectory);
            if (string.IsNullOrEmpty(key)) return _rememberFolderSortByDefault;
            lock (_folderSortsLock)
            {
                FolderSortSetting st;
                if (_folderSorts.TryGetValue(key, out st) && st != null)
                {
                    return st.Remember;
                }
            }
            return _rememberFolderSortByDefault;
        }

        private void ToggleRememberCurrentFolderSort()
        {
            string key = NormalizeFolderKey(_currentDirectory);
            if (string.IsNullOrEmpty(key)) return;

            bool newRemember = true;
            lock (_folderSortsLock)
            {
                FolderSortSetting st;
                if (_folderSorts.TryGetValue(key, out st) && st != null)
                {
                    st.Remember = !st.Remember;
                    st.Field = _sortField;
                    st.Descending = _sortDescending;
                    st.FoldersFirst = _sortFoldersFirst;
                    newRemember = st.Remember;
                }
                else
                {
                    newRemember = !_rememberFolderSortByDefault;
                    _folderSorts[key] = new FolderSortSetting
                    {
                        Field = _sortField,
                        Descending = _sortDescending,
                        FoldersFirst = _sortFoldersFirst,
                        Remember = newRemember
                    };
                }
            }
            SaveFolderSortsToDisk();
            UpdateSortButtonsUI();
            if (_grid != null)
            {
                Point pt = _grid.PointToClient(Cursor.Position);
                _grid.ShowInstantHud(
                    pt,
                    newRemember ? "📌 已开启记忆此文件夹排序" : "ℹ️ 已关闭记忆此文件夹排序",
                    _currentDirectory,
                    newRemember ? Color.FromArgb(40, 180, 100) : Color.FromArgb(200, 120, 40));
            }
        }

        private void PersistCurrentFolderSort()
        {
            string key = NormalizeFolderKey(_currentDirectory);
            if (!string.IsNullOrEmpty(key))
            {
                lock (_folderSortsLock)
                {
                    FolderSortSetting st;
                    if (!_folderSorts.TryGetValue(key, out st) || st == null)
                    {
                        st = new FolderSortSetting { Remember = _rememberFolderSortByDefault };
                        _folderSorts[key] = st;
                    }
                    // When the user explicitly changes sort in a folder, automatically remember it for this folder!
                    st.Field = _sortField;
                    st.Descending = _sortDescending;
                    st.FoldersFirst = _sortFoldersFirst;
                    st.Remember = true;
                }
            }
            SaveFolderSortsToDisk();
        }

        private void RestoreOrDetermineSortForDirectory(string folderPath)
        {
            string key = NormalizeFolderKey(folderPath);
            FolderSortSetting saved = null;
            if (!string.IsNullOrEmpty(key))
            {
                lock (_folderSortsLock)
                {
                    _folderSorts.TryGetValue(key, out saved);
                }
            }

            if (saved != null && saved.Remember)
            {
                _sortField = saved.Field;
                _sortDescending = saved.Descending;
                _sortFoldersFirst = saved.FoldersFirst;
                UpdateSortButtonsUI();
                return;
            }

            // If this folder does not have an explicit remembered sort yet, try syncing from open Explorer window if enabled,
            // otherwise use the persisted global default sort without letting the previous folder's temporary state leak into it!
            if (_autoSyncSystemFolderSort && TryApplyExplorerSortForDirectory(folderPath))
            {
                if (_rememberFolderSortByDefault && !string.IsNullOrEmpty(key))
                {
                    lock (_folderSortsLock)
                    {
                        _folderSorts[key] = new FolderSortSetting
                        {
                            Field = _sortField,
                            Descending = _sortDescending,
                            FoldersFirst = _sortFoldersFirst,
                            Remember = true
                        };
                    }
                    SaveFolderSortsToDisk();
                }
                return;
            }

            _sortField = _defaultSortField;
            _sortDescending = _defaultSortDescending;
            _sortFoldersFirst = _defaultSortFoldersFirst;
            UpdateSortButtonsUI();
        }

        private void LoadFolderSortsFromDisk()
        {
            try
            {
                if (!File.Exists(FOLDER_SORTS_PATH)) return;
                string content = File.ReadAllText(FOLDER_SORTS_PATH, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(content)) return;

                lock (_folderSortsLock)
                {
                    _folderSorts.Clear();
                    string trimmed = content.TrimStart();
                    if (trimmed.StartsWith("{"))
                    {
                        Match mField = Regex.Match(content, "\"defaultField\"\\s*:\\s*\"([^\"]+)\"");
                        if (mField.Success) Enum.TryParse(mField.Groups[1].Value, true, out _defaultSortField);
                        Match mDesc = Regex.Match(content, "\"defaultDescending\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                        if (mDesc.Success) bool.TryParse(mDesc.Groups[1].Value, out _defaultSortDescending);
                        Match mFirst = Regex.Match(content, "\"defaultFoldersFirst\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                        if (mFirst.Success) bool.TryParse(mFirst.Groups[1].Value, out _defaultSortFoldersFirst);
                        Match mAuto = Regex.Match(content, "\"autoSyncSystemFolderSort\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                        if (mAuto.Success) bool.TryParse(mAuto.Groups[1].Value, out _autoSyncSystemFolderSort);
                        Match mRem = Regex.Match(content, "\"rememberFolderSortByDefault\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                        if (mRem.Success) bool.TryParse(mRem.Groups[1].Value, out _rememberFolderSortByDefault);

                        foreach (Match m in Regex.Matches(content, "\"((?:[^\"\\\\]|\\\\.)+)\"\\s*:\\s*\\{([^\\}]*)\\}"))
                        {
                            string rawKey = UnescapeJsonString(m.Groups[1].Value);
                            if (string.Equals(rawKey, "folders", StringComparison.OrdinalIgnoreCase)) continue;
                            string body = m.Groups[2].Value;
                            FolderSortSetting st = new FolderSortSetting
                            {
                                Field = _defaultSortField,
                                Descending = _defaultSortDescending,
                                FoldersFirst = _defaultSortFoldersFirst,
                                Remember = true
                            };
                            Match mf = Regex.Match(body, "\"field\"\\s*:\\s*\"([^\"]+)\"");
                            if (mf.Success) Enum.TryParse(mf.Groups[1].Value, true, out st.Field);
                            Match md = Regex.Match(body, "\"descending\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                            if (md.Success) bool.TryParse(md.Groups[1].Value, out st.Descending);
                            Match mff = Regex.Match(body, "\"foldersFirst\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                            if (mff.Success) bool.TryParse(mff.Groups[1].Value, out st.FoldersFirst);
                            Match mr = Regex.Match(body, "\"remember\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                            if (mr.Success) bool.TryParse(mr.Groups[1].Value, out st.Remember);

                            string normKey = NormalizeFolderKey(rawKey);
                            if (!string.IsNullOrEmpty(normKey))
                            {
                                _folderSorts[normKey] = st;
                            }
                        }
                    }
                    else
                    {
                        // Fallback TSV parsing
                        string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string rawLine in lines)
                        {
                            string line = rawLine.Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                            string[] parts = line.Split('\t');
                            if (parts.Length >= 5 && parts[0] == "@GLOBAL")
                            {
                                Enum.TryParse(parts[1], true, out _defaultSortField);
                                bool.TryParse(parts[2], out _defaultSortDescending);
                                bool.TryParse(parts[3], out _defaultSortFoldersFirst);
                                bool.TryParse(parts[4], out _autoSyncSystemFolderSort);
                            }
                            else if (parts.Length >= 4)
                            {
                                string normKey = NormalizeFolderKey(parts[0]);
                                if (string.IsNullOrEmpty(normKey)) continue;
                                FolderSortSetting st = new FolderSortSetting();
                                Enum.TryParse(parts[1], true, out st.Field);
                                bool.TryParse(parts[2], out st.Descending);
                                bool.TryParse(parts[3], out st.FoldersFirst);
                                if (parts.Length >= 5) bool.TryParse(parts[4], out st.Remember);
                                _folderSorts[normKey] = st;
                            }
                        }
                    }
                }

                _sortField = _defaultSortField;
                _sortDescending = _defaultSortDescending;
                _sortFoldersFirst = _defaultSortFoldersFirst;
            }
            catch { }
        }

        private static string UnescapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char next = s[++i];
                    if (next == '\\') sb.Append('\\');
                    else if (next == '\"') sb.Append('\"');
                    else if (next == 'r') sb.Append('\r');
                    else if (next == 'n') sb.Append('\n');
                    else if (next == 't') sb.Append('\t');
                    else sb.Append(next);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private void SaveFolderSortsToDisk()
        {
            try
            {
                string dir = Path.GetDirectoryName(FOLDER_SORTS_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"defaultField\": \"").Append(_defaultSortField.ToString()).Append("\",\n");
                sb.Append("  \"defaultDescending\": ").Append(_defaultSortDescending ? "true" : "false").Append(",\n");
                sb.Append("  \"defaultFoldersFirst\": ").Append(_defaultSortFoldersFirst ? "true" : "false").Append(",\n");
                sb.Append("  \"autoSyncSystemFolderSort\": ").Append(_autoSyncSystemFolderSort ? "true" : "false").Append(",\n");
                sb.Append("  \"rememberFolderSortByDefault\": ").Append(_rememberFolderSortByDefault ? "true" : "false").Append(",\n");
                sb.Append("  \"folders\": {");

                lock (_folderSortsLock)
                {
                    List<string> keys = new List<string>(_folderSorts.Keys);
                    keys.Sort(StringComparer.OrdinalIgnoreCase);
                    bool first = true;
                    foreach (string k in keys)
                    {
                        FolderSortSetting st = _folderSorts[k];
                        if (st == null) continue;
                        string normKey = NormalizeFolderKey(k);
                        if (string.IsNullOrEmpty(normKey)) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\n    \"").Append(EscapeJsonString(normKey)).Append("\": { ");
                        sb.Append("\"field\": \"").Append(st.Field.ToString()).Append("\", ");
                        sb.Append("\"descending\": ").Append(st.Descending ? "true" : "false").Append(", ");
                        sb.Append("\"foldersFirst\": ").Append(st.FoldersFirst ? "true" : "false").Append(", ");
                        sb.Append("\"remember\": ").Append(st.Remember ? "true" : "false").Append(" }");
                    }
                }

                sb.Append("\n  }\n}\n");
                string tmpPath = FOLDER_SORTS_PATH + ".tmp";
                File.WriteAllText(tmpPath, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FOLDER_SORTS_PATH))
                {
                    File.Copy(tmpPath, FOLDER_SORTS_PATH, true);
                    try { File.Delete(tmpPath); } catch { }
                }
                else
                {
                    File.Move(tmpPath, FOLDER_SORTS_PATH);
                }
            }
            catch { }
        }

        private void CycleSortField()
        {
            if (_isUpdatingSortUi) return;
            SortField next = SortField.Name;
            if (_sortField == SortField.Name) next = SortField.CreatedTime;
            else if (_sortField == SortField.CreatedTime) next = SortField.ModifiedTime;
            else if (_sortField == SortField.ModifiedTime) next = SortField.Size;
            else if (_sortField == SortField.Size) next = SortField.Tag;
            else next = SortField.Name;
            ApplySort(next, _sortDescending, true);
        }

        private void ToggleSortDirection()
        {
            if (_isUpdatingSortUi) return;
            ApplySort(_sortField, !_sortDescending, true);
        }

        private void UpdateSortButtonsUI()
        {
            if (_isUpdatingSortUi) return;
            _isUpdatingSortUi = true;
            try
            {
                string fieldLabel = "名称";
                if (_sortField == SortField.CreatedTime) fieldLabel = "创建时间";
                else if (_sortField == SortField.ModifiedTime) fieldLabel = "修改时间";
                else if (_sortField == SortField.Size) fieldLabel = "大小";
                else if (_sortField == SortField.Tag) fieldLabel = "标签";

                if (_btnSortMode != null) _btnSortMode.Text = "⇅ 排序: " + fieldLabel;
                if (_btnSortDir != null) _btnSortDir.Text = _sortDescending ? "↓ 降序" : "↑ 升序";

                if (_miRememberCurrentFolderSort != null) _miRememberCurrentFolderSort.Checked = IsCurrentFolderSortRemembered();
                if (_miAutoSyncSystemSort != null) _miAutoSyncSystemSort.Checked = _autoSyncSystemFolderSort;
                if (_miSortByName != null) _miSortByName.Checked = (_sortField == SortField.Name);
                if (_miSortByCreated != null) _miSortByCreated.Checked = (_sortField == SortField.CreatedTime);
                if (_miSortByModified != null) _miSortByModified.Checked = (_sortField == SortField.ModifiedTime);
                if (_miSortBySize != null) _miSortBySize.Checked = (_sortField == SortField.Size);
                if (_miSortByTag != null) _miSortByTag.Checked = (_sortField == SortField.Tag);
                if (_miSortAsc != null) _miSortAsc.Checked = !_sortDescending;
                if (_miSortDesc != null) _miSortDesc.Checked = _sortDescending;
                if (_miSortFoldersFirst != null) _miSortFoldersFirst.Checked = _sortFoldersFirst;
            }
            finally
            {
                _isUpdatingSortUi = false;
            }
        }

        internal void ApplySortForTest(SortField field, bool descending)
        {
            ApplySort(field, descending, true, true);
        }

        internal bool VerifyCurrentSortForTest(SortField expectedField, bool expectedDesc)
        {
            return _sortField == expectedField && _sortDescending == expectedDesc && IsCurrentFolderSortRemembered();
        }

        private void ApplySort(SortField field, bool descending, bool refreshNow = true, bool persistSetting = true)
        {
            _sortField = field;
            _sortDescending = descending;
            if (persistSetting)
            {
                _defaultSortField = field;
                _defaultSortDescending = descending;
                PersistCurrentFolderSort();
            }
            UpdateSortButtonsUI();

            if (refreshNow)
            {
                string curSel = _grid.FocusedItem != null ? _grid.FocusedItem.FullPath : null;
                RefreshCurrentDirectory(0, curSel);
            }
        }

        private static string TryDetectExplorerSortColumns(string targetDir)
        {
            if (string.IsNullOrEmpty(targetDir)) return null;
            string normTarget = targetDir.Trim().TrimEnd('\\', '/');
            if (normTarget.Length == 2 && normTarget[1] == ':') normTarget += "\\";
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return null;

                int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null));
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object win = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;
                        object doc = win.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, win, null);
                        if (doc == null) continue;
                        object folder = doc.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, doc, null);
                        if (folder == null) continue;
                        object self = folder.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
                        if (self == null) continue;
                        string folderPath = self.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
                        if (string.IsNullOrEmpty(folderPath)) continue;
                        string normFolder = folderPath.Trim().TrimEnd('\\', '/');
                        if (normFolder.Length == 2 && normFolder[1] == ':') normFolder += "\\";
                        if (!string.Equals(normFolder, normTarget, StringComparison.OrdinalIgnoreCase)) continue;

                        string sortCols = doc.GetType().InvokeMember("SortColumns", System.Reflection.BindingFlags.GetProperty, null, doc, null) as string;
                        if (!string.IsNullOrWhiteSpace(sortCols)) return sortCols.Trim();
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private bool TryApplyExplorerSortForDirectory(string dir)
        {
            string spec = TryDetectExplorerSortColumns(dir);
            if (string.IsNullOrWhiteSpace(spec)) return false;

            if (spec.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
            {
                spec = spec.Substring(5);
            }
            int semi = spec.IndexOf(';');
            string primary = (semi >= 0 ? spec.Substring(0, semi) : spec).Trim();
            if (string.IsNullOrEmpty(primary)) return false;

            bool desc = false;
            string propName = primary;
            if (primary.StartsWith("-"))
            {
                desc = true;
                propName = primary.Substring(1).Trim();
            }
            else if (primary.StartsWith("+"))
            {
                desc = false;
                propName = primary.Substring(1).Trim();
            }

            SortField detectedField = SortField.Name;
            if (propName.IndexOf("DateCreated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.CreatedTime;
            }
            else if (propName.IndexOf("DateModified", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("System.Date", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("DateAccessed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.ModifiedTime;
            }
            else if (propName.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detectedField = SortField.Size;
            }
            else
            {
                detectedField = SortField.Name;
            }

            _sortField = detectedField;
            _sortDescending = desc;
            UpdateSortButtonsUI();
            return true;
        }

        private int CompareEntries(EntryItem a, EntryItem b)
        {
            return CompareEntries(a, b, _sortField, _sortDescending, _sortFoldersFirst);
        }

        internal static int CompareEntries(EntryItem a, EntryItem b, SortField sortField, bool sortDescending, bool sortFoldersFirst)
        {
            if (sortFoldersFirst && a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            int cmp = 0;
            switch (sortField)
            {
                case SortField.CreatedTime:
                    cmp = a.CreatedAt.CompareTo(b.CreatedAt);
                    break;
                case SortField.ModifiedTime:
                    cmp = a.ModifiedAt.CompareTo(b.ModifiedAt);
                    break;
                case SortField.Size:
                    cmp = a.SizeBytes.CompareTo(b.SizeBytes);
                    break;
                case SortField.Tag:
                    int rankA = GetPrimaryTagRank(a.TagUids);
                    int rankB = GetPrimaryTagRank(b.TagUids);
                    cmp = rankA.CompareTo(rankB);
                    break;
            }

            if (cmp == 0)
            {
                cmp = NaturalCompare(a.Name, b.Name);
            }
            return sortDescending ? -cmp : cmp;
        }

        private static int GetPrimaryTagRank(List<string> uids)
        {
            if (uids == null || uids.Count == 0) return 99;
            for (int i = 0; i < ORDERED_TAGS.Length; i++)
            {
                if (uids.Contains(ORDERED_TAGS[i].Uid)) return i;
            }
            return 99;
        }

        private static readonly Regex NATURAL_RE = new Regex(@"(\d+)", RegexOptions.Compiled);

        public static int NaturalCompare(string a, string b)
        {
            string[] pa = NATURAL_RE.Split(a ?? "");
            string[] pb = NATURAL_RE.Split(b ?? "");
            int len = Math.Min(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                if (pa[i] == pb[i]) continue;
                long na, nb;
                if (long.TryParse(pa[i], out na) && long.TryParse(pb[i], out nb))
                {
                    int cmp = na.CompareTo(nb);
                    if (cmp != 0) return cmp;
                }
                int scmp = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
                if (scmp != 0) return scmp;
            }
            return pa.Length.CompareTo(pb.Length);
        }
    }
}
