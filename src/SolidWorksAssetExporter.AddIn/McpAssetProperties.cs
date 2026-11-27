using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed partial class McpSolidWorksTools
    {
        private sealed class ComponentEntry
        {
            internal SwCadNode Source;
            internal ModelDescriptor Model;
            internal IDictionary<string, string> Properties;
            internal string Issue;
            internal string Owner;
            internal string Instance { get { return Source.InstanceId == "root" ? "" : Source.Name; } }

            internal void Read(bool includeAllProperties)
            {
                try
                {
                    var model = includeAllProperties ? Source.Model : Source.ClassificationModel;
                    Properties = PropertyRules.Merge(model);
                    Model = model;
                }
                catch (ValidationException ex) { Issue = ex.Message; }
                catch (COMException ex) { Issue = ex.Message; }
                catch (RuntimeBinderException ex) { Issue = ex.Message; }
                catch (System.IO.IOException ex) { Issue = ex.Message; }
                catch (UnauthorizedAccessException ex) { Issue = ex.Message; }
            }

            internal object Describe()
            {
                var state = "unavailable";
                var issue = Issue;
                bool? canContain = null;
                if (Properties != null)
                {
                    try
                    {
                        var asset = McpAssetPropertyRules.ParseFlag(PropertyValue(Properties, PropertyRules.IsAsset), PropertyRules.IsAsset);
                        state = asset == true ? "asset" : PropertyRules.ReadIsWorkstation(Properties, Source.SourceDocumentKind)
                            ? "workstation" : asset == false ? "non_asset" : "unclassified";
                        if (issue == null) canContain = PropertyRules.ReadCanContain(Properties);
                    }
                    catch (ValidationException ex) { issue = ex.Message; }
                }
                return new
                {
                    instance = Instance, cad_owner = Owner, display_path = Source.InstancePath,
                    source_path = Source.SourcePath, document_kind = Source.SourceDocumentKind.ToString().ToLowerInvariant(),
                    referenced_configuration = Model == null ? null : Model.Configuration,
                    classification = state, can_hold_contents = canContain, dirty = Model != null && Model.IsDirty,
                    is_virtual = Model != null && Model.IsVirtualComponent, read_issue = issue,
                    properties = Properties == null ? null : Properties.Where(pair =>
                        (!pair.Key.Equals(PropertyRules.SlotType, StringComparison.OrdinalIgnoreCase) ||
                         PropertyRules.ReadSlotCapacity(Properties) > 0) &&
                        (McpAssetPropertyRules.Fields.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) ||
                         pair.Key.Equals(PropertyRules.LegacyAcceptsInterface, StringComparison.OrdinalIgnoreCase)))
                        .ToDictionary(pair => pair.Key, pair => pair.Value)
                };
            }
        }

        private IEnumerable<ComponentEntry> ReadComponents(SwCadNode source, string owner)
        {
            if (!AssemblyScanner.IsIncluded(source)) yield break;
            var entry = new ComponentEntry { Source = source, Owner = owner };
            entry.Read(false);
            yield return entry;
            if (entry.Properties == null || PropertyRules.ReadIsAsset(entry.Properties)) yield break;
            foreach (var child in source.GetChildren().Cast<SwCadNode>())
                foreach (var descendant in ReadComponents(child, entry.Instance))
                    yield return descendant;
        }

        private IList<ComponentEntry> Inventory()
        {
            return ReadComponents(SwAssemblyRoot.FromActiveDocument(_app, _coordinator.MutationTracker), null)
                .OrderBy(entry => entry.Instance, StringComparer.Ordinal).ToList();
        }

        private static string[] SourceOccurrences(ModelDoc2 document, ComponentEntry entry)
        {
            if (entry.Instance.Length == 0) return new[] { "" };
            var components = ((AssemblyDoc)document).GetComponents(false) as object[] ?? new object[0];
            return components.OfType<Component2>()
                .Where(component => string.Equals(component.GetPathName(), entry.Source.SourcePath, StringComparison.OrdinalIgnoreCase))
                .Select(component => component.Name2).Concat(new[] { entry.Instance })
                .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private object ListComponents(IDictionary<string, object> args)
        {
            return Page(Inventory(), args, entry =>
            {
                if (entry.Properties != null) entry.Read(true);
                return entry.Describe();
            });
        }

        private sealed class AssetPropertyPlan
        {
            internal InterfaceAsset Asset;
            internal Dictionary<string, string> Changes;
            internal ExportSourceFileSnapshot AssemblySnapshot;
            internal long BindingRevision;
            internal string Token, Instance, ManagedUuid;
            internal bool IsAsset;
            internal bool? CanHoldContents;
            internal string[] Occurrences;
            internal IList<string> InterfaceIssues;

            internal object Describe(bool saved)
            {
                return new
                {
                    plan_token = Token, instance = Instance, source_path = Asset.Path, same_source_instances = Occurrences,
                    is_asset = IsAsset, can_hold_contents = CanHoldContents, changes = Changes, interface_issues = InterfaceIssues, saved = saved
                };
            }
        }

        private AssetPropertyPlan PrepareAssetProperties(ModelDoc2 doc, IDictionary<string, object> args)
        {
            if (IsDirty(doc)) throw new ValidationException("装配有未保存修改，请先保存装配。");
            var session = SwInstanceBindings.ForDocument(doc);
            session.RequireSaved();
            var instance = (string)args["instance"];
            var root = SwAssemblyRoot.FromActiveDocument(_app, _coordinator.MutationTracker);
            var entry = ReadComponents(root, null).FirstOrDefault(item => item.Instance.Equals(instance, StringComparison.Ordinal));
            if (entry == null || !entry.Source.SourcePath.Equals((string)args["source_path"], StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("实例或源文件已改变，请重新调用 component_list。");
            if (entry.Properties == null) throw new ValidationException(entry.Issue);
            var model = entry.Source.Model;
            ModelRules.ValidateClassifiable(model);
            var asset = new InterfaceAsset(entry.Source, _propertyBackupRoot);
            asset.Reload();
            var desired = McpAssetPropertyRules.Normalize(McpProtocol.Object(args["properties"]));
            foreach (var names in new[]
            {
                new[] { PropertyRules.IsWorkstation, PropertyRules.LegacyIsWorkstation },
                new[] { PropertyRules.WorkstationName, PropertyRules.LegacyWorkstationName }
            })
                if (desired.ContainsKey(names[0]) && asset.Snapshot.Values.ContainsKey(names[1]))
                    desired[names[1]] = desired[names[0]];
            var proposed = new Dictionary<string, string>(asset.Snapshot.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in desired) proposed[pair.Key] = pair.Value;

            var isAsset = McpAssetPropertyRules.ParseFlag(PropertyValue(proposed, PropertyRules.IsAsset), PropertyRules.IsAsset) == true;
            var isRobot = isAsset && PropertyRules.IsRobotClass(proposed);
            if (!isAsset && (desired.ContainsKey(PropertyRules.SlotType) || desired.ContainsKey(PropertyRules.AssetEngagement)))
                throw new ValidationException("仅 Asset 的文件级属性可声明 slot_type 和 engagement。");
            if (isAsset) McpAssetPropertyRules.ApplySlotDefinition(asset.Snapshot, desired, proposed, !isRobot);
            var cleanup = isAsset ? McpAssetPropertyRules.ContentsInterfaceCleanup(asset.Snapshot, desired) :
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in cleanup)
            {
                desired.Add(pair.Key, pair.Value);
                proposed[pair.Key] = pair.Value;
            }
            var wasAsset = PropertyRules.ReadIsAsset(asset.Snapshot.Values);
            var workstation = McpAssetPropertyRules.WorkstationFlag(proposed) == true;
            if (workstation && (isAsset || model.DocumentKind != DocumentKind.Assembly))
                throw new ValidationException("仅非 Asset 装配体可标记为工位。将原工位改为 Asset 时请同时关闭工位标记。");
            if (PropertyRules.ReadIsWorkstation(proposed, model.DocumentKind))
                PropertyRules.RequireWorkstationName(proposed, entry.Source.Name);
            if (desired.ContainsKey(PropertyRules.AssetVersion) && !isRobot)
                throw new ValidationException("普通 asset_version 由插件维护。仅 Robot 可填写已确认的外部发布版本。");

            IList<string> interfaceIssues = new List<string>();
            if (isAsset)
            {
                if (isRobot) PropertyRules.BuildRobotAssetId(proposed, entry.Source.Name);
                else
                {
                    ModelRules.ValidateExportable(model);
                    PropertyRules.RequireWanxiangAssetClass(proposed, entry.Source.Name);
                    var missing = PropertyRules.MissingOrBlankProperties(proposed, new[] { PropertyRules.PartName, PropertyRules.DesignPurpose });
                    if (missing.Count > 0) throw new ValidationException("Asset 缺少必填属性：" + string.Join("、", missing));
                }
                var issues = PropertyRules.ValidateAssetConnectionProperties(proposed, false);
                if (issues.Count != 0) throw new ValidationException(string.Join("\n", issues));
                interfaceIssues = PropertyRules.ValidateAssetConnectionProperties(proposed);
            }

            var changes = desired.Where(pair => asset.Snapshot.Get(pair.Key) != pair.Value)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var occurrences = SourceOccurrences(doc, entry);
            if (changes.Count > 0)
            {
                // Contents names already have no mechanical meaning to the binding resolver.
                var explicitChanges = changes.Keys.Where(key => !cleanup.ContainsKey(key));
                GuardAssetPropertyBindings(session, occurrences, asset.Path, proposed, explicitChanges, wasAsset, isAsset,
                    isRobot, model.DocumentKind);
                if (isAsset && !isRobot)
                    foreach (var edit in InterfaceEditRules.VersionChanges(asset.Snapshot, asset.Snapshot.Version)) changes[edit.Key] = edit.Value;
            }
            asset.ValidateForSave(changes);
            RequireContext(doc, args);
            var assemblySnapshot = ExportSourceFileSnapshot.Capture(doc.GetPathName(), null);
            var token = FileHash.Sha256Text(McpProtocol.Json().Serialize(new
            {
                assembly = assemblySnapshot, configuration = Configuration(doc), instance, occurrences,
                source = asset.SourceSnapshot,
                original = asset.Snapshot.Raw.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                properties = desired.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                bindings = session.StoredConfigurations.SelectMany(config => config.Relations.Select(record => record.Id))
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray()
            }));
            return new AssetPropertyPlan
            {
                Asset = asset, Changes = changes, Token = token, AssemblySnapshot = assemblySnapshot, Instance = instance,
                BindingRevision = session.Revision,
                Occurrences = occurrences, IsAsset = isAsset, InterfaceIssues = interfaceIssues,
                CanHoldContents = isAsset ? (bool?)PropertyRules.ReadCanContain(proposed) : null,
                ManagedUuid = isAsset && !isRobot ? IdentityService.AssetUuid(model).ToString("D") : null
            };
        }

        private void GuardAssetPropertyBindings(SwInstanceBindings session, string[] occurrences, string sourcePath,
            IDictionary<string, string> proposed, IEnumerable<string> explicitChanges,
            bool wasAsset, bool isAsset, bool isRobot, DocumentKind kind)
        {
            var active = session.Current;
            var becomesBoundary = !wasAsset && isAsset && kind == DocumentKind.Assembly;
            var sourceName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
            var affectsConnection = explicitChanges.Any(key => key == PropertyRules.IsAsset || key == PropertyRules.AssetClass ||
                key == PropertyRules.IsTool || key == PropertyRules.IsFixture || key == PropertyRules.IsQuickChanger ||
                key == PropertyRules.QuickChangerSide || key == PropertyRules.IsQuickChangerRack ||
                key == PropertyRules.ConnectionInterface || key == PropertyRules.AcceptsInterfaces);
            foreach (var configuration in session.StoredConfigurations)
                foreach (var record in configuration.Relations)
                {
                    foreach (var endpoint in new[] { record.Parent, record.Child })
                    {
                        var sameSource = string.Equals(endpoint.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase);
                        var display = endpoint.DisplayName ?? "";
                        var containsEndpoint = becomesBoundary && (occurrences.Any(occurrence =>
                            occurrence.Length == 0 || display.StartsWith(occurrence + "/", StringComparison.Ordinal)) ||
                            configuration.ConfigurationId != active.ConfigurationId && CadAncestors(display)
                                .Any(segment => segment.StartsWith(sourceName + "-", StringComparison.OrdinalIgnoreCase)));
                        if (sameSource && (!isAsset || isRobot) || containsEndpoint)
                            throw new ValidationException("分类会使已有承载端点失效，请先处理配置 [" + configuration.ConfigurationName +
                                "] 的关系 [" + record.Id + "]。");
                        if (sameSource && affectsConnection && configuration.ConfigurationId != active.ConfigurationId)
                            throw new ValidationException("配置 [" + configuration.ConfigurationName + "] 也使用此源文件的承载属性，请先核对该配置。");
                    }
                }
            if (!affectsConnection) return;
            var affected = active.Relations.Where(record => string.Equals(record.Parent.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.Child.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (affected.Count == 0) return;
            var operation = new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
            foreach (var relation in affected)
            {
                var parent = operation.Resolve(relation.Parent);
                var child = operation.Resolve(relation.Child);
                try { InstanceBindingRules.ValidateConnection(parent, child); }
                catch (ValidationException) { continue; }
                InstanceBindingRules.ValidateConnection(WithAssetProperties(parent, sourcePath, proposed),
                    WithAssetProperties(child, sourcePath, proposed));
            }
        }

        private static IEnumerable<string> CadAncestors(string occurrence)
        {
            var slash = occurrence.LastIndexOf('/');
            return slash < 0 ? Enumerable.Empty<string>() : occurrence.Substring(0, slash).Split('/');
        }

        private static ExportNode WithAssetProperties(ExportNode node, string sourcePath, IDictionary<string, string> proposed)
        {
            if (!string.Equals(((SwCadNode)node.Source).SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)) return node;
            return new ExportNode { Id = node.Id, Name = node.Name, Source = node.Source, Kind = node.Kind, Properties = proposed };
        }

        private async Task<object> ApplyAssetProperties(ModelDoc2 doc, IDictionary<string, object> args)
        {
            var plan = PrepareAssetProperties(doc, args);
            var session = SwInstanceBindings.ForDocument(doc);
            if (!string.Equals(plan.Token, (string)args["plan_token"], StringComparison.Ordinal))
                throw new ValidationException("属性计划已过期或参数已改变，请重新调用 asset_properties_plan。");
            if (plan.Changes.Count == 0) return plan.Describe(false);
            if (plan.ManagedUuid != null)
            {
                var stamp = doc.GetUpdateStamp();
                var registry = await Task.Run(_readRegistry);
                RequireContext(doc, args);
                if (doc.GetUpdateStamp() != stamp || IsDirty(doc) || session.Revision != plan.BindingRevision)
                    throw new ValidationException("查询发布版本期间装配已改变，请重新生成属性计划。");
                var next = InterfaceEditRules.NextVersionIfPublished(registry.Assets, plan.ManagedUuid, plan.Asset.Snapshot.Version);
                if (next.HasValue)
                    foreach (var edit in InterfaceEditRules.VersionChanges(plan.Asset.Snapshot, next.Value)) plan.Changes[edit.Key] = edit.Value;
            }
            plan.AssemblySnapshot.ValidateUnchanged();
            plan.Asset.Save(plan.Changes, McpAssetPropertyRules.Fields.Concat(new[]
                { PropertyRules.LegacyIsWorkstation, PropertyRules.LegacyWorkstationName, PropertyRules.LegacyAcceptsInterface }));
            return plan.Describe(true);
        }
    }
}
