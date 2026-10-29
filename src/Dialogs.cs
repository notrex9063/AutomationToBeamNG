// Folder setup (first run) and settings, the success screen, and the app icon.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutomationToBeamNG
{
    class SetupForm : Form
    {
        readonly Settings settings;
        readonly TextBox autoBox, gameBox, modsBox, repoBox;
        readonly Label autoState, gameState, modsState;
        readonly ComboBox themeBox;
        readonly AccentButton ok;

        public SetupForm(Settings settings, bool firstRun)
        {
            this.settings = settings;
            Text = firstRun ? "Welcome" : "Folders & settings";
            Font = Ui.Base;
            Ui.ThemeWindow(this);
            BackColor = Ui.Card;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.Px(660), Ui.Px(firstRun ? 470 : 760));
            Icon = AppIcon.Get();

            var st = Ui.Stack();
            st.Padding = new Padding(Ui.Px(24), Ui.Px(18), Ui.Px(24), Ui.Px(16));
            Ui.AddRow(st, Ui.MakeLabel(firstRun ? "Welcome! Let's find your games." : "Folders", Ui.Title, Ui.Text), 34);
            Ui.AddRow(st, Ui.MakeLabel(firstRun
                ? "These were found automatically. Check they look right, then press Continue. You can change them later."
                : "Where the converter reads engines from and installs them to.", Ui.Base, Ui.Muted), 40);

            autoBox = Row(st, "Automation exports", "The folder Automation saves exported .engine files in. Its results are read from the folder above it.", settings.AutomationPath, out autoState);
            gameBox = Row(st, "BeamNG.drive game folder", "The folder that contains BeamNG.drive.exe.", settings.BeamNGPath, out gameState);
            modsBox = Row(st, "BeamNG mods folder", "Converted engines are installed here. Usually found automatically.", settings.ModsPath, out modsState);

            if (!firstRun)
            {
                Ui.AddRow(st, Ui.MakeLabel("Appearance", Ui.Title, Ui.Text), 36);
                var theme = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.Card };
                theme.Controls.Add(new Label { Text = "Theme", AutoSize = true, ForeColor = Ui.Text, Margin = new Padding(0, Ui.Px(6), Ui.Px(12), 0) });
                themeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(220), FlatStyle = FlatStyle.Flat };
                themeBox.Items.AddRange(new object[] { "System", "Light", "Dark" });
                themeBox.SelectedItem = settings.Theme;
                if (themeBox.SelectedIndex < 0) themeBox.SelectedIndex = 0;
                Ui.ThemeControl(themeBox);
                theme.Controls.Add(themeBox);
                Ui.AddRow(st, theme, 34);

                Ui.AddRow(st, Ui.MakeLabel("Shortcuts", Ui.Title, Ui.Text), 36);
                var sc = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.Card };
                var startBtn = new PlainButton { Text = "Add to Start menu", Size = new Size(Ui.Px(170), Ui.Px(32)), Margin = new Padding(0, 0, Ui.Px(10), 0) };
                var deskBtn = new PlainButton { Text = "Add Desktop shortcut", Size = new Size(Ui.Px(180), Ui.Px(32)), Margin = Padding.Empty };
                startBtn.Margin = new Padding(0, 0, Ui.Px(10), 0);
                startBtn.Click += delegate { MakeShortcut(Shortcuts.StartMenuPath, "Start menu"); };
                deskBtn.Click += delegate { MakeShortcut(Shortcuts.DesktopPath, "Desktop"); };
                sc.Controls.Add(startBtn);
                sc.Controls.Add(deskBtn);
                Ui.AddRow(st, sc, 38);

                Ui.AddRow(st, Ui.MakeLabel("Updates", Ui.Title, Ui.Text), 36);
                Ui.AddRow(st, Ui.MakeLabel("You're on version " + Updates.Current + ". If this app is published on GitHub, enter its repository (owner/name) to be told about new versions.", Ui.Small, Ui.Muted), 34);
                var up = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
                up.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                up.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(130)));
                repoBox = new TextBox { Text = settings.UpdateRepo, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Px(3), Ui.Px(8), 0) };
                Ui.ThemeControl(repoBox);
                Ui.SetPlaceholder(repoBox, "owner/repository");
                var check = new PlainButton { Text = "Check now", Dock = DockStyle.Fill };
                check.Click += async delegate
                {
                    string repo = repoBox.Text.Trim();
                    check.Enabled = false;
                    Version latest = null; string url = null, msg = null;
                    bool found = await Task.Run(() => Updates.TryGetLatest(repo, out latest, out url, out msg));
                    check.Enabled = true;
                    if (!found) { MessageBox.Show(this, msg, "Updates"); return; }
                    if (latest > Updates.Current)
                    {
                        if (MessageBox.Show(this, "Version " + latest + " is available (you have " + Updates.Current + ").\n\nOpen the download page?", "Updates",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && !string.IsNullOrEmpty(url)) Process.Start(url);
                    }
                    else MessageBox.Show(this, "You have the latest version (" + Updates.Current + ").", "Updates");
                };
                up.Controls.Add(repoBox, 0, 0);
                up.Controls.Add(check, 1, 0);
                Ui.AddRow(st, up, 32);
            }

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Ui.Px(10), 0, 0), BackColor = Ui.Card };
            ok = new AccentButton { Text = firstRun ? "Continue" : "Save", Size = new Size(Ui.Px(130), Ui.Px(38)) };
            var cancel = new PlainButton { Text = "Cancel", Size = new Size(Ui.Px(100), Ui.Px(38)), DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, Ui.Px(10), 0) };
            ok.Click += delegate
            {
                settings.AutomationPath = autoBox.Text.Trim();
                settings.BeamNGPath = gameBox.Text.Trim();
                settings.ModsPath = modsBox.Text.Trim();
                if (themeBox != null) settings.Theme = (string)themeBox.SelectedItem;
                if (repoBox != null) settings.UpdateRepo = repoBox.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Ui.AddRow(st, new Panel { BackColor = Ui.Card }, -1);
            Ui.AddRow(st, buttons, 58);
            Controls.Add(st);
            AcceptButton = ok;
            CancelButton = cancel;

            autoBox.TextChanged += delegate { Validate3(); };
            gameBox.TextChanged += delegate { Validate3(); };
            modsBox.TextChanged += delegate { Validate3(); };
            Validate3();
        }

        void MakeShortcut(string path, string where)
        {
            try
            {
                Shortcuts.Create(path);
                MessageBox.Show(this, "Added a shortcut to the " + where + ".", "Shortcuts");
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't create the shortcut: " + ex.Message, "Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        TextBox Row(TableLayoutPanel st, string title, string hint, string value, out Label state)
        {
            Ui.AddRow(st, Ui.MakeLabel(title, Ui.Bold, Ui.Text), 24);
            Ui.AddRow(st, Ui.MakeLabel(hint, Ui.Small, Ui.Muted), 20);
            var line = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(96)));
            var box = new TextBox { Text = value ?? "", Dock = DockStyle.Fill, Font = Ui.Base, Margin = new Padding(0, Ui.Px(3), Ui.Px(8), 0) };
            Ui.ThemeControl(box);
            var browse = new PlainButton { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Px(1), 0, Ui.Px(1)) };
            browse.Click += delegate
            {
                using (var d = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
                {
                    if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
                }
            };
            line.Controls.Add(box, 0, 0);
            line.Controls.Add(browse, 1, 0);
            Ui.AddRow(st, line, 32);
            state = Ui.AddRow(st, Ui.MakeLabel("", Ui.Small, Ui.Muted), 30);
            state.TextAlign = ContentAlignment.TopLeft;
            return box;
        }

        static void Mark(Label l, bool good, string text)
        {
            l.Text = (good ? "✓  " : "✗  ") + text;
            l.ForeColor = good ? Ui.Good : Ui.Bad;
        }

        void Validate3()
        {
            string a = autoBox.Text.Trim(), g = gameBox.Text.Trim(), m = modsBox.Text.Trim();
            bool aOk = AppPaths.IsFolder(a), gOk = AppPaths.IsBeamNG(g), mOk = m.Length > 0;
            if (aOk)
            {
                int n = 0;
                try { n = Directory.GetFiles(a, "*.engine", SearchOption.AllDirectories).Length; } catch (Exception) { }
                Mark(autoState, true, n == 0 ? "Folder found (no .engine files in it yet)" : n + " engine file" + (n == 1 ? "" : "s") + " found");
            }
            else Mark(autoState, false, a.Length == 0 ? "Not set" : "This folder doesn't exist");
            if (gOk) Mark(gameState, true, "BeamNG.drive found");
            else Mark(gameState, false, g.Length == 0 ? "Not found automatically - press Browse" : "This isn't a BeamNG.drive folder (no content\\vehicles inside)");
            if (mOk) Mark(modsState, true, Directory.Exists(m) ? "Mods folder found" : "Will be created when you install");
            else Mark(modsState, false, "Not set");
            ok.Enabled = aOk && gOk && mOk;
            ok.Invalidate();
        }
    }

    class DoneForm : Form
    {
        public DoneForm(EngineData data, ConvertResult res, ConvertOptions opt)
        {
            var spec = data.Spec;
            Text = "Installed";
            Font = Ui.Base;
            Ui.ThemeWindow(this);
            BackColor = Ui.Card;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            bool notes = res.Warnings.Count > 0 || res.Problems.Count > 0;
            ClientSize = new Size(Ui.Px(600), Ui.Px(notes ? 560 : 440));
            Icon = AppIcon.Get();

            var st = Ui.Stack();
            st.Padding = new Padding(Ui.Px(28), Ui.Px(22), Ui.Px(28), Ui.Px(18));
            var tick = new Control { BackColor = Ui.Card };
            tick.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Ui.Px(44);
                using (var b = new SolidBrush(Ui.Good)) g.FillEllipse(b, 0, 0, d, d);
                using (var p = new Pen(Color.White, Ui.Px(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new PointF(d * 0.28f, d * 0.52f), new PointF(d * 0.44f, d * 0.68f), new PointF(d * 0.73f, d * 0.36f) });
                TextRenderer.DrawText(g, "Engine installed!", Ui.Title, new Point(d + Ui.Px(14), Ui.Px(2)), Ui.Text);
                TextRenderer.DrawText(g, spec.Name + " was added to " + res.Built + " car" + (res.Built == 1 ? "" : "s") + ".", Ui.Base, new Point(d + Ui.Px(14), Ui.Px(26)), Ui.Muted);
            };
            Ui.AddRow(st, tick, 58);

            var check = Ui.MakeLabel("✓  " + res.FilesChecked + " files checked the way BeamNG reads them" + (res.Problems.Count == 0 ? " — no problems." : "."), Ui.Base, Ui.Good);
            Ui.AddRow(st, check, 26);

            Ui.AddRow(st, Ui.MakeLabel("How to use it in BeamNG.drive", Ui.Bold, Ui.Text), 30);
            string spawn = res.Configs > 0
                ? "2.  Open the vehicle selector and pick \"" + spec.Name + " (Automation)\" under one of the cars,\n     or spawn a car and choose it in Vehicle Config  →  Parts  →  Engine.\n"
                : "2.  Spawn one of the cars you picked.\n3.  Open Vehicle Config  →  Parts  →  Engine and choose \"" + spec.Name + " … (Automation)\".\n";
            string first = res.LiveMessage != null
                ? "1.  Already in your running game: " + res.LiveMessage + "\n"
                : "1.  Start BeamNG.drive (restart it if it was already running).\n";
            var steps = Ui.MakeLabel(first + spawn
                + (opt.SimulateBoost && data.IsBoosted ? "\nBoost is simulated by BeamNG, so you'll see it on the boost gauge and hear it." : "")
                + (res.EngineModelFrom != null ? "\nAutomation's own 3D engine model is fitted (from your export \"" + res.EngineModelFrom + "\")." : "")
                + (opt.Upgrades ? "\nTune it in Vehicle Config  →  Tuning  →  Engine; upgrades (ECU, launch control, internals, turbo kits) are under Parts  →  Engine." : ""), Ui.Base, Ui.Text);
            steps.TextAlign = ContentAlignment.TopLeft;
            Ui.AddRow(st, steps, opt.Upgrades ? 132 : 110);

            if (notes)
            {
                Ui.AddRow(st, Ui.MakeLabel("Good to know", Ui.Bold, Ui.Warn), 26);
                var w = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Ui.WarnBg,
                    ForeColor = Ui.Warn, Font = Ui.Small, Text = string.Join("\r\n", res.Problems.Concat(res.Warnings).Select(x => "•  " + x)),
                };
                Ui.AddRow(st, w, -1);
            }
            else Ui.AddRow(st, new Panel { BackColor = Ui.Card }, -1);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Ui.Px(12), 0, 0), BackColor = Ui.Card };
            var close = new AccentButton { Text = "Done", Size = new Size(Ui.Px(110), Ui.Px(38)), DialogResult = DialogResult.OK };
            var open = new PlainButton { Text = "Open mods folder", Size = new Size(Ui.Px(150), Ui.Px(38)), Margin = new Padding(0, 0, Ui.Px(10), 0) };
            open.Click += delegate { MainForm.OpenFolder(Path.GetDirectoryName(res.ZipPath)); };
            buttons.Controls.Add(close);
            buttons.Controls.Add(open);
            Ui.AddRow(st, buttons, 62);
            Controls.Add(st);
            AcceptButton = close;
        }
    }

    static class AppIcon
    {
        static Icon cached;

        // The icon baked into the exe; falls back to drawing it.
        public static Icon Get()
        {
            if (cached != null) return cached;
            try { cached = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            return cached ?? (cached = Draw(64));
        }

        // A blue rounded square with a white torque-curve stroke.
        public static Icon Draw(int size)
        {
            using (var bmp = Render(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size);
            float k = size / 64f;
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(k, k);
                using (var path = Ui.Rounded(new Rectangle(2, 2, 59, 59), 14))
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 45f))
                    g.FillPath(b, path);
                using (var p = new Pen(Color.White, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawBezier(p, 12, 46, 22, 12, 38, 10, 52, 28);
                using (var p = new Pen(Color.FromArgb(253, 186, 116), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawBezier(p, 12, 52, 26, 50, 40, 40, 52, 20);
            }
            return bmp;
        }
    }
}
