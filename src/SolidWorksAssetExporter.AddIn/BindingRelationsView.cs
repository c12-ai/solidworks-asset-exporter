using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class BindingRelationRow
    {
        public BindingRelationRow(InstanceBinding record, string endpointPath, string error)
        {
            Record = record;
            EndpointPath = endpointPath;
            Error = error;
        }

        public InstanceBinding Record { get; private set; }
        public string EndpointPath { get; private set; }
        public string Error { get; private set; }
    }

    internal sealed class BindingRelationsView : UserControl
    {
        private readonly Label _title = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _summary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, RowHeadersVisible = false, RowTemplate = { Height = 30 }, BackgroundColor = SystemColors.Window
        };
        private string _instanceLabel;
        private bool _showingIssues, _rendering;

        public BindingRelationsView()
        {
            Dock = DockStyle.Fill;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(_title, 0, 0);
            layout.Controls.Add(_summary, 0, 1);
            layout.Controls.Add(_grid, 0, 2);
            Controls.Add(layout);

            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "关系", FillWeight = 16, MinimumWidth = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "组件实例路径", FillWeight = 53, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "验证状态", FillWeight = 31, MinimumWidth = 95, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.SelectionChanged += delegate { if (!_rendering) NotifySelectionChanged(); };
            _grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                var handler = HighlightRequested;
                if (e.RowIndex >= 0 && handler != null) handler(this, EventArgs.Empty);
            };
            SetRelations(null, null, null);
        }

        public event EventHandler SelectionChanged;
        public event EventHandler HighlightRequested;

        public IList<InstanceBinding> SelectedBindings
        {
            get
            {
                return _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
                    .Select(row => (InstanceBinding)row.Tag).ToList();
            }
        }

        public void SetRelations(string instanceLabel, IEnumerable<BindingRelationRow> incoming, IEnumerable<BindingRelationRow> outgoing)
        {
            var parents = (incoming ?? Enumerable.Empty<BindingRelationRow>()).ToList();
            var children = (outgoing ?? Enumerable.Empty<BindingRelationRow>()).ToList();
            var preserveSelection = !_showingIssues && string.Equals(_instanceLabel, instanceLabel, StringComparison.Ordinal);
            _instanceLabel = instanceLabel;
            _showingIssues = false;
            Render(instanceLabel ?? "尚未选中组件实例",
                instanceLabel == null ? "选择一个实例查看承载关系。" : "承载于 " + parents.Count + " 个实例  ·  承载了 " + children.Count + " 个实例",
                preserveSelection, delegate { AddRows("承载于", parents); AddRows("承载了", children); });
        }

        public void SetIssues(IEnumerable<BindingRelationRow> issues)
        {
            var rows = (issues ?? Enumerable.Empty<BindingRelationRow>()).ToList();
            var preserveSelection = _showingIssues;
            _instanceLabel = null;
            _showingIssues = true;
            Render("需修复的承载关系", rows.Count + " 条关系需要处理", preserveSelection, delegate { AddRows("需修复", rows); });
        }

        private void Render(string title, string summary, bool preserveSelection, Action addRows)
        {
            var selected = new HashSet<string>(preserveSelection ? SelectedBindings.Select(r => r.Id) : Enumerable.Empty<string>(), StringComparer.Ordinal);
            _title.Text = title;
            _toolTip.SetToolTip(_title, title);
            _summary.Text = summary;
            _rendering = true;
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                addRows();
                _grid.CurrentCell = null;
                _grid.ClearSelection();
                foreach (DataGridViewRow row in _grid.Rows)
                    if (selected.Contains(((InstanceBinding)row.Tag).Id)) row.Selected = true;
            }
            finally
            {
                _grid.ResumeLayout();
                _rendering = false;
            }
            NotifySelectionChanged();
        }

        private void AddRows(string direction, IEnumerable<BindingRelationRow> relations)
        {
            foreach (var relation in relations)
            {
                var row = _grid.Rows[_grid.Rows.Add(direction, relation.EndpointPath, relation.Error ?? "有效")];
                row.Tag = relation.Record;
                row.Cells[1].ToolTipText = relation.EndpointPath;
                if (relation.Error == null) continue;
                row.Cells[2].ToolTipText = relation.Error;
                row.Cells[2].Style.ForeColor = Color.DarkRed;
            }
        }

        private void NotifySelectionChanged()
        {
            var handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _toolTip.Dispose();
            base.Dispose(disposing);
        }
    }
}
