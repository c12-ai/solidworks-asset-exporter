using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed partial class McpSolidWorksTools
    {
        private sealed class BindingSourceChange
        {
            internal readonly InterfaceAsset Asset;
            internal readonly string Uuid;
            internal Dictionary<string, string> Changes;

            internal BindingSourceChange(ExportNode node, string backupRoot)
            {
                Asset = new InterfaceAsset((SwCadNode)node.Source, backupRoot);
                Asset.Reload();
                Uuid = node.GeometryUuid;
                Changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private sealed class BindingPlan
        {
            internal string Token;
            internal long Revision;
            internal ExportSourceFileSnapshot AssemblySnapshot;
            internal BindingEndpoint Parent;
            internal BindingEndpoint[] Children;
            internal IList<BindingSourceChange> Sources;
            internal IList<InstanceBinding> Added;
            internal IList<InstanceBinding> PlannedSlots;
            internal int? Capacity;
            internal int OccupiedSlots, RequiredSlots;
            internal IList<ContactDiagnostic> Contact;

            internal object Describe()
            {
                var additions = new HashSet<string>(Added.Select(record => record.Child.Context), StringComparer.Ordinal);
                var slots = PlannedSlots.ToDictionary(record => record.Child.Context, StringComparer.Ordinal);
                return new
                {
                    plan_token = Token,
                    slots_num = Capacity,
                    occupied_slots = OccupiedSlots,
                    required_slots = RequiredSlots,
                    contact_diagnostics = DescribeContactDiagnostics(Contact),
                    source_changes = Sources.Where(source => source.Changes.Count > 0)
                        .Select(source => new { source_path = source.Asset.Path, changes = source.Changes }).ToArray(),
                    relations = Children.Select(child =>
                    {
                        InstanceBinding slot;
                        slots.TryGetValue(child.Context, out slot);
                        return new { parent = Parent.DisplayName, child = child.DisplayName,
                            already_bound = !additions.Contains(child.Context), slot_id = slot == null ? null : slot.SlotId,
                            slot_offset = DescribeRelationPose(slot == null ? null : slot.SlotOffset) };
                    }).ToArray(),
                    added_count = Added.Count,
                    existing_count = Children.Length - Added.Count
                };
            }
        }

        private BindingPlan PrepareBinding(ModelDoc2 doc, IDictionary<string, object> args, bool checkContact = false)
        {
            var session = SwInstanceBindings.ForDocument(doc);
            session.RequireSaved();
            if (IsDirty(doc)) throw new ValidationException("装配有未保存修改，请先保存装配。");
            var operation = new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
            var group = CaptureBindingGroup(doc, operation, args);
            var parent = operation.Resolve(group.Item1);
            var children = group.Item2.Select(operation.Resolve).ToArray();
            if (children.Any(child => child.Id == parent.Id))
                throw new ValidationException("承载关系不能指向自身。");
            if (children.Any(child => PropertyValue(child.Properties, PropertyRules.AssetClass) == "structure"))
                throw new ValidationException("自动建立承载关系时，structure 子资产直接归 Lab。需要显式例外时请使用 binding_add。");

            var endpoints = new[] { parent }.Concat(children).ToArray();
            var sources = endpoints.GroupBy(node => ((SwCadNode)node.Source).SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grouped => grouped.Key, grouped => new BindingSourceChange(grouped.First(), _propertyBackupRoot),
                    StringComparer.OrdinalIgnoreCase);
            var parentSource = sources[group.Item1.SourcePath];
            var requested = (string)McpProtocol.Value(args, "connection_interface");
            var confirmed = requested == null ? null : InterfaceEditRules.MechanicalName(requested);
            var connections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in group.Item2)
            {
                var source = sources[child.SourcePath];
                if (!connections.ContainsKey(child.SourcePath))
                    connections.Add(child.SourcePath, InterfaceEditRules.BootstrapConnection(source.Asset.Snapshot, confirmed, child.DisplayName));
            }
            var existingAccepts = parentSource.Asset.Snapshot.Get(PropertyRules.AcceptsInterfaces);
            var accepts = InterfaceEditRules.Suggest(existingAccepts, connections.Values);
            if (accepts.SequenceEqual(PropertyRules.MechanicalInterfaceNames(existingAccepts), StringComparer.Ordinal))
                accepts = null;
            var requestedCapacity = (int?)McpProtocol.Value(args, "slots_num");
            foreach (var source in sources.Values)
            {
                string connection;
                connections.TryGetValue(source.Asset.Path, out connection);
                var isParent = ReferenceEquals(source, parentSource);
                source.Changes = InterfaceEditRules.Changes(source.Asset.Snapshot, connection,
                    isParent ? accepts : null, isParent ? requestedCapacity : null,
                    source.Asset.Snapshot.Version, true);
            }

            var records = session.Current.Relations;
            var projected = new Dictionary<string, ExportNode>(StringComparer.Ordinal);
            Func<BindingEndpoint, ExportNode> resolve = endpoint =>
            {
                var node = operation.Resolve(endpoint);
                ExportNode value;
                if (!projected.TryGetValue(node.Id, out value))
                    projected.Add(node.Id, value = WithPlannedProperties(node, sources));
                return value;
            };
            foreach (var child in group.Item2)
                InstanceBindingRules.ValidateConnection(resolve(group.Item1), resolve(child));
            foreach (var record in records)
            {
                var originalParent = operation.Resolve(record.Parent);
                var originalChild = operation.Resolve(record.Child);
                var nextParent = resolve(record.Parent);
                var nextChild = resolve(record.Child);
                if (ReferenceEquals(nextParent, originalParent) && ReferenceEquals(nextChild, originalChild)) continue;
                try { InstanceBindingRules.ValidateConnection(originalParent, originalChild); }
                catch (ValidationException) { continue; }
                InstanceBindingRules.ValidateConnection(nextParent, nextChild);
            }
            var reuseSlot = (string)McpProtocol.Value(args, "reuse_slot_from_binding_id");
            var proposed = InstanceBindingRules.Apply(records, group.Item1, group.Item2, resolve, false, reuseSlot);
            var requiredSlots = session.RequiredSlotCapacity(parentSource.Asset.Path, proposed);
            var occupiedSlots = SwInstanceBindings.CountBoundSlots(proposed.Where(record => resolve(record.Parent).Id == parent.Id));
            var capacity = requestedCapacity ?? PropertyRules.ReadSlotCapacity(parentSource.Asset.Snapshot.Values);
            var plannedSlots = proposed.Where(record => record.SlotId != null && record.Parent.Context == group.Item1.Context).ToArray();
            var existing = new HashSet<string>(records.Select(record => record.Id), StringComparer.Ordinal);
            var orderedSources = sources.Values.OrderBy(source => source.Asset.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var source in orderedSources) source.Asset.ValidateForSave(source.Changes);
            RequireContext(doc, args);
            var snapshot = ExportSourceFileSnapshot.Capture(doc.GetPathName(), null);
            return new BindingPlan
            {
                Token = BindingPlanToken(snapshot, Configuration(doc), group, records, orderedSources, reuseSlot, plannedSlots, requestedCapacity),
                Revision = session.Revision,
                AssemblySnapshot = snapshot,
                Parent = group.Item1,
                Children = group.Item2,
                Sources = orderedSources,
                Added = proposed.Where(record => !existing.Contains(record.Id)).ToArray(),
                PlannedSlots = plannedSlots,
                Capacity = capacity, OccupiedSlots = occupiedSlots, RequiredSlots = requiredSlots,
                Contact = checkContact ? SwContactDiagnostics.ForBinding(_app, doc, parent, children) : new List<ContactDiagnostic>()
            };
        }

        private static ExportNode WithPlannedProperties(ExportNode node, IDictionary<string, BindingSourceChange> sources)
        {
            BindingSourceChange source;
            if (!sources.TryGetValue(((SwCadNode)node.Source).SourcePath, out source) || source.Changes.Count == 0) return node;
            var properties = new Dictionary<string, string>(source.Asset.Snapshot.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var change in source.Changes) properties[change.Key] = change.Value;
            return new ExportNode { Id = node.Id, Kind = node.Kind, Source = node.Source, Name = node.Name, Properties = properties };
        }

        private static string BindingPlanToken(ExportSourceFileSnapshot snapshot, string configuration,
            Tuple<BindingEndpoint, BindingEndpoint[]> group,
            IEnumerable<InstanceBinding> records, IEnumerable<BindingSourceChange> sources,
            string reuseSlot, IEnumerable<InstanceBinding> plannedSlots, int? requestedCapacity)
        {
            var material = new
            {
                assembly = snapshot,
                configuration = configuration,
                reuse_slot_from_binding_id = reuseSlot,
                requested_capacity = requestedCapacity,
                slots = plannedSlots.Select(record => new { record.SlotId, child = record.Child.Context,
                    pose = record.SlotPose.CanonicalValue(), offset = record.SlotOffset.CanonicalValue() }).ToArray(),
                endpoints = new[] { group.Item1 }.Concat(group.Item2).Select(endpoint => new
                {
                    endpoint.Context, endpoint.SourceIdentity, endpoint.SourcePath, endpoint.DisplayName, endpoint.AssetId
                }).ToArray(),
                bindings = records.OrderBy(record => record.Id, StringComparer.Ordinal)
                    .Select(record => new { record.Id, parent = record.Parent.Context, child = record.Child.Context }).ToArray(),
                files = sources.Select(source => new
                {
                    snapshot = source.Asset.SourceSnapshot,
                    properties = source.Asset.Snapshot.Raw.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                    changes = source.Changes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
                }).ToArray()
            };
            return FileHash.Sha256Text(McpProtocol.Json().Serialize(material));
        }

        private async Task<object> ApplyBinding(ModelDoc2 doc, IDictionary<string, object> args)
        {
            var session = SwInstanceBindings.ForDocument(doc);
            session.RequireWritable();
            var plan = PrepareBinding(doc, args);
            if (!string.Equals(plan.Token, (string)args["plan_token"], StringComparison.Ordinal))
                throw new ValidationException("承载计划已过期或参数已改变，请重新调用 binding_plan。");

            var sources = plan.Sources.Where(source => source.Changes.Count != 0).ToArray();
            if (sources.Length != 0)
            {
                var stamp = doc.GetUpdateStamp();
                var registry = await Task.Run(_readRegistry);
                RequireContext(doc, args);
                if (doc.GetUpdateStamp() != stamp || IsDirty(doc) || session.Revision != plan.Revision)
                    throw new ValidationException("查询发布版本期间装配已改变，请重新调用 binding_plan。");
                foreach (var source in sources)
                {
                    var next = InterfaceEditRules.NextVersionIfPublished(registry.Assets, source.Uuid, source.Asset.Snapshot.Version);
                    if (next.HasValue)
                        foreach (var edit in InterfaceEditRules.VersionChanges(source.Asset.Snapshot, next.Value))
                            source.Changes[edit.Key] = edit.Value;
                    source.Asset.ValidateForSave(source.Changes);
                }
            }

            var saved = new List<string>();
            try
            {
                plan.AssemblySnapshot.ValidateUnchanged();
                foreach (var source in plan.Sources) source.Asset.SourceSnapshot.ValidateUnchanged();
                foreach (var source in sources)
                {
                    RequireContext(doc, args);
                    source.Asset.Save(source.Changes);
                    saved.Add(source.Asset.Path);
                }
                if (session.Revision != plan.Revision)
                    throw new ValidationException("承载关系已改变，请重新调用 binding_plan。");
                var result = await AddBindings(doc, args);
                return new { source_files_saved = saved, binding_result = result };
            }
            catch (Exception ex)
            {
                if (saved.Count == 0) throw;
                throw new ValidationException(ex.Message + "\n以下源文件已保存：" + string.Join("；", saved) +
                    "。请读取当前承载状态并重新生成计划。");
            }
        }

        private object DuplicateBinding(ModelDoc2 doc, IDictionary<string, object> args, bool apply)
        {
            var session = SwInstanceBindings.ForDocument(doc);
            Tuple<BindingEndpoint, BindingEndpoint[]> group;
            try
            {
                var operation = new SwBindingEndpoints(_app, session, _coordinator.MutationTracker);
                group = CaptureBindingGroup(doc, operation, new Dictionary<string, object>
                {
                    { "parent", args["parent"] }, { "children", new[] { args["child"] } }
                });
            }
            catch (System.Runtime.InteropServices.COMException ex)
            { throw SwInstanceBindings.DuplicateComError("从总装捕获指定端点", ex); }
            var plan = session.PrepareDuplicate(_app, group.Item1, group.Item2.Single(), (string)McpProtocol.Value(args, "reuse_slot_from_binding_id"));
            var token = FileHash.Sha256Text(McpProtocol.Json().Serialize(new
            {
                plan.Assembly, plan.ParentSource, plan.ChildSource, plan.ConfigurationId, plan.Configuration, plan.Revision, plan.UpdateStamp,
                plan.Parent, plan.Original, plan.SourceConfiguration, plan.ReplacedId, plan.ReuseSlotId, plan.Capacity, plan.Before,
                plan.OccupiedSlots, plan.RequiredSlots,
                plan.OtherConfigurations, plan.Poses
            }));
            if (!apply) return new
            {
                plan_token = token, parent = plan.Parent.DisplayName, child = plan.Original.DisplayName,
                source_path = plan.Original.SourcePath, source_configuration = plan.SourceConfiguration,
                migrated_binding_id = plan.ReplacedId, reused_slot_from_binding_id = plan.ReuseSlotId,
                slots_num = plan.Capacity, occupied_slots = plan.OccupiedSlots, required_slots = plan.RequiredSlots,
                world_transform = plan.Poses[string.Empty]
            };
            if (!string.Equals(token, (string)args["plan_token"], StringComparison.Ordinal))
                throw new ValidationException("原位复制计划已过期或参数已改变，请重新调用 binding_duplicate_plan。");
            RequireContext(doc, args);
            var result = session.ApplyDuplicate(_app, plan, _propertyBackupRoot);
            return new
            {
                saved = true, migrated_binding_id = plan.ReplacedId, backup_path = result.BackupPath,
                slots_num = plan.Capacity, occupied_slots = plan.OccupiedSlots, required_slots = plan.RequiredSlots,
                binding = DescribeBinding(new SwBindingEndpoints(_app, session, _coordinator.MutationTracker), result.Relation)
            };
        }
    }
}
