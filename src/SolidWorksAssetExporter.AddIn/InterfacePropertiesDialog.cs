using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class InterfaceEditorItem
    {
        internal InterfaceAsset Asset;
        internal BindingEndpoint Endpoint;
        internal string AssetUuid;
        internal string DisplayName;
        internal int InstanceCount;
        internal int MaximumBoundSlots;
        internal IList<BindingEndpoint> Children;

        public override string ToString()
        {
            return InstanceCount > 1 ? DisplayName + "（" + InstanceCount + " 个实例）" : DisplayName;
        }
    }

    internal sealed class InterfacePropertiesDialog : Form
    {
        private const string DefaultSlotType = "place";
        private const string DefaultEngagement = "loose";
        private const int SlotTypeRow = 5;
        private static readonly Dictionary<string, string> ChoiceLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "place", "放置" }, { "mount", "安装" }, { "clamp", "夹持" },
            { "thread", "螺纹" }, { "press_fit", "压入配合" }, { "snap_fit", "卡扣" },
            { "loose", "松配合" }, { "tight", "紧配合" }
        };
        private readonly Action<InterfaceEditorItem> _validate;
        private readonly Func<BindingEndpoint, string> _readConnection;
        private readonly Action _onSaved;
        private readonly SharedInterfaceCatalog _catalog = new SharedInterfaceCatalog();
        private readonly SortedSet<string> _names = new SortedSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<InterfaceEditorItem, Draft> _drafts = new Dictionary<InterfaceEditorItem, Draft>();
        private readonly ListBox _files = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private readonly TextBox _path = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        private readonly ComboBox _connection = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
        };
        private readonly CheckedListBox _accepts = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        private readonly TextBox _newName = new TextBox { Width = 230 };
        private readonly TextBox _slots = new TextBox { Width = 88, AccessibleName = "总槽位容量" };
        private readonly ComboBox _slotType = Choice("槽位类型 slot_type", PropertyRules.AssetSlotTypes, 146);
        private readonly ComboBox _engagement = Choice("接合方式 engagement", PropertyRules.AssetEngagementValues, 115);
        private readonly TextBox _preview = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label _status = new Label { Dock = DockStyle.Fill };
        private readonly Button _save = new Button { Text = "检查并保存所选资产…", AutoSize = true };
        private readonly TableLayoutPanel _fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 16 };
        private FlowLayoutPanel _slotTypeFields;
        private readonly Font _sectionFont = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
        private InterfaceEditorItem _current;
        private bool _loading;
        private bool _saving;
        private string _catalogError;

        internal bool SavedAny { get; private set; }

        internal InterfacePropertiesDialog(IList<InterfaceEditorItem> assets, Action<InterfaceEditorItem> validate,
            Func<BindingEndpoint, string> readConnection, Action onSaved)
        {
            _validate = validate;
            _readConnection = readConnection;
            _onSaved = onSaved;
            foreach (var item in assets)
            {
                RememberInterfaceNames(ReadSaved(item));
            }

            Text = "接口属性";
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(980, 765);
            MinimumSize = new Size(830, 680);
            BuildLayout();

            _files.SelectedIndexChanged += delegate { SelectAsset(); };
            _connection.TextChanged += delegate { UpdatePreview(); };
            _slots.TextChanged += delegate { UpdatePreview(); };
            _slotType.SelectedIndexChanged += delegate { UpdatePreview(); };
            _engagement.SelectedIndexChanged += delegate { UpdatePreview(); };
            _accepts.ItemCheck += delegate { if (!_loading && IsHandleCreated) BeginInvoke(new Action(UpdatePreview)); };
            _save.Click += SaveCurrent;
            Shown += async delegate { await LoadCatalog(); };
            FormClosing += CheckUnsaved;
            Disposed += delegate { _sectionFont.Dispose(); };
            foreach (var item in assets) _files.Items.Add(item);
            if (_files.Items.Count != 0) _files.SelectedIndex = 0;
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var intro = Label("编辑源文件的文件级自定义属性。保存会影响引用该文件的所有实例。");
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 2);
            layout.Controls.Add(_files, 0, 1);

            _fields.Padding = new Padding(10, 0, 0, 0);
            foreach (var height in new[] { 29, 23, 32, 23, 32, 0, 23, 22, 30, 32 })
                _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            _fields.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            _fields.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            _slotTypeFields = Row(InlineLabel(_slotType.AccessibleName), _slotType);
            _slotTypeFields.Visible = false;
            _fields.Controls.Add(_path, 0, 0);
            _fields.Controls.Add(Section("资产属性"), 0, 1);
            _fields.Controls.Add(Row(InlineLabel(_engagement.AccessibleName), _engagement), 0, 2);
            _fields.Controls.Add(Section("槽位定义"), 0, 3);
            _fields.Controls.Add(Row(InlineLabel("总槽位容量 slots_num（包含空槽）"), _slots), 0, 4);
            _fields.Controls.Add(_slotTypeFields, 0, SlotTypeRow);
            _fields.Controls.Add(Section("机械接口"), 0, 6);
            _fields.Controls.Add(Label("向上连接接口 connection_interface"), 0, 7);
            _fields.Controls.Add(_connection, 0, 8);
            _fields.Controls.Add(Row(InlineLabel("可接受接口 accepts_interfaces"), Button("根据子组件生成建议", Suggest)), 0, 9);
            _fields.Controls.Add(_accepts, 0, 10);
            _fields.Controls.Add(Row(_newName, Button("添加到在线接口库", AddName)), 0, 11);
            _fields.Controls.Add(Section("修改预览"), 0, 12);
            _fields.Controls.Add(_preview, 0, 13);
            _fields.Controls.Add(Row(_save, Button("重新读取文件属性", ReloadCurrent), Button("关闭", Close)), 0, 14);
            _fields.Controls.Add(_status, 0, 15);
            layout.Controls.Add(_fields, 1, 1);
            Controls.Add(layout);
        }

        private static Label Label(string text) { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }; }

        private Label Section(string text)
        {
            var label = Label(text);
            label.Font = _sectionFont;
            return label;
        }

        private static Label InlineLabel(string text) { return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 6, 0) }; }

        private static ComboBox Choice(string name, IEnumerable<string> values, int width)
        {
            var control = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, Width = width };
            control.Items.AddRange(values.Select(value => new ChoiceItem(value)).ToArray());
            return control;
        }

        private static void SelectChoice(ComboBox control, string key)
        {
            var item = control.Items.Cast<ChoiceItem>().FirstOrDefault(choice => choice.Key == key);
            control.SelectedIndex = item == null ? -1 : control.Items.IndexOf(item);
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            row.Controls.AddRange(controls);
            return row;
        }

        private Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += delegate
            {
                try { action(); }
                catch (Exception ex) { ShowError(ex); }
            };
            return button;
        }

        private static Draft ReadSaved(InterfaceEditorItem item)
        {
            var snapshot = item.Asset.Snapshot;
            return new Draft
            {
                Connection = PropertyRules.MechanicalConnectionInterface(snapshot.Get(PropertyRules.ConnectionInterface)),
                Accepts = PropertyRules.MechanicalInterfaceNames(snapshot.Get(PropertyRules.AcceptsInterfaces)),
                Slots = snapshot.Get(PropertyRules.SlotsNum),
                SlotType = PropertyRules.ReadAssetSlotType(snapshot.Values),
                Engagement = snapshot.Get(PropertyRules.AssetEngagement).Trim()
            };
        }

        private Draft ReadDraft()
        {
            var slot = _slotType.SelectedItem as ChoiceItem;
            var engagement = _engagement.SelectedItem as ChoiceItem;
            return new Draft
            {
                Connection = _connection.Text,
                Accepts = _accepts.CheckedItems.Cast<string>().ToList(),
                Slots = _slots.Text,
                SlotType = slot == null ? null : slot.Key,
                Engagement = engagement == null ? null : engagement.Key
            };
        }

        private void SelectAsset()
        {
            StoreDraft();
            _current = _files.SelectedItem as InterfaceEditorItem;
            _fields.Enabled = _current != null;
            if (_current == null) return;

            Draft draft;
            if (!_drafts.TryGetValue(_current, out draft)) draft = NewDraft(_current);
            _path.Text = _current.Asset.Path;
            FillDraft(draft);
            _status.Text = _catalogError ?? string.Empty;
        }

        private static Draft NewDraft(InterfaceEditorItem item)
        {
            var draft = ReadSaved(item);
            if (draft.Engagement.Length == 0) draft.Engagement = DefaultEngagement;
            return draft;
        }

        private void ReloadCurrent()
        {
            if (_current == null || _saving) return;
            if (HasChanges(ReadSaved(_current), ReadDraft(), false) &&
                MessageBox.Show(this, "放弃所选资产的未保存草稿并重新读取？", "重新读取文件属性",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _validate(_current);
            _current.Asset.Reload();
            _drafts.Remove(_current);
            var draft = NewDraft(_current);
            RememberInterfaceNames(draft);
            FillDraft(draft);
            _status.Text = "已重新读取文件级自定义属性。";
        }

        private void RememberInterfaceNames(Draft draft)
        {
            _names.UnionWith(PropertyRules.MechanicalInterfaceNames(draft.Connection));
            _names.UnionWith(draft.Accepts);
        }

        private void StoreDraft()
        {
            if (_current != null) _drafts[_current] = ReadDraft();
        }

        private void FillDraft(Draft draft)
        {
            _loading = true;
            try
            {
                _connection.Items.Clear();
                _connection.Items.AddRange(_names.Cast<object>().ToArray());
                _connection.Text = draft.Connection;
                _accepts.Items.Clear();
                var checkedNames = new HashSet<string>(draft.Accepts, StringComparer.Ordinal);
                foreach (var name in draft.Accepts.Concat(_names).Distinct(StringComparer.Ordinal))
                    _accepts.Items.Add(name, checkedNames.Contains(name));
                _slots.Text = draft.Slots;
                SelectChoice(_slotType, draft.SlotType);
                SelectChoice(_engagement, draft.Engagement);
            }
            finally { _loading = false; }
            UpdatePreview();
        }

        private async Task LoadCatalog()
        {
            try
            {
                var result = await Task.Run(() => _catalog.Load());
                if (IsDisposed) return;
                _names.UnionWith(result.Names);
                _catalogError = result.Warning;
                if (_current != null) FillDraft(ReadDraft());
                if (!_saving && _catalogError != null) _status.Text = _catalogError;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _catalogError = "接口库读取失败：" + ex.Message;
                if (!_saving) _status.Text = _catalogError;
            }
        }

        private async void AddName()
        {
            if (_saving) return;
            try
            {
                var name = InterfaceEditRules.MechanicalName(_newName.Text);
                var collision = _names.FirstOrDefault(value => value.Equals(name, StringComparison.OrdinalIgnoreCase) && value != name);
                if (collision != null) throw new ValidationException("已有仅大小写不同的接口，请选择已有名称：" + collision);
                SetSaving(true);
                _status.Text = "正在更新在线接口库…";
                var result = await Task.Run(() => _catalog.Merge(new[] { name }));
                _names.UnionWith(result.Names);
                _catalogError = result.Warning;
                FillDraft(ReadDraft());
                _newName.Clear();
                _status.Text = "已加入在线接口库。" + (_catalogError == null ? string.Empty : "\r\n" + _catalogError);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetSaving(false); }
        }

        private void Suggest()
        {
            if (_current == null || _current.Children == null || _current.Children.Count == 0)
                throw new ValidationException("该资产没有可生成建议的直接子组件。");
            _validate(_current);
            var connections = new List<string>();
            var missing = new List<string>();
            foreach (var child in _current.Children.GroupBy(endpoint => endpoint.SourcePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                var value = PropertyRules.MechanicalConnectionInterface(_readConnection(child));
                if (string.IsNullOrWhiteSpace(value)) missing.Add(child.DisplayName);
                else connections.Add(value);
            }
            var draft = ReadDraft();
            var before = draft.Accepts.Count;
            draft.Accepts = InterfaceEditRules.Suggest(string.Join(";", draft.Accepts), connections);
            _names.UnionWith(draft.Accepts);
            FillDraft(draft);
            _status.Text = "建议追加 " + (draft.Accepts.Count - before) + " 个接口。" + (missing.Count == 0 ? string.Empty :
                "以下子组件缺少向上接口，请先编辑并保存其源文件：" + string.Join("、", missing));
        }

        private void UpdatePreview()
        {
            if (_loading || _current == null || IsDisposed) return;
            var draft = ReadDraft();
            var saved = ReadSaved(_current);
            var hasSlots = draft.HasSlots;
            _slotTypeFields.Visible = hasSlots;
            _fields.RowStyles[SlotTypeRow].Height = hasSlots ? 32 : 0;
            if (hasSlots && draft.SlotType == null && saved.SlotType.Length == 0 && draft.Engagement == DefaultEngagement)
            {
                _loading = true;
                try { SelectChoice(_slotType, DefaultSlotType); }
                finally { _loading = false; }
                draft.SlotType = DefaultSlotType;
            }
            var preview = new List<string>
            {
                "接合方式  " + Display(saved.Engagement) + " → " + (draft.Engagement ?? "（请选择）"),
                "设计总槽位容量  " + Display(saved.Slots) + " → " + Display(draft.Slots) +
                    "  ·  同源单实例最多已绑定 " + _current.MaximumBoundSlots.ToString(CultureInfo.InvariantCulture) + " 槽"
            };
            if (hasSlots) preview.Add("槽位类型  " + Display(saved.SlotType) + " → " + (draft.SlotType ?? "（请选择）"));
            preview.Add("向上接口  " + Display(saved.Connection) + " → " + Display(draft.Connection));
            preview.Add("接受接口  " + Display(string.Join(";", saved.Accepts)) + " → " + Display(string.Join(";", draft.Accepts)));
            preview.Add("当前版本 v" + _current.Asset.Snapshot.Version + "。保存前查询 Wanxiang 发布状态。");
            _preview.Lines = preview.ToArray();
            _save.Enabled = draft.Engagement != null && (!hasSlots || draft.SlotType != null) && HasChanges(saved, draft);
        }

        private static string Display(string value) { return string.IsNullOrWhiteSpace(value) ? "（空）" : value; }

        private static bool HasChanges(Draft saved, Draft draft, bool includeSuggestedDefaults = true)
        {
            var slotChanged = draft.HasSlots && draft.SlotType != null && draft.SlotType != saved.SlotType &&
                (includeSuggestedDefaults || saved.SlotType.Length != 0 || draft.SlotType != DefaultSlotType);
            var engagementChanged = draft.Engagement != null && draft.Engagement != saved.Engagement &&
                (includeSuggestedDefaults || saved.Engagement.Length != 0 || draft.Engagement != DefaultEngagement);
            return saved.Connection.Trim() != draft.Connection.Trim() || saved.Slots.Trim() != draft.Slots.Trim() ||
                !new HashSet<string>(saved.Accepts, StringComparer.Ordinal).SetEquals(draft.Accepts) || slotChanged || engagementChanged;
        }

        private static Dictionary<string, string> Changes(InterfaceEditorItem item, Draft draft, int version, bool unpublished)
        {
            if (draft.Engagement == null) throw new ValidationException("请从下拉列表中选择接合方式。");
            if (draft.HasSlots && draft.SlotType == null) throw new ValidationException("请从下拉列表中选择槽位类型。");
            var saved = ReadSaved(item);
            int? slots = null;
            if (saved.Slots.Trim() != draft.Slots.Trim())
            {
                int parsed;
                if (!int.TryParse(draft.Slots.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                    throw new ValidationException("总槽位容量必须是非负整数。没有槽位时请填写 0。");
                slots = parsed;
            }
            var accepts = new HashSet<string>(saved.Accepts, StringComparer.Ordinal).SetEquals(draft.Accepts) ? null : draft.Accepts;
            var connection = saved.Connection.Trim() == draft.Connection.Trim() ? null : draft.Connection;
            return InterfaceEditRules.Changes(item.Asset.Snapshot, connection, accepts, slots,
                draft.HasSlots ? draft.SlotType : null, draft.Engagement, version, unpublished);
        }

        private async void SaveCurrent(object sender, EventArgs args)
        {
            var item = _current;
            if (item == null || _saving) return;
            var draft = ReadDraft();
            try
            {
                var currentVersion = item.Asset.Snapshot.Version;
                var changes = Changes(item, draft, currentVersion, true);
                if (changes.Count == 0) return;
                _validate(item);
                if (item.Asset.SourceSnapshot == null) item.Asset.Reload(item.Asset.Snapshot);
                item.Asset.ValidateForSave(changes);

                SetSaving(true);
                _status.Text = "正在查询 Wanxiang 资产发布状态…";
                var settings = new SettingsStore().Load();
                settings.WanxiangApiKey = new WanxiangApiKeyStore().Load();
                settings.SaveRegistryLocally = false;
                var registry = await Task.Run(() => new WanxiangRegistryProvider().Fetch(settings));
                var next = InterfaceEditRules.NextVersionIfPublished(registry.Registry.Assets, item.AssetUuid, currentVersion);
                if (next.HasValue) changes = Changes(item, draft, next.Value, false);
                var summary = item.Asset.Path + "\r\n\r\n" + string.Join("\r\n", changes.Select(change => change.Key + "  " +
                    Display(item.Asset.Snapshot.Get(change.Key)) + " → " + Display(change.Value)));
                if (MessageBox.Show(this, summary, "确认保存资产源文件", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    _status.Text = "已保留草稿。";
                    return;
                }
                _validate(item);
                item.Asset.Save(changes);
                SavedAny = true;
                _drafts.Remove(item);
                _onSaved();
                if (changes.ContainsKey(PropertyRules.ConnectionInterface) || changes.ContainsKey(PropertyRules.AcceptsInterfaces))
                    await UpdateCatalog(draft);
                FillDraft(NewDraft(item));
                _status.Text = "已保存资产 v" + item.Asset.Snapshot.Version + "。" +
                    (_catalogError == null ? string.Empty : "\r\n" + _catalogError);
                try { item.Asset.SourceSnapshot = ExportSourceFileSnapshot.Capture(item.Asset.Path, null); }
                catch (Exception ex) { _status.Text += "\r\n未能重新记录源文件状态：" + ex.Message; }
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetSaving(false); }
        }

        private async Task UpdateCatalog(Draft draft)
        {
            var names = draft.Accepts.Concat(string.IsNullOrWhiteSpace(draft.Connection) ? new string[0] : new[] { draft.Connection.Trim() }).ToList();
            _names.UnionWith(names);
            if (names.Count == 0) return;
            try
            {
                var result = await Task.Run(() => _catalog.Merge(names));
                _names.UnionWith(result.Names);
                _catalogError = result.Warning;
            }
            catch (Exception ex) { _catalogError = "在线接口库更新失败：" + ex.Message; }
        }

        private void SetSaving(bool saving)
        {
            _saving = saving;
            _files.Enabled = !saving;
            _fields.Enabled = !saving;
            UseWaitCursor = saving;
        }

        private void CheckUnsaved(object sender, FormClosingEventArgs args)
        {
            if (_saving) { args.Cancel = true; return; }
            StoreDraft();
            if (_drafts.Any(pair => HasChanges(ReadSaved(pair.Key), pair.Value, false)))
                args.Cancel = MessageBox.Show(this, "关闭并放弃未保存的资产属性草稿？", "关闭接口属性",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK;
        }

        private void ShowError(Exception error)
        {
            _status.Text = error.Message;
            MessageBox.Show(this, error.Message, "接口属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private sealed class Draft
        {
            internal string Connection;
            internal List<string> Accepts;
            internal string Slots;
            internal string SlotType;
            internal string Engagement;
            internal bool HasSlots
            {
                get
                {
                    int capacity;
                    return int.TryParse(Slots.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out capacity) && capacity > 0;
                }
            }
        }

        private sealed class ChoiceItem
        {
            internal readonly string Key;

            internal ChoiceItem(string key) { Key = key; }

            public override string ToString()
            {
                string label;
                return ChoiceLabels.TryGetValue(Key, out label) ? label + " " + Key : Key;
            }
        }
    }
}
