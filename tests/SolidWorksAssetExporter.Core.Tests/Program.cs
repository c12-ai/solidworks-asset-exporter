using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("Asset boundary never reads children", AssetBoundaryNeverReadsChildren);
            Run("Asset subassembly stays opaque", AssetSubassemblyStaysOpaque);
            Run("Asset parent skips child property validation", AssetParentSkipsChildPropertyValidation);
            Run("Project classification ignores nonsemantic duplicate properties", ProjectClassificationIgnoresNonsemanticDuplicateProperties);
            Run("Non-Asset subassembly continues to leaves", NonAssetSubassemblyContinuesToLeaves);
            Run("Only Asset-containing branch descends", NestedAssetOnlyDescendsRequiredBranch);
            Run("Non-Asset subassembly with two Assets becomes Group", NonAssetSubassemblyWithTwoAssetsBecomesGroup);
            Run("All-project root continues to leaves", AllProjectRootContinuesToLeaves);
            Run("Top Asset stays one opaque unit", TopAssetRoot);
            Run("Repeated part occurrences share Asset identity", RepeatedPartOccurrencesShareAssetIdentity);
            Run("Hidden/suppressed/envelope nodes are ignored", VisibilityFiltering);
            Run("Mixed root requires fixed component", MixedRootRequiresFixedComponent);
            Run("Configuration properties are ignored for classification", ConfigurationPropertiesIgnoredForClassification);
            Run("Configuration properties are ignored for metadata", ConfigurationPropertiesIgnoredForMetadata);
            Run("Required Asset properties report every blank field", RequiredAssetPropertiesReportEveryBlankField);
            Run("Wanxiang publishable classes use the current protocol values", WanxiangPublishableClassContract);
            Run("Quick changer connection properties are validated together", QuickChangerConnectionPropertiesAreValidatedTogether);
            Run("Connection roles are derived from class and is flags", ConnectionRolesAreDerived);
            Run("UUIDv5 matches RFC vector", Uuid5KnownVector);
            Run("Asset identity uses creation time and file name only", AssetIdentityUsesCreationTimeAndFileName);
            Run("Relative transform and quaternion", RelativeTransformAndQuaternion);
            Run("XML has leaf mesh references", XmlLeafReferences);
            Run("Robot is a Project XML reference and never an Asset", RobotIsProjectReference);
            Run("Project export can be disabled", ProjectExportCanBeDisabled);
            Run("Manifest validates hashes and conflicts", ManifestValidation);
            Run("Robot Asset is metadata only", RobotAssetIsMetadataOnly);
            Run("Project report validates immutable package", ProjectReportValidation);
            Run("Directory transaction is immutable", DirectoryTransactionIsImmutable);
            Run("Part Asset source stays single-file", PartAssetSourceStaysSingleFile);
            Run("Part Asset packaging avoids SOLIDWORKS Pack and Go", PartAssetPackagingAvoidsPackAndGo);
            Run("Assembly Asset source stays inside boundary", AssemblyAssetSourceStaysInsideBoundary);
            Run("Asset source keeps virtual children embedded", AssetSourceKeepsVirtualChildrenEmbedded);
            Run("Asset source collection skips child metadata", AssetSourceCollectionSkipsChildMetadata);
            Run("Asset source rejects mismatched extension", AssetSourceRejectsMismatchedExtension);
            Run("Pack and Go may omit embedded virtual model paths", PackAndGoMayOmitEmbeddedVirtualPaths);
            Run("Pack and Go activates the Asset subassembly only", PackAndGoActivatesAssetSubassemblyOnly);
            Run("Pack and Go isolates references outside the Asset boundary", PackAndGoExcludesOutsideAssetReferences);
            Run("Pack and Go accepts a shorter status array when Asset outputs exist", PackAndGoAcceptsShorterStatusArray);
            Run("Pack and Go does not require an embedded virtual child output", PackAndGoAllowsEmbeddedVirtualChild);
            Run("Asset registry persists one entry per Asset ID", AssetRegistryPersistsOneEntryPerAssetId);
            Run("Asset version policy decides reuse and required upgrades", AssetVersionPolicyDecidesUpgrades);
            Run("Assets requiring version upgrade open for editing", AssetsRequiringUpgradeOpenForEditing);
            Run("Embedded Asset root is never opened for version editing", EmbeddedAssetRootIsNeverOpenedForVersionEditing);
            Run("Unregistered local Asset conflict is quarantined for rebuild", UnregisteredLocalAssetConflictIsQuarantined);
            Run("Asset registry imports every manifest for one UUID", AssetRegistryImportsEveryManifestForUuid);
            Run("Asset registry merge retains remote entries", AssetRegistryMergeRetainsRemoteEntries);
            Run("Asset registry merge rejects content conflicts", AssetRegistryMergeRejectsContentConflicts);
            Run("Wanxiang archive upload uses bearer and filters hidden entries", WanxiangArchiveUploadContract);
            Run("Wanxiang Asset publish uses the 0.4.0 atomic API", WanxiangAssetPublishContract);
            Run("Wanxiang upload diagnostics include HTTP response without API key", WanxiangUploadDiagnosticsAreSafe);
            Run("Wanxiang upload log is readable and single-line", WanxiangUploadLogIsReadable);
            Run("Wanxiang export atomically publishes Asset then uploads Project", WanxiangExportUsesAtomicPublication);
            Run("Wanxiang upload skips Project when disabled", WanxiangUploadSkipsDisabledProject);
            Run("Wanxiang preview reads remote registry without local cache by default", WanxiangPreviewUsesRemoteRegistry);
            Run("Wanxiang missing remote registry is treated as an empty library", WanxiangMissingRegistryIsEmpty);
            Run("Asset preview finds drawings without SOLIDWORKS document operations", DrawingLookupUsesFileSystemOnly);
            Run("Asset drawings are copied beside source models without PDF", DrawingSourcesAreCopiedWithoutPdf);
            Run("Asset file fingerprint cache reuses unchanged hashes", FileFingerprintCacheReusesUnchangedHashes);
            Run("Export preview snapshot rejects changed source files", ExportPreviewSnapshotRejectsChangedFiles);
            Run("Project part fingerprint avoids SOLIDWORKS Pack and Go", ProjectPartFingerprintAvoidsPackAndGo);
            Run("Project assembly fingerprint avoids SOLIDWORKS Pack and Go", ProjectAssemblyFingerprintAvoidsPackAndGo);
            Run("SOLIDWORKS component state keeps lightweight nodes", SolidWorksComponentStateKeepsLightweightNodes);
            Run("SOLIDWORKS classification reads file-level Asset flag from hidden model", SolidWorksClassificationReadsFileLevelAssetFlag);
            Run("SOLIDWORKS classification opens each repeated source once", SolidWorksClassificationOpensRepeatedSourceOnce);
            Run("SOLIDWORKS mismatched loaded component uses its source document", MismatchedLoadedComponentUsesSourceDocument);
            Run("SOLIDWORKS virtual component is resolved in context and never closed", VirtualComponentIsResolvedInContext);
            Run("SOLIDWORKS plug-in dirty flag is ignored until a user change", PluginDirtyFlagIsIgnoredUntilUserChange);
            Run("SOLIDWORKS full metadata reads file-level properties only", FullMetadataReadsFilePropertiesOnly);
            Run("SOLIDWORKS operation closes only newly exposed model window", DocumentScopeClosesNewWindow);
            Run("SOLIDWORKS operation preserves user-open model window", DocumentScopePreservesOpenWindow);
            Run("SOLIDWORKS rebuild warning is not an activation failure", DocumentScopeAcceptsRebuildWarning);
            Run("SOLIDWORKS model lease avoids forced read-only", ModelLeaseAvoidsForcedReadOnly);
            Run("SOLIDWORKS model lease falls back to QuitDoc", ModelLeaseFallsBackToQuitDoc);
            Run("SOLIDWORKS retained referenced model is not an open-window failure", RetainedReferencedModelIsNotOpenWindowFailure);

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
            asset.Model.FileProperties[PropertyRules.AssetClass] = "structure";
            asset.Model.FileProperties[PropertyRules.AssemblyVersion] = "1";
            var scan = new AssemblyScanner().Scan(asset);
            Equal(ScanClassification.AssetBoundary, scan.Classification);
            Equal(0, asset.GetChildrenCalls);
        }

        private static void SolidWorksComponentStateKeepsLightweightNodes()
        {
            var lightweight = new FakeSwComponent
            {
                VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
            };
            var lightweightNode = new SwCadNode(null, lightweight, "/lightweight");
            True(lightweightNode.IsVisible);
            Equal(false, lightweightNode.IsSuppressed);

            var hidden = new FakeSwComponent
            {
                VisibleValue = (int)swComponentVisibilityState_e.swComponentHidden,
                SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved
            };
            var hiddenNode = new SwCadNode(null, hidden, "/hidden");
            Equal(false, hiddenNode.IsVisible);
            Equal(false, hiddenNode.IsSuppressed);

            var suppressed = new FakeSwComponent
            {
                VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                SuppressionState = (int)swComponentSuppressionState_e.swComponentSuppressed
            };
            var suppressedNode = new SwCadNode(null, suppressed, "/suppressed");
            True(suppressedNode.IsVisible);
            True(suppressedNode.IsSuppressed);
        }

        private static void SolidWorksClassificationReadsFileLevelAssetFlag()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "asset-part.SLDPRT");
                File.WriteAllText(path, "test");
                var component = new FakeSwComponent
                {
                    PathValue = path,
                    ReferencedConfiguration = "Default",
                    ReferencedDisplayState = "Display State-1",
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
                };
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("asset-part.SLDPRT", path, false);
                var app = new FakeSldWorks(original, target);
                var global = target.ExtensionValue.CustomPropertyManager.Manager(string.Empty);
                global.Values[PropertyRules.IsAsset] = "1";
                global.Values[PropertyRules.AssetVersion] = "3";

                var properties = new SwCadNode(app, component, "/asset-part")
                    .ClassificationModel.FileProperties;

                True(PropertyRules.ReadIsAsset(properties));
                Equal("3", properties[PropertyRules.AssetVersion]);
                Equal(true, global.LastUseCached);
                Equal(0, global.GetNamesCalls);
                Equal(0, component.CustomPropertyManager.Manager(string.Empty).Get6Calls);
                Equal(1, app.ClosedTitles.Count(value => value == target.GetTitle()));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void SolidWorksClassificationOpensRepeatedSourceOnce()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "asset-part.SLDPRT");
                File.WriteAllText(path, "test");
                var first = new FakeSwComponent { PathValue = path, ReferencedConfiguration = "Default" };
                var second = new FakeSwComponent { PathValue = path, ReferencedConfiguration = "Default" };
                var rootComponent = new FakeSwComponent { ChildrenValue = new object[] { first, second } };
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("asset-part.SLDPRT", path, false);
                target.ExtensionValue.CustomPropertyManager.Manager(string.Empty)
                    .Values[PropertyRules.IsAsset] = "1";
                var app = new FakeSldWorks(original, target);
                var root = new SwCadNode(app, rootComponent, "/root", true, "root");
                var children = root.GetChildren().Cast<SwCadNode>().ToList();

                True(PropertyRules.ReadIsAsset(children[0].ClassificationModel.FileProperties));
                True(PropertyRules.ReadIsAsset(children[1].ClassificationModel.FileProperties));
                Equal(1, app.ClosedTitles.Count(value => value == target.GetTitle()));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void MismatchedLoadedComponentUsesSourceDocument()
        {
            var temp = TempDirectory();
            try
            {
                var rootPath = Path.Combine(temp, "root.SLDASM");
                var assetPath = Path.Combine(temp, "asset.SLDASM");
                File.WriteAllText(rootPath, "root");
                File.WriteAllText(assetPath, "asset");
                var root = new FakeSwDocument("root.SLDASM", rootPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var asset = new FakeSwDocument("asset.SLDASM", assetPath, false)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                asset.ExtensionValue.CustomPropertyManager.Manager(string.Empty)
                    .Values[PropertyRules.IsAsset] = "1";
                var app = new FakeSldWorks(root, asset);
                var component = new FakeSwComponent
                {
                    PathValue = assetPath,
                    ReferencedConfiguration = "Default",
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = root
                };

                var model = new SwCadNode(app, component, "/asset").ClassificationModel;

                True(PropertyRules.ReadIsAsset(model.FileProperties));
                Equal(Path.GetFullPath(assetPath), Path.GetFullPath(model.FullPath));
                Equal(1, app.OpenDocCalls);
                True(app.ClosedTitles.Contains(asset.GetTitle()));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void VirtualComponentIsResolvedInContext()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "virtual-asset.SLDASM");
                File.WriteAllText(path, "embedded");
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("virtual-asset.SLDASM", path, false);
                target.ExtensionValue.CustomPropertyManager.Manager(string.Empty)
                    .Values[PropertyRules.IsAsset] = "1";
                var component = new FakeSwComponent
                {
                    PathValue = path,
                    ReferencedConfiguration = "Default",
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight,
                    IsVirtual = true,
                    ModelDocumentWhenResolved = target
                };
                var app = new FakeSldWorks(original, target);

                True(PropertyRules.ReadIsAsset(new SwCadNode(app, component, "/virtual")
                    .ClassificationModel.FileProperties));
                Equal((int)swComponentSuppressionState_e.swComponentLightweight, component.SuppressionState);
                Equal(2, component.SuppressionChanges.Count);
                Equal((int)swComponentSuppressionState_e.swComponentResolved, component.SuppressionChanges[0]);
                Equal((int)swComponentSuppressionState_e.swComponentLightweight, component.SuppressionChanges[1]);
                Equal(0, app.OpenDocCalls);
                Equal(0, app.ClosedTitles.Count);
                True(File.Exists(path));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void PluginDirtyFlagIsIgnoredUntilUserChange()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "assembly.SLDASM");
                File.WriteAllText(path, "saved");
                var document = new FakeSwDocument("assembly.SLDASM", path, true)
                {
                    SaveFlag = false,
                    UpdateStamp = 10
                };
                var app = new FakeSldWorks(document);
                var tracker = new SwPluginMutationTracker(app);

                using (tracker.BeginMutation())
                {
                    document.SaveFlag = true;
                    document.UpdateStamp = 11;
                }
                True(!tracker.IsDirty(document, false, path));

                document.UpdateStamp = 12;
                True(tracker.IsDirty(document, false, path));

                document.SaveFlag = false;
                True(!tracker.IsDirty(document, false, path));
                document.SaveFlag = true;
                document.UpdateStamp = 13;
                True(tracker.IsDirty(document, false, path));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void FullMetadataReadsFilePropertiesOnly()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "asset-part.SLDPRT");
                File.WriteAllText(path, "test");
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("asset-part.SLDPRT", path, false);
                var fileProperties = target.ExtensionValue.CustomPropertyManager.Manager(string.Empty);
                fileProperties.Values[PropertyRules.IsAsset] = "1";
                var configurationProperties = target.ExtensionValue.CustomPropertyManager.Manager("Default");
                configurationProperties.Values["business_value"] = "from-model";
                var component = new FakeSwComponent
                {
                    PathValue = path,
                    ReferencedConfiguration = "Default",
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
                };
                component.CustomPropertyManager.Manager(string.Empty).Values["wrong_scope"] = "component";
                var app = new FakeSldWorks(original, target);

                var model = new SwCadNode(app, component, "/asset-part").Model;

                Equal("1", model.FileProperties[PropertyRules.IsAsset]);
                Equal(0, model.ConfigurationProperties.Count);
                Equal(true, fileProperties.LastUseCached);
                Equal(0, configurationProperties.Get6Calls);
                Equal(0, component.CustomPropertyManager.Manager(string.Empty).Get6Calls);
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void DocumentScopeClosesNewWindow()
        {
            var original = new FakeSwDocument("root.SLDASM", "C:\\models\\root.SLDASM", true);
            var target = new FakeSwDocument("part.SLDPRT", "C:\\models\\part.SLDPRT", false);
            var app = new FakeSldWorks(original, target);

            using (new SwActiveDocumentScope(app, target, true))
            {
                Equal(target, app.ActiveDoc);
                True(target.Visible);
            }

            Equal(original, app.ActiveDoc);
            Equal(1, app.ClosedTitles.Count);
            Equal(target.GetTitle(), app.ClosedTitles[0]);
        }

        private static void DocumentScopePreservesOpenWindow()
        {
            var original = new FakeSwDocument("root.SLDASM", "C:\\models\\root.SLDASM", true);
            var target = new FakeSwDocument("part.SLDPRT", "C:\\models\\part.SLDPRT", true);
            var app = new FakeSldWorks(original, target);

            using (new SwActiveDocumentScope(app, target, true)) { }

            Equal(original, app.ActiveDoc);
            Equal(0, app.ClosedTitles.Count);
            True(target.Visible);
        }

        private static void DocumentScopeAcceptsRebuildWarning()
        {
            var original = new FakeSwDocument("root.SLDASM", "C:\\models\\root.SLDASM", true);
            var target = new FakeSwDocument("part.SLDPRT", "C:\\models\\part.SLDPRT", false);
            var app = new FakeSldWorks(original, target) { ActivationErrors = (int)swActivateDocError_e.swDocNeedsRebuildWarning };

            using (new SwActiveDocumentScope(app, target, true)) { }

            Equal(original, app.ActiveDoc);
            Equal(1, app.ClosedTitles.Count);
        }

        private static void ModelLeaseAvoidsForcedReadOnly()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "part.SLDPRT");
                File.WriteAllText(path, "test");
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("part.SLDPRT", path, false);
                var app = new FakeSldWorks(original, target);

                using (var lease = SwModelDocumentLease.Open(app, path, "Default"))
                {
                    True(lease.OpenedHere);
                    Equal(target, lease.Document);
                    Equal(false, target.Visible);
                    True(app.DocumentsVisible);
                }

                Equal(1, app.ClosedTitles.Count);
                Equal(target.GetTitle(), app.ClosedTitles[0]);
                True(app.OpenOptions.HasValue);
                Equal(0, app.OpenOptions.Value & (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly);
                Equal(false, target.IsOpenedReadOnly());
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void ModelLeaseFallsBackToQuitDoc()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "part.SLDPRT");
                File.WriteAllText(path, "test");
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("part.SLDPRT", path, false);
                var app = new FakeSldWorks(original, target) { IgnoreCloseDoc = true };

                using (var lease = SwModelDocumentLease.Open(app, path, "Default"))
                    True(lease.OpenedHere);

                Equal(1, app.ClosedTitles.Count);
                Equal(1, app.QuitTitles.Count);
                Equal(target.GetTitle(), app.QuitTitles[0]);
                Equal(null, app.GetOpenDocumentByName(path));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void RetainedReferencedModelIsNotOpenWindowFailure()
        {
            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "part.SLDPRT");
                File.WriteAllText(path, "test");
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(temp, "root.SLDASM"), true);
                var target = new FakeSwDocument("part.SLDPRT", path, false);
                var app = new FakeSldWorks(original, target)
                {
                    IgnoreCloseDoc = true,
                    RetainDocumentInMemoryOnQuit = true
                };

                using (var lease = SwModelDocumentLease.Open(app, path, "Default"))
                    True(lease.OpenedHere);

                Equal(1, app.QuitTitles.Count);
                Equal(target, app.GetOpenDocumentByName(path));
                Equal(false, target.Visible);
                Equal(false, target.IsOpenedReadOnly());
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void AssetSubassemblyStaysOpaque()
        {
            var root = Root();
            var asset = Node("asset-subassembly", true, true); MarkAsset(asset, 2);
            asset.ThrowOnChildren = true;
            root.Add(asset);

            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            var exported = plan.Roots.Single();

            Equal(ExportNodeKind.Asset, exported.Kind);
            Equal(0, exported.Children.Count);
            Equal(0, asset.GetChildrenCalls);
        }

        private static void AssetParentSkipsChildPropertyValidation()
        {
            var root = Root();
            var asset = Node("asset-parent", true, true); MarkAsset(asset, 1);
            var child = Node("child-with-duplicate-name", false, false);
            child.Model.FileProperties["名称"] = "file-level";
            child.Model.ConfigurationProperties["名称"] = "configuration-level";
            asset.Add(child);
            root.Add(asset);

            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);

            Equal(ExportNodeKind.Asset, plan.Roots.Single().Kind);
            Equal(0, asset.GetChildrenCalls);
        }

        private static void ProjectClassificationIgnoresNonsemanticDuplicateProperties()
        {
            var root = Root();
            var child = Node("project-child", false, false);
            child.Model.FileProperties["名称"] = "file-level";
            child.Model.ConfigurationProperties["名称"] = "configuration-level";
            root.Add(child);
            var rootReads = root.ModelReadCalls;
            var childReads = child.ModelReadCalls;
            root.ThrowOnModel = true;
            child.ThrowOnModel = true;

            var scan = new AssemblyScanner().Scan(root);
            var plan = new ExportPlanBuilder().Build(scan, ProjectMeshFormat.Step);

            Equal(ScanClassification.NoAsset, scan.Classification);
            Equal(ExportNodeKind.Project, plan.Roots.Single().Kind);
            Equal(rootReads, root.ModelReadCalls);
            Equal(childReads, child.ModelReadCalls);
        }

        private static void NonAssetSubassemblyContinuesToLeaves()
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
            Equal(1, plan.Roots.Count(x => x.Kind == ExportNodeKind.Group));
            var group = plan.Roots.Single(x => x.Name == "custom-frame");
            Equal(2, group.Children.Count);
            Equal(2, group.Children.Count(x => x.Kind == ExportNodeKind.Project));
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

        private static void NonAssetSubassemblyWithTwoAssetsBecomesGroup()
        {
            var root = Root();
            var subassembly = Node("subassembly", true, true);
            var first = Node("first-asset", false, false); MarkAsset(first, 1);
            var second = Node("second-asset", false, false); MarkAsset(second, 1);
            subassembly.Add(first, second); root.Add(subassembly);

            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            var group = plan.Roots.Single();

            Equal(ExportNodeKind.Group, group.Kind);
            Equal(2, group.Children.Count);
            Equal(2, group.Children.Count(node => node.Kind == ExportNodeKind.Asset));
            Equal(0, group.Children.Count(node => node.Kind == ExportNodeKind.Project));
        }

        private static void AllProjectRootContinuesToLeaves()
        {
            var root = Root(); root.Add(Node("a", false, false), Node("b", false, false));
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root), ProjectMeshFormat.Step);
            Equal(2, plan.Roots.Count);
            Equal(2, plan.Roots.Count(node => node.Kind == ExportNodeKind.Project));
            True(plan.Roots.Any(node => node.Name == "a"));
            True(plan.Roots.Any(node => node.Name == "b"));
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

        private static void ConfigurationPropertiesIgnoredForClassification()
        {
            var model = Descriptor("dup");
            model.FileProperties["is_asset"] = "false";
            model.ConfigurationProperties["IS_ASSET"] = "true";
            var properties = PropertyRules.MergeForClassification(model);
            True(!PropertyRules.ReadIsAsset(properties));

            model.FileProperties.Clear();
            properties = PropertyRules.MergeForClassification(model);
            True(!PropertyRules.ReadIsAsset(properties));
        }

        private static void ConfigurationPropertiesIgnoredForMetadata()
        {
            var model = Descriptor("file-only");
            model.FileProperties["图号"] = "FILE";
            model.ConfigurationProperties["图号"] = "CONFIGURATION";
            model.ConfigurationProperties["配置专用"] = "ignored";
            var properties = PropertyRules.Merge(model);
            Equal(1, properties.Count);
            Equal("FILE", properties["图号"]);
            True(!properties.ContainsKey("配置专用"));
        }

        private static void RequiredAssetPropertiesReportEveryBlankField()
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "零件名", " " },
                { "设计目的", "用途" }
            };
            var missing = PropertyRules.MissingOrBlankProperties(properties,
                new[] { "零件名", "图号", "设计目的" });
            Equal(2, missing.Count);
            Equal("零件名", missing[0]);
            Equal("图号", missing[1]);
        }

        private static void WanxiangPublishableClassContract()
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { PropertyRules.AssetClass, "movable" }
            };
            Equal("movable", PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "station";
            Equal("station", PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "structure";
            Equal("structure", PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "Movable";
            Throws<ValidationException>(() => PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "moveable";
            Throws<ValidationException>(() => PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "equipment";
            Throws<ValidationException>(() => PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
            properties[PropertyRules.AssetClass] = "robot";
            Throws<ValidationException>(() => PropertyRules.RequireWanxiangAssetClass(properties, "asset"));
        }

        private static void QuickChangerConnectionPropertiesAreValidatedTogether()
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { PropertyRules.AssetClass, "movable" },
                { PropertyRules.IsQuickChanger, "1" },
                { PropertyRules.QuickChangerSide, "tool_side" },
                { PropertyRules.ConnectionInterface, "快换盘-A" },
                { PropertyRules.AcceptsInterfaces, "法兰-4xM6-PCD30" }
            };
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(properties).Count);

            properties[PropertyRules.QuickChangerSide] = "lower";
            properties[PropertyRules.ConnectionInterface] = string.Empty;
            properties[PropertyRules.AcceptsInterfaces] = string.Empty;
            var issues = PropertyRules.ValidateAssetConnectionProperties(properties);
            Equal(3, issues.Count);
            True(issues.Any(value => value.Contains("quick_changer_side")));
            True(issues.Any(value => value.Contains("connection_interface")));
            True(issues.Any(value => value.Contains("accepts_interfaces")));

            var rack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { PropertyRules.AssetClass, "station" },
                { PropertyRules.IsQuickChangerRack, "1" },
                { PropertyRules.AcceptsInterfaces, "快换盘-A" }
            };
            Equal(0, PropertyRules.ValidateAssetConnectionProperties(rack).Count);
            rack[PropertyRules.AssetClass] = "structure";
            True(PropertyRules.ValidateAssetConnectionProperties(rack)
                .Any(value => value.Contains("station")));
        }

        private static void ConnectionRolesAreDerived()
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { PropertyRules.AssetClass, "movable" },
                { PropertyRules.IsQuickChanger, "1" },
                { PropertyRules.QuickChangerSide, "robot_side" }
            };
            Equal("快换盘-机器人端", PropertyRules.DescribeAssetConnectionRole(properties));
            properties[PropertyRules.IsQuickChanger] = "0";
            properties[PropertyRules.AssetClass] = "station";
            properties[PropertyRules.IsQuickChangerRack] = "1";
            Equal("快换架", PropertyRules.DescribeAssetConnectionRole(properties));
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
                File.WriteAllText(Path.Combine(versionDir, "source", "models", "root.SLDDRW"), "drawing-data");
                var manifest = new AssetManifest
                {
                    SchemaVersion = "1.0", Uuid = uuid, Version = 1, ContentFingerprint = "fingerprint",
                    Files = AssetManifestValidator.DescribeFiles(versionDir, new[]
                        { "geometry/model.step", "geometry/model.stl", "source/models/root.SLDPRT", "source/models/root.SLDDRW" })
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

        private static void PartAssetSourceStaysSingleFile()
        {
            var part = Node("asset-part", false, false);
            part.Model.DocumentKind = DocumentKind.Part;
            part.Model.FullPath = "C:\\models\\asset-part.SLDPRT";
            part.ThrowOnChildren = true;
            var files = AssetSourcePlanner.CollectModelFiles(part);
            Equal(1, files.Count);
            Equal(part.Model.FullPath, files[0]);
            Equal(0, part.GetChildrenCalls);
        }

        private static void AssetIdentityUsesCreationTimeAndFileName()
        {
            var model = Descriptor("identity");
            var original = IdentityService.AssetUuid(model);
            model.Configuration = "Another configuration";
            model.DisplayState = "Another display state";
            model.DocumentKind = DocumentKind.Part;
            Equal(original, IdentityService.AssetUuid(model));

            model.FileName = "renamed.SLDPRT";
            True(original != IdentityService.AssetUuid(model));
            model.FileName = "identity.SLDASM";
            model.InternalCreationTime = "2026-01-02T03:04:06.0000000Z";
            True(original != IdentityService.AssetUuid(model));
        }

        private static void PartAssetPackagingAvoidsPackAndGo()
        {
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "asset-part.SLDPRT");
                File.WriteAllText(source, "asset-part");
                var target = new FakeSwDocument("asset-part.SLDPRT", source, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocPART
                };
                var app = new FakeSldWorks(target);
                var component = new FakeSwComponent
                {
                    PathValue = source,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = target
                };
                var node = new SwCadNode(app, component, "/asset-part");
                var destination = Path.Combine(root, "packed");

                new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .PackAsset(node, new[] { source }, destination);

                True(File.Exists(Path.Combine(destination, Path.GetFileName(source))));
                True(target.ExtensionValue.PackAndGoValue == null);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void AssemblyAssetSourceStaysInsideBoundary()
        {
            var parent = Node("parent", false, true);
            var asset = Node("asset-assembly", false, true);
            var nested = Node("nested-assembly", false, true);
            var part = Node("nested-part", false, false); part.Model.DocumentKind = DocumentKind.Part; part.Model.FullPath = "C:\\models\\nested-part.SLDPRT";
            var hidden = Node("hidden-part", false, false); hidden.Model.DocumentKind = DocumentKind.Part; hidden.Model.FullPath = "C:\\models\\hidden-part.SLDPRT"; hidden.IsVisibleValue = false;
            var suppressed = Node("suppressed-part", false, false); suppressed.Model.DocumentKind = DocumentKind.Part; suppressed.Model.FullPath = "C:\\models\\suppressed-part.SLDPRT"; suppressed.IsSuppressedValue = true; suppressed.ThrowOnChildren = true;
            var outside = Node("outside-part", false, false); outside.Model.DocumentKind = DocumentKind.Part; outside.Model.FullPath = "C:\\models\\outside-part.SLDPRT";
            nested.Add(part); asset.Add(nested, hidden, suppressed); parent.Add(asset, outside);

            var files = AssetSourcePlanner.CollectModelFiles(asset);
            Equal(4, files.Count);
            True(files.Contains(asset.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(files.Contains(nested.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(files.Contains(part.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(files.Contains(hidden.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(!files.Contains(suppressed.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(!files.Contains(parent.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(!files.Contains(outside.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            Equal(0, suppressed.GetChildrenCalls);
        }

        private static void AssetSourceCollectionSkipsChildMetadata()
        {
            var asset = Node("asset-assembly", false, true);
            var child = Node("child-part", false, false);
            child.Model.DocumentKind = DocumentKind.Part;
            child.Model.FullPath = "C:\\models\\child-part.SLDPRT";
            asset.Add(child);
            var assetReads = asset.ModelReadCalls;
            var childReads = child.ModelReadCalls;
            asset.ThrowOnModel = true;
            child.ThrowOnModel = true;

            var files = AssetSourcePlanner.CollectModelFiles(asset);

            Equal(2, files.Count);
            Equal(assetReads, asset.ModelReadCalls);
            Equal(childReads, child.ModelReadCalls);
        }

        private static void AssetSourceKeepsVirtualChildrenEmbedded()
        {
            var asset = Node("asset", false, true);
            var virtualAssembly = Node("virtual", false, true);
            virtualAssembly.Model.FullPath = "C:\\models\\装配体2^asset.SLDASM";
            var externalChild = Node("external-child", false, false);
            externalChild.Model.DocumentKind = DocumentKind.Part;
            externalChild.Model.FullPath = "C:\\models\\external-child.SLDPRT";
            virtualAssembly.Add(externalChild);
            asset.Add(virtualAssembly);

            var files = AssetSourcePlanner.CollectModelFiles(asset);

            Equal(2, files.Count);
            True(files.Contains(asset.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(files.Contains(externalChild.Model.FullPath, StringComparer.OrdinalIgnoreCase));
            True(!files.Contains(virtualAssembly.Model.FullPath, StringComparer.OrdinalIgnoreCase));
        }

        private static void AssetSourceRejectsMismatchedExtension()
        {
            var part = Node("asset-part", false, false);
            part.Model.DocumentKind = DocumentKind.Part;
            part.Model.FullPath = "C:\\models\\asset-part.SLDASM";
            Throws<ValidationException>(() => AssetSourcePlanner.CollectModelFiles(part));
        }

        private static void PackAndGoMayOmitEmbeddedVirtualPaths()
        {
            var root = Path.Combine("C:\\models", "asset.SLDASM");
            var external = Path.Combine("C:\\models", "external.SLDPRT");
            var embeddedInternal = Path.Combine(Path.GetTempPath(), "swx25800", "IC~~", "923-F0023.stp.SLDPRT");
            var embeddedVirtual = Path.Combine(Path.GetTempPath(), "swx25800", "VC~~", "virtual.SLDPRT");
            var caretVirtual = Path.Combine("C:\\models", "装配体2^asset.SLDASM");

            True(AssetSourcePathPolicy.IsSessionEmbeddedModelPath(embeddedInternal));
            True(AssetSourcePathPolicy.IsSessionEmbeddedModelPath(embeddedVirtual));
            True(AssetSourcePathPolicy.IsSessionEmbeddedModelPath(caretVirtual));
            True(!AssetSourcePathPolicy.IsSessionEmbeddedModelPath(external));

            var missing = AssetSourcePathPolicy.MissingExternalFiles(
                new[] { root, external, embeddedInternal, embeddedVirtual }, new[] { root });
            Equal(1, missing.Count);
            Equal(Path.GetFullPath(external), missing[0]);
        }

        private static void PackAndGoActivatesAssetSubassemblyOnly()
        {
            var temp = TempDirectory();
            try
            {
                var topPath = Path.Combine(temp, "top.SLDASM");
                var assetPath = Path.Combine(temp, "asset.SLDASM");
                var outsidePath = Path.Combine(temp, "outside.SLDPRT");
                File.WriteAllText(topPath, "top");
                File.WriteAllText(assetPath, "asset");
                File.WriteAllText(outsidePath, "outside");
                var top = new FakeSwDocument("top.SLDASM", topPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var asset = new FakeSwDocument("asset.SLDASM", assetPath, false)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(top, asset);
                var packAndGo = new FakePackAndGo(delegate
                {
                    return ReferenceEquals(app.ActiveDoc, asset)
                        ? new[] { assetPath }
                        : new[] { topPath, assetPath, outsidePath };
                });
                asset.ExtensionValue.PackAndGoValue = packAndGo;
                var component = new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                };
                var node = new SwCadNode(app, component, "/asset");
                var destination = Path.Combine(temp, "packed");

                new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .PackAsset(node, new[] { assetPath }, destination);

                True(packAndGo.GetDocumentNamesCalledWhileAssetActive);
                True(ReferenceEquals(app.ActiveDoc, top));
                True(app.ClosedTitles.Contains(asset.GetTitle()));
                True(File.Exists(Path.Combine(destination, Path.GetFileName(assetPath))));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void RobotIsProjectReference()
        {
            var root = Root();
            var robot = Node("robot", true, true);
            robot.ThrowOnChildren = true;
            robot.Model.InternalCreationTime = string.Empty;
            MarkRobot(robot, "Hebe", 1);
            root.Add(robot);

            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root),
                ProjectMeshFormat.Step, true);
            var node = plan.Roots.Single();
            Equal(ExportNodeKind.Robot, node.Kind);
            Equal("Hebe:1", node.RobotId);
            True(string.IsNullOrEmpty(node.AssetId));
            True(string.IsNullOrEmpty(node.GeometryUuid));
            True(string.IsNullOrEmpty(node.MeshFile));
            Equal(0, robot.GetChildrenCalls);

            var temp = TempDirectory();
            try
            {
                var path = Path.Combine(temp, "assembly.xml");
                AssemblyXmlWriter.Write(path, plan);
                var element = XDocument.Load(path).Descendants("node").Single();
                Equal("robot", (string)element.Attribute("kind"));
                Equal("Hebe:1", (string)element.Element("mesh").Attribute("robot_id"));
                True(element.Element("mesh").Attribute("asset_id") == null);
                True(element.Element("mesh").Attribute("file") == null);
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void ProjectExportCanBeDisabled()
        {
            var root = Root();
            root.Model.FileProperties.Remove(PropertyRules.AssemblyVersion);
            var projectPart = Node("project-part", false, false);
            projectPart.Model.InternalCreationTime = string.Empty;
            var asset = Node("asset", false, false);
            MarkAsset(asset, 1);
            root.Add(projectPart, asset);
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(root),
                ProjectMeshFormat.Step, false);
            True(!plan.ExportProject);
            Equal(0, plan.AssemblyVersion);
            var project = plan.Roots.Single(value => value.Kind == ExportNodeKind.Project);
            True(string.IsNullOrEmpty(project.GeometryUuid));
            True(string.IsNullOrEmpty(project.MeshFile));
            Equal(1, plan.Roots.Count(value => value.Kind == ExportNodeKind.Asset));
            ExportPlanValidator.Validate(plan);

            var settings = new ExporterSettings
            {
                AssetLibraryRoot = TempDirectory(),
                ProjectExportRoot = string.Empty,
                WanxiangBaseUrl = "http://localhost:8100",
                WanxiangApiKey = "secret-key",
                ExportProject = false
            };
            try { settings.Validate(); }
            finally { Directory.Delete(settings.AssetLibraryRoot, true); }
        }

        private static void RobotAssetIsMetadataOnly()
        {
            var temp = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D");
                var versionDirectory = Path.Combine(temp, uuid, "v1");
                Directory.CreateDirectory(versionDirectory);
                var manifestPath = Path.Combine(versionDirectory, "asset_" + uuid + "_v1.json");
                var manifest = new AssetManifest
                {
                    SchemaVersion = "1.0",
                    Uuid = uuid,
                    Version = 1,
                    ContentFingerprint = "robot-fingerprint",
                    Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        { { "CLASS", " Robot " } },
                    Files = new List<ManifestFile>()
                };
                JsonFile.Write(manifestPath, manifest);

                True(PropertyRules.IsRobotClass(manifest.Properties));
                Equal(1, Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories).Count());
                Equal(0, manifest.Files.Count);
                Equal(ExistingAssetState.Reusable, AssetManifestValidator.Inspect(versionDirectory, uuid, 1,
                    "robot-fingerprint"));

                File.Delete(manifestPath);
                manifest.Properties["CLASS"] = "station";
                JsonFile.Write(manifestPath, manifest);
                Throws<ValidationException>(() => AssetManifestValidator.Inspect(versionDirectory, uuid, 1,
                    "robot-fingerprint"));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void PackAndGoExcludesOutsideAssetReferences()
        {
            var temp = TempDirectory();
            try
            {
                var assetPath = Path.Combine(temp, "asset.SLDASM");
                var childPath = Path.Combine(temp, "child.SLDPRT");
                var outsidePath = Path.Combine(temp, "parent-context.SLDPRT");
                File.WriteAllText(assetPath, "asset");
                File.WriteAllText(childPath, "child");
                File.WriteAllText(outsidePath, "outside");
                var asset = new FakeSwDocument("asset.SLDASM", assetPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(asset);
                var packAndGo = new FakePackAndGo(delegate
                {
                    return new[] { assetPath, childPath, outsidePath };
                });
                asset.ExtensionValue.PackAndGoValue = packAndGo;
                var component = new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                };
                var node = new SwCadNode(app, component, "/asset");
                var destination = Path.Combine(temp, "packed");

                new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .PackAsset(node, new[] { assetPath, childPath }, destination);

                Equal(0, packAndGo.SetDocumentSaveToNamesCalls);
                True(File.Exists(Path.Combine(destination, Path.GetFileName(assetPath))));
                True(File.Exists(Path.Combine(destination, Path.GetFileName(childPath))));
                True(!File.Exists(Path.Combine(destination, Path.GetFileName(outsidePath))));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void PackAndGoAcceptsShorterStatusArray()
        {
            var temp = TempDirectory();
            try
            {
                var assetPath = Path.Combine(temp, "asset.SLDASM");
                var childOne = Path.Combine(temp, "child-one.SLDPRT");
                var childTwo = Path.Combine(temp, "child-two.SLDPRT");
                var outsideOne = Path.Combine(temp, "outside-one.SLDPRT");
                var outsideTwo = Path.Combine(temp, "outside-two.SLDPRT");
                foreach (var path in new[] { assetPath, childOne, childTwo, outsideOne, outsideTwo })
                    File.WriteAllText(path, Path.GetFileName(path));
                var asset = new FakeSwDocument("asset.SLDASM", assetPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(asset);
                var packAndGo = new FakePackAndGo(delegate
                {
                    return new[] { assetPath, childOne, childTwo, outsideOne, outsideTwo };
                }) { ReturnedStatusCount = 4 };
                asset.ExtensionValue.PackAndGoValue = packAndGo;
                var node = new SwCadNode(app, new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                }, "/asset");
                var destination = Path.Combine(temp, "packed");

                new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .PackAsset(node, new[] { assetPath, childOne, childTwo }, destination);

                True(File.Exists(Path.Combine(destination, Path.GetFileName(assetPath))));
                True(File.Exists(Path.Combine(destination, Path.GetFileName(childOne))));
                True(File.Exists(Path.Combine(destination, Path.GetFileName(childTwo))));
                True(!File.Exists(Path.Combine(destination, Path.GetFileName(outsideOne))));
                True(!File.Exists(Path.Combine(destination, Path.GetFileName(outsideTwo))));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void PackAndGoAllowsEmbeddedVirtualChild()
        {
            var temp = TempDirectory();
            try
            {
                var assetPath = Path.Combine(temp, "asset.SLDASM");
                var externalChild = Path.Combine(temp, "external-child.SLDPRT");
                var virtualChild = Path.Combine(temp, "装配体2^asset.SLDASM");
                foreach (var path in new[] { assetPath, externalChild, virtualChild })
                    File.WriteAllText(path, Path.GetFileName(path));
                var asset = new FakeSwDocument("asset.SLDASM", assetPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(asset);
                var packAndGo = new FakePackAndGo(delegate
                {
                    return new[] { assetPath, externalChild, virtualChild };
                }) { ReturnedStatusCount = 2 };
                packAndGo.OmittedSourcePaths.Add(virtualChild);
                asset.ExtensionValue.PackAndGoValue = packAndGo;
                var node = new SwCadNode(app, new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                }, "/asset");
                var destination = Path.Combine(temp, "packed");

                new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .PackAsset(node, new[] { assetPath, externalChild, virtualChild }, destination);

                True(File.Exists(Path.Combine(destination, Path.GetFileName(assetPath))));
                True(File.Exists(Path.Combine(destination, Path.GetFileName(externalChild))));
                True(!File.Exists(Path.Combine(destination, Path.GetFileName(virtualChild))));
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void AssetRegistryPersistsOneEntryPerAssetId()
        {
            var temp = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid();
                var assetId = IdentityService.AssetId(uuid, 3);
                Equal(0, AssetRegistryStore.RegisteredAssetIds(temp).Count);
                AssetRegistryStore.Register(temp, uuid.ToString("D"), 3, "fingerprint-a");
                True(File.Exists(Path.Combine(temp, AssetRegistryStore.FileName)));
                True(AssetRegistryStore.RegisteredAssetIds(temp).Contains(assetId));
                AssetRegistryStore.Register(temp, uuid.ToString("D"), 3, "fingerprint-b");
                var registry = AssetRegistryStore.Load(temp);
                Equal(1, registry.Assets.Count);
                Equal("fingerprint-b", registry.Assets[0].ContentFingerprint);
                Equal(uuid.ToString("D") + "/v3", registry.Assets[0].RelativeDirectory);

                var legacyUuid = Guid.NewGuid();
                var legacyDirectory = Path.Combine(temp, legacyUuid.ToString("D"), "v2");
                Directory.CreateDirectory(legacyDirectory);
                JsonFile.Write(Path.Combine(legacyDirectory, "asset_" + legacyUuid.ToString("D") + "_v2.json"),
                    new AssetManifest { SchemaVersion = "1.0", Uuid = legacyUuid.ToString("D"), Version = 2, ContentFingerprint = "legacy-fingerprint" });
                True(AssetRegistryStore.ImportKnownManifest(temp, legacyUuid.ToString("D"), 2));
                Equal(2, AssetRegistryStore.Load(temp).Assets.Count);
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void AssetVersionPolicyDecidesUpgrades()
        {
            var uuid = Guid.NewGuid().ToString("D");
            var registrations = new[]
            {
                Registration(uuid, 1, "fingerprint-v1"),
                Registration(uuid, 2, "fingerprint-v2")
            };
            var newAsset = AssetVersionPolicy.Evaluate(Guid.NewGuid().ToString("D"), 1, "new", registrations);
            Equal(AssetVersionDecisionKind.NewAsset, newAsset.Kind);
            True(newAsset.CanExport);

            var reuse = AssetVersionPolicy.Evaluate(uuid, 2, "fingerprint-v2", registrations);
            Equal(AssetVersionDecisionKind.ReuseCurrentVersion, reuse.Kind);
            True(reuse.CanExport);

            var changedWithoutUpgrade = AssetVersionPolicy.Evaluate(uuid, 2, "changed", registrations);
            Equal(AssetVersionDecisionKind.UpgradeRequired, changedWithoutUpgrade.Kind);
            True(!changedWithoutUpgrade.CanExport);
            Equal(3, changedWithoutUpgrade.SuggestedVersion.Value);

            var upgraded = AssetVersionPolicy.Evaluate(uuid, 3, "changed", registrations);
            Equal(AssetVersionDecisionKind.CreateNewVersion, upgraded.Kind);
            True(upgraded.CanExport);

            var duplicateContent = AssetVersionPolicy.Evaluate(uuid, 3, "fingerprint-v1", registrations);
            Equal(AssetVersionDecisionKind.ContentAlreadyRegisteredAtDifferentVersion, duplicateContent.Kind);
            True(!duplicateContent.CanExport);
            Equal(1, duplicateContent.RegisteredVersion.Value);
        }

        private static void AssetsRequiringUpgradeOpenForEditing()
        {
            var root = TempDirectory();
            try
            {
                var assemblyPath = Path.Combine(root, "root.SLDASM");
                var assetPath = Path.Combine(root, "asset.SLDPRT");
                File.WriteAllText(assemblyPath, "root");
                File.WriteAllText(assetPath, "asset");
                var assembly = new FakeSwDocument("root.SLDASM", assemblyPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var asset = new FakeSwDocument("asset.SLDPRT", assetPath, false)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocPART
                };
                var app = new FakeSldWorks(assembly, asset);
                var component = new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                };
                var analysis = new AnalysisResult();
                analysis.AssetInspections.Add("asset", new AssetInspection
                {
                    Node = new SwCadNode(app, component, "/asset"), Version = 1,
                    RequiresVersionUpgrade = true, SuggestedVersion = 2
                });

                var messages = new ExportCoordinator(app).OpenAssetsRequiringVersionUpgrade(analysis);

                Equal(1, messages.Count);
                True(messages[0].Contains("asset_version：v1 → 建议 v2"));
                True(ReferenceEquals(app.ActiveDoc, asset));
                Equal((int)swOpenDocOptions_e.swOpenDocOptions_Silent, app.OpenOptions.Value);
                True(!asset.IsOpenedReadOnly());
            }
            finally { Directory.Delete(root, true); }
        }

        private static void EmbeddedAssetRootIsNeverOpenedForVersionEditing()
        {
            var root = Path.Combine(Path.GetTempPath(), "swx" + Guid.NewGuid().ToString("N"));
            try
            {
                var assemblyPath = Path.Combine(root, "root.SLDASM");
                var virtualDirectory = Path.Combine(root, "VC~~", "owner");
                var assetPath = Path.Combine(virtualDirectory, "asset^owner.SLDASM");
                Directory.CreateDirectory(virtualDirectory);
                File.WriteAllText(assemblyPath, "root");
                File.WriteAllText(assetPath, "asset");
                var assembly = new FakeSwDocument("root.SLDASM", assemblyPath, true)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var asset = new FakeSwDocument("asset^owner.SLDASM", assetPath, false)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(assembly, asset);
                var component = new FakeSwComponent
                {
                    PathValue = assetPath,
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    ModelDocumentWhenResolved = asset
                };
                var analysis = new AnalysisResult();
                analysis.AssetInspections.Add("asset", new AssetInspection
                {
                    Node = new SwCadNode(app, component, "/asset"), Version = 1,
                    RequiresVersionUpgrade = true, SuggestedVersion = 2,
                    IsEmbeddedAssetRoot = true
                });

                var messages = new ExportCoordinator(app).OpenAssetsRequiringVersionUpgrade(analysis);

                Equal(0, messages.Count);
                True(ReferenceEquals(app.ActiveDoc, assembly));
                True(!app.OpenOptions.HasValue);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void UnregisteredLocalAssetConflictIsQuarantined()
        {
            var root = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D");
                var destination = Path.Combine(root, uuid, "v1");
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "old-package.txt"), "old");

                var backup = ExportCoordinator.QuarantineInvalidLocalPackage(root, destination, uuid, 1);

                True(!Directory.Exists(destination));
                True(Directory.Exists(backup));
                Equal("old", File.ReadAllText(Path.Combine(backup, "old-package.txt")));
                True(backup.StartsWith(Path.Combine(root, ".local-package-backups"),
                    StringComparison.OrdinalIgnoreCase));
                Equal(null, ExportCoordinator.QuarantineInvalidLocalPackage(root, destination, uuid, 1));
                Throws<ValidationException>(() => ExportCoordinator.QuarantineInvalidLocalPackage(root,
                    Path.Combine(root, "wrong"), uuid, 1));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void AssetRegistryImportsEveryManifestForUuid()
        {
            var root = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D");
                for (var version = 1; version <= 2; version++)
                {
                    var directory = Path.Combine(root, uuid, "v" + version.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(directory);
                    JsonFile.Write(Path.Combine(directory, "asset_" + uuid + "_v" +
                        version.ToString(CultureInfo.InvariantCulture) + ".json"), new AssetManifest
                    {
                        SchemaVersion = "1.0",
                        Uuid = uuid,
                        Version = version,
                        ContentFingerprint = "fingerprint-v" + version.ToString(CultureInfo.InvariantCulture)
                    });
                }

                Equal(2, AssetRegistryStore.ImportKnownManifestsForUuid(root, uuid));
                var registry = AssetRegistryStore.Load(root);
                Equal(2, registry.Assets.Count);
                Equal(0, AssetRegistryStore.ImportKnownManifestsForUuid(root, uuid));
            }
            finally { Directory.Delete(root, true); }
        }

        private static AssetRegistration Registration(string uuid, int version, string fingerprint)
        {
            return new AssetRegistration
            {
                Uuid = uuid,
                Version = version,
                AssetId = uuid + ":" + version.ToString(CultureInfo.InvariantCulture),
                RelativeDirectory = uuid + "/v" + version.ToString(CultureInfo.InvariantCulture),
                ContentFingerprint = fingerprint
            };
        }

        private static void AssetRegistryMergeRetainsRemoteEntries()
        {
            var local = TempDirectory();
            var remote = TempDirectory();
            try
            {
                var shared = Guid.NewGuid();
                var remoteOnly = Guid.NewGuid();
                AssetRegistryStore.Register(local, shared.ToString("D"), 1, "same-fingerprint");
                AssetRegistryStore.Register(remote, shared.ToString("D"), 1, "same-fingerprint");
                AssetRegistryStore.Register(remote, remoteOnly.ToString("D"), 2, "remote-fingerprint");

                Equal(1, AssetRegistryStore.MergeFromFile(local, Path.Combine(remote, AssetRegistryStore.FileName)));
                var merged = AssetRegistryStore.Load(local);
                Equal(2, merged.Assets.Count);
                True(merged.Assets.Any(value => value.Uuid == remoteOnly.ToString("D") && value.Version == 2));
            }
            finally
            {
                Directory.Delete(local, true);
                Directory.Delete(remote, true);
            }
        }

        private static void AssetRegistryMergeRejectsContentConflicts()
        {
            var local = TempDirectory();
            var remote = TempDirectory();
            try
            {
                var shared = Guid.NewGuid();
                AssetRegistryStore.Register(local, shared.ToString("D"), 1, "local-fingerprint");
                AssetRegistryStore.Register(remote, shared.ToString("D"), 1, "remote-fingerprint");
                Throws<ValidationException>(() => AssetRegistryStore.MergeFromFile(local,
                    Path.Combine(remote, AssetRegistryStore.FileName)));
            }
            finally
            {
                Directory.Delete(local, true);
                Directory.Delete(remote, true);
            }
        }

        private static void WanxiangArchiveUploadContract()
        {
            var temp = TempDirectory();
            try
            {
                Throws<ArgumentException>(() => WanxiangRemotePath.Normalize("/absolute/path"));
                Throws<ArgumentException>(() => WanxiangRemotePath.Normalize("C:/absolute/path"));
                Directory.CreateDirectory(Path.Combine(temp, "nested"));
                File.WriteAllText(Path.Combine(temp, "nested", "visible.txt"), "visible");
                File.WriteAllText(Path.Combine(temp, ".hidden.txt"), "hidden");
                Directory.CreateDirectory(Path.Combine(temp, "#recycle"));
                File.WriteAllText(Path.Combine(temp, "#recycle", "ignored.txt"), "ignored");
                var handler = new RecordingHttpHandler();
                using (var client = new WanxiangDataClient("http://localhost:8100", "secret-key", handler))
                {
                    var result = client.UploadDirectory(temp, "wanxiang_test/资产/v1");
                    Equal("wanxiang_test/资产/v1", result.Path);
                }

                Equal("PUT", handler.Method);
                Equal("Bearer secret-key", handler.Authorization);
                True(handler.Url.Contains("/archive/wanxiang_test/%E8%B5%84%E4%BA%A7/v1"));
                using (var stream = new MemoryStream(handler.Body))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var entries = archive.Entries.Select(value => value.FullName).ToList();
                    Equal(1, entries.Count);
                    Equal("nested/visible.txt", entries[0]);
                }
            }
            finally { Directory.Delete(temp, true); }
        }

        private static void WanxiangAssetPublishContract()
        {
            var uuid = Guid.NewGuid().ToString("D");
            var fingerprint = "atomic-registration-fingerprint";
            var response = "{\"status\":\"registered\",\"registration\":{" +
                "\"asset_id\":\"" + uuid + ":2\",\"uuid\":\"" + uuid + "\",\"version\":2," +
                "\"name\":\"底板\",\"relative_directory\":\"" + uuid +
                "/v2\",\"content_fingerprint\":\"" +
                fingerprint + "\",\"registered_utc\":\"2026-09-02T08:00:00.000000Z\"}}";
            var root = TempDirectory();
            try
            {
                var directory = Path.Combine(root, uuid, "v2");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "asset.txt"), "asset");
                var handler = new ResponseHttpHandler(HttpStatusCode.Created, response);
                using (var client = new WanxiangDataClient("http://localhost:8100", "secret-key", handler))
                {
                    var result = client.PublishAssetVersion(directory, uuid.ToUpperInvariant(), 2, fingerprint);
                    Equal("registered", result.Status);
                    Equal(uuid, result.Registration.Uuid);
                    Equal(2, result.Registration.Version);
                }

                Equal("PUT", handler.Method);
                Equal("Bearer secret-key", handler.Authorization);
                Equal(fingerprint, handler.ContentFingerprint);
                True(handler.Url.EndsWith("/asset/" + uuid + "/v2", StringComparison.Ordinal));
                using (var stream = new MemoryStream(handler.Body))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    Equal("asset.txt", archive.Entries.Single().FullName);

                var alreadyResponse = response.Replace("\"status\":\"registered\"",
                    "\"status\":\"already_registered\"");
                var alreadyHandler = new ResponseHttpHandler(HttpStatusCode.OK, alreadyResponse);
                using (var client = new WanxiangDataClient("http://localhost:8100", "secret-key",
                    alreadyHandler))
                    Equal("already_registered", client.PublishAssetVersion(directory, uuid, 2,
                        fingerprint).Status);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void WanxiangUploadDiagnosticsAreSafe()
        {
            var uuid = Guid.NewGuid().ToString("D");
            var fingerprint = "diagnostic-fingerprint";
            var response = "{\"status\":\"registered\",\"registration\":{" +
                "\"asset_id\":\"" + uuid + ":1\",\"uuid\":\"" + uuid + "\",\"version\":1," +
                "\"name\":\"测试资产\",\"relative_directory\":\"" + uuid +
                "/v1\",\"content_fingerprint\":\"" + fingerprint +
                "\",\"registered_utc\":\"2026-09-04T01:00:00.000000Z\"}}";
            var root = TempDirectory();
            try
            {
                var directory = Path.Combine(root, uuid, "v1");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "asset.txt"), "asset");
                var diagnostics = new List<string>();
                var handler = new ResponseHttpHandler(HttpStatusCode.Created, response);
                using (var client = new WanxiangDataClient("http://localhost:8100", "secret-key",
                    handler, diagnostics.Add))
                {
                    var result = client.PublishAssetVersion(directory, uuid, 1, fingerprint);
                    Equal(201, result.HttpStatusCode);
                }
                True(diagnostics.Any(value => value.Contains("HTTP REQUEST PUT http://localhost:8100/asset/" +
                    uuid + "/v1")));
                True(diagnostics.Any(value => value.Contains("http_status=201") &&
                    value.Contains("elapsed_ms=") && value.Contains("registered")));
                True(!diagnostics.Any(value => value.Contains("secret-key")));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void WanxiangUploadLogIsReadable()
        {
            var root = TempDirectory();
            try
            {
                var log = WanxiangUploadLog.CreateInDirectory(root);
                log.Write("HTTP 201\r\nregistered");
                True(File.Exists(log.Path));
                var content = File.ReadAllText(log.Path);
                True(content.Contains("HTTP 201 registered"));
                True(!content.Contains("HTTP 201\r\nregistered"));
                True(WanxiangUploadLog.SummarizeResponse(new string('x', 900)).Contains("[truncated]"));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void WanxiangExportUsesAtomicPublication()
        {
            var assetRoot = TempDirectory();
            var projectRoot = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D");
                var fingerprint = "export-registration-fingerprint";
                var assetDirectory = Path.Combine(assetRoot, uuid, "v1");
                var projectDirectory = Path.Combine(projectRoot, "assembly", "v1");
                Directory.CreateDirectory(assetDirectory);
                Directory.CreateDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(assetDirectory, "asset.txt"), "asset");
                File.WriteAllText(Path.Combine(projectDirectory, "project.txt"), "project");
                var export = new ExportCompletion { ProjectDirectory = projectDirectory };
                export.AssetDirectories.Add(assetDirectory);
                export.AssetRegistrations.Add(Registration(uuid, 1, fingerprint));
                var settings = WanxiangSettings(assetRoot, projectRoot);
                settings.UploadAfterExport = true;
                var handler = new AtomicUploadHttpHandler(uuid, 1, fingerprint);
                var progress = new List<string>();

                WanxiangUploadCompletion result;
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey, handler))
                    result = new WanxiangExportUploader().Upload(client, export, settings, progress.Add);

                Equal(2, handler.Requests.Count);
                Equal("PUT /asset/" + uuid + "/v1", handler.Requests[0]);
                Equal("PUT /archive/projects/assembly/v1", handler.Requests[1]);
                Equal(1, result.AssetDirectoriesUploaded);
                Equal(1, result.AssetVersionsRegistered);
                Equal(0, result.AssetVersionsAlreadyRegistered);
                Equal("/asset/registry", result.RemoteRegistryPath);
                True(progress.Any(value => value.Contains("Asset 1/1") && value.Contains("HTTP 201")));
                True(progress.Any(value => value.Contains("正在打包 Project")));
                True(progress.Any(value => value.Contains("Project 已收到 HTTP 200")));
                True(progress.Any(value => value.Contains("全部服务器响应均已收到")));
            }
            finally
            {
                Directory.Delete(assetRoot, true);
                Directory.Delete(projectRoot, true);
            }
        }

        private static void WanxiangUploadSkipsDisabledProject()
        {
            var assetRoot = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid().ToString("D");
                var fingerprint = "asset-only-registration-fingerprint";
                var assetDirectory = Path.Combine(assetRoot, uuid, "v1");
                Directory.CreateDirectory(assetDirectory);
                File.WriteAllText(Path.Combine(assetDirectory, "asset.txt"), "asset");
                var export = new ExportCompletion();
                export.AssetDirectories.Add(assetDirectory);
                export.AssetRegistrations.Add(Registration(uuid, 1, fingerprint));
                var settings = WanxiangSettings(assetRoot, string.Empty);
                settings.ExportProject = false;
                settings.UploadAfterExport = true;
                var handler = new AtomicUploadHttpHandler(uuid, 1, fingerprint);

                WanxiangUploadCompletion result;
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl,
                    settings.WanxiangApiKey, handler))
                    result = new WanxiangExportUploader().Upload(client, export, settings);

                Equal(1, handler.Requests.Count);
                Equal("PUT /asset/" + uuid + "/v1", handler.Requests[0]);
                True(string.IsNullOrEmpty(result.RemoteProjectDirectory));
            }
            finally { Directory.Delete(assetRoot, true); }
        }

        private static void WanxiangPreviewUsesRemoteRegistry()
        {
            var assetRoot = TempDirectory();
            var projectRoot = TempDirectory();
            var remoteRoot = TempDirectory();
            try
            {
                var uuid = Guid.NewGuid();
                AssetRegistryStore.Register(remoteRoot, uuid.ToString("D"), 2, "remote-fingerprint");
                var json = File.ReadAllText(Path.Combine(remoteRoot, AssetRegistryStore.FileName));
                File.Delete(Path.Combine(assetRoot, AssetRegistryStore.FileName));
                var settings = WanxiangSettings(assetRoot, projectRoot);
                True(!settings.SaveRegistryLocally);

                var handler = new ResponseHttpHandler(HttpStatusCode.OK, json, true);
                WanxiangRegistrySnapshot snapshot;
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey, handler))
                    snapshot = new WanxiangRegistryProvider().Fetch(client, settings);

                True(snapshot.RemoteRegistryExists);
                Equal(1, snapshot.Registry.Assets.Count);
                Equal("remote-fingerprint", snapshot.Registry.Assets[0].ContentFingerprint);
                True(handler.Url.EndsWith("/asset/registry",
                    StringComparison.Ordinal));
                True(!File.Exists(Path.Combine(assetRoot, AssetRegistryStore.FileName)));

                settings.SaveRegistryLocally = true;
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey,
                    new ResponseHttpHandler(HttpStatusCode.OK, json, true)))
                    new WanxiangRegistryProvider().Fetch(client, settings);
                Equal("remote-fingerprint", AssetRegistryStore.Load(assetRoot).Assets[0].ContentFingerprint);
            }
            finally
            {
                Directory.Delete(assetRoot, true);
                Directory.Delete(projectRoot, true);
                Directory.Delete(remoteRoot, true);
            }
        }

        private static void WanxiangMissingRegistryIsEmpty()
        {
            var assetRoot = TempDirectory();
            var projectRoot = TempDirectory();
            try
            {
                var settings = WanxiangSettings(assetRoot, projectRoot);
                WanxiangRegistrySnapshot snapshot;
                var empty = "{\"schema_version\":\"1.0\",\"assets\":[]}";
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey,
                    new ResponseHttpHandler(HttpStatusCode.OK, empty, false)))
                    snapshot = new WanxiangRegistryProvider().Fetch(client, settings);
                True(!snapshot.RemoteRegistryExists);
                Equal(0, snapshot.Registry.Assets.Count);
                True(!File.Exists(Path.Combine(assetRoot, AssetRegistryStore.FileName)));
            }
            finally
            {
                Directory.Delete(assetRoot, true);
                Directory.Delete(projectRoot, true);
            }
        }

        private static ExporterSettings WanxiangSettings(string assetRoot, string projectRoot)
        {
            return new ExporterSettings
            {
                AssetLibraryRoot = assetRoot,
                ProjectExportRoot = projectRoot,
                WanxiangBaseUrl = "http://localhost:8100",
                WanxiangApiKey = "secret-key"
            };
        }

        private static void DrawingLookupUsesFileSystemOnly()
        {
            var root = TempDirectory();
            try
            {
                var model = Path.Combine(root, "fixture.SLDPRT");
                var drawing = Path.Combine(root, "fixture.SLDDRW");
                File.WriteAllText(model, "model");
                File.WriteAllText(drawing, "drawing");
                var files = new SwDrawingExporter().FindDirectDrawingFiles(new[] { model });
                Equal(1, files.Count);
                Equal(Path.GetFullPath(drawing), files[0]);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void DrawingSourcesAreCopiedWithoutPdf()
        {
            var root = TempDirectory();
            try
            {
                var drawing = Path.Combine(root, "fixture.SLDDRW");
                var destination = Path.Combine(root, "asset", "source");
                File.WriteAllText(drawing, "drawing");

                var copied = new SwDrawingExporter().CopyDrawingSources(new[] { drawing }, destination);

                Equal(1, copied.Count);
                True(File.Exists(Path.Combine(destination, "fixture.SLDDRW")));
                Equal(0, Directory.EnumerateFiles(Path.Combine(root, "asset"), "*.pdf", SearchOption.AllDirectories).Count());
            }
            finally { Directory.Delete(root, true); }
        }

        private static void FileFingerprintCacheReusesUnchangedHashes()
        {
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "fixture.SLDPRT");
                var cachePath = Path.Combine(root, "fingerprints.json");
                File.WriteAllText(source, "first-content");
                string first;
                using (var cache = new FileFingerprintCache(cachePath))
                {
                    first = cache.Sha256(source);
                    Equal(first, cache.Sha256(source));
                    Equal(1, cache.CacheMisses);
                    Equal(1, cache.CacheHits);
                }
                using (var cache = new FileFingerprintCache(cachePath))
                {
                    Equal(first, cache.Sha256(source));
                    Equal(1, cache.CacheHits);
                    File.WriteAllText(source, "second-content-is-longer");
                    var second = cache.Sha256(source);
                    True(!string.Equals(first, second, StringComparison.OrdinalIgnoreCase));
                    Equal(1, cache.CacheMisses);
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void ExportPreviewSnapshotRejectsChangedFiles()
        {
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "fixture.SLDPRT");
                File.WriteAllText(source, "preview-content");
                var snapshot = ExportSourceFileSnapshot.Capture(source, null);
                Equal(string.Empty, snapshot.Sha256);
                snapshot.ValidateUnchanged();

                File.WriteAllText(source, "changed-content-is-longer");
                Throws<ValidationException>(() => snapshot.ValidateUnchanged());
            }
            finally { Directory.Delete(root, true); }
        }

        private static void ProjectPartFingerprintAvoidsPackAndGo()
        {
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "repeated-project-part.SLDPRT");
                File.WriteAllText(source, "project-part-content");
                var component = new FakeSwComponent
                {
                    PathValue = source,
                    ReferencedConfiguration = "Default",
                    ReferencedDisplayState = "Display State-1",
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
                };
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(root, "root.SLDASM"), true);
                var target = new FakeSwDocument("repeated-project-part.SLDPRT", source, false);
                var app = new FakeSldWorks(original, target);
                var node = new SwCadNode(app, component, "/repeated-project-part");
                var classification = node.ClassificationModel;
                Equal(1, app.OpenDocCalls);

                var packager = new SwSourcePackager(app, new SwPluginMutationTracker(app));
                var first = packager.ContentFingerprint(node, FileHash.Sha256);
                var second = packager.ContentFingerprint(node, FileHash.Sha256);
                Equal(first, second);
                Equal(1, app.OpenDocCalls);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void ProjectAssemblyFingerprintAvoidsPackAndGo()
        {
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "project-leaf.SLDASM");
                File.WriteAllText(source, "project-assembly-content");
                var component = new FakeSwComponent
                {
                    PathValue = source,
                    ReferencedConfiguration = "Default",
                    ReferencedDisplayState = "Display State-1",
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
                };
                var original = new FakeSwDocument("root.SLDASM", Path.Combine(root, "root.SLDASM"), true);
                var target = new FakeSwDocument("project-leaf.SLDASM", source, false)
                {
                    DocumentType = (int)swDocumentTypes_e.swDocASSEMBLY
                };
                var app = new FakeSldWorks(original, target);
                var node = new SwCadNode(app, component, "/project-leaf");
                var classification = node.ClassificationModel;
                Equal(1, app.OpenDocCalls);

                var fingerprint = new SwSourcePackager(app, new SwPluginMutationTracker(app))
                    .ContentFingerprint(node, FileHash.Sha256);

                True(!string.IsNullOrWhiteSpace(fingerprint));
                Equal(1, app.OpenDocCalls);
                True(target.ExtensionValue.PackAndGoValue == null);
            }
            finally { Directory.Delete(root, true); }
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
            node.Model.FileProperties[PropertyRules.AssetClass] = "structure";
        }

        private static void MarkRobot(FakeNode node, string designPurpose, int version)
        {
            node.Model.FileProperties[PropertyRules.IsAsset] = "true";
            node.Model.FileProperties[PropertyRules.AssetClass] = "robot";
            node.Model.FileProperties[PropertyRules.DesignPurpose] = designPurpose;
            node.Model.FileProperties[PropertyRules.AssetVersion] =
                version.ToString(CultureInfo.InvariantCulture);
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

    internal sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public string Method { get; private set; }
        public string Url { get; private set; }
        public string Authorization { get; private set; }
        public byte[] Body { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Url = request.RequestUri.AbsoluteUri;
            Authorization = request.Headers.Authorization == null ? string.Empty : request.Headers.Authorization.ToString();
            Body = request.Content == null ? new byte[0] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"path\":\"wanxiang_test/资产/v1\",\"files_extracted\":1,\"bytes_written\":7}")
            };
            return Task.FromResult(response);
        }
    }

    internal sealed class ResponseHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly bool? _registryExists;

        public ResponseHttpHandler(HttpStatusCode statusCode, string body, bool? registryExists = null)
        {
            _statusCode = statusCode;
            _body = body ?? string.Empty;
            _registryExists = registryExists;
        }

        public string Url { get; private set; }
        public string Method { get; private set; }
        public string Authorization { get; private set; }
        public string ContentFingerprint { get; private set; }
        public byte[] Body { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri.AbsoluteUri;
            Method = request.Method.Method;
            Authorization = request.Headers.Authorization == null ? string.Empty : request.Headers.Authorization.ToString();
            IEnumerable<string> fingerprintValues;
            ContentFingerprint = request.Headers.TryGetValues("X-Content-Fingerprint", out fingerprintValues)
                ? fingerprintValues.FirstOrDefault() : string.Empty;
            Body = request.Content == null ? new byte[0] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(_statusCode) { Content = new StringContent(_body) };
            if (_registryExists.HasValue)
            {
                response.Headers.Add("X-Registry-Exists", _registryExists.Value ? "true" : "false");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                    "\"" + FileHash.Sha256Text(_body) + "\"");
            }
            return Task.FromResult(response);
        }
    }

    internal sealed class AtomicUploadHttpHandler : HttpMessageHandler
    {
        private readonly string _uuid;
        private readonly int _version;
        private readonly string _fingerprint;

        public AtomicUploadHttpHandler(string uuid, int version, string fingerprint)
        {
            _uuid = uuid;
            _version = version;
            _fingerprint = fingerprint;
            Requests = new List<string>();
        }

        public IList<string> Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath ==
                "/asset/" + _uuid + "/v" + _version.ToString(CultureInfo.InvariantCulture))
            {
                IEnumerable<string> fingerprintValues;
                if (!request.Headers.TryGetValues("X-Content-Fingerprint", out fingerprintValues) ||
                    !string.Equals(fingerprintValues.FirstOrDefault(), _fingerprint, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)422)
                        { Content = new StringContent("missing fingerprint") });
                var registration = "{\"status\":\"registered\",\"registration\":{" +
                    "\"asset_id\":\"" + _uuid + ":" + _version.ToString(CultureInfo.InvariantCulture) +
                    "\",\"uuid\":\"" + _uuid + "\",\"version\":" +
                    _version.ToString(CultureInfo.InvariantCulture) + ",\"name\":\"asset\",\"relative_directory\":\"" + _uuid +
                    "/v" + _version.ToString(CultureInfo.InvariantCulture) +
                    "\",\"content_fingerprint\":\"" + _fingerprint +
                    "\",\"registered_utc\":\"2026-09-02T08:00:00.000000Z\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                    { Content = new StringContent(registration) });
            }
            var path = request.RequestUri.AbsolutePath.Substring("/archive/".Length);
            var archive = "{\"path\":\"" + path + "\",\"files_extracted\":1,\"bytes_written\":5}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(archive) });
        }
    }

    internal sealed class FakeNode : ICadNode, ICadSourceReference, ICadClassificationSource
    {
        private readonly List<FakeNode> _children = new List<FakeNode>();
        private readonly bool _assembly;
        private readonly ModelDescriptor _model;

        public FakeNode(string name, ModelDescriptor model, bool fixedValue, bool assembly)
        {
            Name = name; InstanceId = name + "-id"; InstancePath = "/" + name; _model = model;
            IsFixedValue = fixedValue; _assembly = assembly; IsVisibleValue = true; WorldTransformValue = Matrix4.Identity;
        }

        public string InstanceId { get; private set; }
        public string Name { get; private set; }
        public string InstancePath { get; set; }
        public bool IsVisible { get { return IsVisibleValue; } }
        public bool IsSuppressed { get { return IsSuppressedValue; } }
        public bool IsEnvelope { get { return IsEnvelopeValue; } }
        public bool IsFixed { get { return IsFixedValue; } }
        public ModelDescriptor Model
        {
            get
            {
                ModelReadCalls++;
                if (ThrowOnModel) throw new Exception("Model metadata must not be read.");
                return _model;
            }
        }
        public string SourcePath { get { return _model.FullPath; } }
        public DocumentKind SourceDocumentKind { get { return _model.DocumentKind; } }
        public ModelDescriptor ClassificationModel { get { return _model; } }
        public Matrix4 WorldTransform { get { return WorldTransformValue; } }
        public bool IsVisibleValue { get; set; }
        public bool IsSuppressedValue { get; set; }
        public bool IsEnvelopeValue { get; set; }
        public bool IsFixedValue { get; set; }
        public bool ThrowOnChildren { get; set; }
        public bool ThrowOnModel { get; set; }
        public int GetChildrenCalls { get; private set; }
        public int ModelReadCalls { get; private set; }
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

    internal sealed class FakeSwComponent : Component2
    {
        public FakeSwComponent()
        {
            PathValue = "C:\\models\\fake-component.SLDPRT";
            ReferencedConfiguration = string.Empty;
            ReferencedDisplayState = string.Empty;
            CustomPropertyManager = new FakePropertyManagerCollection();
            SuppressionChanges = new List<int>();
        }

        public string Name2 { get { return "fake-component"; } }
        public int Visible { get { return VisibleValue; } }
        public int VisibleValue { get; set; }
        public int SuppressionState { get; set; }
        public string PathValue { get; set; }
        public string ReferencedConfiguration { get; set; }
        public string ReferencedDisplayState { get; set; }
        public object ChildrenValue { get; set; }
        public bool IsVirtual { get; set; }
        public ModelDoc2 ModelDocumentWhenResolved { get; set; }
        public IList<int> SuppressionChanges { get; private set; }
        public FakePropertyManagerCollection CustomPropertyManager { get; private set; }
        public MathTransform Transform2 { get { return null; } }
        public int GetID() { return 1; }
        public bool IsSuppressed() { return SuppressionState != (int)swComponentSuppressionState_e.swComponentResolved; }
        public int GetSuppression2() { return SuppressionState; }
        public int SetSuppression2(int state) { SuppressionChanges.Add(state); SuppressionState = state; return 2; }
        public bool IsEnvelope() { return false; }
        public bool IsFixed() { return false; }
        public bool IsHidden(bool considerSuppressed) { return SuppressionState == (int)swComponentSuppressionState_e.swComponentLightweight; }
        public bool Select4(bool append, object data, bool showPopup) { return true; }
        public object GetModelDoc2()
        {
            return SuppressionState == (int)swComponentSuppressionState_e.swComponentResolved ||
                SuppressionState == (int)swComponentSuppressionState_e.swComponentFullyResolved
                ? ModelDocumentWhenResolved : null;
        }
        public object GetChildren() { return ChildrenValue; }
        public string GetPathName() { return PathValue; }
    }

    internal sealed class FakePropertyManagerCollection
    {
        private readonly IDictionary<string, FakeCustomPropertyManager> _managers =
            new Dictionary<string, FakeCustomPropertyManager>(StringComparer.OrdinalIgnoreCase);

        public object this[string configuration] { get { return Manager(configuration); } }

        public FakeCustomPropertyManager Manager(string configuration)
        {
            configuration = configuration ?? string.Empty;
            FakeCustomPropertyManager manager;
            if (!_managers.TryGetValue(configuration, out manager))
            {
                manager = new FakeCustomPropertyManager();
                _managers.Add(configuration, manager);
            }
            return manager;
        }
    }

    internal sealed class FakeCustomPropertyManager : CustomPropertyManager
    {
        public FakeCustomPropertyManager()
        {
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public IDictionary<string, string> Values { get; private set; }
        public bool LastUseCached { get; private set; }
        public int GetNamesCalls { get; private set; }
        public int Get6Calls { get; private set; }

        public object GetNames()
        {
            GetNamesCalls++;
            return Values.Keys.ToArray();
        }

        public int Get6(string name, bool cached, out string raw, out string resolved,
            out bool wasResolved, out bool linked)
        {
            Get6Calls++;
            LastUseCached = cached;
            linked = false;
            if (!Values.TryGetValue(name, out raw))
            {
                raw = string.Empty;
                resolved = string.Empty;
                wasResolved = false;
                return (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent;
            }
            resolved = raw;
            wasResolved = true;
            return (int)swCustomInfoGetResult_e.swCustomInfoGetResult_ResolvedValue;
        }
    }

    internal sealed class FakeSldWorks : SldWorks
    {
        private readonly IDictionary<string, FakeSwDocument> _documents =
            new Dictionary<string, FakeSwDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, FakeSwDocument> _openDocuments =
            new Dictionary<string, FakeSwDocument>(StringComparer.OrdinalIgnoreCase);

        public FakeSldWorks(FakeSwDocument original, params FakeSwDocument[] documents)
        {
            ClosedTitles = new List<string>();
            QuitTitles = new List<string>();
            DocumentsVisible = true;
            Add(original);
            foreach (var document in documents) Add(document);
            ActiveDoc = original;
            _openDocuments[original.GetPathName()] = original;
            foreach (var document in documents)
                if (document.Visible) _openDocuments[document.GetPathName()] = document;
        }

        public IList<string> ClosedTitles { get; private set; }
        public IList<string> QuitTitles { get; private set; }
        public int ActivationErrors { get; set; }
        public bool IgnoreCloseDoc { get; set; }
        public bool RetainDocumentInMemoryOnQuit { get; set; }
        public bool DocumentsVisible { get; private set; }
        public int? OpenOptions { get; private set; }
        public int OpenDocCalls { get; private set; }

        public override object GetOpenDocumentByName(string path)
        {
            FakeSwDocument document;
            return _openDocuments.TryGetValue(path, out document) ? document : null;
        }

        public override bool GetDocumentVisible(int type) { return DocumentsVisible; }
        public override void DocumentVisible(bool visible, int type) { DocumentsVisible = visible; }

        public override object OpenDoc6(string path, int type, int options, string configuration, ref int errors, ref int warnings)
        {
            OpenDocCalls++;
            FakeSwDocument document;
            if (!_documents.TryGetValue(path, out document))
            {
                errors = 2;
                return null;
            }
            errors = 0;
            warnings = 0;
            OpenOptions = options;
            document.OpenedReadOnly = (options & (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly) != 0;
            document.Visible = DocumentsVisible;
            _openDocuments[path] = document;
            return document;
        }

        public override object ActivateDoc3(string title, bool usePreferences, int option, ref int errors)
        {
            FakeSwDocument document;
            if (!_documents.TryGetValue(title, out document))
            {
                errors = 1;
                return null;
            }
            errors = ActivationErrors;
            document.Visible = true;
            ActiveDoc = document;
            return document;
        }

        public override void CloseDoc(string title)
        {
            ClosedTitles.Add(title);
            if (IgnoreCloseDoc) return;
            FakeSwDocument document;
            if (_documents.TryGetValue(title, out document))
            {
                document.Visible = false;
                _openDocuments.Remove(document.GetPathName());
            }
        }

        public override void QuitDoc(string title)
        {
            QuitTitles.Add(title);
            FakeSwDocument document;
            if (_documents.TryGetValue(title, out document))
            {
                document.Visible = false;
                if (!RetainDocumentInMemoryOnQuit) _openDocuments.Remove(document.GetPathName());
            }
        }

        private void Add(FakeSwDocument document)
        {
            _documents[document.GetTitle()] = document;
            _documents[document.GetPathName()] = document;
        }
    }

    internal sealed class FakeSwDocument : ModelDoc2
    {
        private readonly string _title;
        private readonly string _path;

        public FakeSwDocument(string title, string path, bool visible)
        {
            _title = title;
            _path = path;
            Visible = visible;
            ExtensionValue = new FakeModelDocExtension();
        }

        public bool Visible { get; set; }
        public bool OpenedReadOnly { get; set; }
        public bool SaveFlag { get; set; }
        public int UpdateStamp { get; set; }
        public int DocumentType { get; set; }
        public FakeModelDocExtension ExtensionValue { get; private set; }
        public ConfigurationManager ConfigurationManager { get { return null; } }
        public ModelDocExtension Extension { get { return ExtensionValue; } }
        public object SelectionManager { get { return null; } }
        int ModelDoc2.GetType() { return DocumentType == 0 ? (int)swDocumentTypes_e.swDocPART : DocumentType; }
        public string GetPathName() { return _path; }
        public string GetTitle() { return _title; }
        public bool GetSaveFlag() { return SaveFlag; }
        public int GetUpdateStamp() { return UpdateStamp; }
        public bool IsOpenedReadOnly() { return OpenedReadOnly; }
        public string get_SummaryInfo(int fieldId) { return string.Empty; }
        public void ClearSelection2(bool all) { }
        public bool ShowConfiguration2(string name) { return true; }
    }

    internal sealed class FakeModelDocExtension : ModelDocExtension
    {
        public FakeModelDocExtension()
        {
            CustomPropertyManager = new FakePropertyManagerCollection();
        }

        public FakePropertyManagerCollection CustomPropertyManager { get; private set; }
        public FakePackAndGo PackAndGoValue { get; set; }
        public PackAndGo GetPackAndGo() { return PackAndGoValue; }
        public object SavePackAndGo(PackAndGo value)
        {
            return ((FakePackAndGo)value).Save();
        }
        public bool SaveAs(string name, int version, int options, object exportData,
            ref int errors, ref int warnings) { return false; }
        public bool SaveAs3(string name, int version, int options, object exportData,
            object advancedSaveAsOptions, ref int errors, ref int warnings) { return false; }
    }

    internal sealed class FakePackAndGo : PackAndGo
    {
        private readonly Func<string[]> _documentNames;
        private string _destination;
        private string[] _documentSaveNames;

        public FakePackAndGo(Func<string[]> documentNames)
        {
            _documentNames = documentNames;
            OmittedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IncludeDrawings { get; set; }
        public bool IncludeSuppressed { get; set; }
        public bool IncludeToolboxComponents { get; set; }
        public bool IncludeSimulationResults { get; set; }
        public bool FlattenToSingleFolder { get; set; }
        public bool GetDocumentNamesCalledWhileAssetActive { get; private set; }
        public string[] DocumentSaveNames { get { return _documentSaveNames; } }
        public bool SaveDestinationWasSetAfterDocumentNames { get; private set; }
        public bool DocumentSaveNamesWerePassedAsObjectArray { get; private set; }
        public int SetDocumentSaveToNamesCalls { get; private set; }
        public int? ReturnedStatusCount { get; set; }
        public ISet<string> OmittedSourcePaths { get; private set; }

        public bool GetDocumentNames(out object names)
        {
            var values = _documentNames();
            GetDocumentNamesCalledWhileAssetActive = values.Length == 1;
            names = values.Cast<object>().ToArray();
            return true;
        }

        public bool SetDocumentSaveToNames(object names)
        {
            SetDocumentSaveToNamesCalls++;
            DocumentSaveNamesWerePassedAsObjectArray = names != null && names.GetType() == typeof(object[]);
            var strings = names as string[];
            if (strings != null)
            {
                _documentSaveNames = strings.ToArray();
                return true;
            }
            var objects = names as object[];
            if (objects == null) return false;
            _documentSaveNames = objects.Select(Convert.ToString).ToArray();
            return true;
        }

        public bool GetDocumentSaveToNames(out object names, out object statuses)
        {
            var sourceNames = _documentNames();
            var saveNames = _documentSaveNames ?? sourceNames;
            names = saveNames.Select(SavePath).Cast<object>().ToArray();
            statuses = new int[sourceNames.Length];
            return true;
        }

        public bool SetSaveToName(bool value, string path)
        {
            SaveDestinationWasSetAfterDocumentNames = _documentSaveNames != null;
            _destination = path;
            return value;
        }

        public bool SetSaveToName2(bool value, string path)
        {
            return SetSaveToName(value, path);
        }

        public int[] Save()
        {
            var names = _documentNames();
            var saveNames = _documentSaveNames ?? names;
            var statuses = new int[ReturnedStatusCount ?? names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var saveName = saveNames[i];
                if (string.IsNullOrWhiteSpace(saveName))
                {
                    if (i < statuses.Length) statuses[i] = -1;
                    continue;
                }
                var destination = SavePath(saveName);
                if (OmittedSourcePaths.Contains(names[i])) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(names[i], destination, false);
                if (i < statuses.Length)
                    statuses[i] = (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed;
            }
            return statuses;
        }


        private string SavePath(string saveName)
        {
            return string.IsNullOrWhiteSpace(_destination)
                ? saveName
                : Path.Combine(_destination, Path.GetFileName(saveName));
        }
    }
}
