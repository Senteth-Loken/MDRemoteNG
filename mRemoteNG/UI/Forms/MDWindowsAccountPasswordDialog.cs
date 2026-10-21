using System;
using System.Drawing;
using System.Windows.Forms;

namespace mRemoteNG.UI.Forms
{
    /// <summary>Collects an existing account password in memory for the chosen host setup.</summary>
    public sealed class MDWindowsAccountPasswordDialog : Form
    {
        private readonly TextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 512, Dock = DockStyle.Fill };
        private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(420, 0) };
        public string Password { get; private set; } = "";

        public MDWindowsAccountPasswordDialog(string accountName)
        {
            Text = "Configure my Windows account";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            layout.Controls.Add(new Label { Text = accountName, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(420, 0), Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) });
            layout.Controls.Add(new Label { Text = "Enter this Windows account's password. A Windows Hello PIN is not its password.", AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 12, 3, 10) });
            layout.Controls.Add(_password);
            layout.Controls.Add(new Label { Text = "These credentials will be saved for matching PIN holders on your local network. Remote access uses this account's existing permissions.", AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 12, 3, 10) });
            layout.Controls.Add(_error);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
            var configure = new Button { Text = "Configure host", AutoSize = true };
            configure.Click += OnAccept;
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(configure);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = configure;
            CancelButton = cancel;
            Shown += (_, _) => _password.Focus();
            FormClosed += (_, _) => _password.Clear();
        }

        private void OnAccept(object sender, EventArgs e)
        {
            if (_password.Text.Length == 0)
            {
                _error.Text = "Enter your Windows password to continue.";
                return;
            }
            Password = _password.Text;
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _password.Clear();
            Password = "";
            base.Dispose(disposing);
        }
    }
}
