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

        public static IDictionary<string, string> Merge(ModelDescriptor model)
        {
            if (model == null) throw new ValidationException("模型元数据为空。");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Copy(result, model.FileProperties, "文件级");
            if (model.ConfigurationProperties != null)
            {
                foreach (var pair in model.ConfigurationProperties)
                {
                    if (result.ContainsKey(pair.Key))
                        throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                            "模型 [{0}] 的属性 [{1}] 同时存在于文件级和配置级，不能确定唯一值。", model.FileName, pair.Key));
                    result.Add(pair.Key, pair.Value ?? string.Empty);
                }
            }
            return result;
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
        public static void ValidateExportable(ModelDescriptor model)
        {
            if (model == null) throw new ValidationException("组件没有可用的模型文档。");
            if (!model.IsSaved || string.IsNullOrWhiteSpace(model.FullPath))
                throw new ValidationException("模型 [" + (model.FileName ?? "<未命名>") + "] 尚未保存。");
            if (model.IsDirty)
                throw new ValidationException("模型 [" + model.FileName + "] 存在未保存修改。");
            if (string.IsNullOrWhiteSpace(model.FileName))
                throw new ValidationException("模型缺少文件名。");
            if (string.IsNullOrWhiteSpace(model.InternalCreationTime))
                throw new ValidationException("模型 [" + model.FileName + "] 缺少 SOLIDWORKS 内部创建时间。");
        }
    }

    public static class IdentityService
    {
        public static readonly Guid UrlNamespace = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8");
        private const string AssetPrefix = "urn:solidworks-asset-export:v1:";
        private const string AssemblyPrefix = "urn:solidworks-project-assembly:v1:";
        private const string ProjectUnitPrefix = "urn:solidworks-project-unit:v1:";
        private const string NodePrefix = "urn:solidworks-export-node:v1:";

        public static Guid AssetUuid(ModelDescriptor model)
        {
            return Uuid5.Create(UrlNamespace, AssetPrefix + ModelSeed(model));
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
