using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-SSH-AUTH — the themed host-key prompt shared by both TOFU states:
    ///  • <see cref="HostKeyStatus.Unknown"/> (first connect): host, key type, SHA256 fingerprint, and
    ///    [Accept &amp; remember] / [Accept once] / [Cancel]. Remember ⇒ store (that IS trust-on-first-use).
    ///  • <see cref="HostKeyStatus.Changed"/> (the MITM signal): a LOUD warning with old vs new fingerprints, a
    ///    safe default (Cancel), and a REPLACE button that stays disabled until the user ticks an explicit "I have
    ///    verified this change is expected" gate — so a changed key can never be trusted by blundering through one OK.
    /// Returns a <see cref="HostKeyDecision"/>; the ✕/Esc path returns Reject.
    /// </summary>
    public sealed class HostKeyDialog : ThemedDialog
    {
        private HostKeyDecision _result = HostKeyDecision.Reject;

        public static HostKeyDecision Ask(IWin32Window owner, HostKeyPrompt p)
        {
            using (var d = new HostKeyDialog(p)) { d.ShowDialog(owner); return d._result; }
        }

        private HostKeyDialog(HostKeyPrompt p)
            : base(p.Status == HostKeyStatus.Changed ? "⚠  SSH host key CHANGED" : "Unknown SSH host key",
                   540, p.Status == HostKeyStatus.Changed ? 486 : 372)
        {
            int w = 540 - 40;   // body width (dialog minus chrome/scrollbar margin)
            var rows = new List<Control>();

            if (p.Status == HostKeyStatus.Changed)
            {
                rows.Add(new InfoBlock(w,
                    "WARNING — the identity of " + p.Host + ":" + p.Port + " has CHANGED.\n\n" +
                    "The key the server just presented does NOT match the one you trusted before. This can happen if " +
                    "the server was legitimately rebuilt or rekeyed — or it can mean someone is intercepting your " +
                    "connection (a man-in-the-middle attack).\n\n" +
                    "Do NOT continue unless you know why the key changed.", loud: true));
                rows.Add(new KvBlock("Previously trusted", (p.OldKeyType ?? "?") + "\n" + (p.OldSha256 ?? "?"), w, mono: true));
                rows.Add(new KvBlock("Now presented", p.KeyType + "\n" + p.Sha256, w, mono: true));

                var gate = new ToggleRow("I have verified this key change is expected", false);
                rows.Add(gate);

                var replace = AddFooterButton("Replace stored key", RoundedButtonKind.Danger, DialogResult.None);
                replace.Width = 168;
                replace.Enabled = false;   // gated — a deliberate, explicit action is required
                replace.Click += (s, e) => { _result = HostKeyDecision.AcceptAndStore; DialogResult = DialogResult.OK; };
                var cancel = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
                cancel.Click += (s, e) => { _result = HostKeyDecision.Reject; };
                gate.Changed += () => replace.Enabled = gate.On;
            }
            else
            {
                rows.Add(new InfoBlock(w,
                    "You are connecting to " + p.Host + ":" + p.Port + " for the first time. Finestra has never " +
                    "seen this server's key.\n\n" +
                    "Verify the fingerprint below matches the server you trust, then choose how to proceed.", loud: false));
                rows.Add(new KvBlock("Key type", p.KeyType + (p.KeyBits > 0 ? "  (" + p.KeyBits + " bits)" : ""), w, mono: false));
                rows.Add(new KvBlock("SHA256 fingerprint", p.Sha256, w, mono: true));
                if (!string.IsNullOrEmpty(p.Md5)) rows.Add(new KvBlock("MD5 fingerprint (legacy)", p.Md5, w, mono: true));

                var remember = AddFooterButton("Accept & remember", RoundedButtonKind.Primary, DialogResult.None);
                remember.Width = 168;
                remember.Click += (s, e) => { _result = HostKeyDecision.AcceptAndStore; DialogResult = DialogResult.OK; };
                var once = AddFooterButton("Accept once", RoundedButtonKind.Secondary, DialogResult.None);
                once.Width = 124;
                once.Click += (s, e) => { _result = HostKeyDecision.AcceptOnce; DialogResult = DialogResult.OK; };
                var cancel = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
                cancel.Click += (s, e) => { _result = HostKeyDecision.Reject; };
            }

            PopulateBody(rows.ToArray());
        }
    }

    /// <summary>Word-wrapped body paragraph; <c>loud</c> paints it red + bold for the changed-key warning.</summary>
    internal sealed class InfoBlock : Control
    {
        private readonly string _text;
        private readonly bool _loud;

        public InfoBlock(int width, string text, bool loud)
        {
            _text = text ?? ""; _loud = loud;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Width = width;
            using (var f = FontHelper.Ui(loud ? 10f : 9.75f, loud ? FontStyle.Bold : FontStyle.Regular))
            {
                Size sz = TextRenderer.MeasureText(_text, f, new Size(width - 20, 4000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Height = sz.Height + 18;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            g.Clear(bg);
            if (_loud)
            {
                var band = new Rectangle(6, 4, Width - 14, Height - 10);
                using (var b = new SolidBrush(dark ? Color.FromArgb(58, 30, 30) : Color.FromArgb(252, 232, 232)))
                    g.FillRectangle(b, band);
                using (var p = new Pen(Color.FromArgb(210, 60, 60), 1.5f)) g.DrawRectangle(p, band);
            }
            Color fg = _loud ? Color.FromArgb(210, 60, 60)
                             : (dark ? Color.FromArgb(220, 220, 224) : Color.FromArgb(40, 40, 44));
            using (var f = FontHelper.Ui(_loud ? 10f : 9.75f, _loud ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, _text, f, new Rectangle(14, 10, Width - 28, Height - 16), fg,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    /// <summary>A caption + value block; <c>mono</c> renders the value in the monospace face (for fingerprints).</summary>
    internal sealed class KvBlock : Control
    {
        private readonly string _cap, _val;
        private readonly bool _mono;

        public KvBlock(string cap, string val, int width, bool mono)
        {
            _cap = cap ?? ""; _val = val ?? ""; _mono = mono;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Width = width;
            using (var vf = mono ? FontHelper.Mono(9.5f) : FontHelper.Ui(10.5f))
            {
                Size sz = TextRenderer.MeasureText(_val, vf, new Size(width - 20, 4000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Height = 22 + sz.Height + 8;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);
            Color fg = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(28, 28, 32);
            g.Clear(bg);
            using (var cf = FontHelper.Ui(9f))
                TextRenderer.DrawText(g, _cap, cf, new Rectangle(10, 4, Width - 20, 16), sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            using (var vf = _mono ? FontHelper.Mono(9.5f) : FontHelper.Ui(10.5f))
                TextRenderer.DrawText(g, _val, vf, new Rectangle(10, 22, Width - 20, Height - 28), fg,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
