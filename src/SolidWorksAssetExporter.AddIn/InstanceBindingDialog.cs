using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class InstanceBindingDialog : Form
    {
        private readonly SldWorks _app;
        private readonly ModelDoc2 _document;
        private readonly SwInstanceBindings _session;
        private readonly int _configurationId;
        private readonly BindingEditorState _draft = new BindingEditorState();
        private readonly TextBox _parentText = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly ListBox _childList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
        private readonly DataGridView _parents = Grid(false), _details = Grid(true);
        private readonly CheckBox _continuous = new CheckBox { Text = "连续点选子组件", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly CheckBox _chain = new CheckBox { Text = "链式绑定", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly ComboBox _nextParent = new ComboBox { Width = 245, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        private readonly TextBox _search = new TextBox { Width = 240 };
        private readonly Label _state = new Label { Dock = DockStyle.Fill };
        private readonly Label _childCount = new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
        private readonly Label _totals = new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
        private readonly Timer _timer = new Timer { Interval = 250 };
        private readonly DAssemblyDocEvents_Event _selectionEvents;
        private readonly List<BindingEndpoint> _selectionQueue = new List<BindingEndpoint>();
        private string _selectionError;
        private Dictionary<string, ExportNode> _selectionNodes;
        private List<RelationView> _views = new List<RelationView>();
        private bool _busy, _rendering;
        private string _hint = "先读取父组件，再在模型中逐个点选子组件。";

        public InstanceBindingDialog(SldWorks app)
        {
            _app = app; _document = app.ActiveDoc as ModelDoc2;
            _session = SwInstanceBindings.ForDocument(_document);
            if (_session == null) throw new ValidationException("请先打开一个可写的总装配体。");
            _session.RequireWritable(); _configurationId = _session.Current.ConfigurationId;
            Text = "承载关系 · " + _document.GetTitle() + " · " + _session.Current.ConfigurationName;
            Size = new Size(1220, 840); MinimumSize = new Size(1100, 740);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9F);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(14) };
            foreach (var height in new[] { 32, 42, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.Controls.Add(new Label { Text = "读取父组件 → 添加子组件 → 应用绑定。允许一个子组件关联多个父组件；新增绑定保留已有父级。", Dock = DockStyle.Fill }, 0, 0);
            var parentBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            parentBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); parentBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parentBar.Controls.Add(Button("父组件 · 读取单选", CaptureParent)); parentBar.Controls.Add(_parentText); layout.Controls.Add(parentBar, 0, 1);
            var childBar = Bar(); childBar.Controls.Add(_continuous);
            childBar.Controls.Add(Button("添加当前选择", CaptureChildren)); childBar.Controls.Add(Button("同级同款批量添加", CaptureSiblings));
            childBar.Controls.Add(Button("移除选中子项", delegate { foreach (var e in _childList.SelectedItems.Cast<EndpointItem>().ToArray()) _draft.Children.Remove(e.Endpoint); RefreshDraft(); }));
            childBar.Controls.Add(Button("清空本轮", delegate { _continuous.Checked = false; _draft.Clear(); _document.ClearSelection2(true); RefreshDraft(); }));
            childBar.Controls.Add(_childCount); layout.Controls.Add(childBar, 0, 2); layout.Controls.Add(_childList, 0, 3);
            var applyBar = Bar(); applyBar.Controls.Add(Button("应用绑定", delegate { Apply(false); }));
            applyBar.Controls.Add(Button("替换全部父组件…", delegate { Apply(true); })); applyBar.Controls.Add(_chain);
            applyBar.Controls.Add(new Label { Text = "下一父组件", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }); applyBar.Controls.Add(_nextParent);
            applyBar.Controls.Add(Button("将所选子项作为父", ContinueFromSelected)); layout.Controls.Add(applyBar, 0, 4);
            var filterBar = Bar(); filterBar.Controls.Add(new Label { Text = "搜索父 / 子", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); filterBar.Controls.Add(_search);
            filterBar.Controls.Add(Button("查看全部 / 刷新", delegate { _search.Clear(); RefreshRelations(); })); filterBar.Controls.Add(_totals); layout.Controls.Add(filterBar, 0, 5);
            _parents.Columns.Add("parent", "父组件（每个实例一行）"); _parents.Columns.Add("count", "子组件数"); _parents.Columns.Add("status", "状态");
            _parents.Columns[0].FillWeight = 65; _parents.Columns[1].FillWeight = 15; _parents.Columns[2].FillWeight = 20;
            _details.Columns.Add("child", "子组件明细（可多选）"); _details.Columns.Add("parents", "父级数"); _details.Columns.Add("status", "状态");
            _details.Columns[0].FillWeight = 65; _details.Columns[1].FillWeight = 12; _details.Columns[2].FillWeight = 23;
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            split.Controls.Add(_parents, 0, 0); split.Controls.Add(_details, 1, 0); layout.Controls.Add(split, 0, 6);
            var editBar = Bar(); editBar.Controls.Add(Button("高亮所选子关系", delegate { Highlight(SelectedRecords().SelectMany(r => new[] { r.Parent, r.Child })); }));
            editBar.Controls.Add(Button("解除所选子关系", delegate { Unbind(false); })); editBar.Controls.Add(Button("解除整组关系…", delegate { Unbind(true); }));
            editBar.Controls.Add(Button("将所选子项加入本轮", LoadSelectedChildren)); layout.Controls.Add(editBar, 0, 7);
            editBar.Controls.Add(Button("接口属性 / 建议…", EditInterfaces));
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.Controls.Add(Button("保存总装配", SaveAssembly)); footer.Controls.Add(_state); layout.Controls.Add(footer, 0, 8); Controls.Add(layout);
            _parents.SelectionChanged += delegate { if (!_rendering) RenderDetails(); };
            _details.CellDoubleClick += delegate { Guard(delegate { Highlight(SelectedRecords().SelectMany(r => new[] { r.Parent, r.Child })); }); };
            _search.TextChanged += delegate { RenderGroups(); };
            _continuous.CheckedChanged += delegate { _selectionQueue.Clear(); _selectionError = null; if (_continuous.Checked) Guard(StartContinuous); };
            _chain.CheckedChanged += delegate { if (!_chain.Checked && _draft.NextParents.Count > 0) { _draft.Clear(); RefreshDraft(); } };
            _nextParent.SelectionChangeCommitted += delegate { Guard(delegate {
                var item = _nextParent.SelectedItem as EndpointItem; if (item == null) return;
                _draft.ContinueFrom(item.Endpoint); _document.ClearSelection2(true); RefreshDraft(); _continuous.Checked = true;
                _hint = "已接续父组件，可继续点选下一层子组件。";
            }); };
            _timer.Tick += delegate { if (_busy) return; try { EnsureContext(); PollSelection(); RefreshSaveStatus(); }
                catch (Exception ex) { _continuous.Checked = false; _state.Text = ex.Message; } };
            _selectionEvents = _document as DAssemblyDocEvents_Event;
            if (_selectionEvents != null) _selectionEvents.NewSelectionNotify += SelectionChanged;
            FormClosed += delegate { _timer.Dispose(); if (_selectionEvents != null) { try { _selectionEvents.NewSelectionNotify -= SelectionChanged; } catch { } } };
            Guard(delegate { RefreshDraft(); RefreshRelations(); }); _timer.Start();
        }
        private static DataGridView Grid(bool multi)
        {
            return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = multi, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, RowTemplate = { Height = 32 } };
        }
        private FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true }; }
        private Button Button(string text, Action action)
        { var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 0) }; b.Click += delegate { Guard(action); }; return b; }
        private void EnsureContext()
        { if (!Equals(_app.ActiveDoc, _document) || _session.Current.ConfigurationId != _configurationId) throw new ValidationException("活动总装配或配置已切换，请关闭面板后重新打开。"); }
        private void Guard(Action action)
        {
            var previousBusy = _busy; _busy = true;
            try { EnsureContext(); action(); }
            catch (Exception ex) { _continuous.Checked = false; MessageBox.Show(this, ex.Message, "承载关系", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _busy = previousBusy; }
        }
        private AssemblyExportPlan Plan()
        { return new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(_app, new SwPluginMutationTracker(_app, true))), ProjectMeshFormat.Step, false); }
        private Dictionary<string, ExportNode> SelectionIndex(AssemblyExportPlan plan)
        {
            return SwInstanceBindings.Flatten(plan.Roots).Where(n => n.Source is SwCadNode && n.Source.InstanceId != "root")
                .ToDictionary(n => SwInstanceBindings.Context(((SwCadNode)n.Source).Component), StringComparer.Ordinal);
        }
        private List<BindingEndpoint> Selection(Dictionary<string, ExportNode> nodes)
        {
            var manager = _document.SelectionManager as SelectionMgr;
            if (manager == null) throw new ValidationException("无法读取当前选择。");
            var result = new List<BindingEndpoint>();
            for (var i = 1; i <= manager.GetSelectedObjectCount2(-1); i++)
            {
                var component = manager.GetSelectedObject6(i, -1) as Component2 ?? manager.GetSelectedObjectsComponent4(i, -1) as Component2;
                if (component == null) throw new ValidationException("请选择可独立导出的 Asset 组件实例。");
                ExportNode node; nodes.TryGetValue(SwInstanceBindings.Context(component), out node);
                var e = _session.Capture(node); if (!result.Any(v => v.Context == e.Context)) result.Add(e);
            }
            return result;
        }
        private void CaptureParent()
        {
            _continuous.Checked = false; _selectionNodes = SelectionIndex(Plan());
            var selected = Selection(_selectionNodes); if (selected.Count != 1) throw new ValidationException("父组件必须恰好选择一个实例。");
            _draft.SetParent(selected[0]); _draft.Children.RemoveAll(c => c.Context == selected[0].Context);
            _document.ClearSelection2(true); RefreshDraft(); _continuous.Checked = true;
            _hint = "可在模型中逐个点选子组件，自动累计；选一个子项可批量添加同级同款。";
        }
        private void StartContinuous()
        {
            if (_draft.Parent == null) throw new ValidationException("请先选择父组件；链式多子分支请先选择下一父组件。");
            _selectionNodes = SelectionIndex(Plan());
        }
        private int SelectionChanged()
        {
            // Snapshot every selection event, including fast successive single clicks.
            // Defer UI updates and ClearSelection2 until after the native callback returns.
            if (_busy || !_continuous.Checked || !Enabled || _draft.Parent == null) return 0;
            try { EnsureContext(); foreach (var e in Selection(_selectionNodes))
                if (!_selectionQueue.Any(q => q.Context == e.Context)) _selectionQueue.Add(e); }
            catch (Exception ex) { _selectionError = ex.Message; }
            return 0;
        }
        private void PollSelection()
        {
            if (!_continuous.Checked || !Enabled || _draft.Parent == null) return;
            var manager = _document.SelectionManager as SelectionMgr;
            if (_selectionError != null) { _hint = _selectionError + " 连续点选已暂停。"; _selectionError = null; _continuous.Checked = false; return; }
            if (_selectionQueue.Count == 0 && (manager == null || manager.GetSelectedObjectCount2(-1) == 0)) return;
            _busy = true;
            try { var selected = _selectionQueue.Concat(Selection(_selectionNodes)).ToList(); _selectionQueue.Clear(); Append(selected); }
            catch (ValidationException ex) { _continuous.Checked = false; _hint = ex.Message + " 连续点选已暂停。"; }
            finally { _busy = false; }
        }
        private void Append(List<BindingEndpoint> selected)
        {
            if (selected.Count == 0) throw new ValidationException("请至少选择一个子组件。");
            _draft.AddChildren(selected); _document.ClearSelection2(true); RefreshDraft();
            _hint = "已累计 " + _draft.Children.Count + " 个子组件；可以继续添加或应用绑定。";
        }
        private void CaptureChildren() { Append(Selection(SelectionIndex(Plan()))); }
        private void CaptureSiblings()
        {
            var nodes = SelectionIndex(Plan()); var seeds = Selection(nodes);
            if (seeds.Count == 0) seeds = _childList.SelectedItems.Cast<EndpointItem>().Select(i => i.Endpoint).ToList();
            if (seeds.Count == 0 && _draft.Children.Count > 0) seeds.Add(_draft.Children.Last());
            if (seeds.Count == 0) throw new ValidationException("先点选一个子组件，或在待绑定清单中选中一个子项，再批量添加其同级同款实例。");
            var matches = new List<BindingEndpoint>();
            foreach (var node in nodes.Values.Where(n => n.Kind == ExportNodeKind.Asset))
            {
                var sw = (SwCadNode)node.Source;
                var context = SwInstanceBindings.Context(sw.Component);
                if (!seeds.Any(s => s.AssetId == node.AssetId && string.Equals(s.SourcePath, sw.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                    BindingEditorState.OwnerContext(s.Context) == BindingEditorState.OwnerContext(context))) continue;
                var e = _session.Capture(node);
                if (seeds.Any(s => BindingEditorState.SameSiblingAsset(s, e))) matches.Add(e);
            }
            Append(matches); _hint = "已将同一 CAD 父级中的同款实例加入清单，共 " + _draft.Children.Count + " 个。请核对后应用。";
        }
        private void RefreshDraft()
        {
            _parentText.Text = _draft.Parent == null ? "" : Display(_draft.Parent);
            _childList.BeginUpdate(); _childList.Items.Clear(); foreach (var e in _draft.Children) _childList.Items.Add(new EndpointItem(e)); _childList.EndUpdate();
            _childCount.Text = "待绑定：" + _draft.Children.Count;
            _nextParent.Items.Clear(); foreach (var e in _draft.NextParents) _nextParent.Items.Add(new EndpointItem(e));
            _nextParent.Enabled = _draft.NextParents.Count > 0; _nextParent.SelectedIndex = -1;
        }
        private void Apply(bool replace)
        {
            _session.RequireWritable();
            if (_draft.Parent == null || _draft.Children.Count == 0) throw new ValidationException("请选择一个父组件和至少一个子组件。");
            var plan = Plan(); var records = _session.Current.Relations;
            var proposed = InstanceBindingRules.Apply(records, _draft.Parent, _draft.Children, e => _session.Resolve(e, plan), replace);
            if (!ConfirmBatch(records, replace)) return;
            _session.Commit(proposed); _draft.Applied(_chain.Checked); _document.ClearSelection2(true); _continuous.Checked = false;
            RefreshDraft(); RefreshRelations();
            if (_draft.Parent != null) _continuous.Checked = true;
            _hint = _draft.NextParents.Count > 0 ? "已绑定。请从“下一父组件”选择一个已绑定子组件，继续下一层。" :
                _draft.Parent != null ? "已绑定，子组件已接续为父组件。继续点选下一层子组件。" : "已绑定并清空本轮选择，可读取下一父组件。请保存总装配。";
        }
        private bool ConfirmBatch(List<InstanceBinding> records, bool replace)
        {
            using (var dialog = new Form { Text = replace ? "确认替换全部父组件（仅保留本轮父组件）" : "确认新增绑定（保留已有父组件）", Size = new Size(980, 540), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false })
            {
                var table = Grid(false); table.Columns.Add("child", "子组件"); table.Columns.Add("old", replace ? "全部已有父组件（将被替换）" : "已有父组件（保留）"); table.Columns.Add("next", "本轮父组件");
                foreach (var c in _draft.Children)
                { var old = BindingEditorState.Parents(records, c); table.Rows.Add(Display(c), old.Count == 0 ? "未绑定" : string.Join(System.Environment.NewLine, old.Select(Display)), Display(_draft.Parent)); }
                table.DefaultCellStyle.WrapMode = DataGridViewTriState.True; table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
                var ok = new Button { Text = "确认 " + _draft.Children.Count + " 项", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel }; buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                dialog.Controls.Add(table); dialog.Controls.Add(buttons); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }
        private List<InstanceBinding> SelectedRecords()
        {
            var selected = _details.SelectedRows.Cast<DataGridViewRow>().Select(r => ((RelationView)r.Tag).Record).ToList();
            if (selected.Count == 0) throw new ValidationException("请在右侧子组件明细中选择关系。"); return selected;
        }
        private void LoadSelectedChildren()
        {
            var selected = SelectedRecords(); _continuous.Checked = false; _draft.Clear(); _draft.AddChildren(selected.Select(r => r.Child));
            _document.ClearSelection2(true); RefreshDraft(); _hint = "已读取子组件；读取新父后点击“应用绑定”新增关系，或“替换全部父组件…”移除其他父级。";
        }
        private void ContinueFromSelected()
        {
            var selected = SelectedRecords(); if (selected.Count != 1) throw new ValidationException("请选择一个已绑定子组件作为下一父组件。");
            _continuous.Checked = false; _draft.Clear(); _draft.SetParent(selected[0].Child); _chain.Checked = true;
            _document.ClearSelection2(true); RefreshDraft(); _continuous.Checked = true; _hint = "已接续所选子组件，可继续绑定下一层。";
        }
        private void Unbind(bool wholeGroup)
        {
            var group = SelectedGroup();
            var selected = wholeGroup ? _views.Where(v => v.Record.Parent.Context == group).Select(v => v.Record).ToList() : SelectedRecords();
            if (selected.Count == 0) throw new ValidationException("请先选择父组件组。");
            if (MessageBox.Show(this, "解除" + (wholeGroup ? "整个父组件组的 " : "所选 ") + selected.Count + " 条关系？\n这些子组件与其他父组件的关系会保留。", "解除绑定", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            var ids = new HashSet<string>(selected.Select(r => r.Id)); _session.Commit(_session.Current.Relations.Where(r => !ids.Contains(r.Id)).ToList()); RefreshRelations();
        }
        private void Highlight(IEnumerable<BindingEndpoint> endpoints)
        {
            _continuous.Checked = false; _document.ClearSelection2(true);
            foreach (var e in endpoints.GroupBy(e => e.Context).Select(g => g.First()))
                if (!_session.ResolveComponent(e).Select4(true, null, false)) throw new ValidationException("实例无法高亮：" + e.DisplayPath);
            _hint = "已高亮，连续点选已暂停；继续添加时可重新勾选。";
        }
        private void SaveAssembly()
        {
            _session.RequireWritable();
            int error = 0, warning = 0;
            // Same document save path as Ctrl+S; binding storage commits only on native save events.
            if (!_document.Save3(0, ref error, ref warning) || error != 0) throw new ValidationException("总装配保存未完成，错误码：" + error + "。" + _session.LastSaveError);
            _session.RequireSaved(); RefreshSaveStatus(); _hint = "总装配已保存；导出前请重新分类预览。";
        }
        private void EditInterfaces()
        {
            _continuous.Checked = false;
            var plan = Plan(); var endpoints = new List<BindingEndpoint>();
            if (_draft.Parent != null) endpoints.Add(_draft.Parent); endpoints.AddRange(_draft.Children);
            var records = _session.Current.Relations;
            if (endpoints.Count == 0) endpoints.AddRange(records.Where(r => r.Parent.Context == SelectedGroup()).SelectMany(r => new[] { r.Parent, r.Child }));
            if (endpoints.Count == 0) endpoints.AddRange(Selection(SelectionIndex(plan)));
            endpoints = endpoints.GroupBy(e => e.Context).Select(g => g.First()).ToList();
            if (endpoints.Count == 0) throw new ValidationException("请先读取父/子组件、选择已有父组件组，或在 SW 中选中一个普通 Asset。");
            var assets = new List<InterfaceAsset>();
            foreach (var group in endpoints.GroupBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                // Resolve every occurrence: a replaced/invalid child may not be hidden by source grouping.
                var nodes = group.Select(e => _session.Resolve(e, plan)).ToList();
                var asset = new InterfaceAsset { Endpoint = group.First(), Source = (SwCadNode)nodes[0].Source, InstanceCount = group.Count() };
                var contexts = new HashSet<string>(group.Select(e => e.Context));
                asset.Children.AddRange(records.Where(r => contexts.Contains(r.Parent.Context)).Select(r => r.Child));
                if (_draft.Parent != null && contexts.Contains(_draft.Parent.Context)) asset.Children.AddRange(_draft.Children);
                asset.Reload(); assets.Add(asset);
            }
            var names = new List<string>();
            foreach (var n in SwInstanceBindings.Flatten(plan.Roots).Where(n => n.Kind == ExportNodeKind.Asset).GroupBy(n => ((SwCadNode)n.Source).SourcePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                var props = PropertyRules.Merge(n.Source.Model); string value;
                foreach (var key in InterfaceEditRules.Fields.Take(2)) if (props.TryGetValue(key, out value)) names.AddRange(InterfaceEditRules.Split(value));
            }
            using (var editor = new InterfacePropertiesDialog(assets, names,
                a => { EnsureContext(); _session.Resolve(a.Endpoint, Plan()); },
                e => { var node = _session.Resolve(e, Plan()); string v; return node.Properties.TryGetValue(PropertyRules.ConnectionInterface, out v) ? v : ""; }))
            {
                editor.ShowDialog(this);
                // Never keep a cached classification/version snapshot after editing shared source files.
                _selectionNodes = null; RefreshRelations();
                if (_draft.Parent != null || _draft.Children.Count > 0 || _draft.NextParents.Count > 0)
                {
                    var fresh = Plan();
                    _draft.RefreshEndpoints(e => _session.Capture(_session.Resolve(e, fresh)));
                }
                RefreshDraft();
                _hint = editor.SavedAny ? "接口源文件已保存。请核对关系，保存总装配并重新分类预览。" : "接口编辑已关闭，绑定草稿保留。";
                RefreshSaveStatus();
            }
        }
        private void RefreshRelations()
        {
            AssemblyExportPlan plan = null; string planError = null;
            try { plan = Plan(); } catch (ValidationException ex) { planError = "当前不可导出：" + ex.Message; }
            _views = new List<RelationView>();
            var cache = new Dictionary<string, ExportNode>(StringComparer.Ordinal);
            Func<BindingEndpoint, ExportNode> resolve = e => { var key = e.Context + "|" + e.Reference + "|" + e.SourceIdentity + "|" + e.SourcePath;
                ExportNode n; if (!cache.TryGetValue(key, out n)) { n = _session.Resolve(e, plan); cache.Add(key, n); } return n; };
            foreach (var r in _session.Current.Relations)
            {
                var v = new RelationView { Record = r, Parent = Display(r.Parent), Child = Display(r.Child) };
                try { if (plan == null) throw new ValidationException(planError); var p = resolve(r.Parent); var c = resolve(r.Child);
                    v.Parent = Label(p.Source.InstancePath, p.AssetId); v.Child = Label(c.Source.InstancePath, c.AssetId); InstanceBindingRules.ValidateConnection(p, c); }
                catch (ValidationException ex) { v.Error = ex.Message; }
                _views.Add(v);
            }
            RenderGroups(); RefreshSaveStatus();
        }
        private string SelectedGroup()
        { return _parents.SelectedRows.Count == 0 ? null : _parents.SelectedRows[0].Tag as string; }
        private bool Matches(RelationView v)
        { return string.IsNullOrWhiteSpace(_search.Text) || (v.Parent + " " + v.Child).IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0; }
        private void RenderGroups()
        {
            var selected = SelectedGroup(); _rendering = true; _parents.Rows.Clear();
            var byId = _views.ToDictionary(v => v.Record.Id);
            foreach (var g in BindingEditorState.Group(_views.Select(v => v.Record)).Select(g => g.Select(r => byId[r.Id]).ToList()).Where(g => g.Any(Matches)))
            {
                var row = _parents.Rows[_parents.Rows.Add(g.First().Parent, g.Count(), g.Any(v => v.Error != null) ? "需修复 " + g.Count(v => v.Error != null) : SavedState())];
                row.Tag = g.First().Record.Parent.Context; row.Cells[0].ToolTipText = g.First().Parent;
            }
            _parents.ClearSelection();
            var pick = _parents.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (string)r.Tag == selected) ?? _parents.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (pick != null) { pick.Selected = true; _parents.CurrentCell = pick.Cells[0]; }
            _rendering = false; _totals.Text = _views.Select(v => v.Record.Parent.Context).Distinct().Count() + " 个父组件 / " + _views.Count + " 条关系"; RenderDetails();
        }
        private void RenderDetails()
        {
            _details.Rows.Clear(); var group = SelectedGroup();
            foreach (var v in _views.Where(v => v.Record.Parent.Context == group && Matches(v)))
            {
                var parents = BindingEditorState.Parents(_session.Current.Relations, v.Record.Child);
                var row = _details.Rows[_details.Rows.Add(v.Child, parents.Count, v.Error ?? SavedState())]; row.Tag = v; row.Cells[0].ToolTipText = v.Child;
                row.Cells[1].ToolTipText = string.Join(System.Environment.NewLine, parents.Select(Display)); row.Cells[2].ToolTipText = v.Error;
            }
            _details.ClearSelection();
        }
        private string SavedState() { return _session.Pending ? "待保存" : "有效"; }
        private void RefreshSaveStatus()
        {
            var status = SavedState();
            foreach (DataGridViewRow row in _parents.Rows) if (!((string)row.Cells[2].Value).StartsWith("需修复") && (string)row.Cells[2].Value != status) row.Cells[2].Value = status;
            foreach (DataGridViewRow row in _details.Rows) if (((RelationView)row.Tag).Error == null && (string)row.Cells[2].Value != status) row.Cells[2].Value = status;
            _state.Text = (_session.Pending ? "已应用，待保存总装配。" : "当前关系已保存。") + (_session.LastSaveError ?? "") + "\r\n" + _hint +
                "\r\n未绑定不表示已确认空；修改后请保存并重新分类预览。";
        }
        private static string Label(string path, string assetId) { return (path ?? "").Split('/').LastOrDefault() + " | " + assetId + " | " + path; }
        private static string Display(BindingEndpoint e) { return Label(e.DisplayPath, e.AssetId); }
        private sealed class EndpointItem { public EndpointItem(BindingEndpoint e) { Endpoint = e; } public BindingEndpoint Endpoint; public override string ToString() { return Display(Endpoint); } }
        private sealed class RelationView { public InstanceBinding Record; public string Parent, Child, Error; }
    }
}
