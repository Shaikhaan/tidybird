using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tidybird
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            bool firstInstance;
            using (var mutex = new Mutex(true, "Tidybird.SingleInstance", out firstInstance))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Tidybird.Show"))
            {
                // A second launch just brings the running window forward
                if (!firstInstance) { showSignal.Set(); return; }

                SetProcessDPIAware();
                Sorter.SelfPath = Application.ExecutablePath;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args.Contains("--tray"), showSignal));
            }
        }
    }

    class Settings
    {
        public string Folder;
        public string DateFormat = "";
        public bool AutoSort;
        public bool TrayTipShown;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tidybird", "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings { Folder = KnownFolders.Downloads() };
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                    if (key == "Folder" && value.Length > 0) s.Folder = value;
                    else if (key == "DateFormat") s.DateFormat = value;
                    else if (key == "AutoSort") s.AutoSort = value == "1";
                    else if (key == "TrayTipShown") s.TrayTipShown = value == "1";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "Folder=" + Folder,
                    "DateFormat=" + DateFormat,
                    "AutoSort=" + (AutoSort ? "1" : "0"),
                    "TrayTipShown=" + (TrayTipShown ? "1" : "0"),
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static class KnownFolders
    {
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        // Real Downloads location, which may be moved or redirected (e.g. to OneDrive)
        public static string Downloads()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
            {
                string path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return path;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    class MainForm : Form
    {
        static readonly Color Accent = ColorTranslator.FromHtml("#0F766E");
        static readonly Color AccentDark = ColorTranslator.FromHtml("#134E4A");
        static readonly Color HeaderBack = ColorTranslator.FromHtml("#F0FDFA");
        static readonly Color Muted = ColorTranslator.FromHtml("#64748B");

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly TimeSpan AutoSortMinAge = TimeSpan.FromMinutes(1);

        static readonly KeyValuePair<string, string>[] DateOptions =
        {
            new KeyValuePair<string, string>("Type only", ""),
            new KeyValuePair<string, string>("Type, then month  (2026-10)", "yyyy-MM"),
            new KeyValuePair<string, string>("Type, then year  (2026)", "yyyy"),
            new KeyValuePair<string, string>("Type, then day  (2026-10-31)", "yyyy-MM-dd"),
        };

        readonly Settings settings = Settings.Load();
        bool hiddenStart, quitting, loading = true;

        TextBox folderBox;
        ComboBox dateBox;
        CheckBox autoBox, startupBox;
        ListView list;
        Label status;
        Button sortButton, undoButton;
        NotifyIcon tray;
        ToolStripMenuItem trayAuto;
        FileSystemWatcher watcher;
        System.Windows.Forms.Timer refreshTimer, autoTimer;

        public MainForm(bool startInTray, EventWaitHandle showSignal)
        {
            hiddenStart = startInTray;
            Text = "Tidybird";
            Icon = LoadIcon(new Size(32, 32));
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 580);
            MinimumSize = new Size(640, 460);
            BuildLayout();
            BuildTray();
            ResumeLayout(true);

            refreshTimer = new System.Windows.Forms.Timer { Interval = 800 };
            refreshTimer.Tick += delegate { refreshTimer.Stop(); RefreshPreview(); };
            autoTimer = new System.Windows.Forms.Timer { Interval = 20000 };
            autoTimer.Tick += delegate { AutoSortTick(); };
            autoTimer.Start();

            folderBox.Text = settings.Folder;
            int dateIndex = Array.FindIndex(DateOptions, o => o.Value == settings.DateFormat);
            dateBox.SelectedIndex = dateIndex < 0 ? 0 : dateIndex;
            autoBox.Checked = settings.AutoSort;
            trayAuto.Checked = settings.AutoSort;
            startupBox.Checked = IsStartupEnabled();
            loading = false;

            StartWatcher();

            ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
            {
                // A late signal can arrive while the window is shutting down
                try { if (!IsDisposed) BeginInvoke((Action)ShowFromTray); }
                catch (InvalidOperationException) { }
            }, null, -1, false);
        }

        // Icon embedded by build.ps1 (/resource), picked at the exact size so it stays sharp
        static Icon LoadIcon(Size size)
        {
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("Tidybird.ico"))
                return s != null ? new Icon(s, size) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) RefreshPreview();
        }

        // ---------- Layout ----------

        void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // Header
            var header = new Panel { Dock = DockStyle.Fill, Height = 92, BackColor = HeaderBack, Margin = Padding.Empty };
            var logo = new PictureBox { Image = Logo.Render(128), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(22, 14, 64, 64) };
            var name = new Label { Text = "Tidybird", AutoSize = true, Location = new Point(96, 14), ForeColor = AccentDark,
                Font = new Font("Segoe UI Semibold", 20f) };
            var tagline = new Label { Text = "Your downloads, neatly nested.", AutoSize = true, Location = new Point(100, 54), ForeColor = Accent };
            header.Controls.AddRange(new Control[] { logo, name, tagline });
            root.Controls.Add(header);

            // Options
            var opts = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 3, Padding = new Padding(16, 12, 16, 0) };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 3; i++) opts.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            folderBox = new TextBox { ReadOnly = true, TabStop = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.White, Margin = new Padding(3, 3, 3, 3) };
            var browse = MakeButton("Change…", false);
            browse.Anchor = AnchorStyles.Left;
            browse.Click += delegate { ChooseFolder(); };
            opts.Controls.Add(MakeLabel("Folder"), 0, 0);
            opts.Controls.Add(folderBox, 1, 0);
            opts.Controls.Add(browse, 2, 0);

            dateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(3, 5, 3, 3) };
            foreach (var o in DateOptions) dateBox.Items.Add(o.Key);
            dateBox.SelectedIndexChanged += delegate
            {
                if (loading) return;
                settings.DateFormat = DateOptions[dateBox.SelectedIndex].Value;
                settings.Save();
                RefreshPreview();
            };
            opts.Controls.Add(MakeLabel("Sort into"), 0, 1);
            opts.Controls.Add(dateBox, 1, 1);

            var checks = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            autoBox = new CheckBox { Text = "Auto-sort new downloads", AutoSize = true, Margin = new Padding(3, 3, 24, 3) };
            autoBox.CheckedChanged += delegate { if (!loading) SetAutoSort(autoBox.Checked); };
            startupBox = new CheckBox { Text = "Start with Windows", AutoSize = true };
            startupBox.CheckedChanged += delegate { if (!loading) SetStartup(startupBox.Checked); };
            checks.Controls.Add(autoBox);
            checks.Controls.Add(startupBox);
            opts.Controls.Add(checks, 1, 2);
            opts.SetColumnSpan(checks, 2);
            root.Controls.Add(opts);

            // Preview list
            list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(16, 8, 16, 8), ShowItemToolTips = true,
            };
            list.Columns.Add("File", 300);
            list.Columns.Add("Moves to", 260);
            list.Columns.Add("Modified", 150);
            root.Controls.Add(list);

            // Footer
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 0, 16, 12) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoEllipsis = true,
                Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var refresh = MakeButton("Refresh", false);
            refresh.Click += delegate { RefreshPreview(); };
            undoButton = MakeButton("Undo last sort", false);
            undoButton.Click += delegate { UndoLast(); };
            sortButton = MakeButton("Tidy up now", true);
            sortButton.Click += delegate { SortNow(); };
            buttons.Controls.AddRange(new Control[] { refresh, undoButton, sortButton });
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(buttons, 1, 0);
            root.Controls.Add(footer);

            AcceptButton = sortButton;
            ActiveControl = sortButton;
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open Tidybird", null, delegate { ShowFromTray(); });
            open.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Tidy up now", null, delegate { SortNow(); });
            trayAuto = new ToolStripMenuItem("Auto-sort new downloads", null, delegate { autoBox.Checked = !autoBox.Checked; });
            menu.Items.Add(trayAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { quitting = true; Close(); });

            tray = new NotifyIcon { Icon = LoadIcon(SystemInformation.SmallIconSize), Text = "Tidybird", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
        }

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted, Margin = new Padding(0, 6, 12, 3) };
        }

        static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Padding = new Padding(10, 3, 10, 3), Margin = new Padding(6, 3, 0, 3),
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : AccentDark,
            };
            b.FlatAppearance.BorderColor = primary ? Accent : ColorTranslator.FromHtml("#CBD5E1");
            b.FlatAppearance.MouseOverBackColor = primary ? AccentDark : HeaderBack;
            if (primary) b.Font = new Font("Segoe UI Semibold", 9.75f);
            return b;
        }

        // ---------- Window and tray behaviour ----------

        protected override void SetVisibleCore(bool value)
        {
            // Started with --tray: stay hidden but still create the handle for BeginInvoke
            if (hiddenStart)
            {
                hiddenStart = false;
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // With auto-sort on, closing the window keeps Tidybird running in the tray
            if (!quitting && settings.AutoSort && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!settings.TrayTipShown)
                {
                    tray.ShowBalloonTip(5000, "Tidybird is still sorting",
                        "New downloads keep getting tidied. Right-click the tray icon to quit.", ToolTipIcon.Info);
                    settings.TrayTipShown = true;
                    settings.Save();
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ---------- Sorting ----------

        string DateFormat
        {
            get { return DateOptions[Math.Max(0, dateBox.SelectedIndex)].Value; }
        }

        bool FolderOk()
        {
            if (Directory.Exists(settings.Folder)) return true;
            SetStatus("Folder not found: " + settings.Folder);
            return false;
        }

        void RefreshPreview()
        {
            if (!Visible) return;
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            try
            {
                if (!FolderOk()) { sortButton.Enabled = false; undoButton.Enabled = false; return; }

                var moves = Sorter.Plan(settings.Folder, DateFormat, TimeSpan.Zero);
                foreach (var g in moves.GroupBy(m => m.Category))
                {
                    int n = g.Count();
                    var group = new ListViewGroup(g.Key + "  ·  " + n + (n == 1 ? " file" : " files"));
                    list.Groups.Add(group);
                    foreach (var m in g)
                    {
                        string dest = m.DestDir.Substring(settings.Folder.Length).TrimStart('\\') + "\\";
                        var item = new ListViewItem(new[] { Path.GetFileName(m.Source), dest,
                            m.Modified.ToString("g") }, group);
                        item.ToolTipText = m.Source;
                        list.Items.Add(item);
                    }
                }

                sortButton.Enabled = moves.Count > 0;
                undoButton.Enabled = Sorter.CanUndo(settings.Folder);
                SetStatus(moves.Count == 0
                    ? "Nothing to tidy. The nest is neat."
                    : moves.Count + (moves.Count == 1 ? " file" : " files") + " ready to tidy.");
            }
            catch (Exception ex)
            {
                SetStatus("Could not read folder: " + ex.Message);
            }
            finally
            {
                list.EndUpdate();
            }
        }

        void SortNow()
        {
            if (!FolderOk()) return;
            try
            {
                var result = Sorter.Apply(settings.Folder, Sorter.Plan(settings.Folder, DateFormat, TimeSpan.Zero));
                RefreshPreview();
                string msg = "Tidied " + result.Moved + (result.Moved == 1 ? " file" : " files");
                if (result.Failed > 0) msg += ", skipped " + result.Failed + " in use";
                SetStatus(msg + ".");
                if (!Visible && result.Moved > 0)
                    tray.ShowBalloonTip(3000, "Tidybird", msg + " into " + string.Join(", ", result.Categories) + ".", ToolTipIcon.None);
            }
            catch (Exception ex)
            {
                SetStatus("Sort failed: " + ex.Message);
            }
        }

        void AutoSortTick()
        {
            if (!settings.AutoSort || !Directory.Exists(settings.Folder)) return;
            try
            {
                var moves = Sorter.Plan(settings.Folder, DateFormat, AutoSortMinAge);
                if (moves.Count == 0) return;
                var result = Sorter.Apply(settings.Folder, moves);
                if (result.Moved == 0) return;
                string msg = "Tidied " + result.Moved + (result.Moved == 1 ? " file" : " files") +
                    " into " + string.Join(", ", result.Categories) + ".";
                if (Visible) { RefreshPreview(); SetStatus(msg); }
                else tray.ShowBalloonTip(3000, "Tidybird", msg, ToolTipIcon.None);
            }
            catch (Exception) { }
        }

        void UndoLast()
        {
            if (!FolderOk()) return;
            try
            {
                // Restored files would be re-sorted straight away, so pause auto-sort first
                bool paused = settings.AutoSort;
                if (paused) autoBox.Checked = false;
                int failed;
                int restored = Sorter.Undo(settings.Folder, out failed);
                RefreshPreview();
                if (restored < 0) { SetStatus("Nothing to undo."); return; }
                string msg = "Put back " + restored + (restored == 1 ? " file" : " files");
                if (failed > 0) msg += "; " + failed + " in use, close them and undo again";
                SetStatus(msg + (paused ? ". Auto-sort paused so they stay put." : "."));
            }
            catch (Exception ex)
            {
                SetStatus("Undo failed: " + ex.Message);
            }
        }

        void SetStatus(string text)
        {
            status.Text = text;
        }

        // ---------- Settings ----------

        void ChooseFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose the folder Tidybird keeps tidy", SelectedPath = settings.Folder })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                settings.Folder = dlg.SelectedPath;
                settings.Save();
                folderBox.Text = settings.Folder;
                StartWatcher();
                RefreshPreview();
            }
        }

        void SetAutoSort(bool on)
        {
            settings.AutoSort = on;
            settings.Save();
            trayAuto.Checked = on;
            if (on) AutoSortTick();
        }

        // Watches the folder only to keep the preview live; auto-sort runs on its own timer
        void StartWatcher()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (!Directory.Exists(settings.Folder)) return;
            try
            {
                watcher = new FileSystemWatcher(settings.Folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    SynchronizingObject = this,
                };
                // Throttle, not debounce: a download writing nonstop must not starve the refresh
                FileSystemEventHandler changed = delegate { if (!refreshTimer.Enabled) refreshTimer.Start(); };
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += delegate { if (!refreshTimer.Enabled) refreshTimer.Start(); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception) { watcher = null; }
        }

        static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue("Tidybird") != null;
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue("Tidybird", "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue("Tidybird", false);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not change startup setting: " + ex.Message);
            }
        }
    }
}
