using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class WanxiangUploadCompletion
    {
        public int AssetDirectoriesUploaded { get; set; }
        public int AssetVersionsRegistered { get; set; }
        public int AssetVersionsAlreadyRegistered { get; set; }
        public int FilesExtracted { get; set; }
        public long BytesWritten { get; set; }
        public string RemoteProjectDirectory { get; set; }
        public string RemoteRegistryPath { get; set; }
        public string UploadLogPath { get; set; }
    }

    public sealed class WanxiangExportUploader
    {
        public WanxiangUploadCompletion Upload(ExportCompletion localExport, ExporterSettings settings)
        {
            return Upload(localExport, settings, null);
        }

        public WanxiangUploadCompletion Upload(ExportCompletion localExport, ExporterSettings settings,
            Action<string> progress)
        {
            if (localExport == null) throw new ArgumentNullException("localExport");
            if (settings == null) throw new ArgumentNullException("settings");
            settings.Validate();
            if (!settings.UploadAfterExport) throw new ValidationException("当前设置未启用导出后上传。");

            var log = WanxiangUploadLog.Create();
            log.Write("SESSION START base_url=" + settings.WanxiangBaseUrl + " export_project=" +
                settings.ExportProject.ToString() + " save_registry_locally=" +
                settings.SaveRegistryLocally.ToString());
            try
            {
                using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl,
                    settings.WanxiangApiKey, log.Write))
                {
                    var completion = UploadCore(client, localExport, settings, progress, log);
                    completion.UploadLogPath = log.Path;
                    log.Write("SESSION COMPLETE assets=" + completion.AssetDirectoriesUploaded.ToString(
                        CultureInfo.InvariantCulture) + " new=" + completion.AssetVersionsRegistered.ToString(
                        CultureInfo.InvariantCulture) + " reused=" +
                        completion.AssetVersionsAlreadyRegistered.ToString(CultureInfo.InvariantCulture) +
                        " project=" + (completion.RemoteProjectDirectory ?? "<disabled>"));
                    return completion;
                }
            }
            catch (Exception ex)
            {
                log.Write("SESSION FAILED exception=" + ex.GetType().Name + " message=" +
                    WanxiangUploadLog.SummarizeResponse(ex.Message));
                Report(progress, null, "Wanxiang 上传失败；详细信息已写入上传日志。");
                throw;
            }
        }

        public WanxiangUploadCompletion Upload(WanxiangDataClient client, ExportCompletion localExport, ExporterSettings settings)
        {
            return Upload(client, localExport, settings, null);
        }

        public WanxiangUploadCompletion Upload(WanxiangDataClient client, ExportCompletion localExport,
            ExporterSettings settings, Action<string> progress)
        {
            if (localExport == null) throw new ArgumentNullException("localExport");
            if (settings == null) throw new ArgumentNullException("settings");
            settings.Validate();
            if (!settings.UploadAfterExport) throw new ValidationException("当前设置未启用导出后上传。");
            return UploadCore(client, localExport, settings, progress, null);
        }

        private WanxiangUploadCompletion UploadCore(WanxiangDataClient client, ExportCompletion localExport,
            ExporterSettings settings, Action<string> progress, WanxiangUploadLog log)
        {
            if (client == null) throw new ArgumentNullException("client");
            var completion = new WanxiangUploadCompletion();
            var remoteProjectRoot = WanxiangRemoteLayout.AssemblyPackage;
            var remoteRegistry = WanxiangRemoteLayout.AssetRegistryEndpoint;
            var downloadedRegistry = Path.Combine(Path.GetTempPath(), "wanxiang-registry-" +
                Guid.NewGuid().ToString("N") + ".json");

            try
            {
                var assetDirectories = new HashSet<string>((localExport.AssetDirectories ?? new List<string>())
                    .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                var registrations = (localExport.AssetRegistrations ?? new List<AssetRegistration>())
                    .GroupBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).OrderBy(value => value.AssetId,
                        StringComparer.OrdinalIgnoreCase).ToList();
                if (assetDirectories.Count != registrations.Count)
                    throw new ValidationException("本地 Asset 目录与待发布版本数量不一致，拒绝上传不完整批次。");
                Report(progress, log, "准备上传 " + registrations.Count.ToString(CultureInfo.InvariantCulture) +
                    " 个 Asset" + (settings.ExportProject ? " 和 1 个 Wanxiang assembly_package。" : "；本次不上传 assembly_package。"));
                for (var index = 0; index < registrations.Count; index++)
                {
                    var registration = registrations[index];
                    var directory = Path.GetFullPath(Path.Combine(settings.AssetLibraryRoot,
                        registration.Uuid, "v" + registration.Version.ToString(CultureInfo.InvariantCulture)));
                    if (!assetDirectories.Contains(directory))
                        throw new ValidationException("待发布 Asset 缺少对应的本地版本目录：" +
                            registration.AssetId);
                    var ordinal = (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                        registrations.Count.ToString(CultureInfo.InvariantCulture);
                    Report(progress, log, "正在上传 Asset " + ordinal + "：" + registration.AssetId);
                    var published = client.PublishAssetVersion(directory, registration.Uuid,
                        registration.Version, registration.ContentFingerprint);
                    completion.AssetDirectoriesUploaded++;
                    if (published.Status == "registered") completion.AssetVersionsRegistered++;
                    else completion.AssetVersionsAlreadyRegistered++;
                    Report(progress, log, "Asset " + ordinal + " 已收到 HTTP " +
                        published.HttpStatusCode.ToString(CultureInfo.InvariantCulture) + "（" +
                        published.Status + "）：" + registration.AssetId);
                }

                if (settings.ExportProject)
                {
                    if (string.IsNullOrWhiteSpace(localExport.ProjectDirectory))
                        throw new ValidationException("已选择导出 assembly_package，但本地导出结果缺少装配包目录。");
                    var projectRelative = PathPolicy.RelativeTo(settings.ProjectExportRoot, localExport.ProjectDirectory).Replace('\\', '/');
                    var remoteProject = WanxiangRemotePath.Combine(remoteProjectRoot, projectRelative);
                    Report(progress, log, "Asset 上传完成，开始处理 Wanxiang assembly_package：" + remoteProject);
                    var projectResult = client.UploadDirectory(localExport.ProjectDirectory, remoteProject,
                        delegate(string message) { Report(progress, log, message); });
                    completion.FilesExtracted += projectResult.FilesExtracted;
                    completion.BytesWritten += projectResult.BytesWritten;
                    completion.RemoteProjectDirectory = remoteProject;
                }

                if (settings.SaveRegistryLocally)
                {
                    Report(progress, log, "正在读取发布后的 Wanxiang 注册表并保存本地副本...");
                    client.DownloadAssetRegistry(downloadedRegistry);
                    AssetRegistryStore.ReplaceFromFile(settings.AssetLibraryRoot, downloadedRegistry);
                    Report(progress, log, "Wanxiang 注册表本地副本已更新。");
                }
                completion.RemoteRegistryPath = remoteRegistry;
                Report(progress, log, "Wanxiang 上传完成：全部服务器响应均已收到并通过校验。");
                return completion;
            }
            finally
            {
                if (File.Exists(downloadedRegistry)) File.Delete(downloadedRegistry);
            }
        }

        private static void Report(Action<string> progress, WanxiangUploadLog log, string message)
        {
            if (log != null) log.Write("PROGRESS " + message);
            if (progress == null) return;
            try { progress(message); }
            catch { }
        }
    }
}
