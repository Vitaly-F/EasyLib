// EasyLib main window: search LCSC / EasyEDA parts, preview them and add them to native
// Altium libraries (EasyLib.SchLib + EasyLib.PcbLib).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyLib
{
    class MainForm : Form
    {
        // top
        SearchBox search;
        DropButton sourceSeg;
        FlatButton btnSearch, btnTheme, btnHelp;
        // list
        ResultList list;
        TextLine lblCount;
        FlatButton btnPrev, btnNext;
        SplitContainer split;
        // preview
        Segmented viewSeg;
        PreviewBox symBox, fpBox;
        Viewer3D viewer;
        TableLayoutPanel twoPane;
        BgPanel previewHost, tools3D, toolsDim, pvHead;
        DimView dimView;
        TableLayoutPanel rightCol;
        Toggle tPads;
        FlatButton b3Top, b3Iso, b3Side, b3Window;
        InfoView info;
        FlatButton btnAdd, btnDatasheet, btnPage, btnShops;
        // bottom
        TextLine status;
        FlatButton btnLib, btnOpenFolder, btnClean;
        Toggle t3D, tTop, tExact;
        readonly ToolTip tips = new ToolTip { InitialDelay = 500, AutoPopDelay = 12000 };

        static readonly string[] SourceKeys = { "all", "lcsc", "kicad", "user" };
        FlatButton btnImport;
        Toggle tWatch;
        FileSystemWatcher watcher;
        readonly HashSet<string> seenDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string[]> importQueue = new Queue<string[]>();
        bool importing;

        string curQuery = "";
        int curPage = 1, totalPages = 1;
        Part curPart;
        SearchItem curItem;
        int loadToken, objToken;
        string objUuid, objText;
        bool adding, searching;
        Settings settings;
        Timer selTimer;

        public MainForm()
        {
            settings = Settings.Load();
            Theme.Set(settings.Theme);
            Text = "EasyLib — детали LCSC / EasyEDA для Altium";
            Font = Ui.Font(9f);
            Ui.ApplyIcon(this);
            BackColor = Theme.Current.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;      // small screens with a large Windows scale
            MinimumSize = new Size(Math.Min(Ui.D(980), wa.Width), Math.Min(Ui.D(620), wa.Height));
            ClientSize = new Size(Math.Min(Ui.D(1280), wa.Width - 40), Math.Min(Ui.D(800), wa.Height - 40));
            KeyPreview = true;

            BuildUi();

            sourceSeg.Selected = Math.Max(0, Array.IndexOf(SourceKeys, settings.Source));
            t3D.Checked = settings.With3D;
            tWatch.Checked = settings.WatchDownloads;
            tTop.Checked = settings.TopMost;
            tExact.Checked = settings.Exact;
            Shops.Enabled = Shops.ParseEnabled(settings.Shops);
            TopMost = settings.TopMost;
            UpdateLibButton();
            if (settings.Width > 0 && settings.Height > 0)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(settings.X, settings.Y, settings.Width, settings.Height);
                EnsureVisible();
            }
            Theme.Changed += OnTheme;
            SetStatus("Найдите деталь: номер LCSC (C25804), партномер (STM32F103C8T6) или описание (10k 0603). Enter — искать.", 0);
            UpdateButtons();
        }

        void EnsureVisible()
        {
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(Bounds)) return;
            StartPosition = FormStartPosition.CenterScreen;
            Location = new Point(100, 100);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(this, Theme.Current);
        }

        void OnTheme(object s, EventArgs e)
        {
            BackColor = Theme.Current.Bg;
            split.BackColor = Theme.Current.Bg;
            twoPane.BackColor = Theme.Current.Surface;
            Native.StyleTitleBar(this, Theme.Current);
            UpdateThemeButton();
            Ui.ForAll(this, delegate(Control c) { c.Invalidate(); });
        }

        static FlatButton IconButton(string glyph, string fallback, ButtonKind kind)
        {
            bool icons = Ui.IconFamily != null;
            FlatButton b = new FlatButton(icons ? "" : fallback, kind, icons ? glyph : null);
            b.Width = icons ? Ui.D(36) : b.PreferredWidth;
            b.Height = Ui.D(36);
            return b;
        }

        // ------------------------------------------------------------ layout

        void BuildUi()
        {
            // ---------- top bar
            BgPanel top = new BgPanel { Dock = DockStyle.Top, Height = Ui.D(64) };
            search = new SearchBox("Номер LCSC, партномер или описание детали…");
            search.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(1); }
                else if (e.KeyCode == Keys.Down && list.Items.Count > 0) { e.SuppressKeyPress = true; list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 0; }
            };
            sourceSeg = new DropButton("Все базы", "LCSC", "KiCad", "Сообщество EasyEDA");
            sourceSeg.FitWidth();
            tips.SetToolTip(sourceSeg, "Где искать детали");
            sourceSeg.SelectedChanged += delegate
            {
                settings.Source = SourceKeys[sourceSeg.Selected];
                settings.Save();
                if (curQuery != "") DoSearch(1);
            };
            tExact = new Toggle("Точно").AutoWidth();
            tExact.CheckedChanged += delegate
            {
                settings.Exact = tExact.Checked;
                settings.Save();
                if (curQuery != "" && !searching) DoSearch(1);
            };
            tips.SetToolTip(tExact, "Только детали, в которых есть весь запрос целиком:\nпартномер, номер LCSC, корпус, номинал. Просматривает первые 3 страницы поиска.");
            btnSearch = new FlatButton("Найти", ButtonKind.Primary, "") { Height = Ui.D(36) };
            btnSearch.AutoWidth();
            btnSearch.Click += delegate { DoSearch(1); };
            btnTheme = IconButton("", "Тема", ButtonKind.Ghost);
            btnTheme.Click += delegate
            {
                settings.Theme = Theme.Current.Dark ? "light" : "dark";
                settings.Save();
                Theme.Set(settings.Theme);
            };
            btnHelp = IconButton("", "Справка", ButtonKind.Ghost);
            btnHelp.Click += delegate { ShowHelp(); };
            btnImport = new FlatButton("Импорт", ButtonKind.Secondary, "\uE8B5") { Height = Ui.D(36) };
            btnImport.AutoWidth();
            btnImport.Click += delegate { ImportDialog(); };
            top.Controls.AddRange(new Control[] { search, sourceSeg, tExact, btnSearch, btnImport, btnTheme, btnHelp });
            top.Resize += delegate
            {
                int m = Ui.D(16), y = (top.Height - Ui.D(36)) / 2;
                int r = top.ClientSize.Width - m;
                btnHelp.Location = new Point(r - btnHelp.Width, y); r = btnHelp.Left - Ui.D(4);
                btnTheme.Location = new Point(r - btnTheme.Width, y); r = btnTheme.Left - Ui.D(12);
                btnImport.Location = new Point(r - btnImport.Width, y); r = btnImport.Left - Ui.D(8);
                btnSearch.Location = new Point(r - btnSearch.Width, y); r = btnSearch.Left - Ui.D(8);
                tExact.Location = new Point(r - tExact.Width, (top.Height - tExact.Height) / 2); r = tExact.Left - Ui.D(12);
                sourceSeg.Location = new Point(r - sourceSeg.Width, y); r = sourceSeg.Left - Ui.D(8);
                search.Location = new Point(m, y);
                search.Size = new Size(Math.Max(Ui.D(120), r - search.Left), Ui.D(36));
            };

            // ---------- bottom bar
            BgPanel bottom = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(52) };
            status = new TextLine("") { Dim = true, FontSize = 9f };
            btnLib = new FlatButton("", ButtonKind.Secondary, "") { Height = Ui.D(32) };
            btnLib.Click += delegate { BrowseLibDir(); };
            btnOpenFolder = new FlatButton("Открыть папку", ButtonKind.Ghost, null) { Height = Ui.D(32) };
            btnOpenFolder.AutoWidth();
            btnOpenFolder.Click += delegate { OpenFolder(); };
            btnClean = new FlatButton("Очистка", ButtonKind.Ghost, null) { Height = Ui.D(32) };
            btnClean.AutoWidth();
            btnClean.Click += delegate { OpenLibraryForm(); };
            tips.SetToolTip(btnClean, "Что лежит в библиотеке и сколько места занимает; удаление деталей и 3D-моделей");
            t3D = new Toggle("3D-модель (STEP)").AutoWidth();
            t3D.CheckedChanged += delegate { settings.With3D = t3D.Checked; settings.Save(); };
            tTop = new Toggle("Поверх окон").AutoWidth();
            tTop.CheckedChanged += delegate { TopMost = tTop.Checked; settings.TopMost = tTop.Checked; settings.Save(); };
            tWatch = new Toggle("Из «Загрузок»").AutoWidth();
            tWatch.CheckedChanged += delegate { settings.WatchDownloads = tWatch.Checked; settings.Save(); UpdateWatcher(); };
            bottom.Controls.AddRange(new Control[] { status, btnLib, btnOpenFolder, btnClean, tWatch, t3D, tTop });
            bottom.Resize += delegate { LayoutBottom(bottom); };

            // ---------- centre
            BgPanel centre = new BgPanel { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(16), 0, Ui.D(16), 0) };
            split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = Ui.D(12), BackColor = Theme.Current.Bg, FixedPanel = FixedPanel.None };
            split.Panel1.BackColor = Color.Transparent;
            split.Panel2.BackColor = Color.Transparent;
            centre.Controls.Add(split);

            // left: results
            Card listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(6), Ui.D(6), Ui.D(6), Ui.D(4)) };
            list = new ResultList { Dock = DockStyle.Fill };
            list.Columns.Add(new ListColumn("LCSC", 0.85f, 64, delegate(SearchItem it) { return it.Lcsc != "" ? it.Lcsc : "—"; }) { Dim = true });
            list.Columns.Add(new ListColumn("Партномер", 1.8f, 110, delegate(SearchItem it) { return it.Mpn; }));
            list.Columns.Add(new ListColumn("Производитель", 1.25f, 80, delegate(SearchItem it) { return it.Manufacturer != "" ? it.Manufacturer : it.Source == "user" && it.Author != "" ? "@" + it.Author : ""; }));
            list.Columns.Add(new ListColumn("Корпус", 1.45f, 80, delegate(SearchItem it) { return it.Package; }));
            list.Columns.Add(new ListColumn("Номинал", 0.85f, 56, delegate(SearchItem it) { return it.Value; }));
            list.Columns.Add(new ListColumn("Тип", 1.05f, 84, delegate(SearchItem it) { return it.Class; }) { Chip = true });
            list.Message = "Введите запрос и нажмите Enter";
            list.SelectionChanged += delegate { selTimer.Stop(); selTimer.Start(); ShowPending(); };
            list.ItemActivated += delegate { AddCurrent(); };
            selTimer = new Timer { Interval = 400 };
            selTimer.Tick += delegate { selTimer.Stop(); Task t = LoadSelected(); };

            BgPanel pager = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(40) }.Surface();
            lblCount = new TextLine("") { Dim = true };
            btnPrev = IconButton("", "‹", ButtonKind.Ghost);
            btnPrev.Click += delegate { if (curPage > 1) DoSearch(curPage - 1); };
            btnNext = IconButton("", "›", ButtonKind.Ghost);
            btnNext.Click += delegate { if (curPage < totalPages) DoSearch(curPage + 1); };
            pager.Controls.AddRange(new Control[] { lblCount, btnPrev, btnNext });
            pager.Resize += delegate
            {
                int y = (pager.Height - Ui.D(32)) / 2;
                btnPrev.Size = btnNext.Size = new Size(btnPrev.Width, Ui.D(32));
                btnNext.Location = new Point(pager.Width - btnNext.Width - Ui.D(4), y);
                btnPrev.Location = new Point(btnNext.Left - btnPrev.Width - Ui.D(2), y);
                lblCount.SetBounds(Ui.D(10), 0, Math.Max(10, btnPrev.Left - Ui.D(16)), pager.Height);
            };
            BgPanel vendors = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(38) }.Surface();
            TextLine vlabel = new TextLine("Нет нужной детали? Искать на") { Dim = true, FontSize = 8.5f };
            vendors.Controls.Add(vlabel);
            List<FlatButton> vbtns = new List<FlatButton>();
            foreach (string v in new string[] { "SnapEDA", "Ultra Librarian", "Component Search Engine" })
            {
                string vn = v;
                FlatButton b = new FlatButton(v == "Component Search Engine" ? "CSE (SamacSys)" : v, ButtonKind.Ghost, null) { Height = Ui.D(28), FontSize = 8.5f };
                b.AutoWidth();
                b.Click += delegate { OpenVendor(vn); };
                vbtns.Add(b);
                vendors.Controls.Add(b);
            }
            vendors.Resize += delegate
            {
                using (Graphics g = vendors.CreateGraphics())
                using (Font f = Ui.Font(vlabel.FontSize)) vlabel.Width = Ui.Measure(g, vlabel.Text, f).Width + Ui.D(6);
                vlabel.SetBounds(Ui.D(10), 0, vlabel.Width, vendors.Height);
                int x = vlabel.Right + Ui.D(2);
                foreach (FlatButton b in vbtns) { b.Location = new Point(x, (vendors.Height - b.Height) / 2); x = b.Right + Ui.D(2); b.Visible = b.Right < vendors.Width; }
            };
            listCard.Controls.Add(list);
            listCard.Controls.Add(vendors);
            listCard.Controls.Add(pager);
            split.Panel1.Controls.Add(listCard);

            // right: preview, details, actions
            TableLayoutPanel right = rightCol = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.D(60)));
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Card previewCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Ui.D(6)), Padding = new Padding(Ui.D(10)) };
            BgPanel pvHead = new BgPanel { Dock = DockStyle.Top, Height = Ui.D(40) }.Surface();
            viewSeg = new Segmented("Символ и футпринт", "3D-модель", "Размеры") { Height = Ui.D(32) };
            viewSeg.Width = viewSeg.PreferredWidth;
            viewSeg.SelectedChanged += delegate { ShowView(); };
            tools3D = new BgPanel { Visible = false, Dock = DockStyle.Bottom, Height = Ui.D(44) }.Surface();
            tPads = new Toggle("Площадки").AutoWidth();
            tPads.Checked = true;
            tPads.CheckedChanged += delegate { Build3D(); };
            b3Top = new FlatButton("Сверху", ButtonKind.Ghost, null) { Height = Ui.D(30) }.AutoWidth();
            b3Top.Click += delegate { viewer.SetView(0, 0); };
            b3Iso = new FlatButton("Изометрия", ButtonKind.Ghost, null) { Height = Ui.D(30) }.AutoWidth();
            b3Iso.Click += delegate { viewer.ResetView(); };
            b3Side = new FlatButton("Сбоку", ButtonKind.Ghost, null) { Height = Ui.D(30) }.AutoWidth();
            b3Side.Click += delegate { viewer.SetView(0, -90); };
            b3Window = IconButton("", "Окно", ButtonKind.Ghost);
            b3Window.Height = Ui.D(30);
            b3Window.Click += delegate { Open3DWindow(); };
            tools3D.Controls.AddRange(new Control[] { tPads, b3Top, b3Iso, b3Side, b3Window });
            pvHead.Controls.Add(viewSeg);
            TextLine hint3D = new TextLine("ЛКМ — вращать · ПКМ — сдвиг · колесо — масштаб") { Dim = true, FontSize = 8.5f, Align = TextFormatFlags.Right };
            tools3D.Controls.Add(hint3D);
            tools3D.Resize += delegate
            {
                int x = 0, cy = Ui.D(8) + (tools3D.Height - Ui.D(8)) / 2;
                tPads.Location = new Point(x, cy - tPads.Height / 2); x = tPads.Right + Ui.D(12);
                foreach (Control c in new Control[] { b3Top, b3Iso, b3Side, b3Window }) { c.Location = new Point(x, cy - c.Height / 2); x = c.Right + Ui.D(2); }
                hint3D.SetBounds(x + Ui.D(8), cy - Ui.D(11), Math.Max(0, tools3D.Width - x - Ui.D(10)), Ui.D(22));
                int need;
                using (Graphics g = tools3D.CreateGraphics())
                using (Font f = Ui.Font(hint3D.FontSize)) need = Ui.Measure(g, hint3D.Text, f).Width + Ui.D(4);
                hint3D.Visible = hint3D.Width >= need;
            };
            this.pvHead = pvHead;
            pvHead.Resize += delegate { LayoutPreviewHead(); };

            previewHost = new BgPanel { Dock = DockStyle.Fill }.Surface();
            twoPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Current.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
            twoPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            twoPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            twoPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            symBox = new PreviewBox(false, "СИМВОЛ") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Ui.D(5), 0) };
            fpBox = new PreviewBox(true, "ФУТПРИНТ") { Dock = DockStyle.Fill, Margin = new Padding(Ui.D(5), 0, 0, 0) };
            twoPane.Controls.Add(symBox, 0, 0);
            twoPane.Controls.Add(fpBox, 1, 0);
            viewer = new Viewer3D { Dock = DockStyle.Fill, Visible = false };
            dimView = new DimView { Dock = DockStyle.Fill, Visible = false, Mm = settings.DimUnit != "mil" };
            toolsDim = DimView.MakeToolbar(dimView, settings.DimUnit != "mil",
                delegate(bool mm) { settings.DimUnit = mm ? "mm" : "mil"; settings.Save(); }, delegate { OpenDimWindow(); }, true);
            toolsDim.Visible = false;
            fpBox.DoubleClick += delegate { if (curPart != null && curPart.Pads.Count > 0) viewSeg.Selected = 2; };
            previewHost.Controls.Add(twoPane);
            previewHost.Controls.Add(viewer);
            previewHost.Controls.Add(dimView);
            previewHost.Controls.Add(tools3D);
            previewHost.Controls.Add(toolsDim);
            previewCard.Controls.Add(previewHost);
            previewCard.Controls.Add(pvHead);

            Card infoCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, Ui.D(6), 0, Ui.D(6)), Padding = new Padding(Ui.D(16), Ui.D(14), Ui.D(16), Ui.D(10)) };
            info = new InfoView { Dock = DockStyle.Fill };
            infoCard.Controls.Add(info);

            BgPanel actions = new BgPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            btnAdd = new FlatButton("Добавить в библиотеку", ButtonKind.Primary, "") { Height = Ui.D(42), FontSize = 10f, Bold = true };
            btnAdd.AutoWidth();
            btnAdd.Click += delegate { AddCurrent(); };
            btnDatasheet = new FlatButton("Даташит", ButtonKind.Secondary, "") { Height = Ui.D(42) };
            btnDatasheet.AutoWidth();
            btnDatasheet.Click += delegate { OpenUrl(Info("Datasheet")); };
            btnPage = new FlatButton("Страница детали", ButtonKind.Secondary, "") { Height = Ui.D(42) };
            btnPage.AutoWidth();
            btnPage.Click += delegate { OpenUrl(Info("URL")); };
            btnShops = new FlatButton("Магазины  \u25BE", ButtonKind.Secondary, null) { Height = Ui.D(42) };
            btnShops.AutoWidth();
            btnShops.Click += delegate { ShopsMenu(); };
            tips.SetToolTip(btnShops, "В каких магазинах показывать наличие и цену; открыть деталь на сайте магазина");
            actions.Controls.AddRange(new Control[] { btnAdd, btnDatasheet, btnPage, btnShops });
            actions.Resize += delegate
            {
                int y = actions.Height - btnAdd.Height - Ui.D(6);
                // short captions when the column is narrow
                for (int pass = 0; pass < 2; pass++)
                {
                    string add = pass == 0 ? "Добавить в библиотеку" : "Добавить", page = pass == 0 ? "Страница детали" : "Страница";
                    if (btnAdd.Text != add) { btnAdd.Text = add; btnAdd.AutoWidth(); }
                    if (btnPage.Text != page) { btnPage.Text = page; btnPage.AutoWidth(); }
                    if (btnAdd.Width + btnDatasheet.Width + btnPage.Width + btnShops.Width + 3 * Ui.D(8) <= actions.Width) break;
                }
                btnAdd.Location = new Point(0, y);
                btnDatasheet.Location = new Point(btnAdd.Right + Ui.D(8), y);
                btnPage.Location = new Point(btnDatasheet.Right + Ui.D(8), y);
                btnShops.Location = new Point(btnPage.Right + Ui.D(8), y);
            };

            right.Controls.Add(previewCard, 0, 0);
            right.Controls.Add(infoCard, 0, 1);
            right.Controls.Add(actions, 0, 2);
            split.Panel2.Controls.Add(right);

            Controls.Add(centre);
            Controls.Add(bottom);
            Controls.Add(top);

            symBox.SetMessage("");
            fpBox.SetMessage("");
            info.SetMessage("Выберите деталь в списке");
            UpdateThemeButton();

            Load += delegate
            {
                try { split.SplitterDistance = Math.Max(Ui.D(380), (int)(split.Width * 0.53)); } catch { }
                search.Box.Focus();
                Task t = RepairOnStart();
                UpdateWatcher();
            };
            Ui.ForAll(this, delegate(Control c)
            {
                c.AllowDrop = true;
                c.DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
                c.DragDrop += delegate(object s, DragEventArgs e)
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0) ImportFiles(files, false);
                };
            });
            FormClosing += delegate { SaveSettings(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.SuppressKeyPress = true; }
            };
        }

        void LayoutPreviewHead()
        {
            viewSeg.Location = new Point(0, 0);
            tools3D.Visible = viewSeg.Selected == 1;
            toolsDim.Visible = viewSeg.Selected == 2;
        }

        void LayoutBottom(Control bottom)
        {
            int m = Ui.D(16), y = (bottom.Height - Ui.D(32)) / 2;
            int r = bottom.ClientSize.Width - m;
            tTop.Location = new Point(r - tTop.Width, (bottom.Height - tTop.Height) / 2); r = tTop.Left - Ui.D(16);
            t3D.Location = new Point(r - t3D.Width, (bottom.Height - t3D.Height) / 2); r = t3D.Left - Ui.D(16);
            tWatch.Location = new Point(r - tWatch.Width, (bottom.Height - tWatch.Height) / 2); r = tWatch.Left - Ui.D(16);
            btnClean.Location = new Point(r - btnClean.Width, y); r = btnClean.Left - Ui.D(2);
            btnOpenFolder.Location = new Point(r - btnOpenFolder.Width, y); r = btnOpenFolder.Left - Ui.D(4);
            btnLib.Width = Math.Min(Math.Min(btnLib.PreferredWidth, Ui.D(230)), Math.Max(Ui.D(110), r - m - Ui.D(160)));
            btnLib.Location = new Point(r - btnLib.Width, y); r = btnLib.Left - Ui.D(12);
            status.SetBounds(m, 0, Math.Max(10, r - m), bottom.Height);
        }

        void UpdateThemeButton()
        {
            bool icons = Ui.IconFamily != null;
            if (icons) btnTheme.Glyph = Theme.Current.Dark ? "" : "";
            else btnTheme.Text = Theme.Current.Dark ? "Светлая" : "Тёмная";
            btnTheme.Invalidate();
        }

        void UpdateLibButton()
        {
            string d = LibDir;
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string shown = d.StartsWith(docs, StringComparison.OrdinalIgnoreCase) ? "Документы" + d.Substring(docs.Length) : d;
            btnLib.Text = shown;
            tips.SetToolTip(btnLib, "Папка библиотек: " + LibDir + "\nНажмите, чтобы выбрать другую (например, общую сетевую).");
            if (btnLib.Parent != null) LayoutBottom(btnLib.Parent);
        }

        // ------------------------------------------------------------ helpers

        void SetStatus(string s, int kind)
        {
            status.Text = s;
            Theme t = Theme.Current;
            status.Color_ = kind == 2 ? t.Danger : kind == 1 ? t.Success : (Color?)null;
            status.Dim = kind == 0;
            status.Invalidate();
        }

        void UpdateButtons()
        {
            bool has = curPart != null;
            btnAdd.Enabled = has && !adding && !importing && curItem != null;
            btnDatasheet.Enabled = has && Info("Datasheet") != "";
            btnPage.Enabled = has && Info("URL") != "";
            btnPrev.Enabled = !searching && curPage > 1;
            btnNext.Enabled = !searching && curPage < totalPages;
            btnSearch.Enabled = !searching;
        }

        string Info(string key)
        {
            string v;
            return curPart != null && curPart.Info.TryGetValue(key, out v) && v != null ? v : "";
        }

        string LibDir
        {
            get
            {
                string d = (settings.LibDir ?? "").Trim();
                return d == "" ? Settings.DefaultLibDir : d;
            }
        }

        string SchPath { get { return Path.Combine(LibDir, "EasyLib.SchLib"); } }
        string PcbPath { get { return Path.Combine(LibDir, "EasyLib.PcbLib"); } }

        void SaveSettings()
        {
            if (WindowState == FormWindowState.Normal) { settings.X = Left; settings.Y = Top; settings.Width = Width; settings.Height = Height; }
            settings.Save();
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void OpenLibraryForm()
        {
            LibraryForm f = new LibraryForm(LibDir);
            f.TopMost = TopMost;
            f.LibraryChanged += delegate { SetStatus("Библиотека изменена. В Altium: Components → ≡ → Refresh.", 1); };
            f.Show(this);
        }

        void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(LibDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + LibDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus(ex.Message, 2); }
        }

        void BrowseLibDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Папка для EasyLib.SchLib и EasyLib.PcbLib";
                d.SelectedPath = LibDir;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    settings.LibDir = d.SelectedPath;
                    settings.Save();
                    UpdateLibButton();
                    SetStatus("Библиотеки будут сохраняться в " + d.SelectedPath + ". Подключите их в Altium (кнопка «?»).", 0);
                }
            }
        }

        static string Friendly(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            if (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            if (ex is BlockedException || ex is LibBusyException || ex is NoCadException || ex is InvalidDataException) return ex.Message;
            if (ex is UnauthorizedAccessException && ex.Message.Contains("только для чтения")) return "Не удалось записать библиотеку: " + ex.Message + ".";
            System.Net.WebException we = ex as System.Net.WebException;
            if (we != null)
            {
                System.Net.HttpWebResponse hr = we.Response as System.Net.HttpWebResponse;
                string host = hr != null && hr.ResponseUri != null ? hr.ResponseUri.Host : "";
                if (hr != null)
                    return "Сервер " + (host != "" ? host + " " : "") + "ответил ошибкой " + (int)hr.StatusCode + ". Обычно это временно: попробуйте через пару минут.";
                if (we.Status == System.Net.WebExceptionStatus.Timeout)
                    return "Сервер долго не отвечает. Проверьте интернет и попробуйте ещё раз.";
                return "Нет связи с сервером. Проверьте интернет (или VPN/прокси) и попробуйте ещё раз. (" + we.Message + ")";
            }
            if (ex is IOException || ex is UnauthorizedAccessException)
                return "Не удалось записать библиотеку. Если EasyLib.SchLib/PcbLib открыта в Altium вкладкой, закройте её и повторите. (" + ex.Message + ")";
            return ex.Message;
        }

        async Task RepairOnStart()
        {
            try
            {
                string sch = SchPath, pcb = PcbPath;
                int n = await Task.Run(() => AltiumLib.RepairSchLib(sch) + AltiumLib.RepairPcbLib(pcb));
                if (n > 0)
                    SetStatus("Библиотеки EasyLib исправлены (" + n + " шт.). В Altium: панель Components → ≡ → Refresh; детали на схемах — Tools → Update From Libraries.", 1);
            }
            catch { }
        }

        // ------------------------------------------------------------ search

        async void DoSearch(int page)
        {
            string q = search.Box.Text.Trim();
            if (q == "" || searching) return;
            curQuery = q;
            curPage = page;
            string source = SourceKeys[sourceSeg.Selected];
            searching = true;
            UpdateButtons();
            list.SetItems(null);
            list.Message = "Поиск…";
            SetStatus("Поиск «" + q + "»…", 0);
            try
            {
                Action<string> progress = delegate(string m)
                {
                    try { BeginInvoke((Action)delegate { list.Message = m; SetStatus(m, 0); }); } catch { }
                };
                bool exact = tExact.Checked;
                SearchResult r = await Task.Run(() => SearchAll(q, page, source, exact, progress));
                totalPages = Math.Max(1, r.TotalPages);
                list.SetItems(r.Items);
                list.Message = exact
                    ? "Точных совпадений с «" + q + "» нет. Выключите «Точно», проверьте написание" + (source != "all" ? ", переключитесь на «Все базы»" : "") +
                      " или поищите на SnapEDA, Ultra Librarian, CSE (кнопки ниже)."
                    : "Ничего не найдено. Попробуйте другой запрос" + (source != "all" ? " или переключитесь на «Все базы»." : ".") +
                      " Или поищите на SnapEDA, Ultra Librarian, CSE (кнопки ниже).";
                lblCount.Text = r.Items.Count == 0 ? "" : exact ? "Точные совпадения: " + r.Items.Count + " (первые страницы поиска)"
                              : "Страница " + curPage + " из " + totalPages + "  ·  " + r.Items.Count + " на странице";
                SetStatus(r.Items.Count == 0 ? "Ничего не найдено." : "Выберите деталь. Двойной клик или Enter добавляет её в библиотеку.", 0);
                if (r.Items.Count > 0) { list.Focus(); list.SelectedIndex = 0; }
            }
            catch (Exception ex)
            {
                list.Message = Friendly(ex);
                SetStatus(Friendly(ex), 2);
            }
            finally { searching = false; UpdateButtons(); }
        }

        // LCSC + KiCad + community, depending on the selected source
        internal static SearchResult SearchAll(string q, int page, string source, bool exact, Action<string> progress)
        {
            bool cnum = System.Text.RegularExpressions.Regex.IsMatch(q, "^[Cc]\\d+$");
            if (!exact || cnum)
            {
                SearchResult one = SearchPage(q, page, source, cnum, progress);
                one.Items = SearchRank.Apply(one.Items, q, false);
                return one;
            }
            // exact match: look through the first pages and keep only parts that contain the whole query
            const int maxPages = 3;
            SearchResult all = new SearchResult();
            int pages = 1;
            for (int p = 1; p <= pages && p <= maxPages; p++)
            {
                if (p > 1 && progress != null) progress("Ищу точные совпадения… страница " + p + " из " + Math.Min(pages, maxPages));
                SearchResult r = SearchPage(q, p, source, false, progress);
                pages = Math.Max(1, r.TotalPages);
                all.Items.AddRange(r.Items);
            }
            all.Items = SearchRank.Apply(all.Items, q, true);
            all.TotalPages = 1;
            return all;
        }

        static SearchResult SearchPage(string q, int page, string source, bool cnum, Action<string> progress)
        {
            if (source == "kicad")
            {
                KiCadLib.EnsureIndex(progress);
                return KiCadLib.Search(q, page);
            }
            SearchResult r = EasyEda.Search(q, page, source);
            if (source == "all" && KiCadLib.HasIndex && !cnum)
            {
                try
                {
                    KiCadLib.EnsureIndex(null);
                    SearchResult k = KiCadLib.Search(q, page);
                    int at = r.Items.FindIndex(delegate(SearchItem it) { return it.Source == "user"; });
                    if (at < 0) at = r.Items.Count;
                    r.Items.InsertRange(at, k.Items);
                    r.TotalPages = Math.Max(r.TotalPages, k.TotalPages);
                }
                catch { }
            }
            return r;
        }

        // ------------------------------------------------------------ details

        void ShowPending()
        {
            SearchItem it = list.SelectedItem;
            if (it == null || (curItem != null && it.Key == curItem.Key && curPart != null)) return;
            info.SetMessage("Загружаю " + (it.Mpn != "" ? it.Mpn : it.Title) + "…");
        }

        async Task LoadSelected()
        {
            SearchItem it = list.SelectedItem;
            if (it == null) return;
            if (curItem != null && it.Key == curItem.Key && curPart != null) return;
            int token = ++loadToken;
            try
            {
                Part p = await Task.Run(() =>
                {
                    if (it.Source == "kicad") return KiCadLib.Load(it);
                    Dictionary<string, object> res = EasyEda.Component(it);
                    if (res == null && it.Direct)
                        throw new Exception("Для " + it.Lcsc + " в EasyEDA нет символа и футпринта (или такого номера нет у LCSC). " +
                                            "Поищите деталь по партномеру на SnapEDA, Ultra Librarian или CSE — кнопки под списком.");
                    if (res == null) throw new Exception("для этой детали в EasyEDA нет символа и футпринта");
                    return Converter.Convert(res);
                });
                if (token != loadToken) return;
                ShowPart(p, it);
                string who = p.Source == "kicad" ? "KiCad, " + Info("Lib") : p.Official ? p.Lcsc : "Деталь сообщества";
                SetStatus(who + ": двойной клик, Enter или «Добавить в библиотеку».", 0);
            }
            catch (Exception ex)
            {
                if (token != loadToken) return;
                curPart = null;
                curItem = null;
                symBox.SetMessage("");
                fpBox.SetMessage("");
                dimView.SetMessage("");
                info.SetMessage(Friendly(ex));
                viewer.Message = "";
                SetStatus(Friendly(ex), 2);
            }
            UpdateButtons();
        }

        void ShowPart(Part p, SearchItem it)
        {
            curPart = p;
            curItem = it;
            // stock and price from the search list when the part data has none
            string v;
            if ((!p.Info.TryGetValue("Stock", out v) || string.IsNullOrEmpty(v)) && it.Stock != "") p.Info["Stock"] = it.Stock;
            if ((!p.Info.TryGetValue("Price", out v) || string.IsNullOrEmpty(v)) && it.Price != "") p.Info["Price"] = it.Price;
            if ((!p.Info.TryGetValue("MinQty", out v) || string.IsNullOrEmpty(v)) && it.MinQty != "") p.Info["MinQty"] = it.MinQty;
            if (it.Direct)
            {
                // the row was made from the number only: fill it in from the loaded part
                if (it.Mpn == "" || it.Mpn.StartsWith(it.Lcsc)) { it.Mpn = Info("MPN") != "" ? Info("MPN") : p.Title; it.Title = p.Title; }
                if (it.Manufacturer == "") it.Manufacturer = Info("Manufacturer");
                if (it.Package == "") it.Package = p.Footprint;
                if (it.Value == "") it.Value = Info("Value");
                if (it.Class == "") it.Class = Info("Class");
                list.Invalidate();
            }
            if (p.NoSymbol) symBox.SetMessage("Только футпринт"); else symBox.SetPart(p);
            if (p.Pads.Count > 0) fpBox.SetPart(p);
            else fpBox.SetMessage(p.Source == "kicad" ? "В библиотеке KiCad у этого символа нет футпринта" : "У этой детали нет футпринта");
            if (p.Pads.Count > 0) dimView.SetPart(p); else dimView.SetMessage("У этой детали нет футпринта");
            info.SetPart(p);
            UpdateShops(p);
            if (viewSeg.Selected == 1) Load3D();
            UpdateButtons();
        }

        // stock and price in the chosen shops, each asked in the background
        void UpdateShops(Part p)
        {
            if (p == null) return;
            string mpn = PartNumber(p);
            string lcsc = p.Official && p.Source == "easyeda" ? p.Lcsc : "";
            foreach (string shop in Shops.Enabled)
            {
                if (p.Shops.ContainsKey(shop)) continue;
                if (shop == "lcsc" ? lcsc == "" : mpn == "") continue;
                p.Shops[shop] = new ShopResult();
                AskShop(p, shop, mpn, lcsc);
            }
            if (curPart == p) { info.Invalidate(); UpdateButtons(); }
        }

        async void AskShop(Part p, string shop, string mpn, string lcsc)
        {
            ShopResult r;
            try { r = await Task.Run(() => Shops.Lookup(shop, mpn, lcsc)); }
            catch { r = new ShopResult { State = 3, Error = "нет связи" }; }
            p.Shops[shop] = r;
            if (curPart == p) info.Invalidate();
        }

        // part number worth asking a shop about: not a bare "R" or "C" of a generic symbol
        static string PartNumber(Part p)
        {
            string v;
            string mpn = p.Info.TryGetValue("MPN", out v) && !string.IsNullOrEmpty(v) ? v : p.Title;
            mpn = (mpn ?? "").Trim();
            if (mpn.Length < 4 || !System.Text.RegularExpressions.Regex.IsMatch(mpn, "\\d")) return "";
            return mpn;
        }

        void ShopsMenu()
        {
            ThemedMenu m = new ThemedMenu();
            foreach (string id in Shops.Ids)
            {
                string shop = id;
                ToolStripMenuItem it = m.AddItem("Показывать: " + Shops.Name(shop), Shops.Enabled.Contains(shop), null);
                it.Click += delegate
                {
                    if (!Shops.Enabled.Remove(shop)) Shops.Enabled.Add(shop);
                    Shops.Enabled.Sort(delegate(string a, string b) { return Array.IndexOf(Shops.Ids, a).CompareTo(Array.IndexOf(Shops.Ids, b)); });
                    settings.Shops = string.Join(",", Shops.Enabled.ToArray());
                    settings.Save();
                    if (curPart != null) UpdateShops(curPart);
                    info.Invalidate();
                };
            }
            if (curPart != null)
            {
                m.Items.Add(new ToolStripSeparator());
                string mpn = PartNumber(curPart);
                foreach (string id in Shops.Ids)
                {
                    string shop = id;
                    ShopResult sr;
                    string url = curPart.Shops.TryGetValue(shop, out sr) && sr.State == 1 && sr.Offer.Url != "" ? sr.Offer.Url
                               : shop == "lcsc" ? (curPart.Official && curPart.Lcsc != "" ? "https://www.lcsc.com/product-detail/" + curPart.Lcsc + ".html" : (mpn != "" ? Shops.SearchUrl(shop, mpn) : ""))
                               : mpn != "" ? Shops.SearchUrl(shop, mpn) : "";
                    if (url == "") continue;
                    m.AddItem("Открыть в " + Shops.Name(shop), false, delegate { OpenUrl(url); });
                }
            }
            m.Closed += delegate { BeginInvoke((Action)delegate { m.Dispose(); }); };
            m.Show(btnShops, new Point(0, -m.GetPreferredSize(Size.Empty).Height - Ui.D(2)));
        }

        // ------------------------------------------------------------ 3D

        void ShowView()
        {
            bool is3D = viewSeg.Selected == 1, isDim = viewSeg.Selected == 2;
            viewer.Visible = is3D;
            dimView.Visible = isDim;
            twoPane.Visible = !is3D && !isDim;
            // the dimension view needs room: give the preview more of the column
            float top = isDim ? 66 : 58;
            if (Math.Abs(rightCol.RowStyles[0].Height - top) > 0.1f)
            {
                rightCol.RowStyles[0].Height = top;
                rightCol.RowStyles[1].Height = 100 - top;
            }
            LayoutPreviewHead();
            if (is3D) Load3D();
        }

        async void Load3D()
        {
            Part p = curPart;
            if (p == null) { viewer.Message = "Выберите деталь в списке"; return; }
            if (p.Source != "easyeda")
            {
                viewer.Message = p.KiModel != null || p.StepData != null
                    ? "3D-просмотр работает только для деталей LCSC и EasyEDA.\nSTEP-модель этой детали будет встроена в футпринт при добавлении."
                    : "У этой детали нет 3D-модели";
                return;
            }
            if (p.ModelUuid == "") { viewer.Message = "У этой детали нет 3D-модели"; return; }
            if (objUuid == p.ModelUuid && objText != null) { Build3D(); return; }
            int token = ++objToken;
            viewer.Message = "Загружаю 3D-модель…";
            try
            {
                string uuid = p.ModelUuid;
                string obj = await Task.Run(() => EasyEda.Obj(uuid));
                if (token != objToken || curPart != p) return;
                if (obj == null) { viewer.Message = "3D-модель не найдена на сервере EasyEDA"; return; }
                objUuid = uuid;
                objText = obj;
                Build3D();
            }
            catch (Exception ex)
            {
                if (token == objToken) viewer.Message = Friendly(ex);
            }
        }

        void Build3D()
        {
            if (curPart == null || objText == null || objUuid != curPart.ModelUuid) return;
            try { viewer.Scene = Obj3D.Build(curPart, objText, tPads.Checked); }
            catch (Exception ex) { viewer.Message = "Не удалось показать модель: " + ex.Message; }
        }

        void OpenDimWindow()
        {
            if (curPart == null || curPart.Pads.Count == 0) return;
            DimForm f = new DimForm(curPart, dimView.Mm, delegate(bool mm) { settings.DimUnit = mm ? "mm" : "mil"; settings.Save(); });
            f.TopMost = TopMost;
            f.Show(this);
        }

        void Open3DWindow()
        {
            if (curPart == null || objText == null || objUuid != curPart.ModelUuid) return;
            Model3DForm f = new Model3DForm(curPart, objText);
            f.TopMost = TopMost;
            f.Show(this);
        }

        // ------------------------------------------------------------ add

        async void AddCurrent()
        {
            if (adding) return;
            SearchItem sel = list.SelectedItem;
            if (sel == null) return;
            if (curPart == null || curItem == null || sel.Key != curItem.Key)
            {
                selTimer.Stop();
                await LoadSelected();
                if (curPart == null || curItem == null || sel.Key != curItem.Key) return;
            }
            Part p = curPart;
            string schPath = SchPath, pcbPath = PcbPath;
            bool firstTime = !File.Exists(schPath);
            bool with3D = t3D.Checked && (p.ModelUuid != "" || p.KiModel != null || p.StepData != null);
            adding = true;
            UpdateButtons();
            SetStatus(with3D ? "Скачиваю 3D-модель и записываю в библиотеку…" : "Записываю в библиотеку…", 0);
            try
            {
                string note = "";
                bool w3 = t3D.Checked;
                LibResult r = await Task.Run(() => PartAdder.Add(p, schPath, pcbPath, w3, out note));
                string fp = r.NoFootprint ? ", без футпринта" :
                            ", футпринт «" + r.Footprint + "»" + (r.FootprintExisted ? " уже был" : r.ModelAdded ? " с 3D" : "");
                string msg = (r.SymbolExisted ? "«" + r.LibRef + "» уже есть в библиотеке" : "Добавлено: «" + r.LibRef + "»") + fp + note +
                             ". " + RefreshHint;
                if (r.Repaired > 0) msg += " Заодно исправлены старые записи в библиотеке: " + r.Repaired + ".";
                long pcbSize = 0;
                try { pcbSize = File.Exists(pcbPath) ? new FileInfo(pcbPath).Length : 0; } catch { }
                if (pcbSize > 150L * 1024 * 1024)
                    msg += " PcbLib уже " + LibMaint.Size(pcbSize) + ": если Altium стал медленнее, лишние 3D-модели убираются кнопкой «Очистка».";
                SetStatus(msg, 1);
                if (firstTime) ShowHelp();
            }
            catch (Exception ex)
            {
                SetStatus(Friendly(ex), 2);
                MessageBox.Show(this, Friendly(ex), "EasyLib", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { adding = false; UpdateButtons(); }
        }

        const string RefreshHint = "В Altium обновите панель: Components → ≡ → Refresh.";

        // ------------------------------------------------------------ other databases and import

        void OpenVendor(string vendor)
        {
            string q = search.Box.Text.Trim();
            // the vendors do not know LCSC numbers: search by the part number
            bool cnum = System.Text.RegularExpressions.Regex.IsMatch(q, "^[Cc]\\d+$");
            if (q == "" || cnum)
            {
                SearchItem sel = list.SelectedItem;
                if (curPart != null && Info("MPN") != "") q = Info("MPN");
                else if (sel != null && sel.Mpn != "" && !sel.Mpn.StartsWith(sel.Lcsc)) q = sel.Mpn;
            }
            string e = Uri.EscapeDataString(q);
            string url;
            if (vendor == "SnapEDA") url = q == "" ? "https://www.snapeda.com/" : "https://www.snapeda.com/search/?q=" + e + "&SearchType=parts";
            else if (vendor == "Ultra Librarian") url = q == "" ? "https://app.ultralibrarian.com/" : "https://app.ultralibrarian.com/search?queryText=" + e;
            else url = q == "" ? "https://componentsearchengine.com/" : "https://componentsearchengine.com/search?term=" + e;
            OpenUrl(url);
            SetStatus("На сайте " + vendor + " скачайте модель в формате KiCad. " +
                      (tWatch.Checked ? "EasyLib сам заберёт zip из «Загрузок»." : "Затем перетащите zip в окно EasyLib или нажмите «Импорт»."), 0);
        }

        void ImportDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Импорт деталей в формате KiCad или Altium (SnapEDA, Ultra Librarian, CSE)";
                d.Filter = "Детали KiCad и Altium (*.zip;*.IntLib;*.SchLib;*.PcbLib;*.kicad_sym;*.lib;*.kicad_mod)|*.zip;*.intlib;*.schlib;*.pcblib;*.kicad_sym;*.lib;*.kicad_mod;*.dcm;*.step;*.stp|Все файлы|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) == DialogResult.OK) ImportFiles(d.FileNames, false);
            }
        }

        async void ImportFiles(string[] paths, bool fromDownloads)
        {
            if (importing || adding) { importQueue.Enqueue(paths); return; }
            importing = true;
            UpdateButtons();
            string what = paths.Length == 1 ? Path.GetFileName(paths[0]) : paths.Length + " файл(ов)";
            SetStatus("Импортирую " + what + "…", 0);
            try
            {
                string schPath = SchPath, pcbPath = PcbPath;
                bool firstTime = !File.Exists(schPath);
                bool w3 = t3D.Checked;
                List<string> done = new List<string>();
                ImportResult res = await Task.Run(() =>
                {
                    ImportResult ir = KiImport.FromPaths(paths);
                    // native Altium libraries first: if a download has both formats, the Altium one wins
                    AltiumImport.Apply(ir.Altium, schPath, pcbPath, done);
                    foreach (Part part in ir.Parts)
                    {
                        string note;
                        LibResult lr = PartAdder.Add(part, schPath, pcbPath, w3, out note);
                        string name = part.NoSymbol ? "футпринт «" + lr.Footprint + "»" : "«" + lr.LibRef + "»";
                        if (lr.SymbolExisted || (part.NoSymbol && lr.FootprintExisted)) name += " (уже был)";
                        else if (lr.ModelAdded) name += " с 3D";
                        done.Add(name);
                    }
                    return ir;
                });
                if (res.Parts.Count == 0 && done.Count > 0)
                {
                    // ready-made Altium library: copied without conversion, nothing to preview
                    string src = res.Vendor != "" ? " из " + res.Vendor : "";
                    string list3 = string.Join(", ", done.GetRange(0, Math.Min(3, done.Count)).ToArray()) + (done.Count > 3 ? " и ещё " + (done.Count - 3) : "");
                    SetStatus("Импортировано из библиотеки Altium" + src + " (как есть, без преобразования): " + list3 + ". " + RefreshHint, 1);
                    if (fromDownloads) FlashTaskbar();
                    if (firstTime) ShowHelp();
                }
                else if (res.Parts.Count == 0)
                {
                    string why = res.Notes.Count > 0 ? res.Notes[0] : "в файле нет деталей в формате KiCad или Altium";
                    if (!fromDownloads) SetStatus("Импорт " + what + ": " + why, 2);
                }
                else
                {
                    ShowPart(res.Parts[0], null);
                    string src = res.Vendor != "" ? " из " + res.Vendor : "";
                    string list3 = string.Join(", ", done.GetRange(0, Math.Min(3, done.Count)).ToArray()) + (done.Count > 3 ? " и ещё " + (done.Count - 3) : "");
                    SetStatus("Импортировано" + src + ": " + list3 + ". " + RefreshHint, 1);
                    if (fromDownloads) FlashTaskbar();
                    if (firstTime) ShowHelp();
                }
            }
            catch (Exception ex) { SetStatus("Импорт " + what + ": " + Friendly(ex), 2); }
            finally
            {
                importing = false;
                UpdateButtons();
                if (importQueue.Count > 0) ImportFiles(importQueue.Dequeue(), true);
            }
        }

        void FlashTaskbar()
        {
            try { if (!ContainsFocus) Native.Flash(this); } catch { }
        }

        void UpdateWatcher()
        {
            try
            {
                if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
                if (!tWatch.Checked) return;
                string dir = Native.DownloadsFolder();
                if (!Directory.Exists(dir)) return;
                watcher = new FileSystemWatcher(dir, "*.*");
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.SynchronizingObject = this;
                FileSystemEventHandler onFile = delegate(object s, FileSystemEventArgs e) { DownloadSeen(e.FullPath); };
                watcher.Created += onFile;
                watcher.Changed += onFile;
                watcher.Renamed += delegate(object s, RenamedEventArgs e) { DownloadSeen(e.FullPath); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { SetStatus("Не удалось следить за «Загрузками»: " + ex.Message, 2); }
        }

        async void DownloadSeen(string path)
        {
            bool isZip = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (!isZip && !path.EndsWith(".intlib", StringComparison.OrdinalIgnoreCase)) return;
            lock (seenDownloads) { if (!seenDownloads.Add(path)) return; }
            // wait until the browser has finished writing the file
            bool ready = await Task.Run(() =>
            {
                long last = -1;
                for (int i = 0; i < 60; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    try
                    {
                        long len = new FileInfo(path).Length;
                        if (len > 0 && len == last)
                            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true;
                        last = len;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return false;
            });
            if (!ready || !File.Exists(path)) { lock (seenDownloads) seenDownloads.Remove(path); return; }
            bool kicad = !isZip || await Task.Run(() => KiImport.LooksLikeKiCad(path));
            if (!kicad) return;
            ImportFiles(new string[] { path }, true);
        }

        // ------------------------------------------------------------ help

        void ShowHelp()
        {
            HelpForm h = new HelpForm(SchPath, PcbPath);
            h.TopMost = TopMost;
            h.ShowDialog(this);
        }
    }

    class Model3DForm : Form
    {
        Viewer3D view;

        public Model3DForm(Part p, string obj)
        {
            Text = "3D — " + p.Title + (p.Footprint != "" ? " (" + p.Footprint + ")" : "");
            Font = Ui.Font(9f);
            Ui.ApplyIcon(this);
            BackColor = Theme.Current.Bg;
            ClientSize = new Size(Ui.D(820), Ui.D(640));
            StartPosition = FormStartPosition.CenterParent;
            view = new Viewer3D { Dock = DockStyle.Fill };
            BgPanel bar = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(48) };
            Toggle pads = new Toggle("Площадки").AutoWidth();
            pads.Checked = true;
            pads.CheckedChanged += delegate { view.Scene = Obj3D.Build(p, obj, pads.Checked); };
            FlatButton top = new FlatButton("Сверху", ButtonKind.Secondary, null).AutoWidth();
            top.Click += delegate { view.SetView(0, 0); };
            FlatButton iso = new FlatButton("Изометрия", ButtonKind.Secondary, null).AutoWidth();
            iso.Click += delegate { view.ResetView(); };
            FlatButton side = new FlatButton("Сбоку", ButtonKind.Secondary, null).AutoWidth();
            side.Click += delegate { view.SetView(0, -90); };
            TextLine hint = new TextLine("ЛКМ — вращать, ПКМ — сдвигать, колесо — масштаб, двойной клик — исходный вид") { Dim = true };
            bar.Controls.AddRange(new Control[] { pads, top, iso, side, hint });
            bar.Resize += delegate
            {
                int x = Ui.D(14), y = (bar.Height - Ui.D(32)) / 2;
                pads.Location = new Point(x, (bar.Height - pads.Height) / 2); x = pads.Right + Ui.D(12);
                foreach (Control c in new Control[] { top, iso, side }) { c.Location = new Point(x, y); x = c.Right + Ui.D(6); }
                hint.SetBounds(x + Ui.D(10), 0, Math.Max(10, bar.Width - x - Ui.D(24)), bar.Height);
            };
            Controls.Add(view);
            Controls.Add(bar);
            Shown += delegate { view.Scene = Obj3D.Build(p, obj, true); };
            Theme.Changed += OnTheme;
            FormClosed += delegate { Theme.Changed -= OnTheme; };
        }

        void OnTheme(object s, EventArgs e) { BackColor = Theme.Current.Bg; Native.StyleTitleBar(this, Theme.Current); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(this, Theme.Current);
        }
    }

    // multi-line themed text
    class TextBlock : ThemedControl
    {
        public float FontSize = 9.5f;
        public bool Dim;

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(ParentBack);
            using (Font f = Ui.Font(FontSize))
                Ui.Text(e.Graphics, Text, f, Dim ? t.TextDim : t.Text, ClientRectangle, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }
    }

    class HelpForm : Form
    {
        public HelpForm(string sch, string pcb)
        {
            Text = "Как подключить библиотеку к Altium";
            Font = Ui.Font(9.5f);
            Ui.ApplyIcon(this);
            BackColor = Theme.Current.Bg;
            ClientSize = new Size(Ui.D(780), Ui.D(600));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Card card = new Card { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(22), Ui.D(18), Ui.D(22), Ui.D(14)) };
            BgPanel outer = new BgPanel { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(16), Ui.D(16), Ui.D(16), 0) };
            outer.Controls.Add(card);

            TextLine h1 = new TextLine("Подключить один раз") { FontSize = 12f, Style = FontStyle.Bold, Dock = DockStyle.Top, Height = Ui.D(30) };
            TextBlock steps = new TextBlock
            {
                Dock = DockStyle.Top, Height = Ui.D(196),
                Text =
                    "1.  В Altium откройте панель Components: кнопка Panels в правом нижнем углу → Components.\n" +
                    "2.  В панели нажмите ≡ (три полоски справа вверху) → File-based Libraries Preferences… (не Search!).\n" +
                    "3.  Вкладка Installed → кнопка Install… внизу → Install from file…\n" +
                    "4.  Выделите оба файла (Ctrl+клик). Если их не видно, внизу справа выберите тип «All Files»:\n" +
                    "        " + sch + "\n" +
                    "        " + pcb + "\n" +
                    "5.  Open → Close. В списке библиотек панели Components появится EasyLib.SchLib."
            };
            TextLine h2 = new TextLine("Дальше") { FontSize = 12f, Style = FontStyle.Bold, Dock = DockStyle.Top, Height = Ui.D(30) };
            TextBlock more = new TextBlock
            {
                Dock = DockStyle.Fill,
                Text =
                    "•  Добавляйте детали в EasyLib, а в Altium перетаскивайте их на схему из панели Components → EasyLib.SchLib. " +
                    "Если новой детали не видно, нажмите ≡ → Refresh.\n" +
                    "•  Футпринт и 3D-модель подтягиваются автоматически из EasyLib.PcbLib.\n" +
                    "•  Не держите EasyLib.SchLib / EasyLib.PcbLib открытыми вкладкой в Altium, пока добавляете детали: " +
                    "при сохранении Altium перезапишет файл своей версией.\n" +
                    "•  Детали из базы «Сообщество» никто не проверял. Перед заказом платы сверяйте футпринт с даташитом.\n" +
                    "•  Другие базы: кнопки SnapEDA / Ultra Librarian / CSE под списком открывают поиск на сайте. Скачайте деталь " +
                    "в формате Altium (.IntLib) или KiCad, и EasyLib сам заберёт файл из «Загрузок» (или перетащите его в окно, или нажмите «Импорт»).\n" +
                    "•  Вкладка «Размеры» проверяет футпринт, кнопка «Очистка» внизу показывает состав библиотеки и удаляет лишнее."
            };
            card.Controls.Add(more);
            card.Controls.Add(h2);
            card.Controls.Add(steps);
            card.Controls.Add(h1);

            BgPanel bar = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(60) };
            FlatButton ok = new FlatButton("Понятно", ButtonKind.Primary, null) { Height = Ui.D(36), DialogResult = DialogResult.OK };
            ok.Width = Math.Max(Ui.D(110), ok.PreferredWidth);
            FlatButton copy = new FlatButton("Скопировать путь к папке", ButtonKind.Secondary, "") { Height = Ui.D(36) };
            copy.AutoWidth();
            copy.Click += delegate { try { Clipboard.SetText(Path.GetDirectoryName(sch)); copy.Text = "Скопировано"; } catch { } };
            bar.Controls.Add(ok);
            bar.Controls.Add(copy);
            bar.Resize += delegate
            {
                int y = (bar.Height - ok.Height) / 2;
                ok.Location = new Point(bar.Width - ok.Width - Ui.D(16), y);
                copy.Location = new Point(ok.Left - copy.Width - Ui.D(8), y);
            };
            Controls.Add(outer);
            Controls.Add(bar);
            AcceptButton = ok;
            CancelButton = ok;
            Shown += delegate { ok.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(this, Theme.Current);
        }
    }

    class Settings
    {
        public string LibDir = "";
        public string Theme = "dark", Source = "all";
        public bool With3D = true, TopMost, WatchDownloads = true, Exact;
        public string DimUnit = "mm";
        public string Shops = "lcsc,chipdip,promelec";
        public int X, Y, Width, Height;

        public static string DefaultLibDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyLib"); }
        }

        static string FilePath
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyLib");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    int n;
                    switch (k)
                    {
                        case "LibDir": s.LibDir = v.Trim(); break;
                        case "Theme": s.Theme = v.Trim() == "light" ? "light" : "dark"; break;
                        case "Source": if (Array.IndexOf(new string[] { "all", "lcsc", "kicad", "user" }, v.Trim()) >= 0) s.Source = v.Trim(); break;
                        case "WatchDownloads": s.WatchDownloads = v != "0"; break;
                        case "With3D": s.With3D = v == "1"; break;
                        case "TopMost": s.TopMost = v == "1"; break;
                        case "Exact": s.Exact = v == "1"; break;
                        case "Shops": s.Shops = v.Trim(); break;
                        case "DimUnit": s.DimUnit = v.Trim() == "mil" ? "mil" : "mm"; break;
                        case "X": if (int.TryParse(v, out n)) s.X = n; break;
                        case "Y": if (int.TryParse(v, out n)) s.Y = n; break;
                        case "W": if (int.TryParse(v, out n)) s.Width = n; break;
                        case "H": if (int.TryParse(v, out n)) s.Height = n; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new string[] {
                    "LibDir=" + LibDir, "Theme=" + Theme, "Source=" + Source,
                    "With3D=" + (With3D ? "1" : "0"), "TopMost=" + (TopMost ? "1" : "0"), "WatchDownloads=" + (WatchDownloads ? "1" : "0"),
                    "Exact=" + (Exact ? "1" : "0"), "DimUnit=" + DimUnit, "Shops=" + Shops,
                    "X=" + X, "Y=" + Y, "W=" + Width, "H=" + Height });
            }
            catch { }
        }
    }
}
