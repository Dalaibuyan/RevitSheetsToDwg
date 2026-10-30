using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;

namespace RevitSheetsToDwg
{
    /// <summary>Sheet сонгох, гаралтын файл ба байршуулалтын тохиргооны цонх.</summary>
    class SheetPickerForm : Form
    {
        public const string DefaultSetup = "(Revit-ийн үндсэн тохиргоо)";

        class SheetItem
        {
            public ViewSheet Sheet;
            public string Text;
            public override string ToString() => Text;
        }

        readonly List<SheetItem> _items;
        readonly HashSet<ElementId> _checked;
        readonly CheckedListBox _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        readonly TextBox _filter = new TextBox { Dock = DockStyle.Fill };
        readonly Label _count = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        readonly ComboBox _setup = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox _output = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox _accore = new TextBox { Dock = DockStyle.Fill };
        readonly NumericUpDown _columns = new NumericUpDown { Minimum = 1, Maximum = 100, Width = 70 };
        readonly NumericUpDown _gap = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 0, Width = 90 };
        readonly CheckBox _skip3D = new CheckBox
        {
            Text = "3D харагдацуудыг алгасах (sheet дээрх 3D view-ийг DWG-д оруулахгүй)",
            AutoSize = true, Dock = DockStyle.Fill
        };
        readonly bool _layouts;
        bool _refreshing;

        public List<ViewSheet> SelectedSheets =>
            _items.Where(i => _checked.Contains(i.Sheet.Id)).Select(i => i.Sheet).ToList();
        public string SetupName => _setup.SelectedIndex <= 0 ? null : (string)_setup.SelectedItem;
        public string OutputPath => _output.Text.Trim().Trim('"');
        public string AccorePath => _accore.Text.Trim().Trim('"');
        public int Columns => (int)_columns.Value;
        public double Gap => _layouts ? 0 : (double)_gap.Value;
        public bool Skip3D => _skip3D.Checked;

        public SheetPickerForm(IList<ViewSheet> sheets, IEnumerable<string> setups, Settings settings,
            ICollection<ElementId> preselected, string defaultOutput, bool layouts)
        {
            _layouts = layouts;
            Text = layouts ? "Revit sheet-үүд → нэг DWG (sheet бүр тусдаа layout)"
                           : "Revit sheet-үүд → нэг DWG (бүгд model space-д)";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 560);
            Size = new Size(680, 720);

            _items = sheets.Select(s => new SheetItem { Sheet = s, Text = s.SheetNumber + "  -  " + s.Name }).ToList();
            _checked = new HashSet<ElementId>(preselected.Count > 0 ? preselected : sheets.Select(s => s.Id));

            _setup.Items.Add(DefaultSetup);
            foreach (var name in setups) _setup.Items.Add(name);
            int setupIndex = settings.Setup == null ? -1 : _setup.Items.IndexOf(settings.Setup);
            _setup.SelectedIndex = setupIndex > 0 ? setupIndex : 0;

            _output.Text = defaultOutput;
            _accore.Text = File.Exists(settings.AccorePath ?? "") ? settings.AccorePath : AccoreRunner.FindAccoreConsole();
            _columns.Value = Math.Max(1, Math.Min(100, settings.Columns));
            _gap.Value = (decimal)Math.Max(0, Math.Min(100000, settings.Gap));
            _skip3D.Checked = settings.Skip3D;

            BuildLayout();
            _list.ItemCheck += OnItemCheck;
            _filter.TextChanged += (s, e) => RefreshList();
            RefreshList();
        }

        void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddRow(root, "Хайх:", _filter, null, SizeType.AutoSize);

            var selectButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            selectButtons.Controls.Add(MakeButton("Бүгдийг сонгох", (s, e) => SetAllVisible(true)));
            selectButtons.Controls.Add(MakeButton("Цуцлах", (s, e) => SetAllVisible(false)));
            selectButtons.Controls.Add(_count);
            AddRow(root, "", selectButtons, null, SizeType.AutoSize);

            AddRow(root, "Sheet-үүд:", _list, null, SizeType.Percent);
            AddRow(root, "DWG export setup:", _setup, null, SizeType.AutoSize);
            AddRow(root, "", _skip3D, null, SizeType.AutoSize);
            AddRow(root, "Гаралтын DWG:", _output, MakeButton("...", (s, e) => BrowseOutput()), SizeType.AutoSize);

            // Layout горимд sheet-үүд layout-д байрлах тул model space-ийн тор тохируулах шаардлагагүй
            if (!_layouts)
            {
                var grid = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
                grid.Controls.Add(new Label { Text = "Нэг мөрөнд:", AutoSize = true, Margin = new Padding(0, 6, 3, 0) });
                grid.Controls.Add(_columns);
                grid.Controls.Add(new Label { Text = "   Зай (мм, 0 = автомат):", AutoSize = true, Margin = new Padding(0, 6, 3, 0) });
                grid.Controls.Add(_gap);
                AddRow(root, "Байршуулалт:", grid, null, SizeType.AutoSize);
            }

            AddRow(root, "accoreconsole.exe:", _accore, MakeButton("...", (s, e) => BrowseAccore()), SizeType.AutoSize);

            var ok = new Button { Text = "Нэгтгэх", AutoSize = true, DialogResult = DialogResult.None };
            ok.Click += (s, e) => { if (Validate(out var error)) DialogResult = DialogResult.OK; else MessageBox.Show(this, error, Text); };
            var cancel = new Button { Text = "Болих", AutoSize = true, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(buttons, 0, root.RowCount++);
            root.SetColumnSpan(buttons, 3);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(root);
        }

        static void AddRow(TableLayoutPanel root, string label, Control control, Control extra, SizeType sizeType)
        {
            root.RowStyles.Add(sizeType == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            int row = root.RowCount++;
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            root.Controls.Add(control, 1, row);
            if (extra != null) root.Controls.Add(extra, 2, row);
            else root.SetColumnSpan(control, 2);
        }

        static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        void RefreshList()
        {
            _refreshing = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            var filter = _filter.Text.Trim();
            foreach (var item in _items)
                if (filter.Length == 0 || item.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    _list.Items.Add(item, _checked.Contains(item.Sheet.Id));
            _list.EndUpdate();
            _refreshing = false;
            UpdateCount();
        }

        void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_refreshing) return;
            var item = (SheetItem)_list.Items[e.Index];
            if (e.NewValue == CheckState.Checked) _checked.Add(item.Sheet.Id);
            else _checked.Remove(item.Sheet.Id);
            BeginInvoke(new Action(UpdateCount));
        }

        void SetAllVisible(bool value)
        {
            foreach (SheetItem item in _list.Items)
                if (value) _checked.Add(item.Sheet.Id); else _checked.Remove(item.Sheet.Id);
            RefreshList();
        }

        void UpdateCount() => _count.Text = "   Сонгосон: " + _checked.Count + " / " + _items.Count;

        void BrowseOutput()
        {
            using (var dlg = new SaveFileDialog { Filter = "AutoCAD зураг (*.dwg)|*.dwg" })
            {
                try { dlg.FileName = Path.GetFileName(OutputPath); } catch (ArgumentException) { }
                var dir = PathUtil.DirectoryOf(OutputPath);
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(this) == DialogResult.OK) _output.Text = dlg.FileName;
            }
        }

        void BrowseAccore()
        {
            using (var dlg = new OpenFileDialog { Filter = "accoreconsole.exe|accoreconsole.exe" })
                if (dlg.ShowDialog(this) == DialogResult.OK) _accore.Text = dlg.FileName;
        }

        bool Validate(out string error)
        {
            error = null;
            if (_checked.Count == 0) error = "Дор хаяж нэг sheet сонгоно уу.";
            else if (!OutputPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(PathUtil.DirectoryOf(OutputPath)))
                error = "Гаралтын .dwg файлын замыг зөв оруулна уу (хавтас нь байх ёстой).";
            else if (!File.Exists(AccorePath))
                error = "AutoCAD-ийн accoreconsole.exe олдсонгүй. AutoCAD (2024 эсвэл түүнээс өмнөх) суусан байх шаардлагатай.";
            return error == null;
        }
    }
}
