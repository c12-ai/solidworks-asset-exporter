using System;
using System.Drawing;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class ExportDialog : Form
    {
        private readonly ExportCoordinator _coordinator;
        private readonly SettingsStore _store;
        private readonly TextBox _assetRoot = new TextBox();
        private readonly TextBox _projectRoot = new TextBox();
        private readonly RadioButton _step = new RadioButton();
        private readonly RadioButton _stl = new RadioButton();
        private readonly TextBox _preview = new TextBox();
        private readonly Button _export = new Button();
        private AnalysisResult _analysis;

        public ExportDialog(SldWorks application)
        {
            _coordinator = new ExportCoordinator(application); _store = new SettingsStore();
            Text = "SOLIDWORKS Asset / Project 混合导出"; StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 650); Size = new Size(940, 760); Font = SystemFonts.MessageBoxFont;
            BuildUi(); LoadSettings();
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            AddLabel(layout, "Asset 资产库根目录", 0); AddPathRow(layout, _assetRoot, 0);
            AddLabel(layout, "Project 导出根目录", 1); AddPathRow(layout, _projectRoot, 1);
            AddLabel(layout, "XML Project mesh", 2);
            var formats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _step.Text = "STEP"; _step.AutoSize = true; _stl.Text = "STL"; _stl.AutoSize = true; formats.Controls.Add(_step); formats.Controls.Add(_stl);
            layout.Controls.Add(formats, 1, 2); layout.SetColumnSpan(formats, 2);
            AddLabel(layout, "图纸查找方式", 3);
            var drawingLookup = new Label { Text = "SOLIDWORKS 打开工程图（模型同目录、同文件名）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(drawingLookup, 1, 3); layout.SetColumnSpan(drawingLookup, 2);
            var hint = new Label { Text = "找不到同名图纸时按无图纸处理；不会扫描其他目录。导出前会再次校验模型、哈希和版本。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(hint, 1, 4); layout.SetColumnSpan(hint, 2);
            _preview.Multiline = true; _preview.ReadOnly = true; _preview.ScrollBars = ScrollBars.Both; _preview.WordWrap = false;
            _preview.Font = new Font(FontFamily.GenericMonospace, 9f); _preview.Dock = DockStyle.Fill;
            layout.Controls.Add(_preview, 0, 5); layout.SetColumnSpan(_preview, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += delegate { Close(); };
            _export.Text = "导出"; _export.AutoSize = true; _export.Enabled = false; _export.Click += ExportClicked;
            var analyze = new Button { Text = "分类预览", AutoSize = true }; analyze.Click += AnalyzeClicked;
            buttons.Controls.Add(close); buttons.Controls.Add(_export); buttons.Controls.Add(analyze);
            layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 3);
            Controls.Add(layout);

            _assetRoot.TextChanged += InvalidateAnalysis; _projectRoot.TextChanged += InvalidateAnalysis;
            _step.CheckedChanged += InvalidateAnalysis; _stl.CheckedChanged += InvalidateAnalysis;
        }

        private static void AddLabel(TableLayoutPanel layout, string text, int row)
        {
            layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        }

        private void AddPathRow(TableLayoutPanel layout, TextBox textBox, int row)
        {
            textBox.Dock = DockStyle.Fill; layout.Controls.Add(textBox, 1, row);
            var browse = new Button { Text = "浏览...", Dock = DockStyle.Fill };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { SelectedPath = textBox.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) textBox.Text = dialog.SelectedPath;
            };
            layout.Controls.Add(browse, 2, row);
        }

        private void LoadSettings()
        {
            try
            {
                var settings = _store.Load(); _assetRoot.Text = settings.AssetLibraryRoot ?? string.Empty;
                _projectRoot.Text = settings.ProjectExportRoot ?? string.Empty;
                _step.Checked = settings.ProjectMeshFormat == ProjectMeshFormat.Step; _stl.Checked = !_step.Checked;
            }
            catch (Exception ex) { MessageBox.Show(this, "设置读取失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private ExporterSettings ReadSettings()
        {
            return new ExporterSettings
            {
                AssetLibraryRoot = _assetRoot.Text.Trim(), ProjectExportRoot = _projectRoot.Text.Trim(),
                ProjectMeshFormat = _stl.Checked ? ProjectMeshFormat.Stl : ProjectMeshFormat.Step,
                DrawingSearchDirectories = new string[0]
            };
        }

        private void AnalyzeClicked(object sender, EventArgs args)
        {
            Execute(delegate
            {
                var settings = ReadSettings(); _analysis = _coordinator.Analyze(settings); _preview.Text = _analysis.Preview;
                _store.Save(settings); _export.Enabled = true;
            });
        }

        private void ExportClicked(object sender, EventArgs args)
        {
            if (_analysis == null) return;
            if (ShowExportConfirmation(_analysis.Preview) != DialogResult.OK) return;
            Execute(delegate
            {
                var completion = _coordinator.Export(_analysis, ReadSettings());
                MessageBox.Show(this, string.Format("导出完成。\r\nProject: {0}\r\n新建 Asset: {1}\r\n复用 Asset: {2}\r\nProject 复用: {3}",
                    completion.ProjectDirectory, completion.CreatedAssets, completion.ReusedAssets, completion.ProjectReused ? "是" : "否"),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _analysis = null; _export.Enabled = false;
            });
        }

        private DialogResult ShowExportConfirmation(string preview)
        {
            var workingArea = Screen.FromControl(this).WorkingArea;
            var width = Math.Min(900, Math.Max(600, workingArea.Width - 80));
            var height = Math.Min(700, Math.Max(400, workingArea.Height - 80));

            using (var dialog = new Form
            {
                Text = Text,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = true,
                Size = new Size(width, height),
                MinimumSize = new Size(Math.Min(600, width), Math.Min(400, height)),
                Font = Font
            })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

                var prompt = new Label
                {
                    Text = "确认执行以下导出？",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var previewBox = new TextBox
                {
                    Text = preview ?? string.Empty,
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = false,
                    ScrollBars = ScrollBars.Both,
                    Dock = DockStyle.Fill,
                    Font = new Font(FontFamily.GenericMonospace, 9f)
                };
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(0, 8, 0, 0)
                };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
                var confirm = new Button { Text = "确认导出", DialogResult = DialogResult.OK, AutoSize = true };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(confirm);
                layout.Controls.Add(prompt, 0, 0);
                layout.Controls.Add(previewBox, 0, 1);
                layout.Controls.Add(buttons, 0, 2);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = confirm;
                dialog.CancelButton = cancel;
                return dialog.ShowDialog(this);
            }
        }

        private void Execute(Action action)
        {
            try { UseWaitCursor = true; Enabled = false; action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Enabled = true; UseWaitCursor = false; }
        }

        private void InvalidateAnalysis(object sender, EventArgs args)
        {
            _analysis = null; _export.Enabled = false;
        }
    }
}
