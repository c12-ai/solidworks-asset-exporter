using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    [DataContract]
    internal sealed class FileFingerprintCacheDocument
    {
        public FileFingerprintCacheDocument() { Entries = new List<FileFingerprintCacheEntry>(); }
        [DataMember(Name = "schema_version", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "entries", Order = 2)] public IList<FileFingerprintCacheEntry> Entries { get; set; }
    }

    [DataContract]
    internal sealed class FileFingerprintCacheEntry
    {
        [DataMember(Name = "path", Order = 1)] public string Path { get; set; }
        [DataMember(Name = "size", Order = 2)] public long Size { get; set; }
        [DataMember(Name = "last_write_utc_ticks", Order = 3)] public long LastWriteUtcTicks { get; set; }
        [DataMember(Name = "sha256", Order = 4)] public string Sha256 { get; set; }
    }

    public sealed class FileFingerprintCache : IDisposable
    {
        private readonly string _cachePath;
        private readonly IDictionary<string, FileFingerprintCacheEntry> _entries;
        private bool _dirty;

        public FileFingerprintCache() : this(DefaultPath()) { }

        public FileFingerprintCache(string cachePath)
        {
            if (string.IsNullOrWhiteSpace(cachePath)) throw new ArgumentException("指纹缓存路径不能为空。", "cachePath");
            _cachePath = Path.GetFullPath(cachePath);
            _entries = Load(_cachePath);
        }

        public int CacheHits { get; private set; }
        public int CacheMisses { get; private set; }

        public string Sha256(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("待计算指纹的文件不存在。", fullPath);
            var before = new FileInfo(fullPath);
            FileFingerprintCacheEntry entry;
            if (_entries.TryGetValue(fullPath, out entry) && entry.Size == before.Length &&
                entry.LastWriteUtcTicks == before.LastWriteTimeUtc.Ticks && IsSha256(entry.Sha256))
            {
                CacheHits++;
                return entry.Sha256;
            }

            var hash = FileHash.Sha256(fullPath);
            var after = new FileInfo(fullPath);
            if (before.Length != after.Length || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
                throw new ValidationException("计算指纹期间文件发生变化，请保存完成后重新预览：" + fullPath);
            _entries[fullPath] = new FileFingerprintCacheEntry
            {
                Path = fullPath,
                Size = after.Length,
                LastWriteUtcTicks = after.LastWriteTimeUtc.Ticks,
                Sha256 = hash
            };
            CacheMisses++;
            _dirty = true;
            return hash;
        }

        public void Dispose()
        {
            if (!_dirty) return;
            Save();
        }

        private void Save()
        {
            var directory = Path.GetDirectoryName(_cachePath);
            var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                JsonFile.Write(temporary, new FileFingerprintCacheDocument
                {
                    SchemaVersion = "1.0",
                    Entries = _entries.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList()
                });
                if (File.Exists(_cachePath)) File.Replace(temporary, _cachePath, null);
                else File.Move(temporary, _cachePath);
            }
            catch
            {
                // The cache is only a performance optimization. A cache write failure must not block preview/export.
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private static IDictionary<string, FileFingerprintCacheEntry> Load(string cachePath)
        {
            var result = new Dictionary<string, FileFingerprintCacheEntry>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(cachePath)) return result;
            try
            {
                var document = JsonFile.Read<FileFingerprintCacheDocument>(cachePath);
                if (document == null || !string.Equals(document.SchemaVersion, "1.0", StringComparison.Ordinal))
                    return result;
                foreach (var entry in document.Entries ?? new List<FileFingerprintCacheEntry>())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Path) || !IsSha256(entry.Sha256)) continue;
                    result[Path.GetFullPath(entry.Path)] = entry;
                }
            }
            catch
            {
                // Ignore a damaged cache and rebuild it from source files.
            }
            return result;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            foreach (var character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static string DefaultPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksAssetExporter", "asset-file-hashes.json");
        }
    }
}
