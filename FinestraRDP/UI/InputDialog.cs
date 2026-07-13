using System;
using System.Windows.Forms;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>FRDP-FTP-BUILD-1 — a minimal themed one-field prompt (New folder / Rename). Returns the entered
    /// text, or null if cancelled/blank.</summary>
    public sealed class InputDialog : ThemedDialog
    {
        private readonly TextRow _box;
        private string _value;

        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            using (var d = new InputDialog(title, label, initial))
            {
                var r = d.ShowDialog(owner);
                return r == DialogResult.OK && !string.IsNullOrWhiteSpace(d._value) ? d._value.Trim() : null;
            }
        }

        private InputDialog(string title, string label, string initial) : base(title, 420, 200)
        {
            _box = new TextRow(label, initial ?? "");
            PopulateBody(_box);
            var ok = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.None);
            ok.Click += (s, e) => { _value = _box.Value; DialogResult = DialogResult.OK; };
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }
    }
}
