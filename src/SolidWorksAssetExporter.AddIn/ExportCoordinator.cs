using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class AnalysisResult
    {
        internal AnalysisResult()
        {
            AssetInspections = new Dictionary<string, AssetInspection>(StringComparer.OrdinalIgnoreCase);
            AssetVersionMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            UnsupportedAssetRoots = new List<string>();
            ProjectFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SourceFileSnapshots = new Dictionary<string, ExportSourceFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            DocumentUpdateStamps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CanExport = true;
        }
        public AssemblyExportPlan Plan { get; internal set; }
        public string Preview { get; internal set; }
        public string PlanFingerprint { get; internal set; }
        public string ProjectFingerprint { get; internal set; }
        public bool CanExport { get; internal set; }
        public IList<string> UnsupportedAssetRoots { get; private set; }
        internal IDictionary<string, AssetInspection> AssetInspections { get; private set; }
        internal IDictionary<string, string> AssetVersionMessages { get; private set; }
        internal IDictionary<string, string> ProjectFingerprints { get; private set; }
        internal IDictionary<string, ExportSourceFileSnapshot> SourceFileSnapshots { get; private set; }
        internal IDictionary<string, int> DocumentUpdateStamps { get; private set; }
        internal string ActiveDocumentPath { get; set; }
        internal string ActiveConfigurationName { get; set; }
    }

    internal sealed class AssetInspection
    {
        public SwCadNode Node { get; set; }
        public string Uuid { get; set; }
        public string Fingerprint { get; set; }
        public int Version { get; set; }
        public ExistingAssetState State { get; set; }
        public IList<string> ModelFiles { get; set; }
        public IList<string> Drawings { get; set; }
        public AssetVersionDecision VersionDecision { get; set; }
        public bool RequiresVersionUpgrade { get; set; }
        public int SuggestedVersion { get; set; }
        public bool IsEmbeddedAssetRoot { get; set; }
        public bool RequiresLocalPackageRebuild { get; set; }
        public string LocalPackageError { get; set; }
        public bool IsRobotAsset { get; set; }
        public IDictionary<string, string> Properties { get; set; }
    }

    public sealed class ExportCompletion
    {
        public ExportCompletion()
        {
            AssetDirectories = new List<string>();
            AssetRegistrations = new List<AssetRegistration>();
            LocalPackageBackups = new List<string>();
        }
        public string ProjectDirectory { get; set; }
        public bool ProjectReused { get; set; }
        public int CreatedAssets { get; set; }
        public int ReusedAssets { get; set; }
        public IList<string> AssetDirectories { get; private set; }
        public IList<AssetRegistration> AssetRegistrations { get; private set; }
        public IList<string> LocalPackageBackups { get; private set; }
    }

    public sealed class ExportCoordinator
    {
        private readonly SldWorks _app;
        private readonly SwSourcePackager _packager;
        private readonly SwDrawingExporter _drawings;
        private readonly SwGeometryExporter _geometry;
        private readonly SwPluginMutationTracker _mutationTracker;
        private readonly WanxiangRegistryProvider _registryProvider;

        public ExportCoordinator(SldWorks app)
        {
            _app = app; _mutationTracker = new SwPluginMutationTracker(app);
            _packager = new SwSourcePackager(app, _mutationTracker);
            _drawings = new SwDrawingExporter();
            _geometry = new SwGeometryExporter(app, _mutationTracker);
            _registryProvider = new WanxiangRegistryProvider();
        }

        public AnalysisResult Analyze(ExporterSettings settings)
        {
            settings.Validate();
            return Analyze(settings, _registryProvider.Fetch(settings));
        }

        internal WanxiangRegistrySnapshot FetchWanxiangRegistry(ExporterSettings settings)
        {
            settings.Validate();
            return _registryProvider.Fetch(settings);
        }

        internal AnalysisResult Analyze(ExporterSettings settings, WanxiangRegistrySnapshot registrySnapshot)
        {
            if (registrySnapshot == null || registrySnapshot.Registry == null)
                throw new ArgumentNullException("registrySnapshot");
            settings.Validate();
            var root = SwAssemblyRoot.FromActiveDocument(_app, _mutationTracker);
            var scan = new AssemblyScanner().Scan(root);
            var plan = new ExportPlanBuilder().Build(scan, settings.ProjectMeshFormat);
            var result = new AnalysisResult
            {
                Plan = plan,
                PlanFingerprint = CalculatePlanFingerprint(plan)
            };
            var assetNodes = Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Asset).ToList();
            var registry = registrySnapshot.Registry;
            var inspectionCache = new Dictionary<string, AssetInspection>(StringComparer.OrdinalIgnoreCase);
            using (var fileHashes = new FileFingerprintCache())
            {
                foreach (var group in assetNodes.GroupBy(node => node.AssetId, StringComparer.OrdinalIgnoreCase))
                {
                    var embeddedRoots = group.Select(node => (SwCadNode)node.Source)
                        .Where(source => AssetSourcePathPolicy.IsSessionEmbeddedModelPath(source.SourcePath))
                        .GroupBy(source => source.SourcePath, StringComparer.OrdinalIgnoreCase)
                        .Select(values => values.First()).ToList();
                    if (embeddedRoots.Count != 0)
                    {
                        var embeddedAssetNode = group.First();
                        var source = embeddedRoots[0];
                        var version = int.Parse(embeddedAssetNode.AssetId.Substring(
                            embeddedAssetNode.AssetId.LastIndexOf(':') + 1),
                            CultureInfo.InvariantCulture);
                        result.CanExport = false;
                        result.AssetInspections[group.Key] = new AssetInspection
                        {
                            Node = source,
                            Uuid = embeddedAssetNode.GeometryUuid,
                            Version = version,
                            State = ExistingAssetState.Missing,
                            ModelFiles = new List<string>(),
                            Drawings = new List<string>(),
                            IsEmbeddedAssetRoot = true
                        };
                        result.AssetVersionMessages[group.Key] =
                            "[无法导出：Asset 根是 SOLIDWORKS 虚拟/内嵌组件；请先保存为外部 SLDASM/SLDPRT 并重新预览]";
                        foreach (var embeddedRoot in embeddedRoots)
                            if (!result.UnsupportedAssetRoots.Contains(embeddedRoot.SourcePath,
                                StringComparer.OrdinalIgnoreCase))
                                result.UnsupportedAssetRoots.Add(embeddedRoot.SourcePath);
                        continue;
                    }

                    var inspections = new List<AssetInspection>();
                    foreach (var node in group)
                    {
                        var source = (SwCadNode)node.Source;
                        var cacheKey = Canonical.Join(source.SourcePath, source.ReferencedConfiguration, node.GeometryUuid);
                        AssetInspection inspection;
                        if (!inspectionCache.TryGetValue(cacheKey, out inspection))
                        {
                            inspection = InspectAsset(source, node, fileHashes);
                            inspectionCache.Add(cacheKey, inspection);
                        }
                        inspections.Add(inspection);
                    }
                    var fingerprints = inspections.Select(value => value.Fingerprint)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (fingerprints.Count != 1)
                    {
                        result.CanExport = false;
                        result.AssetVersionMessages[group.Key] = "[错误：同一 asset_id 的多个实例内容指纹不同]";
                        result.AssetInspections[group.Key] = inspections[0];
                        continue;
                    }

                    var current = inspections[0];
                    var assetNode = group.First();
                    var decision = AssetVersionPolicy.Evaluate(assetNode.GeometryUuid, current.Version,
                        current.Fingerprint, registry.Assets);
                    current.VersionDecision = decision;
                    if (decision.Kind == AssetVersionDecisionKind.UpgradeRequired)
                    {
                        current.RequiresVersionUpgrade = true;
                        current.SuggestedVersion = decision.SuggestedVersion ?? current.Version + 1;
                    }
                    string packageError = null;
                    if (decision.CanExport)
                    {
                        try
                        {
                            current.State = AssetManifestValidator.Inspect(
                                AssetVersionDirectory(settings, assetNode.GeometryUuid, current.Version),
                                assetNode.GeometryUuid, current.Version, current.Fingerprint, fileHashes.Sha256);
                        }
                        catch (ValidationException ex)
                        {
                            packageError = ex.Message;
                            // Wanxiang is the version authority. A conflicting or damaged local
                            // package is only a rebuildable cache problem when the remote version
                            // decision itself is valid; it must never force asset_version upward.
                            current.State = ExistingAssetState.Missing;
                            current.RequiresLocalPackageRebuild = true;
                            current.LocalPackageError = ex.Message;
                        }
                    }
                    else result.CanExport = false;
                    result.AssetInspections[group.Key] = current;
                    result.AssetVersionMessages[group.Key] = DescribeVersionDecision(current, packageError) +
                        (current.IsRobotAsset ? " [Robot：纯元数据，不导出 STEP/STL/SLDASM/SLDPRT/SLDDRW]" : string.Empty);
                }
                CaptureSourceSnapshots(result, root, fileHashes);
            }
            result.Preview = BuildPreview(plan.Roots, result.AssetVersionMessages, registrySnapshot,
                settings.SaveRegistryLocally);
            return result;
        }

        internal IList<string> OpenAssetsRequiringVersionUpgrade(AnalysisResult analysis)
        {
            var results = new List<string>();
            if (analysis == null) return results;
            var inspections = analysis.AssetInspections.Values
                .Where(value => value != null && value.RequiresVersionUpgrade && value.Node != null &&
                    !value.IsEmbeddedAssetRoot &&
                    !AssetSourcePathPolicy.IsSessionEmbeddedModelPath(value.Node.SourcePath))
                .GroupBy(value => Path.GetFullPath(value.Node.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            foreach (var inspection in inspections)
            {
                var path = Path.GetFullPath(inspection.Node.SourcePath);
                var suggested = inspection.SuggestedVersion > inspection.Version
                    ? inspection.SuggestedVersion : inspection.Version + 1;
                try
                {
                    if (!File.Exists(path))
                    {
                        results.Add("打开失败：" + path + "（源文件不存在）");
                        continue;
                    }
                    var document = _app.GetOpenDocumentByName(path) as ModelDoc2;
                    if (document == null)
                    {
                        int errors = 0, warnings = 0;
                        var documentType = inspection.Node.SourceDocumentKind == DocumentKind.Assembly
                            ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                        document = _app.OpenDoc6(path, documentType,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty,
                            ref errors, ref warnings) as ModelDoc2;
                        if (document == null || errors != 0)
                        {
                            results.Add(string.Format(CultureInfo.InvariantCulture,
                                "打开失败：{0}（errors={1}, warnings={2}）", path, errors, warnings));
                            continue;
                        }
                    }
                    int activationErrors = 0;
                    var active = _app.ActivateDoc3(document.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors) as ModelDoc2;
                    if (active == null)
                    {
                        results.Add("已打开但无法激活：" + path);
                        continue;
                    }
                    var readOnly = false;
                    try { readOnly = active.IsOpenedReadOnly(); }
                    catch { }
                    results.Add((readOnly ? "已打开（只读，需解除只读后修改）" : "已打开") + "：" + path +
                        "\r\n    asset_version：v" + inspection.Version.ToString(CultureInfo.InvariantCulture) +
                        " → 建议 v" + suggested.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    results.Add("打开失败：" + path + "（" + ex.Message + "）");
                }
            }
            return results;
        }

        private AnalysisResult InspectForExport(AnalysisResult result, ExporterSettings settings,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            result.ProjectFingerprints.Clear();
            var groups = Flatten(result.Plan.Roots).Where(node => node.Kind == ExportNodeKind.Project)
                .GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase).ToList();
            using (var fileHashes = new FileFingerprintCache())
            {
                for (var index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    Checkpoint(progress, cancellationRequested, "计算 Project 指纹 " +
                        (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                        groups.Count.ToString(CultureInfo.InvariantCulture) + "：" + group.First().Name);
                    // Repeated occurrences share one geometry UUID and one exported mesh. Calculate
                    // each distinct source/configuration once instead of reopening every instance.
                    var sources = group.Select(node => (SwCadNode)node.Source)
                        .GroupBy(ProjectSourceKey, StringComparer.OrdinalIgnoreCase)
                        .Select(values => values.First()).ToList();
                    var fingerprints = sources.Select(source =>
                            _packager.ContentFingerprint(source, fileHashes.Sha256))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (fingerprints.Count != 1) throw new ValidationException("同一 Project 单元 UUID 的多个源模型具有不同内容: " + group.Key);
                    result.ProjectFingerprints.Add(group.Key, fingerprints[0]);
                }
            }

            result.ProjectFingerprint = CalculateProjectFingerprint(result.Plan, result.ProjectFingerprints);
            return result;
        }

        public ExportCompletion Export(AnalysisResult previewed, ExporterSettings settings)
        {
            return Export(previewed, settings, null, null);
        }

        public ExportCompletion Export(AnalysisResult previewed, ExporterSettings settings,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            if (previewed == null) throw new ArgumentNullException("previewed");
            if (!previewed.CanExport) throw new ValidationException("预览发现 Asset 版本需要调整，不能继续导出。");
            settings.Validate();
            RevalidatePreview(previewed, settings, progress, cancellationRequested);

            var activeDocument = _app.ActiveDoc as ModelDoc2;
            var completion = new ExportCompletion();
            using (new SwSelectionScope(activeDocument))
            {
                var current = InspectForExport(previewed, settings, progress, cancellationRequested);
                using (new SwExportPreferenceScope(_app))
                {
                    ExportAssets(current, settings, completion, progress, cancellationRequested);
                    ExportProject(current, settings, completion, progress, cancellationRequested);
                }
            }
            if (progress != null) progress("本地导出完成");
            return completion;
        }

        private void RevalidatePreview(AnalysisResult previewed, ExporterSettings settings,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            Checkpoint(progress, cancellationRequested, "校验预览后的源文件和文档状态");
            var activeDocument = _app.ActiveDoc as ModelDoc2;
            if (activeDocument == null) throw new ValidationException("当前没有活动的 SOLIDWORKS 装配体。");
            var documentPath = activeDocument.GetPathName();
            if (string.IsNullOrWhiteSpace(documentPath))
                throw new ValidationException("当前总装配体尚未保存，请保存后重新预览。");
            var activePath = Path.GetFullPath(documentPath);
            if (!string.Equals(activePath, previewed.ActiveDocumentPath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("活动装配体已切换，请重新预览。");
            var configuration = activeDocument.ConfigurationManager == null
                ? null : activeDocument.ConfigurationManager.ActiveConfiguration;
            var configurationName = configuration == null ? string.Empty : configuration.Name;
            if (!string.Equals(configurationName, previewed.ActiveConfigurationName,
                StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("活动配体配置已切换，请重新预览。");
            if (_mutationTracker.IsDirty(activeDocument, false, activePath))
                throw new ValidationException("总装配体在预览后存在未保存修改，请保存并重新预览。");
            if (previewed.SourceFileSnapshots.Count == 0)
                throw new ValidationException("预览结果缺少源文件快照，请重新预览。");

            foreach (var pair in previewed.SourceFileSnapshots.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                pair.Value.ValidateUnchanged();
                var open = _app.GetOpenDocumentByName(pair.Key) as ModelDoc2;
                if (open == null) continue;
                if (_mutationTracker.IsDirty(open, false, pair.Key))
                    throw new ValidationException("模型在预览后存在未保存修改，请保存并重新预览：" + pair.Key);
                int expectedStamp;
                if (previewed.DocumentUpdateStamps.TryGetValue(pair.Key, out expectedStamp) &&
                    SafeUpdateStamp(open) != expectedStamp)
                    throw new ValidationException("模型在预览后发生了会话内修改，请重新预览：" + pair.Key);
            }

            Checkpoint(progress, cancellationRequested, "重新读取 Wanxiang 注册表并校验 Asset 版本");
            var registry = _registryProvider.Fetch(settings).Registry;
            using (var fileHashes = new FileFingerprintCache())
            {
                foreach (var inspection in previewed.AssetInspections.Values)
                {
                    var decision = AssetVersionPolicy.Evaluate(inspection.Uuid, inspection.Version,
                        inspection.Fingerprint, registry.Assets);
                    if (!decision.CanExport)
                        throw new ValidationException("Wanxiang 注册表在预览后发生变化，Asset 版本不再有效，请重新预览：" +
                            IdentityService.AssetId(Guid.Parse(inspection.Uuid), inspection.Version));
                    inspection.VersionDecision = decision;
                    try
                    {
                        inspection.State = AssetManifestValidator.Inspect(
                            AssetVersionDirectory(settings, inspection.Uuid, inspection.Version), inspection.Uuid,
                            inspection.Version, inspection.Fingerprint, fileHashes.Sha256);
                        inspection.RequiresLocalPackageRebuild = false;
                        inspection.LocalPackageError = null;
                    }
                    catch (ValidationException ex)
                    {
                        inspection.State = ExistingAssetState.Missing;
                        inspection.RequiresLocalPackageRebuild = true;
                        inspection.LocalPackageError = ex.Message;
                    }
                }
            }
        }

        private void CaptureSourceSnapshots(AnalysisResult result, SwCadNode root,
            FileFingerprintCache fileHashes)
        {
            result.SourceFileSnapshots.Clear();
            result.DocumentUpdateStamps.Clear();
            var activePath = root.SourcePath;
            if (string.IsNullOrWhiteSpace(activePath) || !File.Exists(activePath))
                throw new ValidationException("总装配体尚未保存，请保存后重新预览。");
            result.ActiveDocumentPath = Path.GetFullPath(activePath);
            result.ActiveConfigurationName = root.ReferencedConfiguration ?? string.Empty;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hashedAssetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSnapshotPath(paths, activePath);
            foreach (var node in Flatten(result.Plan.Roots))
                AddSnapshotPath(paths, ((SwCadNode)node.Source).SourcePath);
            foreach (var inspection in result.AssetInspections.Values)
            {
                foreach (var path in inspection.ModelFiles ?? new List<string>())
                {
                    AddSnapshotPath(paths, path);
                    AddSnapshotPath(hashedAssetFiles, path);
                }
                foreach (var path in inspection.Drawings ?? new List<string>())
                {
                    AddSnapshotPath(paths, path);
                    AddSnapshotPath(hashedAssetFiles, path);
                }
            }

            foreach (var path in paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path)) continue;
                var snapshot = ExportSourceFileSnapshot.Capture(path,
                    hashedAssetFiles.Contains(path) ? new Func<string, string>(fileHashes.Sha256) : null);
                result.SourceFileSnapshots.Add(snapshot.Path, snapshot);
                var open = _app.GetOpenDocumentByName(snapshot.Path) as ModelDoc2;
                if (open != null) result.DocumentUpdateStamps[snapshot.Path] = SafeUpdateStamp(open);
            }
        }

        private static void AddSnapshotPath(ISet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { paths.Add(Path.GetFullPath(path)); }
            catch (Exception ex) { throw new ValidationException("无法建立源文件快照：" + path + "；" + ex.Message); }
        }

        private static string ProjectSourceKey(SwCadNode source)
        {
            if (source == null) return string.Empty;
            string path;
            try { path = Path.GetFullPath(source.SourcePath ?? string.Empty); }
            catch { path = source.SourcePath ?? string.Empty; }
            return Canonical.Join(path, source.ReferencedConfiguration,
                source.SourceDocumentKind.ToString());
        }

        private static int SafeUpdateStamp(ModelDoc2 document)
        {
            try { return document == null ? int.MinValue : document.GetUpdateStamp(); }
            catch { return int.MinValue; }
        }

        private static void Checkpoint(Action<string> progress, Func<bool> cancellationRequested,
            string message)
        {
            if (progress != null) progress(message);
            if (cancellationRequested != null && cancellationRequested())
                throw new OperationCanceledException("用户已取消导出。");
        }

        private AssetInspection InspectAsset(SwCadNode source, ExportNode node, FileFingerprintCache fileHashes)
        {
            var modelFiles = _packager.AssetModelFiles(source);
            var drawings = _drawings.FindDirectDrawingFiles(modelFiles);
            var modelEntries = modelFiles.Select(path => Canonical.Join(Path.GetFileName(path),
                new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), fileHashes.Sha256(path)))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            var modelFingerprint = FileHash.Sha256Text(Canonical.Join(IdentityService.ModelSeed(source.Model), string.Join("\n", modelEntries)));
            var drawingEntries = drawings.Select(path => Canonical.Join(Path.GetFileName(path), fileHashes.Sha256(path)))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            var fingerprint = FileHash.Sha256Text(Canonical.Join(modelFingerprint, string.Join("\n", drawingEntries)));
            var version = int.Parse(node.AssetId.Substring(node.AssetId.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
            var properties = new Dictionary<string, string>(PropertyRules.Merge(source.Model),
                StringComparer.OrdinalIgnoreCase);
            return new AssetInspection
            {
                Node = source, Uuid = node.GeometryUuid, Fingerprint = fingerprint, Version = version,
                State = ExistingAssetState.Missing, ModelFiles = modelFiles, Drawings = drawings,
                Properties = properties, IsRobotAsset = PropertyRules.IsRobotClass(properties)
            };
        }

        private void ExportAssets(AnalysisResult analysis, ExporterSettings settings, ExportCompletion completion,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            var reportCreated = new List<string>();
            var reportReused = new List<string>();
            var assets = analysis.AssetInspections.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < assets.Count; index++)
            {
                var pair = assets[index];
                var assetId = pair.Key; var inspection = pair.Value;
                Checkpoint(progress, cancellationRequested, "导出 Asset " +
                    (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                    assets.Count.ToString(CultureInfo.InvariantCulture) + "：" + inspection.Node.Name);
                if (inspection.State == ExistingAssetState.Reusable)
                {
                    var reusedUuid = IdentityService.AssetUuid(inspection.Node.Model).ToString("D");
                    RecordAssetRegistration(completion, reusedUuid, inspection.Version, inspection.Fingerprint);
                    completion.AssetDirectories.Add(AssetVersionDirectory(settings, reusedUuid, inspection.Version));
                    completion.ReusedAssets++; reportReused.Add(assetId); continue;
                }
                var uuid = IdentityService.AssetUuid(inspection.Node.Model).ToString("D");
                var destination = AssetVersionDirectory(settings, uuid, inspection.Version);
                using (var transaction = new DirectoryTransaction(destination))
                {
                    if (inspection.IsRobotAsset)
                    {
                        Checkpoint(progress, cancellationRequested, "Asset " +
                            (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                            assets.Count.ToString(CultureInfo.InvariantCulture) +
                            "：Robot 跳过 STEP/STL 和 SOLIDWORKS 源文件打包");
                    }
                    else
                    {
                        Checkpoint(progress, cancellationRequested, "Asset " +
                            (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                            assets.Count.ToString(CultureInfo.InvariantCulture) + "：导出 STEP/STL");
                        _geometry.ExportBoth(inspection.Node,
                            Path.Combine(transaction.StagingDirectory, "geometry"));
                        var sourceDirectory = Path.Combine(transaction.StagingDirectory, "source", "models");
                        Checkpoint(progress, cancellationRequested, "Asset " +
                            (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                            assets.Count.ToString(CultureInfo.InvariantCulture) + "：打包源模型");
                        _packager.PackAsset(inspection.Node, inspection.ModelFiles, sourceDirectory);
                        Checkpoint(progress, cancellationRequested, "Asset " +
                            (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                            assets.Count.ToString(CultureInfo.InvariantCulture) + "：复制源图纸");
                        _drawings.CopyDrawingSources(inspection.Drawings, sourceDirectory);
                    }
                    var relativeFiles = Directory.EnumerateFiles(transaction.StagingDirectory, "*", SearchOption.AllDirectories)
                        .Select(path => PathPolicy.RelativeTo(transaction.StagingDirectory, path)).ToList();
                    var manifest = new AssetManifest
                    {
                        SchemaVersion = "1.0", Uuid = uuid, Version = inspection.Version,
                        ContentFingerprint = inspection.Fingerprint,
                        Properties = new Dictionary<string, string>(inspection.Properties,
                            StringComparer.OrdinalIgnoreCase),
                        Files = AssetManifestValidator.DescribeFiles(transaction.StagingDirectory, relativeFiles)
                    };
                    var manifestName = "asset_" + uuid + "_v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + ".json";
                    JsonFile.Write(Path.Combine(transaction.StagingDirectory, manifestName), manifest);
                    if (inspection.RequiresLocalPackageRebuild)
                    {
                        Checkpoint(progress, cancellationRequested, "备份本地冲突 Asset 包：" + inspection.Node.Name);
                        var backup = QuarantineInvalidLocalPackage(settings.AssetLibraryRoot, destination,
                            uuid, inspection.Version);
                        if (!string.IsNullOrWhiteSpace(backup)) completion.LocalPackageBackups.Add(backup);
                    }
                    transaction.Commit();
                }
                RecordAssetRegistration(completion, uuid, inspection.Version, inspection.Fingerprint);
                completion.AssetDirectories.Add(destination);
                completion.CreatedAssets++; reportCreated.Add(assetId);
            }
        }

        private void ExportProject(AnalysisResult analysis, ExporterSettings settings, ExportCompletion completion,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            var plan = analysis.Plan;
            Checkpoint(progress, cancellationRequested, "检查 Project 导出包");
            var destination = Path.Combine(settings.ProjectExportRoot, plan.AssemblyUuid,
                "v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture));
            completion.ProjectDirectory = destination;
            if (ProjectReportValidator.Inspect(destination, plan.AssemblyUuid, plan.AssemblyVersion, analysis.ProjectFingerprint) == ExistingProjectState.Reusable)
            {
                completion.ProjectReused = true; return;
            }

            using (var transaction = new DirectoryTransaction(destination))
            {
                var projects = Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Project)
                    .GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                for (var index = 0; index < projects.Count; index++)
                {
                    var project = projects[index];
                    Checkpoint(progress, cancellationRequested, "导出 Project STEP/STL " +
                        (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                        projects.Count.ToString(CultureInfo.InvariantCulture) + "：" + project.Name);
                    _geometry.ExportBoth((SwCadNode)project.Source, Path.Combine(transaction.StagingDirectory, "meshes", project.GeometryUuid));
                }

                Checkpoint(progress, cancellationRequested, "生成 Project XML 和 export-report.json");
                var xmlName = "assembly_" + plan.AssemblyUuid + "_v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture) + ".xml";
                AssemblyXmlWriter.Write(Path.Combine(transaction.StagingDirectory, xmlName), plan);
                var relativeFiles = Directory.EnumerateFiles(transaction.StagingDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => PathPolicy.RelativeTo(transaction.StagingDirectory, path)).ToList();
                var report = new ExportReport
                {
                    SchemaVersion = "1.0", AssemblyUuid = plan.AssemblyUuid, AssemblyVersion = plan.AssemblyVersion,
                    ContentFingerprint = analysis.ProjectFingerprint,
                    Files = AssetManifestValidator.DescribeFiles(transaction.StagingDirectory, relativeFiles)
                };
                foreach (var asset in analysis.AssetInspections)
                {
                    if (asset.Value.State == ExistingAssetState.Reusable) report.ReusedAssets.Add(asset.Key);
                    else report.CreatedAssets.Add(asset.Key);
                }
                foreach (var unit in analysis.ProjectFingerprints.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) report.ProjectUnits.Add(unit);
                JsonFile.Write(Path.Combine(transaction.StagingDirectory, "export-report.json"), report);
                transaction.Commit();
            }
        }

        private static string CalculateProjectFingerprint(AssemblyExportPlan plan, IDictionary<string, string> unitFingerprints)
        {
            var builder = PlanFingerprintMaterial(plan);
            foreach (var unit in unitFingerprints.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                builder.Append(Canonical.Join(unit.Key, unit.Value));
            return FileHash.Sha256Text(builder.ToString());
        }

        private static string CalculatePlanFingerprint(AssemblyExportPlan plan)
        {
            return FileHash.Sha256Text(PlanFingerprintMaterial(plan).ToString());
        }

        private static StringBuilder PlanFingerprintMaterial(AssemblyExportPlan plan)
        {
            var builder = new StringBuilder();
            builder.Append(Canonical.Join(plan.AssemblyUuid, plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture), plan.MeshFormat.ToString()));
            foreach (var node in Flatten(plan.Roots))
                builder.Append(Canonical.Join(node.Id, node.ParentId, node.Name, node.Kind.ToString(), node.AssetId, node.MeshFile,
                    node.GeometryUuid, Number(node.Pose.Tx), Number(node.Pose.Ty), Number(node.Pose.Tz),
                    Number(node.Pose.Rotation.X), Number(node.Pose.Rotation.Y), Number(node.Pose.Rotation.Z), Number(node.Pose.Rotation.W)));
            return builder;
        }

        private static string BuildPreview(IEnumerable<ExportNode> roots,
            IDictionary<string, string> assetVersionMessages, WanxiangRegistrySnapshot registrySnapshot,
            bool savedLocally)
        {
            var builder = new StringBuilder();
            builder.Append("Wanxiang 注册表: ").Append(registrySnapshot.RemotePath)
                .Append(registrySnapshot.RemoteRegistryExists ? " [已读取 " : " [远端不存在，按空库判断；")
                .Append(registrySnapshot.RemoteRegistryExists
                    ? registrySnapshot.Registry.Assets.Count.ToString(CultureInfo.InvariantCulture) + " 条；"
                    : string.Empty)
                .Append(savedLocally ? "已保存本地副本]" : "不保存本地副本]")
                .AppendLine().AppendLine();
            var values = roots.ToList();
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Count; i++)
                AppendPreview(builder, values[i], string.Empty, i == values.Count - 1,
                    assetVersionMessages, seenAssets);
            return builder.ToString().TrimEnd();
        }

        private static void AppendPreview(StringBuilder builder, ExportNode node, string indent, bool last,
            IDictionary<string, string> assetVersionMessages, ISet<string> seenAssets)
        {
            builder.Append(indent).Append(last ? "└─ " : "├─ ").Append(node.Kind.ToString().PadRight(8)).Append(' ').Append(node.Name);
            if (node.Kind == ExportNodeKind.Asset)
            {
                if (seenAssets.Add(node.AssetId))
                {
                    string message;
                    builder.Append("  ").Append(assetVersionMessages.TryGetValue(node.AssetId, out message)
                        ? message : "[未完成版本判断]");
                }
                else builder.Append("  [同一 Asset 的另一实例]");
            }
            if (node.Kind == ExportNodeKind.Project) builder.Append("  [导出 STEP/STL]");
            builder.AppendLine();
            var children = node.Children.ToList();
            for (var i = 0; i < children.Count; i++)
                AppendPreview(builder, children[i], indent + (last ? "   " : "│  "), i == children.Count - 1,
                    assetVersionMessages, seenAssets);
        }

        private static string DescribeVersionDecision(AssetInspection inspection, string packageError)
        {
            if (!string.IsNullOrWhiteSpace(packageError))
                return "[Wanxiang 版本有效；本地旧包冲突，导出时将备份旧包并重建当前版本：" + packageError + "]";
            var decision = inspection.VersionDecision;
            switch (decision.Kind)
            {
                case AssetVersionDecisionKind.NewAsset:
                    return "[新 Asset：当前指纹未注册；导出时创建 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "]";
                case AssetVersionDecisionKind.ReuseCurrentVersion:
                    return inspection.State == ExistingAssetState.Reusable
                        ? "[指纹一致：已注册 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "，可直接复用]"
                        : "[指纹一致：已注册 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "，但本地包缺失，导出时重建]";
                case AssetVersionDecisionKind.CreateNewVersion:
                    return "[内容已变化：版本已从 v" + decision.RegisteredVersion.Value.ToString(CultureInfo.InvariantCulture) +
                        " 升级到 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "，导出时新建]";
                case AssetVersionDecisionKind.ContentAlreadyRegisteredAtDifferentVersion:
                    return "[无需升级：当前内容已注册为 v" + decision.RegisteredVersion.Value.ToString(CultureInfo.InvariantCulture) +
                        "；请将 asset_version 从 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + " 改回 v" +
                        decision.RegisteredVersion.Value.ToString(CultureInfo.InvariantCulture) + "]";
                default:
                    return "[必须升级 asset_version：v" + inspection.Version.ToString(CultureInfo.InvariantCulture) +
                        " 与已注册指纹不同；建议改为 v" + decision.SuggestedVersion.Value.ToString(CultureInfo.InvariantCulture) + "]";
            }
        }

        private static void RecordAssetRegistration(ExportCompletion completion,
            string uuid, int version, string fingerprint)
        {
            var parsedUuid = Guid.Parse(uuid);
            var normalizedUuid = parsedUuid.ToString("D");
            completion.AssetRegistrations.Add(new AssetRegistration
            {
                AssetId = IdentityService.AssetId(parsedUuid, version),
                Uuid = normalizedUuid,
                Version = version,
                RelativeDirectory = normalizedUuid + "/v" + version.ToString(CultureInfo.InvariantCulture),
                ContentFingerprint = fingerprint,
                RegisteredUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
            });
        }

        private static IEnumerable<ExportNode> Flatten(IEnumerable<ExportNode> roots)
        {
            foreach (var root in roots)
            {
                yield return root;
                foreach (var child in Flatten(root.Children)) yield return child;
            }
        }

        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string AssetVersionDirectory(ExporterSettings settings, string uuid, int version)
        {
            return Path.Combine(settings.AssetLibraryRoot, uuid, "v" + version.ToString(CultureInfo.InvariantCulture));
        }

        internal static string QuarantineInvalidLocalPackage(string assetLibraryRoot, string destination,
            string uuid, int version)
        {
            var root = Path.GetFullPath(assetLibraryRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var expected = Path.GetFullPath(Path.Combine(root, uuid,
                "v" + version.ToString(CultureInfo.InvariantCulture)));
            var actual = Path.GetFullPath(destination);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("拒绝备份非预期的本地 Asset 目录：" + actual);
            if (File.Exists(actual))
                throw new ValidationException("本地 Asset 版本路径是文件而不是目录，无法自动备份：" + actual);
            if (!Directory.Exists(actual)) return null;

            var backupRoot = Path.Combine(root, ".local-package-backups");
            Directory.CreateDirectory(backupRoot);
            var backup = Path.Combine(backupRoot, uuid + "-v" +
                version.ToString(CultureInfo.InvariantCulture) + "-" +
                DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.Move(actual, backup);
            return backup;
        }

    }
}
