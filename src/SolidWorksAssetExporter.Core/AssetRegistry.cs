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

    public enum AssetVersionDecisionKind
    {
        NewAsset,
        ReuseCurrentVersion,
        CreateNewVersion,
        ContentAlreadyRegisteredAtDifferentVersion,
        UpgradeRequired
    }

    public sealed class AssetVersionDecision
    {
        public AssetVersionDecisionKind Kind { get; set; }
        public bool CanExport { get; set; }
        public int? RegisteredVersion { get; set; }
        public int? SuggestedVersion { get; set; }
    }

    public static class AssetVersionPolicy
    {
        public static AssetVersionDecision Evaluate(string uuid, int currentVersion, string currentFingerprint,
            IEnumerable<AssetRegistration> registrations)
        {
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid)) throw new ValidationException("无法判断无效的 Asset UUID：" + uuid);
            if (currentVersion <= 0) throw new ValidationException("无法判断非正整数 Asset 版本。");
            if (string.IsNullOrWhiteSpace(currentFingerprint)) throw new ValidationException("无法判断缺少内容指纹的 Asset。");
            var normalizedUuid = parsedUuid.ToString("D");
            var sameUuid = (registrations ?? Enumerable.Empty<AssetRegistration>())
                .Where(value => value != null && string.Equals(value.Uuid, normalizedUuid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.Version).ToList();
            var matching = sameUuid.Where(value => string.Equals(value.ContentFingerprint, currentFingerprint,
                StringComparison.OrdinalIgnoreCase)).OrderByDescending(value => value.Version).ToList();
            var exactMatch = matching.FirstOrDefault(value => value.Version == currentVersion);
            if (exactMatch != null)
            {
                return new AssetVersionDecision
                {
                    Kind = AssetVersionDecisionKind.ReuseCurrentVersion,
                    CanExport = true,
                    RegisteredVersion = currentVersion
                };
            }
            if (matching.Count != 0)
            {
                return new AssetVersionDecision
                {
                    Kind = AssetVersionDecisionKind.ContentAlreadyRegisteredAtDifferentVersion,
                    CanExport = false,
                    RegisteredVersion = matching[0].Version
                };
            }
            if (sameUuid.Count == 0)
                return new AssetVersionDecision { Kind = AssetVersionDecisionKind.NewAsset, CanExport = true };

            var maximumVersion = sameUuid.Max(value => value.Version);
            if (currentVersion > maximumVersion)
            {
                return new AssetVersionDecision
                {
                    Kind = AssetVersionDecisionKind.CreateNewVersion,
                    CanExport = true,
                    RegisteredVersion = maximumVersion
                };
            }
            return new AssetVersionDecision
            {
                Kind = AssetVersionDecisionKind.UpgradeRequired,
                CanExport = false,
                RegisteredVersion = maximumVersion,
                SuggestedVersion = maximumVersion + 1
            };
        }
    }

    public static class AssetRegistryStore
    {
        public const string FileName = "asset-registry.json";

        public static AssetRegistryDocument Load(string assetLibraryRoot)
        {
            var path = RegistryPath(assetLibraryRoot);
            if (!File.Exists(path)) return Empty();
            return LoadFromFile(path);
        }

        public static AssetRegistryDocument LoadFromFile(string registryPath)
        {
            if (string.IsNullOrWhiteSpace(registryPath) || !File.Exists(registryPath))
                throw new ValidationException("Asset 注册表不存在：" + registryPath);
            AssetRegistryDocument registry;
            try { registry = JsonFile.Read<AssetRegistryDocument>(registryPath); }
            catch (Exception ex) { throw new ValidationException("Asset 注册表无法读取：" + ex.Message); }
            Validate(registry);
            return registry;
        }

        public static AssetRegistryDocument CreateEmpty()
        {
            return Empty();
        }

        public static void Replace(string assetLibraryRoot, AssetRegistryDocument registry)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            Validate(registry);
            var root = Path.GetFullPath(assetLibraryRoot);
            Directory.CreateDirectory(root);
            WriteAtomic(root, registry);
        }

        public static void ReplaceFromFile(string assetLibraryRoot, string incomingRegistryPath)
        {
            Replace(assetLibraryRoot, LoadFromFile(incomingRegistryPath));
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

        public static int ImportKnownManifestsForUuid(string assetLibraryRoot, string uuid)
        {
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid)) throw new ValidationException("无法导入无效的 Asset UUID：" + uuid);
            var root = Path.GetFullPath(assetLibraryRoot);
            var normalizedUuid = parsedUuid.ToString("D");
            var uuidDirectory = Path.Combine(root, normalizedUuid);
            if (!Directory.Exists(uuidDirectory)) return 0;

            Directory.CreateDirectory(root);
            var registry = Load(root);
            var byId = registry.Assets.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            foreach (var versionDirectory in Directory.EnumerateDirectories(uuidDirectory, "v*", SearchOption.TopDirectoryOnly))
            {
                int version;
                var directoryName = Path.GetFileName(versionDirectory);
                if (directoryName == null || directoryName.Length < 2 ||
                    !int.TryParse(directoryName.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version <= 0)
                    continue;
                var manifestPath = Path.Combine(versionDirectory,
                    "asset_" + normalizedUuid + "_v" + version.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(manifestPath)) continue;
                AssetManifest manifest;
                try { manifest = JsonFile.Read<AssetManifest>(manifestPath); }
                catch (Exception ex) { throw new ValidationException("已有 Asset manifest 无法导入注册表：" + ex.Message); }
                if (!string.Equals(manifest.Uuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) ||
                    manifest.Version != version || string.IsNullOrWhiteSpace(manifest.ContentFingerprint))
                    throw new ValidationException("已有 Asset manifest 的 UUID、版本或内容指纹无效：" + manifestPath);
                var assetId = IdentityService.AssetId(parsedUuid, version);
                AssetRegistration existing;
                if (byId.TryGetValue(assetId, out existing))
                {
                    if (!string.Equals(existing.ContentFingerprint, manifest.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new ValidationException("Asset 注册表与本地 manifest 内容指纹冲突：" + assetId);
                    continue;
                }
                var entry = new AssetRegistration
                {
                    AssetId = assetId,
                    Uuid = normalizedUuid,
                    Version = version,
                    RelativeDirectory = normalizedUuid + "/v" + version.ToString(CultureInfo.InvariantCulture),
                    ContentFingerprint = manifest.ContentFingerprint,
                    RegisteredUtc = File.GetLastWriteTimeUtc(manifestPath).ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
                };
                registry.Assets.Add(entry);
                byId.Add(assetId, entry);
                imported++;
            }
            if (imported == 0) return 0;
            registry.Assets = registry.Assets.OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase).ToList();
            Validate(registry);
            WriteAtomic(root, registry);
            return imported;
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

        public static int MergeFromFile(string assetLibraryRoot, string incomingRegistryPath)
        {
            if (string.IsNullOrWhiteSpace(incomingRegistryPath) || !File.Exists(incomingRegistryPath))
                throw new ValidationException("待合并的 Asset 注册表不存在：" + incomingRegistryPath);

            var incoming = LoadFromFile(incomingRegistryPath);

            var root = Path.GetFullPath(assetLibraryRoot);
            Directory.CreateDirectory(root);
            var local = Load(root);
            var byId = local.Assets.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            foreach (var entry in incoming.Assets)
            {
                AssetRegistration existing;
                if (byId.TryGetValue(entry.AssetId, out existing))
                {
                    if (!SameRegistration(existing, entry))
                        throw new ValidationException("本地与远端 Asset 注册表冲突：" + entry.AssetId);
                    continue;
                }
                local.Assets.Add(entry);
                byId.Add(entry.AssetId, entry);
                imported++;
            }
            local.Assets = local.Assets.OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase).ToList();
            Validate(local);
            WriteAtomic(root, local);
            return imported;
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

        private static bool SameRegistration(AssetRegistration left, AssetRegistration right)
        {
            return string.Equals(left.AssetId, right.AssetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase) &&
                left.Version == right.Version &&
                string.Equals((left.RelativeDirectory ?? string.Empty).Replace('\\', '/'),
                    (right.RelativeDirectory ?? string.Empty).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.ContentFingerprint, right.ContentFingerprint, StringComparison.OrdinalIgnoreCase);
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
