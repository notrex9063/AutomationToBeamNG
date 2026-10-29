// The main window. "Convert": (1) pick an engine, (2) check its numbers, (3) pick cars, install.
// "Installed engines": what this app already put in the mods folder, with update/remove.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutomationToBeamNG
{
    class MainForm : Form
    {
        readonly Settings settings;
        readonly List<string> extraFiles = new List<string>();   // engines added from outside the export folder
        readonly List<string> log = new List<string>();
        readonly Dictionary<string, EngineSpec> specs = new Dictionary<string, EngineSpec>(StringComparer.OrdinalIgnoreCase);
        List<VehicleInfo> cars = new List<VehicleInfo>();
        EngineSpec spec;
        EngineData data;
        bool fillingCars, busy, loadingEngine;

        Panel convertPage, installedPage, runningBanner, updateBanner, outdatedBanner;
        NavTab tabConvert, tabInstalled;
        LinkLabel updateLink;
        // step 1
        SmoothListView engineList;
        Label engineEmpty;
        // step 2
        Label engineName, calWarning;
        FlowLayoutPanel chips;
        StatTile tilePower, tileTorque, tileRedline;
        EngineChart chart;
        ComboBox compareBox, soundBox;
        CheckBox useDyno, simBoost, makeConfigs, useModel, fitToCar, addUpgrades;
        Dictionary<string, ExportedEngine> exports = new Dictionary<string, ExportedEngine>(StringComparer.OrdinalIgnoreCase);
        NumericUpDown numTorque, numPower;
        // step 3
        TextBox carSearch;
        SmoothListView carList;
        Label carCount;
        ContextMenuStrip baseMenu;
        // installed page
        SmoothListView modList;
        PlainButton modUpdate, modRemove, modReinstall;
        // footer
        Label status;
        ProgressBar progress;
        AccentButton installBtn;

        public MainForm(Settings settings, string startFile)
        {
            this.settings = settings;
            if (!string.IsNullOrEmpty(startFile) && File.Exists(startFile))
            {
                if (!IsInExportFolder(startFile)) extraFiles.Add(startFile);
                settings.LastEngine = startFile;
            }

            Text = "Automation → BeamNG.drive Engine Converter";
            Font = Ui.Base;
            Ui.ThemeWindow(this);
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Ui.Px(1240), Ui.Px(860));
            MinimumSize = new Size(Ui.Px(1020), Ui.Px(700));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon.Get();
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            convertPage = BuildConvertPage();
            installedPage = BuildInstalledPage();
            installedPage.Visible = false;
            Controls.Add(convertPage);     // fill first, then the edges
            Controls.Add(installedPage);
            Controls.Add(BuildFooter());
            Controls.Add(BuildBanners());
            Controls.Add(BuildHeader());

            var poll = new Timer { Interval = 3000 };
            poll.Tick += delegate { UpdateRunningBanner(); };
            poll.Start();
            Shown += OnShown;
        }

        // ------------------------------------------------------------------ layout

        Control BuildHeader()
        {
            var p = new Panel { Dock = DockStyle.Top, Height = Ui.Px(68), BackColor = Ui.Header };
            var title = new Label { Text = "Automation  →  BeamNG.drive", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Color.White, AutoSize = true, Location = new Point(Ui.Px(20), Ui.Px(10)) };
            var sub = new Label { Text = "Fit engines you designed in Automation to BeamNG cars.", Font = Ui.Small, ForeColor = Ui.HeaderSub, AutoSize = true, Location = new Point(Ui.Px(22), Ui.Px(42)) };
            tabConvert = new NavTab("Convert") { Selected = true };
            tabInstalled = new NavTab("Installed engines");
            tabConvert.Picked += delegate { ShowPage(false); };
            tabInstalled.Picked += delegate { ShowPage(true); };
            var btn = new Button
            {
                Text = "⚙  Folders && settings", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Ui.HeaderButton,
                Font = Ui.Base, Size = new Size(Ui.Px(170), Ui.Px(34)), Cursor = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor = Ui.HeaderButtonHover;
            btn.FlatAppearance.MouseOverBackColor = Ui.HeaderButtonHover;
            btn.Click += delegate { OpenSetup(false); };
            p.Controls.AddRange(new Control[] { title, sub, tabConvert, tabInstalled, btn });
            p.Resize += delegate
            {
                btn.Location = new Point(p.Width - btn.Width - Ui.Px(20), Ui.Px(17));
                int x = Math.Max(title.Right, sub.Right) + Ui.Px(40);
                tabConvert.Location = new Point(x, p.Height - tabConvert.Height);
                tabInstalled.Location = new Point(tabConvert.Right + Ui.Px(4), p.Height - tabInstalled.Height);
            };
            return p;
        }

        Control BuildBanners()
        {
            var host = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            runningBanner = Banner("BeamNG.drive is running. Installs go straight into the game (if it was started before live updates were added, restart it once).", null, out updateLink);
            updateBanner = Banner("A new version is available.", "Download", out updateLink);
            LinkLabel reinstallLink;
            outdatedBanner = Banner("Some installed engines are out of date.", "Reinstall all", out reinstallLink);
            reinstallLink.LinkClicked += delegate { ReinstallAll(); };
            runningBanner.Visible = updateBanner.Visible = outdatedBanner.Visible = false;
            host.Controls.Add(outdatedBanner);
            host.Controls.Add(updateBanner);
            host.Controls.Add(runningBanner);
            return host;
        }

        Panel Banner(string text, string linkText, out LinkLabel link)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = Ui.Px(34), BackColor = Ui.WarnBg, Padding = new Padding(Ui.Px(20), 0, Ui.Px(20), 0) };
            var l = new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Ui.Warn, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.Base };
            link = null;
            if (linkText != null)
            {
                link = new LinkLabel { Text = linkText, Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, LinkColor = Ui.Accent, Font = Ui.Bold, Padding = new Padding(0, Ui.Px(8), 0, 0) };
                p.Controls.Add(link);
            }
            p.Controls.Add(l);
            l.BringToFront();
            return p;
        }

        Control BuildFooter()
        {
            var p = new Panel { Dock = DockStyle.Bottom, Height = Ui.Px(66), BackColor = Ui.Card };
            p.Paint += (s, e) => { using (var pen = new Pen(Ui.Border)) e.Graphics.DrawLine(pen, 0, 0, p.Width, 0); };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(Ui.Px(20), Ui.Px(12), Ui.Px(20), Ui.Px(12)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(160)));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(270)));
            status = Ui.MakeLabel("", Ui.Base, Ui.Muted);
            status.AutoEllipsis = true;
            progress = new ProgressBar { Style = ProgressBarStyle.Continuous, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = Ui.Px(8), Visible = false };
            var logLink = Ui.MakeLink("View log", delegate { ShowLog(); });
            logLink.Anchor = AnchorStyles.None;
            logLink.Margin = new Padding(Ui.Px(12), 0, Ui.Px(16), 0);
            installBtn = new AccentButton { Text = "Install into BeamNG", Dock = DockStyle.Fill, Enabled = false };
            installBtn.Click += delegate { InstallSelected(); };
            t.Controls.Add(status, 0, 0);
            t.Controls.Add(progress, 1, 0);
            t.Controls.Add(logLink, 2, 0);
            t.Controls.Add(installBtn, 3, 0);
            p.Controls.Add(t);
            return p;
        }

        Panel BuildConvertPage()
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(Ui.Px(10)), BackColor = Ui.Bg };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(BuildEngineCard(), 0, 0);
            body.Controls.Add(BuildDetailsCard(), 1, 0);
            body.Controls.Add(BuildCarsCard(), 2, 0);
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            page.Controls.Add(body);
            return page;
        }

        Control BuildEngineCard()
        {
            var card = new CardPanel();
            var st = Ui.Stack();
            Ui.AddRow(st, new StepHeader("1", "Choose an engine", "Engines you exported from Automation"), 50);

            var host = new Panel();
            engineList = new SmoothListView { Dock = DockStyle.Fill, MultiSelect = false };
            engineList.Columns.Add("Engine", Ui.Px(140));
            engineList.Columns.Add("Type", Ui.Px(70));
            engineList.Columns.Add("Power", Ui.Px(66), HorizontalAlignment.Right);
            engineList.Columns.Add("Torque", Ui.Px(70), HorizontalAlignment.Right);
            engineList.SelectedIndexChanged += delegate { OnEngineSelected(); };
            engineList.Resize += delegate { FitFirstColumn(engineList); };
            engineEmpty = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Muted, Visible = false, BackColor = Ui.Card,
                Text = "No engines found yet.\n\nIn Automation, open your engine and use\nExport, then press Refresh below —\nor drag a .engine file onto this window.",
            };
            host.Controls.Add(engineEmpty);
            host.Controls.Add(engineList);
            Ui.AddRow(st, host, -1);

            Ui.AddRow(st, Ui.MakeLabel("≈ estimated (Automation's results weren't found)", Ui.Small, Ui.Muted), 22);
            var links = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            links.Controls.Add(Ui.MakeLink("Refresh", delegate { AutomationDb.ClearCache(); specs.Clear(); LoadEngines(); ScanExports(); }));
            links.Controls.Add(Ui.MakeLink("Add .engine file…", delegate { AddEngineFiles(); }));
            links.Controls.Add(Ui.MakeLink("Open folder", delegate { OpenFolder(settings.AutomationPath); }));
            Ui.AddRow(st, links, 30);
            card.Controls.Add(st);
            return card;
        }

        Control BuildDetailsCard()
        {
            var card = new CardPanel();
            var st = Ui.Stack();
            Ui.AddRow(st, new StepHeader("2", "Check the engine", "What BeamNG will get — hover the chart for exact numbers"), 50);
            engineName = Ui.AddRow(st, Ui.MakeLabel("No engine selected", Ui.Big, Ui.Text), 36);
            engineName.AutoEllipsis = true;
            chips = Ui.AddRow(st, new FlowLayoutPanel { WrapContents = true, Padding = new Padding(0, Ui.Px(2), 0, 0), BackColor = Ui.Card }, 64);

            var tiles = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            tilePower = new StatTile("PEAK POWER", Ui.Power) { Dock = DockStyle.Fill };
            tileTorque = new StatTile("PEAK TORQUE", Ui.Accent) { Dock = DockStyle.Fill };
            tileRedline = new StatTile("REV LIMIT", Ui.Bad) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            tiles.Controls.Add(tilePower, 0, 0);
            tiles.Controls.Add(tileTorque, 1, 0);
            tiles.Controls.Add(tileRedline, 2, 0);
            Ui.AddRow(st, tiles, 76);

            // compare + sound pickers
            var pick = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, Ui.Px(6), 0, 0) };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            compareBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Font = Ui.Base };
            soundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Font = Ui.Base, MaxDropDownItems = 20 };
            Ui.ThemeControl(compareBox);
            Ui.ThemeControl(soundBox);
            compareBox.SelectedIndexChanged += delegate { UpdateCompare(); };
            soundBox.SelectedIndexChanged += delegate { OnSoundPicked(); };
            pick.Controls.Add(SmallLabel("Compare with"), 0, 0);
            pick.Controls.Add(compareBox, 1, 0);
            pick.Controls.Add(SmallLabel("Sound"), 2, 0);
            pick.Controls.Add(soundBox, 3, 0);
            Ui.AddRow(st, pick, 38);

            chart = Ui.AddRow(st, new EngineChart(), -1);

            // numbers source + options
            var opt = new TableLayoutPanel { ColumnCount = 6, RowCount = 5, Margin = Padding.Empty, BackColor = Ui.Soft, Padding = new Padding(Ui.Px(10), Ui.Px(6), Ui.Px(10), Ui.Px(4)) };
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(84)));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(84)));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (int h in new[] { 28, 32, 36, 26, 26, 26, 26, 26 }) opt.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Px(h)));
            useDyno = Ui.MakeCheck("Type in Automation's dyno numbers", false);
            useDyno.Anchor = AnchorStyles.Left;
            opt.Controls.Add(useDyno, 0, 0);
            opt.SetColumnSpan(useDyno, 6);
            numTorque = new NumericUpDown { Maximum = 50000, DecimalPlaces = 0, Width = Ui.Px(80), Anchor = AnchorStyles.Left, Enabled = false };
            numPower = new NumericUpDown { Maximum = 50000, DecimalPlaces = 0, Width = Ui.Px(80), Anchor = AnchorStyles.Left, Enabled = false };
            Ui.ThemeControl(numTorque);
            Ui.ThemeControl(numPower);
            opt.Controls.Add(OptLabel("Peak torque", 6), 0, 1);
            opt.Controls.Add(numTorque, 1, 1);
            opt.Controls.Add(OptLabel("Nm", 20), 2, 1);
            opt.Controls.Add(OptLabel("Peak power", 6), 3, 1);
            opt.Controls.Add(numPower, 4, 1);
            opt.Controls.Add(OptLabel("hp", 0), 5, 1);
            calWarning = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Font = Ui.Small, BackColor = Color.Transparent };
            opt.Controls.Add(calWarning, 0, 2);
            opt.SetColumnSpan(calWarning, 6);
            simBoost = Ui.MakeCheck("Simulate boost in BeamNG", settings.SimulateBoost);
            makeConfigs = Ui.MakeCheck("Add a ready-made config to each car's vehicle list", settings.MakeConfigs);
            var tips = new ToolTip();
            tips.SetToolTip(simBoost, "BeamNG runs a real turbo/supercharger tuned to Automation's boost curve: boost gauge, turbo lag, whine and blow-off.\nOff: the boost is baked into the torque curve.");
            tips.SetToolTip(makeConfigs, "Each car also gets a config like \"Covet - <engine> (Automation)\" in the vehicle selector, so you can spawn it with the engine already fitted.");
            simBoost.Anchor = makeConfigs.Anchor = AnchorStyles.Left;
            opt.Controls.Add(simBoost, 0, 3);
            opt.SetColumnSpan(simBoost, 6);
            opt.Controls.Add(makeConfigs, 0, 4);
            opt.SetColumnSpan(makeConfigs, 6);
            useModel = Ui.MakeCheck("Use Automation's 3D engine model", settings.UseEngineModel);
            useModel.Anchor = AnchorStyles.Left;
            tips.SetToolTip(useModel, "Uses the engine's own 3D model from a car you exported from Automation to BeamNG (it must be in your mods folder).\nThe stock engine model is hidden and Automation's is placed in the engine bay.");
            opt.Controls.Add(useModel, 0, 5);
            opt.SetColumnSpan(useModel, 6);
            fitToCar = Ui.MakeCheck("If BeamNG is running, put the engine into the car I'm driving", settings.FitToCar);
            fitToCar.Anchor = AnchorStyles.Left;
            tips.SetToolTip(fitToCar, "Installs straight into a running BeamNG (no restart needed). If you're driving one of the selected cars,\nits engine is swapped for this one; untick to just reload the car so you can pick the engine in the parts menu.");
            opt.Controls.Add(fitToCar, 0, 6);
            opt.SetColumnSpan(fitToCar, 6);
            fitToCar.CheckedChanged += delegate { settings.FitToCar = fitToCar.Checked; };
            addUpgrades = Ui.MakeCheck("Add tuning sliders and upgrade parts (ECU tunes, launch control, turbo kits…)", settings.Upgrades);
            addUpgrades.Anchor = AnchorStyles.Left;
            tips.SetToolTip(addUpgrades, "Vehicle Config → Tuning gets sliders for rev limit, idle, power, boost and backfire (defaults = Automation).\nParts → Engine gets Sport/Race ECUs, two-step launch control, forged internals, an oil cooler,\nnitrous (where the car has it) and, for engines without boost, three turbo kits.");
            opt.Controls.Add(addUpgrades, 0, 7);
            opt.SetColumnSpan(addUpgrades, 6);
            addUpgrades.CheckedChanged += delegate { settings.Upgrades = addUpgrades.Checked; };
            simBoost.CheckedChanged += delegate { settings.SimulateBoost = simBoost.Checked; };
            makeConfigs.CheckedChanged += delegate { settings.MakeConfigs = makeConfigs.Checked; };
            useModel.CheckedChanged += delegate { if (useModel.Enabled) settings.UseEngineModel = useModel.Checked; };
            useDyno.CheckedChanged += delegate
            {
                if (loadingEngine) return;
                numTorque.Enabled = numPower.Enabled = useDyno.Checked;
                if (useDyno.Checked && spec != null && numTorque.Value == 0 && numPower.Value == 0)
                {
                    var pk = EngineModel.GetPeaks(EngineModel.Curve(spec), spec.RPMLimit);
                    loadingEngine = true;
                    numTorque.Value = (decimal)Math.Round(pk.TorqueNm);
                    numPower.Value = (decimal)Math.Round(pk.PowerHP);
                    loadingEngine = false;
                }
                RememberDyno();
                UpdateCurve();
            };
            numTorque.ValueChanged += delegate { if (!loadingEngine) { RememberDyno(); UpdateCurve(); } };
            numPower.ValueChanged += delegate { if (!loadingEngine) { RememberDyno(); UpdateCurve(); } };
            Ui.AddRow(st, new Panel { BackColor = Ui.Card }, 8);
            Ui.AddRow(st, opt, 240);

            card.Controls.Add(st);
            return card;
        }

        static Label SmallLabel(string t)
        {
            return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted, Margin = new Padding(0, Ui.Px(4), Ui.Px(8), 0), BackColor = Color.Transparent };
        }

        static Label OptLabel(string t, int right)
        {
            return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted, Margin = new Padding(Ui.Px(4), 0, Ui.Px(right), 0), BackColor = Color.Transparent };
        }

        Control BuildCarsCard()
        {
            var card = new CardPanel();
            var st = Ui.Stack();
            Ui.AddRow(st, new StepHeader("3", "Pick cars", "Right-click a car to choose which stock engine it builds on"), 50);
            carSearch = new TextBox { Font = Ui.Base, BorderStyle = BorderStyle.FixedSingle };
            Ui.ThemeControl(carSearch);
            Ui.SetPlaceholder(carSearch, "Search cars…");
            carSearch.TextChanged += delegate { FillCarList(); };
            var searchHost = new Panel { Padding = new Padding(0, Ui.Px(2), 0, Ui.Px(6)), BackColor = Ui.Card };
            carSearch.Dock = DockStyle.Fill;
            searchHost.Controls.Add(carSearch);
            Ui.AddRow(st, searchHost, 34);

            carList = new SmoothListView { CheckBoxes = true, ShowGroups = true };
            carList.SmallImageList = new ImageList { ImageSize = new Size(Ui.Px(64), Ui.Px(36)), ColorDepth = ColorDepth.Depth32Bit };
            carList.Columns.Add("Car", Ui.Px(150));
            carList.Columns.Add("Built on", Ui.Px(150));
            carList.ItemChecked += OnCarChecked;
            carList.Resize += delegate { FitColumns(carList); };
            baseMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            carList.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = carList.HitTest(e.Location);
                if (hit.Item != null) ShowBaseMenu(hit.Item, e.Location);
            };
            carList.MouseDoubleClick += (s, e) =>
            {
                var hit = carList.HitTest(e.Location);
                if (hit.Item != null && hit.SubItem == hit.Item.SubItems[1]) ShowBaseMenu(hit.Item, e.Location);
            };
            Ui.AddRow(st, carList, -1);

            var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.Card };
            carCount = new Label { AutoSize = true, ForeColor = Ui.Text, Font = Ui.Bold, Margin = new Padding(0, Ui.Px(6), Ui.Px(16), 0) };
            bottom.Controls.Add(carCount);
            bottom.Controls.Add(Ui.MakeLink("Select all", delegate { SetAllCars(true); }));
            bottom.Controls.Add(Ui.MakeLink("Clear", delegate { SetAllCars(false); }));
            bottom.Controls.Add(Ui.MakeLink("Rescan", delegate { LoadCars(true); }));
            Ui.AddRow(st, bottom, 30);
            card.Controls.Add(st);
            return card;
        }

        Panel BuildInstalledPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(Ui.Px(16)) };
            var card = new CardPanel { Margin = Padding.Empty };
            var st = Ui.Stack();
            Ui.AddRow(st, new StepHeader(null, "Installed engines", "Engines this app put in your BeamNG mods folder. Update rebuilds one from its .engine file with the same cars and options; Reinstall all rebuilds every one (after a BeamNG or converter update)."), 50);
            modList = new SmoothListView { MultiSelect = false };
            modList.Columns.Add("Engine", Ui.Px(260));
            modList.Columns.Add("Cars", Ui.Px(380));
            modList.Columns.Add("Power", Ui.Px(90), HorizontalAlignment.Right);
            modList.Columns.Add("Installed", Ui.Px(140));
            modList.Columns.Add("Size", Ui.Px(80), HorizontalAlignment.Right);
            modList.SelectedIndexChanged += delegate { UpdateModButtons(); };
            modList.Resize += delegate { FitFirstColumn(modList); };
            Ui.AddRow(st, modList, -1);
            var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, Ui.Px(10), 0, 0), BackColor = Ui.Card };
            modUpdate = new PlainButton { Text = "⟳  Update", Size = new Size(Ui.Px(120), Ui.Px(34)), Margin = new Padding(0, 0, Ui.Px(8), 0) };
            modRemove = new PlainButton { Text = "✕  Remove", Size = new Size(Ui.Px(120), Ui.Px(34)), Margin = new Padding(0, 0, Ui.Px(8), 0) };
            modReinstall = new PlainButton { Text = "⟳  Reinstall all", Size = new Size(Ui.Px(150), Ui.Px(34)), Margin = new Padding(0, 0, Ui.Px(8), 0) };
            modReinstall.Click += delegate { ReinstallAll(); };
            var open = new PlainButton { Text = "Open mods folder", Size = new Size(Ui.Px(150), Ui.Px(34)), Margin = new Padding(0, 0, Ui.Px(8), 0) };
            var refresh = new PlainButton { Text = "Refresh", Size = new Size(Ui.Px(100), Ui.Px(34)), Margin = Padding.Empty };
            modUpdate.Click += delegate { UpdateSelectedMod(); };
            modRemove.Click += delegate { RemoveSelectedMod(); };
            open.Click += delegate { OpenFolder(settings.ModsPath); };
            refresh.Click += delegate { LoadInstalled(); };
            bar.Controls.AddRange(new Control[] { modUpdate, modReinstall, modRemove, open, refresh });
            Ui.AddRow(st, bar, 56);
            card.Controls.Add(st);
            page.Controls.Add(card);
            return page;
        }

        static void FitFirstColumn(ListView lv)
        {
            if (lv.Columns.Count == 0) return;
            int rest = 0;
            for (int i = 1; i < lv.Columns.Count; i++) rest += lv.Columns[i].Width;
            lv.Columns[0].Width = Math.Max(Ui.Px(80), lv.ClientSize.Width - rest - SystemInformation.VerticalScrollBarWidth - 2);
        }

        static void FitColumns(ListView lv)
        {
            int w = lv.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            lv.Columns[0].Width = Math.Max(Ui.Px(150), (int)(w * 0.6));
            lv.Columns[1].Width = Math.Max(Ui.Px(80), w - lv.Columns[0].Width);
        }

        void ShowPage(bool installed)
        {
            tabConvert.Selected = !installed;
            tabInstalled.Selected = installed;
            convertPage.Visible = !installed;
            installedPage.Visible = installed;
            installBtn.Visible = !installed;
            if (installed) LoadInstalled();
        }

        // ------------------------------------------------------------------ startup

        void OnShown(object sender, EventArgs e)
        {
            soundBox.Items.Add("Automatic");
            foreach (var o in Sounds.All) soundBox.Items.Add(o);
            if (!settings.IsComplete)
            {
                settings.FillDetected();
                OpenSetup(true);
            }
            else
            {
                LoadEngines();
                LoadCars(false);
            }
            ScanExports();
            UpdateRunningBanner();
            CheckForUpdates(false);
        }

        void OpenSetup(bool firstRun)
        {
            string oldGame = settings.BeamNGPath, oldTheme = settings.Theme;
            using (var f = new SetupForm(settings, firstRun))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    settings.Save();
                    if (settings.Theme != oldTheme &&
                        MessageBox.Show(this, "Restart the converter now to switch the theme?", "Theme", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Application.Restart();
                        return;
                    }
                }
            }
            AutomationDb.ClearCache();
            specs.Clear();
            LoadEngines();
            if (firstRun || oldGame != settings.BeamNGPath || cars.Count == 0) LoadCars(false);
            ScanExports();
            if (installedPage.Visible) LoadInstalled();
            UpdateInstallButton();
        }

        void UpdateRunningBanner()
        {
            bool on = BeamNGProcess.IsRunning();
            if (runningBanner.Visible != on) runningBanner.Visible = on;
        }

        async void CheckForUpdates(bool manual)
        {
            if (!Updates.IsValidRepo(settings.UpdateRepo)) return;
            DateTime last;
            if (!manual && DateTime.TryParse(settings.LastUpdateCheck, Num.Inv, System.Globalization.DateTimeStyles.None, out last) && (DateTime.Now - last).TotalHours < 24) return;
            Version latest = null; string url = null, msg = null;
            bool ok = await Task.Run(() => Updates.TryGetLatest(settings.UpdateRepo, out latest, out url, out msg));
            settings.LastUpdateCheck = DateTime.Now.ToString("s", Num.Inv);
            settings.Save();
            if (ok && latest > Updates.Current)
            {
                updateBanner.Controls.OfType<Label>().First().Text = "Version " + latest + " is available (you have " + Updates.Current + ").";
                updateLink.LinkClicked += delegate { if (!string.IsNullOrEmpty(url)) Process.Start(url); };
                updateBanner.Visible = true;
            }
        }

        // ------------------------------------------------------------------ step 1: engines

        bool IsInExportFolder(string file)
        {
            return AppPaths.IsFolder(settings.AutomationPath)
                && Path.GetFullPath(file).StartsWith(Path.GetFullPath(settings.AutomationPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        EngineSpec SpecFor(string file)
        {
            EngineSpec s;
            if (!specs.TryGetValue(file, out s)) specs[file] = s = EngineSpec.Load(file);
            return s;
        }

        // The engine as it will be built: Automation's results if found, else estimate (+ remembered dyno numbers).
        EngineData DataFor(EngineSpec s, bool useManual)
        {
            var real = AutomationDb.Find(s.VariantUID, settings.AutomationPath);
            double[] dyno;
            if (real == null && useManual && settings.Dyno.TryGetValue(Settings.EngineId(s), out dyno)) return EngineData.Build(s, null, dyno[0], dyno[1] * Num.KWPerHP);
            return EngineData.Build(s, real, 0, 0);
        }

        List<string> EngineFiles()
        {
            var files = new List<string>();
            if (AppPaths.IsFolder(settings.AutomationPath))
            {
                try { files.AddRange(Directory.GetFiles(settings.AutomationPath, "*.engine", SearchOption.AllDirectories)); }
                catch (Exception ex) { Log("Could not read " + settings.AutomationPath + ": " + ex.Message); }
            }
            foreach (var f in extraFiles) if (!files.Contains(f, StringComparer.OrdinalIgnoreCase) && File.Exists(f)) files.Add(f);
            return files.OrderBy(Path.GetFileName).ToList();
        }

        void LoadEngines()
        {
            string keep = engineList.SelectedItems.Count > 0 ? (string)engineList.SelectedItems[0].Tag : settings.LastEngine;
            var files = EngineFiles();
            engineList.BeginUpdate();
            engineList.Items.Clear();
            int real = 0;
            foreach (var f in files)
            {
                ListViewItem it;
                try
                {
                    var s = SpecFor(f);
                    var d = DataFor(s, true);
                    var pk = d.Peaks;
                    string pre = d.FromAutomation ? "" : "≈";
                    if (d.FromAutomation) real++;
                    it = new ListViewItem(new[] { s.Name, s.LayoutShort + " " + s.SizeText, pre + Num.F(pk.PowerHP, "#,0") + " hp", pre + Num.F(pk.TorqueNm, "#,0") + " Nm" });
                }
                catch (Exception)
                {
                    it = new ListViewItem(new[] { Path.GetFileNameWithoutExtension(f) + " (can't read)", "", "", "" }) { ForeColor = Ui.Muted };
                }
                it.Tag = f;
                it.ToolTipText = f;
                engineList.Items.Add(it);
            }
            engineList.ShowItemToolTips = true;
            engineList.EndUpdate();
            engineEmpty.Visible = engineList.Items.Count == 0;
            if (engineEmpty.Visible) { engineEmpty.BringToFront(); ShowEngine(null); }

            // the compare list mirrors the engine list
            compareBox.Items.Clear();
            compareBox.Items.Add("Nothing");
            foreach (ListViewItem it in engineList.Items) compareBox.Items.Add(new CompareItem { File = (string)it.Tag, Name = it.Text });
            compareBox.SelectedIndex = 0;

            ListViewItem sel = engineList.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals((string)i.Tag, keep, StringComparison.OrdinalIgnoreCase))
                ?? (engineList.Items.Count > 0 ? engineList.Items[0] : null);
            if (sel != null) { sel.Selected = true; sel.EnsureVisible(); }
            SetStatus(files.Count + " engine" + (files.Count == 1 ? "" : "s") + " found, " + real + " with Automation's own results", false);
        }

        class CompareItem
        {
            public string File, Name;
            public override string ToString() { return Name; }
        }

        void AddEngineFiles()
        {
            using (var d = new OpenFileDialog { Filter = "Automation engine (*.engine)|*.engine", Multiselect = true, Title = "Add Automation engine files" })
            {
                if (AppPaths.IsFolder(settings.AutomationPath)) d.InitialDirectory = settings.AutomationPath;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddFiles(d.FileNames);
            }
        }

        void AddFiles(IEnumerable<string> files)
        {
            string last = null;
            foreach (var f in files.Where(f => f.EndsWith(".engine", StringComparison.OrdinalIgnoreCase)))
            {
                if (!IsInExportFolder(f) && !extraFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) extraFiles.Add(f);
                specs.Remove(f);
                last = f;
            }
            if (last == null) return;
            settings.LastEngine = last;
            engineList.SelectedItems.Clear();
            ShowPage(false);
            LoadEngines();
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effect = files != null && files.Any(f => f.EndsWith(".engine", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        // ------------------------------------------------------------------ step 2: details

        void OnEngineSelected()
        {
            if (engineList.SelectedItems.Count == 0) return;
            string f = (string)engineList.SelectedItems[0].Tag;
            try
            {
                var s = SpecFor(f);
                settings.LastEngine = f;
                ShowEngine(s);
            }
            catch (Exception ex)
            {
                ShowEngine(null);
                engineName.Text = "Can't read this file";
                SetStatus(Path.GetFileName(f) + ": " + ex.Message, true);
            }
        }

        void ShowEngine(EngineSpec s)
        {
            spec = s;
            chips.SuspendLayout();
            chips.Controls.Clear();
            if (s == null)
            {
                data = null;
                engineName.Text = "No engine selected";
                chart.SetCurve(null, 0);
                tilePower.Set("-", ""); tileTorque.Set("-", ""); tileRedline.Set("-", "");
                chips.ResumeLayout();
                UpdateInstallButton();
                return;
            }
            engineName.Text = s.Name;
            var real = AutomationDb.Find(s.VariantUID, settings.AutomationPath);
            if (real != null) chips.Controls.Add(new Chip("✓ Automation's own results", Color.FromArgb(Ui.Dark ? 20 : 220, Ui.Dark ? 83 : 252, Ui.Dark ? 45 : 231), Ui.Dark ? Color.FromArgb(134, 239, 172) : Color.FromArgb(21, 128, 61)));
            else chips.Controls.Add(new Chip("≈ Estimated", Ui.WarnBg, Ui.Warn));
            // the 3D model status goes right after the source, so it never wraps out of view
            var export = ExportFor(s);
            var modelChip = export != null
                ? new Chip("✓ 3D model", Color.FromArgb(Ui.Dark ? 20 : 220, Ui.Dark ? 83 : 252, Ui.Dark ? 45 : 231), Ui.Dark ? Color.FromArgb(134, 239, 172) : Color.FromArgb(21, 128, 61))
                : new Chip("No 3D model", Ui.Soft, Ui.Muted);
            new ToolTip().SetToolTip(modelChip, export != null
                ? "Automation's own engine model, from your export \"" + export.Describe + "\"."
                : "To use this engine's own 3D model: in Automation, export any car with this engine to BeamNG, then press Refresh.");
            chips.Controls.Add(modelChip);
            foreach (var t in new[] { s.LayoutLong, s.SizeText, s.InductionText, s.FuelText, "Bore × stroke " + s.BoreStrokeText, "Compression " + Num.F(s.Compression, "0.0") + ":1" })
                chips.Controls.Add(new Chip(t));
            if (real != null && real.WeightKg > 0) chips.Controls.Add(new Chip(Num.F(real.WeightKg, "0") + " kg"));
            UpdateModelOption();
            chips.ResumeLayout();

            // remembered choices for this engine
            loadingEngine = true;
            string id = Settings.EngineId(s), snd;
            soundBox.SelectedItem = settings.EngineSound.TryGetValue(id, out snd) && Sounds.ByName(snd) != null ? (object)Sounds.ByName(snd) : "Automatic";
            if (soundBox.SelectedIndex < 0) soundBox.SelectedIndex = 0;
            double[] dyno;
            bool hasDyno = real == null && settings.Dyno.TryGetValue(id, out dyno);
            useDyno.Checked = hasDyno;
            useDyno.Enabled = real == null;
            numTorque.Enabled = numPower.Enabled = hasDyno;
            if (real != null) { numTorque.Value = (decimal)Math.Min(50000, Math.Round(real.PeakTorqueNm)); numPower.Value = (decimal)Math.Min(50000, Math.Round(real.PeakPowerKW / Num.KWPerHP)); }
            else if (hasDyno) { numTorque.Value = (decimal)settings.Dyno[id][0]; numPower.Value = (decimal)settings.Dyno[id][1]; }
            else { numTorque.Value = 0; numPower.Value = 0; }
            loadingEngine = false;
            UpdateCurve();
            UpdateCompare();
        }

        void RememberDyno()
        {
            if (spec == null || loadingEngine) return;
            string id = Settings.EngineId(spec);
            if (useDyno.Checked && (numTorque.Value > 0 || numPower.Value > 0)) settings.Dyno[id] = new[] { (double)numTorque.Value, (double)numPower.Value };
            else settings.Dyno.Remove(id);
        }

        void OnSoundPicked()
        {
            if (spec == null || loadingEngine) return;
            var o = soundBox.SelectedItem as Sounds.Option;
            if (o != null) settings.EngineSound[Settings.EngineId(spec)] = o.Name; else settings.EngineSound.Remove(Settings.EngineId(spec));
            UpdateSoundLabel();
        }

        void UpdateSoundLabel()
        {
            if (data == null || soundBox.Items.Count == 0) return;
            string auto = "Automatic (" + Sounds.Auto(spec, data.RpmLimit).Name + ")";
            if (!Equals(soundBox.Items[0], auto))
            {
                loadingEngine = true;
                int sel = soundBox.SelectedIndex;
                soundBox.Items[0] = auto;
                soundBox.SelectedIndex = sel < 0 ? 0 : sel;
                loadingEngine = false;
            }
        }

        void UpdateCurve()
        {
            if (spec == null) return;
            data = DataFor(spec, true);
            var pk = data.Peaks;
            string src = data.FromAutomation ? "" : " (est.)";
            tilePower.Set(Num.F(pk.PowerHP, "#,0") + " hp", "at " + Num.F(pk.PowerRPM, "#,0") + " rpm" + src);
            tileTorque.Set(Num.F(pk.TorqueNm, "#,0") + " Nm", "at " + Num.F(pk.TorqueRPM, "#,0") + " rpm" + src);
            tileRedline.Set(Num.F(data.RpmLimit, "#,0"), data.IdleRpm > 0 ? "rpm  ·  idle " + Num.F(data.IdleRpm, "#,0") : "rpm");
            chart.SetCurve(data.Curve, data.RpmLimit);
            simBoost.Enabled = data.IsBoosted;
            simBoost.Text = data.IsBoosted
                ? "Simulate the " + (spec.IsTurbo ? "turbo" : "supercharger") + " in BeamNG (" + Num.F(data.PeakBoost, "0.0#") + " bar, with gauge and lag)"
                : "Simulate boost in BeamNG (not needed: no turbo)";
            if (data.FromAutomation)
            {
                calWarning.Text = "✓ Using Automation's saved dyno results (" + Path.GetFileName(data.Real.DbFile) + ").";
                calWarning.ForeColor = Ui.Good;
            }
            else if (data.CalWarning != null) { calWarning.Text = "⚠ " + data.CalWarning; calWarning.ForeColor = Ui.Warn; }
            else if (useDyno.Checked) { calWarning.Text = "The estimated curve is reshaped to match these peaks."; calWarning.ForeColor = Ui.Good; }
            else
            {
                calWarning.Text = "Automation's results weren't found (open the engine in Automation once, then Refresh) — showing an estimate.";
                calWarning.ForeColor = Ui.Warn;
            }
            UpdateSoundLabel();
            UpdateBuiltOn();
            UpdateInstallButton();
        }

        ExportedEngine ExportFor(EngineSpec s)
        {
            ExportedEngine e;
            return s != null && !string.IsNullOrEmpty(s.VariantUID) && exports.TryGetValue(s.VariantUID, out e) ? e : null;
        }

        // Automation BeamNG exports in the mods folder (their engines' 3D models), found in the background.
        async void ScanExports()
        {
            string mods = settings.ModsPath;
            try { exports = await Task.Run(() => EngineExports.Scan(mods)); }
            catch (Exception ex) { Log("Couldn't look for Automation exports: " + ex.Message); }
            if (spec != null) ShowEngine(spec);
        }

        void UpdateModelOption()
        {
            var export = spec != null ? ExportFor(spec) : null;
            useModel.Enabled = export != null;
            useModel.Checked = export != null && settings.UseEngineModel;
            useModel.Text = export != null
                ? "Use Automation's 3D engine model (from \"" + export.CarName + "\")"
                : "Use Automation's 3D engine model (needs a BeamNG export)";
        }

        void UpdateCompare()
        {
            var ci = compareBox.SelectedItem as CompareItem;
            if (ci == null || spec == null || string.Equals(ci.File, spec.File, StringComparison.OrdinalIgnoreCase)) { chart.SetCompare(null, 0, null); return; }
            try
            {
                var d = DataFor(SpecFor(ci.File), true);
                chart.SetCompare(d.Curve, d.RpmLimit, ci.Name);
            }
            catch (Exception) { chart.SetCompare(null, 0, null); }
        }

        // ------------------------------------------------------------------ step 3: cars

        async void LoadCars(bool force)
        {
            if (busy) return;
            if (!AppPaths.IsBeamNG(settings.BeamNGPath))
            {
                cars = new List<VehicleInfo>();
                FillCarList();
                SetStatus("Set your BeamNG.drive folder in Folders & settings", true);
                return;
            }
            SetBusy(true, "Looking for cars in BeamNG.drive…", false);
            string game = settings.BeamNGPath;
            try
            {
                cars = await Task.Run(() => VehicleIndex.Load(game, msg => BeginInvoke(new Action(() => status.Text = msg)), force));
                SetBusy(false, cars.Count + " BeamNG cars available", false);
            }
            catch (Exception ex)
            {
                cars = new List<VehicleInfo>();
                SetBusy(false, null, false);
                SetStatus("Couldn't read BeamNG.drive: " + ex.Message, true);
            }
            LoadThumbs();
            FillCarList();
            CheckOutdated();
        }

        void LoadThumbs()
        {
            var il = carList.SmallImageList;
            il.Images.Clear();
            int w = il.ImageSize.Width, h = il.ImageSize.Height;
            foreach (var v in cars)
            {
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Ui.Soft);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    try
                    {
                        if (File.Exists(v.ThumbFile))
                            using (var src = Image.FromFile(v.ThumbFile)) g.DrawImage(src, new Rectangle(0, 0, w, h - Ui.Px(2)));
                    }
                    catch (Exception) { }
                }
                il.Images.Add(v.Model, bmp);
            }
        }

        string BuiltOnText(VehicleInfo v)
        {
            string over;
            if (settings.BaseOverrides.TryGetValue(v.Model, out over))
            {
                var e = v.Engines.FirstOrDefault(x => x.PartKey == over);
                if (e != null) return "★ " + e.DisplayName;
            }
            if (spec == null || data == null) return "";
            try { return Converter.SelectBase(v, spec, data.Peaks.TorqueNm, null).DisplayName; }
            catch (Exception) { return "?"; }
        }

        void UpdateBuiltOn()
        {
            carList.BeginUpdate();
            foreach (ListViewItem it in carList.Items) it.SubItems[1].Text = BuiltOnText((VehicleInfo)it.Tag);
            carList.EndUpdate();
        }

        void FillCarList()
        {
            fillingCars = true;
            carList.BeginUpdate();
            carList.Items.Clear();
            carList.Groups.Clear();
            string q = carSearch.Text.Trim();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var v in cars)
            {
                if (q.Length > 0 && v.FullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && v.Model.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string brand = v.Brand.Length > 0 ? v.Brand : "Other";
                ListViewGroup g;
                if (!groups.TryGetValue(brand, out g)) { g = new ListViewGroup(brand, brand); groups[brand] = g; carList.Groups.Add(g); }
                var it = new ListViewItem(new[] { v.Name, BuiltOnText(v) }, v.Model, g)
                {
                    Tag = v, Checked = settings.CheckedCars.Contains(v.Model),
                    ToolTipText = v.FullName + " (" + v.Model + ")\nRight-click to choose the stock engine it builds on.",
                };
                it.UseItemStyleForSubItems = false;
                it.SubItems[1].ForeColor = Ui.Muted;
                carList.Items.Add(it);
            }
            carList.ShowItemToolTips = true;
            carList.EndUpdate();
            fillingCars = false;
            UpdateCarCount();
        }

        void ShowBaseMenu(ListViewItem item, Point at)
        {
            var v = (VehicleInfo)item.Tag;
            string over;
            settings.BaseOverrides.TryGetValue(v.Model, out over);
            baseMenu.Items.Clear();
            baseMenu.Items.Add(new ToolStripLabel("Build " + v.FullName + " on…") { Font = Ui.Bold });
            var auto = new ToolStripMenuItem("Automatic (best match)") { Checked = string.IsNullOrEmpty(over) };
            auto.Click += delegate { settings.BaseOverrides.Remove(v.Model); item.SubItems[1].Text = BuiltOnText(v); };
            baseMenu.Items.Add(auto);
            baseMenu.Items.Add(new ToolStripSeparator());
            foreach (var e in v.MainSlotEngines.OrderBy(x => x.PeakTorque))
            {
                var eng = e;
                var mi = new ToolStripMenuItem(e.Describe()) { Checked = over == e.PartKey };
                mi.Click += delegate { settings.BaseOverrides[v.Model] = eng.PartKey; item.SubItems[1].Text = BuiltOnText(v); };
                baseMenu.Items.Add(mi);
            }
            baseMenu.Show(carList, at);
        }

        void OnCarChecked(object sender, ItemCheckedEventArgs e)
        {
            if (fillingCars) return;
            var v = (VehicleInfo)e.Item.Tag;
            if (e.Item.Checked) settings.CheckedCars.Add(v.Model); else settings.CheckedCars.Remove(v.Model);
            UpdateCarCount();
        }

        void SetAllCars(bool on)
        {
            fillingCars = true;
            foreach (ListViewItem it in carList.Items)
            {
                it.Checked = on;
                var v = (VehicleInfo)it.Tag;
                if (on) settings.CheckedCars.Add(v.Model); else settings.CheckedCars.Remove(v.Model);
            }
            fillingCars = false;
            UpdateCarCount();
        }

        List<VehicleInfo> SelectedCars { get { return cars.Where(v => settings.CheckedCars.Contains(v.Model)).ToList(); } }

        void UpdateCarCount()
        {
            int n = SelectedCars.Count;
            carCount.Text = n == 0 ? "No cars selected" : n + " car" + (n == 1 ? "" : "s") + " selected";
            carCount.ForeColor = n == 0 ? Ui.Muted : Ui.Text;
            UpdateInstallButton();
        }

        // ------------------------------------------------------------------ install

        void UpdateInstallButton()
        {
            if (installBtn == null) return;
            int n = SelectedCars.Count;
            installBtn.Enabled = !busy && spec != null && n > 0 && AppPaths.IsBeamNG(settings.BeamNGPath);
            installBtn.Text = n > 0 ? "Install for " + n + " car" + (n == 1 ? "" : "s") + "  ›" : "Install into BeamNG";
            installBtn.Invalidate();
        }

        ConvertOptions CurrentOptions()
        {
            var o = new ConvertOptions
            {
                SimulateBoost = simBoost.Checked, MakeConfigs = makeConfigs.Checked, Sound = soundBox.SelectedItem as Sounds.Option,
                Model = useModel.Checked && spec != null ? ExportFor(spec) : null,
                BaseOverrides = new Dictionary<string, string>(settings.BaseOverrides),
                Upgrades = addUpgrades.Checked,
            };
            if (data != null && !data.FromAutomation && useDyno.Checked) { o.ManualTorque = (double)numTorque.Value; o.ManualPowerKW = (double)numPower.Value * Num.KWPerHP; }
            return o;
        }

        void InstallSelected()
        {
            if (spec == null || data == null || busy) return;
            var sel = SelectedCars;
            if (sel.Count == 0) return;
            settings.Save();
            RunInstall(data, sel, CurrentOptions());
        }

        // Is the live-update link inside a running BeamNG answering? Installs the companion mod first if needed.
        async Task<bool> ConnectLive(string mods)
        {
            bool fresh = !File.Exists(Path.Combine(mods, LiveLink.ModZip));
            bool written = LiveLink.EnsureInstalled(mods);
            if (!BeamNGProcess.IsRunning()) return false;
            SetBusy(true, "Connecting to BeamNG…", false);
            // a running BeamNG picks up a newly added mod by itself, which takes a few seconds
            bool ok = await Task.Run(() => LiveLink.IsConnected(mods) || (fresh && written && WaitForLive(mods)));
            SetBusy(false, ok ? "Connected to BeamNG" : "", false);
            return ok;
        }

        static bool WaitForLive(string mods)
        {
            for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(1500); if (LiveLink.IsConnected(mods)) return true; }
            return false;
        }

        string CarName(string model)
        {
            var c = cars.FirstOrDefault(x => x.Model == model);
            return c != null ? c.FullName : model;
        }

        async void RunInstall(EngineData d, List<VehicleInfo> sel, ConvertOptions opt)
        {
            string game = settings.BeamNGPath, mods = settings.ModsPath;
            bool live = await ConnectLive(mods);
            if (!live && BeamNGProcess.IsRunning() &&
                MessageBox.Show(this, "BeamNG.drive is running, but its live-update link isn't answering (it starts with BeamNG, so restart the game once to turn it on).\n\nThe engine will show up after you restart BeamNG. If BeamNG has an older version of this engine loaded, the install may fail until you close it.\n\nInstall now?",
                    "BeamNG.drive is running", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) { SetStatus("", false); return; }
            if (live) opt.Unlock = zip => LiveLink.Unmount(mods, zip);
            bool fit = settings.FitToCar;
            while (true)
            {
                ModLockedException locked = null;
                lock (log) log.Clear();
                SetBusy(true, "Building " + d.Spec.Name + "…", true);
                progress.Maximum = sel.Count + 1;
                try
                {
                    Action<int, int, string> prog = (i, n, name) => BeginInvoke(new Action(() =>
                    {
                        progress.Value = Math.Min(progress.Maximum, i);
                        status.Text = i < n ? "Building " + name + " (" + (i + 1) + "/" + n + ")…" : "Checking and writing the mod…";
                    }));
                    var res = await Task.Run(() => Converter.Convert(d, sel, game, mods, opt, msg => { lock (log) log.Add(msg); }, prog));
                    if (live)
                    {
                        status.Text = "Loading it into BeamNG…";
                        res.LiveMessage = await Task.Run(() => LiveLink.Reload(mods, res.ZipPath, res.Parts, fit, CarName));
                        lock (log) log.Add("Live update: " + res.LiveMessage);
                    }
                    progress.Value = progress.Maximum;
                    SetBusy(false, "Installed " + d.Spec.Name + " for " + res.Built + " car" + (res.Built == 1 ? "" : "s"), true);
                    using (var f = new DoneForm(d, res, opt)) f.ShowDialog(this);
                    if (installedPage.Visible) LoadInstalled();
                    return;
                }
                catch (ModLockedException ex) { locked = ex; }
                catch (Exception ex)
                {
                    SetBusy(false, null, true);
                    SetStatus("Install failed: " + ex.Message, true);
                    MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // the old version may have been unmounted for the swap: give it back to BeamNG
                if (live) await Task.Run(() => LiveLink.Reload(mods, locked.ZipPath, new Dictionary<string, string[]>(), false, CarName));
                SetBusy(false, null, true);
                SetStatus(locked.Message, true);
                if (MessageBox.Show(this, locked.Message, "Mod in use", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;
            }
        }

        // ------------------------------------------------------------------ installed page

        void LoadInstalled()
        {
            modList.BeginUpdate();
            modList.Items.Clear();
            var names = cars.ToDictionary(c => c.Model, c => c.FullName);
            foreach (var m in InstalledMod.List(settings.ModsPath))
            {
                string carText = string.Join(", ", m.Cars.Select(c => { string n; return names.TryGetValue(c, out n) ? n : c; }));
                string power = m.Get("PowerHP").Length > 0 ? m.Get("PowerHP") + " hp" : "";
                var it = new ListViewItem(new[] { m.EngineName, m.Cars.Count + ": " + carText, power, m.Date.ToString("g"), (m.Size / 1024 + 1) + " KB" }) { Tag = m };
                it.ToolTipText = m.ZipPath + (m.EngineFile.Length > 0 ? "\nFrom: " + m.EngineFile : "");
                if (m.EngineFile.Length > 0 && !File.Exists(m.EngineFile)) it.ForeColor = Ui.Muted;
                modList.Items.Add(it);
            }
            modList.ShowItemToolTips = true;
            modList.EndUpdate();
            UpdateModButtons();
            SetStatus(modList.Items.Count + " installed engine" + (modList.Items.Count == 1 ? "" : "s") + " in " + settings.ModsPath, false);
        }

        InstalledMod SelectedMod { get { return modList.SelectedItems.Count > 0 ? (InstalledMod)modList.SelectedItems[0].Tag : null; } }

        void UpdateModButtons()
        {
            var m = SelectedMod;
            modRemove.Enabled = m != null && !busy;
            modUpdate.Enabled = m != null && !busy && m.EngineFile.Length > 0 && File.Exists(m.EngineFile);
            modReinstall.Enabled = !busy && modList.Items.Count > 0;
        }

        void RemoveSelectedMod()
        {
            var m = SelectedMod;
            if (m == null) return;
            if (MessageBox.Show(this, "Remove " + m.EngineName + " from BeamNG?\n\nThis deletes " + Path.GetFileName(m.ZipPath) + " from your mods folder.",
                    "Remove engine", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                File.Delete(m.ZipPath);
                SetStatus("Removed " + m.EngineName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't remove it: " + ex.Message + (BeamNGProcess.IsRunning() ? "\n\nClose BeamNG.drive and try again." : ""), "Remove engine", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            LoadInstalled();
        }

        void UpdateSelectedMod()
        {
            var m = SelectedMod;
            if (m == null || !File.Exists(m.EngineFile)) return;
            if (cars.Count == 0) { MessageBox.Show(this, "BeamNG's cars haven't been loaded yet.", "Update"); return; }
            EngineData d; List<VehicleInfo> sel; ConvertOptions opt; string why;
            if (!JobFor(m, false, out d, out sel, out opt, out why)) { MessageBox.Show(this, "Couldn't update: " + why, "Update", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            RunInstall(d, sel, opt);
        }

        // Everything needed to rebuild an installed engine: its .engine file, cars and options as the mod remembers them.
        // allCars: every car BeamNG has instead of the ones it was installed in.
        bool JobFor(InstalledMod m, bool allCars, out EngineData d, out List<VehicleInfo> sel, out ConvertOptions opt, out string why)
        {
            d = null; sel = null; opt = null; why = null;
            if (m.EngineFile.Length == 0 || !File.Exists(m.EngineFile)) { why = "its .engine file is gone (" + (m.EngineFile.Length > 0 ? m.EngineFile : "not recorded") + ")"; return false; }
            try
            {
                specs.Remove(m.EngineFile);
                var s = SpecFor(m.EngineFile);
                double t, kw;
                double.TryParse(m.Get("ManualTorque"), System.Globalization.NumberStyles.Float, Num.Inv, out t);
                double.TryParse(m.Get("ManualPowerKW"), System.Globalization.NumberStyles.Float, Num.Inv, out kw);
                var real = AutomationDb.Find(s.VariantUID, settings.AutomationPath);
                d = EngineData.Build(s, real, real == null ? t : 0, real == null ? kw : 0);
                sel = allCars ? cars.ToList() : cars.Where(c => m.Cars.Contains(c.Model)).ToList();
                string up = m.Get("Upgrades");
                opt = new ConvertOptions
                {
                    SimulateBoost = m.Get("SimulateBoost") != "0", MakeConfigs = m.Get("MakeConfigs") != "0",
                    Sound = Sounds.ByName(m.Get("Sound")), ManualTorque = t, ManualPowerKW = kw,
                    Model = m.Get("EngineModel") == "1" ? ExportFor(s) : null,
                    Upgrades = up.Length == 0 ? settings.Upgrades : up == "1",   // engines from before the option get the current choice
                };
                foreach (var pair in m.Get("Overrides").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = pair.IndexOf('=');
                    if (i > 0) opt.BaseOverrides[pair.Substring(0, i)] = pair.Substring(i + 1);
                }
                if (sel.Count == 0) { why = "none of its cars were found in BeamNG"; return false; }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        // Rebuilds every installed engine with this version of the app and the current game files.
        async void ReinstallAll()
        {
            if (busy) return;
            if (cars.Count == 0) { MessageBox.Show(this, "BeamNG's cars haven't been loaded yet.", "Reinstall all"); return; }
            var all = InstalledMod.List(settings.ModsPath);
            if (all.Count == 0) { MessageBox.Show(this, "No engines are installed yet.", "Reinstall all"); return; }
            var answer = MessageBox.Show(this, "Rebuild all " + all.Count + " installed engine" + (all.Count == 1 ? "" : "s") + " with this version of the converter and your current BeamNG?\n\n"
                + "Yes:  each engine goes back into the cars it's in now\nNo:  each engine goes into every car BeamNG has (" + cars.Count + " cars)\nCancel:  do nothing",
                "Reinstall all", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            bool allCars = answer == DialogResult.No;
            string game = settings.BeamNGPath, mods = settings.ModsPath;
            bool live = await ConnectLive(mods);
            AutomationDb.ClearCache();
            var done = new List<string>();
            var failed = new List<string>();
            int warnings = 0;
            lock (log) log.Clear();
            for (int n = 0; n < all.Count; n++)
            {
                var m = all[n];
                EngineData d; List<VehicleInfo> sel; ConvertOptions opt; string why;
                if (!JobFor(m, allCars, out d, out sel, out opt, out why)) { failed.Add(m.EngineName + ": " + why); continue; }
                if (live) opt.Unlock = zip => LiveLink.Unmount(mods, zip);
                SetBusy(true, "Reinstalling " + d.Spec.Name + " (" + (n + 1) + "/" + all.Count + ")…", true);
                progress.Maximum = sel.Count + 1;
                string label = d.Spec.Name, count = "(" + (n + 1) + "/" + all.Count + ")";
                Action<int, int, string> prog = (i, total, carName) => BeginInvoke(new Action(() =>
                {
                    progress.Value = Math.Min(progress.Maximum, i);
                    status.Text = "Reinstalling " + label + " " + count + ": " + carName + "…";
                }));
                ConvertResult res = null;
                Exception error = null;
                try { res = await Task.Run(() => Converter.Convert(d, sel, game, mods, opt, msg => { lock (log) log.Add(msg); }, prog)); }
                catch (Exception ex) { error = ex; }
                if (res != null)
                {
                    if (live) await Task.Run(() => LiveLink.Reload(mods, res.ZipPath, res.Parts, false, CarName));
                    done.Add(label + ": " + res.Built + " car" + (res.Built == 1 ? "" : "s") + (res.Failed > 0 ? ", " + res.Failed + " skipped" : ""));
                    warnings += res.Warnings.Count;
                    continue;
                }
                // the old version may have been unmounted for the swap: give it back to BeamNG
                var locked = error as ModLockedException;
                if (live && locked != null) await Task.Run(() => LiveLink.Reload(mods, locked.ZipPath, new Dictionary<string, string[]>(), false, CarName));
                failed.Add(label + ": " + error.Message);
            }
            SetBusy(false, "Reinstalled " + done.Count + " of " + all.Count + " engines", true);
            LoadInstalled();
            CheckOutdated();
            string msgText = (done.Count > 0 ? "Rebuilt:\n  " + string.Join("\n  ", done) : "Nothing was rebuilt.")
                + (failed.Count > 0 ? "\n\nNot rebuilt:\n  " + string.Join("\n  ", failed) : "")
                + (warnings > 0 ? "\n\n" + warnings + " warning" + (warnings == 1 ? "" : "s") + " (details in the log)." : "")
                + (live ? "\n\nBeamNG is running and has loaded the new versions." : BeamNGProcess.IsRunning() ? "\n\nRestart BeamNG to load them." : "");
            MessageBox.Show(this, msgText, "Reinstall all", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // Shows the banner when installed engines were built for an older BeamNG or an older converter.
        void CheckOutdated()
        {
            if (outdatedBanner == null) return;
            List<InstalledMod> stale;
            try { stale = InstalledMod.List(settings.ModsPath).Where(m => BeamNGProcess.Outdated(m, settings.BeamNGPath) != null).ToList(); }
            catch (Exception) { stale = new List<InstalledMod>(); }
            if (stale.Count > 0)
            {
                string why = BeamNGProcess.Outdated(stale[0], settings.BeamNGPath);
                outdatedBanner.Controls.OfType<Label>().First().Text = stale.Count + " installed engine" + (stale.Count == 1 ? " is" : "s are") + " out of date (" + why
                    + "). Rebuild them so they keep working with the latest fixes.";
            }
            outdatedBanner.Visible = stale.Count > 0;
        }

        // ------------------------------------------------------------------ helpers

        void SetBusy(bool on, string msg, bool determinate)
        {
            busy = on;
            progress.Visible = on;
            progress.Style = determinate ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (on && determinate) progress.Value = 0;
            UseWaitCursor = on;
            if (msg != null) SetStatus(msg, false);
            UpdateInstallButton();
            if (modUpdate != null) UpdateModButtons();
        }

        void SetStatus(string msg, bool problem)
        {
            status.Text = msg;
            status.ForeColor = problem ? Ui.Warn : Ui.Muted;
            Log(msg);
        }

        void Log(string msg) { lock (log) log.Add(msg); }

        void ShowLog()
        {
            string text;
            lock (log) text = string.Join("\r\n", log);
            using (var f = new Form { Text = "Log", Size = new Size(Ui.Px(820), Ui.Px(520)), StartPosition = FormStartPosition.CenterParent, Icon = Icon, AutoScaleMode = AutoScaleMode.None })
            {
                Ui.ThemeWindow(f);
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), Text = text };
                Ui.ThemeControl(box);
                f.Controls.Add(box);
                f.ShowDialog(this);
            }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path)) { Directory.CreateDirectory(path); Process.Start("explorer.exe", "\"" + path + "\""); }
            }
            catch (Exception) { }
        }
    }
}
