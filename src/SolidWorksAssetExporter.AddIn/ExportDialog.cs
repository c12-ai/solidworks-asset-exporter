using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class ExportDialog : Form
    {
        private readonly ExportCoordinator _coordinator;
        private readonly SwManagedVersions _managedVersions;
        private readonly SettingsStore _store;
        private readonly WanxiangApiKeyStore _apiKeyStore;
        private readonly AssemblyParserTokenStore _parserTokenStore = new AssemblyParserTokenStore();
        private readonly WanxiangExportUploader _uploader;
        private readonly TextBox _assetRoot = new TextBox();
        private readonly TextBox _projectRoot = new TextBox();
        private readonly RadioButton _step = new RadioButton();
        private readonly RadioButton _stl = new RadioButton();
        private readonly TextBox _preview = new TextBox();
        private readonly Button _export = new Button();
        private readonly Button _analyze = new Button();
        private readonly Button _checkResults = new Button();
        private readonly Button _close = new Button();
        private readonly Button _cancel = new Button();
        private readonly Button _viewUploadLog = new Button();
        private readonly Label _status = new Label();
        private readonly IList<Button> _browseButtons = new List<Button>();
        private readonly CheckBox _upload = new CheckBox();
        private readonly CheckBox _saveRegistryLocally = new CheckBox();
        private readonly CheckBox _exportProject = new CheckBox();
        private readonly TextBox _serviceUrl = new TextBox();
        private readonly TextBox _apiKey = new TextBox();
        private readonly CheckBox _notifyParser = new CheckBox();
        private readonly TextBox _parserUrl = new TextBox();
        private readonly TextBox _parserToken = new TextBox();
        private readonly ComboBox _parserProjectType = new ComboBox();
        private readonly TextBox _parserProject = new TextBox();
        private readonly TableLayoutPanel _parserOptions = new TableLayoutPanel();
        private readonly RowStyle _parserOptionsRow = new RowStyle(SizeType.Absolute, 0);
        private bool _parserTokenChanged;
        private Button _projectBrowse;
        private AnalysisResult _analysis;
        private WanxiangRegistrySnapshot _previewRegistry;
        private bool _busy;
        private bool _detailsOpen;
        private bool _cancelRequested;
        private string _lastUploadLogPath;
        internal bool Busy { get { return _busy || _detailsOpen; } }
        internal Func<bool> McpRunning { get; set; }

        public ExportDialog(SldWorks application)
        {
            _coordinator = new ExportCoordinator(application); _store = new SettingsStore();
            _managedVersions = new SwManagedVersions(application);
            _apiKeyStore = new WanxiangApiKeyStore(); _uploader = new WanxiangExportUploader();
            Text = "SOLIDWORKS Asset / assembly_package 混合导出"; StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(850, 720); Size = new Size(980, 860); Font = SystemFonts.MessageBoxFont;
            BuildUi(); LoadSettings();
            FormClosing += ExportDialogFormClosing;
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 15 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(_parserOptionsRow);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            AddLabel(layout, "Asset 本地输出根目录", 0); AddPathRow(layout, _assetRoot, 0);
            AddLabel(layout, "assembly_package 本地输出根目录", 1); _projectBrowse = AddPathRow(layout, _projectRoot, 1);
            _exportProject.Text = "导出 assembly_package（装配包；Robot 仅写 asset_id 地址）";
            _exportProject.AutoSize = true; _exportProject.Dock = DockStyle.Fill;
            layout.Controls.Add(_exportProject, 1, 2); layout.SetColumnSpan(_exportProject, 2);
            AddLabel(layout, "XML assembly_package mesh", 3);
            var formats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _step.Text = "STEP"; _step.AutoSize = true; _stl.Text = "STL"; _stl.AutoSize = true; formats.Controls.Add(_step); formats.Controls.Add(_stl);
            layout.Controls.Add(formats, 1, 3); layout.SetColumnSpan(formats, 2);
            AddLabel(layout, "图纸查找方式", 4);
            var drawingLookup = new Label { Text = "文件系统查找同目录、同文件名（预览不打开工程图）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(drawingLookup, 1, 4); layout.SetColumnSpan(drawingLookup, 2);

            _upload.Text = "本地导出成功后直接上传 Wanxiang 数据服务"; _upload.AutoSize = true; _upload.Dock = DockStyle.Fill;
            layout.Controls.Add(_upload, 1, 5); layout.SetColumnSpan(_upload, 2);
            AddLabel(layout, "数据服务地址", 6); _serviceUrl.Dock = DockStyle.Fill;
            layout.Controls.Add(_serviceUrl, 1, 6); layout.SetColumnSpan(_serviceUrl, 2);
            AddLabel(layout, "API key", 7); _apiKey.Dock = DockStyle.Fill; _apiKey.UseSystemPasswordChar = true;
            layout.Controls.Add(_apiKey, 1, 7); layout.SetColumnSpan(_apiKey, 2);
            _saveRegistryLocally.Text = "将 Wanxiang asset-registry.json 同步保存到本地（默认不保存）";
            _saveRegistryLocally.AutoSize = true; _saveRegistryLocally.Dock = DockStyle.Fill;
            layout.Controls.Add(_saveRegistryLocally, 1, 8); layout.SetColumnSpan(_saveRegistryLocally, 2);
            AddParserOptions(layout);

            var hint = new Label { Text = "本地与 Wanxiang 顶层目录统一为 assembly_package；Robot 仅在装配包 XML 中写 asset_id。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(hint, 0, 11); layout.SetColumnSpan(hint, 3);
            _preview.Multiline = true; _preview.ReadOnly = true; _preview.ScrollBars = ScrollBars.Both; _preview.WordWrap = false;
            _preview.Font = new Font(FontFamily.GenericMonospace, 9f); _preview.Dock = DockStyle.Fill;
            layout.Controls.Add(_preview, 0, 12); layout.SetColumnSpan(_preview, 3);

            _status.Text = "请先执行分类预览。"; _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft; _status.ForeColor = Color.DimGray;
            layout.Controls.Add(_status, 0, 13); layout.SetColumnSpan(_status, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            _close.Text = "关闭"; _close.AutoSize = true; _close.Click += delegate { Close(); };
            _export.Text = "导出"; _export.AutoSize = true; _export.Enabled = false; _export.Click += ExportClicked;
            _analyze.Text = "分类预览"; _analyze.AutoSize = true; _analyze.Click += AnalyzeClicked;
            _checkResults.Text = "检查结果…"; _checkResults.AutoSize = true; _checkResults.Enabled = false;
            _checkResults.Click += delegate { ShowPreviewIssues(); };
            _cancel.Text = "取消导出"; _cancel.AutoSize = true; _cancel.Visible = false;
            _cancel.Click += delegate { RequestCancellation(); };
            _viewUploadLog.Text = "查看上传日志"; _viewUploadLog.AutoSize = true;
            _viewUploadLog.Click += delegate { OpenUploadLog(); };
            buttons.Controls.Add(_close); buttons.Controls.Add(_export); buttons.Controls.Add(_analyze);
            buttons.Controls.Add(_checkResults); buttons.Controls.Add(_cancel); buttons.Controls.Add(_viewUploadLog);
            layout.Controls.Add(buttons, 0, 14); layout.SetColumnSpan(buttons, 3);
            Controls.Add(layout);

            _assetRoot.TextChanged += InvalidateAnalysis; _projectRoot.TextChanged += InvalidateAnalysis;
            _step.CheckedChanged += InvalidateAnalysis; _stl.CheckedChanged += InvalidateAnalysis;
            _upload.CheckedChanged += delegate(object sender, EventArgs args)
            {
                UpdateParserControls();
                InvalidateAnalysis(sender, args);
            };
            _serviceUrl.TextChanged += InvalidateAnalysis;
            _apiKey.TextChanged += InvalidateAnalysis;
            _saveRegistryLocally.CheckedChanged += InvalidateAnalysis;
            _exportProject.CheckedChanged += delegate(object sender, EventArgs args)
            {
                UpdateProjectControls(_busy);
                UpdateParserControls();
                InvalidateAnalysis(sender, args);
            };
        }

        private void AddParserOptions(TableLayoutPanel layout)
        {
            _notifyParser.Text = "上传装配包后自动提交解析";
            _notifyParser.AutoSize = true;
            _notifyParser.Dock = DockStyle.Fill;
            _notifyParser.CheckedChanged += delegate { UpdateParserControls(); };
            layout.Controls.Add(_notifyParser, 1, 9);
            layout.SetColumnSpan(_notifyParser, 2);

            _parserOptions.Dock = DockStyle.Fill;
            _parserOptions.Margin = Padding.Empty;
            _parserOptions.ColumnCount = 2;
            _parserOptions.RowCount = 3;
            _parserOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _parserOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < _parserOptions.RowCount; row++)
                _parserOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            AddLabel(_parserOptions, "解析服务地址", 0);
            _parserUrl.Dock = DockStyle.Fill;
            _parserUrl.AccessibleName = "解析服务地址";
            _parserOptions.Controls.Add(_parserUrl, 1, 0);

            AddLabel(_parserOptions, "装配提供方令牌", 1);
            _parserToken.Dock = DockStyle.Fill;
            _parserToken.UseSystemPasswordChar = true;
            _parserToken.AccessibleName = "装配提供方令牌";
            _parserToken.TextChanged += delegate { _parserTokenChanged = true; };
            _parserOptions.Controls.Add(_parserToken, 1, 1);

            AddLabel(_parserOptions, "Wanxiang 项目", 2);
            var project = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
            project.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            project.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _parserProjectType.DropDownStyle = ComboBoxStyle.DropDownList;
            _parserProjectType.Items.AddRange(new object[] { "项目编号", "项目名称" });
            _parserProjectType.SelectedIndex = 0;
            _parserProjectType.Dock = DockStyle.Fill;
            _parserProjectType.AccessibleName = "项目匹配方式";
            _parserProject.Dock = DockStyle.Fill;
            _parserProject.AccessibleName = "项目编号或名称";
            project.Controls.Add(_parserProjectType, 0, 0);
            project.Controls.Add(_parserProject, 1, 0);
            _parserOptions.Controls.Add(project, 1, 2);

            layout.Controls.Add(_parserOptions, 0, 10);
            layout.SetColumnSpan(_parserOptions, 3);
        }

        private static void AddLabel(TableLayoutPanel layout, string text, int row)
        {
            layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        }

        private Button AddPathRow(TableLayoutPanel layout, TextBox textBox, int row)
        {
            textBox.Dock = DockStyle.Fill; layout.Controls.Add(textBox, 1, row);
            var browse = new Button { Text = "浏览...", Dock = DockStyle.Fill };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { SelectedPath = textBox.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) textBox.Text = dialog.SelectedPath;
            };
            layout.Controls.Add(browse, 2, row);
            _browseButtons.Add(browse);
            return browse;
        }

        private void LoadSettings()
        {
            try
            {
                var settings = _store.Load(); _assetRoot.Text = settings.AssetLibraryRoot ?? string.Empty;
                _projectRoot.Text = settings.ProjectExportRoot ?? string.Empty;
                _exportProject.Checked = settings.ExportProject;
                _step.Checked = settings.ProjectMeshFormat == ProjectMeshFormat.Step; _stl.Checked = !_step.Checked;
                _upload.Checked = settings.UploadAfterExport; _serviceUrl.Text = settings.WanxiangBaseUrl;
                _apiKey.Text = _apiKeyStore.Load();
                _saveRegistryLocally.Checked = settings.SaveRegistryLocally;
                _parserUrl.Text = settings.AssemblyParserBaseUrl ?? string.Empty;
                var useProjectName = string.IsNullOrWhiteSpace(settings.AssemblyParserProjectCode) &&
                    !string.IsNullOrWhiteSpace(settings.AssemblyParserProjectName);
                _parserProjectType.SelectedIndex = useProjectName ? 1 : 0;
                _parserProject.Text = (useProjectName ? settings.AssemblyParserProjectName : settings.AssemblyParserProjectCode) ?? string.Empty;
                _notifyParser.Checked = settings.NotifyAssemblyParser;
                _lastUploadLogPath = WanxiangUploadLog.FindLatest();
                _viewUploadLog.Enabled = !string.IsNullOrWhiteSpace(_lastUploadLogPath);
                UpdateProjectControls(false);
                UpdateParserControls();
                _parserToken.Text = _parserTokenStore.Load();
                _parserTokenChanged = false;
            }
            catch (Exception ex) { MessageBox.Show(this, "设置读取失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private ExporterSettings ReadSettings()
        {
            return new ExporterSettings
            {
                AssetLibraryRoot = _assetRoot.Text.Trim(), ProjectExportRoot = _projectRoot.Text.Trim(),
                ProjectMeshFormat = _stl.Checked ? ProjectMeshFormat.Stl : ProjectMeshFormat.Step,
                DrawingSearchDirectories = new string[0], UploadAfterExport = _upload.Checked,
                WanxiangBaseUrl = _serviceUrl.Text.Trim(),
                WanxiangApiKey = _apiKey.Text.Trim(), SaveRegistryLocally = _saveRegistryLocally.Checked,
                ExportProject = _exportProject.Checked,
                NotifyAssemblyParser = _notifyParser.Checked,
                AssemblyParserBaseUrl = _parserUrl.Text.Trim(),
                AssemblyParserProviderToken = _parserToken.Text.Trim(),
                AssemblyParserProjectCode = _parserProjectType.SelectedIndex == 0 ? _parserProject.Text : null,
                AssemblyParserProjectName = _parserProjectType.SelectedIndex == 1 ? _parserProject.Text.Trim() : null
            };
        }

        private void SaveSettings(ExporterSettings settings)
        {
            _store.Save(settings);
            if (!_parserTokenChanged) return;
            _parserTokenStore.Save(settings.AssemblyParserProviderToken);
            _parserTokenChanged = false;
        }

        private async void AnalyzeClicked(object sender, EventArgs args)
        {
            if (McpRunning != null && McpRunning()) return;
            try
            {
                SetBusy(true, false);
                var settings = ReadSettings(); settings.Validate();
                _apiKeyStore.Save(settings.WanxiangApiKey);
                SaveSettings(settings);
                _preview.Text = "正在通过 Wanxiang GET /asset/registry 读取资产注册表...";
                _status.Text = "读取 Wanxiang 远程注册表...";
                _preview.Refresh();
                var registry = await Task.Run(delegate { return _coordinator.FetchWanxiangRegistry(settings); });
                _preview.Text = settings.ExportProject
                    ? "正在读取 Asset/assembly_package 源文件和内容指纹，并在分类预览阶段判断 asset_version/assembly_version..."
                    : "正在读取 Asset 源文件和内容指纹；本次不导出 assembly_package...";
                _status.Text = settings.ExportProject
                    ? "分类装配树、检查 Asset/assembly_package 版本并建立导出快照..."
                    : "分类装配树、检查 Asset 版本并建立导出快照...";
                _preview.Refresh();
                _analysis = _coordinator.Analyze(settings, registry); _preview.Text = _analysis.Preview;
                _previewRegistry = registry;
                _export.Enabled = _analysis.CanExport;
                _status.Text = _analysis.CanExport ? "预览完成，可以导出。" : "预览完成，请先处理检查结果中的问题。";
                _status.ForeColor = _analysis.CanExport ? Color.DarkGreen : Color.DarkRed;
            }
            catch (Exception ex)
            {
                _analysis = null; _export.Enabled = false;
                _status.Text = "预览失败。";
                _status.ForeColor = Color.DarkRed;
                ShowDetails("分类预览失败", "请处理以下问题后重新预览。", ex.Message);
            }
            finally { SetBusy(false, false); }

            if (_analysis != null && !_analysis.CanExport) ShowPreviewIssues();
        }

        private void ShowPreviewIssues()
        {
            if (_analysis == null || _analysis.CanExport) return;
            if (McpRunning != null && McpRunning()) return;
            var upgrades = _analysis.AssetInspections.Values
                .Where(asset => SwManagedVersions.SuggestedVersion(asset).HasValue).ToList();
            var notices = new List<string>();
            if (_analysis.UnsupportedAssetRoots.Count != 0)
                notices.Add("请将以下 " + _analysis.UnsupportedAssetRoots.Count +
                    " 个虚拟/内嵌 Asset 根保存为外部 SLDASM/SLDPRT，并保存引用它们的父装配体。\r\n\r\n" +
                    string.Join("\r\n", _analysis.UnsupportedAssetRoots));
            if (upgrades.Count != 0)
            {
                var lines = new List<string>();
                foreach (var asset in upgrades)
                {
                    var reason = string.IsNullOrWhiteSpace(asset.SemanticMismatchReason)
                        ? (asset.RequiresVersionUpgrade ? "当前内容指纹未匹配已注册版本。" : "当前内容匹配另一个已注册版本。")
                        : asset.SemanticMismatchReason;
                    lines.Add(asset.SourcePath + "\r\n    asset_version  v" + asset.Version +
                        " → 建议 v" + SwManagedVersions.SuggestedVersion(asset) + "    " + reason);
                }
                notices.Add("检测到 " + upgrades.Count + " 个 Asset 需要同步版本。\r\n\r\n" +
                    string.Join("\r\n\r\n", lines));
            }
            if (_analysis.SuggestedProjectVersion.HasValue) notices.Add(_analysis.ProjectVersionMessage);
            notices.AddRange(_analysis.ValidationErrors);
            var details = notices.Count == 0 ? _analysis.Preview : string.Join("\r\n\r\n", notices);
            if (ShowDetails("分类预览检查结果", "处理完问题后，切回总装配体重新执行分类预览。", details,
                applyVersions: SwManagedVersions.CanPrepare(_analysis)) == DialogResult.Retry)
                ApplySuggestedVersions();
        }

        private async void ApplySuggestedVersions()
        {
            try
            {
                SetBusy(true, false);
                var settings = ReadSettings();
                var previous = _analysis;
                var registry = await Task.Run(() => _coordinator.FetchWanxiangRegistry(settings));
                _managedVersions.RequireTarget(previous);
                var current = _coordinator.Analyze(settings, registry);
                var saved = new List<string>();
                _analysis = _managedVersions.Prepare(_coordinator, settings, registry, current, saved);
                _previewRegistry = registry;
                _preview.Text = _analysis.Preview;
                _status.Text = _analysis.CanExport ? "插件版本已校验，可以导出。" : "请查看剩余预览问题。";
                _status.ForeColor = _analysis.CanExport ? Color.DarkGreen : Color.DarkRed;
            }
            catch (Exception ex)
            {
                _analysis = null;
                _status.Text = "版本维护未完成，请重新预览。";
                ShowDetails("版本维护未完成", "请检查以下信息。", ex.Message);
            }
            finally { SetBusy(false, false); }
        }

        private async void ExportClicked(object sender, EventArgs args)
        {
            if (_analysis == null || (McpRunning != null && McpRunning())) return;
            var settings = ReadSettings();
            try { settings.ValidateAssemblyParser(); }
            catch (ValidationException ex)
            {
                MessageBox.Show(this, ex.Message, "请检查解析设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var details = _analysis.Preview;
            if (settings.NotifyAssemblyParser && settings.UploadAfterExport && settings.ExportProject)
            {
                var useProjectName = string.IsNullOrWhiteSpace(settings.AssemblyParserProjectCode);
                details += "\r\n\r\n解析服务：" + settings.AssemblyParserBaseUrl + "\r\nWanxiang 项目" +
                    (useProjectName ? "名称：" + settings.AssemblyParserProjectName : "编号：" + settings.AssemblyParserProjectCode);
            }
            if (ShowDetails(Text, "确认执行以下导出？", details, "确认导出") != DialogResult.OK) return;
            var localCompleted = false;
            try
            {
                _cancelRequested = false;
                SetBusy(true, true);
                settings.Validate();
                _apiKeyStore.Save(settings.WanxiangApiKey);
                SaveSettings(settings);
                _analysis = _managedVersions.Prepare(_coordinator, settings, _previewRegistry, _analysis, new List<string>());
                if (!_analysis.CanExport) throw new ValidationException(_analysis.Preview);
                var completion = _coordinator.Export(_analysis, settings, ShowExportProgress,
                    delegate { return _cancelRequested; });
                localCompleted = true;
                WanxiangUploadCompletion upload = null;
                if (settings.UploadAfterExport)
                {
                    SetBusy(true, false);
                    _status.Text = "本地导出完成，正在上传 Wanxiang 数据服务...";
                    _status.ForeColor = Color.DarkBlue;
                    _status.Refresh();
                    upload = await Task.Run(delegate
                    {
                        try
                        {
                            var result = _uploader.Upload(completion, settings, ReportUploadProgress);
                            ReportUploadProgress("Wanxiang 后台上传任务已结束，正在刷新完成界面...");
                            return result;
                        }
                        catch
                        {
                            ReportUploadProgress("Wanxiang 后台上传任务已结束，正在显示错误信息...");
                            throw;
                        }
                    });
                    _lastUploadLogPath = upload.UploadLogPath;
                    _viewUploadLog.Enabled = File.Exists(_lastUploadLogPath);
                }
                var uploadText = upload == null ? "未启用" : string.Format(
                    "完成（Asset 原子发布 {0}，新注册 {1}，幂等复用 {2}，Wanxiang assembly_package {3}，注册表 {4}）\r\n上传日志: {5}",
                    upload.AssetDirectoriesUploaded, upload.AssetVersionsRegistered,
                    upload.AssetVersionsAlreadyRegistered, upload.RemoteProjectDirectory,
                    upload.RemoteRegistryPath, upload.UploadLogPath);
                var parseFailed = upload != null && !string.IsNullOrWhiteSpace(upload.AssemblyParseError);
                var parseAccepted = upload != null && !string.IsNullOrWhiteSpace(upload.AssemblyParseJobId);
                if (parseFailed) uploadText += "\r\n解析通知失败：" + upload.AssemblyParseError;
                else if (parseAccepted) uploadText += "\r\nWanxiang 已受理解析任务 " + upload.AssemblyParseJobId +
                    "\r\n事件 ID：" + upload.AssemblyParseEventId;
                var localBackupText = completion.LocalPackageBackups.Count == 0 ? "无" :
                    completion.LocalPackageBackups.Count + " 个（保存在 Asset 根目录的 .local-package-backups 中）";
                MessageBox.Show(this, string.Format("导出完成。\r\nassembly_package: {0}\r\n新建 Asset: {1}\r\n复用 Asset: {2}\r\nassembly_package 复用: {3}\r\n本地冲突包备份: {4}\r\n远端上传: {5}",
                    settings.ExportProject ? completion.ProjectDirectory : "未选择导出",
                    completion.CreatedAssets, completion.ReusedAssets,
                    settings.ExportProject ? (completion.ProjectReused ? "是" : "否") : "不适用", localBackupText, uploadText),
                    Text, MessageBoxButtons.OK, parseFailed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                _analysis = null; _export.Enabled = false;
                _status.Text = parseFailed ? "上传完成，解析通知失败。请查看上传日志。" :
                    parseAccepted ? "上传完成，Wanxiang 已受理解析任务。" : "导出完成。";
                _status.ForeColor = parseFailed ? Color.DarkRed : Color.DarkGreen;
                _status.Refresh();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "导出已取消。";
                MessageBox.Show(this, "导出已取消。\r\n已完整提交的 Asset 会保留并在下次导出时复用；未提交的临时目录会自动清理。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                var prefix = localCompleted ? "本地导出已完成，但上传失败；本地文件不会删除，可直接重试。\r\n" : string.Empty;
                if (localCompleted)
                {
                    _lastUploadLogPath = WanxiangUploadLog.FindLatest();
                    _viewUploadLog.Enabled = File.Exists(_lastUploadLogPath);
                    if (!string.IsNullOrWhiteSpace(_lastUploadLogPath))
                        prefix += "上传日志: " + _lastUploadLogPath + "\r\n";
                }
                _status.Text = localCompleted ? "上传失败，本地导出已保留。" : "导出失败。";
                _status.ForeColor = Color.DarkRed;
                MessageBox.Show(this, prefix + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, false);
                _export.Enabled = _analysis != null && _analysis.CanExport;
                _viewUploadLog.Enabled = File.Exists(_lastUploadLogPath);
                _status.Refresh();
                Update();
            }
        }

        private void ShowExportProgress(string phase)
        {
            _status.Text = phase + "（取消会在当前 SOLIDWORKS 操作结束后生效）";
            _status.Refresh();
            Application.DoEvents();
        }

        private void ReportUploadProgress(string phase)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(ReportUploadProgress), phase); }
                catch (InvalidOperationException) { }
                return;
            }
            var latest = WanxiangUploadLog.FindLatest();
            if (!string.IsNullOrWhiteSpace(latest)) _lastUploadLogPath = latest;
            _viewUploadLog.Enabled = File.Exists(_lastUploadLogPath);
            _status.Text = phase;
            _status.ForeColor = phase.IndexOf("失败", StringComparison.Ordinal) >= 0
                ? Color.DarkRed : Color.DarkBlue;
            _status.Refresh();
            Update();
        }

        private void OpenUploadLog()
        {
            var path = !string.IsNullOrWhiteSpace(_lastUploadLogPath)
                ? _lastUploadLogPath : WanxiangUploadLog.FindLatest();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "还没有可查看的 Wanxiang 上传日志。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开上传日志：" + ex.Message + "\r\n" + path,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RequestCancellation()
        {
            if (!_busy || !_cancel.Visible || _cancelRequested) return;
            _cancelRequested = true;
            _cancel.Enabled = false;
            _status.Text = "已请求取消，正在等待当前 SOLIDWORKS 操作结束...";
            _status.Refresh();
        }

        private void SetBusy(bool busy, bool canCancel)
        {
            _busy = busy;
            _assetRoot.Enabled = !busy; _exportProject.Enabled = !busy;
            _upload.Enabled = !busy;
            _serviceUrl.Enabled = !busy; _apiKey.Enabled = !busy;
            _saveRegistryLocally.Enabled = !busy;
            foreach (var browse in _browseButtons) browse.Enabled = !busy;
            UpdateProjectControls(busy);
            UpdateParserControls();
            _analyze.Enabled = !busy; _close.Enabled = !busy;
            _export.Enabled = !busy && _analysis != null && _analysis.CanExport;
            _checkResults.Enabled = !busy && _analysis != null && !_analysis.CanExport;
            _cancel.Visible = busy && canCancel;
            _cancel.Enabled = busy && canCancel && !_cancelRequested;
            UseWaitCursor = busy && !canCancel;
        }

        private void UpdateProjectControls(bool busy)
        {
            var enabled = !busy && _exportProject.Checked;
            _projectRoot.Enabled = enabled;
            if (_projectBrowse != null) _projectBrowse.Enabled = enabled;
            _step.Enabled = enabled;
            _stl.Enabled = enabled;
        }

        private void UpdateParserControls()
        {
            var canNotify = _exportProject.Checked && _upload.Checked;
            var showOptions = canNotify && _notifyParser.Checked;
            _notifyParser.Enabled = canNotify && !_busy;
            _parserOptions.Enabled = !_busy;
            _parserOptions.Visible = showOptions;
            _parserOptionsRow.Height = showOptions ? 102 : 0;
        }

        private void ExportDialogFormClosing(object sender, FormClosingEventArgs args)
        {
            if (!_busy) return;
            args.Cancel = true;
            RequestCancellation();
        }

        private DialogResult ShowDetails(string title, string promptText, string details,
            string confirmText = null, bool applyVersions = false)
        {
            var workingArea = Screen.FromControl(this).WorkingArea;
            var width = Math.Min(900, workingArea.Width - 32);
            var height = Math.Min(700, workingArea.Height - 32);

            using (var dialog = new Form
            {
                Text = title,
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
                    Text = promptText,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var previewBox = new TextBox
                {
                    Text = details ?? string.Empty,
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
                var cancel = new Button { Text = confirmText == null ? "关闭" : "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
                buttons.Controls.Add(cancel);
                if (confirmText != null)
                {
                    var confirm = new Button { Text = confirmText, DialogResult = DialogResult.OK, AutoSize = true };
                    buttons.Controls.Add(confirm);
                    dialog.AcceptButton = confirm;
                }

                if (applyVersions)
                    buttons.Controls.Add(new Button { Text = "应用插件建议版本", AutoSize = true, DialogResult = DialogResult.Retry });
                layout.Controls.Add(prompt, 0, 0);
                layout.Controls.Add(previewBox, 0, 1);
                layout.Controls.Add(buttons, 0, 2);
                dialog.Controls.Add(layout);
                dialog.CancelButton = cancel;
                _detailsOpen = true;
                try { return dialog.ShowDialog(this); }
                finally { _detailsOpen = false; }
            }
        }

        private void InvalidateAnalysis(object sender, EventArgs args)
        {
            _analysis = null; _export.Enabled = false; _checkResults.Enabled = false;
        }
    }
}
