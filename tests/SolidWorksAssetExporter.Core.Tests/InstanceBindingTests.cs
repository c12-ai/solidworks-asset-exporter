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
            Run("Duplicate stored bindings merge on read and after instance resolution", BindingDuplicateRecords);
            Run("Binding AT-07 invalid batch is atomic and replacement is explicit", BindingAtomicConflict);
            Run("Binding clone migrates a legacy parent with the same stored edge and slot", BindingDuplicateMigration);
            Run("Binding clone reads the typed edit-target guard and reports COM stage and HRESULT", BindingDuplicateEditTarget);
            Run("Binding multiple parents allow diamonds but reject cycles and duplicate edges", BindingMultipleParentGraphs);
            Run("Binding shared child exports a separate relative pose for each parent", BindingMultipleParentPoses);
            Run("Binding relation schema reads legacy data and guards new multi-parent packages", BindingSchemaCompatibility);
            Run("Reusing a rotated slot preserves a single slot and captures child offsets", BindingSharedSlot);
            Run("Wanxiang placement edges identify asset UUIDs and expose final poses by stable slot", BindingWanxiangPlacements);
            Run("Clamp bindings preserve identity and enforce engagement per physical slot", BindingClampSlots);
            Run("Clamp metadata round trips through current storage and package formats", BindingClampRoundTrip);
            Run("Publication uses UUID interfaces and stable names without changing source properties", BindingPublishedAssetProperties);
            Run("Clamp definition fields publish file-level asset metadata without annotating ordinary bindings", BindingPublishedClampDefinition);
            Run("Asset slot and engagement options enforce their shared pair contract", AssetSlotDefinitionOptions);
            Run("Publication recognizes only the canonical contents property", BindingPublishedContentsField);
            Run("Contents cannot bind as a mechanical interface and legacy lists preserve real connections", BindingExcludesContents);
            Run("Binding AT-09 self and long cycles are rejected atomically", BindingCycles);
            Run("Binding AT-10 cross-group multilevel chains are allowed", BindingChains);
            Run("Binding AT-03 unsupported endpoints do not expand Asset boundaries", BindingEndpointKinds);
            Run("Binding AT-11 required interfaces and role chains are revalidated", BindingInterfaces);
            Run("New compatible binding preserves an existing incompatible parent edge", BindingPreservesInterfaceConflicts);
            Run("Pipette installation interface is a Tool alias for the pipette interface", BindingPipetteToolInterface);
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
            Run("Binding graph reuses identical stored references only within one pass", BindingResolverCache);
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
            public void AddLegacy(string parent, string child)
            { Records.Add(new InstanceBinding { Parent = E(parent), Child = E(child) }); }
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
            var f = new BindingFixture(); f.Apply("A", false, "T1"); var id = f.Records[0].Id;
            f.Apply("A", false, "T1", "T2"); Equal(2, f.Records.Count); Equal(id, f.Records[0].Id);
            f.Apply("A", false, "T1"); Equal(2, f.Records.Count); Equal(id, f.Records[0].Id);
            f.Records.RemoveAll(r => r.Child.Reference == "T1"); Equal("T2", f.Export().Relations.Single().ChildNodeId);
        }
        private static void BindingDuplicateRecords()
        {
            var f = new BindingFixture();
            f.Apply("A", false, "T1");
            f.AddLegacy("B", "T1");
            var first = f.Records[0];
            f.Records.Add(new InstanceBinding { Parent = f.E("A"), Child = f.E("T1") });
            var data = new BindingDocument();
            data.Configurations.Add(new ConfigurationBindings { ConfigurationId = 1, Relations = f.Records.ToList() });
            var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(BindingDocument));
            var doc = new BindingTestDocument();
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, data); doc.Bytes = stream.ToArray(); }
            var session = SwInstanceBindings.ForDocument(doc);
            try
            {
                Equal(2, session.Current.Relations.Count);
                Equal(first.Id, session.Current.Relations[0].Id);
                True(!session.Pending);
            }
            finally { session.Dispose(); }

            var alias = f.E("renamed-reference");
            f.Nodes.Add(alias.Reference, f.Nodes["A"]);
            f.Records.Add(new InstanceBinding { Parent = alias, Child = f.E("T1") });
            Equal(2, f.Export().Relations.Count);
            f.Apply("A", false, "T1");
            Equal(2, f.Records.Count);
            Equal(first.Id, f.Records[0].Id);
        }
        private static void BindingAtomicConflict()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            f.Nodes["T2"].Properties[PropertyRules.ConnectionInterface] = "wrong-mount";
            Throws<ValidationException>(() => f.Apply("B", false, "T2", "T1"));
            Equal(1, f.Records.Count); Equal("A", f.Export().Relations.Single().ParentNodeId);
            Throws<ValidationException>(() => f.Apply("B", false, "T1"));
            f.AddLegacy("B", "T1");
            var targetId = f.Records.Single(r => r.Parent.Reference == "B").Id;
            f.Apply("B", true, "T1"); Equal(1, f.Records.Count); Equal("B", f.Export().Relations.Single().ParentNodeId);
            Equal(targetId, f.Records.Single().Id);
        }
        private static void BindingDuplicateMigration()
        {
            var f = new BindingFixture();
            f.Apply("A", false, "T1");
            f.AddLegacy("B", "T1");
            var original = f.E("T1");
            var copy = f.E("T2");
            original.SourcePath = "C:\\models\\part.sldprt";
            copy.SourcePath = "C:\\models\\different.sldprt";
            Throws<ValidationException>(() => InstanceBindingRules.BindDuplicate(f.Records, f.E("B"), original, copy, f.Resolve));
            copy.SourcePath = original.SourcePath;
            f.Records[0].Child.SourcePath = original.SourcePath;
            f.Records[1].Child.SourcePath = original.SourcePath;
            var moved = f.Records[1];
            moved.SlotId = "legacy-slot";
            moved.SlotPose = RelationPose.Identity;
            moved.SlotOffset = RelationPose.Identity;
            var migrated = InstanceBindingRules.BindDuplicate(f.Records, f.E("B"), original, copy, f.Resolve);
            Equal(moved.Id, migrated[1].Id);
            Equal(moved.SlotId, migrated[1].SlotId);
            Equal("T2", migrated[1].Child.Context);
            Equal(f.Records[0], migrated[0]);
            Equal(0, InstanceBindingRules.MultipleParentBindingIds(migrated).Count);

            var old = new BindingDocument { SchemaVersion = BindingDocument.SharedSlotSchemaVersion };
            old.Configurations.Add(new ConfigurationBindings { ConfigurationId = 1, Relations = f.Records.ToList() });
            var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(BindingDocument));
            var document = new BindingTestDocument();
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, old); document.Bytes = stream.ToArray(); }
            using (var session = SwInstanceBindings.ForDocument(document))
            {
                Equal(2, InstanceBindingRules.MultipleParentBindingIds(session.Current.Relations).Count);
                Throws<ValidationException>(() => session.Commit(f.Records.Concat(new[] { new InstanceBinding { Parent = f.E("T3"), Child = original } }).ToList()));
                session.Commit(migrated);
                True(session.Pending);
                session.Save(true);
            }
            using (var reopened = SwInstanceBindings.ForDocument(new BindingTestDocument { Bytes = document.Bytes }))
            {
                Equal("T2", reopened.Current.Relations.Single(record => record.Id == moved.Id).Child.Context);
                Equal(0, InstanceBindingRules.MultipleParentBindingIds(reopened.Current.Relations).Count);
            }
        }
        private static void BindingDuplicateEditTarget()
        {
            var document = new BindingTestDocument { EditingSelf = false };
            var app = new SldWorks { ActiveDoc = document };
            using (var session = SwInstanceBindings.ForDocument(document))
            {
                Throws<ValidationException>(() => session.PrepareDuplicate(app, null, null, null));
                document.EditTargetError = new COMException("无效索引。", unchecked((int)0x8002000B));
                try
                {
                    session.PrepareDuplicate(app, null, null, null);
                    throw new Exception("Expected duplicate preflight COM error.");
                }
                catch (ValidationException error)
                {
                    True(error.Message.Contains("核对总装编辑目标"));
                    True(error.Message.Contains("0x8002000B"));
                }
                True(!session.Pending && !document.Dirty);
            }
        }
        private static void BindingMultipleParentGraphs()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1", "T2");
            f.Apply("T1", false, "T3"); f.AddLegacy("T2", "T3"); f.AddLegacy("B", "T3");
            Equal(5, f.Export().Relations.Count);
            f.Apply("T1", false, "T3", "T3"); Equal(5, f.Records.Count);
            Throws<ValidationException>(() => f.Apply("T3", false, "A")); Equal(5, f.Records.Count);
            Throws<ValidationException>(() => f.Apply("T3", false, "B")); Equal(5, f.Records.Count);
            var removed = f.Records.Single(r => r.Parent.Reference == "T1" && r.Child.Reference == "T3");
            f.Records.Remove(removed); Equal(4, f.Export().Relations.Count);
            Equal(2, f.Export().Relations.Count(r => r.ChildNodeId == "T3"));
            var malformed = f.Export();
            malformed.Relations.Add(malformed.Relations.First());
            Throws<ValidationException>(() => malformed.Validate(BindingXml(f), f.Plan.AssemblyUuid, f.Plan.AssemblyVersion));
        }
        private static void BindingMultipleParentPoses()
        {
            var f = new BindingFixture();
            ((FakeNode)f.Nodes["A"].Source).WorldTransformValue = BindingTransform(1, 2, 0, Math.PI / 2);
            ((FakeNode)f.Nodes["B"].Source).WorldTransformValue = BindingTransform(-2, .5, 1, 0);
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = BindingTransform(.8, 2.1, .3, Math.PI / 2);
            f.Apply("A", false, "T1"); f.AddLegacy("B", "T1");
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
        private static void BindingSharedSlot()
        {
            var f = new BindingFixture();
            var parent = BindingTransform(1, 2, 0, Math.PI / 2);
            var slot = BindingTransform(.1, .2, 0, Math.PI / 2);
            var offset = BindingTransform(.03, .04, .05, -Math.PI / 2);
            ((FakeNode)f.Nodes["A"].Source).WorldTransformValue = parent;
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = parent.Multiply(slot);
            ((FakeNode)f.Nodes["T2"].Source).WorldTransformValue = parent.Multiply(slot).Multiply(offset);
            f.Apply("A", false, "T1");
            var anchor = f.Records.Single();
            Equal("1.1", f.Export().SchemaVersion);
            Throws<ValidationException>(() => InstanceBindingRules.Apply(f.Records, f.E("B"), new[] { f.E("T2") }, f.Resolve, false, anchor.Id));
            var proposed = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T2") }, f.Resolve, false, anchor.Id);
            True(anchor.SlotId == null);
            f.Records.Clear(); f.Records.AddRange(proposed);
            var export = f.Export();
            Equal("1.2", export.SchemaVersion);
            var shared = export.Slots.Single();
            Equal(anchor.Id, shared.Id); Equal("A", shared.ParentNodeId);
            Near(.1, shared.Pose.Tx); Near(.2, shared.Pose.Ty); Near(Math.Sqrt(.5), shared.Pose.Qz);
            var original = export.Relations.Single(r => r.ChildNodeId == "T1");
            Near(0, original.SlotOffset.Tx); Near(1, original.SlotOffset.Qw);
            var replacement = export.Relations.Single(r => r.ChildNodeId == "T2");
            Equal(anchor.Id, replacement.SlotId);
            Near(.03, replacement.SlotOffset.Tx); Near(.04, replacement.SlotOffset.Ty); Near(.05, replacement.SlotOffset.Tz);
            Near(-Math.Sqrt(.5), replacement.SlotOffset.Qz);
            Near(.06, replacement.Pose.Tx); Near(.23, replacement.Pose.Ty);

            var retry = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T2") }, f.Resolve, false, anchor.Id);
            True(f.Records.Select(r => r.Id).SequenceEqual(retry.Select(r => r.Id)));
            var temp = TempDirectory();
            try
            {
                BindingWritePackage(temp, f);
                var read = JsonFile.Read<AssemblyRelations>(Path.Combine(temp, AssemblyRelations.FileName));
                Equal(export.FingerprintMaterial(), read.FingerprintMaterial());
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                read.Slots[0].ParentNodeId = "B";
                Throws<ValidationException>(() => read.Validate(BindingXml(f), f.Plan.AssemblyUuid, 2));
            }
            finally { Directory.Delete(temp, true); }

            f.Records.RemoveAll(r => r.Id == anchor.Id);
            var remaining = f.Records.Single();
            ((FakeNode)f.Nodes["T3"].Source).WorldTransformValue = parent.Multiply(slot);
            var withThird = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T3") }, f.Resolve, false, remaining.Id);
            var afterRemoval = InstanceBindingRules.Resolve(withThird, f.Resolve, f.Plan);
            Equal(anchor.Id, afterRemoval.Slots.Single().Id);
            Equal(shared.Pose.CanonicalValue(), afterRemoval.Slots.Single().Pose.CanonicalValue());
            var aligned = afterRemoval.Relations.Single(r => r.ChildNodeId == "T3").SlotOffset;
            Near(0, aligned.Tx); Near(0, aligned.Ty); Near(0, aligned.Qz); Near(1, aligned.Qw);
        }
        private static void BindingWanxiangPlacements()
        {
            var f = new BindingFixture();
            foreach (var node in f.Nodes.Values)
            {
                node.GeometryUuid = Guid.NewGuid().ToString("D");
                node.AssetId = node.GeometryUuid + ":1";
            }
            ((FakeNode)f.Nodes["T1"].Source).WorldTransformValue = BindingTransform(.1, .2, 0, Math.PI / 2);
            ((FakeNode)f.Nodes["T2"].Source).WorldTransformValue = BindingTransform(.03, .5, .1, 0);
            f.Apply("A", false, "T1", "T3");
            var anchor = f.Records[0];
            var independent = f.Records[1];
            var proposed = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T2") }, f.Resolve, false, anchor.Id);
            f.Records.Clear(); f.Records.AddRange(proposed);
            ((FakeNode)f.Nodes["T2"].Source).WorldTransformValue = BindingTransform(3, 2, 1, 0);
            var relations = f.Export();
            relations.IncludeAssetPlacements(f.Nodes.Values);
            Equal("1.3", relations.SchemaVersion);
            Equal(2, relations.Relations.Select(edge => edge.Placement.SlotKey).Distinct().Count());
            Equal(independent.Id, relations.Relations.Single(edge => edge.ChildNodeId == "T3").Placement.SlotKey);
            var candidate = relations.Relations.Single(edge => edge.ChildNodeId == "T2");
            Equal(anchor.Id, candidate.Placement.SlotKey);
            Equal(f.Nodes["T2"].GeometryUuid, candidate.Placement.ChildAssetUuid);
            Equal(f.Nodes["A"].GeometryUuid, candidate.Placement.ParentAssetUuid);
            Near(.03, candidate.Placement.Pose.Tx); Near(.5, candidate.Placement.Pose.Ty); Near(.1, candidate.Placement.Pose.Tz);
            Near(3, candidate.Pose.Tx);
            var temp = TempDirectory();
            try
            {
                var xml = Path.Combine(temp, "assembly_" + f.Plan.AssemblyUuid + "_v2.xml");
                var json = Path.Combine(temp, AssemblyRelations.FileName);
                AssemblyXmlWriter.Write(xml, f.Plan);
                JsonFile.Write(json, relations);
                JsonFile.Write(Path.Combine(temp, "export-report.json"), new ExportReport
                {
                    SchemaVersion = "1.0", AssemblyUuid = f.Plan.AssemblyUuid, AssemblyVersion = 2,
                    RelationsSchemaVersion = relations.SchemaVersion, ContentFingerprint = "binding-fingerprint",
                    Files = AssetManifestValidator.DescribeFiles(temp, new[] { Path.GetFileName(xml), AssemblyRelations.FileName })
                });
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                var restored = JsonFile.Read<AssemblyRelations>(json);
                Equal(relations.FingerprintMaterial(), restored.FingerprintMaterial());
                restored.Relations[0].Placement.ChildAssetUuid = f.Nodes["B"].GeometryUuid;
                Throws<ValidationException>(() => restored.Validate(XDocument.Load(xml), f.Plan.AssemblyUuid, 2));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void BindingPublishedAssetProperties()
        {
            var owner = "11111111-1111-4111-8111-111111111111";
            var parent = "22222222-2222-4222-8222-222222222222";
            var child = "33333333-3333-4333-8333-333333333333";
            var declared = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            var requests = new Dictionary<string, string> { { owner, "Tool" }, { parent, "tool" } };
            var known = new[] { new AssetRegistration { Uuid = owner, Name = "Tool", Version = 2 } };
            var names = AssetRegistryStore.AllocateNames(requests, known);
            Equal("Tool", names[owner]);
            Equal("tool [" + parent + "]", names[parent]);
            var unbound = Node("Unbound", true, false);
            unbound.Model.FileProperties[PropertyRules.PartName] = "tool";
            var unboundNode = new ExportNode { GeometryUuid = parent, Source = unbound };
            Equal(names[parent], ExportCoordinator.ResolveAssetExportNames(new[] { unboundNode }, known)[parent]);
            var previouslySuffixed = new[] { new AssetRegistration { Uuid = parent, Name = names[parent], Version = 1 } };
            Equal(names[parent], AssetRegistryStore.AllocateNames(requests, previouslySuffixed)[parent]);
            var source = new Dictionary<string, string>
            {
                { PropertyRules.PartName, "tool" }, { PropertyRules.ConnectionInterface, "机械接口" },
                { PropertyRules.AcceptsInterfaces, "机械接口;" + declared.ToUpperInvariant() }
            };
            var published = ExportCoordinator.PublishedAssetProperties(source, parent, names[parent], new[] { child, child });
            Equal(names[parent], published[PropertyRules.PartName]);
            Equal(parent, published[PropertyRules.ConnectionInterface]);
            Equal(child + ";" + declared, published[PropertyRules.AcceptsInterfaces]);
            Equal("机械接口", source[PropertyRules.ConnectionInterface]);
        }

        private static void AssetSlotDefinitionOptions()
        {
            True(PropertyRules.AssetSlotTypes.SequenceEqual(new[] { "place", "mount", "clamp", "thread", "press_fit", "snap_fit" }));
            True(PropertyRules.AssetEngagementValues.SequenceEqual(new[] { "loose", "tight" }));
            var properties = new Dictionary<string, string>();
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            properties[PropertyRules.SlotsNum] = "1";
            foreach (var slotType in PropertyRules.AssetSlotTypes)
                foreach (var engagement in PropertyRules.AssetEngagementValues)
                {
                    properties[PropertyRules.SlotType] = slotType;
                    properties[PropertyRules.AssetEngagement] = engagement;
                    Equal(slotType == "place" && engagement == "tight" ? 1 : 0,
                        PropertyRules.ValidateAssetConnectionProperties(properties).Count);
                }
            properties[PropertyRules.SlotType] = "support";
            properties[PropertyRules.AssetEngagement] = "loose";
            Equal(1, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            properties.Remove(PropertyRules.SlotType);
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            properties[PropertyRules.SlotType] = "place";
            properties[PropertyRules.AssetEngagement] = "tight";
            foreach (var capacity in new[] { "0", "", "-1" })
            {
                properties[PropertyRules.SlotsNum] = capacity;
                Equal(string.Empty, PropertyRules.ReadAssetSlotType(properties));
                Equal(capacity == "-1" ? 1 : 0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            }
            properties.Remove(PropertyRules.SlotsNum);
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);
            properties[PropertyRules.AssetEngagement] = "closed";
            True(PropertyRules.ValidateAssetConnectionProperties(properties).Single().Contains("engagement"));
        }

        private static void BindingPublishedClampDefinition()
        {
            var fixture = new BindingFixture();
            var parent = fixture.Nodes["A"];
            parent.GeometryUuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            fixture.Nodes["T1"].GeometryUuid = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
            var model = parent.Source.Model;
            model.ConfigurationProperties["slots_num"] = "2";
            model.ConfigurationProperties["slot_type"] = "clamp";
            model.ConfigurationProperties["engagement"] = "loose";
            var plain = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            True(!plain.ContainsKey("slot_type") && !plain.ContainsKey("engagement"));
            var plainFingerprint = ExportCoordinator.AssetContentFingerprint("same model", new string[0], plain, false);

            model.FileProperties["slot_type"] = " ";
            model.FileProperties["engagement"] = " ";
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(parent.Properties).Count);
            var blank = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            Equal(plainFingerprint, ExportCoordinator.AssetContentFingerprint("same model", new string[0], blank, false));
            model.FileProperties["slots_num"] = "1";
            var noType = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            True(!noType.ContainsKey("slot_type"));
            model.FileProperties["slot_type"] = " clamp ";
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(parent.Properties).Count);
            model.FileProperties["engagement"] = "closed";
            Equal(1, PropertyRules.ValidateAssetConnectionProperties(parent.Properties).Count);
            model.FileProperties["engagement"] = " tight ";
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(parent.Properties).Count);
            var tight = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            Equal("clamp", tight["slot_type"]);
            Equal("tight", tight["engagement"]);
            Equal(" tight ", model.FileProperties["engagement"]);
            var tightFingerprint = ExportCoordinator.AssetContentFingerprint("same model", new string[0], tight, false);
            True(plainFingerprint != tightFingerprint);

            model.FileProperties["engagement"] = "loose";
            var loose = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            True(tightFingerprint != ExportCoordinator.AssetContentFingerprint("same model", new string[0], loose, false));
            True(ExportCoordinator.RegisteredPropertyDifferences(tight, loose).Single().StartsWith("engagement"));
            model.FileProperties["slots_num"] = "0";
            model.FileProperties["engagement"] = "tight";
            var zero = ExportCoordinator.PublishedAssetProperties(PropertyRules.Merge(model), parent.GeometryUuid, "夹持块", new string[0]);
            True(!zero.ContainsKey("slot_type"));
            Equal("tight", zero["engagement"]);
            var stale = new Dictionary<string, string>(zero) { { "slot_type", "mount" } };
            Equal(ExportCoordinator.AssetContentFingerprint("same model", new string[0], zero, false),
                ExportCoordinator.AssetContentFingerprint("same model", new string[0], stale, false));
            var manifestPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            try
            {
                JsonFile.Write(manifestPath, new AssetManifest { Properties = stale });
                var written = JsonFile.Read<AssetManifest>(manifestPath);
                True(!written.Properties.ContainsKey("slot_type"));
                Equal("tight", written.Properties["engagement"]);
                Equal("mount", stale["slot_type"]);
                File.WriteAllText(manifestPath, "{\"properties\":{\"slots_num\":\"0\",\"slot_type\":\"mount\"}}");
                Equal("mount", JsonFile.Read<AssetManifest>(manifestPath).Properties["slot_type"]);
            }
            finally { File.Delete(manifestPath); }
            fixture.Apply("A", false, "T1");
            var relations = fixture.Export();
            relations.IncludeAssetPlacements(fixture.Nodes.Values);
            Equal("1.3", relations.SchemaVersion);
            var edge = relations.Relations.Single();
            True(edge.Placement.SlotType == null && edge.Engagement == null);
        }

        private static void BindingPublishedContentsField()
        {
            const string uuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            var source = new Dictionary<string, string> { { PropertyRules.LegacyCanContain, " YES " } };
            True(!PropertyRules.ReadCanContain(source));
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(source).Count);
            var published = ExportCoordinator.PublishedAssetProperties(source, uuid, "容器", new string[0]);
            True(!PropertyRules.ReadCanContain(published));
            True(!published.ContainsKey("can_hold_contents"));
            True(!published.ContainsKey("can_contain"));
            Equal(" YES ", source["can_contain"]);

            var registered = new Dictionary<string, string>(published) { { PropertyRules.LegacyCanContain, "yes" } };
            Equal(0, ExportCoordinator.RegisteredPropertyDifferences(registered, published).Count);
            Equal(ExportCoordinator.AssetContentFingerprint("same model", new string[0], registered, PropertyRules.ReadCanContain(registered)),
                ExportCoordinator.AssetContentFingerprint("same model", new string[0], published, PropertyRules.ReadCanContain(published)));

            source[PropertyRules.CanContain] = " ";
            True(!PropertyRules.ReadCanContain(source));
            source[PropertyRules.CanContain] = "true";
            var enabled = ExportCoordinator.PublishedAssetProperties(source, uuid, "容器", new string[0]);
            Equal("1", enabled[PropertyRules.CanContain]);
            True(!enabled.ContainsKey(PropertyRules.LegacyCanContain));
            True(ExportCoordinator.RegisteredPropertyDifferences(registered, enabled).Single().StartsWith(PropertyRules.CanContain));
            True(ExportCoordinator.AssetContentFingerprint("same model", new string[0], registered, PropertyRules.ReadCanContain(registered)) !=
                ExportCoordinator.AssetContentFingerprint("same model", new string[0], enabled, PropertyRules.ReadCanContain(enabled)));

            source[PropertyRules.CanContain] = "unrecognized";
            True(PropertyRules.ValidateAssetConnectionProperties(source).Single().Contains("[can_hold_contents]"));
            Throws<ValidationException>(() => PropertyRules.ReadCanContain(source));
            source.Remove(PropertyRules.CanContain);
            source[PropertyRules.LegacyCanContain] = "unrecognized";
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(source).Count);
            True(!PropertyRules.ReadCanContain(source));
        }

        private static void BindingClampSlots()
        {
            var f = new BindingFixture();
            foreach (var node in f.Nodes.Values)
            {
                node.GeometryUuid = Guid.NewGuid().ToString("D");
                node.AssetId = node.GeometryUuid + ":1";
            }
            f.Apply("A", false, "T1");
            var original = f.Records.Single();
            var parent = f.E("A");
            var first = new[] { f.E("T1") };
            var alternate = new[] { f.E("T2") };
            Throws<ValidationException>(() => InstanceBindingRules.Apply(f.Records, parent, first, f.Resolve, false, null, "clamp"));
            Throws<ValidationException>(() => InstanceBindingRules.Apply(f.Records, parent, first, f.Resolve, false, null, null, "tight"));
            Throws<ValidationException>(() => InstanceBindingRules.Apply(f.Records, parent, first, f.Resolve, false, null, "hinge", "tight"));
            Throws<ValidationException>(() => InstanceBindingRules.Apply(f.Records, parent, first, f.Resolve, false, null, "clamp", "closed"));
            var clamped = InstanceBindingRules.Apply(f.Records, parent, first, f.Resolve, false, null, "clamp", "tight");
            Equal(original.Id, clamped.Single().Id);
            True(original.SlotType == null && original.Engagement == null);
            var implicitSlot = InstanceBindingRules.Resolve(clamped, f.Resolve, f.Plan);
            Equal("1.4", implicitSlot.SchemaVersion);
            True(implicitSlot.Slots == null);
            Equal(original.Id, implicitSlot.Relations.Single().Placement.SlotKey);
            Equal("clamp", implicitSlot.Relations.Single().Placement.SlotType);
            Equal("tight", implicitSlot.Relations.Single().Engagement);
            True(ReferenceEquals(clamped.Single(), InstanceBindingRules.Apply(clamped, parent, first, f.Resolve, false).Single()));

            Throws<ValidationException>(() => InstanceBindingRules.Apply(clamped, parent, alternate, f.Resolve, false, original.Id));
            Throws<ValidationException>(() => InstanceBindingRules.Apply(clamped, parent, alternate, f.Resolve, false, original.Id, "clamp", "tight"));
            True(clamped.Single().SlotId == null);
            var shared = InstanceBindingRules.Apply(clamped, parent, alternate, f.Resolve, false, original.Id, "clamp", "loose");
            True(shared.All(record => record.SlotId == original.Id && record.SlotType == "clamp"));
            Equal("tight", shared[0].Engagement);
            Equal("loose", shared[1].Engagement);
            Throws<ValidationException>(() => InstanceBindingRules.Apply(shared, parent, alternate, f.Resolve, false, null, "clamp", "tight"));

            var updated = InstanceBindingRules.Apply(shared, parent, first, f.Resolve, false, original.Id, "clamp", "loose");
            Equal(shared[0].Id, updated[0].Id);
            Equal(shared[0].SlotId, updated[0].SlotId);
            True(ReferenceEquals(shared[0].SlotPose, updated[0].SlotPose));
            True(ReferenceEquals(shared[0].SlotOffset, updated[0].SlotOffset));
            Equal("tight", shared[0].Engagement);
            var published = InstanceBindingRules.Resolve(updated, f.Resolve, f.Plan);
            Equal("1.4", published.SchemaVersion);
            True(published.Relations.All(relation => relation.Placement.SlotKey == original.Id));
            True(published.FingerprintMaterial() != InstanceBindingRules.Resolve(shared, f.Resolve, f.Plan).FingerprintMaterial());

            var legacyOtherParent = new InstanceBinding { Parent = f.E("B"), Child = first[0] };
            var migrated = InstanceBindingRules.BindDuplicate(shared.Concat(new[] { legacyOtherParent }), parent, first[0], f.E("T3"), f.Resolve);
            var moved = migrated.Single(record => record.Id == original.Id);
            Equal("T3", moved.Child.Reference);
            Equal("clamp", moved.SlotType);
            Equal("tight", moved.Engagement);
            Equal(shared[0].SlotId, moved.SlotId);
            True(ReferenceEquals(shared[0].SlotOffset, moved.SlotOffset));
        }

        private static void BindingClampRoundTrip()
        {
            var f = new BindingFixture();
            foreach (var node in f.Nodes.Values)
            {
                node.GeometryUuid = Guid.NewGuid().ToString("D");
                node.AssetId = node.GeometryUuid + ":1";
            }
            var parent = f.E("A");
            var anchor = InstanceBindingRules.Apply(f.Records, parent, new[] { f.E("T1") }, f.Resolve, false, null, "clamp", "tight");
            f.Records.AddRange(InstanceBindingRules.Apply(anchor, parent, new[] { f.E("T2") }, f.Resolve, false, anchor[0].Id, "clamp", "loose"));
            f.Apply("B", false, "T3");
            var data = new BindingDocument { SchemaVersion = BindingDocument.CouplingSchemaVersion };
            data.Configurations.Add(new ConfigurationBindings { ConfigurationId = 1, ConfigurationName = "Default", Relations = f.Records });
            var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(BindingDocument));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, data);
                stream.Position = 0;
                var restored = (BindingDocument)serializer.ReadObject(stream);
                var saved = restored.ForConfiguration(1, "Default").Relations;
                Equal("clamp", saved[0].SlotType);
                Equal("tight", saved[0].Engagement);
                Equal("loose", saved[1].Engagement);
                True(saved[2].SlotType == null && saved[2].Engagement == null);
                restored.SchemaVersion = BindingDocument.SharedSlotSchemaVersion;
                Throws<ValidationException>(() => restored.ForConfiguration(1, "Default"));
                restored.SchemaVersion = BindingDocument.CouplingSchemaVersion;
                saved[1].Engagement = "tight";
                Throws<ValidationException>(() => restored.ForConfiguration(1, "Default"));
            }

            var temp = TempDirectory();
            try
            {
                BindingWritePackage(temp, f);
                var xml = XDocument.Load(Path.Combine(temp, "assembly_" + f.Plan.AssemblyUuid + "_v2.xml"));
                var restored = JsonFile.Read<AssemblyRelations>(Path.Combine(temp, AssemblyRelations.FileName));
                restored.Validate(xml, f.Plan.AssemblyUuid, 2);
                Equal(ExistingProjectState.Reusable, ProjectReportValidator.Inspect(temp, f.Plan.AssemblyUuid, 2, "binding-fingerprint"));
                Equal(f.Plan.Relations.FingerprintMaterial(), restored.FingerprintMaterial());
                restored.IncludeAssetPlacements(f.Nodes.Values);
                Equal(f.Plan.Relations.FingerprintMaterial(), restored.FingerprintMaterial());
                var alternate = restored.Relations.Single(relation => relation.ChildNodeId == "T2");
                alternate.Engagement = "tight";
                Throws<ValidationException>(() => restored.Validate(xml, f.Plan.AssemblyUuid, 2));
                alternate.Engagement = null;
                Throws<ValidationException>(() => restored.Validate(xml, f.Plan.AssemblyUuid, 2));
                alternate.Placement.SlotType = null;
                Throws<ValidationException>(() => restored.Validate(xml, f.Plan.AssemblyUuid, 2));
                alternate.Placement.SlotType = "clamp";
                alternate.Engagement = "loose";
                restored.SchemaVersion = AssemblyRelations.PlacementSchemaVersion;
                Throws<ValidationException>(() => restored.Validate(xml, f.Plan.AssemblyUuid, 2));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void BindingExcludesContents()
        {
            var fixture = new BindingFixture();
            var parent = fixture.Nodes["A"];
            var child = fixture.Nodes["T1"];
            parent.Properties[PropertyRules.AcceptsInterfaces] = "细胞样本;样本.细胞;125ml锥形瓶盖;试剂瓶.50ml";
            child.Properties[PropertyRules.ConnectionInterface] = "125ml锥形瓶盖";
            InstanceBindingRules.ValidateConnection(parent, child);
            child.Properties[PropertyRules.ConnectionInterface] = "样本.细胞";
            Throws<ValidationException>(() => InstanceBindingRules.ValidateConnection(parent, child));

            var uuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            parent.Properties[PropertyRules.AcceptsInterfaces] += ";" + uuid;
            parent.Properties[PropertyRules.LegacyAcceptsInterface] = "溶液;125ml锥形瓶盖;内容物.培养基";
            var published = ExportCoordinator.PublishedAssetProperties(parent.Properties, uuid, "容器", new string[0]);
            Equal(uuid, published[PropertyRules.AcceptsInterfaces]);
            Equal("125ml锥形瓶盖", published[PropertyRules.LegacyAcceptsInterface]);
            Equal("溶液;125ml锥形瓶盖;内容物.培养基", parent.Properties[PropertyRules.LegacyAcceptsInterface]);
            parent.Properties[PropertyRules.LegacyAcceptsInterface] = "细胞样本;溶液";
            published = ExportCoordinator.PublishedAssetProperties(parent.Properties, uuid, "容器", new string[0]);
            True(!published.ContainsKey(PropertyRules.LegacyAcceptsInterface));
        }

        private static void BindingSchemaCompatibility()
        {
            var f = new BindingFixture(); f.Apply("A", false, "T1");
            var legacy = f.Export(); legacy.SchemaVersion = "1.0"; legacy.Validate(BindingXml(f), f.Plan.AssemblyUuid, 2);
            f.AddLegacy("B", "T1"); var multiple = f.Export(); multiple.SchemaVersion = "1.0";
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
                Equal(1, session.Current.Relations.Count); f.Apply("B", false, "T2"); session.Commit(f.Records.ToList()); document.Save(true);
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
            Throws<ValidationException>(() => f.Export());
            var retained = f.Records.Single();
            f.Apply("A", false, "T2", "T1");
            Equal(2, f.Records.Count); Equal(retained, f.Records[0]);
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = ""; Throws<ValidationException>(() => f.Export());
            f = new BindingFixture(); f.Nodes["A"].Properties[PropertyRules.IsQuickChangerRack] = "1";
            f.Nodes["A"].Properties[PropertyRules.AssetClass] = "station";
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));
            f.Nodes["T1"].Properties[PropertyRules.IsQuickChanger] = "1";
            f.Nodes["T1"].Properties[PropertyRules.QuickChangerSide] = "tool_side";
            f.Apply("A", false, "T1"); Equal(1, f.Export().Relations.Count);
        }

        private static void BindingPreservesInterfaceConflicts()
        {
            var f = new BindingFixture();
            f.Apply("A", false, "T1");
            var retained = f.Records.Single();
            f.Nodes["T1"].Properties[PropertyRules.ConnectionInterface] = "alternate";
            f.Nodes["B"].Properties[PropertyRules.AcceptsInterfaces] = "alternate";
            f.Nodes["T2"].Properties[PropertyRules.ConnectionInterface] = "alternate";
            var duplicated = InstanceBindingRules.BindDuplicate(f.Records, f.E("B"), f.E("T1"), f.E("T2"), f.Resolve);
            f.Records.Add(duplicated[1]);
            Equal(2, f.Records.Count);
            Equal(retained, f.Records[0]);
            Equal("B", f.Records[1].Parent.Reference);
            Throws<ValidationException>(() => f.Apply("T1", false, "A"));
            Throws<ValidationException>(() => f.Apply("B", false, "T3"));
            Throws<ValidationException>(() => f.Apply("A", true, "T2"));
            Throws<ValidationException>(() => f.Export());
            Equal(2, f.Records.Count);
        }

        private static void BindingPipetteToolInterface()
        {
            var f = new BindingFixture();
            var parent = f.Nodes["A"].Properties;
            var child = f.Nodes["T1"].Properties;
            parent[PropertyRules.AcceptsInterfaces] = "移液器;快换盘.B型";
            child[PropertyRules.ConnectionInterface] = "工具安装.移液器";
            Throws<ValidationException>(() => f.Apply("A", false, "T1"));

            child[PropertyRules.IsTool] = "1";
            f.Apply("A", false, "T1");
            Equal(1, f.Export().Relations.Count);

            parent[PropertyRules.AcceptsInterfaces] = "工具安装.移液器";
            child[PropertyRules.ConnectionInterface] = "移液器";
            Equal(1, f.Export().Relations.Count);
            child[PropertyRules.ConnectionInterface] = "工具安装.电动助吸器";
            Throws<ValidationException>(() => f.Export());
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
            Throws<ValidationException>(() => f.Apply("B", false, "T1"));
            f.Apply("A", false, "T1"); Equal(1, f.Export().Relations.Count);
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
                SchemaVersion = "1.0", AssemblyUuid = f.Plan.AssemblyUuid, AssemblyVersion = 2, RelationsSchemaVersion = f.Plan.Relations.SchemaVersion,
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
                var f = new BindingFixture(); f.Apply("A", false, "T1", "T2", "T3"); f.Apply("B", false, "A"); BindingWritePackage(temp, f);
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
                var f = new BindingFixture(); f.Apply("A", false, "T1"); f.Apply("B", false, "T2");
                s.Commit(f.Records.ToList()); True(s.Pending); True(d.Dirty);
                d.Save(false); True(s.Pending); d.FailWrite = true; d.Save(true); True(s.Pending); True(s.LastSaveError != null);
                d.FailWrite = false; s.Save(true); Equal(1, d.SaveOptions); True(!s.Pending); True(s.LastSaveError == null);
                var bytes = d.Bytes; s.Dispose();
                var reopened = new BindingTestDocument { Bytes = bytes }; var next = SwInstanceBindings.ForDocument(reopened);
                try { Equal(2, next.Current.Relations.Count); Equal(2, next.Current.Relations.Select(r => r.Parent.Context).Distinct().Count()); Equal(2, next.Current.Relations.Select(r => r.Child.Context).Distinct().Count()); True(!next.Pending); }
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
        private static void BindingResolverCache()
        {
            var extension = new FakeModelDocExtension();
            var document = new BindingTestDocument { ReferenceExtension = extension };
            using (var session = SwInstanceBindings.ForDocument(document))
            {
                var plan = new AssemblyExportPlan();
                var resolve = session.CreateResolver(plan);
                var reference = Convert.ToBase64String(new byte[] { 1 });
                var first = new BindingEndpoint { Reference = reference, Context = "1" };
                var copy = new BindingEndpoint { Reference = reference, Context = "1" };
                var other = new BindingEndpoint { Reference = reference, Context = "2" };
                Throws<ValidationException>(() => resolve(first));
                Throws<ValidationException>(() => resolve(copy));
                Equal(1, extension.PersistReferenceReads);
                Throws<ValidationException>(() => resolve(other));
                Equal(2, extension.PersistReferenceReads);
                Throws<ValidationException>(() => session.CreateResolver(plan)(first));
                Equal(3, extension.PersistReferenceReads);
            }
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
        public Component2 RootComponent { get; set; }
        public Component2 GetRootComponent3(bool resolve) { return RootComponent; }
        public object GetDisplayStates() { return null; }
        public bool GetComponentSuppressionState(string componentName) { throw new NotSupportedException(); }
    }
    public sealed class BindingTestDocument : AssemblyDoc, DAssemblyDocEvents_Event
    {
        public event Func<int> NewSelectionNotify { add { } remove { } }
        public event Func<int> ClearSelectionsNotify { add { } remove { } }
        public event Func<int> UserSelectionPostNotify { add { } remove { } }
        public readonly BindingTestConfiguration Config = new BindingTestConfiguration { Name = "A" };
        public bool Dirty, FailWrite, Released; public byte[] Bytes; private BindingTestStream _writing;
        public int SaveOptions;
        public ModelDocExtension ReferenceExtension;
        public string FilePath { get; set; }
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
        public bool Save3(int options, ref int errors, ref int warnings) { SaveOptions = options; Save(true); return true; }
        int ModelDoc2.GetType() { return 2; }
        public string GetPathName() { return FilePath ?? @"C:\models\binding.SLDASM"; }
        public string GetTitle() { return Path.GetFileName(GetPathName()); }
        public bool GetSaveFlag() { return Dirty; } public int GetUpdateStamp() { return 1; }
        public string get_SummaryInfo(int field) { return "test"; }
        public bool IsOpenedReadOnly() { return false; } public bool Visible { get; set; }
        public ConfigurationManager ConfigurationManager { get { return Config; } }
        public ModelView IActiveView { get { return null; } }
        public FeatureManager FeatureManager { get { return null; } }
        public ModelDocExtension Extension { get { return ReferenceExtension; } }
        public object SelectionManager { get { return null; } }
        public double ClosestDistance(object first, object second, out object point1, out object point2)
        { point1 = point2 = null; return -1; }
        public void ClearSelection2(bool all) { } public bool ShowConfiguration2(string name) { return true; }
        public void GraphicsRedraw2() { }
        public object GetConfigurationNames() { return new[] { Config.Name }; }
        public object GetConfigurationByName(string name) { return Config.Name == name ? Config : null; }
        public bool EditRebuild3() { throw new NotSupportedException(); }
        public bool EditingSelf = true;
        public COMException EditTargetError;
        public bool IsEditingSelf()
        { if (EditTargetError != null) throw EditTargetError; return EditingSelf; }
        public Component2 AddComponent5(string path, int configOption, string newConfigName, bool useExistingConfig,
            string existingConfigName, double x, double y, double z) { throw new NotSupportedException(); }
        public bool SetComponentState(int suppression, object components, int configurationOption, string configuration, bool saveClosedDocuments)
        { throw new NotSupportedException(); }
        public object GetComponents(bool topLevelOnly)
        {
            return Config.RootComponent == null ? null : Config.RootComponent.GetChildren();
        }
        public int GetLightWeightComponentCount() { return 0; }
        public int ResolveAllLightWeightComponents(bool warnUser) { throw new NotSupportedException(); }
        public bool Isolate() { throw new NotSupportedException(); }
        public bool ExitIsolate() { throw new NotSupportedException(); }
        public void SetIsolateVisibility(int option) { throw new NotSupportedException(); }
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
