using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed partial class InstanceBindingDialog
    {
        private readonly BindingAssemblyTree _assemblyTree = new BindingAssemblyTree { Dock = DockStyle.Fill };
        private readonly BindingRelationsView _treeRelations = new BindingRelationsView { Dock = DockStyle.Fill };
        private readonly DataGridView _treeBatch = Grid(false);
        private readonly ComboBox _treeDirection = new ComboBox { Width = 225, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _treeStatus = new Label { Dock = DockStyle.Fill };
        private const string TreeInstructions = "选中一个 Asset，勾选另一侧的一个或多个实例。";
        private Button _treeCheck, _treeAdd, _treeReplace, _treeIssues;
        private string _treePlanError;
        private string _treeHint = TreeInstructions;
        private bool _showTreeIssues, _refreshingTree;

        private void InitializeBindingPages(Control modelPicker)
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var treePage = new TabPage("总装树");
            var pickerPage = new TabPage("模型点选");
            tabs.TabPages.Add(treePage);
            tabs.TabPages.Add(pickerPage);
            pickerPage.Controls.Add(modelPicker);
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == treePage) _continuous.Checked = false; };
            Controls.Add(tabs);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            var header = Bar();
            header.Controls.Add(Button("刷新总装树", delegate { RefreshRelations(true); }));
            _treeIssues = Button("需修复关系", delegate { _showTreeIssues = !_showTreeIssues; RenderTreeRelations(); });
            header.Controls.Add(_treeIssues);
            header.Controls.Add(new Label { AutoSize = true, Padding = new Padding(4, 7, 0, 0),
                Text = "左侧按 CAD 层级浏览，右侧编辑当前配置的显式承载关系。" });
            layout.Controls.Add(header, 0, 0);

            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1120, 580),
                Panel1MinSize = 260, Panel2MinSize = 570, SplitterDistance = 350 };
            split.Panel1.Controls.Add(_assemblyTree);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            right.Controls.Add(_treeRelations, 0, 0);
            var relationsBar = Bar();
            relationsBar.Controls.Add(Button("解除所选关系…", delegate { RemoveRelations(_treeRelations.SelectedBindings); }));
            relationsBar.Controls.Add(Button("高亮关系", HighlightTreeRelations));
            relationsBar.Controls.Add(Button("高亮当前实例", HighlightTreeAsset));
            relationsBar.Controls.Add(Button("接口属性 / 建议…", delegate { EditTreeInterfaces(true); }));
            right.Controls.Add(relationsBar, 0, 1);

            var directionBar = Bar();
            directionBar.Controls.Add(new Label { Text = "批量编辑", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _treeDirection.Items.AddRange(new object[] { "当前实例为父，勾选子项", "当前实例为子，勾选父项" });
            _treeDirection.SelectedIndex = 0;
            directionBar.Controls.Add(_treeDirection);
            directionBar.Controls.Add(Button("勾选此分支", delegate { _assemblyTree.CheckBranch(); }));
            directionBar.Controls.Add(Button("清空勾选", _assemblyTree.ClearChecks));
            right.Controls.Add(directionBar, 0, 2);
            _treeBatch.Columns.Add("instance", "勾选实例");
            _treeBatch.Columns.Add("status", "接口检查");
            _treeBatch.BackgroundColor = SystemColors.Window;
            _treeBatch.Columns[0].FillWeight = 48;
            _treeBatch.Columns[1].FillWeight = 52;
            right.Controls.Add(_treeBatch, 0, 3);
            var batchBar = Bar();
            _treeCheck = Button("检查接口", delegate { ApplyTreeBatch(false, true); });
            _treeAdd = Button("新增绑定", delegate { ApplyTreeBatch(false, false); });
            _treeReplace = Button("替换全部父项…", delegate { ApplyTreeBatch(true, false); });
            batchBar.Controls.Add(_treeCheck);
            batchBar.Controls.Add(_treeAdd);
            batchBar.Controls.Add(_treeReplace);
            batchBar.Controls.Add(Button("补接口…", delegate { EditTreeInterfaces(false); }));
            right.Controls.Add(batchBar, 0, 4);
            split.Panel2.Controls.Add(right);
            layout.Controls.Add(split, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.Controls.Add(Button("保存总装配", SaveAssembly), 0, 0);
            footer.Controls.Add(_treeStatus, 1, 0);
            layout.Controls.Add(footer, 0, 2);
            treePage.Controls.Add(layout);

            _assemblyTree.SelectionChanged += delegate
            {
                if (_refreshingTree) return;
                _showTreeIssues = false;
                RenderTreeRelations();
                RenderTreeBatch();
            };
            _assemblyTree.ChecksChanged += delegate { if (!_refreshingTree) RenderTreeBatch(); };
            _treeDirection.SelectedIndexChanged += delegate { RenderTreeBatch(); };
            _treeRelations.HighlightRequested += delegate { Guard(HighlightTreeRelations); };
            _treeBatch.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) Guard(delegate { EditTreeInterfaces(false); });
            };
        }

        private void RefreshAssemblyTree(AssemblyExportPlan plan, string planError, bool reload)
        {
            if (reload || plan == null)
            {
                _refreshingTree = true;
                try
                {
                    _assemblyTree.Load(_document, plan);
                    _treePlanError = planError;
                }
                catch (Exception ex)
                {
                    _assemblyTree.Load(_document, null);
                    _treePlanError = "无法读取总装特征树：" + ex.Message;
                }
                finally { _refreshingTree = false; }
                RenderTreeBatch();
            }
            RenderTreeRelations();
        }

        private void RenderTreeRelations()
        {
            var issues = _views.Where(v => v.Error != null).ToList();
            _treeIssues.Text = (_showTreeIssues ? "返回所选实例" : "需修复关系") + " (" + issues.Count + ")";
            if (_showTreeIssues)
            {
                _treeRelations.SetIssues(issues.Select(v => new BindingRelationRow(v.Record, v.Parent + " → " + v.Child, v.Error)));
                return;
            }
            var context = _assemblyTree.SelectedAsset == null ? null : _assemblyTree.SelectedContext;
            var incoming = _views.Where(v => v.Record.Child.Context == context)
                .Select(v => new BindingRelationRow(v.Record, v.Parent, v.Error));
            var outgoing = _views.Where(v => v.Record.Parent.Context == context)
                .Select(v => new BindingRelationRow(v.Record, v.Child, v.Error));
            var asset = _assemblyTree.SelectedAsset;
            var label = asset == null ? _assemblyTree.SelectedLabel : asset.Source.InstancePath;
            _treeRelations.SetRelations(string.IsNullOrWhiteSpace(label) ? null : label, incoming, outgoing);
        }

        private List<ExportNode> TreeTargets()
        {
            var selected = _assemblyTree.SelectedAsset;
            return _assemblyTree.CheckedAssets.Where(n => !ReferenceEquals(n, selected)).ToList();
        }

        private void RenderTreeBatch(IList<string> messages = null)
        {
            var targets = TreeTargets();
            _treeBatch.Rows.Clear();
            for (var index = 0; index < targets.Count; index++)
            {
                var node = targets[index];
                var message = messages == null ? "待检查" : messages[index];
                var row = _treeBatch.Rows[_treeBatch.Rows.Add(node.Source.InstancePath, message)];
                row.Tag = node;
                row.Cells[0].ToolTipText = node.Source.InstancePath;
                row.Cells[1].ToolTipText = message;
            }
            _treeBatch.ClearSelection();
            _treeBatch.CurrentCell = null;
            var ready = _assemblyTree.SelectedAsset != null && targets.Count > 0;
            var anchorIsParent = _treeDirection.SelectedIndex == 0;
            _treeAdd.Text = anchorIsParent ? "新增为子项" : "新增为父项";
            _treeBatch.Columns[0].HeaderText = anchorIsParent ? "勾选子项" : "勾选父项";
            _treeCheck.Enabled = _treeAdd.Enabled = ready;
            _treeReplace.Enabled = ready && anchorIsParent;
            if (messages == null)
            {
                _treeHint = ready ? "本轮将为当前实例添加 " + targets.Count + (anchorIsParent ? " 个子项。" : " 个父项。") : TreeInstructions;
                RefreshTreeSaveStatus();
            }
        }

        private BindingEndpoint CaptureTreeAsset(ExportNode node, AssemblyExportPlan plan)
        {
            if (node == null) throw new ValidationException("请在总装树中选中一个普通 Asset 实例。");
            // A tree snapshot can outlive a replacement or property edit in the CAD document.
            var captured = _session.Capture(node);
            var fresh = _session.Resolve(captured, plan);
            if (!string.Equals(node.GeometryUuid, fresh.GeometryUuid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(((SwCadNode)node.Source).ClassificationModel.FullPath,
                    ((SwCadNode)fresh.Source).ClassificationModel.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("实例源模型已更换，请刷新总装树后重新选择：" + node.Name);
            return _session.Capture(fresh);
        }

        private void ApplyTreeBatch(bool replace, bool checkOnly)
        {
            _session.RequireWritable();
            var plan = Plan();
            var anchor = CaptureTreeAsset(_assemblyTree.SelectedAsset, plan);
            var targets = TreeTargets().Select(node => CaptureTreeAsset(node, plan)).ToList();
            if (targets.Count == 0) throw new ValidationException("请勾选至少一个其他 Asset 实例。");
            var anchorIsParent = _treeDirection.SelectedIndex == 0;
            if (replace && !anchorIsParent) throw new ValidationException("替换全部父项时，请选中新父项并勾选子项。");
            var resolved = new Dictionary<string, ExportNode>(StringComparer.Ordinal);
            Func<BindingEndpoint, ExportNode> resolve = endpoint =>
            {
                var key = endpoint.Context + "|" + endpoint.Reference + "|" + endpoint.SourceIdentity + "|" + endpoint.SourcePath;
                ExportNode node;
                if (!resolved.TryGetValue(key, out node)) resolved.Add(key, node = _session.Resolve(endpoint, plan));
                return node;
            };
            var messages = new List<string>();
            var invalid = 0;
            foreach (var target in targets)
            {
                try
                {
                    InstanceBindingRules.ValidateConnection(resolve(anchorIsParent ? anchor : target), resolve(anchorIsParent ? target : anchor));
                    messages.Add("接口与角色通过");
                }
                catch (ValidationException ex)
                {
                    invalid++;
                    messages.Add(ex.Message.Replace("\n", " · "));
                }
            }
            RenderTreeBatch(messages);
            _treeHint = invalid > 0 ? invalid + " 条候选关系需修复。选择对应行后补接口。"
                : "接口与角色检查通过，应用时将检查整个关系图。";
            RefreshTreeSaveStatus();
            if (invalid > 0 || checkOnly) return;

            var current = _session.Current.Relations;
            var revision = _session.Revision;
            List<InstanceBinding> proposed;
            if (anchorIsParent)
                proposed = InstanceBindingRules.Apply(current, anchor, targets, resolve, replace);
            else
            {
                proposed = current;
                foreach (var parent in targets)
                    proposed = InstanceBindingRules.Apply(proposed, parent, new[] { anchor }, resolve, false);
            }
            if (replace && !ConfirmBatch(current, true, anchor, targets)) return;
            CommitRelations(proposed, revision);
            _assemblyTree.ClearChecks();
            _showTreeIssues = false;
            _treeHint = "批量关系已应用。确认后保存总装配。";
            RefreshRelations();
        }

        private void EditTreeInterfaces(bool fromRelations)
        {
            var plan = Plan();
            if (fromRelations)
            {
                var records = _treeRelations.SelectedBindings;
                if (records.Count > 0)
                {
                    ShowInterfaceEditor(plan, records.SelectMany(r => new[] { r.Parent, r.Child }), Enumerable.Empty<InstanceBinding>());
                    return;
                }
            }
            var anchor = CaptureTreeAsset(_assemblyTree.SelectedAsset, plan);
            var pendingNode = _treeBatch.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag as ExportNode).FirstOrDefault();
            var targets = fromRelations ? new List<ExportNode>() :
                pendingNode == null ? TreeTargets() : new List<ExportNode> { pendingNode };
            var endpoints = targets.Select(node => CaptureTreeAsset(node, plan)).ToList();
            var candidates = _treeDirection.SelectedIndex == 0
                ? endpoints.Select(e => new InstanceBinding { Parent = anchor, Child = e })
                : endpoints.Select(e => new InstanceBinding { Parent = e, Child = anchor });
            ShowInterfaceEditor(plan, endpoints.Concat(new[] { anchor }), candidates);
        }

        private void HighlightTreeAsset()
        {
            Highlight(new[] { CaptureTreeAsset(_assemblyTree.SelectedAsset, Plan()) });
        }

        private void HighlightTreeRelations()
        {
            var records = _treeRelations.SelectedBindings;
            if (records.Count == 0) throw new ValidationException("请先选中一条或多条关系。");
            Highlight(records.SelectMany(r => new[] { r.Parent, r.Child }));
        }

        private void RefreshTreeSaveStatus()
        {
            _treeStatus.Text = (_session.Pending ? "关系待保存总装配。" : "关系已保存。") +
                (_session.LastSaveError ?? "") + "\r\n" + (_treePlanError ?? _treeHint);
        }
    }
}
