using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolidWorksAssetExporter.Core
{
    public static class PropertyRules
    {
        public const string IsAsset = "is_asset";
        public const string AssetVersion = "asset_version";
        public const string AssemblyVersion = "assembly_version";
        public const string AssetClass = "class";
        public const string PartName = "零件名";
        public const string DesignPurpose = "设计目的";
        public const string IsTool = "is_tool";
        public const string IsFixture = "is_fixture";
        public const string IsQuickChanger = "is_quick_changer";
        public const string QuickChangerSide = "quick_changer_side";
        public const string IsQuickChangerRack = "is_quick_changer_rack";
        public const string ConnectionInterface = "connection_interface";
        public const string AcceptsInterfaces = "accepts_interfaces";
        public const string IsAdjustable = "is_adjustable";
        public const string HasQrCode = "has_QRcode";

        private static readonly string[] BooleanAssetProperties =
            { IsTool, IsFixture, IsQuickChanger, IsQuickChangerRack, IsAdjustable, HasQrCode };

        public static IDictionary<string, string> Merge(ModelDescriptor model)
        {
            if (model == null) throw new ValidationException("模型元数据为空。");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Copy(result, model.FileProperties, "文件级");
            return result;
        }

        public static IDictionary<string, string> MergeForClassification(ModelDescriptor model)
        {
            if (model == null) throw new ValidationException("模型元数据为空。");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CopyClassification(result, model.FileProperties, "文件级", model.FileName);
            return result;
        }

        public static IList<string> MissingOrBlankProperties(IDictionary<string, string> properties,
            IEnumerable<string> requiredNames)
        {
            var missing = new List<string>();
            if (requiredNames == null) return missing;
            foreach (var name in requiredNames.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string value;
                if (properties == null || !properties.TryGetValue(name, out value) ||
                    string.IsNullOrWhiteSpace(value))
                    missing.Add(name);
            }
            return missing;
        }

        public static IList<string> ValidateAssetConnectionProperties(
            IDictionary<string, string> properties)
        {
            var issues = new List<string>();
            var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in BooleanAssetProperties)
            {
                bool value;
                string raw;
                if (!TryReadBoolean(properties, name, out value, out raw))
                    issues.Add("属性 [" + name + "] 必须使用 1/0、true/false 或 yes/no；当前值为 [" + raw + "]。");
                flags[name] = value;
            }

            var isRobot = IsRobotClass(properties);
            var isTool = flags[IsTool];
            var isFixture = flags[IsFixture];
            var isQuickChanger = flags[IsQuickChanger];
            var isQuickChangerRack = flags[IsQuickChangerRack];
            var side = ReadTrimmed(properties, QuickChangerSide);
            var connection = ReadTrimmed(properties, ConnectionInterface);
            var accepts = ReadTrimmed(properties, AcceptsInterfaces);

            if (isQuickChanger)
            {
                if (side != "robot_side" && side != "tool_side")
                    issues.Add("属性 [quick_changer_side] 必须为 [robot_side] 或 [tool_side]。");
                RequireValue(issues, connection, ConnectionInterface,
                    "快换盘必须声明自身安装接口");
                RequireValue(issues, accepts, AcceptsInterfaces,
                    "快换盘必须声明其可接受的接口");
            }
            else if (!string.IsNullOrWhiteSpace(side))
            {
                issues.Add("仅当 [is_quick_changer]=1 时才可填写 [quick_changer_side]。");
            }

            if (isQuickChanger && isQuickChangerRack)
                issues.Add("同一个 Asset 不能同时是快换盘和快换架。");

            if (isQuickChangerRack)
            {
                RequireValue(issues, accepts, AcceptsInterfaces,
                    "快换架必须声明可停放的接口");
                var assetClass = ReadTrimmed(properties, AssetClass);
                if (!string.Equals(assetClass, "station", StringComparison.Ordinal))
                    issues.Add("快换架不能由机器人搬运但需要参与快换交互，[class] 必须为 [station]。");
            }

            if (isTool)
                RequireValue(issues, connection, ConnectionInterface,
                    "Tool 必须声明自身安装接口");
            if (isFixture)
                RequireValue(issues, accepts, AcceptsInterfaces,
                    "Fixture 必须声明其可接受的接口");

            if (isRobot)
            {
                RequireValue(issues, accepts, AcceptsInterfaces,
                    "Robot 必须声明末端可接受的国标安装接口");
                if (isTool || isFixture || isQuickChanger || isQuickChangerRack)
                    issues.Add("[class]=robot 不能同时标记为 Tool、Fixture、快换盘或快换架。");
            }

            ValidateInterfaceList(issues, AcceptsInterfaces, accepts);
            if (connection.IndexOf(';') >= 0 || connection.IndexOf('；') >= 0)
                issues.Add("属性 [connection_interface] 只能填写一个接口；多个可接受接口请填写到 [accepts_interfaces]。");
            return issues;
        }

        public static string DescribeAssetConnectionRole(IDictionary<string, string> properties)
        {
            bool value;
            string ignored;
            if (IsRobotClass(properties)) return "Robot";
            if (TryReadBoolean(properties, IsQuickChanger, out value, out ignored) && value)
                return ReadTrimmed(properties, QuickChangerSide) == "robot_side"
                    ? "快换盘-机器人端" : "快换盘-工具端";
            if (TryReadBoolean(properties, IsQuickChangerRack, out value, out ignored) && value)
                return "快换架";
            if (TryReadBoolean(properties, IsTool, out value, out ignored) && value)
                return "Tool";
            if (TryReadBoolean(properties, IsFixture, out value, out ignored) && value)
                return "Fixture";
            return "普通 Asset";
        }

        private static bool TryReadBoolean(IDictionary<string, string> properties, string key,
            out bool value, out string rawValue)
        {
            rawValue = ReadTrimmed(properties, key);
            if (string.IsNullOrEmpty(rawValue) || rawValue == "0" ||
                rawValue.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                rawValue.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
            if (rawValue == "1" || rawValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            value = false;
            return false;
        }

        private static string ReadTrimmed(IDictionary<string, string> properties, string key)
        {
            string value;
            return properties != null && properties.TryGetValue(key, out value)
                ? (value ?? string.Empty).Trim() : string.Empty;
        }

        private static void RequireValue(ICollection<string> issues, string value, string propertyName,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(value))
                issues.Add(reason + "，属性 [" + propertyName + "] 不能为空。");
        }

        private static void ValidateInterfaceList(ICollection<string> issues, string propertyName,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.IndexOf('；') >= 0)
                issues.Add("属性 [" + propertyName + "] 的多个接口必须使用英文分号 [;] 分隔。");
            var values = value.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length != 0).ToList();
            if (values.Count != values.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                issues.Add("属性 [" + propertyName + "] 包含重复接口。");
        }

        private static void CopyClassification(IDictionary<string, string> target,
            IDictionary<string, string> source, string scope, string fileName)
        {
            if (source == null) return;
            foreach (var pair in source)
            {
                if (!IsClassificationProperty(pair.Key)) continue;
                if (target.ContainsKey(pair.Key))
                    throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                        "模型 [{0}] 的分类属性 [{1}] 同时存在于文件级和配置级，不能确定唯一值。", fileName, pair.Key));
                target.Add(pair.Key, pair.Value ?? string.Empty);
            }
        }

        private static bool IsClassificationProperty(string name)
        {
            return string.Equals(name, IsAsset, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, AssetVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, AssemblyVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, AssetClass, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, DesignPurpose, StringComparison.OrdinalIgnoreCase);
        }

        private static void Copy(IDictionary<string, string> target, IDictionary<string, string> source, string scope)
        {
            if (source == null) return;
            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new ValidationException(scope + "自定义属性包含空名称。");
                if (target.ContainsKey(pair.Key))
                    throw new ValidationException(scope + "自定义属性包含大小写不同的重复名称: [" + pair.Key + "]。");
                target.Add(pair.Key, pair.Value ?? string.Empty);
            }
        }

        public static bool ReadIsAsset(IDictionary<string, string> properties)
        {
            string raw;
            if (properties == null || !properties.TryGetValue(IsAsset, out raw)) return false;
            var value = (raw ?? string.Empty).Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRobotClass(IDictionary<string, string> properties)
        {
            if (properties == null) return false;
            foreach (var pair in properties)
            {
                if (!string.Equals(pair.Key, AssetClass, StringComparison.OrdinalIgnoreCase)) continue;
                return string.Equals((pair.Value ?? string.Empty).Trim(), "robot",
                    StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public static string RequireWanxiangAssetClass(IDictionary<string, string> properties, string modelName)
        {
            string raw;
            var value = properties != null && properties.TryGetValue(AssetClass, out raw)
                ? raw ?? string.Empty : string.Empty;
            if (value == "movable" || value == "structure" || value == "station") return value;
            if (value == "moveable")
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "Asset 模型 [{0}] 的文件级自定义属性 [class] 使用了旧拼写 [moveable]；" +
                    "Wanxiang 0.4.0 只接受 [movable]。", modelName));
            if (value == "equipment")
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "Asset 模型 [{0}] 的文件级自定义属性 [class] 使用了旧值 [equipment]；" +
                    "请按资产属性协议改为 [station]。", modelName));
            throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                "Asset 模型 [{0}] 必须设置文件级自定义属性 [class]，且值必须精确为 " +
                "[movable]、[structure] 或 [station]（全小写、无首尾空格）。", modelName));
        }

        public static string BuildRobotId(IDictionary<string, string> properties, string modelName)
        {
            var designPurpose = FirstNonBlank(properties, DesignPurpose);
            if (string.IsNullOrWhiteSpace(designPurpose))
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "Robot 模型 [{0}] 必须填写文件级自定义属性 [设计目的]，用于生成 robot_id。",
                    modelName));
            var version = RequirePositiveInteger(properties, AssetVersion, modelName);
            return designPurpose + ":" + version.ToString(CultureInfo.InvariantCulture);
        }

        private static string FirstNonBlank(IDictionary<string, string> properties, params string[] keys)
        {
            foreach (var key in keys)
            {
                string raw;
                if (properties == null || !properties.TryGetValue(key, out raw)) continue;
                var value = (raw ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        public static int RequirePositiveInteger(IDictionary<string, string> properties, string key, string modelName)
        {
            string raw;
            int value;
            if (properties == null || !properties.TryGetValue(key, out raw) ||
                !int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0)
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "模型 [{0}] 必须设置正整数属性 [{1}]。", modelName, key));
            return value;
        }
    }

    public static class ModelRules
    {
        public static void ValidateClassifiable(ModelDescriptor model)
        {
            if (model == null) throw new ValidationException("组件没有可用的分类元数据。");
            if (!model.IsSaved || string.IsNullOrWhiteSpace(model.FullPath))
                throw new ValidationException("模型 [" + (model.FileName ?? "<未命名>") + "] 尚未保存。");
            if (model.IsDirty)
                throw new ValidationException("模型 [" + model.FileName + "] 存在未保存修改。");
            if (string.IsNullOrWhiteSpace(model.FileName))
                throw new ValidationException("模型缺少文件名。");
        }

        public static void ValidateExportable(ModelDescriptor model)
        {
            ValidateClassifiable(model);
            if (string.IsNullOrWhiteSpace(model.InternalCreationTime))
                throw new ValidationException("模型 [" + model.FileName + "] 缺少 SOLIDWORKS 内部创建时间。");
        }
    }

    public static class IdentityService
    {
        public static readonly Guid UrlNamespace = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8");
        private const string AssetPrefix = "urn:solidworks-asset-export:v2:";
        private const string AssemblyPrefix = "urn:solidworks-project-assembly:v1:";
        private const string ProjectUnitPrefix = "urn:solidworks-project-unit:v1:";
        private const string NodePrefix = "urn:solidworks-export-node:v1:";

        public static Guid AssetUuid(ModelDescriptor model)
        {
            return Uuid5.Create(UrlNamespace, AssetPrefix + AssetIdentitySeed(model));
        }

        public static Guid AssemblyUuid(ModelDescriptor model)
        {
            return Uuid5.Create(UrlNamespace, AssemblyPrefix + ModelSeed(model));
        }

        public static Guid ProjectUnitUuid(Guid assemblyUuid, ModelDescriptor model)
        {
            return Uuid5.Create(UrlNamespace, ProjectUnitPrefix + Canonical.Join(assemblyUuid.ToString("D"), ModelSeed(model)));
        }

        public static Guid ExportNodeUuid(Guid assemblyUuid, string instancePath)
        {
            return Uuid5.Create(UrlNamespace, NodePrefix + Canonical.Join(assemblyUuid.ToString("D"), instancePath));
        }

        public static string AssetId(Guid uuid, int version)
        {
            if (version <= 0) throw new ArgumentOutOfRangeException("version");
            return uuid.ToString("D") + ":" + version.ToString(CultureInfo.InvariantCulture);
        }

        public static string ModelSeed(ModelDescriptor model)
        {
            if (model == null) throw new ArgumentNullException("model");
            return Canonical.Join(model.FileName, model.InternalCreationTime, model.Configuration,
                model.DisplayState, model.DocumentKind.ToString());
        }

        public static string AssetIdentitySeed(ModelDescriptor model)
        {
            if (model == null) throw new ArgumentNullException("model");
            return Canonical.Join(model.InternalCreationTime, model.FileName);
        }
    }

    public static class Canonical
    {
        public static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        public static string Join(params string[] values)
        {
            var builder = new StringBuilder();
            foreach (var value in values ?? new string[0])
            {
                var normalized = Normalize(value);
                builder.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(normalized);
                builder.Append('|');
            }
            return builder.ToString();
        }
    }

    public static class Uuid5
    {
        public static Guid Create(Guid namespaceId, string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            var namespaceBytes = ToNetworkOrder(namespaceId.ToByteArray());
            var nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] hash;
            using (var sha1 = SHA1.Create())
            {
                var input = new byte[namespaceBytes.Length + nameBytes.Length];
                Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
                Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);
                hash = sha1.ComputeHash(input);
            }
            var uuid = hash.Take(16).ToArray();
            uuid[6] = (byte)((uuid[6] & 0x0f) | 0x50);
            uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
            return new Guid(FromNetworkOrder(uuid));
        }

        private static byte[] ToNetworkOrder(byte[] bytes)
        {
            var result = (byte[])bytes.Clone();
            Swap(result, 0, 3); Swap(result, 1, 2); Swap(result, 4, 5); Swap(result, 6, 7);
            return result;
        }

        private static byte[] FromNetworkOrder(byte[] bytes) { return ToNetworkOrder(bytes); }
        private static void Swap(byte[] bytes, int left, int right)
        { var value = bytes[left]; bytes[left] = bytes[right]; bytes[right] = value; }
    }
}
