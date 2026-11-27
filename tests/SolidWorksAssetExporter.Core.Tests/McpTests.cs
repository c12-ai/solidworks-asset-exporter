using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunMcpTests()
        {
            RunMcpBindingEvidenceTests();
            RunMcpBindingPlanTests();
            RunMcpAssetPropertiesTests();
            Run("MCP negotiates protocol, accepts notifications and advertises model tools", delegate
            {
                Func<string, Dictionary<string, object>, object> unused = (name, args) =>
                    { throw new InvalidOperationException("No tool dispatch expected."); };
                var initialize = McpProtocol.Respond(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}", unused);
                var response = ParseMcp(initialize);
                Equal("init", response["id"]);
                Equal("2025-06-18", McpProtocol.Object(response["result"])["protocolVersion"]);
                Equal<string>(null, McpProtocol.Respond(
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", unused));
                var listing = ParseMcp(McpProtocol.Respond("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", unused));
                var tools = ((object[])McpProtocol.Object(listing["result"])["tools"]).Select(McpProtocol.Object).ToList();
                True(tools.Any(tool => Equals(tool["name"], "binding_add")));
                True(tools.Any(tool => Equals(tool["name"], "asset_update_interfaces")));
                True(tools.Any(tool => Equals(tool["name"], "asset_export")));
                Equal(17, tools.Count);
                var copyPlan = tools.Single(tool => Equals(tool["name"], "binding_duplicate_plan"));
                var copyApply = tools.Single(tool => Equals(tool["name"], "binding_duplicate_apply"));
                Equal(true, McpProtocol.Object(copyPlan["annotations"])["readOnlyHint"]);
                Equal(false, McpProtocol.Object(copyApply["annotations"])["readOnlyHint"]);
                var plan = tools.Single(tool => Equals(tool["name"], "binding_plan"));
                var apply = tools.Single(tool => Equals(tool["name"], "binding_apply"));
                Equal(true, McpProtocol.Object(plan["annotations"])["readOnlyHint"]);
                Equal(false, McpProtocol.Object(apply["annotations"])["readOnlyHint"]);
                True(((object[])McpProtocol.Object(apply["inputSchema"])["required"]).Contains("plan_token"));
                foreach (var tool in new[] { plan, apply })
                {
                    var properties = McpProtocol.Object(McpProtocol.Object(tool["inputSchema"])["properties"]);
                    True(!properties.ContainsKey("slot_type"));
                    True(!properties.ContainsKey("engagement"));
                }
            });

            Run("Binding bootstrap uses explicit interfaces and preserves existing parent capabilities", delegate
            {
                var child = new InterfacePropertySnapshot();
                Throws<ValidationException>(() => InterfaceEditRules.BootstrapConnection(child, null, "Tube-1"));
                Equal("试管.D20", InterfaceEditRules.BootstrapConnection(child, "试管.D20", "Tube-1"));
                child.Values[PropertyRules.ConnectionInterface] = "试管.D20";
                Equal("试管.D20", InterfaceEditRules.BootstrapConnection(child, null, "Tube-1"));
                Throws<ValidationException>(() => InterfaceEditRules.BootstrapConnection(child, "试管.D18", "Tube-1"));
                Equal("试管.D18;试管.D20", string.Join(";", InterfaceEditRules.Suggest("试管.D18",
                    new[] { "试管.D20", "试管.D20" })));
            });

            Run("MCP validates write arguments before dispatch and preserves Unicode", delegate
            {
                var calls = 0;
                Func<string, Dictionary<string, object>, object> call = (name, args) =>
                {
                    calls++;
                    Equal("asset_update_interfaces", name);
                    Equal(24, args["slots_num"]);
                    Equal("试管-D20", ((object[])args["accepts_interfaces"]).Single());
                    return new { slots_num = args["slots_num"] };
                };
                var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" +
                    "{\"name\":\"asset_update_interfaces\",\"arguments\":{\"document_path\":\"assembly\",\"configuration\":\"Default\"," +
                    "\"instance\":\"rack-1\",\"source_path\":\"rack\",\"accepts_interfaces\":[\"试管-D20\"],\"slots_num\":24}}}";
                var result = McpProtocol.Object(ParseMcp(McpProtocol.Respond(request, call))["result"]);
                Equal(false, result["isError"]);
                Equal(-32602, McpProtocol.Object(ParseMcp(McpProtocol.Respond(request.Replace(":24", ":-1"), call))["error"])["code"]);
                Equal(1, calls);
                result = McpProtocol.Object(ParseMcp(McpProtocol.Respond(request, (name, args) =>
                    { throw new InvalidOperationException("Save failed."); }))["result"]);
                Equal(true, result["isError"]);
            });

            Run("MCP rejects instance coupling arguments on binding writes", delegate
            {
                var request = new Dictionary<string, object>
                {
                    { "document_path", "assembly" }, { "configuration", "Default" },
                    { "parent", "Ring-1" }, { "children", new object[] { "Bottle-1" } }
                };
                foreach (var field in new[] { "slot_type", "engagement" })
                {
                    request.Add(field, "example");
                    Throws<ArgumentException>(() => McpProtocol.ToolArguments("binding_add", request));
                    Throws<ArgumentException>(() => McpProtocol.ToolArguments("binding_plan", request));
                    var apply = new Dictionary<string, object>(request) { { "plan_token", "token" } };
                    Throws<ArgumentException>(() => McpProtocol.ToolArguments("binding_apply", apply));
                    request.Remove(field);
                }
            });
        }

        private static Dictionary<string, object> ParseMcp(string response)
        {
            return McpProtocol.Object(McpProtocol.Json().DeserializeObject(response));
        }
    }
}
