using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class InstanceBindingDialog : Form
    {
        private readonly SldWorks _app;
        private readonly ModelDoc2 _document;
        private readonly SwInstanceBindings _session;
        private readonly SwBindingViewport _viewport;
        private readonly int _configurationId;
        private readonly BindingEditorState _draft = new BindingEditorState();
        private readonly TextBox _parentText = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly ListBox _childList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
        private readonly DataGridView _parents = Grid(false), _details = Grid(true);
        private readonly DataGridView _assetGroups = Grid(false), _assetRelations = Grid(false);
        private readonly TabControl _relationshipTabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage _instanceTab = new TabPage("实例绑定");
        private readonly CheckBox _continuous = new CheckBox { Text = "连续点选子组件", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly CheckBox _chain = new CheckBox { Text = "链式绑定", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly CheckBox _highlightOnPick = new CheckBox { Text = "点选时高亮关联组件", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly ComboBox _nextParent = new ComboBox { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        private readonly TextBox _search = new TextBox { Width = 180 };
        private readonly Label _state = new Label { Dock = DockStyle.Fill };
        private readonly Label _childCount = new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
        private readonly Label _totals = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly TableLayoutPanel _interfaceBanner = new TableLayoutPanel
            { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.MistyRose, Visible = false };
        private readonly Label _interfaceSummary = new Label
            { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed };
        private readonly Button _interfaceIssues;
        private readonly Button _assetIsolation;
        private readonly Timer _timer = new Timer { Interval = 250 };
        private readonly DAssemblyDocEvents_Event _selectionEvents;
        private readonly List<Component2> _selectionQueue = new List<Component2>();
        private readonly HashSet<string> _queuedSelectionNames = new HashSet<string>(StringComparer.Ordinal);
        private List<RelationView> _views = new List<RelationView>();
        private bool _busy, _rendering;
        private long _displayedRevision;
        internal bool Busy { get { return _busy; } }
        internal Func<bool> McpRunning { get; set; }
        private string _displayedSavedState;
        private bool _relationsValidated;
        private ILookup<string, InstanceBinding> _relationsByEndpoint;
        private bool _highlightPending, _hasAutomaticSelection, _assetsIsolated;
        private string _lastPickedSelection;
        private readonly HashSet<string> _pickedRelationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _pickedParentContexts = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pickedRoles = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pickedAssetRoles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Color> _roleTints = new Dictionary<int, Color>();
        private string _pickedAssetId;
        private string _pickedAssetName;
        private IList<InstanceBinding> _visualizedAssetRelations;
        private Dictionary<string, string> _visualizedAssetIds;
        private string _highlightHint;
        private const string ParentPickHint = "在 SOLIDWORKS 中选一个父资产，再点击“读取父组件”。";
        private string _hint = ParentPickHint;

        public InstanceBindingDialog(SldWorks app)
        {
            _app = app; _document = app.ActiveDoc as ModelDoc2;
            _session = SwInstanceBindings.ForDocument(_document);
            if (_session == null) throw new ValidationException("请先打开一个可写的总装配体。");
            _session.RequireWritable();
            _viewport = new SwBindingViewport(_document);
            var current = _session.Current;
            _configurationId = current.ConfigurationId;
            Text = "承载关系 · " + _document.GetTitle() + " · " + current.ConfigurationName;
            Size = new Size(1040, 700); MinimumSize = new Size(840, 590);
            StartPosition = FormStartPosition.Manual; Font = new Font("Microsoft YaHei UI", 9F);
            var frame = app.IFrameObject();
            var workArea = Screen.FromHandle(frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWndx64())).WorkingArea;
            Location = new Point(Math.Max(workArea.Left, workArea.Right - Width), workArea.Top);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(14) };
            foreach (var height in new[] { 32, 42, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.Controls.Add(new Label { Text = "在 SOLIDWORKS 中选父组件。子组件可 Ctrl 多选或在图形区框选，再点击“添加所选子组件”。", Dock = DockStyle.Fill }, 0, 0);
            var parentBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            parentBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); parentBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parentBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            parentBar.Controls.Add(Button("读取父组件", CaptureParent)); parentBar.Controls.Add(_parentText);
            parentBar.Controls.Add(Button("接口属性 / 建议…", EditInterfaces, false));
            layout.Controls.Add(parentBar, 0, 1);
            var childBar = Bar(); childBar.Controls.Add(_continuous);
            childBar.Controls.Add(Button("添加所选子组件", CaptureChildren)); childBar.Controls.Add(Button("同级同款批量添加", CaptureSiblings));
            childBar.Controls.Add(Button("移除选中子项", delegate { foreach (var e in _childList.SelectedItems.Cast<EndpointItem>().ToArray()) _draft.Children.Remove(e.Endpoint); RefreshDraft(); }));
            childBar.Controls.Add(Button("清空本轮", delegate { _draft.Clear(); _continuous.Checked = false; _document.ClearSelection2(true); _hint = ParentPickHint; RefreshDraft(); }));
            childBar.Controls.Add(_childCount); layout.Controls.Add(childBar, 0, 2); layout.Controls.Add(_childList, 0, 3);
            var applyBar = Bar(); applyBar.Controls.Add(Button("应用绑定", () => Apply(false)));
            applyBar.Controls.Add(Button("原位复制并绑定…", DuplicateSelected));
            applyBar.Controls.Add(Button("替换全部父组件…", () => Apply(true))); applyBar.Controls.Add(_chain);
            applyBar.Controls.Add(new Label { Text = "下一父组件", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }); applyBar.Controls.Add(_nextParent);
            applyBar.Controls.Add(Button("将所选子项作为父", ContinueFromSelected)); layout.Controls.Add(applyBar, 0, 4);
            var filterBar = Bar(); filterBar.Controls.Add(new Label { Text = "搜索父 / 子", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); filterBar.Controls.Add(_search);
            filterBar.Controls.Add(Button("查看全部", delegate { _search.Clear(); }));
            var appendToInstance = Button("向所选父组件添加子项", AppendToSelectedParent);
            filterBar.Controls.Add(appendToInstance);
            filterBar.Controls.Add(Button("校验已有关系", ValidateRelations));
            _assetIsolation = Button("仅显示普通资产", ToggleAssetIsolation);
            filterBar.Controls.Add(_assetIsolation);
            layout.Controls.Add(filterBar, 0, 5);
            var summary = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 3 };
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(_totals, 0, 0);
            _highlightOnPick.Padding = Padding.Empty;
            summary.Controls.Add(_highlightOnPick, 1, 0);
            var selectionLegend = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            selectionLegend.Controls.Add(RoleLegend("点中实例", SwBindingHighlight.ClickedMark));
            selectionLegend.Controls.Add(RoleLegend("实例父级", SwBindingHighlight.ParentMark));
            selectionLegend.Controls.Add(RoleLegend("实例子级", SwBindingHighlight.ChildMark));
            selectionLegend.Controls.Add(RoleLegend("同资产其他实例", SwBindingHighlight.AssetMark));
            selectionLegend.Controls.Add(RoleLegend("资产父级", SwBindingHighlight.AssetParentMark));
            selectionLegend.Controls.Add(RoleLegend("资产子级", SwBindingHighlight.AssetChildMark));
            summary.Controls.Add(selectionLegend, 0, 1);
            summary.SetColumnSpan(selectionLegend, 2);
            _interfaceIssues = Button("定位接口冲突", ShowInterfaceIssues, false);
            _interfaceIssues.ForeColor = Color.DarkRed;
            _interfaceIssues.Margin = new Padding(4);
            _interfaceBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _interfaceBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _interfaceBanner.Controls.Add(_interfaceSummary, 0, 0);
            _interfaceBanner.Controls.Add(_interfaceIssues, 1, 0);
            summary.Controls.Add(_interfaceBanner, 0, 2);
            summary.SetColumnSpan(_interfaceBanner, 2);
            layout.Controls.Add(summary, 0, 6);
            _parents.Columns.Add("parent", "父组件（每个实例一行）"); _parents.Columns.Add("count", "子组件数"); _parents.Columns.Add("status", "状态");
            _parents.Columns[0].FillWeight = 65; _parents.Columns[1].FillWeight = 15; _parents.Columns[2].FillWeight = 20;
            _parents.Columns[1].MinimumWidth = 74;
            _details.Columns.Add("child", "子组件明细（可多选）"); _details.Columns.Add("parents", "父级数"); _details.Columns.Add("status", "状态");
            _details.Columns[0].FillWeight = 65; _details.Columns[1].FillWeight = 12; _details.Columns[2].FillWeight = 23;
            _details.Columns[1].MinimumWidth = 62;
            _parents.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _details.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            var assetTab = new TabPage("资产关系");
            _assetGroups.Columns.Add("asset", "资产（UUID / 版本）");
            _assetGroups.Columns.Add("instances", "关联实例数");
            _assetGroups.Columns[0].FillWeight = 78; _assetGroups.Columns[1].FillWeight = 22;
            _assetRelations.Columns.Add("role", "方向");
            _assetRelations.Columns.Add("asset", "关联资产");
            _assetRelations.Columns.Add("count", "实例关系数");
            _assetRelations.Columns[0].FillWeight = 16; _assetRelations.Columns[1].FillWeight = 65;
            _assetRelations.Columns[2].FillWeight = 19;
            assetTab.Controls.Add(RelationshipSplit(_assetGroups, _assetRelations));
            _instanceTab.Controls.Add(RelationshipSplit(_parents, _details));
            _relationshipTabs.TabPages.Add(assetTab);
            _relationshipTabs.TabPages.Add(_instanceTab);
            layout.Controls.Add(_relationshipTabs, 0, 7);
            var editBar = Bar(); editBar.Controls.Add(Button("高亮所选子关系", delegate { Highlight(SelectedRecords()); }));
            editBar.Controls.Add(Button("解除所选子关系", delegate { Unbind(false); })); editBar.Controls.Add(Button("解除整组关系…", delegate { Unbind(true); }));
            editBar.Controls.Add(Button("将所选子项加入本轮", LoadSelectedChildren));
            editBar.Controls.Add(Button("复用所选槽位…", ReuseSelectedSlot)); layout.Controls.Add(editBar, 0, 8);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.Controls.Add(Button("保存总装配", SaveAssembly)); footer.Controls.Add(_state); layout.Controls.Add(footer, 0, 9);
            Controls.Add(layout);
            Action showInstanceActions = () =>
            {
                var editInstances = _relationshipTabs.SelectedTab == _instanceTab;
                appendToInstance.Visible = editInstances;
                editBar.Visible = editInstances;
                layout.RowStyles[8].Height = editInstances ? 42 : 0;
            };
            _relationshipTabs.SelectedIndexChanged += delegate { showInstanceActions(); };
            showInstanceActions();
            _assetGroups.SelectionChanged += delegate { if (!_rendering) RenderAssetRelations(); };
            _parents.SelectionChanged += delegate { if (!_rendering) RenderDetails(); };
            _details.CellDoubleClick += delegate { Guard(delegate { Highlight(SelectedRecords()); }); };
            _search.TextChanged += delegate { RenderGroups(); RenderAssetGroups(); };
            _continuous.CheckedChanged += delegate
            {
                ClearSelectionQueue();
                if (_continuous.Checked) ResetPickedHighlight();
                if (_continuous.Checked && _draft.Parent == null)
                {
                    _continuous.Checked = false;
                    _hint = ParentPickHint;
                }
            };
            _highlightOnPick.CheckedChanged += delegate
            {
                if (_highlightOnPick.Checked)
                {
                    _highlightPending = true;
                    _hint = "点选普通资产可查看同资产实例及直接承载关系。";
                }
                else ResetPickedHighlight();
            };
            _chain.CheckedChanged += delegate { if (!_chain.Checked && _draft.NextParents.Count > 0) { _draft.Clear(); _hint = ParentPickHint; RefreshDraft(); } };
            _nextParent.SelectionChangeCommitted += delegate { Guard(delegate {
                var item = _nextParent.SelectedItem as EndpointItem; if (item == null) return;
                _draft.ContinueFrom(item.Endpoint); _document.ClearSelection2(true); RefreshDraft();
                _hint = "已接续父组件，可继续点选下一层子组件。";
            }); };
            _timer.Tick += delegate
            {
                if (_busy || (McpRunning != null && McpRunning())) return;
                try
                {
                    if (_assetsIsolated && !IsCurrentContext()) RestoreAssetDisplay();
                    if (_displayedRevision != _session.Revision && IsCurrentContext())
                        RefreshRelations(_session.Current.Relations);
                    if (Enabled && _continuous.Checked) PollSelection();
                    else if (Enabled && _highlightOnPick.Checked && Control.MouseButtons == MouseButtons.None)
                    {
                        if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) == Keys.None) RefreshPickedHighlight();
                        else if (_hasAutomaticSelection) ResetPickedHighlight();
                    }
                    else if (_hasAutomaticSelection && !IsCurrentContext()) ResetPickedHighlight();
                    RefreshSaveStatus();
                }
                catch (Exception ex)
                {
                    _continuous.Checked = false;
                    _hint = ex.Message;
                    _state.Text = ex.Message;
                }
            };
            _selectionEvents = _document as DAssemblyDocEvents_Event;
            if (_selectionEvents != null)
            {
                _selectionEvents.NewSelectionNotify += SelectionChanged;
                _selectionEvents.ClearSelectionsNotify += SelectionChanged;
                _selectionEvents.UserSelectionPostNotify += UserSelectionCompleted;
            }
            FormClosed += delegate
            {
                _timer.Dispose();
                ResetPickedHighlight();
                _viewport.Dispose();
                if (_assetsIsolated) RestoreAssetDisplay();
                if (_selectionEvents != null)
                {
                    try
                    {
                        _selectionEvents.NewSelectionNotify -= SelectionChanged;
                        _selectionEvents.ClearSelectionsNotify -= SelectionChanged;
                        _selectionEvents.UserSelectionPostNotify -= UserSelectionCompleted;
                    }
                    catch (COMException) { }
                }
            };
            RefreshDraft();
            RefreshRelations(current.Relations);
            _timer.Start();
        }
        private static DataGridView Grid(bool multi)
        {
            return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = multi, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, RowTemplate = { Height = 32 } };
        }

        private static Control RelationshipSplit(Control left, Control right)
        {
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            split.Controls.Add(left, 0, 0);
            split.Controls.Add(right, 1, 0);
            return split;
        }
        private FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true }; }
        private Button Button(string text, Action action, bool resetHighlight = true)
        { var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 0) }; b.Click += delegate { Guard(action, resetHighlight); }; return b; }
        private bool IsCurrentContext()
        {
            var active = _app.ActiveDoc as ModelDoc2;
            var configuration = Equals(active, _document) ? _document.ConfigurationManager.ActiveConfiguration : null;
            return configuration != null && configuration.GetID() == _configurationId;
        }
        private void EnsureContext()
        {
            if (!IsCurrentContext())
                throw new ValidationException("活动总装配或配置已切换，请关闭面板后重新打开。");
        }
        private void Guard(Action action, bool resetHighlight = true)
        {
            if (_busy || (McpRunning != null && McpRunning())) return;
            _busy = true;
            try { EnsureContext(); if (resetHighlight) ResetPickedHighlight(); action(); }
            catch (Exception ex) { _continuous.Checked = false; MessageBox.Show(this, ex.Message, "承载关系", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _busy = false; }
        }

        private SwBindingEndpoints Endpoints() { return new SwBindingEndpoints(_app, _session); }

        private List<Component2> CurrentSelection()
        {
            var selection = _document.SelectionManager as SelectionMgr;
            if (selection == null) throw new ValidationException("无法读取 SOLIDWORKS 当前选择。");
            return ManualSelection(selection).ToList();
        }

        private IEnumerable<Component2> ManualSelection(SelectionMgr selection)
        {
            return ManualSelectionIndices(selection)
                .Select(index => SwBindingEndpoints.SelectedComponent(selection, index));
        }

        private static IEnumerable<int> ManualSelectionIndices(SelectionMgr selection)
        {
            return Enumerable.Range(1, selection.GetSelectedObjectCount2(-1))
                .Where(index => !SwBindingHighlight.IsAutomaticMark(selection.GetSelectedObjectMark(index)));
        }

        private void CaptureParent()
        {
            var components = CurrentSelection();
            if (components.Count == 0) throw new ValidationException(ParentPickHint);
            var operation = Endpoints();
            ExportNode parent = null;
            foreach (var component in components)
            {
                var asset = operation.FindSelectedAsset(component);
                if (parent != null && parent.Id != asset.Id)
                    throw new ValidationException("请选择一个父资产实例。");
                parent = asset;
            }
            var captured = operation.Capture(parent);
            _draft.SetParent(captured);
            _draft.Children.RemoveAll(child => child.Context == captured.Context);
            ClearSelectionQueue();
            _document.ClearSelection2(true);
            RefreshDraft();
            _hint = "父组件已选定。Ctrl 多选或框选子组件后，点击“添加所选子组件”。";
        }

        private void AppendToSelectedParent()
        {
            var group = SelectedGroup();
            var records = _session.Current.Relations.Where(r => r.Parent.Context == group).ToList();
            if (records.Count == 0) throw new ValidationException("请在已有关系列表中选择一个父组件。");
            if (_draft.Parent != null && _draft.Parent.Context != group && _draft.Children.Count != 0)
                throw new ValidationException("当前还有另一父组件的待绑定子项，请先应用绑定或清空本轮。");

            var operation = Endpoints();
            var parent = operation.Capture(operation.Resolve(records[0].Parent));
            var selection = CurrentSelection();
            var alreadyBound = new HashSet<string>(records.Select(r => r.Child.Context), StringComparer.Ordinal);
            List<string> ignored;
            var children = CaptureSelected(operation, selection, out ignored);
            var additions = children.Where(child => child.Context != parent.Context && !alreadyBound.Contains(child.Context)).ToList();

            _continuous.Checked = false;
            _draft.SetParent(parent);
            _draft.Children.RemoveAll(child => child.Context == parent.Context || alreadyBound.Contains(child.Context));
            if (additions.Count > 0) Append(additions);
            else RefreshDraft();
            _hint = _draft.Children.Count > 0 ? "已选好新增子项，点击“应用绑定”提交。" :
                "已读取已有父组件。请在 SOLIDWORKS 中选择新子项，再点击“添加所选子组件”。";
            if (children.Count > additions.Count) _hint += " 已跳过已有子项或父组件本身。";
            if (ignored.Count > 0) _hint += " " + ignored[0];
        }

        private int SelectionChanged()
        {
            if (_busy || (McpRunning != null && McpRunning()) || !Enabled || !_continuous.Checked || _draft.Parent == null) return 0;
            try
            {
                var selection = _document.SelectionManager as SelectionMgr;
                // A completed multi-selection is read once on the timer. Single clicks can
                // replace each other before then, so retain their component occurrences.
                if (selection != null && ManualSelectionIndices(selection).Take(2).Count() == 1)
                {
                    var component = ManualSelection(selection).FirstOrDefault();
                    if (component != null && _queuedSelectionNames.Add(component.Name2))
                        _selectionQueue.Add(component);
                }
            }
            catch (COMException) { }
            return 0;
        }

        private int UserSelectionCompleted()
        {
            if (!_busy && (McpRunning == null || !McpRunning()) && Enabled && !_continuous.Checked && _highlightOnPick.Checked)
                _highlightPending = true;
            return 0;
        }

        private void RefreshPickedHighlight()
        {
            if (!IsCurrentContext())
            {
                ResetPickedHighlight();
                return;
            }
            var selection = _document.SelectionManager as SelectionMgr;
            if (selection == null) return;
            var picks = ReadManualPicks(selection);
            var selectionKey = SelectionKey(picks);
            if (!_highlightPending && selectionKey == _lastPickedSelection &&
                (!_hasAutomaticSelection || SwBindingHighlight.HasSelection(selection)))
                return;

            _busy = true;
            try
            {
                ResetPickedHighlight();
                _lastPickedSelection = selectionKey;
                if (picks.Count == 0) return;

                var operation = Endpoints();
                var assets = new List<ExportNode>();
                var selectedIds = new HashSet<string>(StringComparer.Ordinal);
                var normalize = false;
                string ignored = null;
                foreach (var pick in picks)
                {
                    try
                    {
                        var asset = operation.FindSelectedAsset(pick.Component);
                        if (selectedIds.Add(asset.Id)) assets.Add(asset);
                        else normalize = true;
                        if (!pick.WholeComponent || pick.Name != asset.Name) normalize = true;
                    }
                    catch (ValidationException ex) { normalize = true; if (ignored == null) ignored = ex.Message; }
                    catch (COMException) { normalize = true; if (ignored == null) ignored = "无法读取所选组件。"; }
                }
                if (normalize)
                {
                    _document.ClearSelection2(true);
                    var selected = SwBindingHighlight.SelectMany(_document, selection, assets.Select(AssetComponent), 0);
                    _lastPickedSelection = SelectionKey(ReadManualPicks(selection));
                    if (selected != assets.Count)
                    {
                        _highlightHint = "有资产实例无法在图形区选中。";
                        return;
                    }
                }
                if (assets.Count == 1) HighlightPickedAsset(selection, operation, assets[0]);
                if (ignored != null)
                    _highlightHint = (_highlightHint == null ? string.Empty : _highlightHint + " ") + ignored;
            }
            finally { _busy = false; }
        }

        private static List<ManualPick> ReadManualPicks(SelectionMgr selection)
        {
            var picks = new List<ManualPick>();
            var normalCount = selection.GetSelectedObjectCount2(0);
            var onlyNormalAndAutomatic = normalCount + SwBindingHighlight.AutomaticCount(selection) == selection.GetSelectedObjectCount2(-1);
            var mark = onlyNormalAndAutomatic ? 0 : -1;
            var indices = onlyNormalAndAutomatic ? Enumerable.Range(1, normalCount) : ManualSelectionIndices(selection);
            foreach (var index in indices)
            {
                var whole = selection.GetSelectedObject6(index, mark) as Component2;
                var component = whole ?? SwBindingEndpoints.SelectedComponent(selection, index, mark);
                picks.Add(new ManualPick { Component = component, WholeComponent = whole != null,
                    Name = component == null ? string.Empty : component.Name2 });
            }
            return picks;
        }

        private static string SelectionKey(IEnumerable<ManualPick> picks)
        {
            return string.Join("\0", picks.Select(pick => (pick.WholeComponent ? "component:" : "entity:") + pick.Name));
        }

        private static Component2 AssetComponent(ExportNode asset) { return ((SwCadNode)asset.Source).Component; }

        private void HighlightPickedAsset(SelectionMgr selection, SwBindingEndpoints operation, ExportNode asset)
        {
            var context = SwInstanceBindings.Context(AssetComponent(asset));
            var occurrences = new Dictionary<string, ExportNode>(StringComparer.Ordinal) { { context, asset } };
            foreach (var occurrence in operation.SameAssetInstances(asset))
                occurrences[SwInstanceBindings.Context(AssetComponent(occurrence))] = occurrence;

            var relations = new List<InstanceBinding>();
            foreach (var relation in occurrences.Keys.SelectMany(key => _relationsByEndpoint[key]).Distinct().ToList())
            {
                var parent = ResolveHighlightAsset(operation, relation.Parent);
                var child = ResolveHighlightAsset(operation, relation.Child);
                if (parent == null || child == null || (parent.AssetId != asset.AssetId && child.AssetId != asset.AssetId))
                    continue;
                occurrences[relation.Parent.Context] = parent;
                occurrences[relation.Child.Context] = child;
                relations.Add(relation);
            }

            var relatedAssets = occurrences.Values.Where(node => node.AssetId != asset.AssetId)
                .GroupBy(node => node.AssetId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            if (relatedAssets.Length > 0)
            {
                foreach (var occurrence in operation.SameAssetInstances(relatedAssets))
                    occurrences[SwInstanceBindings.Context(AssetComponent(occurrence))] = occurrence;
            }

            var visualization = BindingEditorState.AssetVisualization(
                occurrences.Select(pair => new BindingAssetOccurrence(pair.Key, pair.Value.AssetId)), context, relations);
            foreach (var pair in visualization.Roles)
                _pickedRoles[pair.Key] = HighlightMark(pair.Value);

            var components = occurrences.ToDictionary(pair => pair.Key, pair => AssetComponent(pair.Value), StringComparer.Ordinal);
            var expected = _pickedRoles.Count - 1;
            var highlighted = PaintHighlight(selection, components, context);

            _pickedAssetId = asset.AssetId;
            _pickedAssetName = System.IO.Path.GetFileNameWithoutExtension(((SwCadNode)asset.Source).ClassificationModel.FileName);
            _visualizedAssetRelations = visualization.AssetRelations;
            _visualizedAssetIds = occurrences.ToDictionary(pair => pair.Key, pair => pair.Value.AssetId, StringComparer.Ordinal);
            _pickedAssetRoles[asset.AssetId] = SwBindingHighlight.ClickedMark;
            foreach (var relation in _visualizedAssetRelations.Where(relation => _visualizedAssetIds[relation.Child.Context] == asset.AssetId))
            {
                var parentId = _visualizedAssetIds[relation.Parent.Context];
                if (!_pickedAssetRoles.ContainsKey(parentId))
                    _pickedAssetRoles[parentId] = SwBindingHighlight.AssetParentMark;
            }
            foreach (var relation in _visualizedAssetRelations.Where(relation => _visualizedAssetIds[relation.Parent.Context] == asset.AssetId))
            {
                var childId = _visualizedAssetIds[relation.Child.Context];
                if (!_pickedAssetRoles.ContainsKey(childId))
                    _pickedAssetRoles[childId] = SwBindingHighlight.AssetChildMark;
            }
            RenderAssetGroups(asset.AssetId);
            FollowPickedRelations(visualization.DirectRelations, context);
            var direct = visualization.DirectRelations.Count;
            var assetRelations = visualization.AssetRelations.Count;
            _highlightHint = "同资产 " + occurrences.Values.Count(node => node.AssetId == asset.AssetId) +
                " 个实例，当前实例 " + direct + " 条直接关系，资产共 " + assetRelations + " 条实例关系。";
            if (highlighted < expected) _highlightHint += " " + (expected - highlighted) + " 个关联实例当前无法选中。";
        }

        private static int HighlightMark(BindingAssetRole role)
        {
            switch (role)
            {
                case BindingAssetRole.Clicked: return SwBindingHighlight.ClickedMark;
                case BindingAssetRole.DirectParent: return SwBindingHighlight.ParentMark;
                case BindingAssetRole.DirectChild: return SwBindingHighlight.ChildMark;
                case BindingAssetRole.SameAsset: return SwBindingHighlight.AssetMark;
                case BindingAssetRole.AssetParent: return SwBindingHighlight.AssetParentMark;
                case BindingAssetRole.AssetChild: return SwBindingHighlight.AssetChildMark;
                default: throw new ArgumentOutOfRangeException("role");
            }
        }

        private void AddHighlightRole(SwBindingEndpoints operation, IEnumerable<BindingEndpoint> endpoints, int mark,
            IDictionary<string, Component2> components)
        {
            foreach (var group in endpoints.GroupBy(endpoint => endpoint.Context, StringComparer.Ordinal))
            {
                if (_pickedRoles.ContainsKey(group.Key)) continue;
                var asset = group.Select(endpoint => ResolveHighlightAsset(operation, endpoint)).FirstOrDefault(node => node != null);
                _pickedRoles[group.Key] = mark;
                components[group.Key] = asset == null ? null : AssetComponent(asset);
            }
        }

        private int PaintHighlight(SelectionMgr selection, IDictionary<string, Component2> components, string manualContext = null)
        {
            _hasAutomaticSelection = true;
            var highlighted = 0;
            foreach (var group in _pickedRoles.Where(pair => pair.Key != manualContext).GroupBy(pair => pair.Value))
                highlighted += SwBindingHighlight.SelectMany(_document, selection, group.Select(pair => components[pair.Key]), group.Key);
            _hasAutomaticSelection = SwBindingHighlight.HasSelection(selection);
            var outlined = _viewport.Show(_pickedRoles.Where(pair => components[pair.Key] != null).Select(pair =>
                new KeyValuePair<Component2, Color>(components[pair.Key], SwBindingHighlight.GetRoleColor(_app, pair.Value))));
            if (_hasAutomaticSelection && outlined == 0) _document.GraphicsRedraw2();
            return highlighted;
        }


        private void FollowPickedRelations(IList<InstanceBinding> relations, string endpointContext)
        {
            foreach (var relation in relations)
            {
                _pickedRelationIds.Add(relation.Id);
                _pickedParentContexts.Add(relation.Parent.Context);
            }

            var parents = _parents.Rows.Cast<DataGridViewRow>()
                .Where(row => _pickedParentContexts.Contains((string)row.Tag)).ToList();
            var preferred = parents.FirstOrDefault(row => (string)row.Tag == endpointContext) ??
                parents.FirstOrDefault(row => row.Selected) ?? parents.FirstOrDefault();
            if (preferred != null && !preferred.Selected)
            {
                _rendering = true;
                try
                {
                    _parents.ClearSelection();
                    _parents.CurrentCell = preferred.Cells[0];
                    preferred.Selected = true;
                }
                finally { _rendering = false; }
                RenderDetails();
            }
            StylePickedRows();
            var firstMatch = _details.Rows.Cast<DataGridViewRow>().FirstOrDefault(row =>
                _pickedRelationIds.Contains(((RelationView)row.Tag).Record.Id));
            if (firstMatch != null && !firstMatch.Displayed)
                _details.FirstDisplayedScrollingRowIndex = firstMatch.Index;
        }

        private void StylePickedRows()
        {
            foreach (DataGridViewRow row in _parents.Rows)
                StyleRole(row, (string)row.Tag);
            foreach (DataGridViewRow row in _details.Rows)
                StylePickedChild(row, (RelationView)row.Tag);
        }

        private void StylePickedChild(DataGridViewRow row, RelationView view)
        {
            StyleRole(row, view.Record.Child.Context);
        }

        private void StyleRole(DataGridViewRow row, string context)
        {
            int mark;
            ColorRow(row, _pickedRoles.TryGetValue(context, out mark) ? mark : 0);
        }

        private void ColorRow(DataGridViewRow row, int mark)
        {
            var color = mark == 0 ? Color.Empty : RoleTint(mark);
            row.DefaultCellStyle.BackColor = color;
            row.DefaultCellStyle.SelectionBackColor = color;
            row.DefaultCellStyle.SelectionForeColor = mark == 0 ? Color.Empty : SystemColors.WindowText;
        }

        private Control RoleLegend(string text, int mark)
        {
            return new Label { Text = text, AutoSize = true, BackColor = RoleTint(mark),
                Padding = new Padding(5, 2, 5, 2), Margin = new Padding(5, 1, 0, 0) };
        }

        private Color RoleTint(int mark)
        {
            Color tint;
            if (_roleTints.TryGetValue(mark, out tint)) return tint;
            var color = SwBindingHighlight.GetRoleColor(_app, mark);
            tint = Color.FromArgb((color.R + 2 * 255) / 3, (color.G + 2 * 255) / 3, (color.B + 2 * 255) / 3);
            _roleTints.Add(mark, tint);
            return tint;
        }

        private static Component2 ResolveHighlightComponent(SwBindingEndpoints operation, IEnumerable<BindingEndpoint> endpoints)
        {
            var asset = endpoints.Select(endpoint => ResolveHighlightAsset(operation, endpoint)).FirstOrDefault(node => node != null);
            return asset == null ? null : AssetComponent(asset);
        }

        private static ExportNode ResolveHighlightAsset(SwBindingEndpoints operation, BindingEndpoint endpoint)
        {
            try { return operation.ResolveVisualization(endpoint); }
            catch (ValidationException) { }
            catch (COMException) { }
            return null;
        }

        private void ResetPickedHighlight()
        {
            _highlightPending = false;
            _viewport.Clear();
            _highlightHint = null;
            _lastPickedSelection = null;
            var hadAssetPick = _pickedAssetId != null;
            _pickedAssetId = null;
            _pickedAssetName = null;
            _visualizedAssetIds = null;
            _visualizedAssetRelations = null;
            _pickedAssetRoles.Clear();
            if (hadAssetPick) RenderAssetGroups();
            if (_pickedRoles.Count > 0 || _pickedRelationIds.Count > 0)
            {
                _pickedRoles.Clear();
                _pickedRelationIds.Clear();
                _pickedParentContexts.Clear();
                StylePickedRows();
            }
            if (!_hasAutomaticSelection) return;
            var wasBusy = _busy;
            _busy = true;
            try
            {
                var selection = _document.SelectionManager as SelectionMgr;
                if (selection == null) return;
                SwBindingHighlight.Clear(selection);
                _hasAutomaticSelection = SwBindingHighlight.HasSelection(selection);
            }
            catch (COMException) { }
            finally { _busy = wasBusy; }
        }

        private void ToggleAssetIsolation()
        {
            if (_assetsIsolated)
            {
                if (!RestoreAssetDisplay())
                    throw new ValidationException("SOLIDWORKS 尚未退出隔离。可在图形区点击“退出隔离”。");
                _hint = "已恢复装配显示。";
                return;
            }

            var operation = Endpoints();
            var assets = operation.ReadVisualization().Assets.Select(AssetComponent).ToList();
            if (assets.Count == 0) throw new ValidationException("当前配置没有可显示的普通资产。");
            var selection = _document.SelectionManager as SelectionMgr;
            if (selection == null) throw new ValidationException("无法读取 SOLIDWORKS 当前选择。");
            List<string> ignored;
            var previous = CaptureSelected(operation, ManualSelection(selection), out ignored);
            var assembly = (AssemblyDoc)_document;
            try
            {
                _document.ClearSelection2(true);
                if (SwBindingHighlight.SelectMany(_document, selection, assets, 0) != assets.Count || !assembly.Isolate())
                    throw new ValidationException("SOLIDWORKS 无法隔离当前配置的资产实例。");
                _assetsIsolated = true;
                assembly.SetIsolateVisibility((int)swIsolateVisibility_e.swIsolateVisibility_HIDDEN);
                _assetIsolation.Text = "恢复全部组件";
                _hint = "当前仅显示普通资产。";
            }
            catch
            {
                if (_assetsIsolated) RestoreAssetDisplay();
                throw;
            }
            finally
            {
                _document.ClearSelection2(true);
                SwBindingHighlight.SelectMany(_document, selection,
                    previous.Select(endpoint => ResolveHighlightComponent(operation, new[] { endpoint })), 0);
                _highlightPending = true;
            }
        }

        private bool RestoreAssetDisplay()
        {
            try
            {
                if (!((AssemblyDoc)_document).ExitIsolate()) return false;
            }
            catch (COMException) { return false; }
            _assetsIsolated = false;
            _assetIsolation.Text = "仅显示普通资产";
            return true;
        }

        private void ClearSelectionQueue()
        {
            _selectionQueue.Clear();
            _queuedSelectionNames.Clear();
        }

        private void PollSelection()
        {
            if (_draft.Parent == null || (Control.MouseButtons & MouseButtons.Left) != 0) return;
            var selection = _document.SelectionManager as SelectionMgr;
            var count = selection == null ? 0 : selection.GetSelectedObjectCount2(-1);
            if (count == 0 && _selectionQueue.Count == 0) return;
            _busy = true;
            try
            {
                EnsureContext();
                var components = selection == null ? _selectionQueue.ToList() : _selectionQueue.Concat(ManualSelection(selection)).ToList();
                ClearSelectionQueue();
                CaptureChildren(components);
            }
            finally { _busy = false; }
        }

        private static List<BindingEndpoint> CaptureSelected(SwBindingEndpoints operation,
            IEnumerable<Component2> components, out List<string> ignored)
        {
            ignored = new List<string>();
            var componentNames = new HashSet<string>(StringComparer.Ordinal);
            var assetIds = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<BindingEndpoint>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    ignored.Add("选择中有对象不属于装配组件。");
                    continue;
                }
                try
                {
                    if (!componentNames.Add(component.Name2)) continue;
                    var asset = operation.FindSelectedAsset(component);
                    if (assetIds.Add(asset.Id)) selected.Add(operation.Capture(asset));
                }
                catch (ValidationException ex) { ignored.Add(ex.Message); }
                catch (COMException ex) { ignored.Add("无法读取组件实例：" + ex.Message); }
            }
            return selected;
        }

        private void Append(IList<BindingEndpoint> selected)
        {
            if (selected.Count == 0) throw new ValidationException("请至少选择一个普通子资产。");
            _draft.AddChildren(selected);
            ClearSelectionQueue();
            _document.ClearSelection2(true);
            RefreshDraft();
            _hint = "已累计 " + _draft.Children.Count + " 个子组件。";
        }

        private void CaptureChildren()
        {
            var components = _selectionQueue.Concat(CurrentSelection()).ToList();
            CaptureChildren(components);
        }

        private void CaptureChildren(List<Component2> components)
        {
            if (_draft.Parent == null) throw new ValidationException(ParentPickHint);
            if (components.Count == 0) throw new ValidationException("请先在 SOLIDWORKS 中多选或框选子组件。");
            List<string> ignored;
            var children = CaptureSelected(Endpoints(), components, out ignored);
            if (children.Count == 0) throw new ValidationException(ignored.FirstOrDefault() ?? "当前选择中没有可绑定的普通子资产。");
            Append(children);
            if (ignored.Count > 0) _hint += " 已跳过 " + ignored.Count + " 项：" + ignored[0];
        }

        private void CaptureSiblings()
        {
            if (_draft.Parent == null) throw new ValidationException(ParentPickHint);
            var operation = Endpoints();
            List<string> ignored;
            var seeds = CaptureSelected(operation, CurrentSelection(), out ignored);
            if (seeds.Count == 0) seeds = _childList.SelectedItems.Cast<EndpointItem>().Select(i => i.Endpoint).ToList();
            if (seeds.Count == 0 && _draft.Children.Count > 0) seeds.Add(_draft.Children.Last());
            if (seeds.Count == 0) throw new ValidationException(ignored.FirstOrDefault() ?? "请先选中一个子资产作为同级批量添加的起点。");
            var matches = seeds.GroupBy(s => new { Owner = BindingEditorState.OwnerContext(s.Context), s.AssetId, s.SourcePath })
                .SelectMany(g => operation.Siblings(g.First())).ToList();
            Append(matches);
        }

        private void RefreshDraft()
        {
            _parentText.Text = _draft.Parent == null ? "" : Display(_draft.Parent);
            _childList.BeginUpdate(); _childList.Items.Clear(); foreach (var e in _draft.Children) _childList.Items.Add(new EndpointItem(e)); _childList.EndUpdate();
            _childCount.Text = "待绑定：" + _draft.Children.Count;
            _nextParent.Items.Clear(); foreach (var e in _draft.NextParents) _nextParent.Items.Add(new EndpointItem(e));
            _nextParent.Enabled = _draft.NextParents.Count > 0; _nextParent.SelectedIndex = -1;
        }
        private void ReuseSelectedSlot()
        {
            var selected = SelectedRecords();
            if (selected.Count != 1) throw new ValidationException("请在实例明细中选择一条已有关系作为槽位参考。");
            Apply(false, selected[0]);
        }

        private void DuplicateSelected()
        {
            if (_draft.Parent == null || _draft.Children.Count != 1)
                throw new ValidationException("请读取目标父组件，并只选一个要原位复制的子资产实例。");
            var plan = _session.PrepareDuplicate(_app, _draft.Parent, _draft.Children[0], null);
            var preview = "在当前配置的总装层原位复制并保存：\n" + Display(plan.Original) + "\n目标父组件：" +
                Display(plan.Parent) + (plan.ReplacedId == null ? "" : "\n复制后迁移目标父组件的那条旧关系，沿用原槽位。");
            if (MessageBox.Show(this, preview, "确认原位复制并绑定", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            EnsureContext();
            var result = _session.ApplyDuplicate(_app, plan);
            _draft.Clear();
            _continuous.Checked = false;
            _document.ClearSelection2(true);
            RefreshDraft();
            RefreshRelations(_session.Current.Relations, Endpoints().Resolve);
            _hint = "已原位复制并保存 " + result.Relation.Child.DisplayName + "。备份：" + result.BackupPath;
            RefreshSaveStatus();
        }

        private void Apply(bool replace, InstanceBinding slotReference = null)
        {
            _session.RequireWritable();
            var parent = _draft.Parent ?? (slotReference == null ? null : slotReference.Parent);
            if (parent == null || _draft.Children.Count == 0) throw new ValidationException("请选择父组件并将待安装的实例加入本轮子组件。");
            var records = _session.Current.Relations; var revision = _session.Revision;
            var resolve = Endpoints().Resolve;
            var proposed = InstanceBindingRules.Apply(records, parent, _draft.Children, resolve, replace,
                slotReference == null ? null : slotReference.Id);
            if (records.Select(r => r.Id).SequenceEqual(proposed.Select(r => r.Id)))
            {
                _hint = "这些父子关系已存在。";
                RefreshRelations(records, resolve);
                return;
            }
            var updateStamp = _document.GetUpdateStamp();
            var contact = SwContactDiagnostics.ForBinding(_app, _document, resolve(parent), _draft.Children.Select(resolve))
                .Where(value => value.NeedsReview).Select(value => value.Message).ToArray();
            if (!ConfirmBatch(records, replace, parent, _draft.Children, slotReference, contact)) return;
            if (_document.GetUpdateStamp() != updateStamp)
                throw new ValidationException("装配在确认期间已改变，请重新应用绑定。");
            var continuous = _continuous.Checked;
            CommitRelations(proposed, revision); _draft.Applied(_chain.Checked); _document.ClearSelection2(true); _continuous.Checked = false;
            RefreshDraft(); RefreshRelations(proposed, resolve);
            if (_draft.Parent != null) _continuous.Checked = continuous;
            _hint = _draft.NextParents.Count > 0 ? "已绑定。请从“下一父组件”选择一个已绑定子组件，继续下一层。" :
                _draft.Parent != null ? "已绑定，子组件已接续为父组件。继续点选下一层子组件。" : "已绑定并清空本轮选择，可读取下一父组件。请保存总装配。";
            if (slotReference != null) _hint = "已复用槽位并记录各子组件偏移。请保存总装配。";
        }
        private bool ConfirmBatch(List<InstanceBinding> records, bool replace, BindingEndpoint parent, IList<BindingEndpoint> children,
            InstanceBinding slotReference, string[] contact)
        {
            using (var dialog = new Form { Text = replace ? "确认替换全部父组件（仅保留本轮父组件）" : "确认新增绑定", Size = new Size(980, 540), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false })
            {
                var table = Grid(false); table.Columns.Add("child", "子组件"); table.Columns.Add("old", replace ? "全部已有父组件（将被替换）" : "已有父组件（保留）"); table.Columns.Add("next", "本轮父组件");
                foreach (var c in children)
                { var old = BindingEditorState.Parents(records, c); table.Rows.Add(Display(c), old.Count == 0 ? "未绑定" : string.Join(System.Environment.NewLine, old.Select(Display)), Display(parent)); }
                table.DefaultCellStyle.WrapMode = DataGridViewTriState.True; table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
                var ok = new Button { Text = "确认 " + children.Count + " 项", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel }; buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                dialog.Controls.Add(table); dialog.Controls.Add(buttons);
                if (contact.Length != 0)
                    dialog.Controls.Add(new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom,
                        Height = Math.Min(140, 30 + 40 * contact.Length), Text = "请核对父子实体接触\r\n" + string.Join("\r\n", contact) });
                if (slotReference != null)
                {
                    dialog.Text = "确认复用槽位";
                    dialog.Controls.Add(new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8),
                        Text = "参考子组件 " + Display(slotReference.Child) + "\n请确认待绑定子组件已定位在同一槽位。插件将按当前 CAD 位姿计算偏移。" });
                }
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
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
            _document.ClearSelection2(true); RefreshDraft(); _hint = "已读取子组件。读入新父组件后，可原位复制并绑定，或替换原实例的父组件。";
        }
        private void ContinueFromSelected()
        {
            var selected = SelectedRecords(); if (selected.Count != 1) throw new ValidationException("请选择一个已绑定子组件作为下一父组件。");
            _continuous.Checked = false; _draft.Clear(); _draft.SetParent(selected[0].Child); _chain.Checked = true;
            _document.ClearSelection2(true); RefreshDraft(); _hint = "已接续所选子组件，可继续绑定下一层。";
        }
        private void Unbind(bool wholeGroup)
        {
            var group = SelectedGroup();
            var selected = wholeGroup ? _views.Where(v => v.Record.Parent.Context == group).Select(v => v.Record).ToList() : SelectedRecords();
            RemoveRelations(selected);
        }
        private void RemoveRelations(IList<InstanceBinding> selected)
        {
            if (selected.Count == 0) throw new ValidationException("请先选择要解除的关系。");
            var revision = _session.Revision;
            var current = _session.Current.Relations;
            if (MessageBox.Show(this, "解除所选 " + selected.Count + " 条关系？\n未选中的关系会保留。", "解除绑定", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            var ids = new HashSet<string>(selected.Select(r => r.Id));
            var remaining = current.Where(r => !ids.Contains(r.Id)).ToList();
            CommitRelations(remaining, revision);
            RefreshRelations(remaining);
        }
        private void CommitRelations(List<InstanceBinding> proposed, long revision)
        {
            EnsureContext();
            if (_session.Revision != revision) throw new ValidationException("关系在确认期间已改变，请刷新后重试。");
            _session.Commit(proposed);
        }
        private void Highlight(IList<InstanceBinding> relations)
        {
            _continuous.Checked = false;
            _document.ClearSelection2(true);
            var selection = _document.SelectionManager as SelectionMgr;
            if (selection == null) throw new ValidationException("无法读取 SOLIDWORKS 当前选择。");
            var operation = Endpoints();
            var roles = new Dictionary<string, Component2>(StringComparer.Ordinal);
            AddHighlightRole(operation, relations.Select(relation => relation.Parent), SwBindingHighlight.ParentMark, roles);
            AddHighlightRole(operation, relations.Select(relation => relation.Child), SwBindingHighlight.ChildMark, roles);
            var highlighted = PaintHighlight(selection, roles);
            _lastPickedSelection = string.Empty;
            StylePickedRows();
            _hint = "已高亮 " + highlighted + " 个承载关系实例。";
            if (highlighted < roles.Count) _hint += " " + (roles.Count - highlighted) + " 个实例当前无法高亮。";
        }
        private void SaveAssembly()
        {
            _session.Save(false);
            _hint = "总装配已保存。导出前请重新分类预览。";
            RefreshSaveStatus();
        }

        private void EditInterfaces()
        {
            _continuous.Checked = false;
            var operation = Endpoints();
            var records = _session.Current.Relations;
            var endpoints = InterfaceSelection(operation, records);
            if (endpoints.Count == 0)
                throw new ValidationException("请选择要编辑接口的普通资产，或先读取本轮父、子组件。");

            var resolved = endpoints.GroupBy(endpoint => endpoint.Context, StringComparer.Ordinal).Select(group => group.First())
                .Select(endpoint => new { Endpoint = endpoint, Node = operation.ResolveVisualization(endpoint) }).ToList();
            var allOccurrences = operation.SameAssetInstances(resolved.Select(item => item.Node))
                .ToLookup(node => ((SwCadNode)node.Source).SourcePath, StringComparer.OrdinalIgnoreCase);
            var items = new List<InterfaceEditorItem>();
            foreach (var group in resolved.GroupBy(item => ((SwCadNode)item.Node.Source).SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                var source = (SwCadNode)first.Node.Source;
                // Binding picks tolerate pending assembly edits. A source editor must use its own dirty-state tracking.
                var asset = new InterfaceAsset(new SwCadNode(_app, source.Component, source.InstancePath));
                asset.Reload();
                var occurrences = new HashSet<string>(allOccurrences[group.Key]
                    .Select(node => SwInstanceBindings.Context(AssetComponent(node))), StringComparer.Ordinal);
                occurrences.UnionWith(group.Select(item => item.Endpoint.Context));
                var children = records.Where(record => occurrences.Contains(record.Parent.Context)).Select(record => record.Child);
                if (_draft.Parent != null && occurrences.Contains(_draft.Parent.Context)) children = children.Concat(_draft.Children);
                items.Add(new InterfaceEditorItem
                {
                    Asset = asset,
                    AssetUuid = IdentityService.AssetUuid(source.ClassificationModel).ToString("D"),
                    DisplayName = AssetName(first.Endpoint),
                    InstanceCount = occurrences.Count,
                    MaximumBoundSlots = _session.RequiredSlotCapacity(asset.Path, records),
                    Endpoint = first.Endpoint,
                    Children = children.GroupBy(child => child.Context, StringComparer.Ordinal).Select(child => child.First()).ToList()
                });
            }

            var updateStamp = _document.GetUpdateStamp();
            var suggestionSource = Endpoints();
            using (var editor = new InterfacePropertiesDialog(items,
                item =>
                {
                    EnsureContext();
                    if (_document.GetUpdateStamp() != updateStamp)
                        throw new ValidationException("总装配已变化，请重新打开接口编辑窗口。");
                    Endpoints().ResolveVisualization(item.Endpoint);
                },
                endpoint =>
                {
                    EnsureContext();
                    var child = suggestionSource.Resolve(endpoint);
                    string value;
                    return child.Properties.TryGetValue(PropertyRules.ConnectionInterface, out value) ? value : string.Empty;
                },
                () =>
                {
                    suggestionSource = Endpoints();
                    updateStamp = _document.GetUpdateStamp();
                }))
            {
                editor.ShowDialog(this);
                if (editor.SavedAny)
                {
                    var fresh = Endpoints();
                    RefreshRelations(_session.Current.Relations, fresh.Resolve);
                    _hint = "接口已保存到资产源文件，请重新分类预览。";
                    _draft.RefreshEndpoints(endpoint => fresh.Capture(fresh.ResolveVisualization(endpoint)));
                    RefreshDraft();
                }
            }
            RefreshSaveStatus();
        }

        private List<BindingEndpoint> InterfaceSelection(SwBindingEndpoints operation, IList<InstanceBinding> records)
        {
            var draft = new List<BindingEndpoint>();
            if (_draft.Parent != null) draft.Add(_draft.Parent);
            draft.AddRange(_draft.Children);
            if (draft.Count != 0) return draft;

            if (_relationshipTabs.SelectedTab == _instanceTab)
            {
                var selected = _details.SelectedRows.Cast<DataGridViewRow>().Select(row => ((RelationView)row.Tag).Record).ToList();
                if (selected.Count == 0) selected = records.Where(record => record.Parent.Context == SelectedGroup()).ToList();
                if (selected.Count != 0) return selected.SelectMany(record => new[] { record.Parent, record.Child }).ToList();
            }
            else
            {
                var selectedAsset = _assetRelations.SelectedRows.Count != 0 ? (string)_assetRelations.SelectedRows[0].Tag :
                    _assetGroups.SelectedRows.Count != 0 ? (string)_assetGroups.SelectedRows[0].Tag : null;
                if (selectedAsset != null)
                {
                    var selected = _views.SelectMany(view => new[]
                    {
                        new { Id = ViewAssetId(view, true), Endpoint = view.Record.Parent },
                        new { Id = ViewAssetId(view, false), Endpoint = view.Record.Child }
                    }).Where(item => string.Equals(item.Id, selectedAsset, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Endpoint).ToList();
                    if (selected.Count != 0) return selected;
                }
            }

            List<string> ignored;
            var picked = CaptureSelected(operation, CurrentSelection(), out ignored);
            if (picked.Count == 0 && ignored.Count != 0) throw new ValidationException(ignored[0]);
            return picked;
        }
        private void ValidateRelations()
        {
            var records = _session.Current.Relations;
            if (records.Count > 0) RefreshRelations(records, Endpoints().Resolve);
            _hint = records.Count == 0 ? "当前配置没有已有关系。" : "已有关系的端点与接口已检查。";
            RefreshSaveStatus();
        }

        private void RefreshRelations(IList<InstanceBinding> records, Func<BindingEndpoint, ExportNode> resolve = null)
        {
            ResetPickedHighlight();
            _displayedRevision = _session.Revision;
            _relationsByEndpoint = BindingEditorState.IndexRelatedInstances(records);
            _relationsValidated = resolve != null;
            _views = new List<RelationView>();
            var multiple = InstanceBindingRules.MultipleParentBindingIds(records);
            foreach (var relation in records)
            {
                var view = new RelationView { Record = relation, Parent = Display(relation.Parent), Child = Display(relation.Child) };
                if (resolve != null)
                {
                    var endpointsResolved = false;
                    try
                    {
                        var parent = resolve(relation.Parent);
                        var child = resolve(relation.Child);
                        view.Parent = Label(parent.Source.InstancePath, parent.AssetId);
                        view.Child = Label(child.Source.InstancePath, child.AssetId);
                        view.ParentName = InstanceName(parent.Source.InstancePath);
                        view.ChildName = InstanceName(child.Source.InstancePath);
                        view.ParentAssetId = parent.AssetId;
                        view.ChildAssetId = child.AssetId;
                        endpointsResolved = true;
                        InstanceBindingRules.ValidateConnection(parent, child);
                    }
                    catch (ValidationException ex)
                    {
                        view.Error = ex.Message;
                        view.InterfaceIssue = endpointsResolved;
                    }
                    catch (COMException ex) { view.Error = ex.Message; }
                }
                if (multiple.Contains(relation.Id))
                {
                    view.LegacyMultipleParents = true;
                    view.Error = (view.Error == null ? "" : view.Error + "\n") + "历史关系中此实例有多个承载父组件，请原位复制并迁移。";
                }
                _views.Add(view);
            }
            var parentCount = records.Select(r => r.Parent.Context).Distinct(StringComparer.Ordinal).Count();
            _totals.Text = "总关系 " + records.Count + " 条 · 涉及 " + _relationsByEndpoint.Count + " 个组件（" + parentCount + " 个父组件）";
            var issues = _views.Where(v => v.InterfaceIssue).ToList();
            _interfaceBanner.Visible = issues.Count > 0;
            _interfaceSummary.Text = issues.Count == 1
                ? "接口不兼容   父 " + issues[0].ParentName + " → 子 " + issues[0].ChildName
                : issues.Count + " 条接口不兼容";
            _interfaceIssues.Text = issues.Count == 1 ? "定位并查看原因" : "查看接口冲突";
            RenderGroups();
            RenderAssetGroups();
            RefreshSaveStatus();
        }

        private void ShowInterfaceIssues()
        {
            var issues = _views.Where(v => v.InterfaceIssue).ToList();
            if (issues.Count == 0) return;
            using (var dialog = new Form
            {
                Text = "接口不兼容 · " + issues.Count + " 条", Size = new Size(850, 420),
                MinimumSize = new Size(630, 350), StartPosition = FormStartPosition.CenterParent, Font = Font,
                MinimizeBox = false, ShowInTaskbar = false
            })
            {
                var table = Grid(false);
                table.Columns.Add("parent", "父组件");
                table.Columns.Add("child", "子组件");
                foreach (var issue in issues)
                {
                    var row = table.Rows[table.Rows.Add(issue.ParentName, issue.ChildName)];
                    row.Tag = issue;
                    row.Cells[0].ToolTipText = issue.Parent;
                    row.Cells[1].ToolTipText = issue.Child;
                }

                var reason = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
                var locate = new Button { Text = "定位所选关系", AutoSize = true, DialogResult = DialogResult.OK };
                var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
                var actions = Bar();
                actions.Controls.Add(locate);
                actions.Controls.Add(close);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                layout.Controls.Add(table, 0, 0);
                layout.Controls.Add(reason, 0, 1);
                layout.Controls.Add(actions, 0, 2);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = locate;
                dialog.CancelButton = close;
                Action showReason = () =>
                {
                    var selected = table.SelectedRows.Count == 0 ? null : (RelationView)table.SelectedRows[0].Tag;
                    reason.Text = selected == null ? "" : selected.Error.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    locate.Enabled = selected != null;
                };
                table.SelectionChanged += delegate { showReason(); };
                table.CurrentCell = table.Rows[0].Cells[0];
                table.Rows[0].Selected = true;
                showReason();
                if (dialog.ShowDialog(this) == DialogResult.OK && table.SelectedRows.Count > 0)
                    LocateInterfaceIssue((RelationView)table.SelectedRows[0].Tag);
            }
        }

        private void LocateInterfaceIssue(RelationView issue)
        {
            _relationshipTabs.SelectedTab = _instanceTab;
            if (!Matches(issue)) _search.Clear();
            var parent = _parents.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (string)row.Tag == issue.Record.Parent.Context);
            if (parent == null) return;
            _rendering = true;
            try
            {
                _parents.ClearSelection();
                _parents.CurrentCell = parent.Cells[0];
                parent.Selected = true;
            }
            finally { _rendering = false; }
            RenderDetails();
            var child = _details.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => ((RelationView)row.Tag).Record.Id == issue.Record.Id);
            if (child == null) return;
            _details.CurrentCell = child.Cells[0];
            child.Selected = true;
            _details.Focus();
            _highlightHint = null;
            _hint = "接口不兼容：父 " + issue.ParentName + " → 子 " + issue.ChildName;
            RefreshSaveStatus();
        }

        private string SelectedGroup()
        { return _parents.SelectedRows.Count == 0 ? null : _parents.SelectedRows[0].Tag as string; }

        private void RenderAssetGroups(string preferredAssetId = null)
        {
            var selected = preferredAssetId ?? (_assetGroups.SelectedRows.Count == 0 ? null : (string)_assetGroups.SelectedRows[0].Tag);
            var endpoints = AssetViewRecords().SelectMany(view => new[]
                { new { Id = ViewAssetId(view, true), Endpoint = view.Record.Parent },
                  new { Id = ViewAssetId(view, false), Endpoint = view.Record.Child } });
            var groups = endpoints.Where(item => !string.IsNullOrEmpty(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Id = group.Key, Name = AssetName(group.First().Endpoint),
                    Count = group.Select(item => item.Endpoint.Context).Distinct(StringComparer.Ordinal).Count() }).ToList();
            if (_pickedAssetId != null && !groups.Any(group => group.Id == _pickedAssetId))
                groups.Add(new { Id = _pickedAssetId, Name = _pickedAssetName, Count = 0 });

            _rendering = true;
            try
            {
                _assetGroups.Rows.Clear();
                foreach (var group in groups.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id))
                {
                    var row = _assetGroups.Rows[_assetGroups.Rows.Add(group.Name + " | " + group.Id, group.Count)];
                    row.Tag = group.Id;
                    row.Cells[0].ToolTipText = group.Name + " | " + group.Id;
                    int mark;
                    if (_pickedAssetRoles.TryGetValue(group.Id, out mark)) ColorRow(row, mark);
                }
                var current = _assetGroups.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (string)row.Tag == selected) ??
                    _assetGroups.Rows.Cast<DataGridViewRow>().FirstOrDefault();
                if (current != null)
                {
                    _assetGroups.CurrentCell = current.Cells[0];
                    current.Selected = true;
                }
            }
            finally { _rendering = false; }
            RenderAssetRelations();
        }

        private void RenderAssetRelations()
        {
            _assetRelations.Rows.Clear();
            if (_assetGroups.SelectedRows.Count == 0) return;
            var selected = (string)_assetGroups.SelectedRows[0].Tag;
            var records = selected == _pickedAssetId && _visualizedAssetRelations != null
                ? _views.Where(view => _visualizedAssetRelations.Contains(view.Record)).ToList()
                : AssetViewRecords().ToList();
            foreach (var isParent in new[] { true, false })
            {
                var related = records.Where(view => string.Equals(ViewAssetId(view, !isParent), selected, StringComparison.OrdinalIgnoreCase));
                foreach (var group in related.Where(view => !string.IsNullOrEmpty(ViewAssetId(view, isParent)))
                    .GroupBy(view => ViewAssetId(view, isParent), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => AssetName(isParent ? group.First().Record.Parent : group.First().Record.Child), StringComparer.CurrentCultureIgnoreCase))
                {
                    var endpoint = isParent ? group.First().Record.Parent : group.First().Record.Child;
                    var row = _assetRelations.Rows[_assetRelations.Rows.Add(isParent ? "父资产" : "子资产",
                        AssetName(endpoint) + " | " + group.Key, group.Count())];
                    row.Tag = group.Key;
                    row.Cells[1].ToolTipText = string.Join(System.Environment.NewLine, group.Select(view => view.Parent + " → " + view.Child));
                    if (selected == _pickedAssetId)
                        ColorRow(row, isParent ? SwBindingHighlight.AssetParentMark : SwBindingHighlight.AssetChildMark);
                    else
                    {
                        int mark;
                        if (group.Key != null && _pickedAssetRoles.TryGetValue(group.Key, out mark)) ColorRow(row, mark);
                    }
                }
            }
            _assetRelations.ClearSelection();
        }

        private IEnumerable<RelationView> AssetViewRecords()
        {
            return _views.Where(view => Matches(view) || (_visualizedAssetRelations != null && _visualizedAssetRelations.Contains(view.Record)));
        }

        private string ViewAssetId(RelationView view, bool parent)
        {
            var endpoint = parent ? view.Record.Parent : view.Record.Child;
            string current;
            if (_visualizedAssetIds != null && _visualizedAssetIds.TryGetValue(endpoint.Context, out current)) return current;
            return (parent ? view.ParentAssetId : view.ChildAssetId) ?? endpoint.AssetId;
        }

        private static string AssetName(BindingEndpoint endpoint)
        {
            return string.IsNullOrWhiteSpace(endpoint.SourcePath) ? InstanceName(endpoint.DisplayPath) :
                System.IO.Path.GetFileNameWithoutExtension(endpoint.SourcePath);
        }

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
                if (g.Any(v => v.InterfaceIssue)) MarkInterfaceStatus(row.Cells[2]);
                if (_pickedRoles.Count > 0) StyleRole(row, (string)row.Tag);
            }
            _parents.ClearSelection();
            var pick = _parents.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (string)r.Tag == selected) ?? _parents.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (pick != null) { pick.Selected = true; _parents.CurrentCell = pick.Cells[0]; }
            _rendering = false;
            RenderDetails();
        }
        private void RenderDetails()
        {
            _details.Rows.Clear(); var group = SelectedGroup();
            var parentsByChild = _views.Select(v => v.Record).ToLookup(r => r.Child.Context, r => r.Parent, StringComparer.Ordinal);
            foreach (var v in _views.Where(v => v.Record.Parent.Context == group && Matches(v)))
            {
                var parents = parentsByChild[v.Record.Child.Context].GroupBy(p => p.Context, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                var status = v.LegacyMultipleParents ? "旧多父待迁移" : v.InterfaceIssue ? "接口不兼容" : v.Error ?? SavedState();
                var row = _details.Rows[_details.Rows.Add(v.Child, parents.Length, status)]; row.Tag = v;
                row.Cells[0].ToolTipText = v.Record.SlotId == null ? v.Child : v.Child + "\n共享槽位 ID " + v.Record.SlotId;
                if (v.InterfaceIssue) MarkInterfaceStatus(row.Cells[2]);
                if (_pickedRoles.Count > 0) StylePickedChild(row, v);
                row.Cells[1].ToolTipText = string.Join(System.Environment.NewLine, parents.Select(Display)); row.Cells[2].ToolTipText = v.Error;
            }
            _details.ClearSelection();
        }
        private static void MarkInterfaceStatus(DataGridViewCell cell)
        {
            cell.Style.ForeColor = Color.DarkRed;
            cell.Style.BackColor = Color.MistyRose;
            cell.Style.SelectionBackColor = Color.DarkRed;
            cell.Style.SelectionForeColor = Color.White;
        }
        private string SavedState() { return _session.Pending ? "待保存" : (_relationsValidated ? "有效" : "已保存"); }
        private void RefreshSaveStatus()
        {
            var status = SavedState();
            if (status != _displayedSavedState)
            {
                foreach (DataGridViewRow row in _parents.Rows) if (!((string)row.Cells[2].Value).StartsWith("需修复") && (string)row.Cells[2].Value != status) row.Cells[2].Value = status;
                foreach (DataGridViewRow row in _details.Rows) if (((RelationView)row.Tag).Error == null && (string)row.Cells[2].Value != status) row.Cells[2].Value = status;
                _displayedSavedState = status;
            }
            var instructions = _highlightOnPick.Checked && _continuous.Checked
                ? "取消“连续点选子组件”即可恢复自动高亮。"
                : "未绑定不表示已确认空；修改后请保存并重新分类预览。";
            _state.Text = (_session.Pending ? "已应用，待保存总装配。" : "当前关系已保存。") + (_session.LastSaveError ?? "") + "\r\n" +
                (_highlightHint ?? _hint) + "\r\n" + instructions;
        }
        private static string InstanceName(string path) { return (path ?? "").Split('/').LastOrDefault(); }
        private static string Label(string path, string assetId) { return InstanceName(path) + " | " + assetId + " | " + path; }
        private static string Display(BindingEndpoint e) { return Label(e.DisplayPath, e.AssetId); }
        private sealed class EndpointItem { public EndpointItem(BindingEndpoint e) { Endpoint = e; } public BindingEndpoint Endpoint; public override string ToString() { return Display(Endpoint); } }
        private sealed class ManualPick { public Component2 Component; public string Name; public bool WholeComponent; }
        private sealed class RelationView
        {
            public InstanceBinding Record;
            public string Parent, Child, ParentName, ChildName, ParentAssetId, ChildAssetId, Error;
            public bool InterfaceIssue, LegacyMultipleParents;
        }
    }
}
