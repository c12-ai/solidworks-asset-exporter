using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("Asset boundary never reads children", AssetBoundaryNeverReadsChildren);
            Run("Maximal no-Asset subtree becomes Project", MaximalNoAssetSubtree);
            Run("Only Asset-containing branch descends", NestedAssetOnlyDescendsRequiredBranch);
            Run("All-project root stays one unit", AllProjectRoot);
            Run("Top Asset stays one opaque unit", TopAssetRoot);
            Run("Repeated part occurrences share Asset identity", RepeatedPartOccurrencesShareAssetIdentity);
            Run("Hidden/suppressed/envelope nodes are ignored", VisibilityFiltering);
            Run("Mixed root requires fixed component", MixedRootRequiresFixedComponent);
            Run("Duplicate property scope fails", DuplicatePropertyScopeFails);
            Run("UUIDv5 matches RFC vector", Uuid5KnownVector);
            Run("Relative transform and quaternion", RelativeTransformAndQuaternion);
            Run("XML has leaf mesh references", XmlLeafReferences);
            Run("Manifest validates hashes and conflicts", ManifestValidation);
            Run("Project report validates immutable package", ProjectReportValidation);
            Run("Directory transaction is immutable", DirectoryTransactionIsImmutable);

            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : _failures + " TEST(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void AssetBoundaryNeverReadsChildren()
        {
            var asset = Node("asset", true, true);
            asset.ThrowOnChildren = true;
            asset.Model.FileProperties[PropertyRules.IsAsset] = "yes";
            asset.Model.FileProperties[PropertyRules.AssetVersion] = "2";
            asset.Model.FileProperties[PropertyRules.AssemblyVersion] = "1";
            var scan = new AssemblyScanner().Scan(asset);
            Equal(ScanClassification.AssetBoundary, scan.Classification);
            Equal(0, asset.GetChildrenCalls);
        }

        private static void MaximalNoAssetSubtree()
        {
            var root = Root();
            var asset = Node("motor", true, true);
            MarkAsset(asset, 3);
            var custom = Node("custom-frame", false, true);
            custom.Add(Node("plate", false, false), Node("bolt", false, false));
            root.Add(asset, custom);
            var scan = new AssemblyScanner().Scan(root);
            Equal(ScanClassification.ContainsAsset, scan.Classification);
            Equal(ScanClassification.NoAsset, scan.Children.Single(x => x.Source.Name == "custom-frame").Classification);
            var plan = new ExportPlanBuilder().Build(scan, ProjectMeshFormat.Step);
            Equal(2, plan.Roots.Count);
            Equal(1, plan.Roots.Count(x => x.Kind == ExportNodeKind.Asset));
            Equal(1, plan.Roots.Count(x => x.Kind == ExportNodeKind.Project));
            Equal(0, plan.Roots.Single(x => x.Kind == ExportNodeKind.Project).Children.Count);
        }

        private static void NestedAssetOnlyDescendsRequiredBranch()
        {
            var root = Root();
            var tooling = Node("tooling", true, true);
            var cylinder = Node("cylinder", false, false); MarkAsset(cylinder, 1);
            var fixture = Node("fixture", false, false); fixture.Add(Node("fixture-child", false, false));
            tooling.Add(cylinder, fixture);
            var unrelated = Node("unrelated", false, false); unrelated.Add(Node("detail", false, false));
            root.Add(tooling, unrelated);
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Stl);
            var group = plan.Roots.Single(x => x.Name == "tooling");
            Equal(ExportNodeKind.Group, group.Kind);
            Equal(2, group.Children.Count);
            Equal(ExportNodeKind.Asset, group.Children.Single(x => x.Name == "cylinder").Kind);
            Equal(ExportNodeKind.Project, group.Children.Single(x => x.Name == "fixture").Kind);
            Equal(ExportNodeKind.Project, plan.Roots.Single(x => x.Name == "unrelated").Kind);
            Equal(0, unrelated.Children[0].GetChildrenCalls);
        }

        private static void AllProjectRoot()
        {
            var root = Root(); root.Add(Node("a", false, false), Node("b", false, false));
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            Equal(1, plan.Roots.Count);
            Equal(ExportNodeKind.Project, plan.Roots[0].Kind);
            Equal(root.Name, plan.Roots[0].Name);
        }

        private static void TopAssetRoot()
        {
            var root = Root(); MarkAsset(root, 4); root.ThrowOnChildren = true;
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            Equal(1, plan.Roots.Count);
            Equal(ExportNodeKind.Asset, plan.Roots[0].Kind);
            Equal(0, root.GetChildrenCalls);
            True(plan.Roots[0].AssetId.EndsWith(":4", StringComparison.Ordinal));
        }

        private static void RepeatedPartOccurrencesShareAssetIdentity()
        {
            var root = Root();
            var firstModel = Descriptor("Follower"); firstModel.DocumentKind = DocumentKind.Part;
            var secondModel = Descriptor("Follower"); secondModel.DocumentKind = DocumentKind.Part;
            var first = new FakeNode("Follower-1", firstModel, true, false);
            var second = new FakeNode("Follower-3", secondModel, false, false);
            MarkAsset(first, 1); MarkAsset(second, 1); root.Add(first, second);

            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            var assets = plan.Roots.Where(node => node.Kind == ExportNodeKind.Asset).ToList();
            Equal(2, assets.Count);
            Equal(1, assets.Select(node => node.AssetId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            True(!string.Equals(assets[0].Id, assets[1].Id, StringComparison.OrdinalIgnoreCase));
        }

        private static void VisibilityFiltering()
        {
            var root = Root();
            var visible = Node("visible", true, true); MarkAsset(visible, 1);
            var hidden = Node("hidden", false, false); hidden.IsVisibleValue = false; MarkAsset(hidden, 1); hidden.ThrowOnChildren = true;
            var suppressed = Node("suppressed", false, false); suppressed.IsSuppressedValue = true; MarkAsset(suppressed, 1);
            var envelope = Node("envelope", false, false); envelope.IsEnvelopeValue = true; MarkAsset(envelope, 1);
            root.Add(visible, hidden, suppressed, envelope);
            var scan = new AssemblyScanner().Scan(root);
            Equal(1, scan.Children.Count);
            Equal("visible", scan.Children[0].Source.Name);
            Equal(0, hidden.GetChildrenCalls);
        }

        private static void MixedRootRequiresFixedComponent()
        {
            var root = Root();
            var asset = Node("asset", false, true); MarkAsset(asset, 1);
            root.Add(asset, Node("project", false, false));
            Throws<ValidationException>(() => new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step));
        }

        private static void DuplicatePropertyScopeFails()
        {
            var model = Descriptor("dup");
            model.FileProperties["is_asset"] = "false";
            model.ConfigurationProperties["IS_ASSET"] = "false";
            Throws<ValidationException>(() => PropertyRules.Merge(model));
        }

        private static void Uuid5KnownVector()
        {
            var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
            Equal("21f7f8de-8051-5b89-8680-0195ef798b6a", Uuid5.Create(dns, "www.widgets.com").ToString("D"));
        }

        private static void RelativeTransformAndQuaternion()
        {
            var parent = Translate(10, 3, -2);
            var child = Translate(12, 8, 1);
            var relative = parent.InverseRigid().Multiply(child);
            Near(2, relative[0, 3]); Near(5, relative[1, 3]); Near(3, relative[2, 3]);
            var z90 = new Matrix4(new[] { 0d, -1d, 0d, 0d, 1d, 0d, 0d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 0d, 1d });
            var q = z90.ToQuaternion();
            Near(0, q.X); Near(0, q.Y); Near(Math.Sqrt(0.5), Math.Abs(q.Z)); Near(Math.Sqrt(0.5), Math.Abs(q.W));
        }

        private static void XmlLeafReferences()
        {
            var root = Root();
            var group = Node("group", true, true);
            var asset = Node("asset", false, false); MarkAsset(asset, 2);
            var project = Node("project", false, false);
            group.Add(asset, project); root.Add(group);
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "assembly.xml"); AssemblyXmlWriter.Write(path, plan);
                var doc = XDocument.Load(path);
                Equal("m", (string)doc.Root.Attribute("length_unit"));
                Equal("xyzw", (string)doc.Root.Attribute("quaternion_order"));
                var nodes = doc.Descendants("node").ToList();
                Equal(3, nodes.Count);
                var groupElement = nodes.Single(x => (string)x.Attribute("kind") == "group");
                True(groupElement.Element("mesh") == null);
                var assetElement = nodes.Single(x => (string)x.Attribute("kind") == "asset");
                True(assetElement.Element("mesh").Attribute("asset_id") != null);
                True(assetElement.Element("mesh").Attribute("file") == null);
                var projectElement = nodes.Single(x => (string)x.Attribute("kind") == "project");
                True(((string)projectElement.Element("mesh").Attribute("file")).EndsWith("model.step", StringComparison.Ordinal));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void ManifestValidation()
        {
            var temp = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D"); var versionDir = Path.Combine(temp, uuid, "v1");
                Directory.CreateDirectory(Path.Combine(versionDir, "geometry"));
                Directory.CreateDirectory(Path.Combine(versionDir, "source", "models"));
                var model = Path.Combine(versionDir, "geometry", "model.step"); File.WriteAllText(model, "step-data");
                File.WriteAllText(Path.Combine(versionDir, "geometry", "model.stl"), "stl-data");
                File.WriteAllText(Path.Combine(versionDir, "source", "models", "root.SLDPRT"), "source-data");
                var manifest = new AssetManifest
                {
                    SchemaVersion = "1.0", Uuid = uuid, Version = 1, ContentFingerprint = "fingerprint",
                    Files = AssetManifestValidator.DescribeFiles(versionDir, new[] { "geometry/model.step", "geometry/model.stl", "source/models/root.SLDPRT" })
                };
                JsonFile.Write(Path.Combine(versionDir, "asset_" + uuid + "_v1.json"), manifest);
                Equal(ExistingAssetState.Reusable, AssetManifestValidator.Inspect(versionDir, uuid, 1, "fingerprint"));
                Throws<ValidationException>(() => AssetManifestValidator.Inspect(versionDir, uuid, 1, "changed"));
                File.AppendAllText(model, "tampered");
                Throws<ValidationException>(() => AssetManifestValidator.Inspect(versionDir, uuid, 1, "fingerprint"));
                Throws<ValidationException>(() => PathPolicy.CombineUnderRoot(versionDir, "../escape"));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void ProjectReportValidation()
        {
            var temp = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D"); var versionDir = Path.Combine(temp, uuid, "v2");
                Directory.CreateDirectory(Path.Combine(versionDir, "meshes", "unit"));
                var xml = "assembly_" + uuid + "_v2.xml";
                File.WriteAllText(Path.Combine(versionDir, xml), "<assembly />");
                File.WriteAllText(Path.Combine(versionDir, "meshes", "unit", "model.step"), "step");
                File.WriteAllText(Path.Combine(versionDir, "meshes", "unit", "model.stl"), "stl");
                var paths = new[] { xml, "meshes/unit/model.step", "meshes/unit/model.stl" };
                var report = new ExportReport
                {
                    SchemaVersion = "1.0", AssemblyUuid = uuid, AssemblyVersion = 2, ContentFingerprint = "project-fingerprint",
                    Files = AssetManifestValidator.DescribeFiles(versionDir, paths)
                };
                JsonFile.Write(Path.Combine(versionDir, "export-report.json"), report);
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(versionDir, uuid, 2, "project-fingerprint"));
                Throws<ValidationException>(() => ProjectReportValidator.Inspect(versionDir, uuid, 2, "changed"));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void DirectoryTransactionIsImmutable()
        {
            var temp = TempDirectory();
            try
            {
                var target = Path.Combine(temp, "v1");
                using (var transaction = new DirectoryTransaction(target))
                {
                    File.WriteAllText(Path.Combine(transaction.StagingDirectory, "value.txt"), "ok"); transaction.Commit();
                }
                True(File.Exists(Path.Combine(target, "value.txt")));
                using (var transaction = new DirectoryTransaction(target))
                    Throws<ValidationException>(() => transaction.Commit());
            }
            finally { Directory.Delete(temp, true); }
        }

        private static FakeNode Root()
        {
            var root = Node("root-assembly", false, true);
            root.Model.FileProperties[PropertyRules.AssemblyVersion] = "1";
            return root;
        }

        private static FakeNode Node(string name, bool fixedValue, bool assembly)
        {
            return new FakeNode(name, Descriptor(name), fixedValue, assembly);
        }

        private static ModelDescriptor Descriptor(string name)
        {
            return new ModelDescriptor
            {
                FullPath = "C:\\models\\" + name + ".SLDASM", FileName = name + ".SLDASM",
                InternalCreationTime = "2026-01-02T03:04:05.0000000Z", Configuration = "Default",
                DisplayState = "Display State-1", DocumentKind = DocumentKind.Assembly, IsSaved = true, IsDirty = false
            };
        }

        private static void MarkAsset(FakeNode node, int version)
        {
            node.Model.FileProperties[PropertyRules.IsAsset] = "true";
            node.Model.FileProperties[PropertyRules.AssetVersion] = version.ToString(CultureInfo.InvariantCulture);
        }

        private static Matrix4 Translate(double x, double y, double z)
        {
            return new Matrix4(new[] { 1d, 0d, 0d, x, 0d, 1d, 0d, y, 0d, 0d, 1d, z, 0d, 0d, 0d, 1d });
        }

        private static string TempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "sw-asset-export-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("Expected " + expected + ", got " + actual + ".");
        }

        private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
        private static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Expected " + expected + ", got " + actual + "."); }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name + ".");
        }
    }

    internal sealed class FakeNode : ICadNode
    {
        private readonly List<FakeNode> _children = new List<FakeNode>();
        private readonly bool _assembly;

        public FakeNode(string name, ModelDescriptor model, bool fixedValue, bool assembly)
        {
            Name = name; InstanceId = name + "-id"; InstancePath = "/" + name; Model = model;
            IsFixedValue = fixedValue; _assembly = assembly; IsVisibleValue = true; WorldTransformValue = Matrix4.Identity;
        }

        public string InstanceId { get; private set; }
        public string Name { get; private set; }
        public string InstancePath { get; set; }
        public bool IsVisible { get { return IsVisibleValue; } }
        public bool IsSuppressed { get { return IsSuppressedValue; } }
        public bool IsEnvelope { get { return IsEnvelopeValue; } }
        public bool IsFixed { get { return IsFixedValue; } }
        public ModelDescriptor Model { get; private set; }
        public Matrix4 WorldTransform { get { return WorldTransformValue; } }
        public bool IsVisibleValue { get; set; }
        public bool IsSuppressedValue { get; set; }
        public bool IsEnvelopeValue { get; set; }
        public bool IsFixedValue { get; set; }
        public bool ThrowOnChildren { get; set; }
        public int GetChildrenCalls { get; private set; }
        public Matrix4 WorldTransformValue { get; set; }
        public IList<FakeNode> Children { get { return _children; } }

        public FakeNode Add(params FakeNode[] nodes)
        {
            foreach (var node in nodes)
            {
                node.InstancePath = InstancePath + "/" + node.Name;
                UpdateDescendantPaths(node);
                _children.Add(node);
            }
            return this;
        }

        public IEnumerable<ICadNode> GetChildren()
        {
            GetChildrenCalls++;
            if (ThrowOnChildren) throw new Exception("GetChildren must not be called.");
            return _assembly ? _children.Cast<ICadNode>() : Enumerable.Empty<ICadNode>();
        }

        private static void UpdateDescendantPaths(FakeNode parent)
        {
            foreach (var child in parent._children)
            {
                child.InstancePath = parent.InstancePath + "/" + child.Name;
                UpdateDescendantPaths(child);
            }
        }
    }
}
