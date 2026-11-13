// 重命名 / 新建文件夹 / 重命名快捷路径 通用输入对话框。
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
    internal sealed class RenameDialog : Form
    {
        private TextBox _box;
        public string NewName { get { return _box.Text.Trim(); } }

        public RenameDialog(string currentName)
        {
            Text = "重命名";
            Size = new Size(460, 165);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 27, 35);
            ForeColor = Color.White;

            Label lbl = new Label
            {
                Text = "请输入新名称:",
                Location = new Point(16, 16),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };

            _box = new TextBox
            {
                Text = currentName,
                Location = new Point(16, 44),
                Width = 410,
                Font = new Font("Microsoft YaHei UI", 10.5f),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            int dot = currentName.LastIndexOf('.');
            if (dot > 0)
            {
                _box.SelectionStart = 0;
                _box.SelectionLength = dot;
            }
            else
            {
                _box.SelectAll();
            }

            Button btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(250, 84),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White
            };
            Button btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(342, 84),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 50, 62),
                ForeColor = Color.White
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lbl, _box, btnOk, btnCancel });
        }
    }
}
