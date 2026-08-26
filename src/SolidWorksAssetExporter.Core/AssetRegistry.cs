using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace SolidWorksAssetExporter.Core
{
    [DataContract]
    public sealed class AssetRegistryDocument
    {
        public AssetRegistryDocument() { Assets = new List<AssetRegistration>(); }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "assets", Order = 2)] public IList<AssetRegistration> Assets { get; set; }
    }

    [DataContract]
    public sealed class AssetRegistration
    {
        [DataMember(Name = "asset_id", Order = 1)] public string AssetId { get; set; }
        [DataMember(Name = "uuid", Order = 2)] public string Uuid { get; set; }
        [DataMember(Name = "version", Order = 3)] public int Version { get; set; }
        [DataMember(Name = "relative_directory", Order = 4)] public string RelativeDirectory { get; set; }
        [DataMember(Name = "content_fingerprint", Order = 5)] public string ContentFingerprint { get; set; }
        [DataMember(Name = "registered_utc", Order = 6)] public string RegisteredUtc { get; set; }
    }

    public static class AssetRegistryStore
    {
        public const string FileName = "asset-registry.json";

        public static AssetRegistryDocument Load(string assetLibraryRoot)
        {
            var path = RegistryPath(assetLibraryRoot);
            if (!File.Exists(path)) return Empty();
            AssetRegistryDocument registry;
            try { registry = JsonFile.Read<AssetRegistryDocument>(path); }
            catch (Exception ex) { throw new ValidationException("Asset 注册表无法读取：" + ex.Message); }
            Validate(registry);
            return registry;
        }

        public static ISet<string> RegisteredAssetIds(string assetLibraryRoot)
        {
            var root = Path.GetFullPath(assetLibraryRoot);
            var registry = Load(root);
            if (!File.Exists(Path.Combine(root, FileName)))
            {
                Directory.CreateDirectory(root);
                WriteAtomic(root, registry);
            }
            return new HashSet<string>(registry.Assets.Select(value => value.AssetId), StringComparer.OrdinalIgnoreCase);
        }

        public static bool ImportKnownManifest(string assetLibraryRoot, string uuid, int version)
        {
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid) || version <= 0) return false;
            var normalizedUuid = parsedUuid.ToString("D");
            var directory = Path.Combine(Path.GetFullPath(assetLibraryRoot), normalizedUuid,
                "v" + version.ToString(CultureInfo.InvariantCulture));
            var manifestPath = Path.Combine(directory,
                "asset_" + normalizedUuid + "_v" + version.ToString(CultureInfo.InvariantCulture) + ".json");
            if (!File.Exists(manifestPath)) return false;
            AssetManifest manifest;
            try { manifest = JsonFile.Read<AssetManifest>(manifestPath); }
            catch (Exception ex) { throw new ValidationException("已有 Asset manifest 无法导入注册表：" + ex.Message); }
            if (!string.Equals(manifest.Uuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) || manifest.Version != version ||
                string.IsNullOrWhiteSpace(manifest.ContentFingerprint))
                throw new ValidationException("已有 Asset manifest 的 UUID、版本或内容指纹无效：" + manifestPath);
            Register(assetLibraryRoot, normalizedUuid, version, manifest.ContentFingerprint);
            return true;
        }

        public static void Register(string assetLibraryRoot, string uuid, int version, string contentFingerprint)
        {
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid)) throw new ValidationException("无法注册无效的 Asset UUID：" + uuid);
            if (version <= 0) throw new ValidationException("无法注册非正整数 Asset 版本：" + version);
            if (string.IsNullOrWhiteSpace(contentFingerprint)) throw new ValidationException("无法注册缺少内容指纹的 Asset。");

            var root = Path.GetFullPath(assetLibraryRoot);
            Directory.CreateDirectory(root);
            var registry = Load(root);
            var normalizedUuid = parsedUuid.ToString("D");
            var assetId = IdentityService.AssetId(parsedUuid, version);
            var existing = registry.Assets.FirstOrDefault(value => string.Equals(value.AssetId, assetId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new AssetRegistration();
                registry.Assets.Add(existing);
            }
            existing.AssetId = assetId;
            existing.Uuid = normalizedUuid;
            existing.Version = version;
            existing.RelativeDirectory = normalizedUuid + "/v" + version.ToString(CultureInfo.InvariantCulture);
            existing.ContentFingerprint = contentFingerprint;
            existing.RegisteredUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            registry.Assets = registry.Assets.OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase).ToList();
            Validate(registry);
            WriteAtomic(root, registry);
        }

        private static AssetRegistryDocument Empty()
        {
            return new AssetRegistryDocument { SchemaVersion = "1.0" };
        }

        private static string RegistryPath(string assetLibraryRoot)
        {
            if (string.IsNullOrWhiteSpace(assetLibraryRoot)) throw new ValidationException("Asset 资产库根目录不能为空。");
            return Path.Combine(Path.GetFullPath(assetLibraryRoot), FileName);
        }

        private static void Validate(AssetRegistryDocument registry)
        {
            if (registry == null || !string.Equals(registry.SchemaVersion, "1.0", StringComparison.Ordinal))
                throw new ValidationException("Asset 注册表 schema_version 无效。");
            registry.Assets = registry.Assets == null ? new List<AssetRegistration>() : registry.Assets.ToList();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in registry.Assets)
            {
                Guid uuid;
                if (entry == null || !Guid.TryParse(entry.Uuid, out uuid) || entry.Version <= 0)
                    throw new ValidationException("Asset 注册表包含无效的 UUID 或版本。");
                var normalizedUuid = uuid.ToString("D");
                var expectedId = IdentityService.AssetId(uuid, entry.Version);
                var expectedDirectory = normalizedUuid + "/v" + entry.Version.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(entry.AssetId, expectedId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((entry.RelativeDirectory ?? string.Empty).Replace('\\', '/'), expectedDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("Asset 注册表条目的 ID 或目录不一致：" + entry.AssetId);
                if (string.IsNullOrWhiteSpace(entry.ContentFingerprint))
                    throw new ValidationException("Asset 注册表条目缺少内容指纹：" + entry.AssetId);
                if (!ids.Add(entry.AssetId)) throw new ValidationException("Asset 注册表包含重复条目：" + entry.AssetId);
            }
        }

        private static void WriteAtomic(string root, AssetRegistryDocument registry)
        {
            var path = Path.Combine(root, FileName);
            var temporary = Path.Combine(root, ".asset-registry-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                JsonFile.Write(temporary, registry);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                throw new ValidationException("Asset 注册表写入失败：" + ex.Message);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
