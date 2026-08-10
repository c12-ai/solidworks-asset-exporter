using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SolidWorksAssetExporter.Core
{
    [DataContract]
    public sealed class ManifestFile
    {
        [DataMember(Name = "path", Order = 1)] public string Path { get; set; }
        [DataMember(Name = "sha256", Order = 2)] public string Sha256 { get; set; }
        [DataMember(Name = "size", Order = 3)] public long Size { get; set; }
    }

    [DataContract]
    public sealed class AssetManifest
    {
        public AssetManifest()
        {
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Files = new List<ManifestFile>();
        }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "uuid", Order = 2)] public string Uuid { get; set; }
        [DataMember(Name = "version", Order = 3)] public int Version { get; set; }
        [DataMember(Name = "content_fingerprint", Order = 4)] public string ContentFingerprint { get; set; }
        [DataMember(Name = "properties", Order = 5)] public IDictionary<string, string> Properties { get; set; }
        [DataMember(Name = "files", Order = 6)] public IList<ManifestFile> Files { get; set; }
    }

    [DataContract]
    public sealed class ExportReport
    {
        public ExportReport()
        {
            ReusedAssets = new List<string>();
            CreatedAssets = new List<string>();
            ProjectUnits = new List<string>();
            Warnings = new List<string>();
            Files = new List<ManifestFile>();
        }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "assembly_uuid", Order = 2)] public string AssemblyUuid { get; set; }
        [DataMember(Name = "assembly_version", Order = 3)] public int AssemblyVersion { get; set; }
        [DataMember(Name = "content_fingerprint", Order = 4)] public string ContentFingerprint { get; set; }
        [DataMember(Name = "reused_assets", Order = 5)] public IList<string> ReusedAssets { get; set; }
        [DataMember(Name = "created_assets", Order = 6)] public IList<string> CreatedAssets { get; set; }
        [DataMember(Name = "project_units", Order = 7)] public IList<string> ProjectUnits { get; set; }
        [DataMember(Name = "warnings", Order = 8)] public IList<string> Warnings { get; set; }
        [DataMember(Name = "files", Order = 9)] public IList<ManifestFile> Files { get; set; }
    }

    public static class JsonFile
    {
        public static void Write<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var serializer = Create(typeof(T));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, true, true, "  "))
                serializer.WriteObject(writer, value);
        }

        public static T Read<T>(string path)
        {
            var serializer = Create(typeof(T));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (T)serializer.ReadObject(stream);
        }

        private static DataContractJsonSerializer Create(Type type)
        {
            return new DataContractJsonSerializer(type, new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        }
    }

    public static class AssemblyXmlWriter
    {
        public static void Write(string path, AssemblyExportPlan plan)
        {
            ExportPlanValidator.Validate(plan);
            var root = new XElement("assembly",
                new XAttribute("schema_version", "1.0"),
                new XAttribute("uuid", plan.AssemblyUuid),
                new XAttribute("version", plan.AssemblyVersion.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("project_mesh_format", plan.MeshFormat == ProjectMeshFormat.Step ? "step" : "stl"),
                new XAttribute("length_unit", "m"),
                new XAttribute("quaternion_order", "xyzw"));
            var nodes = new XElement("nodes");
            foreach (var exportRoot in plan.Roots) Append(nodes, exportRoot);
            root.Add(nodes);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" };
            using (var writer = XmlWriter.Create(path, settings)) new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        }

        private static void Append(XElement nodes, ExportNode node)
        {
            var element = new XElement("node",
                new XAttribute("id", node.Id),
                new XAttribute("parent_id", node.ParentId ?? string.Empty),
                new XAttribute("name", node.Name ?? string.Empty),
                new XAttribute("kind", node.Kind.ToString().ToLowerInvariant()));
            element.Add(new XElement("pose",
                Number("tx", node.Pose.Tx), Number("ty", node.Pose.Ty), Number("tz", node.Pose.Tz),
                Number("qx", node.Pose.Rotation.X), Number("qy", node.Pose.Rotation.Y),
                Number("qz", node.Pose.Rotation.Z), Number("qw", node.Pose.Rotation.W)));
            if (node.Kind == ExportNodeKind.Asset) element.Add(new XElement("mesh", new XAttribute("asset_id", node.AssetId)));
            if (node.Kind == ExportNodeKind.Project) element.Add(new XElement("mesh", new XAttribute("file", node.MeshFile)));
            nodes.Add(element);
            foreach (var child in node.Children) Append(nodes, child);
        }

        private static XAttribute Number(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ValidationException("位姿包含非有限数值。");
            return new XAttribute(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public enum ExistingAssetState { Missing, Reusable }

    public static class AssetManifestValidator
    {
        public static ExistingAssetState Inspect(string assetVersionDirectory, string expectedUuid, int expectedVersion,
            string currentContentFingerprint)
        {
            var manifestPath = Path.Combine(assetVersionDirectory,
                "asset_" + expectedUuid + "_v" + expectedVersion.ToString(CultureInfo.InvariantCulture) + ".json");
            if (!Directory.Exists(assetVersionDirectory) && !File.Exists(manifestPath)) return ExistingAssetState.Missing;
            if (!File.Exists(manifestPath)) throw new ValidationException("Asset 版本目录存在但缺少 manifest: [" + manifestPath + "]。");
            AssetManifest manifest;
            try { manifest = JsonFile.Read<AssetManifest>(manifestPath); }
            catch (Exception ex) { throw new ValidationException("Asset manifest 无法读取: " + ex.Message); }
            if (!string.Equals(manifest.Uuid, expectedUuid, StringComparison.OrdinalIgnoreCase) || manifest.Version != expectedVersion)
                throw new ValidationException("Asset manifest 的 UUID/版本与目录不一致。");
            if (!string.Equals(manifest.ContentFingerprint, currentContentFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Asset ID 已存在但当前模型内容不同；请提升 asset_version。");
            var assetPaths = new HashSet<string>((manifest.Files ?? new List<ManifestFile>()).Select(value => (value.Path ?? string.Empty).Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
            if (!assetPaths.Contains("geometry/model.step") || !assetPaths.Contains("geometry/model.stl") ||
                !assetPaths.Any(value => value.StartsWith("source/models/", StringComparison.OrdinalIgnoreCase)))
                throw new ValidationException("Asset manifest 缺少 STEP、STL 或源模型包声明。");
            if (assetPaths.Count != (manifest.Files ?? new List<ManifestFile>()).Count)
                throw new ValidationException("Asset manifest 包含重复文件路径。");
            foreach (var file in manifest.Files ?? new List<ManifestFile>())
            {
                var fullPath = PathPolicy.CombineUnderRoot(assetVersionDirectory, file.Path);
                if (!File.Exists(fullPath)) throw new ValidationException("Asset manifest 声明的文件不存在: [" + file.Path + "]。");
                var info = new FileInfo(fullPath);
                if (info.Length != file.Size || !string.Equals(FileHash.Sha256(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("Asset 文件哈希或大小校验失败: [" + file.Path + "]。");
            }
            return ExistingAssetState.Reusable;
        }

        public static IList<ManifestFile> DescribeFiles(string root, IEnumerable<string> relativePaths)
        {
            return relativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(relativePath =>
            {
                var fullPath = PathPolicy.CombineUnderRoot(root, relativePath);
                var info = new FileInfo(fullPath);
                return new ManifestFile { Path = relativePath.Replace('\\', '/'), Size = info.Length, Sha256 = FileHash.Sha256(fullPath) };
            }).ToList();
        }
    }

    public enum ExistingProjectState { Missing, Reusable }

    public static class ProjectReportValidator
    {
        public static ExistingProjectState Inspect(string projectVersionDirectory, string expectedAssemblyUuid,
            int expectedVersion, string currentContentFingerprint)
        {
            var reportPath = Path.Combine(projectVersionDirectory, "export-report.json");
            if (!Directory.Exists(projectVersionDirectory) && !File.Exists(reportPath)) return ExistingProjectState.Missing;
            if (!File.Exists(reportPath)) throw new ValidationException("Project 版本目录存在但缺少 export-report.json。");
            ExportReport report;
            try { report = JsonFile.Read<ExportReport>(reportPath); }
            catch (Exception ex) { throw new ValidationException("Project export report 无法读取: " + ex.Message); }
            if (!string.Equals(report.AssemblyUuid, expectedAssemblyUuid, StringComparison.OrdinalIgnoreCase) || report.AssemblyVersion != expectedVersion)
                throw new ValidationException("Project export report 的 UUID/版本与目录不一致。");
            if (!string.Equals(report.ContentFingerprint, currentContentFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Project 版本已存在但当前内容不同；请提升 assembly_version。");
            var projectPaths = new HashSet<string>((report.Files ?? new List<ManifestFile>()).Select(value => (value.Path ?? string.Empty).Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
            if (projectPaths.Count != (report.Files ?? new List<ManifestFile>()).Count ||
                projectPaths.Count(value => value.StartsWith("assembly_", StringComparison.OrdinalIgnoreCase) && value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new ValidationException("Project report 必须声明一个 assembly XML，且文件路径不能重复。");
            foreach (var path in projectPaths.Where(value => value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) && value.EndsWith("/model.step", StringComparison.OrdinalIgnoreCase)))
                if (!projectPaths.Contains(path.Substring(0, path.Length - 4) + "stl")) throw new ValidationException("Project 单元缺少 STL: " + path);
            foreach (var path in projectPaths.Where(value => value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) && value.EndsWith("/model.stl", StringComparison.OrdinalIgnoreCase)))
                if (!projectPaths.Contains(path.Substring(0, path.Length - 3) + "step")) throw new ValidationException("Project 单元缺少 STEP: " + path);
            foreach (var file in report.Files ?? new List<ManifestFile>())
            {
                var fullPath = PathPolicy.CombineUnderRoot(projectVersionDirectory, file.Path);
                if (!File.Exists(fullPath)) throw new ValidationException("Project report 声明的文件不存在: [" + file.Path + "]。");
                var info = new FileInfo(fullPath);
                if (info.Length != file.Size || !string.Equals(FileHash.Sha256(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("Project 文件哈希或大小校验失败: [" + file.Path + "]。");
            }
            return ExistingProjectState.Reusable;
        }
    }
}
