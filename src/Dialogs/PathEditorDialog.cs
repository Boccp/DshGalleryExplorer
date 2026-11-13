// 快捷路径 / 归档库路径编辑对话框。
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
    internal sealed class PathEditorDialog : Form
    {
        private TextBox _labelBox;
        private TextBox _pathBox;
        public string EditedLabel { get { return _labelBox != null ? _labelBox.Text.Trim() : ""; } }
        public string EditedPath { get { return _pathBox != null ? _pathBox.Text.Trim() : ""; } }

        public PathEditorDialog(string title, string currentLabel, string currentPath, bool showLabelField)
        {
            Text = title;
            Size = new Size(560, showLabelField ? 235 : 180);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 27, 35);
            ForeColor = Color.White;

            int y = 16;
            if (showLabelField)
            {
                Label lblName = new Label
                {
                    Text = "显示名称:",
                    Location = new Point(16, y),
                    AutoSize = true,
                    Font = new Font("Microsoft YaHei UI", 9.5f)
                };
                _labelBox = new TextBox
                {
                    Text = currentLabel ?? "",
                    Location = new Point(16, y + 24),
                    Width = 510,
                    Font = new Font("Microsoft YaHei UI", 10f),
                    BackColor = Color.FromArgb(14, 16, 22),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                Controls.Add(lblName);
                Controls.Add(_labelBox);
                y += 62;
            }

            Label lblPath = new Label
            {
                Text = "目标文件夹路径 (可直接粘贴或点击右侧浏览):",
                Location = new Point(16, y),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            _pathBox = new TextBox
            {
                Text = currentPath ?? "",
                Location = new Point(16, y + 24),
                Width = 420,
                Font = new Font("Microsoft YaHei UI", 10f),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            Button btnBrowse = new Button
            {
                Text = "📂 浏览...",
                Location = new Point(442, y + 22),
                Size = new Size(84, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(42, 48, 62),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            btnBrowse.FlatAppearance.BorderColor = Color.FromArgb(70, 78, 96);
            btnBrowse.Click += (s, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = title;
                    if (!string.IsNullOrEmpty(_pathBox.Text) && Directory.Exists(_pathBox.Text))
                        fbd.SelectedPath = _pathBox.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
                    {
                        _pathBox.Text = fbd.SelectedPath;
                    }
                }
            };

            Button btnOk = new Button
            {
                Text = "保存",
                DialogResult = DialogResult.OK,
                Location = new Point(350, y + 66),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
            };
            Button btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(442, y + 66),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 50, 62),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lblPath, _pathBox, btnBrowse, btnOk, btnCancel });
        }
    }
}
