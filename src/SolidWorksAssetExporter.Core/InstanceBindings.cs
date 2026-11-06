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
        public BindingDocument() { SchemaVersion = CurrentSchemaVersion; Configurations = new List<ConfigurationBindings>(); }
        [DataMember] public string SchemaVersion { get; set; }
        [DataMember] public List<ConfigurationBindings> Configurations { get; set; }
        public ConfigurationBindings ForConfiguration(int id, string name)
        {
            if ((SchemaVersion != "1.0" && SchemaVersion != CurrentSchemaVersion) || Configurations == null || Configurations.Any(c => c == null || c.Relations == null ||
                c.Relations.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || r.Parent == null || r.Child == null ||
                    string.IsNullOrWhiteSpace(r.Parent.Reference) || string.IsNullOrWhiteSpace(r.Child.Reference)) ||
                c.Relations.GroupBy(r => r.Id).Any(g => g.Count() != 1)) ||
                Configurations.GroupBy(c => c.ConfigurationId).Any(g => g.Count() != 1))
                throw new ValidationException("承载关系存储格式无效或版本不受支持，不能忽略已有记录。");
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
        [DataMember(Name = "parent_node_id", Order = 1)] public string ParentNodeId { get; set; }
        [DataMember(Name = "child_node_id", Order = 2)] public string ChildNodeId { get; set; }
        [DataMember(Name = "pose", Order = 3)] public RelationPose Pose { get; set; }
    }

    [DataContract]
    public sealed class AssemblyRelations
    {
        public const string FileName = "assembly-relations.json";
        public const string CurrentSchemaVersion = "1.1";
        public AssemblyRelations() { SchemaVersion = CurrentSchemaVersion; LengthUnit = "m"; QuaternionOrder = "xyzw"; Relations = new List<AssemblyRelation>(); }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "assembly_uuid", Order = 2)] public string AssemblyUuid { get; set; }
        [DataMember(Name = "assembly_version", Order = 3)] public int AssemblyVersion { get; set; }
        [DataMember(Name = "length_unit", Order = 4)] public string LengthUnit { get; set; }
        [DataMember(Name = "quaternion_order", Order = 5)] public string QuaternionOrder { get; set; }
        [DataMember(Name = "relations", Order = 6)] public List<AssemblyRelation> Relations { get; set; }
        public string FingerprintMaterial()
        {
            var b = new StringBuilder(Canonical.Join(FileName, SchemaVersion, LengthUnit, QuaternionOrder));
            foreach (var r in Relations.OrderBy(r => r.ParentNodeId, StringComparer.Ordinal).ThenBy(r => r.ChildNodeId, StringComparer.Ordinal))
                b.Append(Canonical.Join(r.ParentNodeId, r.ChildNodeId, r.Pose.CanonicalValue()));
            return b.ToString();
        }
        public void Validate(XDocument xml, string uuid, int version)
        {
            if ((SchemaVersion != "1.0" && SchemaVersion != CurrentSchemaVersion) || LengthUnit != "m" || QuaternionOrder != "xyzw" || Relations == null ||
                AssemblyUuid != uuid || AssemblyVersion != version || version < 1 || xml.Root == null ||
                (string)xml.Root.Attribute("uuid") != uuid || (string)xml.Root.Attribute("version") != version.ToString(CultureInfo.InvariantCulture))
                throw new ValidationException("承载关系文件的格式或装配 UUID/version 与同包 XML 不一致。");
            var nodes = xml.Descendants("node").ToList();
            if (nodes.GroupBy(n => (string)n.Attribute("id")).Any(g => string.IsNullOrEmpty(g.Key) || g.Count() != 1))
                throw new ValidationException("assembly XML 包含重复或空 node.id。");
            var ids = new HashSet<string>(nodes.Where(n => (string)n.Attribute("kind") == "asset").Select(n => (string)n.Attribute("id")), StringComparer.Ordinal);
            foreach (var r in Relations)
            {
                if (r == null || !ids.Contains(r.ParentNodeId ?? "") || !ids.Contains(r.ChildNodeId ?? "") || r.Pose == null)
                    throw new ValidationException("承载关系端点必须是同包 XML 中唯一的普通 Asset。");
                r.Pose.Validate();
            }
            if (SchemaVersion == "1.0" && Relations.GroupBy(r => r.ChildNodeId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new ValidationException("承载关系 1.0 不支持多父关系，请使用 1.1 格式重新导出。");
            InstanceBindingRules.ValidateGraph(Relations);
        }
    }

    public static class InstanceBindingRules
    {
        public static List<InstanceBinding> Apply(IEnumerable<InstanceBinding> existing, BindingEndpoint parent,
            IEnumerable<BindingEndpoint> children, Func<BindingEndpoint, ExportNode> resolve, bool replaceParent)
        {
            // One immutable plan per batch: resolve each captured endpoint once, not
            // once for every comparison with every other child (hundreds of COM calls).
            var read = resolve; var cache = new Dictionary<BindingEndpoint, ExportNode>();
            resolve = e => { if (e == null) return read(e); ExportNode n;
                if (!cache.TryGetValue(e, out n)) { n = read(e); cache.Add(e, n); } return n; };
            var proposed = existing.ToList();
            var p = RequireAsset(resolve(parent));
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
                    // Normal apply adds an edge; only the explicitly labelled replace action
                    // removes other parents. Retain an existing target edge and its stable ID.
                    if (replaceParent) foreach (var r in old.Except(sameParent)) proposed.Remove(r);
                    if (sameParent.Count > 0) continue;
                    proposed.Add(new InstanceBinding { Parent = parent, Child = child });
                }
                catch (ValidationException ex) { errors.Add(ex.Message); }
            }
            // Validate the entire proposed graph before the caller commits any records.
            try { Resolve(proposed, resolve, null); }
            catch (ValidationException ex) { errors.Add(ex.Message); }
            if (errors.Count != 0) throw new ValidationException(string.Join(Environment.NewLine, errors.Distinct()));
            return proposed;
        }

        public static AssemblyRelations Resolve(IEnumerable<InstanceBinding> records, Func<BindingEndpoint, ExportNode> resolve, AssemblyExportPlan plan)
        {
            var result = new AssemblyRelations { AssemblyUuid = plan == null ? null : plan.AssemblyUuid, AssemblyVersion = plan == null ? 0 : plan.AssemblyVersion };
            var errors = new List<string>();
            foreach (var r in records)
            {
                try
                {
                    if (r == null) throw new ValidationException("承载记录为空。");
                    var p = RequireAsset(resolve(r.Parent)); var c = RequireAsset(resolve(r.Child));
                    ValidateConnection(p, c);
                    // Source.WorldTransform is in the top-level assembly coordinate system (SW Transform2).
                    var pose = RelationPose.From(Pose.FromTransform(p.Source.WorldTransform.InverseRigid().Multiply(c.Source.WorldTransform)));
                    pose.Validate();
                    result.Relations.Add(new AssemblyRelation { ParentNodeId = p.Id, ChildNodeId = c.Id, Pose = pose });
                }
                catch (ValidationException ex) { errors.Add((r == null ? "记录" : r.Id) + ": " + ex.Message); }
            }
            try { ValidateGraph(result.Relations); } catch (ValidationException ex) { errors.Add(ex.Message); }
            if (errors.Count != 0) throw new ValidationException("承载关系需要修复或显式解除：\n" + string.Join("\n", errors));
            result.Relations = result.Relations.OrderBy(r => r.ParentNodeId, StringComparer.Ordinal).ThenBy(r => r.ChildNodeId, StringComparer.Ordinal).ToList();
            return result;
        }

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
            var accepts = Read(p, PropertyRules.AcceptsInterfaces).Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var connection = Read(c, PropertyRules.ConnectionInterface);
            if (accepts.Count == 0) issues.Add(parent.Name + ": accepts_interfaces 不能为空。");
            if (connection.Length == 0) issues.Add(child.Name + ": connection_interface 不能为空。");
            else if (!accepts.Contains(connection, StringComparer.Ordinal)) issues.Add(parent.Name + " → " + child.Name + ": 接口不兼容。");
            var pr = PropertyRules.DescribeAssetConnectionRole(p); var cr = PropertyRules.DescribeAssetConnectionRole(c);
            // A rack may support either a tool-side changer or a tool directly.
            // Every edge still requires matching interfaces, including shared children.
            if (pr == "快换盘-机器人端" && cr != "快换盘-工具端" ||
                pr == "快换架" && cr != "快换盘-工具端" && cr != "Tool" ||
                pr == "快换盘-工具端" && cr != "Tool" || cr == "快换盘-机器人端" ||
                cr == "快换盘-工具端" && pr != "快换盘-机器人端" && pr != "快换架")
                issues.Add(parent.Name + " → " + child.Name + ": 协议禁止的角色链 " + pr + " → " + cr + "。");
            if (issues.Count > 0) throw new ValidationException(string.Join("\n", issues));
        }
        private static string Read(IDictionary<string, string> p, string key)
        { string v; return p != null && p.TryGetValue(key, out v) ? (v ?? "").Trim() : ""; }
    }
}
