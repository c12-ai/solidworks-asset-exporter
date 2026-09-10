using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        [IgnoreDataMember] public int HttpStatusCode { get; set; }
    }

    public sealed class WanxiangRegistryDownloadResult
    {
        public bool Exists { get; set; }
        public string ETag { get; set; }
    }

    [DataContract]
    public sealed class WanxiangAssetRegistrationResult
    {
        [DataMember(Name = "status", Order = 1)] public string Status { get; set; }
        [DataMember(Name = "registration", Order = 2)] public AssetRegistration Registration { get; set; }
        [IgnoreDataMember] public int HttpStatusCode { get; set; }
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
        private readonly Action<string> _diagnostic;

        public WanxiangDataClient(string baseUrl, string apiKey)
            : this(baseUrl, apiKey, CreateHandler(), null)
        {
        }

        public WanxiangDataClient(string baseUrl, string apiKey, HttpMessageHandler handler)
            : this(baseUrl, apiKey, handler, null)
        {
        }

        public WanxiangDataClient(string baseUrl, string apiKey, Action<string> diagnostic)
            : this(baseUrl, apiKey, CreateHandler(), diagnostic)
        {
        }

        public WanxiangDataClient(string baseUrl, string apiKey, HttpMessageHandler handler,
            Action<string> diagnostic)
        {
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Wanxiang 数据服务地址必须是有效的 http/https URL。");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Wanxiang API key 不能为空。");
            if (handler == null) throw new ArgumentNullException("handler");

            _baseUrl = baseUrl.Trim().TrimEnd('/');
            _diagnostic = diagnostic;
            _http = new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        public WanxiangDirectoryUploadResult UploadDirectory(string localDirectory, string remoteDirectory)
        {
            return UploadDirectory(localDirectory, remoteDirectory, null);
        }

        public WanxiangDirectoryUploadResult UploadDirectory(string localDirectory, string remoteDirectory,
            Action<string> progress)
        {
            localDirectory = Path.GetFullPath(localDirectory);
            if (!Directory.Exists(localDirectory)) throw new DirectoryNotFoundException("待上传目录不存在：" + localDirectory);
            var requestUri = Url("archive", remoteDirectory);
            var archivePath = Path.Combine(Path.GetTempPath(), "wanxiang-upload-" + Guid.NewGuid().ToString("N") + ".zip");
            var elapsed = Stopwatch.StartNew();
            try
            {
                Report(progress, "正在打包 Wanxiang Project...");
                Diagnostic("PROJECT PACK START local=" + localDirectory + " remote=" + remoteDirectory);
                CreateArchive(localDirectory, archivePath);
                var archiveBytes = new FileInfo(archivePath).Length;
                Diagnostic("PROJECT PACK COMPLETE bytes=" + archiveBytes.ToString(CultureInfo.InvariantCulture) +
                    " elapsed_ms=" + elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                Report(progress, "Wanxiang Project 打包完成（" + FormatBytes(archiveBytes) + "），正在发送到 Wanxiang...");
                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var content = new StreamContent(stream))
                using (var request = new HttpRequestMessage(HttpMethod.Put, requestUri))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    request.Content = content;
                    Diagnostic("HTTP REQUEST PUT " + requestUri.AbsoluteUri + " bytes=" +
                        archiveBytes.ToString(CultureInfo.InvariantCulture));
                    using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                    {
                        var body = ReadBody(response);
                        DiagnosticResponse("PROJECT", request.Method, requestUri, response, body, elapsed);
                        Report(progress, "Wanxiang Project 已收到 HTTP " + ((int)response.StatusCode).ToString(
                            CultureInfo.InvariantCulture) + "，正在校验响应...");
                        EnsureSuccess(response, body);
                        var result = Deserialize<WanxiangDirectoryUploadResult>(body);
                        result.HttpStatusCode = (int)response.StatusCode;
                        Report(progress, "Wanxiang Project 上传完成：HTTP " + result.HttpStatusCode.ToString(
                            CultureInfo.InvariantCulture) + "，服务端写入 " + result.FilesExtracted.ToString(
                            CultureInfo.InvariantCulture) + " 个文件。");
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticFailure("PROJECT", "PUT", requestUri, elapsed, ex);
                throw;
            }
            finally
            {
                elapsed.Stop();
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
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
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
            var requestUri = Endpoint("asset/registry");
            var elapsed = Stopwatch.StartNew();
            try
            {
                Diagnostic("HTTP REQUEST GET " + requestUri.AbsoluteUri);
                using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
                using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = ReadBody(response);
                        DiagnosticResponse("REGISTRY", request.Method, requestUri, response, body, elapsed);
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
                    DiagnosticResponse("REGISTRY", request.Method, requestUri, response,
                        "<registry bytes=" + new FileInfo(temporary).Length.ToString(
                            CultureInfo.InvariantCulture) + ">", elapsed);
                    if (File.Exists(localPath)) File.Replace(temporary, localPath, null);
                    else File.Move(temporary, localPath);
                    return new WanxiangRegistryDownloadResult { Exists = exists, ETag = etag };
                }
            }
            catch (Exception ex)
            {
                DiagnosticFailure("REGISTRY", "GET", requestUri, elapsed, ex);
                throw;
            }
            finally
            {
                elapsed.Stop();
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public WanxiangAssetRegistrationResult PublishAssetVersion(string localDirectory, string uuid,
            int version, string contentFingerprint)
        {
            localDirectory = Path.GetFullPath(localDirectory);
            if (!Directory.Exists(localDirectory))
                throw new DirectoryNotFoundException("待发布 Asset 版本目录不存在：" + localDirectory);
            Guid parsedUuid;
            if (!Guid.TryParse(uuid, out parsedUuid)) throw new ArgumentException("Asset UUID 无效：" + uuid);
            if (version <= 0) throw new ArgumentOutOfRangeException("version", "Asset 版本必须是正整数。");
            if (string.IsNullOrWhiteSpace(contentFingerprint))
                throw new ArgumentException("Asset 内容指纹不能为空。", "contentFingerprint");
            var normalizedUuid = parsedUuid.ToString("D");
            var expectedDirectoryName = "v" + version.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(Path.GetFileName(localDirectory.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)), expectedDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(localDirectory)), normalizedUuid,
                    StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("待发布 Asset 目录必须使用 <uuid>/v<version> 布局：" + localDirectory);

            var archivePath = Path.Combine(Path.GetTempPath(), "wanxiang-asset-publish-" +
                Guid.NewGuid().ToString("N") + ".zip");
            var requestUri = Endpoint("asset/" + Uri.EscapeDataString(normalizedUuid) + "/v" +
                version.ToString(CultureInfo.InvariantCulture));
            var elapsed = Stopwatch.StartNew();
            try
            {
                Diagnostic("ASSET PACK START asset_id=" + normalizedUuid + ":" +
                    version.ToString(CultureInfo.InvariantCulture) + " local=" + localDirectory);
                CreateArchive(localDirectory, archivePath);
                var archiveBytes = new FileInfo(archivePath).Length;
                Diagnostic("ASSET PACK COMPLETE asset_id=" + normalizedUuid + ":" +
                    version.ToString(CultureInfo.InvariantCulture) + " bytes=" +
                    archiveBytes.ToString(CultureInfo.InvariantCulture) + " elapsed_ms=" +
                    elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var content = new StreamContent(stream))
                using (var request = new HttpRequestMessage(HttpMethod.Put, requestUri))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    request.Headers.Add("X-Content-Fingerprint", contentFingerprint.Trim());
                    request.Content = content;
                    Diagnostic("HTTP REQUEST PUT " + requestUri.AbsoluteUri + " bytes=" +
                        archiveBytes.ToString(CultureInfo.InvariantCulture) + " fingerprint=" +
                        contentFingerprint.Trim());
                    using (var response = _http.SendAsync(request,
                        HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                    {
                        var responseBody = ReadBody(response);
                        DiagnosticResponse("ASSET", request.Method, requestUri, response, responseBody, elapsed);
                        EnsureSuccess(response, responseBody);
                        var result = Deserialize<WanxiangAssetRegistrationResult>(responseBody);
                        var expectedStatus = response.StatusCode == HttpStatusCode.Created
                            ? "registered" : "already_registered";
                        var expectedAssetId = normalizedUuid + ":" +
                            version.ToString(CultureInfo.InvariantCulture);
                        var expectedRelativeDirectory = normalizedUuid + "/v" +
                            version.ToString(CultureInfo.InvariantCulture);
                        if ((response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK) ||
                            result == null || result.Registration == null || result.Status != expectedStatus ||
                            !string.Equals(result.Registration.AssetId, expectedAssetId,
                                StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(result.Registration.Uuid, normalizedUuid,
                                StringComparison.OrdinalIgnoreCase) || result.Registration.Version != version ||
                            string.IsNullOrWhiteSpace(result.Registration.Name) ||
                            !string.Equals((result.Registration.RelativeDirectory ?? string.Empty).Replace('\\', '/'),
                                expectedRelativeDirectory, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(result.Registration.ContentFingerprint, contentFingerprint,
                                StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(result.Registration.RegisteredUtc))
                            throw new InvalidDataException(
                                "Wanxiang 资产发布响应与请求的 UUID、版本、名称、目录或指纹不一致。");
                        result.HttpStatusCode = (int)response.StatusCode;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticFailure("ASSET", "PUT", requestUri, elapsed, ex);
                throw;
            }
            finally
            {
                elapsed.Stop();
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private void Diagnostic(string message)
        {
            if (_diagnostic == null) return;
            try { _diagnostic(message); }
            catch { }
        }

        private void DiagnosticResponse(string operation, HttpMethod method, Uri uri,
            HttpResponseMessage response, string body, Stopwatch elapsed)
        {
            Diagnostic(operation + " RESPONSE " + method.Method + " " + uri.AbsoluteUri +
                " http_status=" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                " reason=" + (response.ReasonPhrase ?? string.Empty) + " elapsed_ms=" +
                elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " body=" +
                WanxiangUploadLog.SummarizeResponse(body));
        }

        private void DiagnosticFailure(string operation, string method, Uri uri,
            Stopwatch elapsed, Exception exception)
        {
            Diagnostic(operation + " FAILED " + method + " " + uri.AbsoluteUri + " elapsed_ms=" +
                elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " exception=" +
                exception.GetType().Name + " message=" +
                WanxiangUploadLog.SummarizeResponse(exception.Message));
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress == null) return;
            try { progress(message); }
            catch { }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
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
