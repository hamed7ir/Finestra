using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — the FTPS server-certificate trust prompt, the TLS twin of <see cref="HostKeyDialog"/>:
    ///  • Unknown (first connect): host, subject, issuer, SHA256 thumbprint, validity, why the OS didn't auto-trust
    ///    it, and [Accept &amp; remember] / [Accept once] / [Cancel].
    ///  • Changed: a LOUD warning with old vs new thumbprint + a gated Replace (a deliberate action required).
    /// TOFU on the thumbprint — never accept-any. Returns a <see cref="CertDecision"/>; ✕/Esc ⇒ Reject.
    /// </summary>
    public sealed class CertTrustDialog : ThemedDialog
    {
        private CertDecision _result = CertDecision.Reject;

        public static CertDecision Ask(IWin32Window owner, CertPrompt p)
        {
            using (var d = new CertTrustDialog(p)) { d.ShowDialog(owner); return d._result; }
        }

        private CertTrustDialog(CertPrompt p)
            : base(p.Status == CertStatus.Changed ? "⚠  TLS certificate CHANGED" : "Unknown TLS certificate",
                   560, p.Status == CertStatus.Changed ? 520 : 470)
        {
            int w = 560 - 40;
            var rows = new List<Control>();
            string validity = p.NotAfter > DateTime.MinValue ? p.NotBefore.ToString("yyyy-MM-dd") + " … " + p.NotAfter.ToString("yyyy-MM-dd") : "?";

            if (p.Status == CertStatus.Changed)
            {
                rows.Add(new InfoBlock(w,
                    "WARNING — the TLS certificate of " + p.Host + ":" + p.Port + " has CHANGED.\n\n" +
                    "The certificate the server just presented does NOT match the one you trusted before. This can be a " +
                    "legitimate certificate renewal — or someone intercepting your connection.\n\n" +
                    "Do NOT continue unless you know why the certificate changed.", loud: true));
                rows.Add(new KvBlock("Previously trusted", p.OldSha256 ?? "?", w, mono: true));
                rows.Add(new KvBlock("Now presented", p.Subject + "\n" + p.Sha256, w, mono: true));

                var gate = new ToggleRow("I have verified this certificate change is expected", false);
                rows.Add(gate);
                var replace = AddFooterButton("Replace stored cert", RoundedButtonKind.Danger, DialogResult.None);
                replace.Width = 176; replace.Enabled = false;
                replace.Click += (s, e) => { _result = CertDecision.AcceptAndStore; DialogResult = DialogResult.OK; };
                var cancel = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
                cancel.Click += (s, e) => { _result = CertDecision.Reject; };
                gate.Changed += () => replace.Enabled = gate.On;
            }
            else
            {
                rows.Add(new InfoBlock(w,
                    "You are connecting to " + p.Host + ":" + p.Port + " over TLS for the first time. Verify this " +
                    "certificate belongs to the server you trust, then choose how to proceed.", loud: false));
                rows.Add(new KvBlock("Subject", p.Subject ?? "?", w, mono: false));
                rows.Add(new KvBlock("Issuer", p.Issuer ?? "?", w, mono: false));
                rows.Add(new KvBlock("Valid", validity, w, mono: false));
                rows.Add(new KvBlock("SHA256 thumbprint", p.Sha256, w, mono: true));
                if (!string.IsNullOrEmpty(p.PolicyErrors) && p.PolicyErrors != "None")
                    rows.Add(new KvBlock("Not auto-trusted because", p.PolicyErrors, w, mono: false));

                var remember = AddFooterButton("Accept & remember", RoundedButtonKind.Primary, DialogResult.None);
                remember.Width = 168;
                remember.Click += (s, e) => { _result = CertDecision.AcceptAndStore; DialogResult = DialogResult.OK; };
                var once = AddFooterButton("Accept once", RoundedButtonKind.Secondary, DialogResult.None);
                once.Width = 124;
                once.Click += (s, e) => { _result = CertDecision.AcceptOnce; DialogResult = DialogResult.OK; };
                var cancel = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
                cancel.Click += (s, e) => { _result = CertDecision.Reject; };
            }

            PopulateBody(rows.ToArray());
        }
    }
}
