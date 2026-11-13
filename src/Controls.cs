// Custom-drawn controls for the EasyLib window (dark / light themes).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EasyLib
{
    class ThemedControl : Control
    {
        public ThemedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Theme.Changed += OnThemeChanged;
        }

        protected virtual void OnThemeChanged(object sender, EventArgs e) { Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        protected Color ParentBack
        {
            get { return Ui.BackOf(Parent); }
        }
    }

    enum ButtonKind { Primary, Secondary, Ghost }

    class FlatButton : ThemedControl, IButtonControl
    {
        public ButtonKind Kind;
        public string Glyph;
        public float FontSize = 9f;
        public bool Bold;
        bool hover, down;
        DialogResult dialogResult;

        public FlatButton(string text, ButtonKind kind, string glyph)
        {
            Text = text;
            Kind = kind;
            Glyph = glyph;
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Ui.D(32);
        }

        public DialogResult DialogResult { get { return dialogResult; } set { dialogResult = value; } }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (dialogResult != DialogResult.None)
            {
                Form f = FindForm();
                if (f != null) f.DialogResult = dialogResult;
            }
        }

        public int PreferredWidth
        {
            get
            {
                using (Graphics g = CreateGraphics())
                using (Font f = Ui.Font(FontSize, Bold ? FontStyle.Bold : FontStyle.Regular))
                {
                    int w = Text == "" ? 0 : Ui.Measure(g, Text, f).Width;
                    if (Glyph != null && Ui.IconFamily != null) w += Ui.D(16) + (Text == "" ? 0 : Ui.D(8));
                    return w + Ui.D(Text == "" ? 18 : 28);
                }
            }
        }

        public FlatButton AutoWidth() { Width = PreferredWidth; return this; }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; PerformClick(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg, fg, border = Color.Empty;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    bg = down ? Ui.Mix(t.Accent, Color.Black, 0.15f) : hover ? t.AccentHover : t.Accent;
                    fg = t.AccentText;
                    break;
                case ButtonKind.Secondary:
                    bg = down ? t.Surface3 : hover ? Ui.Mix(t.Surface2, t.Surface3, 0.6f) : t.Surface2;
                    fg = t.Text;
                    border = t.Border;
                    break;
                default:
                    bg = down ? t.Surface3 : hover ? t.Surface2 : ParentBack;
                    fg = hover ? t.Text : t.TextDim;
                    break;
            }
            if (!Enabled)
            {
                bg = Kind == ButtonKind.Ghost ? ParentBack : Ui.Mix(bg, ParentBack, 0.55f);
                fg = t.TextFaint;
                border = Color.Empty;
            }
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Ui.Df(6);
            Ui.FillRound(g, bg, r, rad);
            if (border != Color.Empty) Ui.DrawRound(g, border, r, rad, 1f);
            if (Focused && ShowFocusCues) Ui.DrawRound(g, t.Accent, RectangleF.Inflate(r, -1.5f, -1.5f), rad, 1.2f);

            using (Font f = Ui.Font(FontSize, Bold ? FontStyle.Bold : FontStyle.Regular))
            {
                Font icon = Glyph != null ? Ui.Icon(FontSize + 1.5f) : null;
                try
                {
                    int tw = Text == "" ? 0 : Ui.Measure(g, Text, f).Width;
                    int iw = icon != null ? Ui.D(16) : 0;
                    int gap = icon != null && tw > 0 ? Ui.D(8) : 0;
                    int x = (Width - (tw + iw + gap)) / 2;
                    if (icon != null)
                        Ui.Text(g, Glyph, icon, fg, new Rectangle(x, 0, iw, Height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (tw > 0)
                        Ui.Text(g, Text, f, fg, new Rectangle(x + iw + gap, 0, tw + 2, Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
                finally { if (icon != null) icon.Dispose(); }
            }
        }
    }

    // rounded input with a search glyph and a placeholder
    class SearchBox : ThemedControl
    {
        public readonly TextBox Box;
        readonly Label hint;

        public SearchBox(string placeholder)
        {
            Height = Ui.D(36);
            Box = new TextBox { BorderStyle = BorderStyle.None, Font = Ui.Font(10.5f) };
            hint = new Label { Text = placeholder, AutoSize = false, Font = Ui.Font(10.5f), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.IBeam };
            hint.Click += delegate { Box.Focus(); };
            Controls.Add(hint);
            Controls.Add(Box);
            hint.BringToFront();
            Box.TextChanged += delegate { hint.Visible = Box.Text.Length == 0; };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Cursor = Cursors.IBeam;
            ApplyColors();
        }

        protected override void OnThemeChanged(object sender, EventArgs e) { ApplyColors(); base.OnThemeChanged(sender, e); }

        void ApplyColors()
        {
            Theme t = Theme.Current;
            Box.BackColor = t.InputBg;
            Box.ForeColor = t.Text;
            hint.BackColor = t.InputBg;
            hint.ForeColor = t.TextFaint;
        }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null || hint == null) return;
            int left = Ui.D(36), right = Ui.D(12);
            Box.SetBounds(left, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(10, Width - left - right), Box.PreferredHeight);
            hint.SetBounds(left - 2, Ui.D(3), Math.Max(10, Width - left - right), Height - Ui.D(6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, t.InputBg, r, Ui.Df(8));
            Ui.DrawRound(g, Box.Focused ? t.Accent : t.Border, r, Ui.Df(8), Box.Focused ? 1.5f : 1f);
            // magnifier
            float cx = Ui.Df(18), cy = Height / 2f - Ui.Df(1), rr = Ui.Df(5.5f);
            using (Pen p = new Pen(t.TextDim, Ui.Df(1.6f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - rr, cy - rr, 2 * rr, 2 * rr);
                g.DrawLine(p, cx + rr * 0.72f, cy + rr * 0.72f, cx + rr * 1.55f, cy + rr * 1.55f);
            }
        }
    }

    // dark / light popup menu
    class ThemedMenu : ContextMenuStrip
    {
        class Colors : ProfessionalColorTable
        {
            Theme T { get { return Theme.Current; } }
            public override Color ToolStripDropDownBackground { get { return T.Surface2; } }
            public override Color ImageMarginGradientBegin { get { return T.Surface2; } }
            public override Color ImageMarginGradientMiddle { get { return T.Surface2; } }
            public override Color ImageMarginGradientEnd { get { return T.Surface2; } }
            public override Color MenuBorder { get { return T.Border; } }
            public override Color MenuItemBorder { get { return T.Surface3; } }
            public override Color MenuItemSelected { get { return T.Surface3; } }
            public override Color MenuItemSelectedGradientBegin { get { return T.Surface3; } }
            public override Color MenuItemSelectedGradientEnd { get { return T.Surface3; } }
            public override Color SeparatorDark { get { return T.Border; } }
            public override Color SeparatorLight { get { return T.Border; } }
            public override Color CheckBackground { get { return T.Surface2; } }
            public override Color CheckSelectedBackground { get { return T.Surface3; } }
            public override Color CheckPressedBackground { get { return T.Surface3; } }
            public override Color ButtonSelectedBorder { get { return T.Surface3; } }
        }

        class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.Current.Surface2)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.Current.Surface2)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.TextFaint;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = e.ImageRectangle;
                using (Pen pen = new Pen(Theme.Current.Accent, Ui.Df(2)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new PointF[] { new PointF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.52f),
                        new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f), new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.28f) });
                }
            }
        }

        public ThemedMenu()
        {
            Renderer = new MenuRenderer();
            Font = Ui.Font(9.5f);
            ShowImageMargin = false;
            ShowCheckMargin = true;
        }

        public ToolStripMenuItem AddItem(string text, bool check, EventHandler click)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Checked = check;
            it.Padding = new Padding(0, Ui.D(3), 0, Ui.D(3));
            if (click != null) it.Click += click;
            Items.Add(it);
            return it;
        }
    }

    // button with a drop-down list: one of several values
    class DropButton : FlatButton
    {
        public string[] Items;
        public string Prefix = "";
        int selected;
        public event EventHandler SelectedChanged;

        public DropButton(params string[] items) : base("", ButtonKind.Secondary, null)
        {
            Items = items;
            Height = Ui.D(36);
            UpdateText();
        }

        public int Selected
        {
            get { return selected; }
            set
            {
                int v = Math.Max(0, Math.Min(Items.Length - 1, value));
                if (v == selected) return;
                selected = v;
                UpdateText();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        void UpdateText()
        {
            Text = Prefix + Items[selected] + "  \u25BE";
            Invalidate();
        }

        // wide enough for the longest value, so the bar does not jump when the value changes
        public void FitWidth()
        {
            int w = 0, keep = selected;
            for (int i = 0; i < Items.Length; i++)
            {
                Text = Prefix + Items[i] + "  \u25BE";
                w = Math.Max(w, PreferredWidth);
            }
            selected = keep;
            UpdateText();
            Width = w;
        }

        protected override void OnClick(EventArgs e)
        {
            ThemedMenu m = new ThemedMenu();
            for (int i = 0; i < Items.Length; i++)
            {
                int idx = i;
                m.AddItem(Items[i], i == selected, delegate { Selected = idx; });
            }
            m.MinimumSize = new Size(Width, 0);
            m.Closed += delegate { BeginInvoke((Action)delegate { m.Dispose(); }); };
            m.Show(this, new Point(0, Height + Ui.D(2)));
            base.OnClick(e);
        }
    }

    class Segmented : ThemedControl
    {
        public string[] Items;
        int selected, hover = -1;
        public event EventHandler SelectedChanged;
        public float FontSize = 9f;

        public Segmented(params string[] items)
        {
            Items = items;
            Height = Ui.D(32);
            Cursor = Cursors.Hand;
        }

        public int Selected
        {
            get { return selected; }
            set
            {
                int v = Math.Max(0, Math.Min(Items.Length - 1, value));
                if (v == selected) return;
                selected = v;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        int[] Widths(Graphics g, Font f)
        {
            int[] w = new int[Items.Length];
            int total = 0;
            for (int i = 0; i < Items.Length; i++) { w[i] = Ui.Measure(g, Items[i], f).Width + Ui.D(24); total += w[i]; }
            // stretch to the control width
            int extra = Width - Ui.D(6) - total;
            if (extra > 0) for (int i = 0; i < w.Length; i++) w[i] += extra / w.Length;
            return w;
        }

        public int PreferredWidth
        {
            get
            {
                using (Graphics g = CreateGraphics())
                using (Font f = Ui.Font(FontSize))
                {
                    int total = Ui.D(6);
                    foreach (string s in Items) total += Ui.Measure(g, s, f).Width + Ui.D(24);
                    return total;
                }
            }
        }

        int HitTest(int x)
        {
            using (Graphics g = CreateGraphics())
            using (Font f = Ui.Font(FontSize))
            {
                int[] w = Widths(g, f);
                int px = Ui.D(3);
                for (int i = 0; i < w.Length; i++) { if (x >= px && x < px + w[i]) return i; px += w[i]; }
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.X);
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.X);
            if (h >= 0) Selected = h;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, t.InputBg, r, Ui.Df(8));
            Ui.DrawRound(g, t.Border, r, Ui.Df(8), 1f);
            using (Font f = Ui.Font(FontSize))
            {
                int[] w = Widths(g, f);
                int x = Ui.D(3);
                for (int i = 0; i < Items.Length; i++)
                {
                    RectangleF sr = new RectangleF(x, Ui.D(3), w[i], Height - Ui.D(6) - 1);
                    Color fg = t.TextDim;
                    if (i == selected) { Ui.FillRound(g, t.Accent, sr, Ui.Df(6)); fg = t.AccentText; }
                    else if (i == hover) { Ui.FillRound(g, t.Surface2, sr, Ui.Df(6)); fg = t.Text; }
                    Ui.Text(g, Items[i], f, fg, Rectangle.Round(sr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    x += w[i];
                }
            }
        }
    }

    class Toggle : ThemedControl
    {
        bool on, hover;
        public event EventHandler CheckedChanged;

        public Toggle(string text)
        {
            Text = text;
            Height = Ui.D(28);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public Toggle AutoWidth()
        {
            using (Graphics g = CreateGraphics())
            using (Font f = Ui.Font(9f))
                Width = Ui.D(32 + 8) + Ui.Measure(g, Text, f).Width + Ui.D(16);   // the text is drawn with GDI padding
            return this;
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float tw = Ui.Df(32), th = Ui.Df(18);
            RectangleF tr = new RectangleF(0.5f, (Height - th) / 2f, tw, th);
            Color track = on ? (hover ? t.AccentHover : t.Accent) : (hover ? Ui.Mix(t.Surface3, t.TextFaint, 0.3f) : t.Surface3);
            Ui.FillRound(g, track, tr, th / 2);
            float k = th - Ui.Df(4);
            float kx = on ? tr.Right - k - Ui.Df(2) : tr.X + Ui.Df(2);
            using (SolidBrush b = new SolidBrush(on ? Color.White : (t.Dark ? t.TextDim : Color.White)))
                g.FillEllipse(b, kx, tr.Y + Ui.Df(2), k, k);
            using (Font f = Ui.Font(9f))
                Ui.Text(g, Text, f, Enabled ? t.Text : t.TextFaint, new Rectangle((int)tw + Ui.D(8), 0, Width - (int)tw - Ui.D(8), Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // rounded surface that hosts other controls
    class Card : Panel
    {
        public int Radius = 10;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(Ui.D(12));
            BackColor = Theme.Current.Surface;
            Theme.Changed += OnTheme;
        }

        void OnTheme(object s, EventArgs e) { BackColor = Theme.Current.Surface; Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnTheme;
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Ui.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, t.Surface, r, Ui.Df(Radius));
            Ui.DrawRound(g, t.Border, r, Ui.Df(Radius), 1f);
        }
    }

    // plain container that follows the theme background
    class BgPanel : Panel
    {
        public bool UseSurface;

        public BgPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Theme.Changed += OnTheme;
            OnTheme(null, EventArgs.Empty);
        }

        void OnTheme(object s, EventArgs e) { BackColor = UseSurface ? Theme.Current.Surface : Theme.Current.Bg; Invalidate(); }

        public BgPanel Surface() { UseSurface = true; OnTheme(null, EventArgs.Empty); return this; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnTheme;
            base.Dispose(disposing);
        }
    }

    // app mark + name in the top bar
    class LogoView : ThemedControl
    {
        public LogoView() { Height = Ui.D(36); }

        public int PreferredWidth
        {
            get
            {
                using (Graphics g = CreateGraphics())
                using (Font f = Ui.Font(13.5f, FontStyle.Bold))
                    return Ui.D(32) + Ui.D(10) + Ui.Measure(g, "EasyLib", f).Width + Ui.D(4);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            int s = Ui.D(32), x = 0, y = (Height - s) / 2;
            Bitmap mark = Ui.LogoMark;
            if (mark != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(mark, new Rectangle(x, y, s, s));
                x += s + Ui.D(10);
            }
            using (Font f = Ui.Font(13.5f, FontStyle.Bold))
                Ui.Text(g, "EasyLib", f, t.Text, new Rectangle(x, 0, Math.Max(1, Width - x), Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // single line of themed text
    class TextLine : ThemedControl
    {
        public float FontSize = 9f;
        public FontStyle Style = FontStyle.Regular;
        public bool Dim;
        public Color? Color_;
        public TextFormatFlags Align = TextFormatFlags.Left;

        public TextLine(string text) { Text = text; Height = Ui.D(22); }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(ParentBack);
            Color c = Color_.HasValue ? Color_.Value : Dim ? t.TextDim : t.Text;
            using (Font f = Ui.Font(FontSize, Style))
                Ui.Text(e.Graphics, Text, f, c, ClientRectangle, Align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------
    // results list: custom drawn, the wheel scrolls (never changes the selection)
    // ------------------------------------------------------------------

    class ListColumn
    {
        public string Title;
        public float Weight;
        public int Min;
        public Func<SearchItem, string> Get;
        public bool Chip, Dim;

        public ListColumn(string title, float weight, int min, Func<SearchItem, string> get)
        {
            Title = title; Weight = weight; Min = min; Get = get;
        }
    }

    class ResultList : ThemedControl
    {
        public readonly List<ListColumn> Columns = new List<ListColumn>();
        List<SearchItem> items = new List<SearchItem>();
        int scrollY, selected = -1, hover = -1;
        bool dragThumb, thumbHover;
        int dragStartY, dragStartScroll;
        string message = "";
        public event EventHandler SelectionChanged;
        public event EventHandler ItemActivated;
        // check list mode: a click toggles the row
        public bool CheckBoxes;
        public readonly HashSet<SearchItem> Checked = new HashSet<SearchItem>();
        public event EventHandler CheckedChanged;

        int CheckW { get { return CheckBoxes ? Ui.D(30) : 0; } }

        public void Toggle(SearchItem it)
        {
            if (it == null) return;
            if (!Checked.Remove(it)) Checked.Add(it);
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        public void CheckAll(bool on)
        {
            Checked.Clear();
            if (on) foreach (SearchItem it in items) Checked.Add(it);
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        public ResultList()
        {
            TabStop = true;
        }

        int RowH { get { return Ui.D(30); } }
        int HeadH { get { return Ui.D(32); } }
        int BarW { get { return Ui.D(10); } }

        public List<SearchItem> Items { get { return items; } }

        public string Message
        {
            get { return message; }
            set { message = value ?? ""; Invalidate(); }
        }

        public void SetItems(List<SearchItem> list)
        {
            items = list ?? new List<SearchItem>();
            Checked.Clear();
            scrollY = 0;
            selected = -1;
            hover = -1;
            Invalidate();
        }

        public SearchItem SelectedItem
        {
            get { return selected >= 0 && selected < items.Count ? items[selected] : null; }
        }

        public int SelectedIndex
        {
            get { return selected; }
            set { Select(value, true); }
        }

        void Select(int i, bool raise)
        {
            if (items.Count == 0) return;
            i = Math.Max(0, Math.Min(items.Count - 1, i));
            bool changed = i != selected;
            selected = i;
            EnsureVisible(i);
            Invalidate();
            if (changed && raise && SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        int ViewH { get { return Math.Max(1, Height - HeadH); } }
        int ContentH { get { return items.Count * RowH; } }
        int MaxScroll { get { return Math.Max(0, ContentH - ViewH); } }

        void ScrollTo(int y)
        {
            int ny = Math.Max(0, Math.Min(MaxScroll, y));
            if (ny != scrollY) { scrollY = ny; Invalidate(); }
        }

        void EnsureVisible(int i)
        {
            int top = i * RowH, bottom = top + RowH;
            if (top < scrollY) ScrollTo(top);
            else if (bottom > scrollY + ViewH) ScrollTo(bottom - ViewH);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(scrollY); }

        bool HasBar { get { return ContentH > ViewH; } }

        RectangleF ThumbRect()
        {
            float track = ViewH - Ui.D(8);
            float h = Math.Max(Ui.D(28), track * ViewH / Math.Max(1f, ContentH));
            float y = HeadH + Ui.D(4) + (track - h) * (MaxScroll == 0 ? 0 : scrollY / (float)MaxScroll);
            return new RectangleF(Width - BarW + Ui.D(2), y, BarW - Ui.D(4), h);
        }

        int RowAt(int y)
        {
            if (y < HeadH) return -1;
            int i = (y - HeadH + scrollY) / RowH;
            return i >= 0 && i < items.Count ? i : -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(scrollY - e.Delta / 120 * RowH * 3);
            hover = RowAt(e.Y);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (HasBar && e.X >= Width - BarW && e.Y >= HeadH)
            {
                RectangleF th = ThumbRect();
                if (e.Y >= th.Top && e.Y <= th.Bottom) { dragThumb = true; dragStartY = e.Y; dragStartScroll = scrollY; }
                else ScrollTo(scrollY + (e.Y < th.Top ? -ViewH : ViewH));
                return;
            }
            int i = RowAt(e.Y);
            if (i >= 0 && e.Button == MouseButtons.Left)
            {
                Select(i, true);
                if (CheckBoxes) Toggle(items[i]);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                float track = ViewH - Ui.D(8) - ThumbRect().Height;
                if (track > 0) ScrollTo(dragStartScroll + (int)((e.Y - dragStartY) * MaxScroll / track));
                return;
            }
            bool th = HasBar && e.X >= Width - BarW;
            int h = th ? -1 : RowAt(e.Y);
            if (h != hover || th != thumbHover) { hover = h; thumbHover = th; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; thumbHover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnDoubleClick(EventArgs e)
        {
            Point p = PointToClient(Cursor.Position);
            if (RowAt(p.Y) >= 0 && ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnDoubleClick(e);
        }

        protected override bool IsInputKey(Keys k)
        {
            switch (k & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Enter: case Keys.Space: return true;
            }
            return base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, ViewH / RowH - 1);
            switch (e.KeyCode)
            {
                case Keys.Down: Select(selected + 1, true); e.Handled = true; break;
                case Keys.Up: Select(selected - 1, true); e.Handled = true; break;
                case Keys.PageDown: Select(selected + page, true); e.Handled = true; break;
                case Keys.PageUp: Select(selected - page, true); e.Handled = true; break;
                case Keys.Home: Select(0, true); e.Handled = true; break;
                case Keys.End: Select(items.Count - 1, true); e.Handled = true; break;
                case Keys.Space: if (CheckBoxes && SelectedItem != null) { Toggle(SelectedItem); e.Handled = true; e.SuppressKeyPress = true; } break;
                case Keys.Enter:
                    if (SelectedItem != null && ItemActivated != null) ItemActivated(this, EventArgs.Empty);
                    e.Handled = true; e.SuppressKeyPress = true;
                    break;
            }
            base.OnKeyDown(e);
        }

        int[] ColumnWidths()
        {
            int avail = Width - BarW - Ui.D(8) - CheckW;
            float sum = 0;
            foreach (ListColumn c in Columns) sum += c.Weight;
            int[] w = new int[Columns.Count];
            for (int i = 0; i < w.Length; i++) w[i] = Math.Max(Ui.D(Columns[i].Min), (int)(avail * Columns[i].Weight / sum));
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int[] w = ColumnWidths();
            int pad = Ui.D(10);
            using (Font hf = Ui.Font(8.5f, FontStyle.Bold))
            using (Font f = Ui.Font(9.5f))
            using (Font cf = Ui.Font(8f, FontStyle.Bold))
            {
                // header
                int x = Ui.D(4) + CheckW;
                for (int c = 0; c < Columns.Count; c++)
                {
                    Ui.Text(g, Columns[c].Title, hf, t.TextDim, new Rectangle(x + pad, 0, w[c] - pad, HeadH),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    x += w[c];
                }
                using (Pen p = new Pen(t.Border)) g.DrawLine(p, Ui.D(4), HeadH - 1, Width - Ui.D(4), HeadH - 1);

                g.SetClip(new Rectangle(0, HeadH, Width, Height - HeadH));
                int first = scrollY / RowH, last = Math.Min(items.Count - 1, (scrollY + ViewH) / RowH);
                for (int i = first; i <= last; i++)
                {
                    SearchItem it = items[i];
                    int y = HeadH + i * RowH - scrollY;
                    RectangleF rr = new RectangleF(Ui.D(4), y + 1, Width - BarW - Ui.D(6), RowH - 2);
                    if (i == selected)
                    {
                        Ui.FillRound(g, t.Selection, rr, Ui.Df(6));
                        Ui.FillRound(g, t.Accent, new RectangleF(rr.X, rr.Y + Ui.D(6), Ui.D(3), rr.Height - Ui.D(12)), Ui.Df(1.5f));
                    }
                    else if (i == hover) Ui.FillRound(g, t.Hover, rr, Ui.Df(6));

                    if (CheckBoxes)
                    {
                        bool on = Checked.Contains(it);
                        RectangleF cb = new RectangleF(Ui.D(4) + Ui.D(10), y + (RowH - Ui.D(16)) / 2f, Ui.D(16), Ui.D(16));
                        if (on)
                        {
                            Ui.FillRound(g, t.Accent, cb, Ui.Df(4));
                            using (Pen pen = new Pen(t.AccentText, Ui.Df(2)))
                            {
                                pen.StartCap = pen.EndCap = LineCap.Round;
                                g.DrawLines(pen, new PointF[] { new PointF(cb.X + cb.Width * 0.25f, cb.Y + cb.Height * 0.52f),
                                    new PointF(cb.X + cb.Width * 0.43f, cb.Y + cb.Height * 0.70f), new PointF(cb.X + cb.Width * 0.76f, cb.Y + cb.Height * 0.32f) });
                            }
                        }
                        else Ui.DrawRound(g, t.TextFaint, cb, Ui.Df(4), 1.4f);
                    }
                    x = Ui.D(4) + CheckW;
                    for (int c = 0; c < Columns.Count; c++)
                    {
                        ListColumn col = Columns[c];
                        string s = col.Get(it) ?? "";
                        Rectangle cell = new Rectangle(x + pad, y, w[c] - pad - Ui.D(2), RowH);
                        if (col.Chip && s != "")
                        {
                            Color cc = t.ClassColor(s);
                            Size sz = Ui.Measure(g, s, cf);
                            RectangleF chip = new RectangleF(cell.X, y + (RowH - Ui.D(20)) / 2f, Math.Min(cell.Width, sz.Width + Ui.D(14)), Ui.D(20));
                            Ui.FillRound(g, Color.FromArgb(t.Dark ? 46 : 30, cc), chip, Ui.Df(10));
                            Ui.Text(g, s, cf, cc, Rectangle.Round(chip), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                        }
                        else
                            Ui.Text(g, s, f, col.Dim ? t.TextDim : t.Text, cell,
                                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                        x += w[c];
                    }
                }
                g.ResetClip();

                if (items.Count == 0 && message != "")
                    using (Font mf = Ui.Font(10f))
                        Ui.Text(g, message, mf, t.TextDim, new Rectangle(Ui.D(20), HeadH, Width - Ui.D(40), Height - HeadH),
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            if (HasBar)
            {
                RectangleF th = ThumbRect();
                Color tc = dragThumb || thumbHover ? t.TextFaint : Color.FromArgb(t.Dark ? 110 : 90, t.TextFaint);
                Ui.FillRound(g, tc, th, th.Width / 2);
            }
        }
    }

    // ------------------------------------------------------------------
    // details of the selected part
    // ------------------------------------------------------------------

    class InfoView : ThemedControl
    {
        Part part;
        string message = "Выберите деталь в списке";

        public void SetPart(Part p) { part = p; Invalidate(); }
        public void SetMessage(string m) { part = null; message = m ?? ""; Invalidate(); }

        string I(string k)
        {
            string v;
            return part != null && part.Info.TryGetValue(k, out v) && v != null ? v : "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (part == null)
            {
                using (Font f = Ui.Font(10f))
                    Ui.Text(g, message, f, t.TextDim, ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int y = 0, W = Width;
            string title = I("MPN") != "" ? I("MPN") : I("Title");
            using (Font tf = Ui.Font(13f, FontStyle.Bold))
            {
                int h = Ui.Measure(g, "Ag", tf).Height;
                Ui.Text(g, title, tf, t.Text, new Rectangle(0, y, W, h), TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += h + Ui.D(2);
            }
            using (Font sf = Ui.Font(9.5f))
            {
                List<string> sub = new List<string>();
                if (I("Manufacturer") != "") sub.Add(I("Manufacturer"));
                if (I("Footprint") != "") sub.Add(I("Footprint"));
                if (I("Official") == "no" && I("Author") != "") sub.Add("автор: " + I("Author"));
                if (I("Lib") != "") sub.Add("библиотека " + I("Lib"));
                int h = Ui.Measure(g, "Ag", sf).Height;
                Ui.Text(g, string.Join("  ·  ", sub.ToArray()), sf, t.TextDim, new Rectangle(0, y, W, h), TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += h + Ui.D(8);
            }
            // chips
            using (Font cf = Ui.Font(8f, FontStyle.Bold))
            {
                int x = 0;
                string cls = I("Class");
                if (cls != "") x = Chip(g, cf, cls, t.ClassColor(cls), x, y);
                if (I("LCSC") != "") x = Chip(g, cf, I("LCSC"), t.Accent, x, y);
                x = Chip(g, cf, I("Model3D") == "yes" ? "3D-модель" : "без 3D", I("Model3D") == "yes" ? t.Success : t.TextDim, x, y);
                y += Ui.D(22) + Ui.D(10);
            }
            using (Font kf = Ui.Font(9f))
            using (Font vf = Ui.Font(9.5f))
            {
                int lh = Ui.Measure(g, "Ag", vf).Height + Ui.D(5);
                List<string[]> rows = new List<string[]>();
                if (I("Value") != "") rows.Add(new string[] { "Номинал", I("Value") });
                if (I("Description") != "") rows.Add(new string[] { "Описание", I("Description") });
                if (I("Keywords") != "") rows.Add(new string[] { "Ключевые слова", I("Keywords") });
                rows.Add(new string[] { "Выводы / площадки", I("Pins") + " / " + I("Pads") });
                // stock and price in the chosen shops
                DateTime shopsAt = DateTime.MinValue;
                foreach (string id in Shops.Enabled)
                {
                    ShopResult sr;
                    if (!part.Shops.TryGetValue(id, out sr)) continue;
                    rows.Add(new string[] { Shops.Name(id), sr.Text, sr.State == 0 ? "dim" : sr.InStock ? "ok" : sr.State == 3 ? "warn" : "dim" });
                    if (sr.State != 0 && sr.At > shopsAt) shopsAt = sr.At;
                }
                if (shopsAt != DateTime.MinValue) rows.Add(new string[] { "", "наличие и цены на " + shopsAt.ToString("dd.MM HH:mm"), "faint" });
                int kw = 0;
                foreach (string[] r in rows) kw = Math.Max(kw, Ui.Measure(g, r[0], kf).Width);
                kw += Ui.D(18);
                foreach (string[] r in rows)
                {
                    if (y > Height - lh) break;
                    Ui.Text(g, r[0], kf, t.TextDim, new Rectangle(0, y, kw, lh), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                    bool wrap = r[0] == "Описание";
                    int vh = lh;
                    if (wrap)
                    {
                        Size need = TextRenderer.MeasureText(g, r[1], vf, new Size(W - kw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        vh = Math.Min(need.Height, (lh - Ui.D(5)) * 3) + Ui.D(5);
                    }
                    Color vc = r.Length < 3 ? t.Text : r[2] == "ok" ? t.Success : r[2] == "warn" ? t.Warning : r[2] == "faint" ? t.TextFaint : t.TextDim;
                    Ui.Text(g, r[1], r.Length > 2 && r[2] == "faint" ? kf : vf, vc, new Rectangle(kw, y, W - kw, vh),
                            TextFormatFlags.Left | TextFormatFlags.Top | (wrap ? TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis));
                    y += vh;
                }
                string warn = I("Official") == "no" ? "Деталь из пользовательской библиотеки EasyEDA: её никто не проверял. Сверьте футпринт с даташитом."
                            : I("NoFootprint") == "yes" ? "В библиотеке KiCad у этого символа нет футпринта: в EasyLib добавится только символ. Футпринт можно взять отдельно (например, из LCSC)."
                            : I("Official") == "import" ? "Импортировано" + (I("Vendor") != "" ? " из " + I("Vendor") : "") + " и уже добавлено в библиотеку."
                            : "";
                if (warn != "" && y < Height - lh)
                {
                    y += Ui.D(4);
                    Ui.Text(g, warn, kf, I("Official") == "import" ? t.Success : t.Warning, new Rectangle(0, y, W, Height - y), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
            }
        }

        int Chip(Graphics g, Font f, string s, Color c, int x, int y)
        {
            Theme t = Theme.Current;
            Size sz = Ui.Measure(g, s, f);
            RectangleF r = new RectangleF(x, y, sz.Width + Ui.D(16), Ui.D(22));
            Ui.FillRound(g, Color.FromArgb(t.Dark ? 46 : 28, c), r, Ui.Df(11));
            Ui.Text(g, s, f, c, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return (int)r.Right + Ui.D(6);
        }
    }

    // symbol or footprint preview, re-rendered at the control's own size
    class PreviewBox : ThemedControl
    {
        readonly bool footprint;
        readonly string caption;
        Part part;
        Bitmap cache;
        string message = "";

        public PreviewBox(bool footprint, string caption)
        {
            this.footprint = footprint;
            this.caption = caption;
        }

        public void SetPart(Part p) { part = p; message = ""; Drop(); Invalidate(); }
        public void SetMessage(string m) { part = null; message = m ?? ""; Drop(); Invalidate(); }

        void Drop() { if (cache != null) { cache.Dispose(); cache = null; } }

        protected override void OnThemeChanged(object sender, EventArgs e) { Drop(); base.OnThemeChanged(sender, e); }
        protected override void OnResize(EventArgs e) { Drop(); base.OnResize(e); }

        protected override void Dispose(bool disposing) { if (disposing) Drop(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            PreviewPalette pal = t.Preview;
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = footprint ? pal.FpBg : pal.SymBg;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Ui.Df(8);
            using (GraphicsPath clip = Ui.Round(r, rad))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, clip);
                if (part != null && Width > 20 && Height > 20)
                {
                    if (cache == null)
                    {
                        try { cache = footprint ? Preview.Footprint(part, Width, Height, pal) : Preview.Symbol(part, Width, Height, pal); }
                        catch { cache = null; }
                    }
                    if (cache != null)
                    {
                        Region old = g.Clip;
                        g.SetClip(clip);
                        g.DrawImageUnscaled(cache, 0, 0);
                        g.Clip = old;
                    }
                }
                using (Pen p = new Pen(t.Border)) g.DrawPath(p, clip);
            }
            using (Font f = Ui.Font(8f, FontStyle.Bold))
            {
                Color fg = footprint || t.Dark ? Color.FromArgb(170, 200, 205, 212) : Color.FromArgb(150, 60, 60, 60);
                Ui.Text(g, caption, f, fg, new Rectangle(Ui.D(10), Ui.D(6), Width - Ui.D(20), Ui.D(18)), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
            }
            if (part == null && message != "")
                using (Font f = Ui.Font(9.5f))
                    Ui.Text(g, message, f, footprint || t.Dark ? Color.FromArgb(150, 170, 175, 182) : t.TextDim, ClientRectangle,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }
}
