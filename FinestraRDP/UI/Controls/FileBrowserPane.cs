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
        private IReadOnlyList<RemoteEntry> _lastItems;   // FRDP-POLISH-4 — cached so a header-click re-sort is instant, no re-List()

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
        /// <summary>FRDP-POLISH-4/FRDP-FTP-RICH — the owner (FtpBrowserControl) appends the Copy/Cut/Paste items
        /// (which need the clipboard state + possibly the SIBLING pane/filesystem) to the SAME menu instance right
        /// before it's shown. This pane only ever builds the actions it can do alone (Select All/New folder/
        /// Rename/Delete/Refresh).</summary>
        public event Action<ThemedContextMenuStrip> ExtraContextItems;
        /// <summary>FRDP-FTP-RICH — Ctrl+C/Ctrl+X/Ctrl+V. Copy/Cut fire only with a non-empty selection (read
        /// <see cref="SelectedEntries"/> in the handler); Paste always fires (the owner decides if there's
        /// anything to paste).</summary>
        public event Action CopyRequested, CutRequested, PasteRequested;
        private bool _live = true;   // false = disconnected: remote nav/refresh/context disabled (SetLive)
        private SortColumn _sortCol = SortColumn.Name;
        private bool _sortAsc = true;

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
            _list.RenameRequested += () => RenameSel();
            _list.DeleteRequested += () => DeleteSel();
            _list.UpRequested += () => GoUp();
            _list.CopyRequested += () => CopyRequested?.Invoke();
            _list.CutRequested += () => CutRequested?.Invoke();
            _list.PasteRequested += () => PasteRequested?.Invoke();
            _list.SortRequested += (col, asc) => { _sortCol = col; _sortAsc = asc; if (_lastItems != null) Populate(_lastItems); };

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
        public IReadOnlyList<RemoteEntry> SelectedEntries => _list.SelectedEntries;

        private void ShowContextMenu(Point screen)
        {
            if (_fs == null || !_live) return;   // FRDP-RECONNECT — no remote ops while disconnected
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.5f) };
            var multi = SelectedEntries;
            var sel = Selected;
            menu.Items.Add(new ToolStripMenuItem("Refresh", null, (s, e) => Reload()) { ShortcutKeyDisplayString = "F5" });
            if (!string.IsNullOrEmpty(_path)) menu.Items.Add(new ToolStripMenuItem("Select All", null, (s, e) => _list.SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" });
            if (!string.IsNullOrEmpty(_path)) menu.Items.Add(new ToolStripMenuItem("New folder…", null, (s, e) => NewFolder()));
            ExtraContextItems?.Invoke(menu);   // FTP-BROWSER's owner appends Copy/Move/Download/Upload here
            if (multi.Count > 0 && !string.IsNullOrEmpty(_path))
            {
                if (_fs.CanRename && multi.Count == 1) menu.Items.Add(new ToolStripMenuItem("Rename…", null, (s, e) => RenameSel()) { ShortcutKeyDisplayString = "F2" });
                menu.Items.Add(new ToolStripMenuItem(multi.Count == 1 ? "Delete" : "Delete " + multi.Count + " items", null, (s, e) => DeleteSel()) { ShortcutKeyDisplayString = "Del" });
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

        /// <summary>FRDP-POLISH-4 — multi-select aware: deletes every currently-selected entry, one confirm dialog
        /// covering the whole batch. Stops (and reports) on the first failure rather than silently skipping it —
        /// a partial batch delete should never look like it fully succeeded.</summary>
        private void DeleteSel()
        {
            var items = SelectedEntries; if (items.Count == 0) return;
            string msg = items.Count == 1
                ? "Delete " + (items[0].IsDirectory ? "folder" : "file") + " '" + items[0].Name + "'?" + (items[0].IsDirectory ? "\n\nThis removes everything inside it." : "")
                : "Delete " + items.Count + " selected items?\n\nAny selected folders remove everything inside them.";
            if (!ConfirmDialog.Ask(FindForm(), msg, "Finestra — Files")) return;
            foreach (var it in items)
            {
                try { _fs.Delete(it.FullPath, it.IsDirectory); }
                catch (Exception ex) { ShowError("Could not delete '" + it.Name + "':\n" + ex.Message); break; }
            }
            Reload();
        }

        /// <summary>FRDP-POLISH-4 — dirs always sort first (unaffected by the column choice), then the clicked
        /// column ascending/descending. Caches the raw items so a header click re-sorts instantly without a
        /// fresh List() round-trip (Reload() re-fetches; a sort click alone should not touch the network).</summary>
        private void Populate(IReadOnlyList<RemoteEntry> items)
        {
            _lastItems = items;
            IOrderedEnumerable<RemoteEntry> byDir = items.OrderByDescending(e => e.IsDirectory);
            IOrderedEnumerable<RemoteEntry> ordered;
            switch (_sortCol)
            {
                case SortColumn.Size: ordered = _sortAsc ? byDir.ThenBy(e => e.Size) : byDir.ThenByDescending(e => e.Size); break;
                case SortColumn.Modified: ordered = _sortAsc ? byDir.ThenBy(e => e.Modified) : byDir.ThenByDescending(e => e.Modified); break;
                case SortColumn.Type: ordered = _sortAsc
                    ? byDir.ThenBy(e => e.IsDirectory ? "" : (e.IsSymlink ? "Link" : "File"), StringComparer.OrdinalIgnoreCase)
                    : byDir.ThenByDescending(e => e.IsDirectory ? "" : (e.IsSymlink ? "Link" : "File"), StringComparer.OrdinalIgnoreCase); break;
                default: ordered = _sortAsc
                    ? byDir.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    : byDir.ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase); break;
            }
            _list.SetEntries(ordered.ToList());
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
