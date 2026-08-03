using System;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-POLISH — the ✕ "close or minimize?" prompt. Themed (ThemedDialog). Two actions + a "Remember my
    /// choice" toggle: if remembered, MainForm writes the chosen action to <see cref="AppSettings.CloseAction"/>
    /// so it never asks again. That is NOT a one-way trap — the same setting is a visible, changeable dropdown in
    /// Settings ("When I close the window"), so a remembered choice is always undoable.
    /// </summary>
    public sealed class CloseChoiceDialog : ThemedDialog
    {
        public CloseAction Choice { get; private set; } = CloseAction.MinimizeToTray;
        public bool Remember => _remember.On;

        private readonly Label _msg;
        private readonly ToggleRow _remember;

        public CloseChoiceDialog() : base("Close Finestra", 440, 288)
        {
            _msg = new Label
            {
                Text = "Keep Finestra running in the tray, or exit completely?\n\nMinimizing keeps your active sessions alive.",
                AutoSize = false,
                Height = 104,
                Padding = new Padding(6, 4, 6, 0),
                Font = FontHelper.Ui(10f),
                TextAlign = ContentAlignment.TopLeft
            };
            _remember = new ToggleRow("Remember my choice", false);

            PopulateBody(_msg, _remember);

            var minimize = AddFooterButton("Minimize to tray", RoundedButtonKind.Primary, DialogResult.None);
            minimize.Width = 150;
            minimize.Click += (s, e) => { Choice = CloseAction.MinimizeToTray; DialogResult = DialogResult.OK; };
            var exit = AddFooterButton("Exit", RoundedButtonKind.Danger, DialogResult.None);
            exit.Click += (s, e) => { Choice = CloseAction.Exit; DialogResult = DialogResult.OK; };

            ApplyMsgTheme();
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            ApplyMsgTheme();
        }

        private void ApplyMsgTheme()
        {
            if (_msg == null) return;
            bool dark = ThemeHelper.IsDark;
            _msg.ForeColor = dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
            _msg.BackColor = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
        }
    }
}
