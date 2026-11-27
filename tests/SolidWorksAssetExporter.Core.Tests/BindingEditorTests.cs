using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunBindingEditorTests()
        {
            Run("Picking nested geometry finds the nearest bound instance and its direct relations", delegate
            {
                var parentA = new BindingEndpoint { Context = "1/4" };
                var parentB = new BindingEndpoint { Context = "2/4" };
                var picked = new BindingEndpoint { Context = "3/4" };
                var child = new BindingEndpoint { Context = "3/5" };
                var first = new InstanceBinding { Parent = parentA, Child = picked };
                var second = new InstanceBinding { Parent = parentB, Child = picked };
                var third = new InstanceBinding { Parent = picked, Child = child };
                var indirect = new InstanceBinding { Parent = child, Child = new BindingEndpoint { Context = "3/6" } };
                var index = BindingEditorState.IndexRelatedInstances(new[] { first, second, third, indirect });
                string matched;
                var found = BindingEditorState.RelationsForPick(index, "3/4/8/9", out matched);
                Equal("3/4", matched);
                True(found.SequenceEqual(new[] { first, second, third }));
                Equal(0, BindingEditorState.RelationsForPick(index, "3/40", out matched).Count);
            });
            Run("Binding picks distinguish plan occurrences with duplicate component IDs", delegate
            {
                var firstComponent = new FakeSwComponent { NameValue = "Rack-1/Holder-1" };
                var secondComponent = new FakeSwComponent { NameValue = "Rack-1/Holder-2" };
                Equal(SwInstanceBindings.Context(firstComponent), SwInstanceBindings.Context(secondComponent));
                var group = new ExportNode
                {
                    Kind = ExportNodeKind.Group,
                    Source = new SwCadNode(null, new FakeSwComponent { NameValue = "Rack-1" }, "/Layout/Rack-1")
                };
                var first = new ExportNode
                {
                    Kind = ExportNodeKind.Asset,
                    Source = new SwCadNode(null, firstComponent, "/Layout/Rack-1/Holder-1")
                };
                var second = new ExportNode
                {
                    Kind = ExportNodeKind.Asset,
                    Source = new SwCadNode(null, secondComponent, "/Layout/Rack-1/Holder-2")
                };
                group.Children.Add(first);
                group.Children.Add(second);
                var plan = new AssemblyExportPlan();
                plan.Roots.Add(group);
                var nodes = SwInstanceBindings.IndexComponents(plan);

                Equal(first, nodes[firstComponent.Name2]);
                Equal(second, nodes[secondComponent.Name2]);
                Equal(group, nodes["Rack-1"]);
            });
            Run("Binding picks visit only selected ancestry and preserve distinct occurrences", BindingTargetedSelection);
            Run("Binding visualization finds matching assets across CAD parents and filters non-asset leaves", BindingVisualizationFindsAssets);
            Run("Asset visualization propagates confirmed relationship roles across matching versions", BindingVisualizationAssetRoles);
            Run("Binding reads native multi-selection and maps selected faces to components", delegate
            {
                var part = new FakeSwComponent { NameValue = "Part-1" };
                var otherPart = new FakeSwComponent { NameValue = "Part-2" };
                var selection = new BindingSelectionList(part, new BindingSelectedFace(otherPart),
                    new BindingSelectedFace(otherPart), new object());
                var components = SwBindingEndpoints.ReadSelection(selection, selection.GetSelectedObjectCount2(-1)).ToArray();
                True(components.SequenceEqual(new Component2[] { part, otherPart, otherPart, null }));
            });
            Run("Binding highlight accepts multiple faces of one occurrence and ignores plugin selections", delegate
            {
                const int highlightMark = 8;
                var part = new FakeSwComponent { NameValue = "Rack-1/Part-1" };
                var otherPart = new FakeSwComponent { NameValue = "Rack-2/Part-1" };
                var selection = new BindingSelectionList(new BindingSelectedFace(part), otherPart, new BindingSelectedFace(part));
                selection.Marks[1] = highlightMark;
                bool wholeComponent;
                Equal(part, SwBindingEndpoints.SinglePickedComponent(selection, highlightMark, out wholeComponent));
                True(!wholeComponent);
                selection.Marks[1] = 0;
                Equal<Component2>(null, SwBindingEndpoints.SinglePickedComponent(selection, highlightMark, out wholeComponent));
                selection = new BindingSelectionList(part, new BindingSelectedFace(part));
                Equal(part, SwBindingEndpoints.SinglePickedComponent(selection, highlightMark, out wholeComponent));
                True(wholeComponent);

                selection = new BindingSelectionList(new BindingSelectedFace(part), otherPart, otherPart);
                selection.Marks[1] = 16;
                selection.Marks[2] = 32;
                Equal(part, SwBindingEndpoints.SinglePickedComponent(selection, mark => mark == 16 || mark == 32, out wholeComponent));
                True(!wholeComponent);
            });
            RunInterfaceEditingTests();
            Run("Binding batch resolves captured endpoints once per transaction", delegate {
                var f = new BindingFixture(); var count = 0;
                var result = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T1"), f.E("T2"), f.E("T3") }, e => { count++; return f.Resolve(e); }, false);
                Equal(3, result.Count); Equal(4, count);
            });
            Run("Binding editor 200 tips have one occurrence parent group", delegate {
                var f = new BindingFixture(); var records = Enumerable.Range(1, 200).Select(i => new InstanceBinding { Parent = f.E("A"), Child = f.E("tip" + i) }).ToList();
                Equal(1, BindingEditorState.Group(records).Count()); Equal(200, BindingEditorState.Group(records).Single().Count());
                records.Add(new InstanceBinding { Parent = f.E("B"), Child = f.E("other") }); Equal(2, BindingEditorState.Group(records).Count());
            });
            Run("Binding editor incremental clicks accumulate and deduplicate", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A"));
                d.AddChildren(new[] { f.E("T1") }); d.AddChildren(new[] { f.E("T2"), f.E("T1"), f.E("A") }); Equal(2, d.Children.Count);
                d.Children.RemoveAt(0); d.AddChildren(new[] { f.E("T3") }); Equal("T2", d.Children[0].Context); Equal(2, d.Children.Count);
            });
            Run("Binding editor shows every parent of a shared child in separate groups", delegate {
                var f = new BindingFixture(); f.Apply("A", false, "T1", "T2"); f.AddLegacy("B", "T1");
                var groups = BindingEditorState.Group(f.Records).ToList(); Equal(2, groups.Count);
                Equal(2, groups.Count(g => g.Any(r => r.Child.Context == "T1")));
                var parents = BindingEditorState.Parents(f.Records, f.E("T1")); Equal(2, parents.Count);
                True(parents.Any(p => p.Context == "A")); True(parents.Any(p => p.Context == "B"));
                Equal(1, BindingEditorState.Parents(f.Records, f.E("T2")).Count);
            });
            Run("Binding editor normal apply clears next-round draft only", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1") });
                var proposed = InstanceBindingRules.Apply(f.Records, d.Parent, d.Children, f.Resolve, false); d.Applied(false);
                True(d.Parent == null); Equal(0, d.Children.Count); Equal(1, proposed.Count); Equal("T1", proposed.Single().Child.Context);
            });
            Run("Binding editor single-child chain retains incoming edge", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1") });
                var first = InstanceBindingRules.Apply(f.Records, d.Parent, d.Children, f.Resolve, false); d.Applied(true);
                Equal("T1", d.Parent.Context); Equal(0, d.Children.Count); d.AddChildren(new[] { f.E("T2") });
                var second = InstanceBindingRules.Apply(first, d.Parent, d.Children, f.Resolve, false); Equal(2, second.Count);
                True(second.Any(r => r.Parent.Context == "A" && r.Child.Context == "T1")); True(second.Any(r => r.Parent.Context == "T1" && r.Child.Context == "T2"));
                d.AddChildren(new[] { f.E("A") }); Throws<ValidationException>(() => InstanceBindingRules.Apply(second, d.Parent, d.Children, f.Resolve, false));
                Equal("T1", d.Parent.Context); Equal(2, d.Children.Count); Equal(2, second.Count);
            });
            Run("Binding editor multi-child chain requires explicit branch", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1"), f.E("T2") });
                d.Applied(true); True(d.Parent == null); Equal(2, d.NextParents.Count); Equal(0, d.Children.Count);
                Throws<ValidationException>(() => d.ContinueFrom(f.E("B"))); True(d.Parent == null);
                d.ContinueFrom(f.E("T2")); Equal("T2", d.Parent.Context); Equal(0, d.NextParents.Count);
            });
            Run("Binding editor bulk selection stays inside same sibling source", delegate {
                var a = new BindingEndpoint { Context = "1/2/3", AssetId = "tip:1", SourceIdentity = "tip", SourcePath = "C:/tip.sldprt" };
                var b = new BindingEndpoint { Context = "1/2/4", AssetId = "tip:1", SourceIdentity = "tip", SourcePath = "C:/tip.sldprt" };
                True(BindingEditorState.SameSiblingAsset(a, b)); b.Context = "1/9/4"; True(!BindingEditorState.SameSiblingAsset(a, b));
                b.Context = "1/2/4"; b.SourcePath = "C:/other.sldprt"; True(!BindingEditorState.SameSiblingAsset(a, b));
            });
        }

        private static void BindingTargetedSelection()
        {
            var directory = TempDirectory();
            try
            {
                var root = new FakeSwComponent { NameValue = "layout" };
                var document = BindingSelectionDocument(directory, "layout.SLDASM", true);
                document.ConfigurationValue = new BindingTestConfiguration { Name = "Default", RootComponent = root };
                var group = BindingSelectionComponent("Group-1", BindingSelectionDocument(directory, "group.SLDASM", true), null);
                var sharedModel = BindingSelectionDocument(directory, "holder.SLDASM", true);
                var properties = sharedModel.ExtensionValue.CustomPropertyManager.Manager("").Values;
                properties[PropertyRules.IsAsset] = "1";
                properties[PropertyRules.AssetClass] = "station";
                properties[PropertyRules.AssetVersion] = "1";
                var first = BindingSelectionComponent("Group-1/Holder-1", sharedModel, group);
                var second = BindingSelectionComponent("Group-1/Holder-2", sharedModel, group);
                var internalPart = new FakeSwComponent { NameValue = "Group-1/Holder-1/Pin-1", ParentValue = first };
                root.ChildrenValue = new object[] { group, new FakeSwComponent { NameValue = "Unavailable-1" } };
                group.ChildrenValue = new object[] { first, second };
                first.ChildrenValue = new object[] { internalPart };

                var operation = new SwBindingEndpoints(new SldWorks { ActiveDoc = document }, null);
                var picked = operation.FindSelectedAsset(internalPart);
                Equal(first.Name2, picked.Source.Name);
                Equal(picked, operation.FindSelectedAsset(first));
                True(picked.Id != operation.FindSelectedAsset(second).Id);
                Equal(0, root.ChildrenReads);
                Equal(0, group.ChildrenReads);
                Equal(0, first.ChildrenReads);
                Throws<ValidationException>(() => operation.FindSelectedAsset(root));
                group.VisibleValue = (int)swComponentVisibilityState_e.swComponentHidden;
                var later = new SwBindingEndpoints(new SldWorks { ActiveDoc = document }, null);
                Throws<ValidationException>(() => later.FindSelectedAsset(first));
            }
            finally { Directory.Delete(directory, true); }
        }

        private static FakeSwDocument BindingSelectionDocument(string directory, string name, bool assembly)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, "test");
            return new FakeSwDocument(name, path, true)
            {
                DocumentType = (int)(assembly ? swDocumentTypes_e.swDocASSEMBLY : swDocumentTypes_e.swDocPART),
                CreationTime = "2026-01-02T03:04:05"
            };
        }

        private static void BindingVisualizationFindsAssets()
        {
            var directory = TempDirectory();
            try
            {
                var root = new FakeSwComponent { NameValue = "layout" };
                var rootPath = Path.Combine(directory, "layout.SLDASM");
                File.WriteAllText(rootPath, "test");
                var document = new BindingSelectionAssembly(rootPath);
                document.ConfigurationValue = new BindingTestConfiguration { Name = "Default", RootComponent = root };
                var group = BindingSelectionDocument(directory, "group.SLDASM", true);
                var firstGroup = BindingSelectionComponent("Group-1", group, root);
                var secondGroup = BindingSelectionComponent("Group-2", group, root);

                var asset = BindingSelectionDocument(directory, "holder.SLDASM", true);
                var assetProperties = asset.ExtensionValue.CustomPropertyManager.Manager("").Values;
                assetProperties[PropertyRules.IsAsset] = "1";
                assetProperties[PropertyRules.AssetClass] = "station";
                assetProperties[PropertyRules.AssetVersion] = "3";
                var first = BindingSelectionComponent("Group-1/Holder-1", asset, firstGroup);
                var second = BindingSelectionComponent("Group-2/Holder-1", asset, secondGroup);
                var hidden = BindingSelectionComponent("Group-2/Holder-2", asset, secondGroup);
                hidden.VisibleValue = (int)swComponentVisibilityState_e.swComponentHidden;
                var suppressed = BindingSelectionComponent("Holder-1", asset, root);
                suppressed.SuppressionState = (int)swComponentSuppressionState_e.swComponentSuppressed;
                var alternateDirectory = Directory.CreateDirectory(Path.Combine(directory, "alternate")).FullName;
                var differentVersion = BindingSelectionDocument(alternateDirectory, "holder.SLDASM", true);
                var versionProperties = differentVersion.ExtensionValue.CustomPropertyManager.Manager("").Values;
                foreach (var property in assetProperties) versionProperties[property.Key] = property.Value;
                versionProperties[PropertyRules.AssetVersion] = "2";
                var alternate = BindingSelectionComponent("Group-2/Holder-3", differentVersion, secondGroup);

                var robot = BindingSelectionDocument(directory, "robot.SLDASM", true);
                var robotProperties = robot.ExtensionValue.CustomPropertyManager.Manager("").Values;
                robotProperties[PropertyRules.IsAsset] = "1";
                robotProperties[PropertyRules.AssetClass] = "robot";
                var robotOccurrence = BindingSelectionComponent("Robot-1", robot, root);
                var assetInsideRobot = BindingSelectionComponent("Robot-1/Holder-1", asset, robotOccurrence);
                robotOccurrence.ChildrenValue = new object[] { assetInsideRobot };
                var part = BindingSelectionDocument(directory, "bolt.SLDPRT", false);
                var bolt = BindingSelectionComponent("Group-1/Bolt-1", part, firstGroup);
                var internalBolt = BindingSelectionComponent("Group-1/Holder-1/Bolt-1", part, first);
                first.ChildrenValue = new object[] { internalBolt };
                firstGroup.ChildrenValue = new object[] { first, bolt };
                secondGroup.ChildrenValue = new object[] { second, hidden, alternate };
                var invalid = BindingSelectionDocument(directory, "missing.SLDASM", true);
                File.Delete(invalid.GetPathName());
                var missing = BindingSelectionComponent("Missing-1", invalid, root);
                root.ChildrenValue = new object[] { missing, firstGroup, robotOccurrence, secondGroup, suppressed };
                document.Components = new object[] { missing, firstGroup, secondGroup, robotOccurrence, first, second,
                    alternate, hidden, suppressed, assetInsideRobot, bolt, internalBolt };

                var operation = new SwBindingEndpoints(new SldWorks { ActiveDoc = document }, null);
                var picked = operation.FindSelectedAsset(internalBolt);
                True(operation.SameAssetInstances(picked).Select(node => node.Name).SequenceEqual(new[] { first.Name2, second.Name2 }));
                var alternateAsset = operation.FindSelectedAsset(alternate);
                var previousReads = document.ComponentListReads;
                True(operation.SameAssetInstances(new[] { picked, alternateAsset, picked }).Select(node => node.Name)
                    .SequenceEqual(new[] { first.Name2, alternate.Name2, second.Name2 }));
                Equal(previousReads + 1, document.ComponentListReads);
                Equal(0, part.ExtensionValue.CustomPropertyManager.Manager("").Get6Calls);
                Equal(0, root.ChildrenReads);

                var visualization = operation.ReadVisualization();
                True(visualization.Assets.Select(node => node.Name).SequenceEqual(new[] { first.Name2, second.Name2, alternate.Name2 }));
                Equal(2, visualization.Assets.Select(node => node.AssetId).Distinct().Count());
                Equal(3, visualization.Assets.Select(node => node.Id).Distinct().Count());
                True(visualization.NonAssetLeaves.SequenceEqual(new Component2[] { bolt }));
                Equal(0, first.ChildrenReads);
                Equal(0, robotOccurrence.ChildrenReads);
                Equal(visualization.Assets[0], operation.FindSelectedAsset(internalBolt));
                Throws<ValidationException>(() => operation.FindSelectedAsset(bolt));
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void BindingVisualizationAssetRoles()
        {
            var occurrences = new[]
            {
                new BindingAssetOccurrence("clicked", "center:3"),
                new BindingAssetOccurrence("peer", "center:3"),
                new BindingAssetOccurrence("unbound", "center:3"),
                new BindingAssetOccurrence("center-old", "center:2"),
                new BindingAssetOccurrence("parent", "parent:2"),
                new BindingAssetOccurrence("parent-peer", "parent:2"),
                new BindingAssetOccurrence("parent-old", "parent:1"),
                new BindingAssetOccurrence("child", "child:4"),
                new BindingAssetOccurrence("child-peer", "child:4"),
                new BindingAssetOccurrence("child-old", "child:3"),
                new BindingAssetOccurrence("peer-parent", "peer-parent:2"),
                new BindingAssetOccurrence("peer-parent-peer", "peer-parent:2"),
                new BindingAssetOccurrence("peer-child", "peer-child:2"),
                new BindingAssetOccurrence("indirect", "indirect:1"),
                new BindingAssetOccurrence("ambiguous", "parent:2"),
                new BindingAssetOccurrence("ambiguous", "child:4")
            };
            var directParent = VisualizedRelation("parent", "clicked");
            var directChild = VisualizedRelation("clicked", "child");
            var sameAssetChild = VisualizedRelation("clicked", "peer");
            var peerParent = VisualizedRelation("peer-parent", "peer");
            var peerChild = VisualizedRelation("peer", "peer-child");
            var relations = new[] { directParent, directChild, sameAssetChild, peerParent, peerChild,
                VisualizedRelation("center-old", "parent-old"), VisualizedRelation("child", "indirect"),
                VisualizedRelation("ambiguous", "clicked"), VisualizedRelation("missing", "clicked") };

            var selected = BindingEditorState.AssetVisualization(occurrences, "clicked", relations);
            Equal("center:3", selected.AssetId);
            Equal(BindingAssetRole.Clicked, selected.Roles["clicked"]);
            Equal(BindingAssetRole.DirectParent, selected.Roles["parent"]);
            Equal(BindingAssetRole.DirectChild, selected.Roles["child"]);
            Equal(BindingAssetRole.DirectChild, selected.Roles["peer"]);
            Equal(BindingAssetRole.SameAsset, selected.Roles["unbound"]);
            Equal(BindingAssetRole.AssetParent, selected.Roles["parent-peer"]);
            Equal(BindingAssetRole.AssetChild, selected.Roles["child-peer"]);
            Equal(BindingAssetRole.AssetParent, selected.Roles["peer-parent"]);
            Equal(BindingAssetRole.AssetParent, selected.Roles["peer-parent-peer"]);
            Equal(BindingAssetRole.AssetChild, selected.Roles["peer-child"]);
            Equal(10, selected.Roles.Count);
            True(selected.DirectRelations.SequenceEqual(new[] { directParent, directChild, sameAssetChild }));
            True(selected.AssetRelations.SequenceEqual(new[] { directParent, directChild, sameAssetChild, peerParent, peerChild }));

            var unbound = BindingEditorState.AssetVisualization(occurrences, "unbound", relations);
            Equal(0, unbound.DirectRelations.Count);
            True(unbound.AssetRelations.SequenceEqual(selected.AssetRelations));
            Equal(BindingAssetRole.Clicked, unbound.Roles["unbound"]);
            Equal(BindingAssetRole.SameAsset, unbound.Roles["clicked"]);
            Equal(BindingAssetRole.SameAsset, unbound.Roles["peer"]);
            Equal(BindingAssetRole.AssetParent, unbound.Roles["parent"]);
            Equal(BindingAssetRole.AssetChild, unbound.Roles["child"]);
            Equal(0, BindingEditorState.AssetVisualization(occurrences, "missing", relations).Roles.Count);
        }

        private static InstanceBinding VisualizedRelation(string parent, string child)
        {
            return new InstanceBinding
            {
                Parent = new BindingEndpoint { Context = parent, AssetId = "old-version" },
                Child = new BindingEndpoint { Context = child, AssetId = "old-version" }
            };
        }

        private static FakeSwComponent BindingSelectionComponent(string name, FakeSwDocument model, Component2 parent)
        {
            return new FakeSwComponent
            {
                NameValue = name,
                PathValue = model.GetPathName(),
                ModelDocumentWhenResolved = model,
                ParentValue = parent,
                VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved
            };
        }

        private sealed class BindingSelectionAssembly : FakeSwDocument, AssemblyDoc
        {
            public Component2 AddComponent5(string path, int configOption, string newConfigName, bool useExistingConfig,
                string existingConfigName, double x, double y, double z) { throw new NotSupportedException(); }
            public bool SetComponentState(int suppression, object components, int configurationOption, string configuration, bool saveClosedDocuments)
            { throw new NotSupportedException(); }
            internal BindingSelectionAssembly(string path) : base(Path.GetFileName(path), path, true)
            {
                DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY;
                CreationTime = "2026-01-02T03:04:05";
            }

            internal object[] Components { get; set; }
            internal int ComponentListReads { get; private set; }
            public object GetComponents(bool topLevelOnly) { ComponentListReads++; return Components; }
            public int GetLightWeightComponentCount() { return 0; }
            public int ResolveAllLightWeightComponents(bool warnUser) { throw new NotSupportedException(); }
            public bool Isolate() { throw new NotSupportedException(); }
            public bool ExitIsolate() { throw new NotSupportedException(); }
            public void SetIsolateVisibility(int option) { throw new NotSupportedException(); }
        }

        private sealed class BindingSelectionList : SelectionMgr
        {
            private readonly object[] _items;
            internal readonly int[] Marks;
            internal BindingSelectionList(params object[] items) { _items = items; Marks = new int[items.Length]; }
            public int GetSelectedObjectCount2(int mark) { return _items.Length; }
            public object GetSelectedObject6(int index, int mark) { return _items[index - 1]; }
            public object GetSelectedObjectsComponent4(int index, int mark) { return _items[index - 1] as Component2; }
            public int GetSelectedObjectMark(int index) { return Marks[index - 1]; }
            public SelectData CreateSelectData() { return null; }
            public int DeSelect2(object indices, int mark) { throw new NotSupportedException(); }
            public void set_SelectionColor(int mark, int color) { }
        }

        private sealed class BindingSelectedFace : Entity
        {
            private readonly Component2 _component;
            internal BindingSelectedFace(Component2 component) { _component = component; }
            public object GetComponent() { return _component; }
        }
    }
}
