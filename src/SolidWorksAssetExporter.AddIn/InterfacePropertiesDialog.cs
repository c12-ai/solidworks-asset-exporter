using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class InterfacePropertiesDialog : Form
    {
        private readonly List<InterfaceAsset> _assets;
        private readonly Action<InterfaceAsset> _validate;
        private readonly Func<BindingEndpoint, string> _readConnection;
        private readonly InterfaceCatalogStore _store = new InterfaceCatalogStore();
        private readonly SortedSet<string> _names = new SortedSet<string>(StringComparer.Ordinal);
        private readonly ListBox _files = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private readonly ComboBox _connection = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        private readonly CheckedListBox _accepts = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        private readonly TextBox _newName = new TextBox { Width = 235 };
        private readonly TextBox _path = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly NumericUpDown _version = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Width = 100 };
        private readonly CheckBox _unpublished = new CheckBox { Text = "确认此资产尚未发布（允许保留版本）", AutoSize = true };
        private readonly TextBox _preview = new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly Label _status = new Label { Dock = DockStyle.Fill };
        private readonly Dictionary<InterfaceAsset, Draft> _drafts = new Dictionary<InterfaceAsset, Draft>();
        private InterfaceAsset _current;
        private bool _loading;
        private string _catalogError;
        public bool SavedAny { get; private set; }
        public InterfacePropertiesDialog(List<InterfaceAsset> assets, IEnumerable<string> names, Action<InterfaceAsset> validate, Func<BindingEndpoint, string> readConnection)
        {
            _assets = assets; _validate = validate; _readConnection = readConnection;
            foreach (var name in names) if (!string.IsNullOrWhiteSpace(name)) _names.Add(name);
            try { foreach (var name in _store.Load()) _names.Add(name); } catch (ValidationException ex) { _catalogError = ex.Message; }
            foreach (var a in assets) { foreach (var n in InterfaceEditRules.Split(a.Snapshot.Get(PropertyRules.AcceptsInterfaces))) _names.Add(n);
                foreach (var n in InterfaceEditRules.Split(a.Snapshot.Get(PropertyRules.ConnectionInterface))) _names.Add(n); }
            Text = "接口属性 · 文件级编辑"; Size = new Size(1160, 800); MinimumSize = new Size(1050, 740);
            Font = new Font("Microsoft YaHei UI", 9F); StartPosition = FormStartPosition.CenterParent;
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 2 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var intro = new Label { Dock = DockStyle.Fill, Text = "选择源文件后编辑接口；同源实例只需改一次。修改会影响引用该文件的所有实例。\n建议只追加接受接口，需核对后写入。保存所选文件后，再保存总装配并重新分类预览。" };
            outer.Controls.Add(intro, 0, 0); outer.SetColumnSpan(intro, 2); outer.Controls.Add(_files, 0, 1);
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(10, 0, 0, 0) };
            foreach (var h in new[] { 32, 25, 35, 35 }) form.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); form.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); form.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); form.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            form.Controls.Add(_path, 0, 0); form.Controls.Add(new Label { Text = "向上连接接口 connection_interface（单值；永久固定可留空）", Dock = DockStyle.Fill }, 0, 1);
            form.Controls.Add(_connection, 0, 2);
            var acceptsBar = Bar(); acceptsBar.Controls.Add(new Label { Text = "可接受接口 accepts_interfaces", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            acceptsBar.Controls.Add(Button("根据子组件生成建议", Suggest)); form.Controls.Add(acceptsBar, 0, 3); form.Controls.Add(_accepts, 0, 4);
            var newBar = Bar(); newBar.Controls.Add(_newName); newBar.Controls.Add(Button("添加新接口选项", AddName)); form.Controls.Add(newBar, 0, 5);
            var versionBar = Bar(); versionBar.Controls.Add(new Label { Text = "保存后的资产版本", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); versionBar.Controls.Add(_version); versionBar.Controls.Add(_unpublished); form.Controls.Add(versionBar, 0, 6);
            form.Controls.Add(_preview, 0, 7);
            var actions = Bar(); actions.Controls.Add(Button("检查并保存所选资产…", SaveCurrent)); actions.Controls.Add(Button("关闭", Close)); form.Controls.Add(actions, 0, 8); form.Controls.Add(_status, 0, 9);
            outer.Controls.Add(form, 1, 1); Controls.Add(outer);
            _files.SelectedIndexChanged += delegate { Safe(SelectAsset); };
            _connection.TextChanged += delegate { Preview(); }; _version.ValueChanged += delegate { Preview(); };
            _accepts.ItemCheck += delegate { if (!_loading && IsHandleCreated) BeginInvoke(new Action(Preview)); };
            _unpublished.CheckedChanged += delegate { if (!_loading && _current != null) _version.Value = _unpublished.Checked ? _current.Snapshot.Version : Math.Min((long)int.MaxValue, (long)_current.Snapshot.Version + 1); Preview(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                StoreDraft(); if (_drafts.Any(p => HasChanges(p.Key, p.Value)) && MessageBox.Show(this, "仍有未写入的接口草稿，关闭并放弃这些草稿？", "关闭接口编辑", MessageBoxButtons.OKCancel) != DialogResult.OK) e.Cancel = true;
            };
            foreach (var asset in assets) _files.Items.Add(asset); if (_files.Items.Count > 0) _files.SelectedIndex = 0;
        }
        private static FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; }
        private Button Button(string label, Action action) { var b = new Button { Text = label, AutoSize = true, Margin = new Padding(0, 0, 8, 0) }; b.Click += delegate { Safe(action); }; return b; }
        private void Safe(Action action) { try { action(); } catch (Exception ex) { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "接口属性", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
        private Draft ReadDraft() { return new Draft { Connection = _connection.Text, Accepts = _accepts.CheckedItems.Cast<string>().ToList(), Version = (int)_version.Value, Unpublished = _unpublished.Checked }; }
        private void StoreDraft() { if (_current != null && !_loading) _drafts[_current] = ReadDraft(); }
        private static bool HasChanges(InterfaceAsset a, Draft d)
        { return d.Connection.Trim() != a.Snapshot.Get(PropertyRules.ConnectionInterface) || string.Join(";", d.Accepts) != a.Snapshot.Get(PropertyRules.AcceptsInterfaces); }
        private void SelectAsset()
        {
            StoreDraft(); _current = _files.SelectedItem as InterfaceAsset; if (_current == null) return;
            Draft d; if (!_drafts.TryGetValue(_current, out d)) d = new Draft { Connection = _current.Snapshot.Get(PropertyRules.ConnectionInterface), Accepts = InterfaceEditRules.Split(_current.Snapshot.Get(PropertyRules.AcceptsInterfaces)), Version = (int)Math.Min((long)int.MaxValue, (long)_current.Snapshot.Version + 1) };
            _loading = true; _path.Text = _current.Path; FillOptions(d); _version.Value = d.Version; _unpublished.Checked = d.Unpublished; _loading = false;
            _status.Text = _catalogError ?? "下拉选项来自当前装配及本机接口库；选项本身不代表已确认兼容。"; Preview();
        }
        private void FillOptions(Draft d)
        {
            _connection.Items.Clear(); _connection.Items.Add(""); _connection.Items.AddRange(_names.Cast<object>().ToArray()); _connection.Text = d.Connection;
            _accepts.Items.Clear(); foreach (var name in d.Accepts.Concat(_names).Distinct(StringComparer.Ordinal)) _accepts.Items.Add(name, d.Accepts.Contains(name, StringComparer.Ordinal));
        }
        private void AddName()
        {
            var name = InterfaceEditRules.Name(_newName.Text); var collision = _names.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase) && s != name);
            if (collision != null) throw new ValidationException("已有仅大小写不同的接口：" + collision + "。请直接选择已有名称。");
            var d = ReadDraft(); _names.Add(name); _loading = true; FillOptions(d); _loading = false; _newName.Clear();
            _status.Text = "已加入本窗口候选项；请选择向上接口或勾选接受接口，确认保存资产后才写入本机接口库。"; Preview();
        }
        private void Suggest()
        {
            if (_current == null || _current.Children.Count == 0) throw new ValidationException("所选实例没有直接子组件。请先在绑定面板选择父和子，或选中已有父组件组。");
            StoreDraft(); var connections = new List<string>(); var missing = new List<string>();
            foreach (var child in _current.Children.GroupBy(e => e.Context).Select(g => g.First()))
            {
                var asset = _assets.FirstOrDefault(a => string.Equals(a.Path, child.SourcePath, StringComparison.OrdinalIgnoreCase));
                // Suggestions use saved source properties, never another file's unconfirmed draft.
                var value = asset == null ? _readConnection(child) : asset.Snapshot.Get(PropertyRules.ConnectionInterface);
                if (string.IsNullOrWhiteSpace(value)) missing.Add(child.DisplayName); else connections.Add(value);
            }
            var d = ReadDraft(); var before = d.Accepts.Count; d.Accepts = InterfaceEditRules.Suggest(string.Join(";", d.Accepts), connections);
            foreach (var n in d.Accepts) _names.Add(n); _loading = true; FillOptions(d); _loading = false; Preview();
            _status.Text = "建议追加 " + (d.Accepts.Count - before) + " 个接口，原有项保留。请核对后保存。" + (missing.Count > 0 ? "\n" + missing.Count + " 个子组件缺少 connection_interface，请先选择其源文件补齐并保存，再重新生成建议。" : "");
        }
        private void Preview()
        {
            if (_loading || _current == null) return;
            var d = ReadDraft();
            _preview.Text = "原向上接口：" + _current.Snapshot.Get(PropertyRules.ConnectionInterface) + "\r\n新向上接口：" + d.Connection +
                "\r\n原接受接口：" + _current.Snapshot.Get(PropertyRules.AcceptsInterfaces) + "\r\n新接受接口：" + string.Join(";", d.Accepts) +
                "\r\n接口有变更时版本：" + _current.Snapshot.Version + " → " + d.Version + "；" + (d.Unpublished ? "由你确认尚未发布" : "已发布或状态不确定，要求升版");
        }
        private void SaveCurrent()
        {
            if (_current == null) return; var d = ReadDraft();
            var changes = InterfaceEditRules.Changes(_current.Snapshot, d.Connection, d.Accepts, d.Version, d.Unpublished);
            if (changes.Count == 0) { _status.Text = "接口未变化，未保存或升版。"; return; }
            var summary = _current.Path + "\n\n" + string.Join("\n", changes.Select(p => p.Key + ": " + _current.Snapshot.Get(p.Key) + " → " + p.Value)) +
                "\n\n将写入并保存这个资产源文件，影响引用它的所有实例。确认兼容性和版本后继续。";
            if (MessageBox.Show(this, summary, "确认资产属性修改", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            string result;
            _status.Text = "正在重新校验组件身份并保存源文件，请稍候…"; _status.Refresh();
            UseWaitCursor = true; Cursor.Current = Cursors.WaitCursor;
            try { _validate(_current); result = _current.Save(changes); }
            finally { UseWaitCursor = false; Cursor.Current = Cursors.Default; }
            SavedAny = true; _drafts.Remove(_current);
            foreach (var name in d.Accepts.Concat(string.IsNullOrWhiteSpace(d.Connection) ? new string[0] : new[] { d.Connection.Trim() })) _names.Add(name);
            try { _store.Merge(d.Accepts.Concat(string.IsNullOrWhiteSpace(d.Connection) ? new string[0] : new[] { d.Connection })); _catalogError = null; }
            catch (Exception ex) { _catalogError = "资产已保存，但接口库更新失败：" + ex.Message; }
            _loading = true; var saved = new Draft { Connection = _current.Snapshot.Get(PropertyRules.ConnectionInterface), Accepts = InterfaceEditRules.Split(_current.Snapshot.Get(PropertyRules.AcceptsInterfaces)), Version = (int)Math.Min((long)int.MaxValue, (long)_current.Snapshot.Version + 1) };
            FillOptions(saved); _version.Value = saved.Version; _unpublished.Checked = false; _loading = false; _status.Text = result + (_catalogError == null ? "" : "\n" + _catalogError); Preview();
        }
        private sealed class Draft { public string Connection; public List<string> Accepts; public int Version; public bool Unpublished; }
    }
}
