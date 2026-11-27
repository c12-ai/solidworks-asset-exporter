using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Xml.Linq;

namespace SolidWorksAssetExporter.Core
{
    [DataContract]
    public sealed class BindingEndpoint
    {
        [DataMember] public string Reference { get; set; }
        [DataMember] public string Context { get; set; }
        [DataMember] public string SourceIdentity { get; set; }
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string DisplayName { get; set; }
        [DataMember] public string DisplayPath { get; set; }
        [DataMember] public string AssetId { get; set; }
    }

    [DataContract]
    public sealed class InstanceBinding
    {
        public InstanceBinding() { Id = Guid.NewGuid().ToString("D"); }
        [DataMember] public string Id { get; set; }
        [DataMember] public BindingEndpoint Parent { get; set; }
        [DataMember] public BindingEndpoint Child { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SlotId { get; set; }
        [DataMember(EmitDefaultValue = false)] public RelationPose SlotPose { get; set; }
        [DataMember(EmitDefaultValue = false)] public RelationPose SlotOffset { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SlotType { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Engagement { get; set; }
    }

    [DataContract]
    public sealed class ConfigurationBindings
    {
        public ConfigurationBindings() { Relations = new List<InstanceBinding>(); }
        [DataMember] public int ConfigurationId { get; set; }
        [DataMember] public string ConfigurationName { get; set; }
        [DataMember] public List<InstanceBinding> Relations { get; set; }
    }

    [DataContract]
    public sealed class BindingDocument
    {
        public const string CurrentSchemaVersion = "1.1";
        public const string SharedSlotSchemaVersion = "1.2";
        public const string CouplingSchemaVersion = "1.3";
        public BindingDocument() { SchemaVersion = CurrentSchemaVersion; Configurations = new List<ConfigurationBindings>(); }
        [DataMember] public string SchemaVersion { get; set; }
        [DataMember] public List<ConfigurationBindings> Configurations { get; set; }
        public ConfigurationBindings ForConfiguration(int id, string name)
        {
            var couplingVersion = SchemaVersion == CouplingSchemaVersion;
            var supportsSharedSlots = SchemaVersion == SharedSlotSchemaVersion || couplingVersion;
            if ((SchemaVersion != "1.0" && SchemaVersion != CurrentSchemaVersion && !supportsSharedSlots) ||
                Configurations == null || Configurations.Any(c => c == null || c.Relations == null ||
                c.Relations.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || r.Parent == null || r.Child == null ||
                    string.IsNullOrWhiteSpace(r.Parent.Reference) || string.IsNullOrWhiteSpace(r.Child.Reference) ||
                    ((r.SlotId != null || r.SlotPose != null || r.SlotOffset != null) &&
                        (!supportsSharedSlots || string.IsNullOrWhiteSpace(r.SlotId) || r.SlotPose == null || r.SlotOffset == null)) ||
                    (!couplingVersion && (r.SlotType != null || r.Engagement != null))) ||
                c.Relations.GroupBy(r => r.Id).Any(g => g.Count() != 1)) ||
                Configurations.GroupBy(c => c.ConfigurationId).Any(g => g.Count() != 1))
                throw new ValidationException("承载关系存储格式无效或版本不受支持，不能忽略已有记录。");
            if (couplingVersion)
            {
                foreach (var configuration in Configurations)
                {
                    foreach (var relation in configuration.Relations)
                        InstanceBindingRules.ValidateCoupling(relation.SlotType, relation.Engagement);
                    InstanceBindingRules.ValidateSharedCouplings(configuration.Relations, r => r.SlotId, r => r.SlotType, r => r.Engagement);
                }
            }
            var value = Configurations.SingleOrDefault(c => c.ConfigurationId == id);
            // SW configuration IDs survive renames; new/copied configurations have new IDs.
            return value ?? new ConfigurationBindings { ConfigurationId = id, ConfigurationName = name };
        }
    }

    [DataContract]
    public sealed class RelationPose
    {
        [DataMember(Name = "tx", Order = 1)] public double Tx { get; set; }
        [DataMember(Name = "ty", Order = 2)] public double Ty { get; set; }
        [DataMember(Name = "tz", Order = 3)] public double Tz { get; set; }
        [DataMember(Name = "qx", Order = 4)] public double Qx { get; set; }
        [DataMember(Name = "qy", Order = 5)] public double Qy { get; set; }
        [DataMember(Name = "qz", Order = 6)] public double Qz { get; set; }
        [DataMember(Name = "qw", Order = 7)] public double Qw { get; set; }
        public static RelationPose From(Pose p)
        { return new RelationPose { Tx = p.Tx, Ty = p.Ty, Tz = p.Tz, Qx = p.Rotation.X, Qy = p.Rotation.Y, Qz = p.Rotation.Z, Qw = p.Rotation.W }; }
        public static RelationPose Identity { get { return new RelationPose { Qw = 1 }; } }
        internal Matrix4 ToTransform()
        {
            Validate();
            var x = Qx; var y = Qy; var z = Qz; var w = Qw;
            return new Matrix4(new[]
            {
                1d - 2d * (y*y + z*z), 2d * (x*y - z*w), 2d * (x*z + y*w), Tx,
                2d * (x*y + z*w), 1d - 2d * (x*x + z*z), 2d * (y*z - x*w), Ty,
                2d * (x*z - y*w), 2d * (y*z + x*w), 1d - 2d * (x*x + y*y), Tz,
                0d, 0d, 0d, 1d
            });
        }
        public string CanonicalValue()
        {
            return Canonical.Join(new[] { Tx, Ty, Tz, Qx, Qy, Qz, Qw }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }
        public void Validate()
        {
            if (new[] { Tx, Ty, Tz, Qx, Qy, Qz, Qw }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                Math.Abs(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw - 1) > 1e-9)
                throw new ValidationException("承载关系位姿无效（要求有限数值和单位四元数）。");
        }
    }

    [DataContract]
    public sealed class AssemblyRelation
    {
        internal string BindingId { get; set; }
        internal string SlotType { get; set; }
        [DataMember(Name = "parent_node_id", Order = 1)] public string ParentNodeId { get; set; }
        [DataMember(Name = "child_node_id", Order = 2)] public string ChildNodeId { get; set; }
        [DataMember(Name = "pose", Order = 3)] public RelationPose Pose { get; set; }
        [DataMember(Name = "slot_id", Order = 4, EmitDefaultValue = false)] public string SlotId { get; set; }
        [DataMember(Name = "slot_offset", Order = 5, EmitDefaultValue = false)] public RelationPose SlotOffset { get; set; }
        [DataMember(Name = "placement", Order = 6, EmitDefaultValue = false)] public AssetSlotPlacement Placement { get; set; }
        [DataMember(Name = "engagement", Order = 7, EmitDefaultValue = false)] public string Engagement { get; set; }
    }

    [DataContract]
    public sealed class AssetSlotPlacement
    {
        [DataMember(Name = "slot_key", Order = 1)] public string SlotKey { get; set; }
        [DataMember(Name = "parent_asset_uuid", Order = 2)] public string ParentAssetUuid { get; set; }
        [DataMember(Name = "child_asset_uuid", Order = 3)] public string ChildAssetUuid { get; set; }
        [DataMember(Name = "pose", Order = 4)] public RelationPose Pose { get; set; }
        [DataMember(Name = "slot_type", Order = 5, EmitDefaultValue = false)] public string SlotType { get; set; }
    }

    [DataContract]
    public sealed class AssemblySlot
    {
        [DataMember(Name = "slot_id", Order = 1)] public string Id { get; set; }
        [DataMember(Name = "parent_node_id", Order = 2)] public string ParentNodeId { get; set; }
        [DataMember(Name = "pose", Order = 3)] public RelationPose Pose { get; set; }
    }

    [DataContract]
    public sealed class AssemblyRelations
    {
        public const string FileName = "assembly-relations.json";
        public const string CurrentSchemaVersion = "1.1";
        public const string SharedSlotSchemaVersion = "1.2";
        public const string PlacementSchemaVersion = "1.3";
        public const string CouplingSchemaVersion = "1.4";
        public static bool SupportsVersion(string version)
        { return version == "1.0" || version == CurrentSchemaVersion || version == SharedSlotSchemaVersion || version == PlacementSchemaVersion || version == CouplingSchemaVersion; }
        public AssemblyRelations() { SchemaVersion = CurrentSchemaVersion; LengthUnit = "m"; QuaternionOrder = "xyzw"; Relations = new List<AssemblyRelation>(); }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "assembly_uuid", Order = 2)] public string AssemblyUuid { get; set; }
        [DataMember(Name = "assembly_version", Order = 3)] public int AssemblyVersion { get; set; }
        [DataMember(Name = "length_unit", Order = 4)] public string LengthUnit { get; set; }
        [DataMember(Name = "quaternion_order", Order = 5)] public string QuaternionOrder { get; set; }
        [DataMember(Name = "relations", Order = 6)] public List<AssemblyRelation> Relations { get; set; }
        [DataMember(Name = "slots", Order = 7, EmitDefaultValue = false)] public List<AssemblySlot> Slots { get; set; }
        public string FingerprintMaterial()
        {
            var b = new StringBuilder(Canonical.Join(FileName, SchemaVersion, LengthUnit, QuaternionOrder));
            foreach (var r in Relations.OrderBy(r => r.ParentNodeId, StringComparer.Ordinal).ThenBy(r => r.ChildNodeId, StringComparer.Ordinal))
            {
                b.Append(Canonical.Join(r.ParentNodeId, r.ChildNodeId, r.Pose.CanonicalValue()));
                if (r.SlotId != null) b.Append(Canonical.Join(r.SlotId, r.SlotOffset.CanonicalValue()));
                if (r.Placement != null) b.Append(Canonical.Join(r.Placement.SlotKey, r.Placement.ParentAssetUuid,
                    r.Placement.ChildAssetUuid, r.Placement.Pose.CanonicalValue()));
                if (r.Engagement != null || (r.Placement != null && r.Placement.SlotType != null))
                    b.Append(Canonical.Join(r.Placement == null ? null : r.Placement.SlotType, r.Engagement));
            }
            if (Slots != null)
                foreach (var slot in Slots.OrderBy(s => s.Id, StringComparer.Ordinal))
                    b.Append(Canonical.Join(slot.Id, slot.ParentNodeId, slot.Pose.CanonicalValue()));
            return b.ToString();
        }

        public void IncludeAssetPlacements(IEnumerable<ExportNode> assets)
        {
            if (Relations.Count == 0) return;
            IncludeAssetPlacements(assets.ToDictionary(node => node.Id, node => node.GeometryUuid, StringComparer.Ordinal));
        }

        internal void IncludeAssetPlacements(IDictionary<string, string> ids)
        {
            var shared = (Slots ?? new List<AssemblySlot>()).ToDictionary(slot => slot.Id, StringComparer.Ordinal);
            foreach (var relation in Relations)
            {
                var basePose = relation.SlotId == null ? null : shared[relation.SlotId].Pose;
                var previous = relation.Placement;
                relation.Placement = new AssetSlotPlacement
                {
                    SlotKey = relation.SlotId ?? relation.BindingId ?? (previous == null ? null : previous.SlotKey),
                    ParentAssetUuid = ids[relation.ParentNodeId],
                    ChildAssetUuid = ids[relation.ChildNodeId],
                    Pose = PlacementPose(relation, basePose),
                    SlotType = previous == null ? relation.SlotType : previous.SlotType
                };
            }
            SchemaVersion = Relations.Any(r => r.Engagement != null) ? CouplingSchemaVersion : PlacementSchemaVersion;
        }

        private static RelationPose PlacementPose(AssemblyRelation relation, RelationPose slotPose)
        {
            return slotPose == null ? relation.Pose : RelationPose.From(Pose.FromTransform(
                slotPose.ToTransform().Multiply(relation.SlotOffset.ToTransform())));
        }

        private static string XmlAssetUuid(XElement node)
        {
            var mesh = node.Element("mesh");
            var assetId = mesh == null ? null : (string)mesh.Attribute("asset_id");
            Guid uuid;
            if (assetId == null || !Guid.TryParseExact(assetId.Split(':')[0], "D", out uuid))
                throw new ValidationException("assembly XML 的 Asset 缺少有效 UUID。");
            return uuid.ToString("D");
        }
        public void Validate(XDocument xml, string uuid, int version)
        {
            if (!SupportsVersion(SchemaVersion) || LengthUnit != "m" || QuaternionOrder != "xyzw" || Relations == null ||
                AssemblyUuid != uuid || AssemblyVersion != version || version < 1 || xml.Root == null ||
                (string)xml.Root.Attribute("uuid") != uuid || (string)xml.Root.Attribute("version") != version.ToString(CultureInfo.InvariantCulture))
                throw new ValidationException("承载关系文件的格式或装配 UUID/version 与同包 XML 不一致。");
            var nodes = xml.Descendants("node").ToList();
            if (nodes.GroupBy(n => (string)n.Attribute("id")).Any(g => string.IsNullOrEmpty(g.Key) || g.Count() != 1))
                throw new ValidationException("assembly XML 包含重复或空 node.id。");
            var ids = new HashSet<string>(nodes.Where(n => (string)n.Attribute("kind") == "asset").Select(n => (string)n.Attribute("id")), StringComparer.Ordinal);
            var couplingVersion = SchemaVersion == CouplingSchemaVersion;
            var placementVersion = SchemaVersion == PlacementSchemaVersion || couplingVersion;
            var assetUuids = placementVersion ? nodes.Where(n => ids.Contains((string)n.Attribute("id")))
                .ToDictionary(n => (string)n.Attribute("id"), XmlAssetUuid, StringComparer.Ordinal) : null;
            var slots = new Dictionary<string, AssemblySlot>(StringComparer.Ordinal);
            if (SchemaVersion == SharedSlotSchemaVersion || (placementVersion && Slots != null))
            {
                if (Slots == null || Slots.Count == 0) throw new ValidationException("共享槽位清单不能为空。");
                foreach (var slot in Slots)
                {
                    if (slot == null || string.IsNullOrWhiteSpace(slot.Id) || slots.ContainsKey(slot.Id) ||
                        !ids.Contains(slot.ParentNodeId ?? "") || slot.Pose == null)
                        throw new ValidationException("共享槽位 ID、父组件或位姿无效。");
                    slot.Pose.Validate();
                    slots.Add(slot.Id, slot);
                }
            }
            else if (Slots != null) throw new ValidationException("此承载关系版本不支持共享槽位。");
            foreach (var r in Relations)
            {
                if (r == null || !ids.Contains(r.ParentNodeId ?? "") || !ids.Contains(r.ChildNodeId ?? "") || r.Pose == null)
                    throw new ValidationException("承载关系端点必须是同包 XML 中唯一的普通 Asset。");
                r.Pose.Validate();
                AssemblySlot slot = null;
                if (r.SlotId != null || r.SlotOffset != null)
                {
                    if (r.SlotId == null || r.SlotOffset == null || !slots.TryGetValue(r.SlotId, out slot) || slot.ParentNodeId != r.ParentNodeId)
                        throw new ValidationException("子组件引用的共享槽位不存在或不属于所绑定的父组件。");
                    r.SlotOffset.Validate();
                }
                if (placementVersion)
                {
                    var placement = r.Placement;
                    Guid slotKey;
                    if (placement == null || !Guid.TryParseExact(placement.SlotKey, "D", out slotKey) ||
                        (slot != null && placement.SlotKey != slot.Id) || placement.ParentAssetUuid != assetUuids[r.ParentNodeId] ||
                        placement.ChildAssetUuid != assetUuids[r.ChildNodeId] || placement.Pose == null ||
                        placement.Pose.CanonicalValue() != PlacementPose(r, slot == null ? null : slot.Pose).CanonicalValue())
                        throw new ValidationException("槽位安装数据的标识、资产 UUID 或位姿与承载关系不一致。");
                }
                else if (r.Placement != null) throw new ValidationException("此承载关系版本不支持槽位安装数据。");
                var slotType = r.Placement == null ? null : r.Placement.SlotType;
                if (couplingVersion) InstanceBindingRules.ValidateCoupling(slotType, r.Engagement);
                else if (slotType != null || r.Engagement != null)
                    throw new ValidationException("此承载关系版本不支持夹持槽位或夹持状态。");
            }
            if (couplingVersion)
            {
                if (!Relations.Any(r => r.Engagement != null))
                    throw new ValidationException("承载关系 1.4 至少需要一条夹持记录。");
                if (Relations.GroupBy(r => r.Placement.SlotKey, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1 && group.Any(r => r.SlotId == null)))
                    throw new ValidationException("独立槽位不能与其他关系使用相同的物理槽位键。");
                InstanceBindingRules.ValidateSharedCouplings(Relations, r => r.Placement.SlotKey, r => r.Placement.SlotType, r => r.Engagement);
            }
            if (slots.Keys.Except(Relations.Select(r => r.SlotId), StringComparer.Ordinal).Any())
                throw new ValidationException("共享槽位没有关联子组件。");
            if (SchemaVersion == "1.0" && Relations.GroupBy(r => r.ChildNodeId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new ValidationException("承载关系 1.0 不支持多父关系，请使用 1.1 格式重新导出。");
            InstanceBindingRules.ValidateGraph(Relations);
        }
    }

    public static class InstanceBindingRules
    {
        internal static void ValidateCoupling(string slotType, string engagement)
        {
            if (slotType == null && engagement == null) return;
            if (slotType != "clamp" || (engagement != "tight" && engagement != "loose"))
                throw new ValidationException("夹持槽位必须同时指定 slot_type=clamp 和 engagement=tight 或 loose。");
        }

        internal static void ValidateSharedCouplings<T>(IEnumerable<T> records, Func<T, string> slotKey,
            Func<T, string> slotType, Func<T, string> engagement)
        {
            foreach (var group in records.GroupBy(slotKey, StringComparer.Ordinal))
            {
                if (group.Key == null) continue;
                if (group.Select(slotType).Distinct(StringComparer.Ordinal).Count() > 1)
                    throw new ValidationException("同一物理槽位的 slot_type 必须一致：" + group.Key);
                if (group.Count(record => engagement(record) == "tight") > 1)
                    throw new ValidationException("同一夹持槽位仅支持一个 tight 备选关系：" + group.Key);
            }
        }

        public static List<InstanceBinding> Apply(IEnumerable<InstanceBinding> existing, BindingEndpoint parent,
            IEnumerable<BindingEndpoint> children, Func<BindingEndpoint, ExportNode> resolve, bool replaceParent,
            string reuseSlotFromBindingId = null, string slotType = null, string engagement = null)
        {
            ValidateCoupling(slotType, engagement);
            // One immutable plan per batch: resolve each captured endpoint once, not
            // once for every comparison with every other child (hundreds of COM calls).
            var read = resolve; var cache = new Dictionary<BindingEndpoint, ExportNode>();
            resolve = e => { if (e == null) return read(e); ExportNode n;
                if (!cache.TryGetValue(e, out n)) { n = read(e); cache.Add(e, n); } return n; };
            var proposed = existing.ToList();
            var p = RequireAsset(resolve(parent));
            InstanceBinding slotBinding = null;
            RelationPose slotPose = null;
            Matrix4 parentToSlotInverse = null;
            if (reuseSlotFromBindingId != null)
            {
                if (replaceParent) throw new ValidationException("复用槽位不能与替换父组件同时使用。");
                slotBinding = proposed.SingleOrDefault(r => r.Id == reuseSlotFromBindingId);
                if (slotBinding == null || RequireAsset(resolve(slotBinding.Parent)).Id != p.Id)
                    throw new ValidationException("请选择当前父组件下的一条已有关系作为槽位参考。");
                slotPose = slotBinding.SlotPose ?? RelativePose(p, RequireAsset(resolve(slotBinding.Child)));
                parentToSlotInverse = slotPose.ToTransform().InverseRigid();
            }
            var selected = children.GroupBy(c => RequireAsset(resolve(c)).Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if (selected.Count == 0) throw new ValidationException("请至少选择一个子组件。");
            var errors = new List<string>();
            foreach (var child in selected)
            {
                try
                {
                    var c = RequireAsset(resolve(child));
                    var old = proposed.Where(r => RequireAsset(resolve(r.Child)).Id == c.Id).ToList();
                    var sameParent = old.Where(r => RequireAsset(resolve(r.Parent)).Id == p.Id).ToList();
                    var removed = replaceParent ? old.Except(sameParent).ToList() : null;
                    if (!replaceParent && sameParent.Count == 0 && old.Count != 0)
                        throw new ValidationException(c.Name + " 已有承载父组件。请使用原位复制并绑定，或显式替换父组件。");
                    // Retain an existing target edge and its stable slot key.
                    if (sameParent.Count == 0 || (removed != null && removed.Count > 0))
                        ValidateConnection(p, c);
                    if (removed != null) foreach (var r in removed) proposed.Remove(r);
                    if (sameParent.Count > 0)
                    {
                        if (slotBinding != null && !sameParent.Any(r => r.Id == slotBinding.Id ||
                            (r.SlotId != null && r.SlotId == slotBinding.SlotId)))
                            throw new ValidationException(c.Name + " 已绑定在此父组件的另一个槽位。");
                        var previous = sameParent[0];
                        if (slotType != null && (previous.SlotType != slotType || previous.Engagement != engagement))
                        {
                            var updated = new InstanceBinding
                            {
                                Id = previous.Id, Parent = previous.Parent, Child = previous.Child,
                                SlotId = previous.SlotId, SlotPose = previous.SlotPose, SlotOffset = previous.SlotOffset,
                                SlotType = slotType, Engagement = engagement
                            };
                            proposed[proposed.IndexOf(previous)] = updated;
                            if (slotBinding == previous) slotBinding = updated;
                        }
                        continue;
                    }
                    var addition = new InstanceBinding { Parent = parent, Child = child, SlotType = slotType, Engagement = engagement };
                    if (slotBinding != null)
                    {
                        if (slotBinding.SlotId == null)
                        {
                            var anchor = new InstanceBinding { Id = slotBinding.Id, Parent = slotBinding.Parent, Child = slotBinding.Child,
                                SlotId = slotBinding.Id, SlotPose = slotPose, SlotOffset = RelationPose.Identity,
                                SlotType = slotBinding.SlotType, Engagement = slotBinding.Engagement };
                            proposed[proposed.IndexOf(slotBinding)] = anchor;
                            slotBinding = anchor;
                        }
                        addition.SlotId = slotBinding.SlotId;
                        addition.SlotPose = slotPose;
                        var offset = parentToSlotInverse.Multiply(RelativeTransform(p, c));
                        addition.SlotOffset = RelationPose.From(Pose.FromTransform(offset));
                    }
                    proposed.Add(addition);
                }
                catch (ValidationException ex) { errors.Add(ex.Message); }
            }
            // Check the full topology, including retained edges with interface issues.
            // Their current compatibility is reported by the editor after the commit.
            var uniqueRecords = new List<InstanceBinding>();
            try { Resolve(proposed, resolve, null, uniqueRecords, false); }
            catch (ValidationException ex) { errors.Add(ex.Message); }
            if (errors.Count != 0) throw new ValidationException(string.Join(Environment.NewLine, errors.Distinct()));
            return uniqueRecords;
        }

        public static HashSet<string> MultipleParentBindingIds(IEnumerable<InstanceBinding> records)
        {
            return new HashSet<string>(records.GroupBy(record => BindingInstanceKey(record.Child))
                .Where(group => group.Select(record => BindingInstanceKey(record.Parent)).Distinct().Skip(1).Any())
                .SelectMany(group => group.Select(record => record.Id)), StringComparer.Ordinal);
        }

        public static void RejectNewMultipleParents(IEnumerable<InstanceBinding> previous, IEnumerable<InstanceBinding> next)
        {
            var oldParents = previous.ToLookup(record => BindingInstanceKey(record.Child), record => BindingInstanceKey(record.Parent));
            foreach (var group in next.GroupBy(record => BindingInstanceKey(record.Child)))
            {
                var parents = group.Select(record => BindingInstanceKey(record.Parent)).Distinct().ToArray();
                if (parents.Length > 1 && parents.Except(oldParents[group.Key]).Any())
                    throw new ValidationException(group.First().Child.DisplayName + " 每个实例只能绑定一个承载父组件。请先原位复制实例。");
            }
        }

        private static Tuple<string, string, string, string> BindingInstanceKey(BindingEndpoint endpoint)
        { return Tuple.Create(endpoint.Context, endpoint.Reference, endpoint.SourceIdentity, endpoint.SourcePath); }

        public static List<InstanceBinding> BindDuplicate(IEnumerable<InstanceBinding> existing, BindingEndpoint parent,
            BindingEndpoint original, BindingEndpoint duplicate, Func<BindingEndpoint, ExportNode> resolve, string reuseSlotFromBindingId = null)
        {
            var records = existing.ToList();
            var originalNode = RequireAsset(resolve(original));
            var duplicateNode = RequireAsset(resolve(duplicate));
            var parentNode = RequireAsset(resolve(parent));
            if (originalNode.Id == duplicateNode.Id || originalNode.Id == parentNode.Id ||
                original.SourceIdentity != duplicate.SourceIdentity || !string.Equals(original.SourcePath, duplicate.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("复制实例必须引用同一个源文件，并具有独立的组件身份。");
            var incoming = records.Where(record => RequireAsset(resolve(record.Child)).Id == originalNode.Id).ToList();
            var replaced = incoming.SingleOrDefault(record => RequireAsset(resolve(record.Parent)).Id == parentNode.Id);
            if (replaced == null)
                return Apply(records, parent, new[] { duplicate }, resolve, false, reuseSlotFromBindingId);
            if (incoming.Count == 1)
                throw new ValidationException("原实例已绑定到此父组件。请选择另一个父组件，或迁移一条旧多父关系。");
            if (reuseSlotFromBindingId != null)
                throw new ValidationException("迁移旧多父关系时沿用原槽位，无需另选槽位。");
            ValidateConnection(parentNode, duplicateNode);
            var next = records.Select(record => record.Id == replaced.Id ? new InstanceBinding
            {
                Id = record.Id, Parent = record.Parent, Child = duplicate,
                SlotId = record.SlotId, SlotPose = record.SlotPose, SlotOffset = record.SlotOffset,
                SlotType = record.SlotType, Engagement = record.Engagement
            } : record).ToList();
            var uniqueRecords = new List<InstanceBinding>();
            Resolve(next, resolve, null, uniqueRecords, false);
            RejectNewMultipleParents(records, uniqueRecords);
            return uniqueRecords;
        }

        public static AssemblyRelations Resolve(IEnumerable<InstanceBinding> records, Func<BindingEndpoint, ExportNode> resolve, AssemblyExportPlan plan)
        { return Resolve(records, resolve, plan, null, true); }

        private static AssemblyRelations Resolve(IEnumerable<InstanceBinding> records,
            Func<BindingEndpoint, ExportNode> resolve, AssemblyExportPlan plan, ICollection<InstanceBinding> uniqueRecords,
            bool validateConnections)
        {
            var result = new AssemblyRelations { AssemblyUuid = plan == null ? null : plan.AssemblyUuid, AssemblyVersion = plan == null ? 0 : plan.AssemblyVersion };
            var errors = new List<string>();
            var pairs = new Dictionary<Tuple<string, string>, InstanceBinding>();
            var slots = new Dictionary<string, AssemblySlot>(StringComparer.Ordinal);
            var assetUuids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                try
                {
                    if (r == null) throw new ValidationException("承载记录为空。");
                    ValidateCoupling(r.SlotType, r.Engagement);
                    var p = RequireAsset(resolve(r.Parent)); var c = RequireAsset(resolve(r.Child));
                    var pair = Tuple.Create(p.Id, c.Id);
                    InstanceBinding previous;
                    if (pairs.TryGetValue(pair, out previous))
                    {
                        if (r.SlotType != previous.SlotType || r.Engagement != previous.Engagement ||
                            (r.SlotType != null && r.SlotId != previous.SlotId))
                            throw new ValidationException("重复承载记录的夹持槽位或夹持状态不一致。");
                        continue;
                    }
                    pairs.Add(pair, r);
                    if (validateConnections) ValidateConnection(p, c);
                    var pose = RelativePose(p, c);
                    pose.Validate();
                    if (r.SlotId != null)
                    {
                        if (string.IsNullOrWhiteSpace(r.SlotId) || r.SlotPose == null || r.SlotOffset == null)
                            throw new ValidationException("共享槽位缺少 ID、位姿或子组件偏移。");
                        r.SlotPose.Validate();
                        r.SlotOffset.Validate();
                        AssemblySlot slot;
                        if (!slots.TryGetValue(r.SlotId, out slot))
                            slots.Add(r.SlotId, new AssemblySlot { Id = r.SlotId, ParentNodeId = p.Id, Pose = r.SlotPose });
                        else if (slot.ParentNodeId != p.Id || slot.Pose.CanonicalValue() != r.SlotPose.CanonicalValue())
                            throw new ValidationException("同一共享槽位的父组件或位姿不一致。");
                    }
                    result.Relations.Add(new AssemblyRelation { ParentNodeId = p.Id, ChildNodeId = c.Id, Pose = pose,
                        BindingId = r.Id, SlotId = r.SlotId, SlotOffset = r.SlotOffset, SlotType = r.SlotType, Engagement = r.Engagement });
                    assetUuids[p.Id] = p.GeometryUuid;
                    assetUuids[c.Id] = c.GeometryUuid;
                    if (uniqueRecords != null) uniqueRecords.Add(r);
                }
                catch (ValidationException ex) { errors.Add((r == null ? "记录" : r.Id) + ": " + ex.Message); }
            }
            var hasCoupling = result.Relations.Any(r => r.Engagement != null);
            try
            {
                ValidateGraph(result.Relations);
                if (hasCoupling)
                {
                    var physicalSlots = result.Relations.GroupBy(r => r.SlotId ?? r.BindingId, StringComparer.Ordinal);
                    if (physicalSlots.Any(group => group.Count() > 1 && group.Any(r => r.SlotId == null)))
                        throw new ValidationException("独立槽位不能与其他关系使用相同的物理槽位键。");
                    ValidateSharedCouplings(result.Relations, r => r.SlotId, r => r.SlotType, r => r.Engagement);
                }
            }
            catch (ValidationException ex) { errors.Add(ex.Message); }
            if (errors.Count != 0) throw new ValidationException("承载关系需要修复或显式解除：\n" + string.Join("\n", errors));
            if (slots.Count > 0)
            {
                result.SchemaVersion = AssemblyRelations.SharedSlotSchemaVersion;
                result.Slots = slots.Values.OrderBy(slot => slot.Id, StringComparer.Ordinal).ToList();
            }
            if (hasCoupling) result.IncludeAssetPlacements(assetUuids);
            result.Relations = result.Relations.OrderBy(r => r.ParentNodeId, StringComparer.Ordinal).ThenBy(r => r.ChildNodeId, StringComparer.Ordinal).ToList();
            return result;
        }

        private static Matrix4 RelativeTransform(ExportNode parent, ExportNode child)
        {
            // SW Transform2 supplies both transforms in the top-level assembly frame.
            return parent.Source.WorldTransform.InverseRigid().Multiply(child.Source.WorldTransform);
        }

        private static RelationPose RelativePose(ExportNode parent, ExportNode child)
        { return RelationPose.From(Pose.FromTransform(RelativeTransform(parent, child))); }

        public static ExportNode RequireAsset(ExportNode node)
        {
            if (node == null || node.Kind != ExportNodeKind.Asset || node.Source == null || node.Source.InstanceId == "root" || !AssemblyScanner.IsIncluded(node.Source))
                throw new ValidationException("当前不可导出：请选择可独立导出的普通 Asset 实例；不支持根、group、meshes、Robot 或 Asset 内部零件。");
            return node;
        }

        public static void ValidateGraph(IEnumerable<AssemblyRelation> relations)
        {
            var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in relations)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.ParentNodeId) || string.IsNullOrWhiteSpace(r.ChildNodeId))
                    throw new ValidationException("承载关系端点不能为空。");
                if (r.ParentNodeId == r.ChildNodeId) throw new ValidationException("禁止自身绑定：" + r.ChildNodeId);
                HashSet<string> edges;
                if (!children.TryGetValue(r.ParentNodeId, out edges)) children.Add(r.ParentNodeId, edges = new HashSet<string>(StringComparer.Ordinal));
                if (!edges.Add(r.ChildNodeId)) throw new ValidationException("重复父子关系：" + r.ParentNodeId + " → " + r.ChildNodeId);
                if (!incoming.ContainsKey(r.ParentNodeId)) incoming.Add(r.ParentNodeId, 0);
                if (!incoming.ContainsKey(r.ChildNodeId)) incoming.Add(r.ChildNodeId, 0);
                incoming[r.ChildNodeId]++;
            }
            // Topological traversal supports shared children and detects cycles through any parent.
            var ready = new Queue<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key));
            var visited = 0;
            while (ready.Count > 0)
            {
                var parent = ready.Dequeue(); visited++;
                HashSet<string> edges;
                if (children.TryGetValue(parent, out edges)) foreach (var child in edges)
                    if (--incoming[child] == 0) ready.Enqueue(child);
            }
            if (visited != incoming.Count) throw new ValidationException("禁止循环承载关系：" + string.Join("、", incoming.Where(p => p.Value > 0).Select(p => p.Key).Take(5)));
        }

        public static void ValidateConnection(ExportNode parent, ExportNode child)
        {
            var p = parent.Properties; var c = child.Properties;
            var issues = PropertyRules.ValidateAssetConnectionProperties(p).Select(s => parent.Name + ": " + s)
                .Concat(PropertyRules.ValidateAssetConnectionProperties(c).Select(s => child.Name + ": " + s)).ToList();
            var accepts = PropertyRules.MechanicalInterfaceNames(Read(p, PropertyRules.AcceptsInterfaces));
            var connection = PropertyRules.MechanicalConnectionInterface(Read(c, PropertyRules.ConnectionInterface));
            var pr = PropertyRules.DescribeAssetConnectionRole(p); var cr = PropertyRules.DescribeAssetConnectionRole(c);
            var matchesInterface = cr == "Tool"
                ? accepts.Select(CanonicalToolInterface).Contains(CanonicalToolInterface(connection), StringComparer.Ordinal)
                : accepts.Contains(connection, StringComparer.Ordinal);
            if (accepts.Count == 0) issues.Add(parent.Name + ": accepts_interfaces 不能为空。");
            if (connection.Length == 0) issues.Add(child.Name + ": connection_interface 不能为空。");
            else if (accepts.Count > 0 && !matchesInterface)
                issues.Add(parent.Name + " → " + child.Name + "：接口不兼容。子组件接口 [" + connection +
                    "]，父组件可接受 [" + string.Join("；", accepts) + "]。");
            // A rack may support either a tool-side changer or a tool directly.
            // Every edge still requires matching interfaces, including shared children.
            if (pr == "快换盘-机器人端" && cr != "快换盘-工具端" ||
                pr == "快换架" && cr != "快换盘-工具端" && cr != "Tool" ||
                pr == "快换盘-工具端" && cr != "Tool" || cr == "快换盘-机器人端" ||
                cr == "快换盘-工具端" && pr != "快换盘-机器人端" && pr != "快换架")
                issues.Add(parent.Name + " → " + child.Name + ": 协议禁止的角色链 " + pr + " → " + cr + "。");
            if (issues.Count > 0) throw new ValidationException(string.Join("\n", issues));
        }

        private static string CanonicalToolInterface(string name)
        {
            return name == "工具安装.移液器" ? "移液器" : name;
        }

        private static string Read(IDictionary<string, string> p, string key)
        { string v; return p != null && p.TryGetValue(key, out v) ? (v ?? "").Trim() : ""; }
    }
}
