// Look and feel: light/dark colors, DPI scaling and the small custom controls the window is built from.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationToBeamNG
{
    static class Ui
    {
        public static float S = 1f;   // DPI scale; every hard-coded pixel size goes through Px()
        public static int Px(int v) { return (int)Math.Round(v * S); }

        public static bool Dark;
        public static Color Bg, Card, Border, Text, Muted, Header, HeaderSub, HeaderButton, HeaderButtonHover, Accent, AccentHover, AccentSoft,
            Field, Soft, Grid, RevZone, ChipBg, ChipBorder, ChipText, WarnBg, Disabled, Selection;
        public static readonly Color Power = Color.FromArgb(234, 88, 12);
        public static readonly Color Good = Color.FromArgb(22, 163, 74);
        public static readonly Color Bad = Color.FromArgb(220, 38, 38);
        public static Color Warn;

        public static Font Base, Small, Bold, Title, Big;

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void Init(string theme)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
            Base = new Font("Segoe UI", 9.75f);
            Small = new Font("Segoe UI", 8.75f);
            Bold = new Font("Segoe UI Semibold", 10f);
            Title = new Font("Segoe UI Semibold", 13f);
            Big = new Font("Segoe UI Semibold", 16f);
            Dark = theme == "Dark" || (theme != "Light" && SystemPrefersDark());
            if (Dark)
            {
                Bg = Color.FromArgb(11, 17, 32); Card = Color.FromArgb(22, 30, 46); Border = Color.FromArgb(44, 56, 78);
                Text = Color.FromArgb(241, 245, 249); Muted = Color.FromArgb(148, 163, 184);
                Header = Color.FromArgb(5, 9, 20); HeaderSub = Color.FromArgb(148, 163, 184);
                HeaderButton = Color.FromArgb(30, 41, 59); HeaderButtonHover = Color.FromArgb(51, 65, 85);
                Accent = Color.FromArgb(59, 130, 246); AccentHover = Color.FromArgb(96, 165, 250); AccentSoft = Color.FromArgb(30, 41, 70);
                Field = Color.FromArgb(15, 23, 38); Soft = Color.FromArgb(15, 23, 38); Grid = Color.FromArgb(38, 50, 70);
                RevZone = Color.FromArgb(58, 24, 30); ChipBg = Color.FromArgb(23, 37, 84); ChipBorder = Color.FromArgb(37, 64, 140); ChipText = Color.FromArgb(191, 219, 254);
                WarnBg = Color.FromArgb(55, 40, 10); Warn = Color.FromArgb(251, 191, 36); Disabled = Color.FromArgb(51, 65, 85);
                Selection = Color.FromArgb(30, 58, 110);
            }
            else
            {
                Bg = Color.FromArgb(243, 244, 246); Card = Color.White; Border = Color.FromArgb(226, 232, 240);
                Text = Color.FromArgb(17, 24, 39); Muted = Color.FromArgb(100, 116, 139);
                Header = Color.FromArgb(15, 23, 42); HeaderSub = Color.FromArgb(148, 163, 184);
                HeaderButton = Color.FromArgb(30, 41, 59); HeaderButtonHover = Color.FromArgb(51, 65, 85);
                Accent = Color.FromArgb(37, 99, 235); AccentHover = Color.FromArgb(29, 78, 216); AccentSoft = Color.FromArgb(239, 246, 255);
                Field = Color.White; Soft = Color.FromArgb(248, 250, 252); Grid = Color.FromArgb(241, 245, 249);
                RevZone = Color.FromArgb(254, 242, 242); ChipBg = Color.FromArgb(239, 246, 255); ChipBorder = Color.FromArgb(191, 219, 254); ChipText = Color.FromArgb(30, 64, 175);
                WarnBg = Color.FromArgb(255, 251, 235); Warn = Color.FromArgb(180, 83, 9); Disabled = Color.FromArgb(203, 213, 225);
                Selection = Color.FromArgb(219, 234, 254);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // Dark title bar on Windows 10/11.
        public static void ThemeWindow(Form f)
        {
            f.BackColor = Bg;
            f.ForeColor = Text;
            if (!Dark) return;
            f.HandleCreated += delegate
            {
                int on = 1;
                try { if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); } catch (Exception) { }
            };
        }

        // Dark scrollbars and colors for lists and text boxes.
        public static void ThemeControl(Control c)
        {
            if (c is TextBox || c is NumericUpDown || c is ComboBox) { c.BackColor = Field; c.ForeColor = Text; }
            else if (c is ListView) { c.BackColor = Card; c.ForeColor = Text; }
            if (!Dark) return;
            Action apply = () =>
            {
                try
                {
                    SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
                    if (c is ListView)
                    {
                        IntPtr header = SendMessage(c.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                        if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
                    }
                }
                catch (Exception) { }
            };
            if (c.IsHandleCreated) apply(); else c.HandleCreated += delegate { apply(); };
        }

        // Grey hint text inside an empty text box.
        public static void SetPlaceholder(TextBox box, string text)
        {
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, (IntPtr)1, text);
            else box.HandleCreated += delegate { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); };
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Label MakeLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
        }

        public static LinkLabel MakeLink(string text, EventHandler click)
        {
            var l = new LinkLabel
            {
                Text = text, AutoSize = true, LinkColor = Accent, ActiveLinkColor = AccentHover, LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(0, Px(6), Px(14), 0), Font = Base, BackColor = Color.Transparent,
            };
            l.LinkClicked += delegate { click(l, EventArgs.Empty); };
            return l;
        }

        public static CheckBox MakeCheck(string text, bool on)
        {
            return new CheckBox { Text = text, Checked = on, AutoSize = true, Font = Base, ForeColor = Text, BackColor = Color.Transparent };
        }

        // Vertical stack: rows are added top to bottom with a fixed height, or -1 to fill.
        public static TableLayoutPanel Stack()
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        public static T AddRow<T>(TableLayoutPanel t, T c, int height) where T : Control
        {
            t.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, Px(height)));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowCount);
            t.RowCount++;
            return c;
        }
    }

    class SmoothListView : ListView
    {
        public SmoothListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            Font = Ui.Base;
            SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Px(28)) };   // taller rows
            Ui.ThemeControl(this);
        }
    }

    // White (or dark) panel with a thin border.
    class CardPanel : Panel
    {
        public CardPanel()
        {
            BackColor = Ui.Card;
            ForeColor = Ui.Text;
            Padding = new Padding(Ui.Px(16), Ui.Px(12), Ui.Px(16), Ui.Px(12));
            Margin = new Padding(Ui.Px(6));
            Dock = DockStyle.Fill;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Ui.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    class AccentButton : Button
    {
        bool hover;

        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Ui.Accent;
            ForeColor = Color.White;
            Font = Ui.Bold;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Draw ourselves so the hover color and the disabled text stay readable.
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = !Enabled ? Ui.Disabled : hover ? Ui.AccentHover : Ui.Accent;
            using (var path = Ui.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Px(6)))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Color.White : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    class PlainButton : Button
    {
        public PlainButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Ui.Border;
            FlatAppearance.MouseOverBackColor = Ui.AccentSoft;
            FlatAppearance.MouseDownBackColor = Ui.AccentSoft;
            BackColor = Ui.Card;
            ForeColor = Ui.Text;
            Font = Ui.Base;
            Cursor = Cursors.Hand;
        }
    }

    // Header tab ("Convert" / "Installed engines") with an underline when selected.
    class NavTab : Control
    {
        bool selected, hover;
        public event EventHandler Picked;

        public NavTab(string text)
        {
            Text = text;
            Font = Ui.Bold;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            var sz = TextRenderer.MeasureText(text, Font);
            Size = new Size(sz.Width + Ui.Px(28), Ui.Px(40));
        }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Picked != null) Picked(this, e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Header);
            if (hover && !selected) using (var b = new SolidBrush(Ui.HeaderButton)) g.FillRectangle(b, 0, 0, Width, Height - Ui.Px(3));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - Ui.Px(3)), selected ? Color.White : Ui.HeaderSub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected) using (var b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, Ui.Px(8), Height - Ui.Px(3), Width - Ui.Px(16), Ui.Px(3));
        }
    }

    // "(1) Choose an engine" with a muted hint under it.
    class StepHeader : Control
    {
        readonly string number, title;
        string hint;

        public StepHeader(string number, string title, string hint)
        {
            this.number = number; this.title = title; this.hint = hint;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Ui.Card;
        }

        public string Hint { get { return hint; } set { hint = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = 0;
            if (number != null)
            {
                int d = Ui.Px(28);
                var circle = new Rectangle(0, Ui.Px(4), d, d);
                using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, circle);
                TextRenderer.DrawText(g, number, Ui.Bold, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x = d + Ui.Px(10);
            }
            TextRenderer.DrawText(g, title, Ui.Title, new Point(x, 0), Ui.Text);
            TextRenderer.DrawText(g, hint, Ui.Small, new Rectangle(x, Ui.Px(26), Width - x, Ui.Px(18)), Ui.Muted, TextFormatFlags.EndEllipsis);
        }
    }

    // Small rounded tag like "I3" or "79 cc".
    class Chip : Control
    {
        readonly Color? back, fore;

        public Chip(string text) : this(text, null, null) { }

        public Chip(string text, Color? back, Color? fore)
        {
            Text = text;
            this.back = back; this.fore = fore;
            Font = Ui.Small;
            DoubleBuffered = true;
            var sz = TextRenderer.MeasureText(text, Font);
            Size = new Size(sz.Width + Ui.Px(16), sz.Height + Ui.Px(8));
            Margin = new Padding(0, 0, Ui.Px(6), Ui.Px(6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            using (var path = Ui.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2 - 1))
            using (var b = new SolidBrush(back ?? Ui.ChipBg))
            using (var p = new Pen(back.HasValue ? back.Value : Ui.ChipBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore ?? Ui.ChipText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Caption / big value / small detail, e.g. "Peak power / 61 hp / at 10,750 rpm".
    class StatTile : Control
    {
        readonly string caption;
        readonly Color accent;
        string value = "-", detail = "";

        public StatTile(string caption, Color accent)
        {
            this.caption = caption; this.accent = accent;
            DoubleBuffered = true;
            ResizeRedraw = true;
            Margin = new Padding(0, 0, Ui.Px(8), 0);
        }

        public void Set(string value, string detail) { this.value = value; this.detail = detail; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Soft);
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, 0, 0, Ui.Px(3), Height);
            int x = Ui.Px(12);
            TextRenderer.DrawText(g, caption, Ui.Small, new Point(x, Ui.Px(6)), Ui.Muted);
            TextRenderer.DrawText(g, value, Ui.Big, new Point(x - Ui.Px(2), Ui.Px(20)), Ui.Text);
            TextRenderer.DrawText(g, detail, Ui.Small, new Rectangle(x, Height - Ui.Px(22), Width - x, Ui.Px(18)), Ui.Muted, TextFormatFlags.EndEllipsis);
        }
    }

    // Torque (left axis) and power (right axis) against rpm, with the rev limiter shaded, an
    // optional second engine to compare against, a hover readout and a short animation when the
    // curve changes.
    class EngineChart : Control
    {
        List<double[]> curve, from, compare;
        double limit, compareLimit;
        string compareName;
        int hover = -1;
        float anim = 1;
        readonly Timer timer = new Timer { Interval = 15 };
        DateTime animStart;
        const double AnimMs = 280;

        public EngineChart()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Ui.Card;
            timer.Tick += delegate
            {
                anim = (float)Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / AnimMs);
                if (anim >= 1) timer.Stop();
                Invalidate();
            };
        }

        public void SetCurve(List<double[]> c, double rpmLimit)
        {
            from = curve != null && c != null ? Displayed() : null;
            curve = c; limit = rpmLimit; hover = -1;
            if (from != null && Visible) { anim = 0; animStart = DateTime.Now; timer.Start(); } else anim = 1;
            Invalidate();
        }

        public void SetCompare(List<double[]> c, double rpmLimit, string name) { compare = c; compareLimit = rpmLimit; compareName = name; Invalidate(); }

        static double At(List<double[]> c, double rpm)
        {
            if (c == null || c.Count == 0) return 0;
            if (rpm <= c[0][0]) return c[0][1];
            for (int i = 1; i < c.Count; i++)
                if (rpm <= c[i][0]) return c[i - 1][1] + (c[i][1] - c[i - 1][1]) * (rpm - c[i - 1][0]) / Math.Max(1e-9, c[i][0] - c[i - 1][0]);
            return c[c.Count - 1][1];
        }

        // the curve as currently drawn (mid-animation it's a blend of old and new)
        List<double[]> Displayed()
        {
            if (anim >= 1 || from == null) return curve;
            float t = 1 - (float)Math.Pow(1 - anim, 3);
            return curve.Select(p => new[] { p[0], At(from, p[0]) + (p[1] - At(from, p[0])) * t }).ToList();
        }

        static double Nice(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * mag >= v) return m * mag;
            return 10 * mag;
        }

        Rectangle Plot { get { return new Rectangle(Ui.Px(50), Ui.Px(28), Width - Ui.Px(50) - Ui.Px(52), Height - Ui.Px(28) - Ui.Px(28)); } }

        double MaxRpm { get { return Math.Ceiling((Math.Max(limit, compare != null ? compareLimit : 0) + 500) / 1000) * 1000; } }

        static double Hp(double[] p) { return p[1] * p[0] * 2 * Math.PI / 60 / 1000 / Num.KWPerHP; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (curve == null) return;
            var pr = Plot;
            double rpm = (e.X - pr.Left) / (double)pr.Width * MaxRpm;
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < curve.Count; i++)
                if (curve[i][0] <= MaxRpm && Math.Abs(curve[i][0] - rpm) < bd) { bd = Math.Abs(curve[i][0] - rpm); best = i; }
            if (e.X < pr.Left || e.X > pr.Right) best = -1;
            if (best != hover) { hover = best; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Card);
            if (curve == null || curve.Count < 2) { TextRenderer.DrawText(g, "Pick an engine to see its power curve", Ui.Base, ClientRectangle, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            var pr = Plot;
            if (pr.Width < 50 || pr.Height < 40) return;
            var shown = Displayed();
            double maxRpm = MaxRpm, maxT = 0, maxHp = 0;
            foreach (var c in new[] { curve, from, compare })
            {
                if (c == null) continue;
                foreach (var p in c) { if (p[0] > maxRpm) break; maxT = Math.Max(maxT, p[1]); maxHp = Math.Max(maxHp, Hp(p)); }
            }
            double topT = Nice(maxT * 1.1), topHp = Nice(maxHp * 1.1);
            Func<double, float> X = r => (float)(pr.Left + r / maxRpm * pr.Width);
            Func<double, double, float> Y = (v, top) => (float)(pr.Bottom - v / top * pr.Height);

            // rev limiter zone
            float lx = X(limit);
            using (var b = new SolidBrush(Ui.RevZone)) g.FillRectangle(b, lx, pr.Top, pr.Right - lx, pr.Height);
            using (var pen = new Pen(Color.FromArgb(Ui.Dark ? 127 : 252, Ui.Dark ? 29 : 165, Ui.Dark ? 29 : 165))) g.DrawLine(pen, lx, pr.Top, lx, pr.Bottom);
            var limSz = TextRenderer.MeasureText("rev limit", Ui.Small);
            int limX = lx + Ui.Px(4) + limSz.Width <= pr.Right ? (int)lx + Ui.Px(4) : (int)lx - limSz.Width - Ui.Px(4);
            TextRenderer.DrawText(g, "rev limit", Ui.Small, new Point(limX, pr.Top + Ui.Px(2)), Color.FromArgb(220, 120, 120));

            // grid and axes
            using (var grid = new Pen(Ui.Grid))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = pr.Bottom - i * pr.Height / 4f;
                    g.DrawLine(grid, pr.Left, y, pr.Right, y);
                    TextRenderer.DrawText(g, Num.F(topT * i / 4, "#,0"), Ui.Small, new Rectangle(0, (int)y - Ui.Px(8), pr.Left - Ui.Px(6), Ui.Px(16)), Ui.Accent, TextFormatFlags.Right);
                    TextRenderer.DrawText(g, Num.F(topHp * i / 4, "#,0"), Ui.Small, new Rectangle(pr.Right + Ui.Px(6), (int)y - Ui.Px(8), Ui.Px(50), Ui.Px(16)), Ui.Power, TextFormatFlags.Left);
                }
                double step = maxRpm > 10000 ? 2000 : 1000;
                for (double r = 0; r <= maxRpm; r += step)
                {
                    float x = X(r);
                    g.DrawLine(grid, x, pr.Top, x, pr.Bottom);
                    string lbl = r == 0 ? "0 rpm" : Num.F(r / 1000, "0.#") + "k";
                    TextRenderer.DrawText(g, lbl, Ui.Small, new Rectangle((int)x - Ui.Px(30), pr.Bottom + Ui.Px(4), Ui.Px(60), Ui.Px(16)), Ui.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            using (var axis = new Pen(Ui.Border)) g.DrawLine(axis, pr.Left, pr.Bottom, pr.Right, pr.Bottom);

            // legend
            int lgx = pr.Left;
            using (var b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, lgx, Ui.Px(8), Ui.Px(14), Ui.Px(4));
            TextRenderer.DrawText(g, "Torque (Nm)", Ui.Small, new Point(lgx + Ui.Px(18), Ui.Px(2)), Ui.Text);
            lgx += Ui.Px(105);
            using (var b = new SolidBrush(Ui.Power)) g.FillRectangle(b, lgx, Ui.Px(8), Ui.Px(14), Ui.Px(4));
            TextRenderer.DrawText(g, "Power (hp)", Ui.Small, new Point(lgx + Ui.Px(18), Ui.Px(2)), Ui.Text);
            if (compare != null)
            {
                lgx += Ui.Px(100);
                using (var p = new Pen(Ui.Muted, Ui.Px(2)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, lgx, Ui.Px(10), lgx + Ui.Px(14), Ui.Px(10));
                TextRenderer.DrawText(g, "vs " + compareName, Ui.Small, new Rectangle(lgx + Ui.Px(18), Ui.Px(2), pr.Right - lgx - Ui.Px(18), Ui.Px(18)), Ui.Muted, TextFormatFlags.EndEllipsis);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Func<List<double[]>, bool, PointF[]> pts = (c, power) => c.Where(p => p[0] <= maxRpm).Select(p => new PointF(X(p[0]), power ? Y(Hp(p), topHp) : Y(p[1], topT))).ToArray();

            // comparison engine (dashed, faded)
            if (compare != null)
            {
                using (var pt = new Pen(Color.FromArgb(140, Ui.Accent), Ui.Px(2)) { DashStyle = DashStyle.Dash })
                using (var pp = new Pen(Color.FromArgb(140, Ui.Power), Ui.Px(2)) { DashStyle = DashStyle.Dash })
                {
                    var a = pts(compare, false); if (a.Length > 1) g.DrawLines(pt, a);
                    var b2 = pts(compare, true); if (b2.Length > 1) g.DrawLines(pp, b2);
                }
            }

            // this engine
            var tq = pts(shown, false);
            var hp = pts(shown, true);
            using (var pt = new Pen(Ui.Accent, Ui.Px(2) + 0.5f) { LineJoin = LineJoin.Round })
            using (var pp = new Pen(Ui.Power, Ui.Px(2) + 0.5f) { LineJoin = LineJoin.Round })
            {
                if (tq.Length > 1) g.DrawLines(pt, tq);
                if (hp.Length > 1) g.DrawLines(pp, hp);
            }

            // hover readout
            if (hover >= 0 && hover < tq.Length && anim >= 1)
            {
                var p = curve[hover];
                float x = tq[hover].X;
                using (var pen = new Pen(Ui.Muted) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, pr.Top, x, pr.Bottom);
                using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, x - Ui.Px(4), tq[hover].Y - Ui.Px(4), Ui.Px(8), Ui.Px(8));
                using (var b = new SolidBrush(Ui.Power)) g.FillEllipse(b, x - Ui.Px(4), hp[hover].Y - Ui.Px(4), Ui.Px(8), Ui.Px(8));
                string txt = Num.F(p[0], "#,0") + " rpm\n" + Num.F(p[1], "0.0") + " Nm\n" + Num.F(Hp(p), "0.0") + " hp";
                if (compare != null) txt += "\nvs " + Num.F(At(compare, p[0]), "0") + " Nm, " + Num.F(Hp(new[] { p[0], At(compare, p[0]) }), "0") + " hp";
                var sz = TextRenderer.MeasureText(txt, Ui.Small);
                int bx = (int)x + Ui.Px(10);
                if (bx + sz.Width + Ui.Px(12) > pr.Right) bx = (int)x - sz.Width - Ui.Px(22);
                var box = new Rectangle(bx, pr.Top + Ui.Px(8), sz.Width + Ui.Px(12), sz.Height + Ui.Px(8));
                using (var path = Ui.Rounded(box, Ui.Px(4)))
                using (var b = new SolidBrush(Color.FromArgb(235, 15, 23, 42)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, txt, Ui.Small, new Rectangle(box.X + Ui.Px(6), box.Y + Ui.Px(4), sz.Width, sz.Height), Color.White);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
