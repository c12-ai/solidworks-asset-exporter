using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunExportPlacementDiagnosticTests()
        {
            Run("Same mounted door transforms require one state", ExportDoorState);
            Run("Independent split doors may share an origin only with review", ExportSplitDoorOrigin);
            Run("Repeated complete racks compare corresponding physical positions", ExportSimilarSlotPositions);
            Run("Shared slot compares live child pose in the slot frame", ExportSlotLocalPose);
            Run("Slot count distinguishes capacity, occupied positions and candidates", ExportSlotCounts);
            Run("Undeclared asset slot metadata is reported once per source file", ExportMissingSlotDefinitions);
            Run("Mechanical accepts require a source connection diagnostic, including the root Asset", ExportMissingMechanicalConnections);
            Run("Capacity declaration warnings use fixture roles, mechanical interfaces and saved bindings", ExportSlotDeclarations);
            Run("Capacity declaration warnings are deduplicated by exact CAD type", ExportSlotDeclarationIdentity);
            Run("An integral tool-side quick changer may declare zero capacity but cannot hide overflow", ExportToolSideCapacity);
            Run("A complete slot representative covers incomplete copies of the same CAD type only", ExportSlotRepresentatives);
            Run("Missing slot templates report the best evidence and every actual overflow", ExportBestSlotEvidence);
            Run("Capacity review only resolves the exact configuration, parent version and bound slots", ExportCapacityReview);
            Run("Local capacity reviews reject malformed or unsupported files in full", ExportLocalCapacityReview);
            Run("Legacy multiple parents require repair before export", ExportMultipleParents);
        }

        private sealed class PlacementExample
        {
            internal readonly List<ExportNode> Assets = new List<ExportNode>();
            internal readonly AssemblyRelations Graph = new AssemblyRelations { Slots = new List<AssemblySlot>() };

            internal ExportNode Asset(string instance, string type, string name, int? slots = null, string connection = null)
            {
                var source = Node(instance, true, false);
                var asset = new ExportNode
                {
                    Id = instance, Name = instance, Source = source, GeometryUuid = type, AssetId = type + ":1", Kind = ExportNodeKind.Asset
                };
                asset.Properties[PropertyRules.PartName] = name;
                asset.Properties[PropertyRules.SlotType] = "place";
                asset.Properties[PropertyRules.AssetEngagement] = "loose";
                if (connection != null) asset.Properties[PropertyRules.ConnectionInterface] = connection;
                if (slots.HasValue) asset.Properties[PropertyRules.SlotsNum] = slots.Value.ToString();
                Assets.Add(asset);
                return asset;
            }

            internal AssemblyRelation Bind(ExportNode parent, ExportNode child, RelationPose pose, string sharedSlot = null)
            {
                var relation = new AssemblyRelation { ParentNodeId = parent.Id, ChildNodeId = child.Id, Pose = pose, SlotId = sharedSlot };
                Graph.Relations.Add(relation);
                return relation;
            }

            internal IList<ExportPlacementDiagnostic> Analyze(ExportPlacementReviewContext reviews = null)
            { return ExportPlacementDiagnostics.Analyze(Assets, Graph, node => node.Properties, reviews); }
        }

        private static RelationPose MeterPose(double x = 0, double z = 0, double halfRotationY = 0, bool flip = false)
        {
            return new RelationPose
            {
                Tx = x, Tz = z, Qy = (flip ? -1 : 1) * Math.Sin(halfRotationY), Qw = (flip ? -1 : 1) * Math.Cos(halfRotationY)
            };
        }

        private static void ExportDoorState()
        {
            var fixture = new PlacementExample();
            var first = fixture.Asset("主体-1", "body", "培养箱主体");
            var second = fixture.Asset("主体-2", "body", "培养箱主体");
            ((FakeNode)second.Source).WorldTransformValue = BindingTransform(1, 3, 4, Math.PI / 2);
            var doorA = fixture.Asset("总成-1", "door", "培养箱下翻门总成");
            var doorB = fixture.Asset("总成-2", "door", "培养箱下翻门总成");
            fixture.Bind(first, doorA, MeterPose());
            var relation = fixture.Bind(second, doorB, MeterPose(flip: true));
            fixture.Bind(first, fixture.Asset("其他门", "other-door", "其他设备门", connection: "设备.门板"), MeterPose(0.1));
            var duplicate = fixture.Bind(first, fixture.Asset("备用门", "door", "培养箱下翻门总成"), MeterPose(0.2));
            relation.Pose = MeterPose(0.04, halfRotationY: Math.PI / 4);
            Equal(0, fixture.Analyze().Count(item => item.Code == "door_state_mismatch")); // No uniquely comparable role.
            fixture.Graph.Relations.Remove(duplicate);
            var mismatch = fixture.Analyze().Single(item => item.Code == "door_state_mismatch");
            Equal(true, mismatch.IsBlocking);
            Equal("door_state_mismatch", mismatch.Code);
            Equal(true, mismatch.Message.Contains("转角差 90°"));
            relation.Pose = MeterPose(flip: true);
            Equal(0, fixture.Analyze().Count(item => item.Code == "door_state_mismatch"));
        }

        private static void ExportSplitDoorOrigin()
        {
            var fixture = new PlacementExample();
            var fridge = fixture.Asset("冰箱-1", "fridge", "冰箱");
            var left = fixture.Asset("左门-1", "left", "冰箱推拉门组件A");
            var right = fixture.Asset("右门-1", "right", "冰箱推拉门组件B");
            var leftEdge = fixture.Bind(fridge, left, MeterPose());
            var rightEdge = fixture.Bind(fridge, right, MeterPose(0.00001));
            fixture.Bind(fridge, fixture.Asset("门附件-1", "handle", "冰箱门把手"), MeterPose());
            var overlap = fixture.Analyze().Single(item => item.Code == "split_door_origin_overlap");
            Equal("split_door_origin_overlap", overlap.Code);
            Equal(false, overlap.IsBlocking);
            Equal(true, overlap.Message.Contains("左门-1") && overlap.Message.Contains("右门-1"));
            leftEdge.SlotId = rightEdge.SlotId = "shared";
            fixture.Graph.Slots.Add(new AssemblySlot { Id = "shared", ParentNodeId = fridge.Id, Pose = MeterPose() });
            Equal(0, fixture.Analyze().Count(item => item.Code == "split_door_origin_overlap"));
        }

        private static void ExportSimilarSlotPositions()
        {
            var fixture = new PlacementExample();
            for (var rackNumber = 1; rackNumber <= 3; rackNumber++)
            {
                var rack = fixture.Asset("底座-" + rackNumber, "rack", "底座", slots: 4);
                for (var slotNumber = 0; slotNumber < 4; slotNumber++)
                {
                    var child = fixture.Asset("底座-" + rackNumber + "/离心管-" + slotNumber, "tube", "离心管", connection: "离心管.D11");
                    var x = slotNumber < 2 ? -0.0215 : 0.0215;
                    if (rackNumber == 3 && slotNumber == 3) x -= 0.001;
                    fixture.Bind(rack, child, MeterPose(x, slotNumber % 2 == 0 ? -0.0265 : -0.0085));
                }
            }

            var warning = fixture.Analyze().Single();
            Equal("slot_pose_variance", warning.Code);
            Equal(false, warning.IsBlocking);
            Equal(true, warning.Message.Contains("平移差 1 mm") && warning.Message.Contains("底座-3/离心管-3"));

            fixture.Graph.Relations.RemoveAll(row => row.ParentNodeId == "底座-2" && row.ChildNodeId.EndsWith("离心管-0"));
            var results = fixture.Analyze();
            Equal(1, results.Count(item => item.Code == "slot_pose_variance"));
            Equal(1, results.Count(item => item.Code == "slot_count_mismatch"));
        }

        private static void ExportSlotLocalPose()
        {
            var fixture = new PlacementExample();
            var parentA = fixture.Asset("底座-1", "rack", "底座", slots: 1);
            var parentB = fixture.Asset("底座-2", "rack", "底座", slots: 1);
            fixture.Graph.Slots.Add(new AssemblySlot { Id = "a", ParentNodeId = parentA.Id, Pose = MeterPose() });
            fixture.Graph.Slots.Add(new AssemblySlot { Id = "b", ParentNodeId = parentB.Id, Pose = MeterPose(halfRotationY: Math.PI / 4) });
            fixture.Bind(parentA, fixture.Asset("圆瓶-1", "bottle", "圆瓶"), MeterPose(0.01), "a");
            var atB = fixture.Bind(parentB, fixture.Asset("圆瓶-2", "bottle", "圆瓶"), MeterPose(z: -0.01, halfRotationY: Math.PI / 4), "b");
            Equal(0, fixture.Analyze().Count);
            atB.Pose = MeterPose(z: -0.011, halfRotationY: Math.PI / 4);
            Equal(true, fixture.Analyze().Single().Message.Contains("未形成多数基准"));
        }

        private static void ExportSlotCounts()
        {
            var fixture = new PlacementExample();
            var a = fixture.Asset("无关系的父级", "holder", "底座", slots: 4);
            var b = fixture.Asset("有备选的父级", "fixture", "底座", slots: 1);
            fixture.Asset("容量未知的父级", "other-holder", "底座");
            fixture.Asset("零容量的父级", "zero-holder", "底座", slots: 0);
            fixture.Graph.Slots.Add(new AssemblySlot { Id = "shared", ParentNodeId = b.Id, Pose = MeterPose() });
            fixture.Bind(b, fixture.Asset("配件1", "part", "配件"), MeterPose(), "shared");
            fixture.Bind(b, fixture.Asset("配件2", "part", "配件"), MeterPose(), "shared");
            var warnings = fixture.Analyze().Where(item => item.Code == "slot_count_mismatch").ToList();
            Equal(2, warnings.Count);
            Equal(true, warnings.Any(item => item.Message.Contains(a.Name) && item.Message.Contains("明确占用 0 个")));
            Equal(true, warnings.Any(item => item.Message.Contains(b.Name) && item.Message.Contains("1 个已绑定槽位和 2 个绑定子实例")));
        }

        private static void ExportMissingSlotDefinitions()
        {
            var fixture = new PlacementExample();
            var first = TypedCapacityParent(fixture, "A-1");
            var otherConfiguration = TypedCapacityParent(fixture, "A-2", "Open", @"c:\MODELS\RACK.SLDASM");
            var partial = TypedCapacityParent(fixture, "B-1", sourcePath: @"C:\Models\second.SLDASM");
            fixture.Asset("完整资产", "complete", "完整资产");
            var zero = fixture.Asset("零槽资产", "zero", "安装件", slots: 0);
            zero.Properties[PropertyRules.AssetEngagement] = "tight";
            var missingSelection = TypedCapacityParent(fixture, "C-1", sourcePath: @"C:\Models\third.SLDASM");
            missingSelection.Properties.Remove(PropertyRules.SlotType);
            missingSelection.Properties[PropertyRules.AssetEngagement] = "tight";
            var onlyEngagement = fixture.Asset("无容量且未填接合", "unknown", "安装件");
            onlyEngagement.Properties.Remove(PropertyRules.AssetEngagement);
            foreach (var asset in new[] { first, otherConfiguration })
            {
                asset.Properties.Remove(PropertyRules.SlotType);
                asset.Properties.Remove(PropertyRules.AssetEngagement);
            }
            partial.Properties[PropertyRules.AssetEngagement] = " ";

            var warnings = fixture.Analyze().Where(warning => warning.Code == "asset_slot_definition_undeclared").ToList();
            Equal(4, warnings.Count);
            True(warnings.All(warning => !warning.IsBlocking));
            True(warnings.Single(warning => warning.Message.Contains("[" + first.Source.InstancePath + "]"))
                .Message.Contains("slot_type、engagement"));
            True(warnings.Single(warning => warning.Message.Contains("[" + partial.Source.InstancePath + "]"))
                .Message.Contains("未填写 engagement"));
            True(warnings.Single(warning => warning.Message.Contains("[" + missingSelection.Source.InstancePath + "]"))
                .Message.Contains("未填写 slot_type"));
            True(!warnings.Single(warning => warning.Message.Contains("[" + onlyEngagement.Source.InstancePath + "]"))
                .Message.Contains("slot_type"));
            True(!warnings.Any(warning => warning.Message.Contains("[" + zero.Source.InstancePath + "]")));
            True(!first.Properties.ContainsKey(PropertyRules.SlotType));
            Equal(" ", partial.Properties[PropertyRules.AssetEngagement]);
        }

        private static void ExportMissingMechanicalConnections()
        {
            var fixture = new PlacementExample();
            var root = fixture.Asset("root", "workstation", "工位底座", slots: 2);
            root.Properties[PropertyRules.IsWorkstation] = "1";
            root.Properties[PropertyRules.AcceptsInterfaces] = "载台.A;内容物.溶液";
            var first = TypedCapacityParent(fixture, "A-1");
            var copy = TypedCapacityParent(fixture, "A-2", sourcePath: @"c:\MODELS\RACK.SLDASM");
            var differentConfiguration = TypedCapacityParent(fixture, "A-3", "Other");
            foreach (var asset in new[] { first, copy, differentConfiguration })
            {
                asset.Properties[PropertyRules.LegacyAcceptsInterface] = "管架.定位座";
                asset.Properties[PropertyRules.ConnectionInterface] = " 溶液.清洗液 ";
            }
            var valid = fixture.Asset("已定义上行接口", "configured", "夹具", connection: "工作台.底座");
            valid.Properties[PropertyRules.AcceptsInterfaces] = "载台.A";
            var contents = fixture.Asset("仅装内容物", "vessel", "容器", connection: " ");
            contents.Properties[PropertyRules.AcceptsInterfaces] = "试剂;内容物.溶液";
            contents.Properties[PropertyRules.DesignPurpose] = "存放细胞样本";
            valid.Properties[PropertyRules.DesignPurpose] = "定位样品瓶，保留二维码安装位";

            var diagnostics = fixture.Analyze();
            var warnings = diagnostics.Where(item => item.Code == "mechanical_accepts_without_connection").ToArray();
            Equal(3, warnings.Length);
            True(warnings.All(item => !item.IsBlocking && item.ReviewKey == null));
            True(new[] { root, first, differentConfiguration }.All(asset =>
                warnings.Any(item => item.Message.Contains("[" + asset.Source.InstancePath + "]"))));
            Equal("载台.A;内容物.溶液", root.Properties[PropertyRules.AcceptsInterfaces]);
            Equal(" 溶液.清洗液 ", first.Properties[PropertyRules.ConnectionInterface]);
            var description = diagnostics.Single(item => item.Code == "contents_in_asset_description");
            True(description.Message.Contains(contents.Source.InstancePath) && description.Message.Contains(PropertyRules.DesignPurpose));
            Equal(false, description.Message.Contains("存放细胞样本"));
            var contentsWarnings = diagnostics.Where(item => item.Code == "contents_in_asset_interface").ToList();
            True(contentsWarnings.Any(item => item.Message.Contains(root.Source.InstancePath) && item.Message.Contains(PropertyRules.AcceptsInterfaces)));
            Equal(false, contentsWarnings.Any(item => item.Message.Contains(valid.Source.InstancePath)));
        }

        private static void ExportSlotDeclarations()
        {
            var fixture = new PlacementExample();
            var scaleTray = fixture.Asset("称量载物台", "scale", "称量载物台");
            scaleTray.Properties[PropertyRules.AssetClass] = "station";
            scaleTray.Properties[PropertyRules.IsFixture] = "true";
            scaleTray.Properties[PropertyRules.SlotsNum] = " ";
            var emptyStation = fixture.Asset("不提供槽位的固定夹具", "empty", "固定夹具", slots: 0);
            emptyStation.Properties[PropertyRules.AssetClass] = "station";
            emptyStation.Properties[PropertyRules.IsFixture] = "1";
            var movable = fixture.Asset("无槽的可搬动夹具", "movable", "夹具");
            movable.Properties[PropertyRules.AssetClass] = "movable";
            movable.Properties[PropertyRules.IsFixture] = "1";
            var vessel = fixture.Asset("只容纳内容物的容器", "vessel", "容器");
            vessel.Properties[PropertyRules.CanContain] = "true";
            vessel.Properties[PropertyRules.AcceptsInterfaces] = "试剂;内容物.溶液";
            vessel.Properties[PropertyRules.LegacyAcceptsInterface] = "样本";
            var holder = fixture.Asset("空置的机械接收位", "mechanical", "放置架", connection: "支架.安装座");
            holder.Properties[PropertyRules.AcceptsInterfaces] = "离心管.D11;样本";
            var emptyHolder = fixture.Asset("零容量的机械接收位", "zero", "放置架", slots: 0, connection: "支架.安装座");
            emptyHolder.Properties[PropertyRules.LegacyAcceptsInterface] = "离心管.D11";
            var bound = fixture.Asset("已有子实例的父级", "parent", "父级");
            fixture.Bind(bound, fixture.Asset("子实例", "child", "子实例"), MeterPose());

            var diagnostics = fixture.Analyze().Where(item => item.Code == "slot_capacity_undeclared" ||
                item.Code == "zero_capacity_with_mechanical_interfaces").ToList();
            Equal(4, diagnostics.Count);
            True(diagnostics.All(item => !item.IsBlocking && item.ReviewKey == null));
            var declarations = diagnostics.Where(item => item.Code == "slot_capacity_undeclared").ToArray();
            Equal(3, declarations.Length);
            True(new[] { scaleTray, holder, bound }.All(asset => declarations.Any(item => item.Message.Contains("[" + asset.Source.InstancePath + "]"))));
            var zero = diagnostics.Single(item => item.Code == "zero_capacity_with_mechanical_interfaces");
            True(zero.Message.Contains("[" + emptyHolder.Source.InstancePath + "]"));
        }

        private static void ExportSlotDeclarationIdentity()
        {
            var fixture = new PlacementExample();
            var first = TypedCapacityParent(fixture, "空置实例");
            var observed = TypedCapacityParent(fixture, "实际承载实例", sourcePath: @"c:\MODELS\RACK.SLDASM");
            var otherConfiguration = TypedCapacityParent(fixture, "其它引用配置", "Open");
            var otherVersion = TypedCapacityParent(fixture, "其它资产版本");
            var otherSource = TypedCapacityParent(fixture, "其它源文件", sourcePath: @"C:\models\distinct.SLDASM");
            otherVersion.AssetId = "holder:2";
            foreach (var node in fixture.Assets)
            {
                node.Properties.Remove(PropertyRules.SlotsNum);
                node.Properties[PropertyRules.AcceptsInterfaces] = "机械连接";
                node.Properties[PropertyRules.ConnectionInterface] = "支架.安装座";
            }
            fixture.Bind(observed, fixture.Asset("已装子件", "child", "子件"), MeterPose());
            var warnings = fixture.Analyze();
            Equal(4, warnings.Count);
            True(warnings.All(item => item.Code == "slot_capacity_undeclared"));
            True(!warnings.Any(item => item.Message.Contains("[" + first.Source.InstancePath + "]")));
            True(new[] { observed, otherConfiguration, otherVersion, otherSource }.All(asset =>
                warnings.Any(item => item.Message.Contains("[" + asset.Source.InstancePath + "]"))));

            foreach (var node in fixture.Assets.Where(node => node.AssetId.StartsWith("holder:", StringComparison.Ordinal)))
                node.Properties[PropertyRules.SlotsNum] = "0";
            warnings = fixture.Analyze();
            Equal(4, warnings.Count(item => item.Code == "zero_capacity_with_mechanical_interfaces"));
            Equal(1, warnings.Count(item => item.Code == "slot_count_mismatch" && item.Message.Contains(observed.Name)));
        }

        private static void ExportToolSideCapacity()
        {
            var fixture = new PlacementExample();
            var toolSide = fixture.Asset("整体工具侧", "tool-side", "工具侧快换", slots: 0, connection: "工具侧快换");
            toolSide.Properties[PropertyRules.IsQuickChanger] = "1";
            toolSide.Properties[PropertyRules.QuickChangerSide] = "tool_side";
            toolSide.Properties[PropertyRules.AcceptsInterfaces] = "工具安装.电爪";
            Equal(0, fixture.Analyze().Count);

            toolSide.Properties.Remove(PropertyRules.SlotsNum);
            Equal("slot_capacity_undeclared", fixture.Analyze().Single().Code);
            toolSide.Properties[PropertyRules.SlotsNum] = "0";
            var robotSide = fixture.Asset("机器人侧", "robot-side", "机器人侧快换", slots: 0, connection: "机械臂.法兰");
            robotSide.Properties[PropertyRules.IsQuickChanger] = "1";
            robotSide.Properties[PropertyRules.QuickChangerSide] = "robot_side";
            robotSide.Properties[PropertyRules.AcceptsInterfaces] = "工具侧快换";
            Equal("zero_capacity_with_mechanical_interfaces", fixture.Analyze().Single().Code);

            fixture.Bind(toolSide, fixture.Asset("新挂接工具", "tool", "工具"), MeterPose());
            var diagnostics = fixture.Analyze();
            Equal(2, diagnostics.Count);
            var overflow = diagnostics.Single(item => item.Code == "slot_count_mismatch");
            True(overflow.Message.Contains(toolSide.Name) && overflow.Message.Contains("超过设计容量"));
        }

        private static void ExportCapacityReview()
        {
            var fixture = new PlacementExample();
            fixture.Graph.AssemblyUuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
            var parent = fixture.Asset("底座-1", "holder", "底座", slots: 4);
            var first = fixture.Asset("瓶-1", "bottle", "瓶");
            var second = fixture.Asset("瓶-2", "bottle", "瓶");
            var third = fixture.Asset("瓶-3", "bottle", "瓶");
            fixture.Bind(parent, first, MeterPose());
            var edge = fixture.Bind(parent, second, MeterPose());
            var identities = fixture.Assets.ToDictionary(node => node.Id, node => "occurrence:" + node.Id);
            var reasons = new Dictionary<string, string>();
            var context = new ExportPlacementReviewContext { Configuration = "Default", ConfigurationId = 9,
                OccurrenceIdentity = node => identities[node.Id], Reasons = reasons };

            var warning = fixture.Analyze(context).Single();
            Equal("slot_count_mismatch", warning.Code);
            Equal(64, warning.ReviewKey.Length);
            reasons.Add(warning.ReviewKey, "已核对：底座共有四个位置，两个空位。");
            fixture.Graph.Relations.Reverse();
            var reviewed = fixture.Analyze(context).Single();
            Equal("slot_count_reviewed", reviewed.Code);
            Equal(warning.ReviewKey, reviewed.ReviewKey);
            Equal(reasons[warning.ReviewKey], reviewed.ReviewReason);
            True(!reviewed.Message.Contains("请人工核对"));

            context.Configuration = "default";
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            context.Configuration = "Default";
            context.ConfigurationId = 10;
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            context.ConfigurationId = 9;
            fixture.Graph.AssemblyUuid = "bbbbbbbb-bbbb-5bbb-8bbb-bbbbbbbbbbbb";
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            fixture.Graph.AssemblyUuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
            parent.AssetId = "holder:2";
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            parent.AssetId = "holder:1";
            parent.Properties[PropertyRules.SlotsNum] = "5";
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            parent.Properties[PropertyRules.SlotsNum] = "4";
            var identity = identities[parent.Id];
            identities[parent.Id] = "replacement";
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            identities[parent.Id] = identity;
            edge.ChildNodeId = third.Id;
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            edge.ChildNodeId = second.Id;
            edge.SlotId = "alternate-slot";
            fixture.Graph.Slots.Add(new AssemblySlot { Id = edge.SlotId, ParentNodeId = parent.Id, Pose = MeterPose() });
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
            edge.SlotId = null;
            fixture.Bind(parent, third, MeterPose());
            Equal("slot_count_mismatch", fixture.Analyze(context).Single().Code);
        }

        private static ExportNode TypedCapacityParent(PlacementExample fixture, string instance, string configuration = "Default",
            string sourcePath = @"C:\models\rack.SLDASM")
        {
            var parent = fixture.Asset(instance, "holder", "底座", slots: 2);
            var model = ((ICadClassificationSource)parent.Source).ClassificationModel;
            model.FullPath = sourcePath;
            model.Configuration = configuration;
            return parent;
        }

        private static void ExportSlotRepresentatives()
        {
            var fixture = new PlacementExample();
            var full = TypedCapacityParent(fixture, "满载");
            var copy = TypedCapacityParent(fixture, "复用");
            var otherConfig = TypedCapacityParent(fixture, "另一配置", "Open");
            var otherSource = TypedCapacityParent(fixture, "另一源文件", sourcePath: @"C:\models\different.SLDASM");
            fixture.Bind(full, fixture.Asset("管-1", "tube", "管"), MeterPose(0));
            fixture.Bind(full, fixture.Asset("管-2", "tube", "管"), MeterPose(.02));
            var warnings = fixture.Analyze().Where(item => item.Code == "slot_count_mismatch").ToArray();
            Equal(2, warnings.Length);
            True(warnings.Any(item => item.Message.Contains(otherConfig.Name)));
            True(warnings.Any(item => item.Message.Contains(otherSource.Name)));
            True(!warnings.Any(item => item.Message.Contains(copy.Name)));
            fixture.Bind(copy, fixture.Asset("管-3", "tube", "管"), MeterPose(0));
            fixture.Bind(copy, fixture.Asset("管-4", "tube", "管"), MeterPose(.021));
            True(fixture.Analyze().Any(item => item.Code == "slot_pose_variance"));
        }

        private static void ExportBestSlotEvidence()
        {
            var fixture = new PlacementExample();
            fixture.Graph.AssemblyUuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
            var empty = TypedCapacityParent(fixture, "无关系");
            var partial = TypedCapacityParent(fixture, "单独槽位");
            var shared = TypedCapacityParent(fixture, "共享槽位");
            fixture.Bind(partial, fixture.Asset("管-1", "tube", "管"), MeterPose(0));
            fixture.Graph.Slots.Add(new AssemblySlot { Id = "shared", ParentNodeId = shared.Id, Pose = MeterPose() });
            fixture.Bind(shared, fixture.Asset("管-2", "tube", "管"), MeterPose(), "shared");
            fixture.Bind(shared, fixture.Asset("管-3", "tube", "管"), MeterPose(), "shared");
            var reasons = new Dictionary<string, string>();
            var context = new ExportPlacementReviewContext { Configuration = "Default", ConfigurationId = 1,
                OccurrenceIdentity = node => node.Source.InstanceId, Reasons = reasons };
            var warning = fixture.Analyze(context).Single();
            True(warning.Message.Contains(partial.Name));
            True(!warning.Message.Contains(empty.Name));
            reasons.Add(warning.ReviewKey, "该模板只完成一个槽位的实物核验。");
            Equal("slot_count_reviewed", fixture.Analyze(context).Single().Code);

            foreach (var name in new[] { "超量1", "超量2" })
            {
                var overflow = TypedCapacityParent(fixture, name);
                for (var i = 0; i < 3; i++)
                    fixture.Bind(overflow, fixture.Asset(name + "/管-" + i, "tube", "管"), MeterPose(.02 * i));
            }
            var excess = fixture.Analyze(context).Where(item => item.Code == "slot_count_mismatch").ToArray();
            Equal(2, excess.Length);
            True(excess.All(item => item.Message.Contains("超过设计容量")));
            reasons.Add(excess[0].ReviewKey, "仍报告超过已声明容量。");
            Equal(2, fixture.Analyze(context).Count(item => item.Code == "slot_count_mismatch"));
        }

        private static void ExportLocalCapacityReview()
        {
            const string assembly = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
            var directory = TempDirectory();
            try
            {
                Equal(0, ExportCoordinator.ReadPlacementReviews(assembly, directory).Count);
                var path = Path.Combine(directory, assembly + ".json");
                var first = new string('8', 64);
                var second = new string('a', 64);
                const string start = "{\"schema_version\":\"1.0\",\"reviews\":{";
                var valid = start + "\"" + first + "\":\"现场复核\",\"" + second + "\":\"两处空位\"}}";
                File.WriteAllText(path, valid);
                var approved = ExportCoordinator.ReadPlacementReviews(assembly, directory);
                Equal(2, approved.Count);
                Equal("现场复核", approved[first]);
                Equal("两处空位", approved[second]);
                Equal(valid, File.ReadAllText(path));
                foreach (var malformed in new[]
                {
                    valid.Replace("1.0", "2.0"), valid.Replace("reviews", "other"), valid.Substring(0, valid.Length - 1),
                    valid.Replace(second, first), valid.Replace(second, "invalid"), valid.Replace("\"两处空位\"", "null"),
                    valid.Replace("\"两处空位\"", "\" \""), valid.Replace("\"两处空位\"", "\"a\\nb\""), valid + "x", valid + valid
                })
                {
                    File.WriteAllText(path, malformed);
                    if (ExportCoordinator.ReadPlacementReviews(assembly, directory).Count != 0)
                        throw new Exception("Accepted malformed placement review: " + malformed);
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void ExportMultipleParents()
        {
            var fixture = new PlacementExample();
            var rack = fixture.Asset("停放支架", "rack", "支架");
            var robotSide = fixture.Asset("机器人侧快换", "robot-side", "快换");
            var tool = fixture.Asset("快换工具-1", "tool", "工具");
            fixture.Bind(rack, tool, MeterPose());
            var edge = fixture.Bind(robotSide, tool, MeterPose());
            var diagnostic = fixture.Analyze().Single(item => item.Code == "multiple_parents");
            Equal("multiple_parents", diagnostic.Code);
            Equal(true, diagnostic.IsBlocking);
            Equal(true, diagnostic.Message.Contains(rack.Name) && diagnostic.Message.Contains(robotSide.Name) && diagnostic.Message.Contains(tool.Name));
            edge.ChildNodeId = fixture.Asset("快换工具-2", "tool", "工具").Id;
            Equal(0, fixture.Analyze().Count(item => item.Code == "multiple_parents"));
        }
    }
}
