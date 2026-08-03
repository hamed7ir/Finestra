using System;
using System.Windows.Forms;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-SSH-AUTH — a minimal themed prompt for an encrypted private key's passphrase, shown at connect time
    /// when the connection has no stored passphrase (the "prompt-at-connect by default" posture). Returns the
    /// entered passphrase, or null if cancelled. To persist a passphrase instead, enter it in the connection
    /// editor (it rides the same DPAPI slot as the password) — this prompt is deliberately session-only.
    /// </summary>
    public sealed class PassphrasePrompt : ThemedDialog
    {
        private readonly TextRow _pass;
        private string _value;

        public static string Ask(IWin32Window owner, string host, string keyPath)
        {
            using (var d = new PassphrasePrompt(host, keyPath))
                return d.ShowDialog(owner) == DialogResult.OK ? d._value : null;
        }

        private PassphrasePrompt(string host, string keyPath) : base("Key passphrase", 460, 268)
        {
            int w = 460 - 40;
            var info = new InfoBlock(w, "The private key for " + host + " is passphrase-protected.\n\n" +
                                        "Enter its passphrase to unlock it for this session.", loud: false);
            _pass = new TextRow("Passphrase", "", password: true);
            PopulateBody(info, _pass);

            var ok = AddFooterButton("Unlock", RoundedButtonKind.Primary, DialogResult.None);
            ok.Click += (s, e) => { _value = _pass.Value ?? ""; DialogResult = DialogResult.OK; };
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }
    }
}
