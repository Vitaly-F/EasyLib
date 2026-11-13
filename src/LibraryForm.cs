// "Очистка" window: what the library holds, what takes the space, deleting parts and 3D models.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyLib
{
    class LibraryForm : Form
    {
        readonly string dir;
        readonly ResultList list;
        readonly TextBlock summary;
        readonly TextLine status;
        readonly FlatButton btnDelete, btnStrip, btnPrune, btnRestore, btnAll, btnNone;
        readonly Dictionary<SearchItem, LibPart> rows = new Dictionary<SearchItem, LibPart>();
        LibReport report;
        DateTime stamp;
        bool busy;
        string lastMessage = "";

        public event EventHandler LibraryChanged;

        public LibraryForm(string libDir)
        {
            dir = libDir;
            Text = "Библиотека EasyLib — состав и очистка";
            Font = Ui.Font(9f);
            Ui.ApplyIcon(this);
            BackColor = Theme.Current.Bg;
            ClientSize = new Size(Ui.D(980), Ui.D(660));
            MinimumSize = new Size(Ui.D(760), Ui.D(480));
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            BgPanel top = new BgPanel { Dock = DockStyle.Top, Height = Ui.D(96), Padding = new Padding(Ui.D(18), Ui.D(14), Ui.D(18), 0) };
            summary = new TextBlock { Dock = DockStyle.Fill, FontSize = 9.5f };
            top.Controls.Add(summary);

            BgPanel centre = new BgPanel { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(16), 0, Ui.D(16), 0) };
            Card card = new Card { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(6)) };
            list = new ResultList { Dock = DockStyle.Fill, CheckBoxes = true };
            list.Columns.Add(new ListColumn("Символ", 2.2f, 120, delegate(SearchItem it) { return it.Mpn; }));
            list.Columns.Add(new ListColumn("Футпринт", 2.2f, 120, delegate(SearchItem it) { return it.Package; }));
            list.Columns.Add(new ListColumn("3D", 0.8f, 60, delegate(SearchItem it) { return it.Value; }) { Dim = true });
            list.Columns.Add(new ListColumn("Размер", 0.8f, 64, delegate(SearchItem it) { return it.Manufacturer; }));
            list.Columns.Add(new ListColumn("Примечание", 1.5f, 90, delegate(SearchItem it) { return it.Author; }) { Dim = true });
            list.Message = "Читаю библиотеку…";
            list.CheckedChanged += delegate { UpdateSelection(); };
            card.Controls.Add(list);
            centre.Controls.Add(card);

            BgPanel bottom = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(96) };
            status = new TextLine("") { Dim = true };
            btnAll = new FlatButton("Выбрать всё", ButtonKind.Ghost, null) { Height = Ui.D(32) }.AutoWidth();
            btnAll.Click += delegate { list.CheckAll(true); };
            btnNone = new FlatButton("Снять выбор", ButtonKind.Ghost, null) { Height = Ui.D(32) }.AutoWidth();
            btnNone.Click += delegate { list.CheckAll(false); };
            btnStrip = new FlatButton("Убрать 3D у выбранных", ButtonKind.Secondary, null) { Height = Ui.D(36) }.AutoWidth();
            btnStrip.Click += delegate { Strip(); };
            btnDelete = new FlatButton("Удалить выбранные", ButtonKind.Primary, null) { Height = Ui.D(36) }.AutoWidth();
            btnDelete.Click += delegate { Delete(); };
            btnPrune = new FlatButton("Удалить лишние 3D-модели", ButtonKind.Secondary, null) { Height = Ui.D(36) }.AutoWidth();
            btnPrune.Click += delegate { Prune(); };
            btnRestore = new FlatButton("Восстановить копию…", ButtonKind.Ghost, null) { Height = Ui.D(36) }.AutoWidth();
            btnRestore.Click += delegate { RestoreMenu(); };
            FlatButton btnClose = new FlatButton("Закрыть", ButtonKind.Ghost, null) { Height = Ui.D(36) }.AutoWidth();
            btnClose.Click += delegate { Close(); };
            bottom.Controls.AddRange(new Control[] { status, btnAll, btnNone, btnStrip, btnDelete, btnPrune, btnRestore, btnClose });
            bottom.Resize += delegate
            {
                int m = Ui.D(16), y1 = Ui.D(8), y2 = Ui.D(48);
                btnAll.Location = new Point(m, y1);
                btnNone.Location = new Point(btnAll.Right + Ui.D(2), y1);
                status.SetBounds(btnNone.Right + Ui.D(12), y1, Math.Max(10, bottom.Width - btnNone.Right - Ui.D(28)), Ui.D(32));
                btnDelete.Location = new Point(m, y2);
                btnStrip.Location = new Point(btnDelete.Right + Ui.D(8), y2);
                btnPrune.Location = new Point(btnStrip.Right + Ui.D(8), y2);
                btnClose.Location = new Point(bottom.Width - m - btnClose.Width, y2);
                btnRestore.Location = new Point(btnClose.Left - Ui.D(4) - btnRestore.Width, y2);
            };

            Controls.Add(centre);
            Controls.Add(bottom);
            Controls.Add(top);
            Shown += delegate { Reload(); };
            Activated += delegate { if (!busy && report != null && Stamp() != stamp) Reload(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Theme.Changed += OnTheme;
            FormClosed += delegate { Theme.Changed -= OnTheme; };
            UpdateSelection();
        }

        void OnTheme(object s, EventArgs e) { BackColor = Theme.Current.Bg; Native.StyleTitleBar(this, Theme.Current); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(this, Theme.Current);
        }

        DateTime Stamp()
        {
            DateTime a = File.Exists(LibMaint.SchPath(dir)) ? File.GetLastWriteTimeUtc(LibMaint.SchPath(dir)) : DateTime.MinValue;
            DateTime b = File.Exists(LibMaint.PcbPath(dir)) ? File.GetLastWriteTimeUtc(LibMaint.PcbPath(dir)) : DateTime.MinValue;
            return a > b ? a : b;
        }

        async void Reload()
        {
            busy = true;
            SetButtons();
            try
            {
                string d = dir;
                stamp = Stamp();
                LibReport r = await Task.Run(() => LibMaint.Analyze(d));
                ShowReport(r);
            }
            catch (Exception ex) { summary.Text = "Не удалось прочитать библиотеку: " + ex.Message; }
            busy = false;
            SetButtons();
        }

        void ShowReport(LibReport r)
        {
            report = r;
            rows.Clear();
            List<SearchItem> items = new List<SearchItem>();
            foreach (LibPart lp in r.Parts)
            {
                SearchItem it = new SearchItem();
                it.Uuid = "lib:" + items.Count;
                it.Mpn = lp.Symbol != "" ? lp.Symbol : "— только футпринт —";
                it.Package = lp.Footprint != "" ? lp.Footprint : "—";
                it.Value = lp.Models > 0 ? LibMaint.Size(lp.ModelBytes) : "нет";
                it.Manufacturer = LibMaint.Size(lp.Total);
                it.Author = lp.FootprintMissing ? "футпринта нет в PcbLib" : lp.SharedFootprint > 0 ? "футпринт общий ещё с " + lp.SharedFootprint
                          : lp.Symbol == "" ? "нет символа" : lp.SharedModel ? "3D общая с другим футпринтом" : "";
                rows[it] = lp;
                items.Add(it);
            }
            list.SetItems(items);
            list.Message = "Библиотека пуста. Добавьте детали в главном окне.";
            string text;
            if (!File.Exists(LibMaint.SchPath(dir)) && !File.Exists(LibMaint.PcbPath(dir)))
                text = "В папке " + dir + " ещё нет библиотек EasyLib.";
            else
            {
                text = "EasyLib.SchLib — " + LibMaint.Size(r.SchFileBytes) + ", символов: " + r.Symbols + ".   EasyLib.PcbLib — " + LibMaint.Size(r.PcbFileBytes) +
                       ", футпринтов: " + r.Footprints + ", 3D-моделей: " + r.ModelCount + " (" + LibMaint.Size(r.ModelBytes) + " в сжатом виде).\n" +
                       "Altium читает библиотеку целиком, поэтому большой PcbLib (сотни мегабайт) открывается и обновляется заметно дольше. " +
                       "Больше всего места занимают встроенные 3D-модели: их можно убрать, оставив символ и футпринт.";
                if (r.UnusedModels > 0) text += "\nВ библиотеке есть лишние 3D-модели (ненужные или повторные копии): " + r.UnusedModels + " шт., " + LibMaint.Size(r.UnusedModelBytes) + ". Их можно удалить без потерь.";
                if (r.Error != "") text = r.Error + "\n" + text;
            }
            summary.Text = text;
            UpdateSelection();
        }

        void UpdateSelection()
        {
            int n = list.Checked.Count;
            long bytes = 0, model = 0;
            int with3D = 0;
            foreach (SearchItem it in list.Checked)
            {
                LibPart lp;
                if (!rows.TryGetValue(it, out lp)) continue;
                bytes += lp.Total;
                model += lp.ModelBytes;
                if (lp.Models > 0) with3D++;
            }
            status.Text = n == 0 ? (lastMessage != "" ? lastMessage : "Отметьте детали щелчком (или пробелом). Список отсортирован по размеру.")
                                 : "Выбрано: " + n + ", это до " + LibMaint.Size(bytes) + " (из них 3D — " + LibMaint.Size(model) + "). " +
                                   "Футпринт, которым пользуется другой символ, при удалении останется.";
            status.Invalidate();
            SetButtons();
        }

        void SetButtons()
        {
            int n = list.Checked.Count;
            bool any3D = false;
            foreach (SearchItem it in list.Checked) { LibPart lp; if (rows.TryGetValue(it, out lp) && lp.Models > 0) any3D = true; }
            btnDelete.Enabled = !busy && n > 0;
            btnStrip.Enabled = !busy && any3D;
            btnPrune.Enabled = !busy && report != null && report.UnusedModels > 0;
            btnRestore.Enabled = !busy && LibMaint.Backups(dir).Count > 0;
            btnAll.Enabled = btnNone.Enabled = !busy;
            foreach (Control c in new Control[] { btnDelete, btnStrip, btnPrune, btnRestore, btnAll, btnNone }) c.Invalidate();
        }

        List<LibPart> Selected()
        {
            List<LibPart> r = new List<LibPart>();
            foreach (SearchItem it in list.Items) if (list.Checked.Contains(it)) { LibPart lp; if (rows.TryGetValue(it, out lp)) r.Add(lp); }
            return r;
        }

        bool Ask(string text)
        {
            return MessageBox.Show(this, text, "EasyLib", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        void Delete()
        {
            List<LibPart> sel = Selected();
            if (sel.Count == 0) return;
            List<string> syms = new List<string>(), fps = new List<string>();
            foreach (LibPart lp in sel)
            {
                if (lp.Symbol != "") syms.Add(lp.Symbol);
                else if (lp.Footprint != "") fps.Add(lp.Footprint);
            }
            if (!Ask("Удалить из библиотеки " + sel.Count + " дет.?\n\n" +
                     "Удаляются символы, их футпринты (если ими не пользуется другой символ) и 3D-модели, которые больше никому не нужны.\n" +
                     "Детали, которые уже стоят на схемах и платах, там останутся, но обновить их из библиотеки будет нельзя.\n\n" +
                     "Перед изменением EasyLib сохранит копию библиотек в папку Backups."))
                return;
            Run(delegate { return LibMaint.Delete(dir, syms, fps); });
        }

        void Strip()
        {
            List<string> fps = new List<string>();
            foreach (LibPart lp in Selected()) if (lp.Models > 0 && lp.Footprint != "" && !fps.Contains(lp.Footprint)) fps.Add(lp.Footprint);
            if (fps.Count == 0) return;
            if (!Ask("Убрать встроенные 3D-модели у " + fps.Count + " футпринт(ов)?\n\nСимволы и футпринты останутся. Модель потом можно вернуть, " +
                     "если удалить деталь и добавить её снова с включённым «3D-модель (STEP)».\n\nПеред изменением EasyLib сохранит копию библиотек в папку Backups."))
                return;
            Run(delegate { return LibMaint.Strip3D(dir, fps); });
        }

        void Prune()
        {
            if (!Ask("Удалить 3D-модели, которыми не пользуется ни один футпринт, и повторные копии моделей?\n\nПеред изменением EasyLib сохранит копию библиотек в папку Backups.")) return;
            Run(delegate { return LibMaint.PruneModels(dir); });
        }

        async void Run(Func<LibMaint.Result> action)
        {
            busy = true;
            SetButtons();
            status.Text = "Записываю библиотеку…";
            status.Invalidate();
            try
            {
                LibMaint.Result r = await Task.Run(action);
                List<string> what = new List<string>();
                if (r.Symbols > 0) what.Add("символов: " + r.Symbols);
                if (r.Footprints > 0) what.Add("футпринтов: " + r.Footprints);
                if (r.Stripped > 0) what.Add("3D убрано у футпринтов: " + r.Stripped);
                if (r.Models > 0) what.Add("3D-моделей удалено: " + r.Models);
                busy = false;
                lastMessage = (what.Count > 0 ? "Готово: " + string.Join(", ", what.ToArray()) + ". " : "Изменений нет. ") +
                              LibMaint.Size(r.BytesBefore) + " → " + LibMaint.Size(r.BytesAfter) + ". В Altium: Components → ≡ → Refresh.";
                Reload();
                if (LibraryChanged != null) LibraryChanged(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                busy = false;
                SetButtons();
                MessageBox.Show(this, "Не получилось: " + ex.Message + "\n\nБиблиотека не изменена или восстановима из папки Backups.", "EasyLib", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void RestoreMenu()
        {
            List<string> backups = LibMaint.Backups(dir);
            if (backups.Count == 0) return;
            ContextMenuStrip m = new ContextMenuStrip();
            foreach (string b in backups)
            {
                string folder = b;
                long size = 0;
                foreach (string f in Directory.GetFiles(folder)) size += new FileInfo(f).Length;
                string name = Path.GetFileName(folder).Replace('_', ' ');
                ToolStripItem item = m.Items.Add("Копия от " + name + "  (" + LibMaint.Size(size) + ")");
                item.Click += delegate
                {
                    if (!Ask("Вернуть библиотеки к состоянию от " + name + "?\n\nВсё, что добавлено после этого, пропадёт из библиотеки.")) return;
                    try
                    {
                        LibMaint.Restore(dir, folder);
                        lastMessage = "Библиотеки восстановлены из копии от " + name + ". В Altium: Components → ≡ → Refresh.";
                        Reload();
                        if (LibraryChanged != null) LibraryChanged(this, EventArgs.Empty);
                    }
                    catch (Exception ex) { MessageBox.Show(this, "Не получилось: " + ex.Message, "EasyLib", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
            }
            m.Items.Add(new ToolStripSeparator());
            ToolStripItem open = m.Items.Add("Открыть папку с копиями");
            open.Click += delegate
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Path.Combine(dir, "Backups") + "\"") { UseShellExecute = true }); } catch { }
            };
            m.Show(btnRestore, new Point(0, btnRestore.Height));
        }
    }
}
