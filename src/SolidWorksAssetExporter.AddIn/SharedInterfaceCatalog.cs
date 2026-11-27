using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class InterfaceCatalogResult
    {
        public List<string> Names { get; set; }
        public string Source { get; set; }
        public string Warning { get; set; }
    }

    public sealed class SharedInterfaceCatalog
    {
        internal const string RemoteDirectory = "asset-interface-catalog/v2";
        private const int ConcurrentReads = 8;
        private const string EntrySchema = "1.0";
        private const string EntryExtension = ".json";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Func<ExporterSettings> _settings;
        private readonly Func<HttpMessageHandler> _handler;
        private readonly InterfaceCatalogStore _local;
        private readonly string _cacheRoot;

        public SharedInterfaceCatalog() : this(LoadSettings,
            () => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, MaxConnectionsPerServer = ConcurrentReads },
            new InterfaceCatalogStore(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksAssetExporter", "SharedInterfaceCatalog")) { }

        internal SharedInterfaceCatalog(Func<ExporterSettings> settings, Func<HttpMessageHandler> handler,
            InterfaceCatalogStore local, string cacheRoot)
        {
            _settings = settings;
            _handler = handler;
            _local = local;
            _cacheRoot = cacheRoot;
        }

        public InterfaceCatalogResult Load()
        {
            List<CachedName> cached = null;
            try
            {
                var settings = _settings();
                var url = NormalizeUrl(settings.WanxiangBaseUrl);
                var cachePath = CachePath(url);
                cached = ReadCache(cachePath);
                using (var client = Open(url, settings.WanxiangApiKey))
                {
                    var names = ReadOnline(client, cached);
                    return OnlineResult(cachePath, names);
                }
            }
            catch (Exception ex)
            {
                var names = cached == null ? _local.Load() : MechanicalNames(cached);
                return new InterfaceCatalogResult
                {
                    Names = names, Source = "local",
                    Warning = "在线接口候选库读取失败，已使用" + (cached == null ? "本机候选库。" : "最近一次在线缓存。") +
                        " " + ErrorMessage(ex)
                };
            }
        }

        public InterfaceCatalogResult Merge(IEnumerable<string> names)
        {
            // Encode the full batch before reading settings or sending any request.
            var requested = names.Select(InterfaceEditRules.MechanicalName).Distinct(StringComparer.Ordinal)
                .Select(name => new { Name = name, Bytes = EncodeEntry(name), File = MarkerName(name) }).ToArray();
            var written = new List<string>();
            try
            {
                var settings = _settings();
                var url = NormalizeUrl(settings.WanxiangBaseUrl);
                var cachePath = CachePath(url);
                using (var client = Open(url, settings.WanxiangApiKey))
                {
                    var online = ReadOnline(client, ReadCache(cachePath));
                    var known = new HashSet<string>(online.Select(entry => entry.Name), StringComparer.Ordinal);
                    foreach (var entry in requested.Where(entry => !known.Contains(entry.Name)))
                    {
                        client.UploadJsonFile(RemoteDirectory + "/" + entry.File, entry.Bytes);
                        written.Add(entry.Name);
                        online.Add(new CachedName { Name = entry.Name, Size = entry.Bytes.LongLength });
                    }
                    return OnlineResult(cachePath, online);
                }
            }
            catch (Exception ex)
            {
                throw new ValidationException("在线接口候选库更新失败。" + (written.Count == 0 ? "" :
                    "服务已确认新增 " + written.Count + " 项，重试会跳过已存在名称。") + " " + ErrorMessage(ex));
            }
        }

        private WanxiangDataClient Open(string url, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ValidationException("请先配置 Wanxiang 数据服务 API key。");
            return new WanxiangDataClient(url, apiKey, _handler(), TimeSpan.FromSeconds(8));
        }

        private static List<CachedName> ReadOnline(WanxiangDataClient client, List<CachedName> cached)
        {
            var listed = client.ListDirectory(RemoteDirectory);
            if (listed == null) throw new InvalidDataException("在线接口候选目录响应为空。");
            var files = listed.Where(file => file != null && file.Type == "file" && IsMarkerName(file.Name)).ToArray();
            if (files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Length)
                throw new InvalidDataException("在线接口候选目录含重复文件。");
            var known = (cached ?? new List<CachedName>()).ToDictionary(entry => MarkerName(entry.Name), StringComparer.Ordinal);
            var results = new CachedName[files.Length];
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentReads }, index =>
            {
                var file = files[index];
                CachedName hit;
                if (known.TryGetValue(file.Name, out hit) && hit.Modified == file.Modified && hit.Size == file.Size)
                {
                    results[index] = hit;
                    return;
                }
                var bytes = client.DownloadFileBytes(RemoteDirectory + "/" + file.Name);
                if (bytes.LongLength != file.Size)
                    throw new InvalidDataException("在线接口候选文件校验失败：" + file.Name);
                var name = DecodeEntry(bytes, file.Name);
                results[index] = new CachedName { Name = name, Size = file.Size, Modified = file.Modified };
            });
            return results.ToList();
        }

        private InterfaceCatalogResult OnlineResult(string path, List<CachedName> online)
        {
            var sorted = online.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
            string warning = null;
            try { SaveCache(path, sorted); }
            catch (Exception ex) { warning = "在线候选库可用，本机缓存更新失败。 " + ErrorMessage(ex); }
            return new InterfaceCatalogResult
            {
                Names = MechanicalNames(sorted), Source = "online", Warning = warning
            };
        }

        private static List<string> MechanicalNames(IEnumerable<CachedName> entries)
        {
            return entries.Select(entry => entry.Name).Where(name => !PropertyRules.IsContentsInterface(name)).ToList();
        }

        private static List<CachedName> ReadCache(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var cached = JsonFile.Read<List<CachedName>>(path);
                if (cached == null || cached.Any(entry => entry == null || entry.Name != InterfaceEditRules.Name(entry.Name) ||
                    entry.Size <= 0) ||
                    cached.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != cached.Count)
                    return null;
                return cached;
            }
            catch (Exception) { return null; }
        }

        private static void SaveCache(string path, List<CachedName> names)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                JsonFile.Write(temporary, names);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private string CachePath(string url)
        { return Path.Combine(_cacheRoot, FileHash.Sha256Text(Canonical.Join(url, RemoteDirectory)) + ".json"); }

        private static string NormalizeUrl(string value)
        {
            Uri url;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                throw new ValidationException("请先配置有效的 Wanxiang 数据服务地址。");
            return url.AbsoluteUri.TrimEnd('/');
        }

        private static byte[] EncodeEntry(string name)
        {
            Utf8.GetByteCount(name);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RemoteName)).WriteObject(stream,
                    new RemoteName { Schema = EntrySchema, Name = name });
                return stream.ToArray();
            }
        }

        private static string DecodeEntry(byte[] bytes, string file)
        {
            Utf8.GetCharCount(bytes);
            RemoteName entry;
            using (var stream = new MemoryStream(bytes, false))
                entry = JsonFile.Read<RemoteName>(stream);
            if (entry == null || entry.Schema != EntrySchema || entry.Name != InterfaceEditRules.Name(entry.Name) ||
                MarkerName(entry.Name) != file)
                throw new InvalidDataException("在线接口候选 JSON 校验失败：" + file);
            return entry.Name;
        }

        private static string MarkerName(string name) { return FileHash.Sha256Text(name) + EntryExtension; }

        private static bool IsMarkerName(string name)
        {
            return name != null && name.Length == 64 + EntryExtension.Length && name.EndsWith(EntryExtension, StringComparison.Ordinal) &&
                name.Take(64).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static string ErrorMessage(Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count != 0)
                error = aggregate.Flatten().InnerExceptions[0];
            var service = error as WanxiangDataServiceException;
            return service == null ? error.Message : "Wanxiang 数据服务返回 HTTP " + (int)service.StatusCode + "。";
        }

        private static ExporterSettings LoadSettings()
        {
            var settings = new SettingsStore().Load();
            settings.WanxiangApiKey = new WanxiangApiKeyStore().Load();
            return settings;
        }

        [DataContract]
        private sealed class RemoteName
        {
            [DataMember(Name = "schema_version", Order = 1, IsRequired = true)] public string Schema { get; set; }
            [DataMember(Name = "name", Order = 2, IsRequired = true)] public string Name { get; set; }
        }

        [DataContract]
        private sealed class CachedName
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
            [DataMember(Name = "size", IsRequired = true)] public long Size { get; set; }
            [DataMember(Name = "mtime")] public double? Modified { get; set; }
        }
    }
}
