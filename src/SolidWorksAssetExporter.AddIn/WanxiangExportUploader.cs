using System;
using System.Collections.Generic;
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
    }

    public sealed class WanxiangExportUploader
    {
        public WanxiangUploadCompletion Upload(ExportCompletion localExport, ExporterSettings settings)
        {
            if (localExport == null) throw new ArgumentNullException("localExport");
            if (settings == null) throw new ArgumentNullException("settings");
            settings.Validate();
            if (!settings.UploadAfterExport) throw new ValidationException("当前设置未启用导出后上传。");

            using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey))
                return Upload(client, localExport, settings);
        }

        public WanxiangUploadCompletion Upload(WanxiangDataClient client, ExportCompletion localExport, ExporterSettings settings)
        {
            if (client == null) throw new ArgumentNullException("client");
            var completion = new WanxiangUploadCompletion();
            var remoteAssetRoot = WanxiangRemoteLayout.Assets;
            var remoteProjectRoot = WanxiangRemoteLayout.Projects;
            var remoteRegistry = WanxiangRemoteLayout.AssetRegistryEndpoint;
            var downloadedRegistry = Path.Combine(Path.GetTempPath(), "wanxiang-registry-" +
                Guid.NewGuid().ToString("N") + ".json");

            try
            {
                foreach (var directory in (localExport.AssetDirectories ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = PathPolicy.RelativeTo(settings.AssetLibraryRoot, directory).Replace('\\', '/');
                    var result = client.UploadDirectory(directory, WanxiangRemotePath.Combine(remoteAssetRoot, relative));
                    completion.AssetDirectoriesUploaded++;
                    completion.FilesExtracted += result.FilesExtracted;
                    completion.BytesWritten += result.BytesWritten;
                }

                var projectRelative = PathPolicy.RelativeTo(settings.ProjectExportRoot, localExport.ProjectDirectory).Replace('\\', '/');
                var remoteProject = WanxiangRemotePath.Combine(remoteProjectRoot, projectRelative);
                var projectResult = client.UploadDirectory(localExport.ProjectDirectory, remoteProject);
                completion.FilesExtracted += projectResult.FilesExtracted;
                completion.BytesWritten += projectResult.BytesWritten;
                completion.RemoteProjectDirectory = remoteProject;

                // The data service owns asset-registry.json. Uploading a version only stages
                // its files; POST /asset/registry validates the manifest and atomically commits
                // the registration. Never merge or overwrite the registry from the client.
                foreach (var registration in (localExport.AssetRegistrations ?? new List<AssetRegistration>())
                    .GroupBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase))
                {
                    var registered = client.RegisterAssetVersion(registration.Uuid, registration.Version,
                        registration.ContentFingerprint);
                    if (registered.Status == "registered") completion.AssetVersionsRegistered++;
                    else completion.AssetVersionsAlreadyRegistered++;
                }

                if (settings.SaveRegistryLocally)
                {
                    client.DownloadAssetRegistry(downloadedRegistry);
                    AssetRegistryStore.ReplaceFromFile(settings.AssetLibraryRoot, downloadedRegistry);
                }
                completion.RemoteRegistryPath = remoteRegistry;
                return completion;
            }
            finally
            {
                if (File.Exists(downloadedRegistry)) File.Delete(downloadedRegistry);
            }
        }
    }
}
