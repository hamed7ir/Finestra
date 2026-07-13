using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — ONE pane of the dual-pane browser, bound to an <see cref="IRemoteFileSystem"/>. Used for
    /// BOTH sides (local via <see cref="LocalFileSystem"/>, remote via the SFTP/FTP backends) — so there are zero
    /// backend-specific branches here: it only ever calls the interface. Header = title + editable path + Up/Refresh;
    /// body = a fully OWNER-DRAWN <see cref="ThemedFileList"/> (FRDP-FTP-POLISH: dark scrollbar + header + glyphs,
    /// identical on 8.1 / 10-11, no native ListView). Navigation runs off the UI thread (a remote List can block).
    /// </summary>
    internal sealed class FileBrowserPane : Panel
    {
        private IRemoteFileSystem _fs;
        private string _path = "";

        private readonly Panel _header;
        private readonly TextBox _pathBox;
        private readonly RoundedButton _up, _refresh;
        private readonly ThemedFileList _list;
        private string _title = "";

        /// <summary>The current directory changed (after a successful navigate).</summary>
        public event Action PathChanged;
        /// <summary>The selection changed — the form re-evaluates the transfer buttons.</summary>
        public event Action SelectionChanged;
        /// <summary>FRDP-RECONNECT — a remote List failed (raised with the exception); the browser decides if it's a drop.</summary>
        public event Action<Exception> RemoteError;
        private bool _live = true;   // false = disconnected: remote nav/refresh/context disabled (SetLive)

        /// <summary>FRDP-RECONNECT — enable/disable remote ops (nav, refresh, context) while dropped/reconnecting. The
        /// list keeps showing the last listing; the local pane is never touched by this.</summary>
        public void SetLive(bool live) { _live = live; try { _up.Enabled = _refresh.Enabled = _pathBox.Enabled = live; } catch { } }

        public FileBrowserPane()
        {
            DoubleBuffered = true;
            _header = new Panel { Dock = DockStyle.Top, Height = 70 };
            _header.Paint += HeaderPaint;

            _pathBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(9.5f) };
            _pathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NavigateAsync(_pathBox.Text); } };
            _up = new RoundedButton { Text = "↑ Up", Kind = RoundedButtonKind.Neutral, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _up.Click += (s, e) => GoUp();
            _refresh = new RoundedButton { Text = "⟳", Kind = RoundedButtonKind.Neutral, Font = FontHelper.Ui(11f, FontStyle.Bold) };
            _refresh.Click += (s, e) => Reload();

            _list = new ThemedFileList { Dock = DockStyle.Fill };
            _list.SelectionChanged += () => SelectionChanged?.Invoke();
            _list.ItemActivated += e => { if (e.IsDirectory && _fs != null) NavigateAsync(e.FullPath); };
            _list.ContextRequested += screen => ShowContextMenu(screen);
            _list.ReloadRequested += () => Reload();

            _header.Controls.Add(_pathBox);
            _header.Controls.Add(_up);
            _header.Controls.Add(_refresh);
            Controls.Add(_list);       // Fill (added first)
            Controls.Add(_header);     // Top
            ApplyTheme();
            ThemeHelper.ThemeChanged += ApplyTheme;
        }

        public void SetTitle(string t) { _title = t ?? ""; _header.Invalidate(); }
        public void Bind(IRemoteFileSystem fs) { _fs = fs; }
        public IRemoteFileSystem Fs => _fs;
        public string CurrentPath => _path;
        /// <summary>Give the list keyboard focus (so an active FTP tab takes keys — FRDP-FTP-BUILD-2).</summary>
        public void FocusList() { try { _list.Focus(); } catch { } }

        public RemoteEntry? Selected => _list.Selected;

        // ── navigation (off the UI thread — a remote List can block) ──
        public void NavigateAsync(string path) { NavigateAsync(path, null); }

        public void NavigateAsync(string path, Action done)
        {
            if (_fs == null || !_live) return;   // FRDP-RECONNECT — no remote nav while disconnected
            SetBusy(true);
            Task.Run(() =>
            {
                IReadOnlyList<RemoteEntry> items = null; Exception err = null;
                try { items = _fs.List(path); } catch (Exception ex) { err = ex; }
                try
                {
                    if (!IsDisposed) BeginInvoke((Action)(() =>
                    {
                        SetBusy(false);
                        if (err != null) { RemoteError?.Invoke(err); ShowError("Could not open\n" + path + "\n\n" + err.Message); done?.Invoke(); return; }
                        _path = path ?? "";
                        _pathBox.Text = string.IsNullOrEmpty(_path) ? "(drives)" : _path;
                        Populate(items);
                        PathChanged?.Invoke();
                        done?.Invoke();
                    }));
                }
                catch { }
            });
        }

        public void GoUp() { if (_fs != null) NavigateAsync(_fs.Parent(_path)); }
        public void Reload() { if (_fs != null) NavigateAsync(_path); }

        // ── context actions (all via the interface — no backend branch) ──
        private void ShowContextMenu(Point screen)
        {
            if (_fs == null || !_live) return;   // FRDP-RECONNECT — no remote ops while disconnected
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.5f) };
            var sel = Selected;
            menu.Items.Add(new ToolStripMenuItem("Refresh", null, (s, e) => Reload()));
            if (!string.IsNullOrEmpty(_path)) menu.Items.Add(new ToolStripMenuItem("New folder…", null, (s, e) => NewFolder()));
            if (sel != null && !string.IsNullOrEmpty(_path))
            {
                if (_fs.CanRename) menu.Items.Add(new ToolStripMenuItem("Rename…", null, (s, e) => RenameSel()));
                menu.Items.Add(new ToolStripMenuItem("Delete", null, (s, e) => DeleteSel()));
            }
            menu.Show(screen);
        }

        private void NewFolder()
        {
            string name = InputDialog.Ask(FindForm(), "New folder", "Folder name", "");
            if (name == null) return;
            try { _fs.Mkdir(_fs.Combine(_path, name)); Reload(); } catch (Exception ex) { ShowError("Could not create the folder:\n" + ex.Message); }
        }

        private void RenameSel()
        {
            var sel = Selected; if (sel == null) return;
            string name = InputDialog.Ask(FindForm(), "Rename", "New name", sel.Value.Name);
            if (name == null || name == sel.Value.Name) return;
            try { _fs.Rename(sel.Value.FullPath, _fs.Combine(_path, name)); Reload(); } catch (Exception ex) { ShowError("Could not rename:\n" + ex.Message); }
        }

        private void DeleteSel()
        {
            var sel = Selected; if (sel == null) return;
            string what = sel.Value.IsDirectory ? "folder" : "file";
            if (!ConfirmDialog.Ask(FindForm(), "Delete " + what + " '" + sel.Value.Name + "'?" + (sel.Value.IsDirectory ? "\n\nThis removes everything inside it." : ""),
                "Finestra — Files")) return;
            try { _fs.Delete(sel.Value.FullPath, sel.Value.IsDirectory); Reload(); } catch (Exception ex) { ShowError("Could not delete:\n" + ex.Message); }
        }

        private void Populate(IReadOnlyList<RemoteEntry> items)
        {
            var sorted = items.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _list.SetEntries(sorted);
        }

        private void SetBusy(bool busy) { try { Cursor = busy ? Cursors.WaitCursor : Cursors.Default; _up.Enabled = _refresh.Enabled = _pathBox.Enabled = !busy; } catch { } }

        private void ShowError(string msg) { MessageBox.Show(FindForm(), msg, "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        // ── theming ──
        private void ApplyTheme()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ApplyTheme); } catch { } return; }
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(28, 28, 32) : Color.FromArgb(250, 250, 252);
            Color fg = dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(28, 28, 32);
            BackColor = bg; _header.BackColor = bg;
            _pathBox.BackColor = dark ? Color.FromArgb(44, 44, 50) : Color.White; _pathBox.ForeColor = fg;
            _header.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_pathBox == null) return;
            int rw = 44, uw = 60, pad = 8;
            _refresh.SetBounds(_header.Width - rw - pad, 34, rw, 28);
            _up.SetBounds(_header.Width - rw - uw - pad - 6, 34, uw, 28);
            _pathBox.SetBounds(pad, 36, _header.Width - rw - uw - 3 * pad - 6, 24);
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            using (var f = FontHelper.Ui(11f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, _title, f, new Rectangle(8, 6, _header.Width - 16, 22),
                    ThemeHelper.GetWindowsAccentColor(), TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= ApplyTheme;
            base.Dispose(disposing);
        }
    }
}
