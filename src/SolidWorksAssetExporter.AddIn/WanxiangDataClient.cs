using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    [DataContract]
    public sealed class WanxiangFileUploadResult
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "overwritten")] public bool Overwritten { get; set; }
    }

    [DataContract]
    public sealed class WanxiangDirectoryUploadResult
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "files_extracted")] public int FilesExtracted { get; set; }
        [DataMember(Name = "bytes_written")] public long BytesWritten { get; set; }
    }

    public sealed class WanxiangRegistryDownloadResult
    {
        public bool Exists { get; set; }
        public string ETag { get; set; }
    }

    [DataContract]
    public sealed class WanxiangAssetRegistrationRequest
    {
        [DataMember(Name = "uuid", Order = 1)] public string Uuid { get; set; }
        [DataMember(Name = "version", Order = 2)] public int Version { get; set; }
        [DataMember(Name = "content_fingerprint", Order = 3)] public string ContentFingerprint { get; set; }
    }

    [DataContract]
    public sealed class WanxiangAssetRegistrationResult
    {
        [DataMember(Name = "status", Order = 1)] public string Status { get; set; }
        [DataMember(Name = "registration", Order = 2)] public AssetRegistration Registration { get; set; }
    }

    public sealed class WanxiangDataServiceException : Exception
    {
        public WanxiangDataServiceException(HttpStatusCode statusCode, string responseBody)
            : base("Wanxiang 数据服务返回 HTTP " + (int)statusCode + "：" + (responseBody ?? string.Empty))
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }

        public HttpStatusCode StatusCode { get; private set; }
        public string ResponseBody { get; private set; }
    }

    public static class WanxiangRemotePath
    {
        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("远端路径必须是相对路径，不能以 / 开头。");
            value = value.TrimEnd('/');
            if (value.IndexOf('\\') >= 0) throw new ArgumentException("远端路径必须使用 /，不能包含反斜杠。");
            var segments = value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && segments[0].Length >= 2 && segments[0][1] == ':')
                throw new ArgumentException("远端路径不能包含 Windows 盘符。");
            foreach (var segment in segments)
            {
                if (segment == ".." || segment.StartsWith(".", StringComparison.Ordinal) ||
                    segment.StartsWith("#", StringComparison.Ordinal) || segment.IndexOf('\0') >= 0)
                    throw new ArgumentException("远端路径包含数据服务禁止的路径段：" + segment);
            }
            return string.Join("/", segments);
        }

        public static string Combine(params string[] values)
        {
            return Normalize(string.Join("/", (values ?? new string[0]).Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        public static string Encode(string value)
        {
            var normalized = Normalize(value);
            return string.Join("/", normalized.Split('/').Select(Uri.EscapeDataString));
        }
    }

    public sealed class WanxiangDataClient : IDisposable
    {
        private readonly string _baseUrl;
        private readonly HttpClient _http;

        public WanxiangDataClient(string baseUrl, string apiKey)
            : this(baseUrl, apiKey, CreateHandler())
        {
        }

        public WanxiangDataClient(string baseUrl, string apiKey, HttpMessageHandler handler)
        {
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Wanxiang 数据服务地址必须是有效的 http/https URL。");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Wanxiang API key 不能为空。");
            if (handler == null) throw new ArgumentNullException("handler");

            _baseUrl = baseUrl.Trim().TrimEnd('/');
            _http = new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        public WanxiangDirectoryUploadResult UploadDirectory(string localDirectory, string remoteDirectory)
        {
            localDirectory = Path.GetFullPath(localDirectory);
            if (!Directory.Exists(localDirectory)) throw new DirectoryNotFoundException("待上传目录不存在：" + localDirectory);
            var archivePath = Path.Combine(Path.GetTempPath(), "wanxiang-upload-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                CreateArchive(localDirectory, archivePath);
                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var content = new StreamContent(stream))
                using (var request = new HttpRequestMessage(HttpMethod.Put, Url("archive", remoteDirectory)))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    request.Content = content;
                    using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        var body = ReadBody(response);
                        EnsureSuccess(response, body);
                        return Deserialize<WanxiangDirectoryUploadResult>(body);
                    }
                }
            }
            finally
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
        }

        public WanxiangFileUploadResult UploadFile(string localPath, string remotePath)
        {
            localPath = Path.GetFullPath(localPath);
            if (!File.Exists(localPath)) throw new FileNotFoundException("待上传文件不存在。", localPath);
            using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var content = new StreamContent(stream))
            using (var request = new HttpRequestMessage(HttpMethod.Put, Url("files", remotePath)))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content = content;
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    var body = ReadBody(response);
                    EnsureSuccess(response, body);
                    return Deserialize<WanxiangFileUploadResult>(body);
                }
            }
        }

        public bool DownloadFileIfExists(string remotePath, string localPath)
        {
            localPath = Path.GetFullPath(localPath);
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, Url("files", remotePath)))
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return false;
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = ReadBody(response);
                        throw new WanxiangDataServiceException(response.StatusCode, body);
                    }
                    using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[1024 * 1024];
                        long received = 0;
                        int count;
                        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, count);
                            received += count;
                        }
                        var expected = response.Content.Headers.ContentLength;
                        if (expected.HasValue && expected.Value != received)
                            throw new InvalidDataException("Wanxiang 注册表下载不完整：预期 " + expected.Value + " 字节，实际 " + received + " 字节。");
                    }
                }
                if (File.Exists(localPath)) File.Replace(temporary, localPath, null);
                else File.Move(temporary, localPath);
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public WanxiangRegistryDownloadResult DownloadAssetRegistry(string localPath)
        {
            localPath = Path.GetFullPath(localPath);
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("asset/registry")))
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = ReadBody(response);
                        throw new WanxiangDataServiceException(response.StatusCode, body);
                    }
                    IEnumerable<string> existsHeaders;
                    bool exists;
                    if (!response.Headers.TryGetValues("X-Registry-Exists", out existsHeaders) ||
                        !bool.TryParse(existsHeaders.FirstOrDefault(), out exists))
                        throw new InvalidDataException("Wanxiang 资产注册表响应缺少有效的 X-Registry-Exists 头。");
                    using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(target);
                    }
                    var etag = response.Headers.ETag == null ? string.Empty :
                        (response.Headers.ETag.Tag ?? string.Empty).Trim('"');
                    if (string.IsNullOrWhiteSpace(etag) ||
                        !string.Equals(FileHash.Sha256(temporary), etag, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Wanxiang 资产注册表响应的 ETag 与下载内容不一致。");
                    if (File.Exists(localPath)) File.Replace(temporary, localPath, null);
                    else File.Move(temporary, localPath);
                    return new WanxiangRegistryDownloadResult { Exists = exists, ETag = etag };
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public WanxiangAssetRegistrationResult RegisterAssetVersion(string uuid, int version,
            string contentFingerprint)
        {
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid)) throw new ArgumentException("Asset UUID 无效：" + uuid);
            if (version <= 0) throw new ArgumentOutOfRangeException("version", "Asset 版本必须是正整数。");
            if (string.IsNullOrWhiteSpace(contentFingerprint))
                throw new ArgumentException("Asset 内容指纹不能为空。", "contentFingerprint");
            var body = Serialize(new WanxiangAssetRegistrationRequest
            {
                Uuid = parsedUuid.ToString("D"),
                Version = version,
                ContentFingerprint = contentFingerprint
            });
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("asset/registry")))
            {
                request.Content = content;
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    var responseBody = ReadBody(response);
                    EnsureSuccess(response, responseBody);
                    var result = Deserialize<WanxiangAssetRegistrationResult>(responseBody);
                    if (result == null || result.Registration == null ||
                        (result.Status != "registered" && result.Status != "already_registered") ||
                        !string.Equals(result.Registration.Uuid, parsedUuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                        result.Registration.Version != version ||
                        !string.Equals(result.Registration.ContentFingerprint, contentFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Wanxiang 资产注册响应与请求的 UUID、版本或指纹不一致。");
                    return result;
                }
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private Uri Url(string space, string remotePath)
        {
            return new Uri(_baseUrl + "/" + space + "/" + WanxiangRemotePath.Encode(remotePath), UriKind.Absolute);
        }

        private Uri Endpoint(string relativePath)
        {
            return new Uri(_baseUrl + "/" + relativePath.TrimStart('/'), UriKind.Absolute);
        }

        private static HttpMessageHandler CreateHandler()
        {
            return new HttpClientHandler { UseProxy = false };
        }

        private static string ReadBody(HttpResponseMessage response)
        {
            return response.Content == null ? string.Empty : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        private static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (!response.IsSuccessStatusCode) throw new WanxiangDataServiceException(response.StatusCode, body);
        }

        private static T Deserialize<T>(string json)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                    return (T)serializer.ReadObject(stream);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Wanxiang 数据服务响应无法解析：" + ex.Message);
            }
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void CreateArchive(string sourceDirectory, string archivePath)
        {
            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = RelativePath(sourceDirectory, file);
                    if (IsHidden(relative)) continue;
                    var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = entry.Open()) input.CopyTo(output);
                }
            }
        }

        private static string RelativePath(string root, string path)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("文件不在上传目录内：" + path);
            return path.Substring(prefix.Length);
        }

        private static bool IsHidden(string relativePath)
        {
            return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.StartsWith(".", StringComparison.Ordinal) || segment.StartsWith("#", StringComparison.Ordinal));
        }
    }
}
