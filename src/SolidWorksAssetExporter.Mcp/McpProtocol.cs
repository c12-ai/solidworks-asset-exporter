using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SolidWorksAssetExporter.Mcp
{
    internal sealed class McpTool
    {
        internal readonly string Name;
        internal readonly string Description;
        private readonly IDictionary<string, object> _properties;
        private readonly string[] _required;
        private readonly bool _readOnly;

        internal McpTool(string name, string description, bool readOnly, string[] required,
            params string[] optional)
        {
            Name = name;
            Description = description;
            _readOnly = readOnly;
            _required = required.Select(FieldName).ToArray();
            _properties = required.Concat(optional).ToDictionary(FieldName, Schema);
        }

        internal object Definition()
        {
            return new
            {
                name = Name, description = Description,
                inputSchema = new { type = "object", properties = _properties, required = _required, additionalProperties = false },
                annotations = new { readOnlyHint = _readOnly }
            };
        }

        internal void Validate(Dictionary<string, object> args)
        {
            foreach (var name in _required)
                if (!args.ContainsKey(name)) throw new ArgumentException("缺少参数 " + name + "。");
            foreach (var arg in args)
            {
                object schema;
                if (!_properties.TryGetValue(arg.Key, out schema))
                    throw new ArgumentException("未知参数 " + arg.Key + "。");
                var type = ((Dictionary<string, object>)schema)["type"] as string;
                if (type == "string" && arg.Value is string ||
                    type == "integer" && arg.Value is int && (int)arg.Value >= 0 ||
                    type == "boolean" && arg.Value is bool ||
                    type == "object" && arg.Value is Dictionary<string, object> ||
                    type == "array" && arg.Value is object[] && ((object[])arg.Value).All(value => value is string))
                    continue;
                throw new ArgumentException("参数 " + arg.Key + " 的类型或取值无效。");
            }
        }

        private static string FieldName(string field) { return field.Split(':')[0]; }

        private static object Schema(string field)
        {
            var type = field.Split(':').Last();
            var schema = new Dictionary<string, object> { { "type", type == "strings" ? "array" : type } };
            if (type == "strings") schema.Add("items", new { type = "string" });
            if (type == "integer") schema.Add("minimum", 0);
            return schema;
        }
    }

    internal static class McpProtocol
    {
        private static readonly string[] Target = { "document_path:string", "configuration:string" };
        private static readonly string[] Paging = { "offset:integer", "limit:integer" };
        private static readonly string[] BindingGroup = Target.Concat(new[] { "parent:string", "children:strings" }).ToArray();
        private static readonly string[] DuplicateBinding = Target.Concat(new[] { "parent:string", "child:string" }).ToArray();
        private const string ReuseSlot = "reuse_slot_from_binding_id:string";
        private static readonly string[] BindingOptions = { "connection_interface:string", "slots_num:integer", ReuseSlot };
        private static readonly string[] PropertyTarget = Target.Concat(new[] { "instance:string", "source_path:string", "properties:object" }).ToArray();
        private static readonly McpTool[] Tools =
        {
            new McpTool("assembly_context", "读取活动 SOLIDWORKS 装配文档路径、配置和保存状态。", true, new string[0]),
            new McpTool("interface_catalog", "读取 Wanxiang 在线接口候选名称。服务不可用时返回本地候选及提示。", true, new string[0]),
            new McpTool("interface_catalog_add", "向 Wanxiang 在线接口候选库追加已确认的名称。", false, new[] { "names:strings" }),
            new McpTool("asset_list", "列出活动装配内的资产实例及文件级接口、槽位定义、容量和版本。limit 默认 100，最多 500。", true, Target, Paging),
            new McpTool("component_list", "分页列出活动配置可见的零部件、当前资产分类和文件级自定义属性。包括未分类组件，已有 Asset 装配体为扫描边界。", true, Target, Paging),
            new McpTool("asset_properties_plan", "只读预检零部件的分类和文件级自定义属性。仅在 slots_num 为正整数时可填写 slot_type。engagement 可填写 tight 或 loose。返回计划令牌、同源实例和待补接口。", true, PropertyTarget),
            new McpTool("asset_properties_apply", "用 asset_properties_plan 的原参数和 plan_token 写入文件级自定义属性。修改作用于整个源文件，普通资产版本由插件维护。", false,
                PropertyTarget.Concat(new[] { "plan_token:string" }).ToArray()),
            new McpTool("binding_evidence", "读取资产实例的 CAD 上级、世界坐标原点、固定状态和可读取的双端配合。配合与位置用于核对设计依据。limit 默认 100，最多 500。", true, Target, Paging),
            new McpTool("binding_list", "读取活动配置的承载关系、端点和旧多父诊断。limit 默认 100，最多 500。", true, Target, Paging),
            new McpTool("binding_add", "新增并保存承载关系。每个子实例只能有一个承载父级。slots_num 仅在显式传入已确认的设计容量时写入；实际占用独立返回。可复用同一父组件下的已有槽位。", false,
                BindingGroup, ReuseSlot, "slots_num:integer"),
            new McpTool("binding_plan", "只读预检承载关系并返回 plan_token。可填写 connection_interface、已确认的设计 slots_num 或 reuse_slot_from_binding_id。绑定数不推断设计容量。", true,
                BindingGroup, BindingOptions),
            new McpTool("binding_apply", "用 binding_plan 的原参数和 plan_token 重新校验后，保存所需文件级接口与版本，再保存当前配置的承载关系。", false,
                BindingGroup.Concat(new[] { "plan_token:string" }).ToArray(), BindingOptions),
            new McpTool("binding_duplicate_plan", "只读预检在当前配置原位复制一个普通资产实例并绑定到指定父级。如指定的父级已承载原实例且原实例还有其他父级，计划迁移该条旧关系并保留槽位。", true,
                DuplicateBinding, ReuseSlot),
            new McpTool("binding_duplicate_apply", "用原参数与 plan_token 复核后，原位复制实例并保存当前配置的承载关系。", false,
                DuplicateBinding.Concat(new[] { "plan_token:string" }).ToArray(), ReuseSlot),
            new McpTool("asset_update_interfaces", "维护资产文件的向上接口、兼容接口和总槽位。省略的字段保留，空字符串或空数组可清除接口。sync_accepts_from_bindings 追加当前配置已绑定子级声明的接口。slots_num 是已确认的总容量。", false,
                Target.Concat(new[] { "instance:string", "source_path:string" }).ToArray(),
                "connection_interface:string", "accepts_interfaces:strings", "slots_num:integer", "sync_accepts_from_bindings:boolean"),
            new McpTool("asset_export_preview", "只读检查活动装配的导出计划和版本。使用插件已保存的目录和 Wanxiang 配置。", true,
                Target, "export_project:boolean"),
            new McpTool("asset_export", "维护版本、重新校验并导出资产。export_project 默认使用插件设置。upload=true 时按插件设置上传至 Wanxiang。", false,
                Target, "export_project:boolean", "upload:boolean")
        };

        internal static string PipeName(int processId) { return "SolidWorksAssetExporter.Mcp.v1." + processId; }

        internal static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
        }

        internal static StreamReader Reader(Stream stream)
        {
            return new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
        }

        internal static StreamWriter Writer(Stream stream)
        {
            return new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
        }

        internal static Dictionary<string, object> Object(object value)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null) throw new ArgumentException("需要 JSON 对象。");
            return dictionary;
        }

        internal static object Value(IDictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value : null;
        }

        internal static Dictionary<string, object> ToolArguments(string name, object arguments)
        {
            var tool = Tools.SingleOrDefault(item => item.Name == name);
            if (tool == null) throw new ArgumentException("未知 MCP 工具：" + name);
            var args = arguments == null ? new Dictionary<string, object>() : Object(arguments);
            tool.Validate(args);
            return args;
        }

        internal static string Respond(string line, Func<string, Dictionary<string, object>, object> call)
        {
            var json = Json();
            object id = null;
            object value;
            try { value = json.DeserializeObject(line); }
            catch (ArgumentException) { return json.Serialize(Error(null, -32700, "Parse error")); }
            catch (InvalidOperationException) { return json.Serialize(Error(null, -32700, "Parse error")); }
            var request = value as Dictionary<string, object>;
            if (request == null) return json.Serialize(Error(null, -32600, "Invalid request"));
            var method = Value(request, "method") as string;
            var hasId = request.TryGetValue("id", out id);
            if (!Equals(Value(request, "jsonrpc"), "2.0") || string.IsNullOrWhiteSpace(method) ||
                (hasId && !(id is string || id is int || id is long)))
                return json.Serialize(Error(null, -32600, "Invalid request"));
            if (!hasId) return null;

            try
            {
                var rawParams = Value(request, "params");
                var parameters = rawParams == null ? new Dictionary<string, object>() : Object(rawParams);
                object result;
                switch (method)
                {
                    case "initialize":
                        var version = Value(parameters, "protocolVersion") as string;
                        if (string.IsNullOrEmpty(version)) throw new ArgumentException("protocolVersion is required.");
                        var supported = new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
                        result = new
                        {
                            protocolVersion = supported.Contains(version) ? version : supported[0],
                            capabilities = new { tools = new { listChanged = false } },
                            serverInfo = new { name = "solidworks-asset-exporter", version = "1.0.0" }
                        };
                        break;
                    case "ping": result = new { }; break;
                    case "tools/list": result = new { tools = Tools.Select(tool => tool.Definition()).ToArray() }; break;
                    case "tools/call":
                        var name = Value(parameters, "name") as string;
                        var args = ToolArguments(name, Value(parameters, "arguments"));
                        try { result = Content(json.Serialize(call(name, args)), false); }
                        catch (Exception ex) { result = Content(ex.Message, true); }
                        break;
                    default: return json.Serialize(Error(id, -32601, "Method not found"));
                }
                return json.Serialize(new { jsonrpc = "2.0", id = id, result = result });
            }
            catch (ArgumentException ex) { return json.Serialize(Error(id, -32602, ex.Message)); }
        }

        private static object Content(string text, bool error)
        {
            return new { content = new[] { new { type = "text", text = text } }, isError = error };
        }

        private static object Error(object id, int code, string message)
        {
            return new { jsonrpc = "2.0", id = id, error = new { code = code, message = message } };
        }
    }
}
