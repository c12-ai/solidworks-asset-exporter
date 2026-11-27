using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SolidWorksAssetExporter.Core
{
    public sealed class ExportPlacementDiagnostic
    {
        internal ExportPlacementDiagnostic(string code, bool isBlocking, string message, string reviewKey = null, string reviewReason = null)
        { Code = code; IsBlocking = isBlocking; Message = message; ReviewKey = reviewKey; ReviewReason = reviewReason; }

        public string Code { get; private set; }
        public bool IsBlocking { get; private set; }
        public string Message { get; private set; }
        public string ReviewKey { get; private set; }
        public string ReviewReason { get; private set; }
    }

    public sealed class ExportPlacementReviewContext
    {
        public string Configuration { get; set; }
        public int ConfigurationId { get; set; }
        public Func<ExportNode, string> ReferencedConfiguration { get; set; }
        public Func<ExportNode, string> OccurrenceIdentity { get; set; }
        public IDictionary<string, string> Reasons { get; set; }
    }

    public static class ExportPlacementDiagnostics
    {
        private const double PositionTolerance = 0.0001; // 0.1 mm
        private const double AngleTolerance = 0.1; // degrees
        private const double MaximumNearbySlotDistance = 0.005; // 5 mm
        private static readonly HashSet<string> KnownDoorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BSC门", "BSC上拉门", "BSC下拉门", "冰箱推拉门", "冰箱推拉门组件A", "冰箱推拉门组件B",
            "Shaker下翻门", "培养箱下翻门总成"
        };
        private static readonly HashSet<string> DoorInterfaceTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "门", "门板", "推拉门", "滑门", "上拉门", "下拉门", "下翻门", "铰链门" };
        private static readonly string[] AssetSlotDefinitionFields = { PropertyRules.SlotType, PropertyRules.AssetEngagement };
        private static readonly string[] AssetEngagementFields = { PropertyRules.AssetEngagement };
        private static readonly string[] DescriptionFields = { PropertyRules.DesignPurpose, "设计原理", "升版说明" };
        private static readonly string[] ContentsDescriptionTerms = { "细胞样本", "细胞样品", "细胞悬液", "溶液", "培养液", "液体样本", "液体样品", "试剂" };
        private static readonly string[] InterfaceFields = { PropertyRules.ConnectionInterface,
            PropertyRules.AcceptsInterfaces, PropertyRules.LegacyAcceptsInterface };

        public static IList<ExportPlacementDiagnostic> Analyze(IEnumerable<ExportNode> assets, AssemblyRelations graph,
            Func<ExportNode, IDictionary<string, string>> readProperties, ExportPlacementReviewContext reviewContext = null)
        {
            var readConfiguration = reviewContext == null ? null : reviewContext.ReferencedConfiguration;
            var endpoints = assets.Select(node => new Endpoint(node, readProperties(node), readConfiguration))
                .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal).ToDictionary(endpoint => endpoint.Node.Id, StringComparer.Ordinal);
            var slots = (graph.Slots ?? new List<AssemblySlot>()).ToDictionary(slot => slot.Id, StringComparer.Ordinal);
            var incoming = graph.Relations.ToLookup(relation => relation.ChildNodeId, StringComparer.Ordinal);
            var observations = graph.Relations.Select(relation => new Observation(endpoints[relation.ParentNodeId],
                endpoints[relation.ChildNodeId], relation, relation.SlotId == null ? null : slots[relation.SlotId].Pose,
                incoming[relation.ChildNodeId].Count() > 1)).ToList();
            var diagnostics = new List<ExportPlacementDiagnostic>();
            foreach (var parents in observations.Where(row => row.HasOtherParent)
                .GroupBy(row => row.Child.Path, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
                diagnostics.Add(new ExportPlacementDiagnostic("multiple_parents", true,
                    "子实例 [" + parents.Key + "] 有多个父级（" + string.Join("、", parents.Select(row => "[" + row.Parent.Path + "]")
                        .Distinct().OrderBy(path => path, StringComparer.Ordinal)) + "）。请在承载关系中原位复制并绑定，或确认唯一父级。"));
            CheckDoors(observations.Where(row => row.Child.IsDoor && !row.HasOtherParent), diagnostics);
            CheckRepeatedSlots(observations.Where(row => !row.Child.IsDoor && !row.HasOtherParent), diagnostics);
            CheckMechanicalConnections(endpoints.Values, diagnostics);
            CheckAssetSlotDefinitions(endpoints.Values, diagnostics);
            CheckAssetDescriptions(endpoints.Values, diagnostics);
            CheckCapacity(endpoints.Values, observations.ToLookup(row => row.Parent.Node.Id, StringComparer.Ordinal),
                diagnostics, graph.AssemblyUuid, reviewContext);
            return diagnostics;
        }

        private sealed class Endpoint
        {
            internal Endpoint(ExportNode node, IDictionary<string, string> properties, Func<ExportNode, string> readConfiguration)
            {
                Node = node;
                Path = node.Source == null ? node.Name : node.Source.InstancePath;
                var name = Value(properties, PropertyRules.PartName);
                var model = node.Source as ICadClassificationSource;
                Configuration = (readConfiguration != null ? readConfiguration(node) : model == null ? null : model.ClassificationModel.Configuration)
                    ?? string.Empty;
                var source = node.Source as ICadSourceReference;
                SourcePath = source == null ? null : source.SourcePath;
                Connection = PropertyRules.MechanicalConnectionInterface(Value(properties, PropertyRules.ConnectionInterface));
                IsDoor = KnownDoorNames.Contains(name) || Connection.Split('.').Any(DoorInterfaceTerms.Contains);
                IsStationFixture = Value(properties, PropertyRules.AssetClass) == "station" &&
                    PropertyRules.CanonicalPublishedValue(PropertyRules.IsFixture, Value(properties, PropertyRules.IsFixture)) == "1";
                IsToolSideQuickChanger = Value(properties, PropertyRules.QuickChangerSide) == "tool_side" &&
                    PropertyRules.CanonicalPublishedValue(PropertyRules.IsQuickChanger, Value(properties, PropertyRules.IsQuickChanger)) == "1";
                HasMechanicalAccepts = PropertyRules.MechanicalInterfaceNames(Value(properties, PropertyRules.AcceptsInterfaces)).Count != 0 ||
                    PropertyRules.MechanicalInterfaceNames(Value(properties, PropertyRules.LegacyAcceptsInterface)).Count != 0;
                Capacity = PropertyRules.ReadSlotCapacity(properties);
                MissingSlotDefinitionFields = PropertyRules.MissingOrBlankProperties(properties,
                    Capacity > 0 ? AssetSlotDefinitionFields : AssetEngagementFields);
                ContentsDescriptionFields = DescriptionFields.Where(field => ContentsDescriptionTerms.Any(term =>
                    Value(properties, field).IndexOf(term, StringComparison.Ordinal) >= 0)).ToArray();
                ContentsInterfaceFields = InterfaceFields.Where(field => Value(properties, field).Split(';', '；')
                    .Any(PropertyRules.IsContentsInterface)).ToArray();
                HasCapacityDeclaration = Value(properties, PropertyRules.SlotsNum).Length != 0;
            }

            internal ExportNode Node { get; private set; }
            internal string Path { get; private set; }
            internal string Configuration { get; private set; }
            internal string SourcePath { get; private set; }
            internal string Connection { get; private set; }
            internal bool IsDoor { get; private set; }
            internal bool IsStationFixture { get; private set; }
            internal bool IsToolSideQuickChanger { get; private set; }
            internal bool HasMechanicalAccepts { get; private set; }
            internal IList<string> MissingSlotDefinitionFields { get; private set; }
            internal string[] ContentsDescriptionFields { get; private set; }
            internal string[] ContentsInterfaceFields { get; private set; }
            internal bool HasCapacityDeclaration { get; private set; }
            internal int? Capacity { get; private set; }
        }

        private sealed class Observation
        {
            internal Observation(Endpoint parent, Endpoint child, AssemblyRelation relation, RelationPose slot, bool hasOtherParent)
            {
                Parent = parent;
                Child = child;
                Actual = relation.Pose;
                HasSlotAnchor = slot != null;
                Location = slot ?? Actual;
                Compared = slot == null ? Actual : RelationPose.From(Pose.FromTransform(
                    slot.ToTransform().InverseRigid().Multiply(Actual.ToTransform())));
                SlotKey = relation.SlotId ?? (relation.Placement == null ? child.Node.Id : relation.Placement.SlotKey);
                BindingId = relation.BindingId;
                HasOtherParent = hasOtherParent;
            }

            internal Endpoint Parent { get; private set; }
            internal Endpoint Child { get; private set; }
            internal RelationPose Actual { get; private set; }
            internal RelationPose Location { get; private set; }
            internal RelationPose Compared { get; private set; }
            internal string SlotKey { get; private set; }
            internal string BindingId { get; private set; }
            internal bool HasSlotAnchor { get; private set; }
            internal bool HasOtherParent { get; private set; }
        }

        private static void CheckDoors(IEnumerable<Observation> doorRows, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            var doors = doorRows.OrderBy(row => row.Child.Path, StringComparer.Ordinal).ToList();
            foreach (var group in doors.GroupBy(row => Tuple.Create(row.Parent.Node.AssetId, row.Child.Node.AssetId, row.Child.Connection)))
            {
                var rows = group.ToList();
                // Without a separate door role, two copies of one door on a single body may be two different installations.
                if (rows.Select(row => row.Parent.Node.Id).Distinct().Count() != rows.Count) continue;
                var reference = rows[0];
                var different = rows.Skip(1).Where(row => !SamePose(reference.Actual, row.Actual)).ToList();
                if (different.Count != 0)
                    diagnostics.Add(new ExportPlacementDiagnostic("door_state_mismatch", true,
                        "同款门的父级相对状态不一致。核对参考 " + Describe(reference) + "，再统一以下门的开合位置。" +
                        string.Join("；", different.Select(row => Describe(row) + "（" + Difference(reference.Actual, row.Actual) + "）"))));
            }

            foreach (var group in doors.GroupBy(row => row.Parent.Node.Id))
            {
                var children = group.ToList();
                for (var i = 0; i < children.Count; i++)
                for (var j = i + 1; j < children.Count; j++)
                {
                    var a = children[i]; var b = children[j];
                    if (a.Child.Node.GeometryUuid == b.Child.Node.GeometryUuid || a.SlotKey == b.SlotKey ||
                        PositionDistance(a.Actual, b.Actual) > PositionTolerance) continue;
                    diagnostics.Add(new ExportPlacementDiagnostic("split_door_origin_overlap", false,
                        "同一主体 [" + a.Parent.Path + "] 的独立门 [" + a.Child.Path + "] 与 [" + b.Child.Path +
                        "] 的原点重合。请确认两扇门的模型原点及安装位置。"));
                }
            }
        }

        private static void CheckRepeatedSlots(IEnumerable<Observation> rows, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            foreach (var family in rows.GroupBy(row => new
            {
                Parent = row.Parent.Node.AssetId, Child = row.Child.Node.AssetId, row.Child.Connection,
                ParentConfiguration = row.Parent.Configuration, ChildConfiguration = row.Child.Configuration
            }))
            {
                CompareNearbySlots(family.Where(row => row.HasSlotAnchor), diagnostics);

                var ordinary = family.Where(row => !row.HasSlotAnchor).ToList();
                // Without stored anchors, use a full repeated layout only when every slot is uniquely occupied by this asset type.
                var complete = ordinary.GroupBy(row => row.Parent.Node.Id).Select(group => group.ToArray())
                    .Where(layout => layout[0].Parent.Capacity == layout.Length && layout.Select(row => row.SlotKey).Distinct().Count() == layout.Length)
                    .ToList();
                var compared = new HashSet<Observation>();
                foreach (var layouts in complete.GroupBy(layout => layout.Length).Where(group => group.Count() > 1))
                {
                    var reference = layouts.GroupBy(LayoutKey).OrderByDescending(group => group.Count())
                        .ThenBy(group => group.Key, StringComparer.Ordinal).First().OrderBy(layout => layout[0].Parent.Path, StringComparer.Ordinal).First();
                    var locations = reference.Select(row => new List<Observation> { row }).ToArray();
                    foreach (var layout in layouts.Where(layout => layout != reference))
                        foreach (var match in MatchLayout(reference, layout))
                            locations[match.Key].Add(match.Value);
                    foreach (var location in locations.Where(group => group.Count > 1))
                    {
                        ReportVariation(location, diagnostics);
                        compared.UnionWith(location);
                    }
                }
                CompareNearbySlots(ordinary.Where(row => !compared.Contains(row)), diagnostics);
            }
        }

        private static string LayoutKey(IEnumerable<Observation> layout)
        {
            return string.Join("|", layout.Select(row => string.Join(",", new[] { row.Location.Tx, row.Location.Ty, row.Location.Tz }
                .Select(value => Math.Round(value / PositionTolerance).ToString("0", CultureInfo.InvariantCulture))))
                .OrderBy(value => value, StringComparer.Ordinal));
        }

        private static IDictionary<int, Observation> MatchLayout(Observation[] reference, Observation[] sample)
        {
            var match = new Dictionary<int, Observation>();
            for (var index = 0; index < reference.Length; index++)
            {
                var spot = reference[index].Location;
                // Stay below the distance to neighboring slots. Large single-slot deviations can still be identified by elimination.
                var limit = Math.Min(MaximumNearbySlotDistance, reference.Where((row, other) => other != index)
                    .Select(row => PositionDistance(spot, row.Location) / 3).DefaultIfEmpty(MaximumNearbySlotDistance).Min());
                var candidates = sample.Where(row => PositionDistance(spot, row.Location) <= limit).Take(2).ToList();
                if (candidates.Count == 1 && !match.ContainsValue(candidates[0])) match.Add(index, candidates[0]);
            }
            if (match.Count == reference.Length - 1)
                match.Add(Enumerable.Range(0, reference.Length).Single(index => !match.ContainsKey(index)),
                    sample.Single(row => !match.ContainsValue(row)));
            return match;
        }

        private static void CompareNearbySlots(IEnumerable<Observation> rows, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            var locations = new List<List<Observation>>();
            foreach (var row in rows.OrderBy(value => value.Child.Path, StringComparer.Ordinal))
            {
                var location = locations.FirstOrDefault(group => PositionDistance(group[0].Location, row.Location) <= PositionTolerance);
                if (location == null) locations.Add(new List<Observation> { row });
                else location.Add(row);
            }
            foreach (var location in locations)
                ReportVariation(location.GroupBy(row => row.Parent.Node.Id).Where(group => group.Count() == 1)
                    .Select(group => group.Single()).ToList(), diagnostics);
        }

        private static void ReportVariation(IList<Observation> rows, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            if (rows.Count < 2) return;
            var reference = rows.Select(row => new { Row = row, Support = rows.Count(other => SamePose(row.Compared, other.Compared)) })
                .OrderByDescending(item => item.Support).ThenBy(item => item.Row.Child.Path, StringComparer.Ordinal).First();
            if (reference.Support == rows.Count) return;
            if (reference.Support <= rows.Count / 2)
            {
                diagnostics.Add(new ExportPlacementDiagnostic("slot_pose_variance", false,
                    "同款子资产在对应槽位存在位姿差异，样本未形成多数基准。请人工核对 " + Describe(rows[0]) +
                    " 与 " + string.Join("；", rows.Skip(1).Select(row => Describe(row) + "（" +
                        Difference(rows[0].Compared, row.Compared) + "）")) + "。"));
                return;
            }
            var different = rows.Where(row => !SamePose(row.Compared, reference.Row.Compared));
            diagnostics.Add(new ExportPlacementDiagnostic("slot_pose_variance", false,
                "同款子资产在对应槽位偏离多数位姿（" + reference.Support + "/" + rows.Count + "），请人工核对。参考 " +
                Describe(reference.Row) + "。" + string.Join("；", different.Select(row =>
                    Describe(row) + "（" + Difference(reference.Row.Compared, row.Compared) + "）"))));
        }

        private static void CheckMechanicalConnections(IEnumerable<Endpoint> endpoints, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            var missing = endpoints.Where(endpoint => endpoint.HasMechanicalAccepts && endpoint.Connection.Length == 0)
                .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal).GroupBy(AssetDefinitionIdentity).Select(group => group.First());
            foreach (var endpoint in missing)
                diagnostics.Add(new ExportPlacementDiagnostic("mechanical_accepts_without_connection", false,
                    "资产 [" + endpoint.Path + "] 接受机械接口，缺少自身机械安装接口 connection_interface。请依据实际安装方式核实并补齐。"));
        }

        private static void CheckAssetDescriptions(IEnumerable<Endpoint> endpoints, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            var definitions = endpoints.OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal).GroupBy(AssetDefinitionIdentity);
            foreach (var group in definitions)
            {
                var description = group.FirstOrDefault(endpoint => endpoint.ContentsDescriptionFields.Length != 0);
                if (description != null)
                    diagnostics.Add(new ExportPlacementDiagnostic("contents_in_asset_description", false,
                        "资产 [" + description.Path + "] 的 " + string.Join("、", description.ContentsDescriptionFields) +
                        " 含内容物描述。请仅记录硬件用途，容纳能力用 " + PropertyRules.CanContain + " 表示。"));
                var interfaces = group.FirstOrDefault(endpoint => endpoint.ContentsInterfaceFields.Length != 0);
                if (interfaces != null)
                    diagnostics.Add(new ExportPlacementDiagnostic("contents_in_asset_interface", false,
                        "资产 [" + interfaces.Path + "] 的 " + string.Join("、", interfaces.ContentsInterfaceFields) +
                        " 含非机械接口值。请保存清理后的源文件属性。"));
            }
        }

        private static void CheckAssetSlotDefinitions(IEnumerable<Endpoint> endpoints, ICollection<ExportPlacementDiagnostic> diagnostics)
        {
            var missing = endpoints.Where(endpoint => endpoint.MissingSlotDefinitionFields.Count != 0)
                .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
                .GroupBy(endpoint => string.IsNullOrEmpty(endpoint.SourcePath) ? "instance:" + endpoint.Node.Id : "source:" + endpoint.SourcePath,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var group in missing)
                diagnostics.Add(new ExportPlacementDiagnostic("asset_slot_definition_undeclared", false,
                    "资产 [" + group.First().Path + "] 未填写 " + string.Join("、", group.SelectMany(endpoint => endpoint.MissingSlotDefinitionFields).Distinct()) +
                    "。请按实际装配在文件级属性中补齐。"));
        }

        private static void CheckCapacity(IEnumerable<Endpoint> endpoints, ILookup<string, Observation> byParent,
            ICollection<ExportPlacementDiagnostic> diagnostics, string assemblyUuid, ExportPlacementReviewContext context)
        {
            var ordered = endpoints.OrderBy(parent => parent.Path, StringComparer.Ordinal).ToList();
            foreach (var equivalent in ordered.GroupBy(AssetDefinitionIdentity))
            {
                var missing = equivalent.Where(parent => !parent.HasCapacityDeclaration &&
                    (parent.IsStationFixture || parent.HasMechanicalAccepts || byParent.Contains(parent.Node.Id)))
                    .OrderByDescending(parent => byParent.Contains(parent.Node.Id)).FirstOrDefault();
                if (missing != null)
                {
                    diagnostics.Add(new ExportPlacementDiagnostic("slot_capacity_undeclared", false,
                        "资产 [" + missing.Path + "] 未声明设计总槽数 slots_num。请依据实际安装位置确认容量（含空位）。"));
                    continue;
                }
                var zero = equivalent.FirstOrDefault(parent => parent.Capacity == 0 && parent.HasMechanicalAccepts && !parent.IsToolSideQuickChanger);
                if (zero != null)
                    diagnostics.Add(new ExportPlacementDiagnostic("zero_capacity_with_mechanical_interfaces", false,
                        "资产 [" + zero.Path + "] 声明 slots_num=0 且接受机械接口。请核对可用槽位及接受接口是否符合设计。"));
            }

            var evidence = ordered.Where(parent => parent.Capacity.HasValue)
                .Select(parent => new CapacityEvidence(parent, byParent[parent.Node.Id]))
                .GroupBy(item => Tuple.Create(AssetDefinitionIdentity(item.Parent), item.Parent.Capacity));
            foreach (var group in evidence)
            {
                foreach (var abnormal in group.Where(item => item.Overflow || item.Overlap))
                    ReportCapacity(abnormal, diagnostics, assemblyUuid, context);
                if (group.Any(item => item.IsComplete)) continue;

                // One evidence-based diagnostic represents incomplete copies of the same actual CAD type.
                var best = group.Where(item => !item.Overflow && !item.Overlap)
                    .OrderByDescending(item => item.Observed).ThenByDescending(item => item.BoundSlots)
                    .ThenByDescending(item => item.BoundChildren).ThenBy(item => item.Parent.Path, StringComparer.Ordinal).FirstOrDefault();
                if (best != null) ReportCapacity(best, diagnostics, assemblyUuid, context);
            }
        }

        private sealed class CapacityEvidence
        {
            internal CapacityEvidence(Endpoint parent, IEnumerable<Observation> observations)
            {
                Parent = parent;
                Rows = observations.OrderBy(row => row.SlotKey, StringComparer.Ordinal).ToArray();
                BoundChildren = Rows.Select(row => row.Child.Node.Id).Distinct(StringComparer.Ordinal).Count();
                var slots = Rows.GroupBy(row => row.SlotKey, StringComparer.Ordinal).ToArray();
                BoundSlots = slots.Length;
                var witnessed = new List<RelationPose>();
                foreach (var slot in slots)
                {
                    var row = slot.First();
                    if (slot.Count() > 1 || row.HasOtherParent) { Candidates++; continue; }
                    if (witnessed.Any(pose => SamePose(pose, row.Location))) { Overlap = true; continue; }
                    witnessed.Add(row.Location);
                }
                Observed = witnessed.Count;
            }

            internal readonly Endpoint Parent;
            internal readonly Observation[] Rows;
            internal readonly int Observed, BoundSlots, BoundChildren, Candidates;
            internal readonly bool Overlap;
            internal bool Overflow { get { return BoundSlots > Parent.Capacity.Value; } }
            internal bool IsComplete
            { get { return Observed == Parent.Capacity.Value && BoundSlots == Observed && BoundChildren == Observed; } }
        }

        private static Tuple<string, string, string> AssetDefinitionIdentity(Endpoint endpoint)
        {
            var identity = string.IsNullOrEmpty(endpoint.SourcePath) || string.IsNullOrEmpty(endpoint.Configuration)
                ? "instance:" + endpoint.Node.Id : "source:" + endpoint.SourcePath.ToUpperInvariant();
            return Tuple.Create(identity, endpoint.Configuration, endpoint.Node.AssetId);
        }

        private static void ReportCapacity(CapacityEvidence item, ICollection<ExportPlacementDiagnostic> diagnostics,
            string assemblyUuid, ExportPlacementReviewContext context)
        {
            var summary = string.Format(CultureInfo.InvariantCulture,
                "父级 [{0}] 声明 slots_num={1}，有 {2} 个已绑定槽位和 {3} 个绑定子实例，明确占用 {4} 个。",
                item.Parent.Path, item.Parent.Capacity, item.BoundSlots, item.BoundChildren, item.Observed);
            if (item.Overflow) summary += "已绑定槽位数超过设计容量。";
            if (item.Overlap) summary += "有独立槽位的安装位姿重合。";
            if (item.Candidates != 0) summary += "有槽位包含备选子级或子级有多个父级。";
            var key = CapacityReviewKey(assemblyUuid, context, item);
            string reason;
            if (!item.Overflow && key != null && context.Reasons != null && context.Reasons.TryGetValue(key, out reason) && !string.IsNullOrWhiteSpace(reason))
                diagnostics.Add(new ExportPlacementDiagnostic("slot_count_reviewed", false, summary + "本机复核：" + reason, key, reason));
            else
                diagnostics.Add(new ExportPlacementDiagnostic("slot_count_mismatch", false, summary + "请人工核对容量和装载情况。", key));
        }

        private static string CapacityReviewKey(string assemblyUuid, ExportPlacementReviewContext context, CapacityEvidence evidence)
        {
            var parent = evidence.Parent;
            if (context == null || string.IsNullOrEmpty(context.Configuration) || context.OccurrenceIdentity == null ||
                string.IsNullOrEmpty(assemblyUuid) || string.IsNullOrEmpty(parent.Node.AssetId)) return null;
            var parentIdentity = context.OccurrenceIdentity(parent.Node);
            if (string.IsNullOrEmpty(parentIdentity)) return null;
            var occupied = new List<string>();
            foreach (var row in evidence.Rows)
            {
                var childIdentity = context.OccurrenceIdentity(row.Child.Node);
                if (string.IsNullOrEmpty(childIdentity) || string.IsNullOrEmpty(row.Child.Node.AssetId) || string.IsNullOrEmpty(row.SlotKey))
                    return null;
                occupied.Add(ExactReviewValues(row.Child.Node.Id, row.Child.Path, row.Child.Node.AssetId, childIdentity,
                    row.Child.Configuration, row.SlotKey, row.BindingId, row.HasSlotAnchor ? "shared" : "ordinary", row.HasOtherParent ? "multiple" : "single"));
            }
            var header = ExactReviewValues("placement-capacity-review-v1", assemblyUuid, context.Configuration,
                context.ConfigurationId.ToString(CultureInfo.InvariantCulture), parent.Node.Id, parent.Path, parent.Node.AssetId,
                parentIdentity, parent.Configuration, parent.Capacity.Value.ToString(CultureInfo.InvariantCulture),
                evidence.Observed.ToString(CultureInfo.InvariantCulture), evidence.Overlap ? "overlap" : "distinct");
            return FileHash.Sha256Text(header + string.Concat(occupied.OrderBy(value => value, StringComparer.Ordinal)));
        }

        private static string ExactReviewValues(params string[] values)
        {
            var key = new StringBuilder();
            foreach (var value in values)
                key.Append(value == null ? "-1" : value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            return key.ToString();
        }

        private static string Value(IDictionary<string, string> properties, string key)
        {
            string value;
            return properties != null && properties.TryGetValue(key, out value) ? (value ?? string.Empty).Trim() : string.Empty;
        }

        private static string Describe(Observation row)
        { return "[" + row.Parent.Path + "] → [" + row.Child.Path + "]"; }

        private static bool SamePose(RelationPose a, RelationPose b)
        { return PositionDistance(a, b) <= PositionTolerance && AngleDistance(a, b) <= AngleTolerance; }

        private static double PositionDistance(RelationPose a, RelationPose b)
        {
            var x = a.Tx - b.Tx; var y = a.Ty - b.Ty; var z = a.Tz - b.Tz;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static double AngleDistance(RelationPose a, RelationPose b)
        {
            var dot = Math.Abs(a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw);
            return 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI;
        }

        private static string Difference(RelationPose a, RelationPose b)
        { return string.Format(CultureInfo.InvariantCulture, "平移差 {0:0.###} mm，转角差 {1:0.###}°", PositionDistance(a, b) * 1000, AngleDistance(a, b)); }
    }
}
