using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class BindingAssemblyTree : UserControl
    {
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly TreeView _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            HideSelection = false,
            ShowNodeToolTips = true
        };
        private readonly Label _status = new Label { AutoSize = true, Dock = DockStyle.Fill };
        private readonly List<Entry> _assets = new List<Entry>();
        private readonly HashSet<Entry> _checked = new HashSet<Entry>();
        private Entry _root;
        private int _missingAssetCount;
        private bool _rendering;

        public event EventHandler SelectionChanged;
        public event EventHandler ChecksChanged;

        public BindingAssemblyTree()
        {
            Dock = DockStyle.Fill;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "查找实例", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(_search, 1, 0);
            layout.Controls.Add(_tree, 0, 1);
            layout.SetColumnSpan(_tree, 2);
            layout.Controls.Add(_status, 0, 2);
            layout.SetColumnSpan(_status, 2);
            Controls.Add(layout);

            _search.TextChanged += delegate { RenderTree(); };
            _tree.AfterSelect += delegate { if (!_rendering) Raise(SelectionChanged); };
            _tree.BeforeCheck += delegate(object sender, TreeViewCancelEventArgs e)
            {
                var entry = (Entry)e.Node.Tag;
                e.Cancel = !entry.CanBind;
            };
            _tree.AfterCheck += delegate(object sender, TreeViewEventArgs e)
            {
                if (_rendering) return;
                var entry = (Entry)e.Node.Tag;
                if (e.Node.Checked) _checked.Add(entry);
                else _checked.Remove(entry);
                UpdateStatus();
                Raise(ChecksChanged);
            };
            UpdateStatus();
        }

        private Entry SelectedEntry { get { return _tree.SelectedNode == null ? null : (Entry)_tree.SelectedNode.Tag; } }
        public ExportNode SelectedNode { get { var entry = SelectedEntry; return entry == null ? null : entry.Node; } }
        public ExportNode SelectedAsset { get { var entry = SelectedEntry; return entry != null && entry.CanBind ? entry.Node : null; } }
        public string SelectedLabel { get { var entry = SelectedEntry; return entry == null ? string.Empty : entry.Text; } }
        public string SelectedContext { get { var entry = SelectedEntry; return entry == null ? string.Empty : entry.Context; } }
        public IList<ExportNode> CheckedAssets { get { return _assets.Where(_checked.Contains).Select(entry => entry.Node).ToList(); } }

        public new void Load(ModelDoc2 document, AssemblyExportPlan plan)
        {
            var previous = SelectedEntry;
            var checkedKeys = new HashSet<string>(_checked.Select(entry => entry.StateKey), StringComparer.OrdinalIgnoreCase);
            _root = null;
            _assets.Clear();
            _checked.Clear();
            _missingAssetCount = 0;
            try
            {
                if (plan != null)
                {
                    ReadTree(document, plan);
                    _checked.UnionWith(_assets.Where(entry => checkedKeys.Contains(entry.StateKey)));
                }
            }
            finally
            {
                var selected = _root == null || previous == null || previous.StateKey.Length == 0 ? null :
                    Flatten(_root).FirstOrDefault(entry => string.Equals(entry.StateKey, previous.StateKey, StringComparison.OrdinalIgnoreCase));
                RenderTree(selected);
                Raise(ChecksChanged);
            }
        }

        public void ClearChecks()
        {
            if (_checked.Count == 0) return;
            _checked.Clear();
            _rendering = true;
            try { foreach (TreeNode node in _tree.Nodes) SetBranchChecks(node, false); }
            finally { _rendering = false; }
            UpdateStatus();
            Raise(ChecksChanged);
        }

        public int CheckBranch()
        {
            if (_tree.SelectedNode == null) return 0;
            var before = _checked.Count;
            _rendering = true;
            _tree.BeginUpdate();
            try { SetBranchChecks(_tree.SelectedNode, true); }
            finally
            {
                _tree.EndUpdate();
                _rendering = false;
            }
            var added = _checked.Count - before;
            if (added != 0)
            {
                UpdateStatus();
                Raise(ChecksChanged);
            }
            return added;
        }

        private void SetBranchChecks(TreeNode node, bool value)
        {
            var entry = (Entry)node.Tag;
            if (entry.CanBind)
            {
                if (value) _checked.Add(entry);
                if (node.Checked != value) node.Checked = value;
            }
            foreach (TreeNode child in node.Nodes) SetBranchChecks(child, value);
        }

        private void ReadTree(ModelDoc2 document, AssemblyExportPlan plan)
        {
            var nodes = SwInstanceBindings.Flatten(plan.Roots)
                .Where(node => node.Source is SwCadNode && node.Source.InstanceId != "root")
                .ToDictionary(node => SwInstanceBindings.Context(((SwCadNode)node.Source).Component), StringComparer.Ordinal);
            var featureManager = document.FeatureManager;
            var treeRoot = featureManager == null ? null :
                featureManager.GetFeatureTreeRootItem2((int)swFeatMgrPane_e.swFeatMgrPaneBottom) ??
                featureManager.GetFeatureTreeRootItem2((int)swFeatMgrPane_e.swFeatMgrPaneTop);
            if (treeRoot == null) throw new ValidationException("无法读取 SOLIDWORKS 当前配置的 FeatureManager 树，请在 SOLIDWORKS 中显示设计树后刷新。");

            var rootNode = plan.Roots.FirstOrDefault(node => node.Source.InstanceId == "root");
            var rootLabel = treeRoot.Text;
            var root = new Entry(string.IsNullOrWhiteSpace(rootLabel) ? document.GetTitle() : rootLabel, rootNode, string.Empty);
            if (!IsBoundary(rootNode))
                ReadChildren(treeRoot, root, nodes, new HashSet<string>(StringComparer.Ordinal), string.Empty);

            _assets.AddRange(Flatten(root).Where(entry => entry.CanBind));
            _missingAssetCount = nodes.Values.Count(node => node.Kind == ExportNodeKind.Asset) - _assets.Count;
            _root = root;
        }

        // FeatureManager items can become invalid after a model rebuild. Only the projected data
        // and current plan nodes are retained by the window.
        private static void ReadChildren(TreeControlItem parent, Entry target,
            Dictionary<string, ExportNode> nodes, HashSet<string> shown, string owner)
        {
            for (var item = parent.GetFirstChild(); item != null; item = item.GetNext())
            {
                ExportNode node = null;
                var context = string.Empty;
                if (item.ObjectType == (int)swTreeControlItemType_e.swFeatureManagerItem_Component)
                {
                    var component = item.Object as Component2;
                    if (component == null) continue;
                    context = SwInstanceBindings.Context(component);
                    if (BindingEditorState.OwnerContext(context) != owner ||
                        !nodes.TryGetValue(context, out node) || !shown.Add(context)) continue;
                }
                else if (IsReferenceFolder(item)) continue;

                var entry = new Entry(item.Text, node, context);
                if (!IsBoundary(node)) ReadChildren(item, entry, nodes, shown, node == null ? owner : context);
                if (entry.Children.Count != 0 || (node != null &&
                    (node.Kind == ExportNodeKind.Asset || node.Kind == ExportNodeKind.Workstation)))
                    target.Children.Add(entry);
            }
        }

        private static bool IsReferenceFolder(TreeControlItem item)
        {
            if (item.ObjectType != (int)swTreeControlItemType_e.swFeatureManagerItem_Feature) return false;
            var feature = item.Object as Feature;
            if (feature == null) return false;
            // History and selection sets contain aliases of real component rows.
            var type = feature.GetTypeName2();
            return type == "HistoryFolder" || type == "SelectionSetFolder" || type == "SelectionSet";
        }

        private static bool IsBoundary(ExportNode node)
        {
            return node != null && (node.Kind == ExportNodeKind.Asset || node.Kind == ExportNodeKind.Robot);
        }

        private void RenderTree() { RenderTree(SelectedEntry); }

        private void RenderTree(Entry selected)
        {
            var previous = SelectedEntry;
            TreeNode restored = null;
            var query = _search.Text.Trim();
            _rendering = true;
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                if (_root != null)
                {
                    var root = CreateTreeNode(_root, query, false, selected, ref restored);
                    _tree.Nodes.Add(root);
                    if (query.Length != 0) root.ExpandAll();
                    else root.Expand();
                    if (restored != null)
                    {
                        _tree.SelectedNode = restored;
                        restored.EnsureVisible();
                    }
                }
            }
            finally
            {
                _tree.EndUpdate();
                _rendering = false;
            }
            UpdateStatus();
            if (!ReferenceEquals(previous, SelectedEntry)) Raise(SelectionChanged);
        }

        private TreeNode CreateTreeNode(Entry entry, string query, bool ancestorMatches, Entry selected, ref TreeNode restored)
        {
            var matches = ancestorMatches || entry.Matches(query);
            var children = new List<TreeNode>();
            foreach (var child in entry.Children)
            {
                var projected = CreateTreeNode(child, query, matches, selected, ref restored);
                if (projected != null) children.Add(projected);
            }
            if (entry != _root && !matches && children.Count == 0) return null;
            var result = new TreeNode(entry.Text, children.ToArray())
            {
                Tag = entry,
                Checked = _checked.Contains(entry),
                ToolTipText = entry.Path,
                ForeColor = entry.CanBind ? SystemColors.WindowText : SystemColors.GrayText
            };
            if (ReferenceEquals(entry, selected)) restored = result;
            return result;
        }

        private void UpdateStatus()
        {
            _status.Text = _root == null ? "总装树待加载" :
                "资产 " + _assets.Count + " · 已勾选 " + _checked.Count +
                (_missingAssetCount == 0 ? string.Empty : " · SOLIDWORKS 树未显示 " + _missingAssetCount + " 个资产");
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static IEnumerable<Entry> Flatten(Entry entry)
        {
            yield return entry;
            foreach (var child in entry.Children)
                foreach (var descendant in Flatten(child)) yield return descendant;
        }

        private sealed class Entry
        {
            public readonly string Text;
            public readonly string Path;
            public readonly string Context;
            public readonly string StateKey;
            public readonly ExportNode Node;
            public readonly List<Entry> Children = new List<Entry>();
            private readonly string _searchText;

            public Entry(string text, ExportNode node, string context)
            {
                Node = node;
                Context = context;
                Text = string.IsNullOrWhiteSpace(text) ? (node == null ? "总装" : node.Name) : text;
                Path = node == null ? string.Empty : node.Source.InstancePath;
                var sw = node == null ? null : node.Source as SwCadNode;
                StateKey = context.Length == 0 || sw == null ? string.Empty :
                    context + "\0" + sw.SourcePath + "\0" + node.GeometryUuid;
                var workstation = node != null && node.Kind == ExportNodeKind.Workstation;
                var workstationName = workstation ? PropertyRules.RequireWorkstationName(node.Properties, node.Name) : string.Empty;
                if (workstation) Text = "工位 · " + workstationName + " · " + Text;
                else if (CanBind) Text = "资产 · " + Text;
                _searchText = Text + "\n" + Path + "\n" + (node == null ? string.Empty : node.AssetId);
            }

            public bool CanBind { get { return Node != null && Node.Kind == ExportNodeKind.Asset && Node.Source.InstanceId != "root"; } }
            public bool Matches(string query) { return _searchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        }
    }
}
