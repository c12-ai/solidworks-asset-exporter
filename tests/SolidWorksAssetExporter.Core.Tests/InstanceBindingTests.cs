using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunInstanceBindingTests()
        {
            RunBindingEditorTests();
            Run("Binding AT-04 repeated asset occurrences remain distinct", BindingRepeatedInstances);
            Run("Binding AT-06 apply is idempotent and unbind is specific", BindingIdempotence);
            Run("Binding AT-07 invalid batch is atomic and replacement is explicit", BindingAtomicConflict);
            Run("Binding multiple parents allow diamonds but reject cycles and duplicate edges", BindingMultipleParentGraphs);
            Run("Binding shared child exports a separate relative pose for each parent", BindingMultipleParentPoses);
            Run("Binding relation schema reads legacy data and guards new multi-parent packages", BindingSchemaCompatibility);
            Run("Binding AT-09 self and long cycles are rejected atomically", BindingCycles);
            Run("Binding AT-10 cross-group multilevel chains are allowed", BindingChains);
            Run("Binding AT-03 unsupported endpoints do not expand Asset boundaries", BindingEndpointKinds);
            Run("Binding AT-11 required interfaces and role chains are revalidated", BindingInterfaces);
            Run("Optional Asset interfaces are required on explicit binding endpoints", BindingOptionalInterfaces);
            Run("Binding rack supports direct tools and changer children in exported packages", BindingRackTools);
            Run("Binding direct rack tools retain interface checks and support shared tools", BindingRackToolGuards);
            Run("Binding direct rack tool support preserves other changer role restrictions", BindingRackToolRoleGuards);
            Run("Binding AT-13 unbound instances produce no inferred declarations", BindingEmpty);
            Run("Binding AT-19 invisible suppressed and envelope endpoints block", BindingUnavailable);
            Run("Binding AT-23 cross-group rotation computes parent-relative pose", BindingRotatedPose);
            Run("Binding AT-24 relative quaternion and common outer transform", BindingOuterTransform);
            Run("Binding AT-25 CAD hierarchy and poses remain unchanged", BindingPreservesXml);
            Run("Binding AT-22 same-package node IDs and identity are validated", BindingPackageIdentity);
            Run("Binding AT-27 corrupt or missing relation files cannot be reused", BindingReportValidation);
            Run("Binding AT-28 binding changes and movement change fingerprints", BindingFingerprints);
            Run("Binding AT-30 HTTP archive contains exact relations and report", BindingZip);
            Run("Binding AT-14,15 saved revisions survive close and retry", BindingSaveLifecycle);
            Run("Binding AT-08 cancelled selections and unsaved close do not persist", BindingDiscard);
            Run("Binding AT-17 configurations and copies do not share records", BindingConfigurations);
            Run("Binding AT-29 binding dirty bypasses internal mutation exemptions", BindingBusinessDirty);
            Run("Binding AT-18 unresolved persistent refs never fall back", BindingNoFallback);
            Run("Binding storage corruption and unsupported schemas fail closed", BindingCorruptStorage);
        }
        private sealed class BindingFixture
        {
            public readonly AssemblyExportPlan Plan = new AssemblyExportPlan { AssemblyUuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa", AssemblyVersion = 2, ExportProject = true };
            public readonly Dictionary<string, ExportNode> Nodes = new Dictionary<string, ExportNode>();
            public readonly List<InstanceBinding> Records = new List<InstanceBinding>();
            public BindingFixture()
            {
                foreach (var name in new[] { "A", "B", "T1", "T2", "T3" })
                {
                    var source = Node(name, true, false);
                    source.Model.FileProperties[PropertyRules.ConnectionInterface] = "test-D20";
                    source.Model.FileProperties[PropertyRules.AcceptsInterfaces] = "test-D20";
                    var n = new ExportNode { Id = name, Name = name, Kind = ExportNodeKind.Asset, Source = source,
                        AssetId = (name == "B" ? "A" : name) + ":1", Properties = source.Model.FileProperties, Pose = Pose.FromTransform(Matrix4.Identity) };
                    Nodes.Add(name, n); Plan.Roots.Add(n);
                }
            }
            public BindingEndpoint E(string name) { return new BindingEndpoint { Reference = name, Context = name, DisplayName = name, DisplayPath = "/root/" + name }; }
            public ExportNode Resolve(BindingEndpoint e)
            { if (e == null || !Nodes.ContainsKey(e.Reference)) throw new ValidationException("引用失效：" + (e == null ? "null" : e.Reference)); return Nodes[e.Reference]; }
            public void Apply(string parent, bool replace, params string[] children)
            { var next = InstanceBindingRules.Apply(Records, E(parent), children.Select(E), Resolve, replace); Records.Clear(); Records.AddRange(next); }
            public AssemblyRelations Export() { return Plan.Relations = InstanceBindingRules.Resolve(Records, Resolve, Plan); }
        }
        private static void BindingRepeatedInstances()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1", "T2", "T3", "T1");
            var r = f.Export(); Equal(3, r.Relations.Count); True(r.Relations.All(x => x.ParentNodeId == "A"));
            Equal(f.Nodes["A"].AssetId, f.Nodes["B"].AssetId); True(!r.Relations.Any(x => x.ParentNodeId == "B"));
        }
        private static void BindingIdempotence()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1", "T2"); var id = f.Records[0].Id;
            f.Apply("A", false, "T1"); Equal(2, f.Records.Count); Equal(id, f.Records[0].Id);
            f.Records.RemoveAll(r => r.Child.Reference == "T1"); Equal("T2", f.Export().Relations.Single().ChildNodeId);
        }
        private static void BindingAtomicConflict()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            f.Nodes["T2"].Properties[PropertyRules.ConnectionInterface] = "wrong-mount";
            Throws<ValidationException>(() => f.Apply("B", false, "T2", "T1"));
            Equal(1, f.Records.Count); Equal("A", f.Export().Relations.Single().ParentNodeId);
            f.Apply("B", false, "T1"); Equal(2, f.Records.Count);
            var targetId = f.Records.Single(r => r.Parent.Reference == "B").Id;
            f.Apply("B", true, "T1"); Equal(1, f.Records.Count); Equal("B", f.Export().Relations.Single().ParentNodeId);
            Equal(targetId, f.Records.Single().Id);
        }
        private static void BindingMultipleParentGraphs()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1", "T2");
            f.Apply("T1", false, "T3"); f.Apply("T2", false, "T3"); f.Apply("B", false, "T3");
            Equal(5, f.Export().Relations.Count);
            f.Apply("T1", false, "T3", "T3"); Equal(5, f.Records.Count);
            Throws<ValidationException>(() => f.Apply("T3", false, "A")); Equal(5, f.Records.Count);
            Throws<ValidationException>(() => f.Apply("T3", false, "B")); Equal(5, f.Records.Count);
            var removed = f.Records.Single(r => r.Parent.Reference == "T1" && r.Child.Reference == "T3");
            f.Records.Remove(removed); Equal(4, f.Export().Relations.Count);
            Equal(2, f.Export().Relations.Count(r => r.ChildNodeId == "T3"));
            f.Records.Add(new InstanceBinding { Parent = f.E("T2"), Child = f.E("T3") });
            Throws<ValidationException>(() => f.Export());
        }
        private static void BindingMultipleParentPoses()
        {
            var f = new BindingFixture();
            ((FakeNode)f.Nodes["A"].Source).WorldTransformValue = BindingTransform(1, 2, 0, Math.PI / 2);
            ((FakeNode)f.Nodes["B"].Source).WorldTransformValue = BindingTransform(-2, .5, 1, 0);
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = BindingTransform(.8, 2.1, .3, Math.PI / 2);
            f.Apply("A", false, "T1"); f.Apply("B", false, "T1");
            var r = f.Export(); var a = r.Relations.Single(x => x.ParentNodeId == "A").Pose;
            var b = r.Relations.Single(x => x.ParentNodeId == "B").Pose;
            Near(.1, a.Tx); Near(.2, a.Ty); Near(.3, a.Tz); Near(1, a.Qw);
            Near(2.8, b.Tx); Near(1.6, b.Ty); Near(-.7, b.Tz); Near(Math.Sqrt(.5), Math.Abs(b.Qz));
            var temp = TempDirectory();
            try
            {
                BindingWritePackage(temp, f);
                var read = JsonFile.Read<AssemblyRelations>(Path.Combine(temp, AssemblyRelations.FileName));
                Equal("1.1", read.SchemaVersion); Equal(2, read.Relations.Count);
                read.Validate(BindingXml(f), f.Plan.AssemblyUuid, 2);
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
            }
            finally { Directory.Delete(temp, true); }
        }
        private static void BindingSchemaCompatibility()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            var legacy = f.Export(); legacy.SchemaVersion = "1.0"; legacy.Validate(BindingXml(f), f.Plan.AssemblyUuid, 2);
            f.Apply("B", false, "T1"); var multiple = f.Export(); multiple.SchemaVersion = "1.0";
            Throws<ValidationException>(() => multiple.Validate(BindingXml(f), f.Plan.AssemblyUuid, 2));
            var temp = TempDirectory();
            try
            {
                BindingWritePackage(temp, f);
                var reportPath = Path.Combine(temp, "export-report.json"); var report = JsonFile.Read<ExportReport>(reportPath);
                report.RelationsSchemaVersion = "1.0"; File.Delete(reportPath); JsonFile.Write(reportPath, report);
                Throws<ValidationException>(() => ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                f.Records.RemoveAll(r => r.Parent.Reference == "B");
                var file = Path.Combine(temp, AssemblyRelations.FileName); File.Delete(file); JsonFile.Write(file, legacy);
                report.Files = AssetManifestValidator.DescribeFiles(temp, report.Files.Select(x => x.Path));
                File.Delete(reportPath); JsonFile.Write(reportPath, report);
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
            }
            finally { Directory.Delete(temp, true); }
            var data = new BindingDocument { SchemaVersion = "1.0" };
            data.Configurations.Add(new ConfigurationBindings { ConfigurationId = 1, ConfigurationName = "A", Relations = f.Records.ToList() });
            var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(BindingDocument));
            var document = new BindingTestDocument();
            using (var memory = new MemoryStream()) { serializer.WriteObject(memory, data); document.Bytes = memory.ToArray(); }
            var session = SwInstanceBindings.ForDocument(document);
            try
            {
                Equal(1, session.Current.Relations.Count); f.Apply("B", false, "T1"); session.Commit(f.Records.ToList()); document.Save(true);
                using (var memory = new MemoryStream(document.Bytes))
                { var saved = (BindingDocument)serializer.ReadObject(memory); Equal("1.1", saved.SchemaVersion); Equal(2, saved.ForConfiguration(1, "A").Relations.Count); }
            }
            finally { session.Dispose(); }
        }
        private static void BindingCycles()
        {
            var f = new BindingFixture(); Throws<ValidationException>(() => f.Apply("A", false, "A")); Equal(0, f.Records.Count);
            f.Apply("A", false, "T1"); f.Apply("T1", false, "T2");
            Throws<ValidationException>(() => f.Apply("T2", false, "A")); Equal(2, f.Records.Count);
        }
        private static void BindingChains()
        {
            var f = new BindingFixture(); f.Nodes["A"].ParentId = "g1"; f.Nodes["T1"].ParentId = "g2";
            f.Apply("A", false, "T1"); f.Apply("T1", false, "T2"); Equal(2, f.Export().Relations.Count);
        }
        private static void BindingEndpointKinds()
        {
            foreach (var kind in new[] { ExportNodeKind.Group, ExportNodeKind.Meshes, ExportNodeKind.Robot, ExportNodeKind.Workstation })
            { var f = new BindingFixture(); f.Nodes["T1"].Kind = kind; Throws<ValidationException>(() => f.Apply("A", false, "T2", "T1")); Equal(0, f.Records.Count); }
            var missing = new BindingFixture(); missing.Nodes.Remove("T1");
            Throws<ValidationException>(() => missing.Apply("A", false, "T1"));
        }
        private static void BindingInterfaces()
        {
            var f = new BindingFixture(); f.Nodes["A"].Properties[PropertyRules.ConnectionInterface] = "";
            f.Apply("A", false, "T1"); Equal(1, f.Export().Relations.Count);
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = "mismatch";
            Throws<ValidationException>(() => f.Export()); Throws<ValidationException>(() => f.Apply("A", false, "T2", "T1")); Equal(1, f.Records.Count);
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = ""; Throws<ValidationException>(() => f.Export());
            f = new BindingFixture(); f.Nodes["A"].Properties[PropertyRules.IsQuickChangerRack] = "1";
            f.Nodes["A"].Properties[PropertyRules.AssetClass] = "station";
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));
            f.Nodes["T1"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["T1"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            f.Apply("A", false, "T1"); Equal(1, f.Export().Relations.Count);
        }

        private static void BindingOptionalInterfaces()
        {
            var f = new BindingFixture();
            var parent = f.Nodes["A"].Properties;
            var child = f.Nodes["T1"].Properties;
            var robot = new Dictionary<string, string> { { PropertyRules.AssetClass, "robot" } };
            parent[PropertyRules.AssetClass] = "station";
            parent[PropertyRules.IsFixture] = "1";
            child[PropertyRules.AssetClass] = "movable";
            foreach (var properties in new[] { parent, child, robot })
            {
                properties.Remove(PropertyRules.ConnectionInterface);
                properties.Remove(PropertyRules.AcceptsInterfaces);
                Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
                properties[PropertyRules.ConnectionInterface] = " ";
                properties[PropertyRules.AcceptsInterfaces] = string.Empty;
                Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            }
            Equal(0, f.Export().Relations.Count);

            robot[PropertyRules.IsFixture] = "1";
            True(PropertyRules.ValidateAssetConnectionProperties(robot).Single().Contains("[class]=robot"));
            child[PropertyRules.IsTool] = "1";
            True(PropertyRules.ValidateAssetConnectionProperties(child).Single().Contains(PropertyRules.ConnectionInterface));
            child.Remove(PropertyRules.IsTool);
            parent[PropertyRules.IsQuickChangerRack] = "1";
            True(PropertyRules.ValidateAssetConnectionProperties(parent).Single().Contains(PropertyRules.AcceptsInterfaces));
            parent.Remove(PropertyRules.IsQuickChangerRack);

            parent[PropertyRules.AcceptsInterfaces] = "test-D20";
            child[PropertyRules.ConnectionInterface] = "test-D20";
            f.Apply("A", false, "T1");
            Equal(1, f.Export().Relations.Count);
            parent.Remove(PropertyRules.AcceptsInterfaces);
            Throws<ValidationException>(() => f.Export());
            parent[PropertyRules.AcceptsInterfaces] = "test-D20";
            child[PropertyRules.ConnectionInterface] = " ";
            Throws<ValidationException>(() => f.Export());
            child[PropertyRules.ConnectionInterface] = "mismatch";
            Throws<ValidationException>(() => f.Export());
        }

        private static BindingFixture RackToolFixture()
        {
            var f = new BindingFixture();
            f.Nodes["A"].Properties[PropertyRules.IsQuickChangerRack] = "1";
            f.Nodes["A"].Properties[PropertyRules.AssetClass] = "station";
            f.Nodes["T1"].Properties[PropertyRules.IsTool] = "1";
            return f;
        }
        private static void BindingRackTools()
        {
            var f = RackToolFixture();
            // Revalidate an existing record (including one previously rejected by the old role rule).
            var saved = new InstanceBinding { Parent = f.E("A"), Child = f.E("T1") };
            f.Records.Add(saved); Equal(1, f.Export().Relations.Count);
            f.Apply("A", false, "T1"); Equal(saved.Id, f.Records.Single().Id);
            f.Nodes["T2"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["T2"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            f.Apply("A", false, "T2"); Equal(2, f.Export().Relations.Count);
            var temp = TempDirectory();
            try
            {
                BindingWritePackage(temp, f);
                var relations = JsonFile.Read<AssemblyRelations>(Path.Combine(temp, AssemblyRelations.FileName));
                relations.Validate(BindingXml(f), f.Plan.AssemblyUuid, f.Plan.AssemblyVersion);
                Equal(2, relations.Relations.Count);
                True(relations.Relations.All(r => r.ParentNodeId == "A"));
                True(relations.Relations.Any(r => r.ChildNodeId == "T1"));
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
            }
            finally { Directory.Delete(temp, true); }
        }
        private static void BindingRackToolGuards()
        {
            var f = RackToolFixture();
            f.Nodes["T2"].Properties[PropertyRules.IsTool] = "1";
            f.Nodes["T2"].Properties[PropertyRules.ConnectionInterface] = "wrong-mount";
            Throws<ValidationException>(() => f.Apply("A", false, "T1", "T2")); Equal(0, f.Records.Count);
            f.Apply("A", false, "T1");
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = "wrong-mount";
            Throws<ValidationException>(() => f.Export()); Equal(1, f.Records.Count);
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = "test-D20";
            f.Nodes["B"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["B"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            f.Apply("B", false, "T1"); Equal(2, f.Export().Relations.Count);
            f.Apply("A", false, "T1"); Equal(2, f.Export().Relations.Count);
            f.Apply("B", true, "T1"); Equal("B", f.Export().Relations.Single().ParentNodeId);
            f.Apply("A", true, "T1"); Equal("A", f.Export().Relations.Single().ParentNodeId);
            f.Nodes["A"].Properties[PropertyRules.AssetClass] = "movable";
            Throws<ValidationException>(() => f.Export());
        }
        private static void BindingRackToolRoleGuards()
        {
            var f = RackToolFixture();
            f.Nodes["T1"].Properties.Remove(PropertyRules.IsTool);
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));
            f.Nodes["T1"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["T1"].Properties[PropertyRules.QuickChangerSide] = "robot_side";
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));
            f = RackToolFixture(); f.Nodes["A"].Properties.Remove(PropertyRules.IsQuickChangerRack);
            f.Nodes["A"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["A"].Properties[PropertyRules.QuickChangerSide] = "robot_side";
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));
            f.Nodes["A"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            f.Apply("A", false, "T1"); Equal(1, f.Export().Relations.Count);
            f.Nodes["T2"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["T2"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            Throws<ValidationException>(() => f.Apply("A", false, "T2")); Equal(1, f.Records.Count);
        }
        private static void BindingEmpty()
        { var f = new BindingFixture(); Equal(0, f.Export().Relations.Count); True(!f.Nodes["B"].Properties.ContainsKey("slots_num")); }
        private static void BindingUnavailable()
        {
            foreach (var state in new[] { "hidden", "suppressed", "envelope", "deleted" })
            {
                var f = new BindingFixture(); f.Apply("A", false, "T1"); var s = (FakeNode)f.Nodes["T1"].Source;
                if (state == "hidden") s.IsVisibleValue = false;
                if (state == "suppressed") s.IsSuppressedValue = true;
                if (state == "envelope") s.IsEnvelopeValue = true;
                if (state == "deleted") f.Nodes.Remove("T1");
                Throws<ValidationException>(() => f.Export()); Equal(1, f.Records.Count);
            }
        }
        private static Matrix4 BindingTransform(double x, double y, double z, double angle)
        { var c = Math.Cos(angle); var s = Math.Sin(angle); return new Matrix4(new[] { c, -s, 0, x, s, c, 0, y, 0, 0, 1, z, 0, 0, 0, 1 }); }
        private static void BindingRotatedPose()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            ((FakeNode)f.Nodes["A"].Source).WorldTransformValue = BindingTransform(1, 2, 0, Math.PI / 2);
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = BindingTransform(.8, 2.1, .3, Math.PI / 2);
            var p = f.Export().Relations.Single().Pose; Near(.1, p.Tx); Near(.2, p.Ty); Near(.3, p.Tz); Near(1, p.Qw); Near(0, p.Qz);
        }
        private static void BindingOuterTransform()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            var p = BindingTransform(1, 2, 0, Math.PI / 2); var c = p.Multiply(BindingTransform(.1, .2, .3, Math.PI / 2));
            var outer = BindingTransform(15, -3, 7, -.73);
            ((FakeNode)f.Nodes["A"].Source).WorldTransformValue = outer.Multiply(p);
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = outer.Multiply(c);
            var r = f.Export().Relations.Single().Pose; Near(.1, r.Tx); Near(.2, r.Ty); Near(.3, r.Tz);
            Near(Math.Sqrt(.5), Math.Abs(r.Qz)); Near(Math.Sqrt(.5), Math.Abs(r.Qw));
        }
        private static void BindingPreservesXml()
        {
            var temp = TempDirectory();
            try
            {
                var f = new BindingFixture(); var path = Path.Combine(temp, "before.xml"); AssemblyXmlWriter.Write(path, f.Plan);
                var before = File.ReadAllText(path); f.Apply("A", false, "T1"); f.Export();
                AssemblyXmlWriter.Write(path, f.Plan); Equal(before, File.ReadAllText(path));
            }
            finally { Directory.Delete(temp, true); }
        }
        private static void BindingPackageIdentity()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1"); var r = f.Export();
            var xml = BindingXml(f); r.Validate(xml, f.Plan.AssemblyUuid, 2);
            r.AssemblyVersion = 3; Throws<ValidationException>(() => r.Validate(xml, f.Plan.AssemblyUuid, 2));
            r.AssemblyVersion = 2; r.Relations[0].ChildNodeId = "missing"; Throws<ValidationException>(() => r.Validate(xml, f.Plan.AssemblyUuid, 2));
            r.Relations[0].ChildNodeId = "T1"; r.Relations[0].Pose.Qw = double.NaN; Throws<ValidationException>(() => r.Validate(xml, f.Plan.AssemblyUuid, 2));
        }
        private static XDocument BindingXml(BindingFixture f)
        {
            return new XDocument(new XElement("assembly", new XAttribute("uuid", f.Plan.AssemblyUuid), new XAttribute("version", 2),
                new XElement("nodes", f.Nodes.Values.Select(n => new XElement("node", new XAttribute("id", n.Id), new XAttribute("kind", n.Kind.ToString().ToLowerInvariant()))))));
        }
        private static void BindingWritePackage(string directory, BindingFixture f)
        {
            Directory.CreateDirectory(directory); var xml = "assembly_" + f.Plan.AssemblyUuid + "_v2.xml";
            AssemblyXmlWriter.Write(Path.Combine(directory, xml), f.Plan); JsonFile.Write(Path.Combine(directory, AssemblyRelations.FileName), f.Export());
            JsonFile.Write(Path.Combine(directory, "export-report.json"), new ExportReport
            {
                SchemaVersion = "1.0", AssemblyUuid = f.Plan.AssemblyUuid, AssemblyVersion = 2, RelationsSchemaVersion = AssemblyRelations.CurrentSchemaVersion,
                ContentFingerprint = "binding-fingerprint", Files = AssetManifestValidator.DescribeFiles(directory, new[] { xml, AssemblyRelations.FileName })
            });
        }
        private static void BindingReportValidation()
        {
            var temp = TempDirectory();
            try
            {
                var f = new BindingFixture(); f.Apply("A", false, "T1"); BindingWritePackage(temp, f);
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                var file = Path.Combine(temp, AssemblyRelations.FileName); var saved = File.ReadAllBytes(file);
                File.AppendAllText(file, "tampered"); Throws<ValidationException>(() => ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                File.Delete(file); Throws<ValidationException>(() => ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                File.WriteAllBytes(file, saved);
                var r = JsonFile.Read<AssemblyRelations>(file); r.AssemblyUuid = Guid.NewGuid().ToString("D"); File.Delete(file); JsonFile.Write(file, r);
                var reportFile = Path.Combine(temp, "export-report.json"); var report = JsonFile.Read<ExportReport>(reportFile);
                report.Files = AssetManifestValidator.DescribeFiles(temp, report.Files.Select(x => x.Path)); File.Delete(reportFile); JsonFile.Write(reportFile, report);
                Throws<ValidationException>(() => ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
            }
            finally { Directory.Delete(temp, true); }
        }
        private static void BindingFingerprints()
        {
            var f = new BindingFixture(); f.Export(); var empty = PlanFingerprint(f.Plan);
            f.Apply("A", false, "T1"); f.Export(); var bound = PlanFingerprint(f.Plan); True(bound != empty);
            f.Apply("B", true, "T1"); f.Export(); True(PlanFingerprint(f.Plan) != bound);
            f.Apply("A", true, "T1"); f.Export(); Equal(bound, PlanFingerprint(f.Plan));
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = BindingTransform(.1, 0, 0, 0); f.Export(); True(bound != PlanFingerprint(f.Plan));
            f.Records.Clear(); f.Export(); Equal(empty, PlanFingerprint(f.Plan));
        }
        private static void BindingZip()
        {
            var temp = TempDirectory();
            try
            {
                var f = new BindingFixture(); f.Apply("A", false, "T1", "T2", "T3"); f.Apply("B", false, "T1"); BindingWritePackage(temp, f);
                var handler = new RecordingHttpHandler();
                using (var client = new WanxiangDataClient("http://localhost:8100", "secret-key", handler)) client.UploadDirectory(temp, "assembly_package/test/v2");
                using (var memory = new MemoryStream(handler.Body))
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry(AssemblyRelations.FileName); True(entry != null);
                    using (var reader = new StreamReader(entry.Open())) Equal(File.ReadAllText(Path.Combine(temp, AssemblyRelations.FileName)), reader.ReadToEnd());
                    True(zip.GetEntry("export-report.json") != null); Equal(3, zip.Entries.Count);
                }
            }
            finally { Directory.Delete(temp, true); }
        }
        private static InstanceBinding BindingRecord() { var f = new BindingFixture(); return new InstanceBinding { Parent = f.E("A"), Child = f.E("T1") }; }
        private static void BindingSaveLifecycle()
        {
            var d = new BindingTestDocument(); var s = SwInstanceBindings.ForDocument(d);
            try
            {
                var f = new BindingFixture(); f.Apply("A", false, "T1"); f.Apply("B", false, "T1");
                s.Commit(f.Records.ToList()); True(s.Pending); True(d.Dirty);
                d.Save(false); True(s.Pending); d.FailWrite = true; d.Save(true); True(s.Pending); True(s.LastSaveError != null);
                d.FailWrite = false; d.Save(true); True(!s.Pending); True(s.LastSaveError == null);
                var bytes = d.Bytes; s.Dispose();
                var reopened = new BindingTestDocument { Bytes = bytes }; var next = SwInstanceBindings.ForDocument(reopened);
                try { Equal(2, next.Current.Relations.Count); Equal(2, next.Current.Relations.Select(r => r.Parent.Context).Distinct().Count()); Equal(1, next.Current.Relations.Select(r => r.Child.Context).Distinct().Count()); True(!next.Pending); }
                finally { next.Dispose(); }
            }
            finally { s.Dispose(); }
        }
        private static void BindingDiscard()
        {
            var d = new BindingTestDocument(); var s = SwInstanceBindings.ForDocument(d);
            var f = new BindingFixture(); f.Apply("A", false, "T1"); Equal(0, s.Current.Relations.Count);
            s.Commit(f.Records.ToList()); d.Save(true); var saved = d.Bytes;
            s.Commit(new List<InstanceBinding>()); True(s.Pending); s.Dispose();
            var reopened = SwInstanceBindings.ForDocument(new BindingTestDocument { Bytes = saved });
            try { Equal(1, reopened.Current.Relations.Count); } finally { reopened.Dispose(); }
        }
        private static void BindingConfigurations()
        {
            var d = new BindingTestDocument(); var s = SwInstanceBindings.ForDocument(d);
            try
            {
                s.Commit(new List<InstanceBinding> { BindingRecord() }); d.Config.Id = 2; d.Config.Name = "B"; Equal(0, s.Current.Relations.Count);
                var r = BindingRecord(); r.Parent.Reference = "B"; s.Commit(new List<InstanceBinding> { r });
                d.Config.Id = 1; Equal("A", s.Current.Relations.Single().Parent.Reference);
                d.Config.Name = "renamed"; Equal(1, s.Current.Relations.Count);
                d.Config.Id = 3; Equal(0, s.Current.Relations.Count); d.Save(true); s.Dispose();
                var nextDoc = new BindingTestDocument { Bytes = d.Bytes }; var next = SwInstanceBindings.ForDocument(nextDoc);
                try { Equal("A", next.Current.Relations.Single().Parent.Reference); nextDoc.Config.Id = 2; Equal("B", next.Current.Relations.Single().Parent.Reference); }
                finally { next.Dispose(); }
            }
            finally { s.Dispose(); }
        }
        private static void BindingBusinessDirty()
        {
            var d = new BindingTestDocument(); var app = new SldWorks { ActiveDoc = d }; var tracker = new SwPluginMutationTracker(app);
            var s = SwInstanceBindings.ForDocument(d);
            try
            {
                using (tracker.BeginMutation(d)) { d.Dirty = true; }
                True(!tracker.IsDirty(d, false, d.GetPathName()));
                s.Commit(new List<InstanceBinding> { BindingRecord() }); True(tracker.IsDirty(d, true, d.GetPathName()));
                Throws<ValidationException>(() => s.RequireSaved()); d.Save(true); s.RequireSaved();
                var revision = s.Revision; s.Commit(new List<InstanceBinding>()); True(s.Revision != revision); True(tracker.IsDirty(d, false, d.GetPathName()));
            }
            finally { s.Dispose(); }
        }
        private static void BindingNoFallback()
        {
            var d = new BindingTestDocument(); var s = SwInstanceBindings.ForDocument(d);
            try { Throws<ValidationException>(() => s.ResolveComponent(new BindingEndpoint { Reference = Convert.ToBase64String(new byte[] { 1 }), DisplayName = "same", AssetId = "same:1", Context = "1" })); }
            finally { s.Dispose(); }
        }
        private static void BindingCorruptStorage()
        {
            var d = new BindingTestDocument { Bytes = new byte[] { 1, 2, 3 } }; var s = SwInstanceBindings.ForDocument(d);
            try { Throws<ValidationException>(() => s.RequireSaved()); Throws<ValidationException>(() => s.Commit(new List<InstanceBinding>())); True(d.Released); }
            finally { s.Dispose(); }
            var data = new BindingDocument { SchemaVersion = "2.0" }; Throws<ValidationException>(() => data.ForConfiguration(1, "A"));
            data = new BindingDocument(); var configuration = new ConfigurationBindings { ConfigurationId = 1 };
            configuration.Relations.Add(new InstanceBinding { Parent = null, Child = new BindingEndpoint { Reference = "child" } });
            data.Configurations.Add(configuration); Throws<ValidationException>(() => data.ForConfiguration(1, "A"));
        }
    }

    public sealed class BindingTestConfiguration : Configuration, ConfigurationManager
    {
        public int Id = 1; public int GetID() { return Id; }
        public string Name { get; set; }
        public Configuration ActiveConfiguration { get { return this; } }
        public Component2 GetRootComponent3(bool resolve) { return null; }
        public object GetDisplayStates() { return null; }
    }
    public sealed class BindingTestDocument : ModelDoc2, DAssemblyDocEvents_Event
    {
        public event Func<int> NewSelectionNotify { add { } remove { } }
        public readonly BindingTestConfiguration Config = new BindingTestConfiguration { Name = "A" };
        public bool Dirty, FailWrite, Released; public byte[] Bytes; private BindingTestStream _writing;
        public event Func<int> SaveToStorageNotify;
        public event Func<int, string, int> FileSavePostNotify;
        public event Func<int> FileSavePostCancelNotify;
        public event Func<int, int> DestroyNotify2;
        public void Save(bool success)
        {
            if (SaveToStorageNotify != null) SaveToStorageNotify();
            if (success) { if (_writing != null) Bytes = _writing.Bytes; Dirty = false; if (FileSavePostNotify != null) FileSavePostNotify(1, GetPathName()); }
            else if (FileSavePostCancelNotify != null) FileSavePostCancelNotify();
            _writing = null;
        }
        public object IGet3rdPartyStorage(string name, bool storing)
        { if (storing) return FailWrite ? null : (_writing = new BindingTestStream(new byte[0])); return Bytes == null ? null : new BindingTestStream(Bytes); }
        public void IRelease3rdPartyStorage(string name) { Released = true; }
        public void SetSaveFlag() { Dirty = true; }
        public bool Save3(int options, ref int errors, ref int warnings) { throw new NotSupportedException(); }
        int ModelDoc2.GetType() { return 2; }
        public string GetPathName() { return @"C:\models\binding.SLDASM"; }
        public string GetTitle() { return "binding.SLDASM"; }
        public bool GetSaveFlag() { return Dirty; } public int GetUpdateStamp() { return 1; }
        public string get_SummaryInfo(int field) { return "test"; }
        public bool IsOpenedReadOnly() { return false; } public bool Visible { get; set; }
        public ConfigurationManager ConfigurationManager { get { return Config; } }
        public FeatureManager FeatureManager { get { return null; } }
        public ModelDocExtension Extension { get { return null; } }
        public object SelectionManager { get { return null; } }
        public void ClearSelection2(bool all) { } public bool ShowConfiguration2(string name) { return true; }
    }
    public sealed class BindingTestStream : IStream
    {
        private readonly MemoryStream _stream = new MemoryStream();
        public BindingTestStream(byte[] bytes) { _stream.Write(bytes, 0, bytes.Length); _stream.Position = 0; }
        public byte[] Bytes { get { return _stream.ToArray(); } }
        public void Read(byte[] b, int count, IntPtr read) { var n = _stream.Read(b, 0, count); if (read != IntPtr.Zero) Marshal.WriteInt32(read, n); }
        public void Write(byte[] b, int count, IntPtr written) { _stream.Write(b, 0, count); if (written != IntPtr.Zero) Marshal.WriteInt32(written, count); }
        public void Seek(long move, int origin, IntPtr position) { var p = _stream.Seek(move, (SeekOrigin)origin); if (position != IntPtr.Zero) Marshal.WriteInt64(position, p); }
        public void SetSize(long size) { _stream.SetLength(size); }
        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, int flag) { stat = new System.Runtime.InteropServices.ComTypes.STATSTG { cbSize = _stream.Length }; }
        public void Commit(int flags) { } public void Revert() { throw new NotSupportedException(); }
        public void Clone(out IStream s) { s = new BindingTestStream(Bytes); }
        public void CopyTo(IStream s, long count, IntPtr read, IntPtr written) { throw new NotSupportedException(); }
        public void LockRegion(long offset, long count, int type) { throw new NotSupportedException(); }
        public void UnlockRegion(long offset, long count, int type) { throw new NotSupportedException(); }
    }
}
