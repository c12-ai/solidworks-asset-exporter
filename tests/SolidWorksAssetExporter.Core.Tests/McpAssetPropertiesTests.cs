using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunMcpAssetPropertiesTests()
        {
            Run("MCP inventories and classifies an unclassified source once for all its occurrences", McpClassifyNewAsset);
            Run("MCP classification rejects incomplete, protected and stale property plans", McpRejectAssetPropertyPlans);
            Run("MCP preserves bound asset endpoints and allows explicit non-asset geometry", McpProtectBoundClassification);
            Run("MCP saves paired clamp definitions on an asset source and lists the exact keys", McpSavesClampAssetDefinition);
            Run("MCP gates asset slot type by file capacity while retaining independent engagement", McpSlotTypeUsesFileCapacity);
            Run("MCP lists and saves an optional contents capability on all same-source occurrences", McpCanContain);
            Run("MCP reads and writes only the canonical contents capability", McpUsesCanonicalContentsOnly);
            Run("MCP property saves remove stored contents interfaces and preserve mechanical interfaces", McpCleansStoredContentsInterfaces);
            Run("MCP contents cleanup respects explicit mechanical replacements and linked field guards", McpProtectsContentsInterfaceCleanup);
            Run("Asset manifests publish a top-level contents boolean and require a new version for legacy publications", PublishedCanContain);
        }

        private static void McpSavesClampAssetDefinition()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "夹持环";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "定位样品瓶";
                var fields = new Dictionary<string, object> { { "slot_type", "clamp" } };
                var request = AssetPropertyRequest(fixture, fields);
                True(fixture.Rejection("asset_properties_plan", request).Contains("slots_num"));
                fields[PropertyRules.SlotsNum] = 1;
                fields["engagement_type"] = "tight";
                True(fixture.Rejection("asset_properties_plan", request).Contains("engagement_type"));
                fields.Remove("engagement_type");
                fields["engagement"] = "free";
                True(fixture.Rejection("asset_properties_plan", request).Contains("engagement"));
                fields["engagement"] = "tight";
                fields[PropertyRules.IsAsset] = false;
                True(fixture.Rejection("asset_properties_plan", request).Contains("仅 Asset"));
                fields.Remove(PropertyRules.IsAsset);

                var plan = fixture.Call("asset_properties_plan", request);
                var changes = McpProtocol.Object(plan["changes"]);
                Equal("clamp", changes["slot_type"]);
                Equal("tight", changes["engagement"]);
                Equal(0, fixture.Child.SaveCount);
                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("clamp", fixture.Child.Properties["slot_type"]);
                Equal("tight", fixture.Child.Properties["engagement"]);
                Equal(1, fixture.Child.SaveCount);
                Equal(0, fixture.Session.Current.Relations.Count);

                var component = McpProtocol.Object(ComponentPropertyInventory(fixture)["Tube-2"]["properties"]);
                Equal("clamp", component["slot_type"]);
                Equal("tight", component["engagement"]);
                var context = new Dictionary<string, object>
                {
                    { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name }
                };
                var asset = ((object[])fixture.Call("asset_list", context)["items"]).Select(McpProtocol.Object)
                    .Single(row => Equals(row["instance"], "Tube-2"));
                Equal("clamp", asset["slot_type"]);
                Equal("tight", asset["engagement"]);
                True(!asset.ContainsKey("engagement_type"));

                request.Remove("plan_token");
                fields["slot_type"] = "";
                fields["engagement"] = "";
                True(fixture.Rejection("asset_properties_plan", request).Contains("slot_type"));
                fields["slot_type"] = "support";
                fields["engagement"] = "loose";
                True(fixture.Rejection("asset_properties_plan", request).Contains("slot_type"));
                fields["slot_type"] = "place";
                fields["engagement"] = "tight";
                True(fixture.Rejection("asset_properties_plan", request).Contains("engagement"));
                fields["engagement"] = "loose";
                request["plan_token"] = fixture.Call("asset_properties_plan", request)["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("place", fixture.Child.Properties["slot_type"]);
                Equal("loose", fixture.Child.Properties["engagement"]);

                request.Remove("plan_token");
                fields["engagement"] = "tight";
                foreach (var closureType in new[] { "thread", "press_fit", "snap_fit" })
                {
                    fields["slot_type"] = closureType;
                    var closure = McpProtocol.Object(fixture.Call("asset_properties_plan", request)["changes"]);
                    Equal(closureType, closure["slot_type"]);
                    Equal("tight", closure["engagement"]);
                }
            }
        }

        private static void McpSlotTypeUsesFileCapacity()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "快换盘";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "对接和锁定";
                fixture.Child.Properties[PropertyRules.SlotType] = "place";
                fixture.Child.Properties[PropertyRules.AssetEngagement] = "tight";
                var context = new Dictionary<string, object>
                {
                    { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name }
                };
                var assets = ((object[])fixture.Call("asset_list", context)["items"]).Select(McpProtocol.Object);
                var row = assets.Single(item => Equals(item["instance"], "Tube-1"));
                True(!row.ContainsKey(PropertyRules.SlotType));
                Equal("tight", row[PropertyRules.AssetEngagement]);
                var component = McpProtocol.Object(ComponentPropertyInventory(fixture)["Tube-1"]["properties"]);
                True(!component.ContainsKey(PropertyRules.SlotType));

                var request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.SlotsNum, 0 } });
                var plan = fixture.Call("asset_properties_plan", request);
                var changes = McpProtocol.Object(plan["changes"]);
                Equal("0", changes[PropertyRules.SlotsNum]);
                Equal("", changes[PropertyRules.SlotType]);
                True(!changes.ContainsKey(PropertyRules.AssetEngagement));
                Equal("place", fixture.Child.Properties[PropertyRules.SlotType]);
                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("", fixture.Child.Properties[PropertyRules.SlotType]);
                Equal("tight", fixture.Child.Properties[PropertyRules.AssetEngagement]);

                var chooseType = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.SlotType, "mount" } });
                True(fixture.Rejection("asset_properties_plan", chooseType).Contains("slots_num"));
                var positive = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.SlotsNum, 2 } });
                var tight = fixture.Call("asset_properties_plan", positive);
                True(!McpProtocol.Object(tight["changes"]).ContainsKey(PropertyRules.SlotType));
                positive["plan_token"] = tight["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", positive)["saved"]);
                var choosePlan = fixture.Call("asset_properties_plan", chooseType);
                Equal("mount", McpProtocol.Object(choosePlan["changes"])[PropertyRules.SlotType]);

                var loose = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.AssetEngagement, "loose" } });
                Equal("place", McpProtocol.Object(fixture.Call("asset_properties_plan", loose)["changes"])[PropertyRules.SlotType]);
            }
        }

        private static void McpCleansStoredContentsInterfaces()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "125ml锥形瓶";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "用于存放样本";
                fixture.Child.Properties[PropertyRules.ConnectionInterface] = "内容物;样本";
                fixture.Child.Properties[PropertyRules.AcceptsInterfaces] = "细胞样本;125ml锥形瓶盖；溶液.试剂;运输保护盖";
                fixture.Child.Properties[PropertyRules.LegacyAcceptsInterface] = "细胞悬液；旧机械卡口;溶液";
                var before = McpProtocol.Object(ComponentPropertyInventory(fixture)["Tube-1"]["properties"]);
                Equal("细胞样本;125ml锥形瓶盖；溶液.试剂;运输保护盖", before[PropertyRules.AcceptsInterfaces]);
                Equal(0, fixture.Child.SaveCount);

                var request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.CanContain, true } });
                var plan = fixture.Call("asset_properties_plan", request);
                var changes = McpProtocol.Object(plan["changes"]);
                Equal("125ml锥形瓶盖;运输保护盖", changes[PropertyRules.AcceptsInterfaces]);
                Equal("旧机械卡口", changes[PropertyRules.LegacyAcceptsInterface]);
                Equal("", changes[PropertyRules.ConnectionInterface]);
                Equal("内容物;样本", fixture.Child.Properties[PropertyRules.ConnectionInterface]);
                Equal(0, fixture.Child.SaveCount);

                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                var after = McpProtocol.Object(ComponentPropertyInventory(fixture)["Tube-2"]["properties"]);
                Equal("125ml锥形瓶盖;运输保护盖", after[PropertyRules.AcceptsInterfaces]);
                Equal("旧机械卡口", after[PropertyRules.LegacyAcceptsInterface]);
                Equal("", after[PropertyRules.ConnectionInterface]);
                Equal("1", after[PropertyRules.CanContain]);
                Equal(1, fixture.Child.SaveCount);
                Equal(0, fixture.Parent.SaveCount);

                request.Remove("plan_token");
                var noChanges = fixture.Call("asset_properties_plan", request);
                Equal(0, McpProtocol.Object(noChanges["changes"]).Count);
                request["plan_token"] = noChanges["plan_token"];
                Equal(false, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal(1, fixture.Child.SaveCount);
            }
        }

        private static void McpProtectsContentsInterfaceCleanup()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "125ml锥形瓶";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "用于存放样本";
                fixture.Child.Properties[PropertyRules.ConnectionInterface] = "溶液";
                fixture.Child.Properties[PropertyRules.AcceptsInterfaces] = "细胞样本;旧卡口";
                fixture.Child.Properties[PropertyRules.LegacyAcceptsInterface] = "内容物";
                var request = AssetPropertyRequest(fixture, new Dictionary<string, object>
                {
                    { PropertyRules.ConnectionInterface, "锥形瓶底座" },
                    { PropertyRules.AcceptsInterfaces, "125ml锥形瓶盖" }
                });
                var changes = McpProtocol.Object(fixture.Call("asset_properties_plan", request)["changes"]);
                Equal("锥形瓶底座", changes[PropertyRules.ConnectionInterface]);
                Equal("125ml锥形瓶盖", changes[PropertyRules.AcceptsInterfaces]);
                Equal("", changes[PropertyRules.LegacyAcceptsInterface]);

                var legacy = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.LegacyAcceptsInterface, "卡口" } });
                True(fixture.Rejection("asset_properties_plan", legacy).Contains(PropertyRules.LegacyAcceptsInterface));

                fixture.Child.Properties[PropertyRules.LegacyAcceptsInterface] = "细胞样本;$PRP:\"机械接口\"";
                True(fixture.Rejection("asset_properties_plan", request).Contains("链接或表达式"));
                Equal("细胞样本;$PRP:\"机械接口\"", fixture.Child.Properties[PropertyRules.LegacyAcceptsInterface]);
                Equal(0, fixture.Child.SaveCount);
                Equal(0, fixture.RegistryReads);
            }
        }

        private static void McpCanContain()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "试管";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "用于存放样本";
                var context = new Dictionary<string, object>
                {
                    { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name }
                };
                var before = ComponentPropertyInventory(fixture)["Tube-1"];
                Equal(false, before[PropertyRules.CanContain]);
                True(!McpProtocol.Object(before["properties"]).ContainsKey(PropertyRules.CanContain));
                Equal(0, fixture.Child.SaveCount);

                var request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.CanContain, "maybe" } });
                True(fixture.Rejection("asset_properties_plan", request).Contains(PropertyRules.CanContain));
                request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.CanContain, true } });
                var plan = fixture.Call("asset_properties_plan", request);
                Equal("1", McpProtocol.Object(plan["changes"])[PropertyRules.CanContain]);
                True(!McpProtocol.Object(plan["changes"]).ContainsKey(PropertyRules.SlotType));
                Equal("loose", McpProtocol.Object(plan["changes"])[PropertyRules.AssetEngagement]);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.CanContain));
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.SlotType));
                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("1", fixture.Child.Properties[PropertyRules.CanContain]);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.SlotType));
                Equal("loose", fixture.Child.Properties[PropertyRules.AssetEngagement]);
                Equal("2", fixture.Child.Properties[PropertyRules.AssetVersion]);
                Equal(1, fixture.Child.SaveCount);

                var assets = ((object[])fixture.Call("asset_list", context)["items"]).Select(McpProtocol.Object)
                    .ToDictionary(row => (string)row["instance"], StringComparer.Ordinal);
                Equal(false, assets["Rack-1"][PropertyRules.CanContain]);
                Equal(true, assets["Tube-1"][PropertyRules.CanContain]);
                Equal(true, assets["Tube-2"][PropertyRules.CanContain]);
                Equal("1", McpProtocol.Object(ComponentPropertyInventory(fixture)["Tube-2"]["properties"])[PropertyRules.CanContain]);
            }
        }

        private static void McpUsesCanonicalContentsOnly()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "试管";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "用于存放样本";
                fixture.Child.Properties["can_contain"] = "1";
                fixture.Child.Properties[PropertyRules.SlotType] = "place";
                fixture.Child.Properties[PropertyRules.AssetEngagement] = "loose";
                var before = ComponentPropertyInventory(fixture)["Tube-1"];
                Equal(false, before["can_hold_contents"]);
                True(!before.ContainsKey("can_contain"));
                True(!McpProtocol.Object(before["properties"]).ContainsKey("can_contain"));
                True(!fixture.Child.Properties.ContainsKey("can_hold_contents"));
                var component = ((Array)fixture.Assembly.GetComponents(false)).OfType<Component2>().Single(item => item.Name2 == "Tube-1");
                var source = new SwCadNode(new SldWorks { ActiveDoc = fixture.Assembly }, component, "/Tube-1");
                True(!source.Model.FileProperties.ContainsKey("can_contain"));
                var current = new SwInterfacePropertyTarget(fixture.Child).Read();
                True(!current.Raw.ContainsKey("can_contain"));
                var oldRequest = AssetPropertyRequest(fixture, new Dictionary<string, object> { { "can_contain", false } });
                True(fixture.Rejection("asset_properties_plan", oldRequest).Contains("can_contain"));

                var unrelated = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.PartName, "试管" } });
                var unchanged = fixture.Call("asset_properties_plan", unrelated);
                Equal(0, McpProtocol.Object(unchanged["changes"]).Count);
                unrelated["plan_token"] = unchanged["plan_token"];
                Equal(false, fixture.Call("asset_properties_apply", unrelated)["saved"]);
                Equal(0, fixture.Child.SaveCount);

                var request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { "can_hold_contents", false } });
                var plan = fixture.Call("asset_properties_plan", request);
                var changes = McpProtocol.Object(plan["changes"]);
                Equal(false, plan["can_hold_contents"]);
                Equal("0", changes["can_hold_contents"]);
                True(!changes.ContainsKey("can_contain"));
                Equal("1", fixture.Child.Properties["can_contain"]);
                Equal(0, fixture.Child.SaveCount);

                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("0", fixture.Child.Properties["can_hold_contents"]);
                Equal("1", fixture.Child.Properties["can_contain"]);
                Equal(1, fixture.Child.SaveCount);
                var otherOccurrence = ComponentPropertyInventory(fixture)["Tube-2"];
                Equal(false, otherOccurrence["can_hold_contents"]);
                True(!McpProtocol.Object(otherOccurrence["properties"]).ContainsKey("can_contain"));
            }
        }

        private static void PublishedCanContain()
        {
            const string uuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
            const string modelFingerprint = "unchanged-model";
            var drawings = new[] { "drawing" };
            var source = new Dictionary<string, string> { { PropertyRules.PartName, "试管" } };
            var unspecified = ExportCoordinator.PublishedAssetProperties(source, uuid, null, new string[0]);
            var legacyMaterial = Canonical.Join("asset-placement-uuid-v1", modelFingerprint,
                string.Join("\n", drawings), FileHash.Sha256Text(source[PropertyRules.PartName]), uuid, string.Empty);
            True(!unspecified.ContainsKey(PropertyRules.CanContain));
            var defaultCapability = PropertyRules.ReadCanContain(unspecified);
            Equal(false, defaultCapability);
            var defaultFingerprint = ExportCoordinator.AssetContentFingerprint(modelFingerprint, drawings, unspecified, defaultCapability);

            source[PropertyRules.CanContain] = "no";
            var disabled = ExportCoordinator.PublishedAssetProperties(source, uuid, null, new string[0]);
            Equal("0", disabled[PropertyRules.CanContain]);
            Equal(defaultFingerprint, ExportCoordinator.AssetContentFingerprint(modelFingerprint, drawings,
                disabled, PropertyRules.ReadCanContain(disabled)));
            Equal(0, ExportCoordinator.RegisteredPropertyDifferences(unspecified, disabled).Count);

            source[PropertyRules.CanContain] = " YES ";
            var enabled = ExportCoordinator.PublishedAssetProperties(source, uuid, null, new string[0]);
            var enabledFingerprint = ExportCoordinator.AssetContentFingerprint(modelFingerprint, drawings,
                enabled, PropertyRules.ReadCanContain(enabled));
            True(defaultFingerprint != enabledFingerprint);
            Equal("1", enabled[PropertyRules.CanContain]);
            Equal(" YES ", source[PropertyRules.CanContain]);
            True(ExportCoordinator.RegisteredPropertyDifferences(unspecified, enabled).Single().StartsWith(PropertyRules.CanContain));
            var registrations = new[]
            {
                new AssetRegistration { Uuid = uuid, Version = 1, ContentFingerprint = FileHash.Sha256Text(legacyMaterial) },
                new AssetRegistration { Uuid = uuid, Version = 2, ContentFingerprint =
                    FileHash.Sha256Text(legacyMaterial + Canonical.Join(PropertyRules.CanContain, "1")) }
            };
            Equal(AssetVersionDecisionKind.UpgradeRequired, AssetVersionPolicy.Evaluate(uuid, 1, defaultFingerprint, registrations).Kind);
            Equal(AssetVersionDecisionKind.UpgradeRequired, AssetVersionPolicy.Evaluate(uuid, 2, enabledFingerprint, registrations).Kind);
            Equal(AssetVersionDecisionKind.CreateNewVersion, AssetVersionPolicy.Evaluate(uuid, 3, enabledFingerprint, registrations).Kind);

            var directory = TempDirectory();
            try
            {
                var path = Path.Combine(directory, "asset_" + uuid + "_v1.json");
                var packageFiles = new[] { "geometry/model.step", "geometry/model.stl", "source/models/test.SLDPRT" };
                foreach (var packageFile in packageFiles)
                {
                    var file = Path.Combine(directory, packageFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, packageFile);
                }
                var manifest = new AssetManifest { SchemaVersion = "1.0", Uuid = uuid, Version = 1,
                    ContentFingerprint = defaultFingerprint, Properties = unspecified,
                    Files = AssetManifestValidator.DescribeFiles(directory, packageFiles) };
                JsonFile.Write(path, manifest);
                var legacy = AssetManifestValidator.ReadManifest(path);
                Equal<bool?>(null, legacy.CanHoldContents);
                True(AssetManifestValidator.DescribeContentsCapabilityMismatch(legacy, false).Contains("can_hold_contents"));
                Equal(ExistingAssetState.Reusable, AssetManifestValidator.Inspect(directory, uuid, 1, defaultFingerprint));
                Throws<ValidationException>(() => AssetManifestValidator.Inspect(
                    directory, uuid, 1, defaultFingerprint, FileHash.Sha256, false));

                foreach (var properties in new[] { unspecified, enabled })
                {
                    var capability = PropertyRules.ReadCanContain(properties);
                    manifest.Properties = properties;
                    manifest.CanHoldContents = capability;
                    manifest.ContentFingerprint = capability ? enabledFingerprint : defaultFingerprint;
                    File.Delete(path);
                    JsonFile.Write(path, manifest);
                    var json = McpProtocol.Object(McpProtocol.Json().DeserializeObject(File.ReadAllText(path)));
                    Equal<object>(capability, json["can_hold_contents"]);
                    var exported = AssetManifestValidator.ReadManifest(path);
                    Equal<bool?>(capability, exported.CanHoldContents);
                    Equal(null, AssetManifestValidator.DescribeContentsCapabilityMismatch(exported, capability));
                    True(AssetManifestValidator.DescribeContentsCapabilityMismatch(exported, !capability).Contains("can_hold_contents"));
                    Equal(ExistingAssetState.Reusable, AssetManifestValidator.Inspect(
                        directory, uuid, 1, manifest.ContentFingerprint, FileHash.Sha256, capability));
                }
                foreach (var token in new[] { "\"true\"", "0", "null" })
                {
                    File.WriteAllText(path, "{\"can_hold_contents\":" + token + "}");
                    Throws<ValidationException>(() => AssetManifestValidator.ReadManifest(path));
                }
            }
            finally { Directory.Delete(directory, true); }

            source[PropertyRules.CanContain] = "maybe";
            True(PropertyRules.ValidateAssetConnectionProperties(source).Single().Contains(PropertyRules.CanContain));
            Throws<ValidationException>(() => ExportCoordinator.PublishedAssetProperties(source, uuid, null, new string[0]));
        }

        private static void McpProtectBoundClassification()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.PartName] = "试管";
                fixture.Child.Properties[PropertyRules.DesignPurpose] = "操作中可替换的试管";
                fixture.Child.Properties[PropertyRules.ConnectionInterface] = "试管-D20";
                fixture.Parent.Properties[PropertyRules.AcceptsInterfaces] = "Other;试管-D20";
                var binding = fixture.Request();
                binding.Remove("connection_interface");
                binding.Remove("slots_num");
                var result = fixture.Call("binding_add", binding);
                Equal(2, result["added"]);
                Equal(2, result["occupied_slots"]);
                True(!fixture.Parent.Properties.ContainsKey(PropertyRules.SlotsNum));

                var demote = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.IsAsset, false } });
                fixture.Rejection("asset_properties_plan", demote);
                Equal("1", fixture.Child.Properties[PropertyRules.IsAsset]);
                Equal(2, fixture.Session.Current.Relations.Count);
                Equal(0, fixture.Child.SaveCount);
                Equal(0, fixture.Parent.SaveCount);
                Equal(0, fixture.RegistryReads);
            }

            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties.Clear();
                var request = AssetPropertyRequest(fixture, new Dictionary<string, object> { { PropertyRules.IsAsset, false } });
                var plan = fixture.Call("asset_properties_plan", request);
                Equal("0", McpProtocol.Object(plan["changes"])[PropertyRules.IsAsset]);
                request["plan_token"] = plan["plan_token"];
                Equal(true, fixture.Call("asset_properties_apply", request)["saved"]);
                Equal("non_asset", ComponentPropertyInventory(fixture)["Tube-1"]["classification"]);
                Equal("0", fixture.Child.Properties[PropertyRules.IsAsset]);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.AssetVersion));
                Equal(0, fixture.RegistryReads);
            }
        }

        private static void McpClassifyNewAsset()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties.Clear();
                fixture.Child.Properties["Unrelated model property"] = "preserved";
                var before = ComponentPropertyInventory(fixture);
                Equal("unclassified", before["Tube-1"]["classification"]);
                Equal("unclassified", before["Tube-2"]["classification"]);
                Equal(fixture.Child.GetPathName(), before["Tube-2"]["source_path"]);
                Equal("asset", before["Rack-1"]["classification"]);

                var properties = NewAssetProperties();
                properties[PropertyRules.IsTool] = true;
                properties[PropertyRules.IsAdjustable] = false;
                properties[PropertyRules.HasQrCode] = "yes";
                properties["QR_num"] = 2;
                properties["QR_size"] = "20mm";
                properties["QR_spacing"] = 2.5m;
                properties["设计原理"] = "工具按位置夹紧试管";
                var request = AssetPropertyRequest(fixture, properties);
                var plan = fixture.Call("asset_properties_plan", request);
                var changes = McpProtocol.Object(plan["changes"]);
                Equal("1", changes[PropertyRules.IsAsset]);
                Equal("1", changes[PropertyRules.IsTool]);
                Equal("0", changes[PropertyRules.IsAdjustable]);
                Equal("1", changes[PropertyRules.HasQrCode]);
                True(!changes.ContainsKey(PropertyRules.SlotType));
                Equal("loose", changes[PropertyRules.AssetEngagement]);
                Equal("2", changes["QR_num"]);
                Equal("2.5", changes["QR_spacing"]);
                Equal("1", changes[PropertyRules.AssetVersion]);
                True(((object[])plan["interface_issues"]).Length > 0);
                Equal(0, fixture.RegistryReads);
                Equal(0, fixture.Child.SaveCount);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.IsAsset));

                request["plan_token"] = plan["plan_token"];
                var applied = fixture.Call("asset_properties_apply", request);
                Equal(true, applied["saved"]);
                Equal(1, fixture.RegistryReads);
                Equal(1, fixture.Child.SaveCount);
                Equal("1", fixture.Child.Properties[PropertyRules.IsAsset]);
                Equal("movable", fixture.Child.Properties[PropertyRules.AssetClass]);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.SlotType));
                Equal("loose", fixture.Child.Properties[PropertyRules.AssetEngagement]);
                Equal("试管", fixture.Child.Properties[PropertyRules.PartName]);
                Equal("20mm", fixture.Child.Properties["QR_size"]);
                Equal("工具按位置夹紧试管", fixture.Child.Properties["设计原理"]);
                Equal("2", fixture.Child.Properties[PropertyRules.AssetVersion]);
                Equal("preserved", fixture.Child.Properties["Unrelated model property"]);
                Equal(0, fixture.Session.Current.Relations.Count);

                var after = ComponentPropertyInventory(fixture);
                foreach (var name in new[] { "Tube-1", "Tube-2" })
                {
                    Equal("asset", after[name]["classification"]);
                    Equal("试管", McpProtocol.Object(after[name]["properties"])[PropertyRules.PartName]);
                }
                var retryPlan = fixture.Call("asset_properties_plan", AssetPropertyRequest(fixture, properties));
                request["plan_token"] = retryPlan["plan_token"];
                var retry = fixture.Call("asset_properties_apply", request);
                Equal(false, retry["saved"]);
                Equal(1, fixture.Child.SaveCount);
                Equal(1, fixture.RegistryReads);
            }
        }

        private static void McpRejectAssetPropertyPlans()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties.Clear();
                var properties = NewAssetProperties();
                properties.Remove(PropertyRules.PartName);
                True(fixture.Rejection("asset_properties_plan", AssetPropertyRequest(fixture, properties))
                    .Contains(PropertyRules.PartName));
                properties = NewAssetProperties();
                properties[PropertyRules.AssetClass] = "guessed";
                True(fixture.Rejection("asset_properties_plan", AssetPropertyRequest(fixture, properties))
                    .Contains(PropertyRules.AssetClass));
                properties = NewAssetProperties();
                properties[PropertyRules.AssetVersion] = "3";
                True(fixture.Rejection("asset_properties_plan", AssetPropertyRequest(fixture, properties))
                    .Contains(PropertyRules.AssetVersion));

                var request = AssetPropertyRequest(fixture, NewAssetProperties());
                var plan = fixture.Call("asset_properties_plan", request);
                request["plan_token"] = plan["plan_token"];
                fixture.Child.Properties["An independently edited field"] = "changed";
                fixture.Rejection("asset_properties_apply", request);
                Equal(0, fixture.Child.SaveCount);
                Equal(0, fixture.RegistryReads);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.IsAsset));
            }
        }

        private static Dictionary<string, Dictionary<string, object>> ComponentPropertyInventory(McpPlanFixture fixture)
        {
            var context = new Dictionary<string, object>
            {
                { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name }
            };
            var list = fixture.Call("component_list", context);
            return ((object[])list["items"]).Select(McpProtocol.Object)
                .ToDictionary(item => (string)item["instance"], StringComparer.Ordinal);
        }

        private static Dictionary<string, object> AssetPropertyRequest(McpPlanFixture fixture, Dictionary<string, object> properties)
        {
            return new Dictionary<string, object>
            {
                { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name },
                { "instance", "Tube-1" }, { "source_path", fixture.Child.GetPathName() }, { "properties", properties }
            };
        }

        private static Dictionary<string, object> NewAssetProperties()
        {
            return new Dictionary<string, object>
            {
                { PropertyRules.IsAsset, true }, { PropertyRules.AssetClass, "movable" },
                { PropertyRules.PartName, "试管" }, { PropertyRules.DesignPurpose, "操作中可替换的试管" }
            };
        }
    }
}
