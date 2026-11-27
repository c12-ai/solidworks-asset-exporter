using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunMcpBindingPlanTests()
        {
            Run("MCP binding preview, apply, and retry work on an uninitialized assembly", McpBindingPlanApply);
            Run("MCP binding apply rejects a stale source before any writes", McpBindingStalePlan);
            Run("MCP can reuse an existing slot and persists the slot and offsets", McpBindingReuseSlot);
            Run("MCP binding add measures slots without inferring design capacity", McpBindingDesignCapacity);
        }

        private static void McpBindingReuseSlot()
        {
            using (var fixture = new McpPlanFixture())
            {
                var first = fixture.Request();
                first["children"] = new object[] { "Tube-1" };
                first.Remove("slots_num");
                first["plan_token"] = fixture.Call("binding_plan", first)["plan_token"];
                fixture.Call("binding_apply", first);
                True(!fixture.Parent.Properties.ContainsKey(PropertyRules.SlotsNum));
                var anchor = fixture.Session.Current.Relations.Single();
                var second = fixture.Request();
                second["children"] = new object[] { "Tube-2" };
                second.Remove("slots_num");
                second["reuse_slot_from_binding_id"] = anchor.Id;
                var preview = fixture.Call("binding_plan", second);
                Equal<object>(null, preview["slots_num"]);
                Equal(1, preview["occupied_slots"]);
                var relation = McpProtocol.Object(((object[])preview["relations"]).Single());
                Equal(anchor.Id, relation["slot_id"]);
                Near(1d, Convert.ToDouble(McpProtocol.Object(relation["slot_offset"])["qw"]));
                Equal(1, fixture.Session.Current.Relations.Count);

                var changed = new Dictionary<string, object>(second) { { "plan_token", preview["plan_token"] } };
                changed.Remove("reuse_slot_from_binding_id");
                True(fixture.Rejection("binding_apply", changed).Contains("承载计划已过期"));
                second["plan_token"] = preview["plan_token"];
                var result = McpProtocol.Object(fixture.Call("binding_apply", second)["binding_result"]);
                Equal(true, result["saved"]);
                Equal(1, result["added"]);
                True(!fixture.Parent.Properties.ContainsKey(PropertyRules.SlotsNum));
                Equal(1, result["occupied_slots"]);
                Equal(1, fixture.Parent.SaveCount);
                var lowerCapacity = new Dictionary<string, object>
                {
                    { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name },
                    { "instance", "Rack-1" }, { "source_path", fixture.Parent.GetPathName() }, { "slots_num", 0 }
                };
                Equal(0, fixture.Call("asset_update_interfaces", lowerCapacity)["slots_num"]);
                Equal("0", fixture.Parent.Properties[PropertyRules.SlotsNum]);
                var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(BindingDocument));
                using (var stream = new MemoryStream(fixture.Assembly.Bytes))
                {
                    var saved = (BindingDocument)serializer.ReadObject(stream);
                    Equal("1.2", saved.SchemaVersion);
                    var records = saved.Configurations.Single().Relations;
                    Equal(2, records.Count);
                    True(records.All(r => r.SlotId == anchor.Id && r.SlotPose != null && r.SlotOffset != null));
                }
                var list = fixture.Call("binding_list", new Dictionary<string, object>
                {
                    { "document_path", fixture.Assembly.GetPathName() }, { "configuration", fixture.Assembly.Config.Name }
                });
                True(((object[])list["items"]).All(item => Equals(McpProtocol.Object(item)["slot_id"], anchor.Id)));
            }
        }

        private static void McpBindingDesignCapacity()
        {
            using (var fixture = new McpPlanFixture())
            {
                fixture.Child.Properties[PropertyRules.ConnectionInterface] = "试管-D20";
                fixture.Parent.Properties[PropertyRules.AcceptsInterfaces] = "试管-D20";
                var request = fixture.Request();
                request.Remove("slots_num");
                request.Remove("connection_interface");
                var first = fixture.Call("binding_add", request);
                Equal<object>(null, first["slots_num"]);
                Equal(2, first["occupied_slots"]);
                Equal(2, first["required_slots"]);
                Equal(0, fixture.Parent.SaveCount);
                True(!fixture.Parent.Properties.ContainsKey(PropertyRules.SlotsNum));
                request["slots_num"] = 1;
                var second = fixture.Call("binding_add", request);
                Equal(1, second["slots_num"]);
                Equal(2, second["occupied_slots"]);
                Equal("1", fixture.Parent.Properties[PropertyRules.SlotsNum]);
                Equal(1, fixture.Parent.SaveCount);
                Equal(2, fixture.Session.Current.Relations.Count);
                request.Remove("slots_num");
                Equal(1, fixture.Call("binding_add", request)["slots_num"]);
                Equal(1, fixture.Parent.SaveCount);
                Equal("1", fixture.Parent.Properties[PropertyRules.SlotsNum]);

                var explicitPlan = fixture.Request();
                explicitPlan["slots_num"] = 0;
                var preview = fixture.Call("binding_plan", explicitPlan);
                Equal(0, preview["slots_num"]);
                Equal(2, preview["required_slots"]);
                explicitPlan["plan_token"] = preview["plan_token"];
                fixture.Call("binding_apply", explicitPlan);
                Equal("0", fixture.Parent.Properties[PropertyRules.SlotsNum]);
                Equal(2, fixture.Session.Current.Relations.Count);
            }
        }

        private static void McpBindingPlanApply()
        {
            using (var fixture = new McpPlanFixture())
            {
                var preview = fixture.Call("binding_plan", fixture.Request());
                Equal(2, preview["added_count"]);
                Equal(2, ((object[])preview["source_changes"]).Length);
                Equal(0, fixture.RegistryReads);
                Equal(0, fixture.Parent.SaveCount);
                Equal(0, fixture.Child.SaveCount);
                Equal(0, fixture.Session.Current.Relations.Count);

                var result = fixture.Call("binding_apply", fixture.Request((string)preview["plan_token"]));
                Equal(2, ((object[])result["source_files_saved"]).Length);
                var bindings = McpProtocol.Object(result["binding_result"]);
                Equal(2, bindings["added"]);
                Equal(true, bindings["saved"]);
                Equal("Other;试管-D20", fixture.Parent.Properties[PropertyRules.AcceptsInterfaces]);
                Equal("4", fixture.Parent.Properties[PropertyRules.SlotsNum]);
                Equal("试管-D20", fixture.Child.Properties[PropertyRules.ConnectionInterface]);
                Equal("2", fixture.Child.Properties[PropertyRules.AssetVersion]);
                Equal(1, fixture.RegistryReads);
                Equal(1, fixture.Parent.SaveCount);
                Equal(1, fixture.Child.SaveCount);
                Equal(2, fixture.Session.Current.Relations.Count);
                True(fixture.Assembly.Bytes != null && fixture.Assembly.Bytes.Length > 0);

                var nextPlan = fixture.Call("binding_plan", fixture.Request());
                Equal(0, nextPlan["added_count"]);
                Equal(2, nextPlan["existing_count"]);
                Equal(0, ((object[])nextPlan["source_changes"]).Length);
                var retry = fixture.Call("binding_apply", fixture.Request((string)nextPlan["plan_token"]));
                Equal(0, McpProtocol.Object(retry["binding_result"])["added"]);
                Equal(1, fixture.RegistryReads);
                Equal(1, fixture.Parent.SaveCount);
                Equal(1, fixture.Child.SaveCount);
            }
        }

        private static void McpBindingStalePlan()
        {
            using (var fixture = new McpPlanFixture())
            {
                var preview = fixture.Call("binding_plan", fixture.Request());
                fixture.Parent.Properties[PropertyRules.AcceptsInterfaces] = "Other;New";
                var failure = fixture.Rejection("binding_apply", fixture.Request((string)preview["plan_token"]));
                True(failure.Contains("承载计划已过期"));
                Equal(0, fixture.RegistryReads);
                Equal(0, fixture.Parent.SaveCount);
                Equal(0, fixture.Child.SaveCount);
                True(!fixture.Child.Properties.ContainsKey(PropertyRules.ConnectionInterface));
                Equal(0, fixture.Session.Current.Relations.Count);
            }
        }

        private sealed class McpPlanFixture : IDisposable
        {
            private readonly string _directory;
            private readonly McpSolidWorksTools _tools;
            internal readonly BindingTestDocument Assembly;
            internal readonly SwInstanceBindings Session;
            internal readonly McpPlanPart Parent, Child;
            internal int RegistryReads;

            internal McpPlanFixture()
            {
                _directory = TempDirectory();
                Parent = new McpPlanPart(_directory, "rack.SLDPRT", "station");
                Child = new McpPlanPart(_directory, "tube.SLDPRT", "movable");
                Parent.Properties[PropertyRules.AcceptsInterfaces] = "Other";
                Parent.Properties[PropertyRules.IsFixture] = "1";
                var first = BindingSelectionComponent("Rack-1", Parent, null);
                var second = BindingSelectionComponent("Tube-1", Child, null);
                var third = BindingSelectionComponent("Tube-2", Child, null);
                var components = new[] { first, second, third };
                var extension = new FakeModelDocExtension();
                for (var index = 0; index < components.Length; index++)
                {
                    components[index].ComponentId = index + 1;
                    extension.PersistentComponents.Add(components[index].ComponentId, components[index]);
                }
                Assembly = new BindingTestDocument
                {
                    FilePath = Path.Combine(_directory, "new-lab.SLDASM"),
                    ReferenceExtension = extension
                };
                File.WriteAllText(Assembly.GetPathName(), "test assembly");
                Assembly.Config.RootComponent = new FakeSwComponent
                {
                    IsRootValue = true, ChildrenValue = components
                };
                Session = SwInstanceBindings.ForDocument(Assembly);
                var uuid = IdentityService.AssetUuid(new SwCadNode(new SldWorks(), second, "/test/Tube-1").Model);
                var registry = new AssetRegistryDocument();
                registry.Assets.Add(new AssetRegistration { Uuid = uuid.ToString("D"), Version = 1 });
                _tools = new McpSolidWorksTools(new SldWorks { ActiveDoc = Assembly }, () => false,
                    () => { RegistryReads++; return registry; }, _directory);
            }

            internal Dictionary<string, object> Request(string token = null)
            {
                var args = new Dictionary<string, object>
                {
                    { "document_path", Assembly.GetPathName() }, { "configuration", Assembly.Config.Name },
                    { "parent", "Rack-1" }, { "children", new object[] { "Tube-1", "Tube-2" } },
                    { "connection_interface", "试管-D20" }, { "slots_num", 4 }
                };
                if (token != null) args.Add("plan_token", token);
                return args;
            }

            internal Dictionary<string, object> Call(string name, Dictionary<string, object> args)
            {
                var result = _tools.Call(name, McpProtocol.ToolArguments(name, args)).GetAwaiter().GetResult();
                var json = McpProtocol.Json();
                return McpProtocol.Object(json.DeserializeObject(json.Serialize(result)));
            }

            internal string Rejection(string name, Dictionary<string, object> args)
            {
                try { Call(name, args); }
                catch (ValidationException ex) { return ex.Message; }
                throw new Exception("MCP operation should have rejected the request.");
            }

            public void Dispose()
            {
                Session.Dispose();
                Directory.Delete(_directory, true);
            }
        }

        private sealed class McpPlanPart : FakeSwDocument
        {
            internal int SaveCount;
            internal IDictionary<string, string> Properties
            { get { return ExtensionValue.CustomPropertyManager.Manager("").Values; } }

            internal McpPlanPart(string directory, string name, string assetClass)
                : base(name, Path.Combine(directory, name), false)
            {
                CreationTime = "2026-01-02T03:04:05";
                Properties[PropertyRules.IsAsset] = "1";
                Properties[PropertyRules.AssetClass] = assetClass;
                Properties[PropertyRules.AssetVersion] = "1";
                File.WriteAllText(GetPathName(), "test asset");
            }

            public override bool Save3(int options, ref int errors, ref int warnings)
            {
                File.AppendAllText(GetPathName(), " saved");
                SaveCount++;
                SaveFlag = false;
                return true;
            }
        }
    }
}
