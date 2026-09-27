using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RankedDuelsCompanion
{
    // The small status window plus the tray icon. Closing the window only
    // hides it; the app keeps watching the save file from the tray.
    class MainForm : Form
    {
        static readonly Color Bg = Color.FromArgb(0x09, 0x09, 0x0b);
        static readonly Color Surface = Color.FromArgb(0x11, 0x11, 0x14);
        static readonly Color Border = Color.FromArgb(0x2a, 0x2a, 0x30);
        static readonly Color TextColor = Color.FromArgb(0xed, 0xed, 0xf0);
        static readonly Color Muted = Color.FromArgb(0x8a, 0x8a, 0x95);
        static readonly Color Accent = Color.FromArgb(0xe5, 0x48, 0x4d);
        static readonly Color Win = Color.FromArgb(0x46, 0xc4, 0x6b);

        const int ContentWidth = 460;              // every box gets the same width
        const int DebounceMs = 4000;              // WoW writes the file in bursts on logout
        const int PollMs = 2 * 60 * 1000;          // safety net if a file event is missed
        const int UpdateCheckMs = 12 * 60 * 60 * 1000;

        readonly Settings settings = Settings.Load();
        readonly bool startHidden;
        bool quitting;

        string retail;
        FileSystemWatcher watcher;
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = DebounceMs };
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = PollMs };
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = UpdateCheckMs };
        bool syncing, syncAgain;
        CancellationTokenSource login;
        string accountDetail;

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Label lblAccount = new Label(), lblAccountDetail = new Label(), lblFiles = new Label();
        readonly Label lblStatus = new Label(), lblStatusTime = new Label();
        readonly Button btnLogin, btnFolder, btnWebsite;
        readonly CheckBox chkStartup = new CheckBox(), chkUpdates = new CheckBox();
        readonly LinkLabel linkUpdate = new LinkLabel(), linkUninstall = new LinkLabel();
        readonly ToolTip tips = new ToolTip();

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = Install.AppName;
            Icon = AppIcon();
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9.75F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(22, 18, 22, 14);

            btnLogin = MakeButton("Log in with Battle.net", primary: true);
            btnFolder = MakeButton("Change\u2026", primary: false);
            btnWebsite = MakeButton("Open website", primary: false);

            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
            };
            stack.Controls.Add(Header());
            stack.Controls.Add(Section("BATTLE.NET ACCOUNT", Stack(lblAccount, lblAccountDetail), btnLogin));
            stack.Controls.Add(Section("SAVE FILE", lblFiles, btnFolder));
            stack.Controls.Add(Section("LAST UPLOAD", Stack(lblStatus, lblStatusTime), null));
            stack.Controls.Add(Actions());
            stack.Controls.Add(Footer());
            Controls.Add(stack);

            foreach (var l in new[] { lblAccount, lblStatus })
            {
                l.AutoSize = true;
                l.MaximumSize = new Size(270, 0);
                l.Margin = new Padding(0, 0, 0, 2);
            }
            lblAccount.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            foreach (var l in new[] { lblAccountDetail, lblStatusTime, lblFiles })
            {
                l.AutoSize = true;
                l.MaximumSize = new Size(270, 0);
                l.ForeColor = Muted;
                l.Margin = Padding.Empty;
            }

            btnLogin.Click += async (s, e) => await LoginClicked();
            btnFolder.Click += (s, e) => PickFolder();
            btnWebsite.Click += (s, e) => OpenUrl(Api.Website);
            chkStartup.CheckedChanged += (s, e) =>
            {
                try { Install.StartsWithWindows = chkStartup.Checked; } catch { }
            };
            chkUpdates.Checked = !settings.UpdateCheckOff;
            chkUpdates.CheckedChanged += async (s, e) =>
            {
                settings.UpdateCheckOff = !chkUpdates.Checked;
                settings.Save();
                if (settings.UpdateCheckOff) linkUpdate.Visible = false;
                else await CheckForUpdate();
            };
            linkUpdate.LinkClicked += (s, e) => OpenUrl(Api.Website + "/download/RankedDuelsCompanion.exe");
            linkUninstall.LinkClicked += (s, e) =>
            {
                if (Install.Uninstall(interactive: true)) Quit();
            };

            SetupTray();
            ResumeLayout(true);

            debounce.Tick += async (s, e) => { debounce.Stop(); await SyncAsync(manual: false); };
            poll.Tick += async (s, e) => await SyncAsync(manual: false);
            clock.Tick += (s, e) => ShowLastResult();
            updateTimer.Tick += async (s, e) => await CheckForUpdate();
            poll.Start();
            clock.Start();
            updateTimer.Start();

            ListenForOtherCopies();
        }

        // ------------------------------------------------------------------
        // Start-up
        // ------------------------------------------------------------------
        bool loaded;

        protected override async void OnLoad(EventArgs e)
        {
            if (loaded) return; // started hidden: we ran this already, don't again on first Show()
            loaded = true;
            base.OnLoad(e);

            if (!settings.Installed)
            {
                try { Install.StartsWithWindows = true; } catch { }
                settings.Installed = true;
                settings.Save();
            }
            try { chkStartup.Checked = Install.StartsWithWindows; } catch { }

            FindWow();
            ShowAccount();
            ShowLastResult();

            if (settings.Session != null)
            {
                await RefreshAccountAsync();
                await SyncAsync(manual: false);
            }
            await CheckForUpdate();
        }

        // Autostart (--minimized) keeps the window hidden; the tray icon still shows.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated)
            {
                CreateHandle();
                value = false;
                OnLoad(EventArgs.Empty);
            }
            base.SetVisibleCore(value);
        }

        void FindWow()
        {
            retail = SaveFiles.FindRetail(settings.WowFolder);
            watcher?.Dispose();
            watcher = null;

            if (retail != null && Directory.Exists(SaveFiles.AccountFolder(retail)))
            {
                watcher = new FileSystemWatcher(SaveFiles.AccountFolder(retail), SaveFiles.FileName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (s, e) => BeginInvoke((Action)(() => { debounce.Stop(); debounce.Start(); }));
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += (s, e) => changed(s, e);
                watcher.EnableRaisingEvents = true;
            }
            ShowFiles();
        }

        void ShowFiles()
        {
            if (retail == null)
            {
                lblFiles.Text = "World of Warcraft wasn't found. Click Change\u2026 and pick your World of Warcraft folder.";
                lblFiles.ForeColor = Accent;
                return;
            }
            lblFiles.ForeColor = Muted;
            var files = SaveFiles.Find(retail);
            lblFiles.Text = files.Count == 0
                ? "No Ranked Duels save file yet. Duel with the addon, then log out or /reload."
                : string.Join("\n", files.Select(f => "WoW account " + SaveFiles.AccountName(f)));
            tips.SetToolTip(lblFiles, files.Count == 0 ? SaveFiles.AccountFolder(retail) : string.Join("\n", files));
        }

        // ------------------------------------------------------------------
        // Login
        // ------------------------------------------------------------------
        async Task LoginClicked()
        {
            if (login != null) { login.Cancel(); return; }        // "Cancel" while waiting
            if (settings.Session != null) { Logout(null); return; } // "Log out"

            login = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            btnLogin.Text = "Cancel";
            lblAccount.Text = "Logging in\u2026";
            lblAccountDetail.Text = "Finish logging in with Battle.net in your browser.";
            try
            {
                settings.Session = await Api.LoginAsync(login.Token);
                settings.Save();
                BringToFront();
                Activate();
                await RefreshAccountAsync();
                await SyncAsync(manual: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't log in: " + ex.Message, Install.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                login.Dispose();
                login = null;
                ShowAccount();
            }
        }

        void Logout(string reason)
        {
            settings.Session = null;
            settings.Battletag = null;
            settings.UploadedHashes.Clear();
            settings.Save();
            accountDetail = reason;
            ShowAccount();
        }

        async Task RefreshAccountAsync()
        {
            var session = settings.Session;
            if (session == null) return;
            try
            {
                var me = await Api.Me(session);
                settings.Battletag = me.TryGetValue("battletag", out var bt) ? bt as string : null;
                settings.Save();
                int onLadder = Api.List(me, "characters").OfType<Dictionary<string, object>>()
                    .Count(c => Api.Int(c, "wins") + Api.Int(c, "losses") > 0);
                int pending = Api.Int(me, "pending");
                var parts = new List<string> { Plural(onLadder, "character") + " on the ladder" };
                if (pending > 0) parts.Add(Plural(pending, "duel") + " waiting for the opponent to upload");
                accountDetail = string.Join(" \u00B7 ", parts);
            }
            catch (NotLoggedInException)
            {
                Logout("Your login expired. Please log in again.");
                return;
            }
            catch
            {
                // Offline: keep showing what we had.
            }
            ShowAccount();
        }

        void ShowAccount()
        {
            bool loggedIn = settings.Session != null;
            if (login != null) return;
            btnLogin.Text = loggedIn ? "Log out" : "Log in with Battle.net";
            StyleButton(btnLogin, primary: !loggedIn);
            lblAccount.Text = loggedIn ? (settings.Battletag ?? "Logged in") : "Not logged in";
            lblAccountDetail.Text = loggedIn
                ? (accountDetail ?? "")
                : (accountDetail ?? "Log in so the website knows which characters are yours.");
            lblAccountDetail.Visible = lblAccountDetail.Text.Length > 0;
        }

        // ------------------------------------------------------------------
        // Sync
        // ------------------------------------------------------------------
        async Task SyncAsync(bool manual)
        {
            if (syncing)
            {
                syncAgain = true;
                return;
            }
            syncing = true;
            try
            {
                do
                {
                    syncAgain = false;
                    await SyncOnce(manual);
                    manual = false;
                } while (syncAgain);
            }
            finally
            {
                syncing = false;
            }
        }

        async Task SyncOnce(bool manual)
        {
            var session = settings.Session;
            if (session == null) return;
            if (retail == null) FindWow();
            if (retail == null) return;

            ShowFiles();
            var files = SaveFiles.Find(retail);
            if (files.Count == 0)
            {
                if (manual) SetResult("No save file to upload yet.", ok: false);
                return;
            }

            if (manual) lblStatus.Text = "Uploading\u2026";
            int newReports = 0, confirmed = 0, newSkirmishes = 0, uploaded = 0, oldOnly = 0;
            var notYours = new HashSet<string>();
            string problem = null;

            foreach (var file in files)
            {
                string text;
                try
                {
                    text = await Task.Run(() => SaveFiles.Read(file));
                }
                catch
                {
                    problem = $"Couldn't open the save file of WoW account {SaveFiles.AccountName(file)}.";
                    continue;
                }
                var hash = SaveFiles.Hash(text);
                if (!manual && settings.UploadedHashes.TryGetValue(file, out var done) && done == hash) continue;

                try
                {
                    var r = await Api.Upload(session, text);
                    settings.UploadedHashes[file] = hash;
                    uploaded++;
                    newReports += Api.Int(r, "newReports");
                    confirmed += Api.Int(r, "newlyConfirmed");
                    newSkirmishes += Api.Int(r, "newSkirmishes");
                    if (Api.Int(r, "received") == 0 && Api.Int(r, "fileDuels") > 0) oldOnly++;
                    foreach (var n in Api.List(r, "notYourCharacters").OfType<string>()) notYours.Add(n);
                }
                catch (NotLoggedInException)
                {
                    Logout("Your login expired. Please log in again.");
                    Notify("Please log in again", "Ranked Duels Companion can't upload your duels until you log in.");
                    return;
                }
                catch (ApiException ex) when (ex.Code == "not_ranked_duels_file")
                {
                    settings.UploadedHashes[file] = hash; // nothing to do until it changes
                }
                catch (ApiException ex) when (ex.Code == "unreadable_file")
                {
                    problem = "The save file looked half-written. Trying again soon."; // WoW still saving
                }
                catch (Exception)
                {
                    problem = "Couldn't reach the website. Trying again in a few minutes.";
                }
            }

            if (uploaded == 0)
            {
                if (problem != null) SetResult(problem, ok: false);
                else if (manual) SetResult("Nothing new to upload.", ok: true);
                return;
            }

            var lines = new List<string>();
            if (newReports > 0) lines.Add(Plural(newReports, "new ranked duel") + " uploaded");
            if (confirmed > 0) lines.Add($"{confirmed} confirmed by both players and now on the ladder");
            if (newSkirmishes > 0) lines.Add(Plural(newSkirmishes, "new skirmish duel") + " saved");
            if (lines.Count == 0) lines.Add(oldOnly > 0 ? "No duels from addon 1.1 or newer yet." : "Nothing new since the last upload.");
            if (notYours.Count > 0) lines.Add($"Skipped {string.Join(", ", notYours)}: not on this Battle.net account");
            if (problem != null) lines.Add(problem);

            settings.LastSyncUtc = DateTime.UtcNow;
            SetResult(string.Join("\n", lines), ok: problem == null);

            if (newReports + confirmed + newSkirmishes > 0)
                Notify("Duels uploaded", string.Join("\n", lines.Take(3)));
            await RefreshAccountAsync();
        }

        void SetResult(string text, bool ok)
        {
            settings.LastResult = text;
            settings.Save();
            ShowLastResult();
            lblStatus.ForeColor = ok ? TextColor : Accent;
        }

        void ShowLastResult()
        {
            lblStatus.Text = settings.LastResult ?? (settings.Session == null ? "Log in to start uploading." : "Nothing uploaded yet.");
            lblStatusTime.Text = settings.LastSyncUtc.HasValue
                ? "Last upload " + TimeAgo(settings.LastSyncUtc.Value)
                : "Duels upload by themselves after you /reload or log out of WoW.";
        }

        // ------------------------------------------------------------------
        // Folder, updates, tray
        // ------------------------------------------------------------------
        void PickFolder()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Pick your World of Warcraft folder (the one with _retail_ inside).",
                ShowNewFolderButton = false,
                SelectedPath = retail ?? "",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var picked = SaveFiles.AsRetail(dlg.SelectedPath);
                if (picked == null)
                {
                    MessageBox.Show(this, "That doesn't look like a World of Warcraft folder. Pick the folder that has _retail_ inside.",
                        Install.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.WowFolder = picked;
                settings.Save();
                FindWow();
            }
            _ = SyncAsync(manual: false);
        }

        async Task CheckForUpdate()
        {
            if (settings.UpdateCheckOff) return; // the player turned it off: no request at all
            var latest = await Api.LatestVersion();
            if (latest != null && new Version(latest) > new Version(Install.Version))
            {
                linkUpdate.Text = $"Update available (v{latest}) \u2014 download";
                linkUpdate.Visible = true;
            }
        }

        void SetupTray()
        {
            tray.Icon = AppIcon();
            tray.Text = Install.AppName;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowWindow());
            menu.Items.Add("Sync now", null, async (s, e) => await SyncAsync(manual: true));
            menu.Items.Add("Open website", null, (s, e) => OpenUrl(Api.Website));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            menu.Items[0].Font = new Font(menu.Font, FontStyle.Bold);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowWindow();
            tray.BalloonTipClicked += (s, e) => ShowWindow();
            tray.Visible = true;
        }

        void Notify(string title, string text)
        {
            tray.ShowBalloonTip(6000, title, text, ToolTipIcon.None);
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void Quit()
        {
            quitting = true;
            tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!settings.TrayHintShown)
                {
                    Notify("Still running", "Ranked Duels Companion keeps uploading from the tray by the clock. Right-click its icon to quit.");
                    settings.TrayHintShown = true;
                    settings.Save();
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // A second launch asks us to show the window; an update asks us to quit.
        void ListenForOtherCopies()
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
            new Thread(() =>
            {
                for (;;)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { show, quit });
                    try
                    {
                        if (which == 0) BeginInvoke((Action)ShowWindow);
                        else { BeginInvoke((Action)Quit); return; }
                    }
                    catch (InvalidOperationException) { return; } // window already gone
                }
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------------
        // Look
        // ------------------------------------------------------------------
        static Icon AppIcon()
        {
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
                return new Icon(s);
        }

        Control Header()
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            var pic = new PictureBox
            {
                Image = new Icon(AppIcon(), 48, 48).ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(44, 44),
                Margin = new Padding(0, 0, 10, 0),
            };
            var title = new Label { Text = "RANKED DUELS", AutoSize = true, Font = new Font(Font.FontFamily, 15F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 0) };
            // Special characters in this file are written as \u escapes, so a file-encoding slip can't garble them.
            var sub = new Label { Text = "Companion \u00B7 v" + Install.Version, AutoSize = true, ForeColor = Muted, Margin = Padding.Empty };
            row.Controls.Add(pic);
            row.Controls.Add(Stack(title, sub));
            return row;
        }

        Control Section(string caption, Control body, Button button)
        {
            var box = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                BackColor = Surface,
                Padding = new Padding(14, 10, 12, 12),
                Margin = new Padding(0, 0, 0, 10),
                MinimumSize = new Size(ContentWidth, 0), MaximumSize = new Size(ContentWidth, 0),
            };
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            box.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var cap = new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = Muted,
                Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 6),
            };
            box.Controls.Add(cap, 0, 0);
            box.SetColumnSpan(cap, 2);
            box.Controls.Add(body, 0, 1);
            if (button != null)
            {
                button.Anchor = AnchorStyles.Right | AnchorStyles.Top;
                box.Controls.Add(button, 1, 1);
            }
            box.Paint += (s, e) =>
            {
                using (var pen = new Pen(Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            };
            return box;
        }

        static Control Stack(params Control[] children)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = Padding.Empty,
            };
            p.Controls.AddRange(children);
            return p;
        }

        Control Actions()
        {
            // No "Sync now" here: uploads happen by themselves. It's still in the tray menu.
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 2, 0, 8), MinimumSize = new Size(ContentWidth, 0), MaximumSize = new Size(ContentWidth, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            chkStartup.Text = "Start with Windows";
            chkStartup.AutoSize = true;
            chkStartup.ForeColor = Muted;
            chkStartup.Anchor = AnchorStyles.Right;
            chkUpdates.Text = "Check for updates";
            chkUpdates.AutoSize = true;
            chkUpdates.ForeColor = Muted;
            chkUpdates.Anchor = AnchorStyles.Right;
            tips.SetToolTip(chkUpdates, "Asks rankedduels.io for the newest version number at start-up and every 12 hours. Nothing else is sent.");
            row.Controls.Add(btnWebsite, 0, 0);
            row.Controls.Add(chkStartup, 1, 0);
            row.Controls.Add(chkUpdates, 1, 1);
            return row;
        }

        Control Footer()
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty, MinimumSize = new Size(ContentWidth, 0), MaximumSize = new Size(ContentWidth, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var l in new[] { linkUpdate, linkUninstall })
            {
                l.AutoSize = true;
                l.LinkBehavior = LinkBehavior.HoverUnderline;
                l.Margin = Padding.Empty;
            }
            linkUpdate.LinkColor = linkUpdate.ActiveLinkColor = Win;
            linkUpdate.Visible = false;
            linkUninstall.Text = "Uninstall";
            linkUninstall.LinkColor = linkUninstall.ActiveLinkColor = Muted;
            linkUninstall.Anchor = AnchorStyles.Right;
            row.Controls.Add(linkUpdate, 0, 0);
            row.Controls.Add(linkUninstall, 1, 0);
            return row;
        }

        Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Padding = new Padding(8, 3, 8, 3),
                Margin = Padding.Empty,
            };
            StyleButton(b, primary);
            return b;
        }

        static void StyleButton(Button b, bool primary)
        {
            b.BackColor = primary ? Accent : Surface;
            b.ForeColor = primary ? Color.White : TextColor;
            b.Font = new Font(b.Font, primary ? FontStyle.Bold : FontStyle.Regular);
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.1f) : Border;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { } // dark title bar
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

        static string TimeAgo(DateTime utc)
        {
            var s = (DateTime.UtcNow - utc).TotalSeconds;
            if (s < 60) return "just now";
            if (s < 3600) return Plural((int)(s / 60), "minute") + " ago";
            if (s < 86400) return Plural((int)(s / 3600), "hour") + " ago";
            return Plural((int)(s / 86400), "day") + " ago";
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}

