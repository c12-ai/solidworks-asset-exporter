using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    [DataContract]
    public sealed class WanxiangAssemblyParseReceipt
    {
        [DataMember(Name = "event_id")] public string EventId { get; private set; }
        [DataMember(Name = "job_id")] public string JobId { get; private set; }
        [DataMember(Name = "accepted")] public bool Accepted { get; private set; }
    }

    public sealed class WanxiangAssemblyParserClient : IDisposable
    {
        private const string EventsPath = "/api/v1/integrations/assembly-packages/events";
        private const string SchemaVersion = "wanxiang.assembly-upload/v2";
        private const int MaxAttempts = 3;
        private static readonly string[] KnownErrorCodes =
        {
            "assembly_project_required", "assembly_project_not_found", "assembly_project_ambiguous",
            "assembly_project_conflict", "notification_conflict", "source_identity_conflict"
        };

        private readonly Uri _eventsUri;
        private readonly HttpClient _http;
        private readonly string _providerToken;

        public WanxiangAssemblyParserClient(string baseUrl, string providerToken, HttpMessageHandler handler = null)
        {
            Uri baseUri;
            if (!TryGetBaseUri(baseUrl, out baseUri))
                throw new ValidationException("解析服务地址必须为 HTTP 或 HTTPS 源地址，例如 https://wanxiang.example.com。");
            _providerToken = (providerToken ?? string.Empty).Trim();
            if (!IsValidProviderToken(_providerToken))
                throw new ValidationException("解析服务提供方令牌必须是非空且不含空白的 ASCII 字符串。");

            _eventsUri = new Uri(baseUri, EventsPath);
            _http = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false
            }, true)
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 64 * 1024
            };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _providerToken);
        }

        public static bool IsValidBaseUrl(string baseUrl)
        {
            Uri ignored;
            return TryGetBaseUri(baseUrl, out ignored);
        }

        public static bool IsValidProviderToken(string providerToken)
        {
            var token = (providerToken ?? string.Empty).Trim();
            return token.Length > 0 && !token.Any(character => character < '!' || character > '~');
        }

        public WanxiangAssemblyParseReceipt Notify(string assemblyUuid, int assemblyVersion,
            string exportReportSha256, string projectCode, string projectName)
        {
            Guid uuid;
            if (!Guid.TryParseExact(assemblyUuid, "D", out uuid) || assemblyVersion < 1)
                throw new ValidationException("解析通知需要装配包 UUID 和正整数版本。");
            if (exportReportSha256 == null || exportReportSha256.Length != 64 ||
                exportReportSha256.Any(character => !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')))
                throw new ValidationException("解析通知需要已上传导出报告的 64 位小写 SHA-256 摘要。");

            var hasCode = !string.IsNullOrWhiteSpace(projectCode);
            var hasName = !string.IsNullOrWhiteSpace(projectName);
            if (hasCode == hasName)
                throw new ValidationException("解析通知需要填写一个现有项目的编号或名称。");
            var projectValue = hasCode ? projectCode : projectName.Trim();
            var canonicalUuid = uuid.ToString("D");
            // Preserve exact project text because a project code is case-sensitive.
            var eventSeed = SchemaVersion + "|" + Canonical.Join(canonicalUuid,
                assemblyVersion.ToString(CultureInfo.InvariantCulture), exportReportSha256, hasCode ? "code" : "name") + projectValue;
            var notification = new UploadEvent
            {
                SchemaVersion = SchemaVersion,
                EventId = Uuid5.Create(IdentityService.UrlNamespace, eventSeed).ToString("D"),
                AssemblyUuid = canonicalUuid,
                AssemblyVersion = assemblyVersion,
                ExportReportSha256 = exportReportSha256,
                Project = hasCode ? new ProjectSelector { Code = projectValue } : new ProjectSelector { Name = projectValue }
            };

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UploadEvent)).WriteObject(stream, notification);
                payload = stream.ToArray();
            }
            if (payload.Length > 4096) throw new ValidationException("解析通知的请求体超过 4096 字节，请使用较短的项目编号。");

            string failure = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var delay = TimeSpan.FromMilliseconds(250 * attempt);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, _eventsUri))
                    {
                        request.Content = new ByteArrayContent(payload);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        using (var response = _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                        {
                            var body = response.Content == null ? new byte[0] : response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            var status = (int)response.StatusCode;
                            if (response.StatusCode == HttpStatusCode.Accepted)
                            {
                                var receipt = ReadReceipt(body, notification.EventId);
                                if (receipt != null) return receipt;
                                failure = "解析服务返回 HTTP 202，但回执缺少有效的任务编号或事件编号。";
                            }
                            else
                            {
                                failure = FormatHttpError(status, Encoding.UTF8.GetString(body));
                                if (status != 408 && status != 429 && (status < 500 || status > 599)) break;
                                var retryAfter = response.Headers.RetryAfter;
                                if (retryAfter != null)
                                {
                                    var wait = retryAfter.Delta ?? (retryAfter.Date.GetValueOrDefault() - DateTimeOffset.UtcNow);
                                    if (wait > TimeSpan.FromSeconds(3)) break;
                                    if (wait > delay) delay = wait;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { failure = "解析服务请求超时。"; }
                catch (HttpRequestException) { failure = "连接解析服务失败，请检查服务地址及网络。"; }
                catch (IOException) { failure = "解析服务连接中断。"; }

                if (attempt < MaxAttempts) Thread.Sleep(delay);
            }
            throw new ValidationException(failure + " 事件编号 " + notification.EventId + "。");
        }

        public void Dispose() { _http.Dispose(); }

        private static bool TryGetBaseUri(string baseUrl, out Uri parsed)
        {
            return Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) && parsed.Host.Length > 0 &&
                parsed.UserInfo.Length == 0 && parsed.Query.Length == 0 && parsed.Fragment.Length == 0 && parsed.AbsolutePath == "/";
        }

        private WanxiangAssemblyParseReceipt ReadReceipt(byte[] body, string eventId)
        {
            if (body.Length == 0) return null;
            WanxiangAssemblyParseReceipt receipt;
            try
            {
                using (var stream = new MemoryStream(body))
                    receipt = (WanxiangAssemblyParseReceipt)new DataContractJsonSerializer(typeof(WanxiangAssemblyParseReceipt)).ReadObject(stream);
            }
            catch (SerializationException) { return null; }
            if (receipt == null || !receipt.Accepted || receipt.EventId != eventId || string.IsNullOrWhiteSpace(receipt.JobId) ||
                receipt.JobId.Any(char.IsControl) || receipt.JobId.Contains(_providerToken))
                return null;
            return receipt;
        }

        private static string FormatHttpError(int status, string body)
        {
            var code = KnownErrorCodes.FirstOrDefault(value => body.IndexOf("\"" + value + "\"", StringComparison.Ordinal) >= 0);
            return "解析服务返回 HTTP " + status.ToString(CultureInfo.InvariantCulture) +
                (code == null ? "。" : "（" + code + "）。") +
                (status == 401 || status == 403 ? "请检查解析服务提供方令牌。" : string.Empty);
        }

        [DataContract]
        private sealed class UploadEvent
        {
            [DataMember(Name = "schema_version")] public string SchemaVersion { get; set; }
            [DataMember(Name = "event_id")] public string EventId { get; set; }
            [DataMember(Name = "assembly_uuid")] public string AssemblyUuid { get; set; }
            [DataMember(Name = "assembly_version")] public int AssemblyVersion { get; set; }
            [DataMember(Name = "export_report_sha256")] public string ExportReportSha256 { get; set; }
            [DataMember(Name = "project")] public ProjectSelector Project { get; set; }
        }

        [DataContract]
        private sealed class ProjectSelector
        {
            [DataMember(Name = "code", EmitDefaultValue = false)] public string Code { get; set; }
            [DataMember(Name = "name", EmitDefaultValue = false)] public string Name { get; set; }
        }
    }
}
