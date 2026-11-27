using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;
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
            ValidationErrors = new List<string>();
            PlacementDiagnostics = new List<ExportPlacementDiagnostic>();
            ContactDiagnostics = new List<ContactDiagnostic>();
            CanExport = true;
        }
        public AssemblyExportPlan Plan { get; internal set; }
        public string Preview { get; internal set; }
        public string PlanFingerprint { get; internal set; }
        public string ProjectFingerprint { get; internal set; }
        public string ProjectVersionMessage { get; internal set; }
        public string ProjectVersionError { get; internal set; }
        public bool CanExport { get; internal set; }
        public IList<string> UnsupportedAssetRoots { get; private set; }
        public IList<string> ValidationErrors { get; private set; }
        public IList<ExportPlacementDiagnostic> PlacementDiagnostics { get; internal set; }
        public IList<ContactDiagnostic> ContactDiagnostics { get; internal set; }
        internal IDictionary<string, AssetInspection> AssetInspections { get; private set; }
        internal IDictionary<string, string> AssetVersionMessages { get; private set; }
        internal IDictionary<string, string> ProjectFingerprints { get; private set; }
        internal IDictionary<string, ExportSourceFileSnapshot> SourceFileSnapshots { get; private set; }
        internal IDictionary<string, int> DocumentUpdateStamps { get; private set; }
        internal ExistingProjectState ProjectState { get; set; }
        internal string ActiveDocumentPath { get; set; }
        internal string ActiveConfigurationName { get; set; }
        internal long BindingRevision { get; set; }
        internal int? SuggestedProjectVersion { get; set; }
    }

    internal sealed class AssetInspection
    {
        public SwCadNode Node { get; set; }
        public string SourcePath { get; set; }
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
        public IDictionary<string, string> Properties { get; set; }
        public bool CanHoldContents { get; set; }
        public bool ReuseRemotePackage { get; set; }
        public string SemanticMismatchReason { get; set; }
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
        private static readonly string[] RequiredAssetProperties =
            { PropertyRules.PartName, PropertyRules.DesignPurpose };
        private readonly SldWorks _app;
        private readonly SwSourcePackager _packager;
        private readonly SwDrawingExporter _drawings;
        private readonly SwGeometryExporter _geometry;
        private readonly SwPluginMutationTracker _mutationTracker;
        private readonly WanxiangRegistryProvider _registryProvider;
        internal SwPluginMutationTracker MutationTracker { get { return _mutationTracker; } }

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
            var bindings = SwInstanceBindings.ForDocument(_app.ActiveDoc as ModelDoc2);
            if (bindings != null) bindings.RequireSaved();
            var root = SwAssemblyRoot.FromActiveDocument(_app, _mutationTracker);
            var classificationIssues = new List<string>();
            var requiredPropertyIssues = new List<string>();
            var assetProperties = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            CollectClassificationAndRequiredPropertyIssues(root, classificationIssues, requiredPropertyIssues, assetProperties);
            if (classificationIssues.Count != 0)
            {
                var allIssues = classificationIssues.Concat(requiredPropertyIssues)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                throw new ValidationException(FormatAggregatedIssues(
                    "分类预览已检查完整棵可见装配树，发现以下问题", allIssues));
            }
            AssemblyExportPlan plan;
            try
            {
                var scan = new AssemblyScanner().Scan(root);
                plan = new ExportPlanBuilder().Build(scan, settings.ProjectMeshFormat, settings.ExportProject);
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                var issues = requiredPropertyIssues.Concat(new[] { ex.Message })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                throw new ValidationException(FormatAggregatedIssues(
                    "分类预览已完成属性预检查，但导出树无法建立", issues));
            }
            var result = new AnalysisResult
            {
                Plan = plan,
                PlanFingerprint = CalculatePlanFingerprint(plan)
            };
            var assetNodes = Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Asset).ToList();
            if (bindings != null)
            {
                result.BindingRevision = bindings.Revision;
                try
                {
                    plan.Relations = ExportRelations(bindings, plan);
                    if (settings.ExportProject)
                    {
                        var active = bindings.Current;
                        result.PlacementDiagnostics = ExportPlacementDiagnostics.Analyze(assetNodes, plan.Relations,
                            node => assetProperties[AssetPropertyKey((SwCadNode)node.Source)], new ExportPlacementReviewContext
                            {
                                Configuration = active.ConfigurationName,
                                ConfigurationId = active.ConfigurationId,
                                ReferencedConfiguration = node => ((SwCadNode)node.Source).ReferencedConfiguration,
                                OccurrenceIdentity = PlacementReviewOccurrence,
                                Reasons = ReadPlacementReviews(plan.AssemblyUuid)
                            });
                        result.ContactDiagnostics = SwContactDiagnostics.ForExport(_app, _app.ActiveDoc as ModelDoc2, assetNodes, plan.Relations);
                        foreach (var diagnostic in result.PlacementDiagnostics.Where(value => value.IsBlocking))
                        {
                            result.CanExport = false;
                            AddValidationError(result, diagnostic.Message);
                        }
                    }
                }
                catch (ValidationException ex) { AddValidationError(result, ex.Message); }
                result.PlanFingerprint = CalculatePlanFingerprint(plan);
            }
            foreach (var issue in requiredPropertyIssues)
            {
                result.CanExport = false;
                AddValidationError(result, issue);
            }
            var registry = registrySnapshot.Registry;
            var exportNames = ResolveAssetExportNames(assetNodes,
                registry.Assets.Concat(AssetRegistryStore.LocalPackageNames(settings.AssetLibraryRoot)));
            var acceptedAssets = plan.Relations.Relations.Where(relation => relation.Placement != null)
                .ToLookup(relation => relation.Placement.ParentAssetUuid, relation => relation.Placement.ChildAssetUuid, StringComparer.Ordinal);
            var inspectionCache = new Dictionary<string, AssetInspection>(StringComparer.OrdinalIgnoreCase);
            using (var fileHashes = new FileFingerprintCache())
            {
                foreach (var group in assetNodes.GroupBy(node => node.AssetId, StringComparer.OrdinalIgnoreCase))
                {
                    try
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
                            string name;
                            exportNames.TryGetValue(node.GeometryUuid, out name);
                            inspection = InspectAsset(source, node, fileHashes, name, acceptedAssets[node.GeometryUuid]);
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
                    var registered = registry.Assets.FirstOrDefault(value =>
                        string.Equals(value.Uuid, current.Uuid, StringComparison.OrdinalIgnoreCase) &&
                        value.Version == current.Version);
                    if (registered != null &&
                        !string.Equals(current.Fingerprint, registered.ContentFingerprint, StringComparison.OrdinalIgnoreCase) &&
                        RegisteredGeometryMatches(settings, current, registered))
                    {
                        current.Fingerprint = registered.ContentFingerprint;
                        current.ReuseRemotePackage = true;
                    }
                    var decision = AssetVersionPolicy.Evaluate(assetNode.GeometryUuid, current.Version,
                        current.Fingerprint, registry.Assets);
                    current.VersionDecision = decision;
                    if (decision.Kind == AssetVersionDecisionKind.UpgradeRequired)
                    {
                        if (!decision.SuggestedVersion.HasValue) throw new ValidationException("asset_version 已达到上限。");
                        current.RequiresVersionUpgrade = true;
                        current.SuggestedVersion = decision.SuggestedVersion.Value;
                    }
                    string packageError = null;
                    if (decision.CanExport)
                    {
                        try
                        {
                            current.State = AssetManifestValidator.Inspect(
                                AssetVersionDirectory(settings, assetNode.GeometryUuid, current.Version),
                                assetNode.GeometryUuid, current.Version, current.Fingerprint, fileHashes.Sha256,
                                current.CanHoldContents);
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
                    result.AssetVersionMessages[group.Key] = DescribeVersionDecision(current, packageError);
                    }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        result.CanExport = false;
                        var first = group.First();
                        var source = first.Source as SwCadNode;
                        var detail = "Asset [" + first.Name + "]" +
                            (source == null || string.IsNullOrWhiteSpace(source.SourcePath)
                                ? string.Empty : "（" + source.SourcePath + "）") +
                            " 检查失败：" + ex.Message;
                        AddValidationError(result, detail);
                        result.AssetVersionMessages[group.Key] = "[检查失败：" + ex.Message + "]";
                    }
                }
                if (settings.ExportProject)
                {
                    InspectProjectFingerprints(result, null, null, fileHashes);
                    if (!string.IsNullOrWhiteSpace(result.ProjectFingerprint))
                        InspectProjectVersion(result, settings);
                }
                else
                {
                    result.ProjectFingerprint = string.Empty;
                    result.ProjectVersionMessage = "[未选择导出 assembly_package：不检查 assembly_version，不生成装配包 XML/几何]";
                }
                CaptureSourceSnapshots(result, root, fileHashes);
            }
            result.Preview = BuildPreview(plan.Roots, result.AssetVersionMessages, registrySnapshot,
                settings.SaveRegistryLocally, result.ProjectVersionMessage, result.ValidationErrors,
                result.PlacementDiagnostics, settings.ExportProject);
            var contactReview = result.ContactDiagnostics.Where(value => value.NeedsReview).Select(value => value.Message).ToArray();
            if (contactReview.Length != 0)
                result.Preview += "\r\n父子实体接触复核\r\n" + string.Join("\r\n", contactReview) + "\r\n";
            if (bindings != null && plan.Relations != null)
            {
                var relationText = new StringBuilder("\r\n承载关系（当前配置）：\r\n");
                foreach (var r in plan.Relations.Relations)
                    relationText.AppendLine("有效：" + r.ParentNodeId + " → " + r.ChildNodeId);
                foreach (var n in Flatten(plan.Roots).Where(n => n.Kind == ExportNodeKind.Asset))
                {
                    if (!plan.Relations.Relations.Any(r => r.ChildNodeId == n.Id)) relationText.AppendLine("未绑定：" + n.Source.InstancePath);
                    if (!plan.Relations.Relations.Any(r => r.ParentNodeId == n.Id)) relationText.AppendLine("未绑定子对象：" + n.Source.InstancePath);
                }
                result.Preview += relationText.ToString();
            }
            return result;
        }

        private static string AssetPropertyKey(SwCadNode node) { return node.SourcePath ?? node.Name; }

        private static void CollectClassificationAndRequiredPropertyIssues(SwCadNode node,
            IList<string> classificationIssues, IList<string> requiredPropertyIssues,
            IDictionary<string, IDictionary<string, string>> inspectedAssetProperties)
        {
            if (node == null) return;
            ModelDescriptor model = null;
            IDictionary<string, string> properties = null;
            try
            {
                model = node.ClassificationModel;
                ModelRules.ValidateClassifiable(model);
                properties = PropertyRules.MergeForClassification(model);
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                AddIssue(classificationIssues, "模型 [" + (node.Name ?? "<未命名>") + "]（" +
                    (node.SourcePath ?? string.Empty) + "）分类属性读取失败：" + ex.Message);
            }

            var isAsset = properties != null && PropertyRules.ReadIsAsset(properties);
            if (properties != null)
            {
                try
                {
                    if (PropertyRules.ReadIsWorkstation(properties, node.SourceDocumentKind))
                        PropertyRules.RequireWorkstationName(properties, node.Name);
                }
                catch (ValidationException ex) { AddIssue(classificationIssues, ex.Message); }
            }
            if (isAsset)
            {
                var isRobot = PropertyRules.IsRobotClass(properties);
                try
                {
                    if (isRobot)
                        PropertyRules.BuildRobotAssetId(properties, node.Name);
                    else
                    {
                        ModelRules.ValidateExportable(model);
                        PropertyRules.RequireWanxiangAssetClass(properties, node.Name);
                    }
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    AddIssue(classificationIssues, ex.Message);
                }

                var propertyKey = AssetPropertyKey(node);
                if (!inspectedAssetProperties.ContainsKey(propertyKey))
                {
                    inspectedAssetProperties.Add(propertyKey, null);
                    try
                    {
                        var businessProperties = PropertyRules.Merge(node.Model);
                        inspectedAssetProperties[propertyKey] = businessProperties;
                        if (!isRobot)
                        {
                            foreach (var name in PropertyRules.MissingOrBlankProperties(
                                businessProperties, RequiredAssetProperties))
                                AddIssue(requiredPropertyIssues, "Asset [" + node.Name + "]（" +
                                    node.SourcePath + "）的文件级自定义属性 [" + name + "] 缺失或为空白。");
                        }
                        foreach (var issue in PropertyRules.ValidateAssetConnectionProperties(businessProperties))
                            AddIssue(requiredPropertyIssues, "Asset [" + node.Name + "]（" +
                                node.SourcePath + "）" + issue);
                    }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        AddIssue(classificationIssues, "Asset [" + node.Name + "]（" +
                            node.SourcePath + "）自定义属性读取失败：" + ex.Message);
                    }
                }
                return;
            }

            var children = new List<SwCadNode>();
            try
            {
                foreach (var childValue in node.GetChildren())
                {
                    var child = childValue as SwCadNode;
                    if (child == null) continue;
                    try
                    {
                        if (AssemblyScanner.IsIncluded(child)) children.Add(child);
                    }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        AddIssue(classificationIssues, "组件 [" + child.Name + "] 状态读取失败：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                AddIssue(classificationIssues, "模型 [" + node.Name + "] 子组件读取失败：" + ex.Message);
            }

            foreach (var child in children)
                CollectClassificationAndRequiredPropertyIssues(child, classificationIssues,
                    requiredPropertyIssues, inspectedAssetProperties);
        }

        private static string FormatAggregatedIssues(string heading, IList<string> issues)
        {
            var builder = new StringBuilder().Append(heading).Append("（")
                .Append(issues.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 项）：");
            for (var index = 0; index < issues.Count; index++)
                builder.Append(index + 1).Append(". ").AppendLine(issues[index]);
            return builder.ToString().TrimEnd();
        }

        private static void AddIssue(IList<string> issues, string message)
        {
            if (issues == null || string.IsNullOrWhiteSpace(message)) return;
            if (!issues.Any(value => string.Equals(value, message, StringComparison.OrdinalIgnoreCase)))
                issues.Add(message);
        }

        internal static IList<AssetInspection> AssetsRequiringVersionUpgrade(AnalysisResult analysis)
        {
            return analysis.AssetInspections.Values
                .Where(value => value.RequiresVersionUpgrade && !string.IsNullOrWhiteSpace(value.SourcePath) &&
                    !value.IsEmbeddedAssetRoot &&
                    !AssetSourcePathPolicy.IsSessionEmbeddedModelPath(value.SourcePath))
                .GroupBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(value => Path.GetFileName(value.SourcePath), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal bool OpenAssetForVersionEditing(string sourcePath)
        {
            var path = Path.GetFullPath(sourcePath);
            var extension = Path.GetExtension(path);
            var isAssembly = string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase);
            if (AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path) ||
                (!isAssembly && !string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase)))
                throw new ValidationException("待升版资产必须是外部 SLDASM/SLDPRT 文件：" + path);
            if (!File.Exists(path))
                throw new ValidationException("资产源文件不存在：" + path);

            var document = _app.GetOpenDocumentByName(path) as ModelDoc2;
            if (document == null)
            {
                int errors = 0, warnings = 0;
                var documentType = isAssembly
                    ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                document = _app.OpenDoc6(path, documentType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty,
                    ref errors, ref warnings) as ModelDoc2;
                if (document == null || errors != 0)
                    throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                        "无法打开资产：{0}（errors={1}, warnings={2}）", path, errors, warnings));
            }

            int activationErrors = 0;
            var active = _app.ActivateDoc3(document.GetTitle(), false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors) as ModelDoc2;
            if (active == null || !string.Equals(active.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("已打开但无法激活所选资产：" + path);
            return active.IsOpenedReadOnly();
        }

        private AnalysisResult InspectProjectFingerprints(AnalysisResult result,
            Action<string> progress, Func<bool> cancellationRequested, FileFingerprintCache fileHashes)
        {
            result.ProjectFingerprints.Clear();
            var failed = false;
            var groups = Flatten(result.Plan.Roots).Where(node => node.Kind == ExportNodeKind.Meshes)
                .GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                try
                {
                    Checkpoint(progress, cancellationRequested, "计算 assembly_package 几何单元指纹 " +
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
                    if (fingerprints.Count != 1) throw new ValidationException(
                        "同一 assembly_package 几何单元 UUID 的多个源模型具有不同内容: " + group.Key);
                    result.ProjectFingerprints.Add(group.Key, fingerprints[0]);
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    failed = true;
                    result.CanExport = false;
                    AddValidationError(result, "assembly_package 几何单元 [" + group.First().Name + "] 检查失败：" + ex.Message);
                }
            }

            if (failed)
            {
                result.ProjectFingerprint = string.Empty;
                result.ProjectVersionMessage = "[assembly_package 指纹检查存在错误，不能判断 assembly_version]";
                return result;
            }
            result.ProjectFingerprint = CalculateProjectFingerprint(result.Plan, result.ProjectFingerprints);
            return result;
        }

        private static void InspectProjectVersion(AnalysisResult result, ExporterSettings settings)
        {
            var plan = result.Plan;
            var destination = ProjectVersionDirectory(settings, plan);
            try
            {
                result.ProjectState = ProjectReportValidator.InspectVersion(destination, plan.AssemblyUuid,
                    plan.AssemblyVersion, result.ProjectFingerprint);
                if (result.ProjectState == ExistingProjectState.ContentChanged)
                {
                    var latest = plan.AssemblyVersion;
                    foreach (var directory in Directory.EnumerateDirectories(Path.GetDirectoryName(destination), "v*"))
                    {
                        int version;
                        if (int.TryParse(Path.GetFileName(directory).Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out version))
                            latest = Math.Max(latest, version);
                    }
                    if (latest == int.MaxValue) throw new ValidationException("assembly_version 已达到上限。");
                    result.SuggestedProjectVersion = latest + 1;
                    result.CanExport = false;
                    result.ProjectVersionMessage = "[装配内容已变化，插件建议提升 assembly_version 至 v" +
                        result.SuggestedProjectVersion + "]";
                    return;
                }
                result.ProjectVersionMessage = result.ProjectState == ExistingProjectState.Reusable
                    ? "[assembly_package 指纹一致：assembly_version v" +
                        plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture) + " 可直接复用]"
                    : "[新 assembly_package：分类预览已完成指纹检查；导出时创建 assembly_version v" +
                        plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture) + "]";
            }
            catch (ValidationException ex)
            {
                result.CanExport = false;
                result.ProjectVersionError = ex.Message;
                result.ProjectVersionMessage = "[assembly_package 版本校验失败：" + ex.Message + "]";
                AddValidationError(result, "assembly_package 版本校验失败：" + ex.Message);
            }
        }

        public ExportCompletion Export(AnalysisResult previewed, ExporterSettings settings)
        {
            return Export(previewed, settings, null, null);
        }

        public ExportCompletion Export(AnalysisResult previewed, ExporterSettings settings,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            if (previewed == null) throw new ArgumentNullException("previewed");
            if (!previewed.CanExport) throw new ValidationException("预览发现 Asset/assembly_package 版本需要调整，不能继续导出。");
            settings.Validate();
            if (settings.ExportProject != previewed.Plan.ExportProject)
                throw new ValidationException("导出 assembly_package 选项在预览后发生变化，请重新分类预览。");
            RevalidatePreview(previewed, settings, progress, cancellationRequested);

            var activeDocument = _app.ActiveDoc as ModelDoc2;
            var completion = new ExportCompletion();
            using (new SwSelectionScope(activeDocument))
            {
                var current = previewed;
                using (new SwExportPreferenceScope(_app))
                {
                    ExportAssets(current, settings, completion, progress, cancellationRequested);
                    if (settings.ExportProject)
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
            var bindings = SwInstanceBindings.ForDocument(activeDocument);
            if (bindings != null)
            {
                bindings.RequireSaved();
                if (bindings.Revision != previewed.BindingRevision)
                    throw new ValidationException("预览后承载关系已修改，请保存并重新预览。");
                var fresh = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(_app, _mutationTracker)),
                    previewed.Plan.MeshFormat, previewed.Plan.ExportProject);
                fresh.Relations = ExportRelations(bindings, fresh);
                if (CalculatePlanFingerprint(fresh) != previewed.PlanFingerprint)
                    throw new ValidationException("预览后的关系、位姿或导出节点已变化，请保存并重新预览。");
            }
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
            var currentNames = registry.Assets.Concat(AssetRegistryStore.LocalPackageNames(settings.AssetLibraryRoot))
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name)).ToLookup(asset => Canonical.Normalize(asset.Name));
            using (var fileHashes = new FileFingerprintCache())
            {
                foreach (var inspection in previewed.AssetInspections.Values)
                {
                    var name = AssetName(inspection.Properties);
                    if (currentNames[Canonical.Normalize(name)].Any(asset => !string.Equals(asset.Uuid, inspection.Uuid, StringComparison.OrdinalIgnoreCase)))
                        throw new ValidationException("资产名称已被其他 UUID 使用，请重新分类预览：" + name);
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
                            inspection.Version, inspection.Fingerprint, fileHashes.Sha256, inspection.CanHoldContents);
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

            if (settings.ExportProject)
            {
                Checkpoint(progress, cancellationRequested, "重新校验预览后的 assembly_package 版本状态");
                ExistingProjectState projectState;
                try
                {
                    projectState = ProjectReportValidator.Inspect(
                        ProjectVersionDirectory(settings, previewed.Plan), previewed.Plan.AssemblyUuid,
                        previewed.Plan.AssemblyVersion, previewed.ProjectFingerprint);
                }
                catch (ValidationException ex)
                {
                    throw new ValidationException("assembly_package 本地包在分类预览后发生变化，请重新预览：" + ex.Message);
                }
                if (projectState != previewed.ProjectState)
                    throw new ValidationException("assembly_package 本地版本状态在分类预览后发生变化，请重新预览。");
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
            {
                if (node.Kind == ExportNodeKind.Robot) continue;
                if (node.Kind == ExportNodeKind.Meshes && !result.Plan.ExportProject) continue;
                AddSnapshotPath(paths, ((SwCadNode)node.Source).SourcePath);
            }
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
                try
                {
                    var snapshot = ExportSourceFileSnapshot.Capture(path,
                        hashedAssetFiles.Contains(path) ? new Func<string, string>(fileHashes.Sha256) : null);
                    result.SourceFileSnapshots.Add(snapshot.Path, snapshot);
                    var open = _app.GetOpenDocumentByName(snapshot.Path) as ModelDoc2;
                    if (open != null) result.DocumentUpdateStamps[snapshot.Path] = SafeUpdateStamp(open);
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    result.CanExport = false;
                    AddValidationError(result, "源文件快照失败 [" + path + "]：" + ex.Message);
                }
            }
        }

        private static void AddValidationError(AnalysisResult result, string message)
        {
            if (result == null || string.IsNullOrWhiteSpace(message)) return;
            if (!result.ValidationErrors.Contains(message, StringComparer.OrdinalIgnoreCase))
                result.ValidationErrors.Add(message);
        }

        private static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException || exception is StackOverflowException ||
                exception is AccessViolationException || exception is AppDomainUnloadedException;
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

        private static AssemblyRelations ExportRelations(SwInstanceBindings bindings, AssemblyExportPlan plan)
        {
            var relations = bindings.Export(plan);
            relations.IncludeAssetPlacements(Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Asset));
            return relations;
        }

        private static string PlacementReviewOccurrence(ExportNode node)
        {
            var source = (SwCadNode)node.Source;
            try
            {
                var context = source.Component == null ? source.InstanceId : SwInstanceBindings.Context(source.Component);
                return string.IsNullOrEmpty(context) ? null : context + "\n" + source.SourcePath;
            }
            catch (COMException) { return null; }
            catch (ValidationException) { return null; }
        }

        internal static IDictionary<string, string> ReadPlacementReviews(string assemblyUuid, string directory = null)
        {
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            Guid assembly;
            if (!Guid.TryParseExact(assemblyUuid, "D", out assembly)) return empty;
            var folder = directory ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksAssetExporter", "PlacementReviews");
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return empty;
            try
            {
                using (var stream = File.OpenRead(Path.Combine(folder, assembly.ToString("D") + ".json")))
                {
                    if (stream.Length == 0 || stream.Length > 1024 * 1024) return empty;
                    string content;
                    using (var text = new StreamReader(stream, new UTF8Encoding(false, true), true)) content = text.ReadToEnd();
                    // The JSON reader exposes duplicate object properties, but can stop at the first root.
                    // Validate the complete input before using it to inspect the exact property set.
                    new JavaScriptSerializer { RecursionLimit = 6 }.DeserializeObject(content);
                    var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 6, MaxStringContentLength = 4096 };
                    using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(content), quotas))
                    {
                        var root = XDocument.Load(reader).Root;
                        if (root == null || (string)root.Attribute("type") != "object" || root.Elements().Count() != 2)
                            return empty;
                        var schema = root.Elements("schema_version").SingleOrDefault();
                        var reviews = root.Elements("reviews").SingleOrDefault();
                        if (schema == null || (string)schema.Attribute("type") != "string" || schema.Value != "1.0" ||
                            reviews == null || (string)reviews.Attribute("type") != "object") return empty;
                        var result = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var entry in reviews.Elements())
                        {
                            // The JSON XML reader maps property names beginning with a digit to item attributes.
                            var key = (string)entry.Attribute("item") ?? entry.Name.LocalName;
                            var reason = entry.Value.Trim();
                            if ((string)entry.Attribute("type") != "string" || key.Length != 64 ||
                                key.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) ||
                                reason.Length == 0 || reason.Any(char.IsControl) || result.ContainsKey(key)) return empty;
                            result.Add(key, reason);
                        }
                        return result;
                    }
                }
            }
            catch (IOException) { return empty; }
            catch (UnauthorizedAccessException) { return empty; }
            catch (System.Security.SecurityException) { return empty; }
            catch (XmlException) { return empty; }
            catch (InvalidOperationException) { return empty; }
            catch (ArgumentException) { return empty; }
        }

        private static string AssetName(IDictionary<string, string> properties)
        {
            string name;
            return properties.TryGetValue(PropertyRules.PartName, out name) ? name ?? string.Empty : string.Empty;
        }

        internal static IDictionary<string, string> ResolveAssetExportNames(IEnumerable<ExportNode> assets,
            IEnumerable<AssetRegistration> knownAssets)
        {
            // The export tree has classification properties only for unbound assets.
            return AssetRegistryStore.AllocateNames(assets.GroupBy(node => node.GeometryUuid, StringComparer.Ordinal)
                .Select(group => new KeyValuePair<string, string>(group.Key, AssetName(PropertyRules.Merge(group.First().Source.Model)))), knownAssets);
        }

        internal static Dictionary<string, string> PublishedAssetProperties(IDictionary<string, string> source, string uuid,
            string name, IEnumerable<string> boundChildren)
        {
            var properties = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
            if (name != null) properties[PropertyRules.PartName] = name;
            properties.Remove(PropertyRules.LegacyCanContain);
            if (properties.ContainsKey(PropertyRules.CanContain))
            {
                var canContain = PropertyRules.ReadCanContain(properties);
                properties.Remove(PropertyRules.CanContain);
                properties.Add(PropertyRules.CanContain, canContain ? "1" : "0");
            }
            var slotType = PropertyRules.ReadAssetSlotType(properties);
            properties.Remove(PropertyRules.SlotType);
            if (slotType.Length != 0) properties.Add(PropertyRules.SlotType, slotType);
            string engagement;
            if (properties.TryGetValue(PropertyRules.AssetEngagement, out engagement))
            {
                properties.Remove(PropertyRules.AssetEngagement);
                if (!string.IsNullOrWhiteSpace(engagement)) properties.Add(PropertyRules.AssetEngagement, engagement.Trim());
            }
            properties[PropertyRules.ConnectionInterface] = uuid;
            string legacy;
            if (properties.TryGetValue(PropertyRules.LegacyAcceptsInterface, out legacy) &&
                (legacy ?? "").Split(';', '；').Any(PropertyRules.IsContentsInterface))
            {
                var mechanical = PropertyRules.MechanicalInterfaceNames(legacy);
                if (mechanical.Count == 0) properties.Remove(PropertyRules.LegacyAcceptsInterface);
                else properties[PropertyRules.LegacyAcceptsInterface] = string.Join(";", mechanical);
            }
            string declared;
            properties.TryGetValue(PropertyRules.AcceptsInterfaces, out declared);
            var accepted = new HashSet<string>(boundChildren, StringComparer.Ordinal);
            foreach (var value in PropertyRules.MechanicalInterfaceNames(declared))
            {
                Guid child;
                if (Guid.TryParseExact(value, "D", out child)) accepted.Add(child.ToString("D"));
            }
            properties[PropertyRules.AcceptsInterfaces] = string.Join(";", accepted.OrderBy(child => child, StringComparer.Ordinal));
            return properties;
        }

        internal static string AssetContentFingerprint(string modelFingerprint, IEnumerable<string> drawingEntries,
            IDictionary<string, string> properties, bool canHoldContents)
        {
            var material = Canonical.Join("asset-placement-uuid-v2", modelFingerprint, string.Join("\n", drawingEntries),
                FileHash.Sha256Text(AssetName(properties)), properties[PropertyRules.ConnectionInterface],
                properties[PropertyRules.AcceptsInterfaces], canHoldContents ? "1" : "0");
            var slotType = PropertyRules.ReadAssetSlotType(properties);
            string assetEngagement;
            properties.TryGetValue(PropertyRules.AssetEngagement, out assetEngagement);
            if (!string.IsNullOrWhiteSpace(slotType) || !string.IsNullOrWhiteSpace(assetEngagement))
                material += Canonical.Join(PropertyRules.SlotType, slotType, PropertyRules.AssetEngagement, assetEngagement);
            return FileHash.Sha256Text(material);
        }

        private AssetInspection InspectAsset(SwCadNode source, ExportNode node, FileFingerprintCache fileHashes,
            string name, IEnumerable<string> boundChildren)
        {
            var modelFiles = _packager.AssetModelFiles(source);
            var drawings = _drawings.FindDirectDrawingFiles(modelFiles);
            var modelEntries = modelFiles.Select(path => Canonical.Join(Path.GetFileName(path),
                new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), fileHashes.Sha256(path)))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            var modelFingerprint = FileHash.Sha256Text(Canonical.Join(IdentityService.ModelSeed(source.Model), string.Join("\n", modelEntries)));
            var drawingEntries = drawings.Select(path => Canonical.Join(Path.GetFileName(path), fileHashes.Sha256(path)))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            var version = int.Parse(node.AssetId.Substring(node.AssetId.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
            var properties = PublishedAssetProperties(PropertyRules.Merge(source.Model), node.GeometryUuid, name, boundChildren);
            var canHoldContents = PropertyRules.ReadCanContain(properties);
            var fingerprint = AssetContentFingerprint(modelFingerprint, drawingEntries, properties, canHoldContents);
            return new AssetInspection
            {
                Node = source, SourcePath = Path.GetFullPath(source.SourcePath),
                Uuid = node.GeometryUuid, Fingerprint = fingerprint, Version = version,
                State = ExistingAssetState.Missing, ModelFiles = modelFiles, Drawings = drawings,
                Properties = properties, CanHoldContents = canHoldContents
            };
        }

        private bool RegisteredGeometryMatches(ExporterSettings settings, AssetInspection inspection,
            AssetRegistration registered)
        {
            var temporary = Path.Combine(Path.GetTempPath(), "SolidWorksAssetExporter-geometry-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey))
                {
                    var manifest = DownloadRegisteredManifest(client, registered, temporary);
                    var capabilityMismatch = AssetManifestValidator.DescribeContentsCapabilityMismatch(
                        manifest, inspection.CanHoldContents);
                    if (capabilityMismatch != null)
                    {
                        inspection.SemanticMismatchReason = capabilityMismatch;
                        return false;
                    }
                    var differingProperties = RegisteredPropertyDifferences(manifest.Properties, inspection.Properties);
                    var stl = manifest.Files.FirstOrDefault(file =>
                        string.Equals(file.Path, "geometry/model.stl", StringComparison.OrdinalIgnoreCase));
                    if (stl == null)
                        throw new ValidationException("已注册 Asset 包缺少 geometry/model.stl，无法确认是否需要升版：" + inspection.Node.Name);
                    var remoteStl = Path.Combine(temporary, "registered.stl");
                    if (!client.DownloadFileIfExists(RegisteredRemotePath(registered, stl.Path), remoteStl) ||
                        new FileInfo(remoteStl).Length != stl.Size ||
                        !string.Equals(FileHash.Sha256(remoteStl), stl.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new ValidationException("已注册 Asset 几何文件无法通过清单校验：" + inspection.Node.Name);
                    var currentStl = Path.Combine(temporary, "current.stl");
                    using (new SwExportPreferenceScope(_app))
                        _geometry.ExportStl(inspection.Node, currentStl);
                    var geometryMatches = string.Equals(StlGeometryFingerprint.Compute(remoteStl),
                        StlGeometryFingerprint.Compute(currentStl), StringComparison.OrdinalIgnoreCase);
                    if (differingProperties.Count != 0 || !geometryMatches)
                        inspection.SemanticMismatchReason =
                            (differingProperties.Count == 0 ? string.Empty :
                                "文件级自定义属性不同：" + string.Join("、", differingProperties)) +
                            (differingProperties.Count != 0 && !geometryMatches ? "；" : string.Empty) +
                            (geometryMatches ? string.Empty : "STL 三角面不同");
                    return differingProperties.Count == 0 && geometryMatches;
                }
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }

        private static AssetManifest DownloadRegisteredManifest(WanxiangDataClient client,
            AssetRegistration registered, string destination)
        {
            var name = "asset_" + registered.Uuid + "_v" +
                registered.Version.ToString(CultureInfo.InvariantCulture) + ".json";
            var path = Path.Combine(destination, name);
            if (!client.DownloadFileIfExists(RegisteredRemotePath(registered, name), path))
                throw new ValidationException("Wanxiang 已注册 Asset 清单不存在：" + registered.AssetId);
            var manifest = AssetManifestValidator.ReadManifest(path);
            if (manifest == null || manifest.Files == null ||
                !string.Equals(manifest.Uuid, registered.Uuid, StringComparison.OrdinalIgnoreCase) ||
                manifest.Version != registered.Version ||
                !string.Equals(manifest.ContentFingerprint, registered.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Wanxiang 已注册 Asset 清单与注册表不一致：" + registered.AssetId);
            return manifest;
        }

        internal static IList<string> RegisteredPropertyDifferences(IDictionary<string, string> registered,
            IDictionary<string, string> current)
        {
            var differences = new List<string>();
            if (registered == null || current == null)
            {
                differences.Add("<属性清单缺失>");
                return differences;
            }
            foreach (var key in registered.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (key.Equals(PropertyRules.AssetVersion, StringComparison.OrdinalIgnoreCase) ||
                    key.Equals(PropertyRules.AssemblyVersion, StringComparison.OrdinalIgnoreCase) ||
                    key.Equals(PropertyRules.LegacyCanContain, StringComparison.OrdinalIgnoreCase))
                    continue;
                string oldValue;
                string value;
                var oldPresent = registered.TryGetValue(key, out oldValue);
                var newPresent = current.TryGetValue(key, out value);
                if (!string.Equals(PropertyRules.CanonicalPublishedValue(key, oldPresent ? oldValue : null),
                    PropertyRules.CanonicalPublishedValue(key, newPresent ? value : null), StringComparison.Ordinal))
                    differences.Add(key + "（" + (oldPresent ? AbbreviateValue(oldValue) : "<未设置>") +
                        " → " + (newPresent ? AbbreviateValue(value) : "<未设置>") + "）");
            }
            return differences;
        }

        private static string AbbreviateValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? "<空>" :
                normalized.Length <= 60 ? normalized : normalized.Substring(0, 60) + "…";
        }

        private static string RegisteredRemotePath(AssetRegistration registered, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
                relativePath.Replace('\\', '/').Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
                throw new ValidationException("已注册 Asset 清单包含无效路径：" + relativePath);
            return "assets/" + registered.Uuid + "/v" +
                registered.Version.ToString(CultureInfo.InvariantCulture) + "/" +
                relativePath.Replace('\\', '/');
        }

        private static void RestoreRegisteredAssetPackage(ExporterSettings settings,
            AssetInspection inspection, ExportCompletion completion)
        {
            var destination = AssetVersionDirectory(settings, inspection.Uuid, inspection.Version);
            var registered = new AssetRegistration
            {
                Uuid = inspection.Uuid, Version = inspection.Version,
                AssetId = IdentityService.AssetId(Guid.Parse(inspection.Uuid), inspection.Version),
                ContentFingerprint = inspection.Fingerprint
            };
            using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey))
            using (var transaction = new DirectoryTransaction(destination))
            {
                var manifest = DownloadRegisteredManifest(client, registered, transaction.StagingDirectory);
                foreach (var file in manifest.Files)
                {
                    var path = PathPolicy.CombineUnderRoot(transaction.StagingDirectory, file.Path);
                    if (!client.DownloadFileIfExists(RegisteredRemotePath(registered, file.Path), path))
                        throw new ValidationException("Wanxiang 已注册 Asset 包文件不存在：" + file.Path);
                }
                if (AssetManifestValidator.Inspect(transaction.StagingDirectory, inspection.Uuid,
                    inspection.Version, inspection.Fingerprint, FileHash.Sha256,
                    inspection.CanHoldContents) != ExistingAssetState.Reusable)
                    throw new ValidationException("Wanxiang 已注册 Asset 包校验未通过：" + registered.AssetId);
                if (inspection.RequiresLocalPackageRebuild)
                {
                    var backup = QuarantineInvalidLocalPackage(settings.AssetLibraryRoot, destination,
                        inspection.Uuid, inspection.Version);
                    if (!string.IsNullOrWhiteSpace(backup)) completion.LocalPackageBackups.Add(backup);
                }
                transaction.Commit();
            }
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
                    RecordAssetRegistration(completion, reusedUuid, inspection);
                    completion.AssetDirectories.Add(AssetVersionDirectory(settings, reusedUuid, inspection.Version));
                    completion.ReusedAssets++; reportReused.Add(assetId); continue;
                }
                var uuid = IdentityService.AssetUuid(inspection.Node.Model).ToString("D");
                var destination = AssetVersionDirectory(settings, uuid, inspection.Version);
                if (inspection.ReuseRemotePackage)
                {
                    RestoreRegisteredAssetPackage(settings, inspection, completion);
                    RecordAssetRegistration(completion, uuid, inspection);
                    completion.AssetDirectories.Add(destination);
                    completion.ReusedAssets++; reportReused.Add(assetId); continue;
                }
                using (var transaction = new DirectoryTransaction(destination))
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
                    var relativeFiles = Directory.EnumerateFiles(transaction.StagingDirectory, "*", SearchOption.AllDirectories)
                        .Select(path => PathPolicy.RelativeTo(transaction.StagingDirectory, path)).ToList();
                    var manifest = new AssetManifest
                    {
                        SchemaVersion = "1.0", Uuid = uuid, Version = inspection.Version,
                        ContentFingerprint = inspection.Fingerprint,
                        CanHoldContents = inspection.CanHoldContents,
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
                RecordAssetRegistration(completion, uuid, inspection);
                completion.AssetDirectories.Add(destination);
                completion.CreatedAssets++; reportCreated.Add(assetId);
            }
        }

        private void ExportProject(AnalysisResult analysis, ExporterSettings settings, ExportCompletion completion,
            Action<string> progress, Func<bool> cancellationRequested)
        {
            var plan = analysis.Plan;
            Checkpoint(progress, cancellationRequested, "检查 assembly_package 导出包");
            var destination = Path.Combine(settings.ProjectExportRoot, plan.AssemblyUuid,
                "v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture));
            completion.ProjectDirectory = destination;
            if (ProjectReportValidator.Inspect(destination, plan.AssemblyUuid, plan.AssemblyVersion, analysis.ProjectFingerprint) == ExistingProjectState.Reusable)
            {
                completion.ProjectReused = true; return;
            }

            using (var transaction = new DirectoryTransaction(destination))
            {
                var projects = Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Meshes)
                    .GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                for (var index = 0; index < projects.Count; index++)
                {
                    var project = projects[index];
                    Checkpoint(progress, cancellationRequested, "导出 assembly_package 几何 STEP/STL " +
                        (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                        projects.Count.ToString(CultureInfo.InvariantCulture) + "：" + project.Name);
                    var source = (SwCadNode)project.Source;
                    try
                    {
                        _geometry.ExportBoth(source, Path.Combine(transaction.StagingDirectory, "meshes", project.GeometryUuid));
                    }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex) || ex is OperationCanceledException) throw;
                        throw new ValidationException("assembly_package 网格导出失败：" + project.Name +
                            "；源文件：" + source.SourcePath +
                            "；引用配置：" + source.ReferencedConfiguration +
                            "；网格 UUID：" + project.GeometryUuid + "；" + ex.Message);
                    }
                }

                Checkpoint(progress, cancellationRequested, "生成 assembly_package XML 和 export-report.json");
                var xmlName = "assembly_" + plan.AssemblyUuid + "_v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture) + ".xml";
                AssemblyXmlWriter.Write(Path.Combine(transaction.StagingDirectory, xmlName), plan);
                plan.Relations.AssemblyUuid = plan.AssemblyUuid;
                plan.Relations.AssemblyVersion = plan.AssemblyVersion;
                plan.Relations.Validate(System.Xml.Linq.XDocument.Load(Path.Combine(transaction.StagingDirectory, xmlName)), plan.AssemblyUuid, plan.AssemblyVersion);
                JsonFile.Write(Path.Combine(transaction.StagingDirectory, AssemblyRelations.FileName), plan.Relations);
                var relativeFiles = Directory.EnumerateFiles(transaction.StagingDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => PathPolicy.RelativeTo(transaction.StagingDirectory, path)).ToList();
                var report = new ExportReport
                {
                    SchemaVersion = "1.0", AssemblyUuid = plan.AssemblyUuid, AssemblyVersion = plan.AssemblyVersion,
                    ContentFingerprint = analysis.ProjectFingerprint, RelationsSchemaVersion = plan.Relations.SchemaVersion,
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
            builder.Append(Canonical.Join(plan.AssemblyUuid,
                plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture), plan.MeshFormat.ToString(),
                plan.ExportProject ? "export-project" : "skip-project"));
            foreach (var node in Flatten(plan.Roots))
            {
                // Keep the retired robot identity field's empty slot so packages without
                // Robot nodes retain their existing fingerprint material.
                builder.Append(Canonical.Join(node.Id, node.ParentId, node.Name, node.Kind.ToString(),
                    node.AssetId, string.Empty, node.MeshFile,
                    node.GeometryUuid, Number(node.Pose.Tx), Number(node.Pose.Ty), Number(node.Pose.Tz),
                    Number(node.Pose.Rotation.X), Number(node.Pose.Rotation.Y), Number(node.Pose.Rotation.Z), Number(node.Pose.Rotation.W)));
                if (AssemblyXmlWriter.IsWorkstation(node))
                    builder.Append(Canonical.Join("workstation", AssemblyXmlWriter.WorkstationName(node)));
            }
            if (plan.ExportProject) builder.Append(plan.Relations.FingerprintMaterial());
            return builder;
        }

        private static string BuildPreview(IEnumerable<ExportNode> roots,
            IDictionary<string, string> assetVersionMessages, WanxiangRegistrySnapshot registrySnapshot,
            bool savedLocally, string projectVersionMessage, IEnumerable<string> validationErrors,
            IEnumerable<ExportPlacementDiagnostic> placementDiagnostics, bool exportProject)
        {
            var builder = new StringBuilder();
            builder.Append("Wanxiang 注册表: ").Append(registrySnapshot.RemotePath)
                .Append(registrySnapshot.RemoteRegistryExists ? " [已读取 " : " [远端不存在，按空库判断；")
                .Append(registrySnapshot.RemoteRegistryExists
                    ? registrySnapshot.Registry.Assets.Count.ToString(CultureInfo.InvariantCulture) + " 条；"
                    : string.Empty)
                .Append(savedLocally ? "已保存本地副本]" : "不保存本地副本]")
                .AppendLine()
                .Append("assembly_package 版本: ").AppendLine(string.IsNullOrWhiteSpace(projectVersionMessage)
                    ? "[未完成版本判断]" : projectVersionMessage)
                .AppendLine();
            var errors = (validationErrors ?? Enumerable.Empty<string>()).ToList();
            if (errors.Count != 0)
            {
                builder.Append("分类预览检查问题（")
                    .Append(errors.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 项）：");
                for (var index = 0; index < errors.Count; index++)
                    builder.Append(index + 1).Append(". ").AppendLine(errors[index]);
                builder.AppendLine();
            }
            var warnings = placementDiagnostics.Where(item => !item.IsBlocking && item.ReviewReason == null).ToList();
            if (warnings.Count != 0)
            {
                builder.Append("装配位置和槽位核对（")
                    .Append(warnings.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 项提醒）：");
                for (var index = 0; index < warnings.Count; index++)
                    builder.Append(index + 1).Append(". ").AppendLine(warnings[index].Message);
                builder.AppendLine();
            }
            var reviews = placementDiagnostics.Where(item => item.ReviewReason != null).ToList();
            if (reviews.Count != 0)
            {
                builder.Append("已复核的槽位容量（本机记录，")
                    .Append(reviews.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 项）：");
                foreach (var review in reviews) builder.AppendLine(review.Message);
                builder.AppendLine();
            }
            var values = roots.ToList();
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Count; i++)
                AppendPreview(builder, values[i], string.Empty, i == values.Count - 1,
                    assetVersionMessages, seenAssets, exportProject);
            return builder.ToString().TrimEnd();
        }

        private static string ProjectVersionDirectory(ExporterSettings settings, AssemblyExportPlan plan)
        {
            return Path.Combine(settings.ProjectExportRoot, plan.AssemblyUuid,
                "v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendPreview(StringBuilder builder, ExportNode node, string indent, bool last,
            IDictionary<string, string> assetVersionMessages, ISet<string> seenAssets, bool exportProject)
        {
            var kindLabel = node.Kind == ExportNodeKind.Meshes ? "Meshes" : node.Kind.ToString();
            builder.Append(indent).Append(last ? "└─ " : "├─ ").Append(kindLabel.PadRight(15)).Append(' ').Append(node.Name);
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
            if (node.Kind == ExportNodeKind.Meshes)
                builder.Append(exportProject ? "  [导出装配包 STEP/STL]" : "  [未选择导出 assembly_package]");
            if (node.Kind == ExportNodeKind.Robot)
                builder.Append(exportProject
                    ? "  [跳过 Asset/几何；assembly_package XML: asset_id=" + node.AssetId + "]"
                    : "  [跳过 Asset；未选择导出 assembly_package]");
            builder.AppendLine();
            var children = node.Children.ToList();
            for (var i = 0; i < children.Count; i++)
                AppendPreview(builder, children[i], indent + (last ? "   " : "│  "), i == children.Count - 1,
                    assetVersionMessages, seenAssets, exportProject);
        }

        private static string DescribeVersionDecision(AssetInspection inspection, string packageError)
        {
            if (!string.IsNullOrWhiteSpace(packageError))
                return inspection.ReuseRemotePackage
                    ? "[实际几何未变：已注册 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) +
                      "；本地旧包冲突，导出时备份并恢复 Wanxiang 原包：" + packageError + "]"
                    : "[Wanxiang 版本有效；本地旧包冲突，导出时将备份旧包并重建当前版本：" + packageError + "]";
            var decision = inspection.VersionDecision;
            switch (decision.Kind)
            {
                case AssetVersionDecisionKind.NewAsset:
                    return "[新 Asset：当前指纹未注册；导出时创建 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "]";
                case AssetVersionDecisionKind.ReuseCurrentVersion:
                    if (inspection.ReuseRemotePackage)
                        return inspection.State == ExistingAssetState.Reusable
                            ? "[实际几何未变：文件哈希变化但已注册 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "，复用原包，无需升版]"
                            : "[实际几何未变：文件哈希变化但已注册 v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + "，导出时恢复 Wanxiang 原包，无需升版]";
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
                        " 与已注册内容不同" +
                        (string.IsNullOrWhiteSpace(inspection.SemanticMismatchReason) ? string.Empty :
                            "（" + inspection.SemanticMismatchReason + "）") +
                        "；建议改为 v" + decision.SuggestedVersion.Value.ToString(CultureInfo.InvariantCulture) + "]";
            }
        }

        private static void RecordAssetRegistration(ExportCompletion completion,
            string uuid, AssetInspection inspection)
        {
            var parsedUuid = Guid.Parse(uuid);
            var normalizedUuid = parsedUuid.ToString("D");
            var version = inspection.Version;
            completion.AssetRegistrations.Add(new AssetRegistration
            {
                AssetId = IdentityService.AssetId(parsedUuid, version),
                Uuid = normalizedUuid,
                Version = version,
                Name = AssetName(inspection.Properties),
                RelativeDirectory = normalizedUuid + "/v" + version.ToString(CultureInfo.InvariantCulture),
                ContentFingerprint = inspection.Fingerprint,
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
