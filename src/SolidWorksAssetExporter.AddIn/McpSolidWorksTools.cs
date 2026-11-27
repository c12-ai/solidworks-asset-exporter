using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed partial class McpSolidWorksTools
    {
        private readonly SldWorks _app;
        private readonly ExportCoordinator _coordinator;
        private readonly Func<bool> _uiBusy;
        private readonly Func<AssetRegistryDocument> _readRegistry;
        private readonly string _propertyBackupRoot;
        internal bool Running { get; private set; }

        internal McpSolidWorksTools(SldWorks app, Func<bool> uiBusy,
            Func<AssetRegistryDocument> readRegistry = null, string propertyBackupRoot = null)
        {
            _app = app;
            _coordinator = new ExportCoordinator(app);
            _uiBusy = uiBusy;
            _readRegistry = readRegistry ?? WanxiangRegistryProvider.FetchForPropertyEdit;
            _propertyBackupRoot = propertyBackupRoot;
        }

        internal async Task<object> Call(string name, Dictionary<string, object> args)
        {
            if (Running || _uiBusy()) throw new ValidationException("Asset Exporter 正在执行另一项操作，请在该操作结束后重试。");
            Running = true;
            try
            {
                if (name == "interface_catalog" || name == "interface_catalog_add")
                {
                    var catalog = new SharedInterfaceCatalog();
                    var result = await Task.Run(() => name == "interface_catalog_add"
                        ? catalog.Merge(((object[])args["names"]).Cast<string>())
                        : catalog.Load());
                    return new { interfaces = result.Names, source = result.Source, warning = result.Warning };
                }
                var document = ActiveAssembly();
                if (name == "assembly_context")
                    return new
                    {
                        document_path = document.GetPathName(), configuration = Configuration(document),
                        dirty = IsDirty(document), read_only = document.IsOpenedReadOnly(),
                        pending_bindings = SwInstanceBindings.HasPending(document)
                    };
                RequireContext(document, args);
                switch (name)
                {
                    case "asset_list": return ListAssets(args, false);
                    case "component_list": return ListComponents(args);
                    case "asset_properties_plan": return PrepareAssetProperties(document, args).Describe(false);
                    case "asset_properties_apply": return await ApplyAssetProperties(document, args);
                    case "binding_evidence": return ListAssets(args, true);
                    case "binding_list": return ListBindings(document, args);
                    case "binding_add": return await AddBindings(document, args);
                    case "binding_plan": return PrepareBinding(document, args, true).Describe();
                    case "binding_apply": return await ApplyBinding(document, args);
                    case "binding_duplicate_plan": return DuplicateBinding(document, args, false);
                    case "binding_duplicate_apply": return DuplicateBinding(document, args, true);
                    case "asset_update_interfaces": return await UpdateInterfaces(document, args);
                    case "asset_export_preview": return await Export(document, args, true);
                    case "asset_export": return await Export(document, args, false);
                    default: throw new ArgumentException("未知 MCP 工具：" + name);
                }
            }
            finally { Running = false; }
        }

        private ModelDoc2 ActiveAssembly()
        {
            var doc = _app.ActiveDoc as ModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new ValidationException("请在 SOLIDWORKS 中激活一个已保存的装配体。");
            return doc;
        }

        private static string Configuration(ModelDoc2 doc)
        {
            var configuration = doc.ConfigurationManager.ActiveConfiguration;
            if (configuration == null) throw new ValidationException("装配没有活动配置。");
            return configuration.Name;
        }

        private void RequireContext(ModelDoc2 expected, IDictionary<string, object> args)
        {
            var active = ActiveAssembly();
            if (!Equals(active, expected) || string.IsNullOrWhiteSpace(active.GetPathName()) ||
                !string.Equals(active.GetPathName(), (string)args["document_path"], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Configuration(active), (string)args["configuration"], StringComparison.Ordinal))
                throw new ValidationException("活动装配或配置已切换，请重新读取 assembly_context。");
        }

        private IList<ScanNode> ScanAssets()
        {
            var root = new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(_app, _coordinator.MutationTracker));
            return Assets(root).OrderBy(node => Instance(node), StringComparer.Ordinal).ToList();
        }

        private bool IsDirty(ModelDoc2 doc) { return _coordinator.MutationTracker.IsDirty(doc, false, doc.GetPathName()); }

        private static IEnumerable<ScanNode> Assets(ScanNode node)
        {
            if (node.Classification == ScanClassification.AssetBoundary) yield return node;
            foreach (var child in node.Children)
                foreach (var asset in Assets(child)) yield return asset;
        }

        private static string Instance(ScanNode node) { return node.Source.InstanceId == "root" ? "" : node.Source.Name; }

        private static ScanNode FindAsset(IEnumerable<ScanNode> assets, string instance)
        {
            var node = assets.SingleOrDefault(asset => string.Equals(Instance(asset), instance, StringComparison.Ordinal));
            if (node == null) throw new ValidationException("找不到资产实例 [" + instance + "]，请重新调用 asset_list。");
            return node;
        }

        private static object Page<T>(IList<T> items, IDictionary<string, object> args, Func<T, object> map)
        {
            var offset = (int?)McpProtocol.Value(args, "offset") ?? 0;
            var limit = (int?)McpProtocol.Value(args, "limit") ?? 100;
            if (limit < 1 || limit > 500) throw new ArgumentException("limit 必须在 1 到 500 之间。");
            var page = items.Skip(offset).Take(limit).Select(map).ToArray();
            int? next = offset < items.Count && items.Count - offset > page.Length ? offset + page.Length : (int?)null;
            return new { total = items.Count, next_offset = next, items = page };
        }

        private object ListAssets(IDictionary<string, object> args, bool includeEvidence)
        {
            var assets = ScanAssets();
            var instances = includeEvidence
                ? new HashSet<string>(assets.Select(Instance).Where(instance => instance.Length != 0), StringComparer.Ordinal)
                : null;
            return Page(assets, args, asset =>
            {
                var source = (SwCadNode)asset.Source;
                var properties = PropertyRules.Merge(source.Model);
                var robot = PropertyRules.IsRobotClass(properties);
                var capacity = PropertyRules.ReadSlotCapacity(properties);
                var description = new Dictionary<string, object>
                {
                    { "instance", Instance(asset) }, { "display_path", source.InstancePath }, { "source_path", source.SourcePath },
                    { "asset_id", robot ? PropertyRules.BuildRobotAssetId(properties, source.Name) :
                        IdentityService.AssetId(IdentityService.AssetUuid(source.Model), asset.AssetVersion.Value) },
                    { "asset_version", robot ? PropertyRules.RequirePositiveInteger(properties, PropertyRules.AssetVersion, source.Name) : asset.AssetVersion.Value },
                    { "role", PropertyRules.DescribeAssetConnectionRole(properties) },
                    { "asset_class", PropertyValue(properties, PropertyRules.AssetClass) },
                    { "part_name", PropertyValue(properties, PropertyRules.PartName) },
                    { "design_purpose", PropertyValue(properties, PropertyRules.DesignPurpose) },
                    { "can_hold_contents", PropertyRules.ReadCanContain(properties) },
                    { "connection_interface", PropertyRules.MechanicalConnectionInterface(PropertyValue(properties, PropertyRules.ConnectionInterface)) },
                    { "accepts_interfaces", PropertyRules.MechanicalInterfaceNames(PropertyValue(properties, PropertyRules.AcceptsInterfaces)) },
                    { "slots_num", capacity },
                    { "engagement", PropertyValue(properties, PropertyRules.AssetEngagement) },
                    { "binding_endpoint", !robot && Instance(asset).Length != 0 && !source.Component.IsVirtual }
                };
                if (capacity > 0) description.Add("slot_type", PropertyRules.ReadAssetSlotType(properties));
                if (!includeEvidence) return description;
                return new { asset = description, evidence = McpBindingEvidence.Describe(source, instances) };
            });
        }

        private object ListBindings(ModelDoc2 doc, IDictionary<string, object> args)
        {
            var session = SwInstanceBindings.ForDocument(doc);
            var records = session.Current.Relations;
            var operation = records.Count == 0 ? null : new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
            var multiple = InstanceBindingRules.MultipleParentBindingIds(records);
            return Page(records, args, record => DescribeBinding(operation, record, multiple.Contains(record.Id)));
        }

        private static object DescribeBinding(SwBindingEndpoints operation, InstanceBinding record, bool legacyMultipleParents = false)
        {
            var parentName = record.Parent.DisplayName;
            var childName = record.Child.DisplayName;
            string issue = null;
            try
            {
                var parent = operation.Resolve(record.Parent);
                parentName = parent.Source.Name;
                var child = operation.Resolve(record.Child);
                childName = child.Source.Name;
                InstanceBindingRules.ValidateConnection(parent, child);
            }
            catch (ValidationException ex) { issue = ex.Message; }
            catch (System.Runtime.InteropServices.COMException ex) { issue = ex.Message; }
            if (legacyMultipleParents)
                issue = (issue == null ? "" : issue + "\n") + "历史关系中此实例有多个承载父组件，请原位复制并迁移。";
            return new { id = record.Id, parent = parentName, child = childName, validation_issue = issue, legacy_multiple_parents = legacyMultipleParents,
                slot_id = record.SlotId, slot_pose = DescribeRelationPose(record.SlotPose), slot_offset = DescribeRelationPose(record.SlotOffset) };
        }

        private static object DescribeRelationPose(RelationPose pose)
        {
            return pose == null ? null : new { tx = pose.Tx, ty = pose.Ty, tz = pose.Tz, qx = pose.Qx, qy = pose.Qy, qz = pose.Qz, qw = pose.Qw };
        }

        private async Task<object> AddBindings(ModelDoc2 doc, IDictionary<string, object> args)
        {
            var session = SwInstanceBindings.ForDocument(doc);
            session.RequireWritable();
            session.RequireSaved();
            if (IsDirty(doc)) throw new ValidationException("装配有未保存修改，请先保存装配。");
            var operation = new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
            var endpoints = CaptureBindingGroup(doc, operation, args);
            var records = session.Current.Relations;
            var revision = session.Revision;
            var proposed = InstanceBindingRules.Apply(records, endpoints.Item1, endpoints.Item2, operation.Resolve, false,
                (string)McpProtocol.Value(args, "reuse_slot_from_binding_id"));
            RequireContext(doc, args);
            var existing = new HashSet<string>(records.Select(record => record.Id), StringComparer.Ordinal);
            var added = proposed.Where(record => !existing.Contains(record.Id)).ToList();
            var parent = operation.Resolve(endpoints.Item1);
            var requiredSlots = session.RequiredSlotCapacity(endpoints.Item1.SourcePath, proposed);
            var occupiedSlots = SwInstanceBindings.CountBoundSlots(proposed.Where(record => operation.Resolve(record.Parent).Id == parent.Id));
            var requestedCapacity = (int?)McpProtocol.Value(args, "slots_num");
            var capacity = requestedCapacity ?? PropertyRules.ReadSlotCapacity(parent.Properties);
            string sourceSaved = null;
            if (requestedCapacity.HasValue)
            {
                var asset = new InterfaceAsset((SwCadNode)parent.Source, _propertyBackupRoot);
                asset.Reload();
                var changes = InterfaceEditRules.Changes(asset.Snapshot, null, null, requestedCapacity, asset.Snapshot.Version, true);
                if (changes.Count > 0)
                {
                    asset.ValidateForSave(changes);
                    var stamp = doc.GetUpdateStamp();
                    var registry = await Task.Run(_readRegistry);
                    RequireContext(doc, args);
                    if (doc.GetUpdateStamp() != stamp || session.Revision != revision || IsDirty(doc))
                        throw new ValidationException("查询发布版本期间装配已改变，请重新调用 binding_add。");
                    var next = InterfaceEditRules.NextVersionIfPublished(registry.Assets, parent.GeometryUuid, asset.Snapshot.Version);
                    if (next.HasValue)
                        changes = InterfaceEditRules.Changes(asset.Snapshot, null, null, requestedCapacity, next.Value, false);
                    asset.Save(changes);
                    sourceSaved = asset.Path;
                }
            }
            if (added.Count != 0)
            {
                try
                {
                    RequireContext(doc, args);
                    if (session.Revision != revision) throw new ValidationException("承载关系已改变，请重新调用 binding_add。");
                    session.Commit(proposed);
                    session.Save(true);
                }
                catch (Exception ex)
                {
                    if (sourceSaved == null) throw;
                    throw new ValidationException(ex.Message + "\n已保存槽位容量的源文件：" + sourceSaved);
                }
            }
            return new { added = added.Count, total = proposed.Count, saved = !session.Pending,
                slots_num = capacity, occupied_slots = occupiedSlots, required_slots = requiredSlots, capacity_source_saved = sourceSaved,
                bindings = added.Select(record => DescribeBinding(operation, record)).ToArray() };
        }

        private static Tuple<BindingEndpoint, BindingEndpoint[]> CaptureBindingGroup(ModelDoc2 doc, SwBindingEndpoints operation,
            IDictionary<string, object> args)
        {
            var names = ((object[])args["children"]).Cast<string>().ToArray();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
                throw new ValidationException("children 必须包含至少一个装配内的资产实例。");
            var parentName = (string)args["parent"];
            if (string.IsNullOrWhiteSpace(parentName))
                throw new ValidationException("parent 必须为装配内的资产实例。");
            var requested = new HashSet<string>(names, StringComparer.Ordinal) { parentName };
            var components = (((AssemblyDoc)doc).GetComponents(false) as object[] ?? new object[0])
                .OfType<Component2>().Where(component => requested.Contains(component.Name2))
                .ToDictionary(component => component.Name2, StringComparer.Ordinal);
            var parent = Capture(operation, FindComponent(components, parentName));
            var children = names.Distinct(StringComparer.Ordinal)
                .Select(name => Capture(operation, FindComponent(components, name))).ToArray();
            return Tuple.Create(parent, children);
        }

        private static Component2 FindComponent(IDictionary<string, Component2> components, string name)
        {
            Component2 component;
            if (!components.TryGetValue(name, out component))
                throw new ValidationException("找不到组件实例 [" + name + "]，请重新调用 asset_list。");
            return component;
        }

        private static BindingEndpoint Capture(SwBindingEndpoints operation, Component2 component)
        {
            if (component == null || component.IsVirtual)
                throw new ValidationException("承载端点须为外部普通资产实例。");
            var asset = operation.FindSelectedAsset(component);
            if (!string.Equals(asset.Source.Name, component.Name2, StringComparison.Ordinal))
                throw new ValidationException("组件 [" + component.Name2 + "] 位于其他资产内部，请使用外层资产实例。");
            return operation.Capture(asset);
        }

        private async Task<object> UpdateInterfaces(ModelDoc2 doc, IDictionary<string, object> args)
        {
            var assetNode = FindAsset(ScanAssets(), (string)args["instance"]);
            var robot = PropertyRules.IsRobotClass(assetNode.Properties);
            var source = (SwCadNode)assetNode.Source;
            if (!string.Equals(source.SourcePath, (string)args["source_path"], StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("实例的源文件已改变，请重新调用 asset_list。");
            var asset = new InterfaceAsset(source, _propertyBackupRoot);
            asset.Reload();
            var connection = (string)McpProtocol.Value(args, "connection_interface");
            var accepts = (object[])McpProtocol.Value(args, "accepts_interfaces");
            var names = accepts == null ? null : accepts.Cast<string>().Select(InterfaceEditRules.MechanicalName).ToList();
            var slots = (int?)McpProtocol.Value(args, "slots_num");
            var sync = (bool?)McpProtocol.Value(args, "sync_accepts_from_bindings") == true;
            var session = !robot && sync ? SwInstanceBindings.ForDocument(doc) : null;
            if (session != null) session.RequireSaved();
            if (sync)
            {
                if (robot) throw new ValidationException("Robot 不参与承载关系，请提供已确认的 accepts_interfaces。");
                var operation = new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
                var parent = Capture(operation, source.Component);
                var children = session.Current.Relations.Where(record => record.Parent.Context == parent.Context)
                    .Select(record => operation.Resolve(record.Child)).Select(child => new
                    {
                        Child = child,
                        Connection = PropertyRules.MechanicalConnectionInterface(PropertyValue(child.Properties, PropertyRules.ConnectionInterface))
                    }).ToList();
                var missing = children.Where(child => child.Connection.Length == 0).ToList();
                if (missing.Count != 0)
                    throw new ValidationException("请先维护以下已绑定子组件的 connection_interface：" +
                        string.Join("、", missing.Select(child => child.Child.Source.Name)));
                var connections = children.Select(child => child.Connection);
                var existing = names == null ? asset.Snapshot.Get(PropertyRules.AcceptsInterfaces) : string.Join(";", names);
                names = InterfaceEditRules.Suggest(existing, connections);
            }
            var currentVersion = asset.Snapshot.Version;
            var edits = InterfaceEditRules.Changes(asset.Snapshot, connection, names, slots, currentVersion, true);
            if (edits.Count != 0 && !robot)
            {
                var stamp = doc.GetUpdateStamp();
                var bindingRevision = session == null ? 0 : session.Revision;
                var registry = await Task.Run(_readRegistry);
                RequireContext(doc, args);
                if (doc.GetUpdateStamp() != stamp || (session != null && session.Revision != bindingRevision))
                    throw new ValidationException("查询发布版本期间装配已变化，请重新调用 asset_list。");
                var next = InterfaceEditRules.NextVersionIfPublished(registry.Assets,
                    IdentityService.AssetUuid(source.Model).ToString("D"), currentVersion);
                if (next.HasValue) edits = InterfaceEditRules.Changes(asset.Snapshot, connection, names, slots, next.Value, false);
            }
            RequireContext(doc, args);
            asset.Save(edits);
            return new
            {
                source_path = asset.Path, changed = edits.Keys.ToArray(), asset_version = asset.Snapshot.Version,
                connection_interface = PropertyRules.MechanicalConnectionInterface(asset.Snapshot.Get(PropertyRules.ConnectionInterface)),
                accepts_interfaces = PropertyRules.MechanicalInterfaceNames(asset.Snapshot.Get(PropertyRules.AcceptsInterfaces)),
                slots_num = PropertyRules.ReadSlotCapacity(asset.Snapshot.Values),
                assembly_dirty = IsDirty(doc)
            };
        }

        private static ExporterSettings LoadSettings()
        {
            var settings = new SettingsStore().Load();
            settings.WanxiangApiKey = new WanxiangApiKeyStore().Load();
            return settings;
        }

        private async Task<object> Export(ModelDoc2 doc, IDictionary<string, object> args, bool preview)
        {
            var settings = LoadSettings();
            settings.ExportProject = (bool?)McpProtocol.Value(args, "export_project") ?? settings.ExportProject;
            settings.UploadAfterExport = !preview && (bool?)McpProtocol.Value(args, "upload") == true;
            if (preview) settings.SaveRegistryLocally = false;
            if (settings.UploadAfterExport && settings.ExportProject && settings.NotifyAssemblyParser)
                settings.AssemblyParserProviderToken = new AssemblyParserTokenStore().Load();
            settings.ValidateAssemblyParser();
            var registry = await Task.Run(() => _coordinator.FetchWanxiangRegistry(settings));
            RequireContext(doc, args);
            var analysis = _coordinator.Analyze(settings, registry);
            if (preview)
                return new
                {
                    can_export = analysis.CanExport, preview = analysis.Preview, validation_errors = analysis.ValidationErrors,
                    placement_diagnostics = DescribePlacementDiagnostics(analysis),
                    contact_diagnostics = DescribeContactDiagnostics(analysis.ContactDiagnostics),
                    can_prepare_versions = SwManagedVersions.CanPrepare(analysis),
                    suggested_assembly_version = analysis.SuggestedProjectVersion,
                    suggested_asset_versions = analysis.AssetInspections.Values.Where(asset => SwManagedVersions.SuggestedVersion(asset).HasValue)
                        .Select(asset => new { source_path = asset.SourcePath, version = SwManagedVersions.SuggestedVersion(asset).Value }).ToArray()
                };
            var versions = new List<string>();
            analysis = new SwManagedVersions(_app).Prepare(_coordinator, settings, registry, analysis, versions);
            RequireContext(doc, args);
            if (!analysis.CanExport)
                return new { exported = false, preview = analysis.Preview, validation_errors = analysis.ValidationErrors,
                    placement_diagnostics = DescribePlacementDiagnostics(analysis),
                    contact_diagnostics = DescribeContactDiagnostics(analysis.ContactDiagnostics), versions_saved = versions };
            var completion = _coordinator.Export(analysis, settings);
            WanxiangUploadCompletion upload = null;
            string uploadError = null;
            if (settings.UploadAfterExport)
            {
                try { upload = await Task.Run(() => new WanxiangExportUploader().Upload(completion, settings)); }
                catch (Exception ex) { uploadError = ex.Message; }
            }
            return new
            {
                exported = true, project_directory = completion.ProjectDirectory, project_reused = completion.ProjectReused,
                asset_directories = completion.AssetDirectories, created_assets = completion.CreatedAssets,
                reused_assets = completion.ReusedAssets, versions_saved = versions, local_package_backups = completion.LocalPackageBackups,
                placement_diagnostics = DescribePlacementDiagnostics(analysis),
                contact_diagnostics = DescribeContactDiagnostics(analysis.ContactDiagnostics),
                upload = upload, upload_error = uploadError
            };
        }

        private static object DescribePlacementDiagnostics(AnalysisResult analysis)
        {
            return analysis.PlacementDiagnostics.Select(value => new
                { code = value.Code, blocking = value.IsBlocking, message = value.Message,
                    review_key = value.ReviewKey, review_reason = value.ReviewReason }).ToArray();
        }

        private static object DescribeContactDiagnostics(IEnumerable<ContactDiagnostic> diagnostics)
        {
            return diagnostics.Select(value => new
            {
                code = value.Code, needs_review = value.NeedsReview, message = value.Message,
                parent = value.Parent, child = value.Child, representative_count = value.RepresentativeCount,
                gap_mm = value.GapMillimeters, overlap_mm3 = value.OverlapCubicMillimeters
            }).ToArray();
        }

        private static string PropertyValue(IDictionary<string, string> properties, string name)
        {
            string value;
            return properties.TryGetValue(name, out value) ? value : "";
        }

    }
}
