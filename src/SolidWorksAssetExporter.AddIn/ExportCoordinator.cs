using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class AnalysisResult
    {
        internal AnalysisResult()
        {
            AssetInspections = new Dictionary<string, AssetInspection>(StringComparer.OrdinalIgnoreCase);
            ProjectFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        public AssemblyExportPlan Plan { get; internal set; }
        public string Preview { get; internal set; }
        public string ProjectFingerprint { get; internal set; }
        internal IDictionary<string, AssetInspection> AssetInspections { get; private set; }
        internal IDictionary<string, string> ProjectFingerprints { get; private set; }
    }

    internal sealed class AssetInspection
    {
        public SwCadNode Node { get; set; }
        public string Fingerprint { get; set; }
        public int Version { get; set; }
        public ExistingAssetState State { get; set; }
        public IList<string> Drawings { get; set; }
    }

    public sealed class ExportCompletion
    {
        public string ProjectDirectory { get; set; }
        public bool ProjectReused { get; set; }
        public int CreatedAssets { get; set; }
        public int ReusedAssets { get; set; }
    }

    public sealed class ExportCoordinator
    {
        private readonly SldWorks _app;
        private readonly SwSourcePackager _packager;
        private readonly SwDrawingExporter _drawings;
        private readonly SwGeometryExporter _geometry;

        public ExportCoordinator(SldWorks app)
        {
            _app = app; _packager = new SwSourcePackager(app); _drawings = new SwDrawingExporter(app); _geometry = new SwGeometryExporter(app);
        }

        public AnalysisResult Analyze(ExporterSettings settings)
        {
            settings.Validate();
            var root = SwAssemblyRoot.FromActiveDocument(_app);
            var scan = new AssemblyScanner().Scan(root);
            var plan = new ExportPlanBuilder().Build(scan, settings.ProjectMeshFormat);
            var result = new AnalysisResult { Plan = plan };

            foreach (var group in Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Asset).GroupBy(node => node.AssetId, StringComparer.OrdinalIgnoreCase))
            {
                var inspections = group.Select(node => InspectAsset((SwCadNode)node.Source, node, settings)).ToList();
                if (inspections.Select(value => value.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                    throw new ValidationException("同一 asset_id 的多个实例具有不同内容: " + group.Key);
                result.AssetInspections.Add(group.Key, inspections[0]);
            }

            foreach (var group in Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Project).GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase))
            {
                var fingerprints = group.Select(node => _packager.ContentFingerprint((SwCadNode)node.Source))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (fingerprints.Count != 1) throw new ValidationException("同一 Project 单元 UUID 的多个实例具有不同内容: " + group.Key);
                result.ProjectFingerprints.Add(group.Key, fingerprints[0]);
            }

            result.ProjectFingerprint = CalculateProjectFingerprint(plan, result.ProjectFingerprints);
            result.Preview = BuildPreview(plan.Roots, result.AssetInspections);
            return result;
        }

        public ExportCompletion Export(AnalysisResult previewed, ExporterSettings settings)
        {
            if (previewed == null) throw new ArgumentNullException("previewed");
            var current = Analyze(settings);
            if (!string.Equals(previewed.ProjectFingerprint, current.ProjectFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("预览后装配结构或内容发生变化，请重新预览。");

            var activeDocument = _app.ActiveDoc as ModelDoc2;
            var completion = new ExportCompletion();
            using (new SwSelectionScope(activeDocument))
            using (new SwExportPreferenceScope(_app))
            {
                ExportAssets(current, settings, completion);
                ExportProject(current, settings, completion);
            }
            return completion;
        }

        private AssetInspection InspectAsset(SwCadNode source, ExportNode node, ExporterSettings settings)
        {
            var drawings = _drawings.FindDirectDrawingFiles(source, settings.DrawingSearchDirectories);
            var modelFingerprint = _packager.ContentFingerprint(source);
            var drawingEntries = drawings.Select(path => Canonical.Join(Path.GetFileName(path), FileHash.Sha256(path)));
            var fingerprint = FileHash.Sha256Text(Canonical.Join(modelFingerprint, string.Join("\n", drawingEntries)));
            var version = int.Parse(node.AssetId.Substring(node.AssetId.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
            var directory = AssetVersionDirectory(settings, node.GeometryUuid, version);
            var state = AssetManifestValidator.Inspect(directory, node.GeometryUuid, version, fingerprint);
            return new AssetInspection { Node = source, Fingerprint = fingerprint, Version = version, State = state, Drawings = drawings };
        }

        private void ExportAssets(AnalysisResult analysis, ExporterSettings settings, ExportCompletion completion)
        {
            var reportCreated = new List<string>();
            var reportReused = new List<string>();
            foreach (var pair in analysis.AssetInspections.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                var assetId = pair.Key; var inspection = pair.Value;
                if (inspection.State == ExistingAssetState.Reusable)
                {
                    completion.ReusedAssets++; reportReused.Add(assetId); continue;
                }
                var uuid = IdentityService.AssetUuid(inspection.Node.Model).ToString("D");
                var destination = AssetVersionDirectory(settings, uuid, inspection.Version);
                using (var transaction = new DirectoryTransaction(destination))
                {
                    _geometry.ExportBoth(inspection.Node, Path.Combine(transaction.StagingDirectory, "geometry"));
                    _packager.Pack(inspection.Node.Document, Path.Combine(transaction.StagingDirectory, "source", "models"));
                    _drawings.ExportDirectDrawings(inspection.Node, settings.DrawingSearchDirectories,
                        Path.Combine(transaction.StagingDirectory, "drawings", "source"),
                        Path.Combine(transaction.StagingDirectory, "drawings", "pdf"));
                    var relativeFiles = Directory.EnumerateFiles(transaction.StagingDirectory, "*", SearchOption.AllDirectories)
                        .Select(path => PathPolicy.RelativeTo(transaction.StagingDirectory, path)).ToList();
                    var manifest = new AssetManifest
                    {
                        SchemaVersion = "1.0", Uuid = uuid, Version = inspection.Version,
                        ContentFingerprint = inspection.Fingerprint,
                        Properties = new Dictionary<string, string>(inspection.Node.Model.FileProperties, StringComparer.OrdinalIgnoreCase),
                        Files = AssetManifestValidator.DescribeFiles(transaction.StagingDirectory, relativeFiles)
                    };
                    foreach (var property in inspection.Node.Model.ConfigurationProperties) manifest.Properties.Add(property.Key, property.Value);
                    var manifestName = "asset_" + uuid + "_v" + inspection.Version.ToString(CultureInfo.InvariantCulture) + ".json";
                    JsonFile.Write(Path.Combine(transaction.StagingDirectory, manifestName), manifest);
                    transaction.Commit();
                }
                completion.CreatedAssets++; reportCreated.Add(assetId);
            }
        }

        private void ExportProject(AnalysisResult analysis, ExporterSettings settings, ExportCompletion completion)
        {
            var plan = analysis.Plan;
            var destination = Path.Combine(settings.ProjectExportRoot, plan.AssemblyUuid,
                "v" + plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture));
            completion.ProjectDirectory = destination;
            if (ProjectReportValidator.Inspect(destination, plan.AssemblyUuid, plan.AssemblyVersion, analysis.ProjectFingerprint) == ExistingProjectState.Reusable)
            {
                completion.ProjectReused = true; return;
            }

            using (var transaction = new DirectoryTransaction(destination))
            {
                foreach (var project in Flatten(plan.Roots).Where(node => node.Kind == ExportNodeKind.Project)
                    .GroupBy(node => node.GeometryUuid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
                    _geometry.ExportBoth((SwCadNode)project.Source, Path.Combine(transaction.StagingDirectory, "meshes", project.GeometryUuid));

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
            var builder = new StringBuilder();
            builder.Append(Canonical.Join(plan.AssemblyUuid, plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture), plan.MeshFormat.ToString()));
            foreach (var node in Flatten(plan.Roots))
            {
                builder.Append(Canonical.Join(node.Id, node.ParentId, node.Name, node.Kind.ToString(), node.AssetId, node.MeshFile,
                    node.GeometryUuid, Number(node.Pose.Tx), Number(node.Pose.Ty), Number(node.Pose.Tz),
                    Number(node.Pose.Rotation.X), Number(node.Pose.Rotation.Y), Number(node.Pose.Rotation.Z), Number(node.Pose.Rotation.W)));
            }
            foreach (var unit in unitFingerprints.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                builder.Append(Canonical.Join(unit.Key, unit.Value));
            return FileHash.Sha256Text(builder.ToString());
        }

        private static string BuildPreview(IEnumerable<ExportNode> roots, IDictionary<string, AssetInspection> assets)
        {
            var builder = new StringBuilder(); var values = roots.ToList();
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Count; i++)
                AppendPreview(builder, values[i], string.Empty, i == values.Count - 1, assets, seenAssets);
            return builder.ToString().TrimEnd();
        }

        private static void AppendPreview(StringBuilder builder, ExportNode node, string indent, bool last,
            IDictionary<string, AssetInspection> assets, ISet<string> seenAssets)
        {
            builder.Append(indent).Append(last ? "└─ " : "├─ ").Append(node.Kind.ToString().PadRight(8)).Append(' ').Append(node.Name);
            if (node.Kind == ExportNodeKind.Asset)
                builder.Append(seenAssets.Add(node.AssetId)
                    ? (assets[node.AssetId].State == ExistingAssetState.Reusable ? "  [库中已存在]" : "  [需要新建]")
                    : "  [同一 Asset 的另一实例]");
            if (node.Kind == ExportNodeKind.Project) builder.Append("  [导出 STEP/STL]");
            builder.AppendLine();
            var children = node.Children.ToList();
            for (var i = 0; i < children.Count; i++)
                AppendPreview(builder, children[i], indent + (last ? "   " : "│  "), i == children.Count - 1, assets, seenAssets);
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
    }
}
