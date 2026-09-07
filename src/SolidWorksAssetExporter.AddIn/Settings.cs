using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    [DataContract]
    public sealed class ExporterSettings
    {
        public const string DefaultWanxiangBaseUrl = "http://192.168.12.87:8100";

        public ExporterSettings()
        {
            DrawingSearchDirectories = new List<string>();
            ProjectMeshFormat = ProjectMeshFormat.Step;
            WanxiangBaseUrl = DefaultWanxiangBaseUrl;
        }
        [DataMember(Name = "asset_library_root", Order = 1)] public string AssetLibraryRoot { get; set; }
        [DataMember(Name = "project_export_root", Order = 2)] public string ProjectExportRoot { get; set; }
        [DataMember(Name = "project_mesh_format", Order = 3)] public ProjectMeshFormat ProjectMeshFormat { get; set; }
        [DataMember(Name = "drawing_search_directories", Order = 4)] public IList<string> DrawingSearchDirectories { get; set; }
        [DataMember(Name = "upload_after_export", Order = 5)] public bool UploadAfterExport { get; set; }
        [DataMember(Name = "wanxiang_base_url", Order = 6)] public string WanxiangBaseUrl { get; set; }
        [DataMember(Name = "save_registry_locally", Order = 8)] public bool SaveRegistryLocally { get; set; }
        [DataMember(Name = "export_project", Order = 9, EmitDefaultValue = false)]
        public bool? ExportProjectOption { get; set; }
        [IgnoreDataMember]
        public bool ExportProject
        {
            get { return !ExportProjectOption.HasValue || ExportProjectOption.Value; }
            set { ExportProjectOption = value; }
        }
        [IgnoreDataMember] public string WanxiangApiKey { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(AssetLibraryRoot)) throw new ValidationException("必须设置 Asset 资产库根目录。");
            AssetLibraryRoot = Path.GetFullPath(AssetLibraryRoot);
            if (ExportProject)
            {
                if (string.IsNullOrWhiteSpace(ProjectExportRoot)) throw new ValidationException("选择导出 Project 时必须设置 Project 导出根目录。");
                ProjectExportRoot = Path.GetFullPath(ProjectExportRoot);
                var asset = AssetLibraryRoot.TrimEnd('\\') + "\\";
                var project = ProjectExportRoot.TrimEnd('\\') + "\\";
                if (asset.StartsWith(project, StringComparison.OrdinalIgnoreCase) || project.StartsWith(asset, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("Asset 资产库和 Project 导出目录不能相同或相互嵌套。");
            }
            ValidateWanxiang();
        }

        public void ValidateWanxiang()
        {
            Uri serviceUri;
            if (!Uri.TryCreate(WanxiangBaseUrl, UriKind.Absolute, out serviceUri) ||
                (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps))
                throw new ValidationException("Wanxiang 数据服务地址必须是有效的 http/https URL。");
            if (string.IsNullOrWhiteSpace(WanxiangApiKey)) throw new ValidationException("Wanxiang API key 不能为空。");
        }

        public void ApplyDefaults()
        {
            if (string.IsNullOrWhiteSpace(WanxiangBaseUrl)) WanxiangBaseUrl = DefaultWanxiangBaseUrl;
            if (DrawingSearchDirectories == null) DrawingSearchDirectories = new List<string>();
        }
    }

    public static class WanxiangRemoteLayout
    {
        public const string Projects = "projects";
        public const string AssetRegistryEndpoint = "/asset/registry";
    }

    public sealed class SettingsStore
    {
        public string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SolidWorksAssetExporter", "settings.json");
            }
        }

        public ExporterSettings Load()
        {
            var settings = File.Exists(SettingsPath) ? JsonFile.Read<ExporterSettings>(SettingsPath) : new ExporterSettings();
            settings.ApplyDefaults();
            return settings;
        }

        public void Save(ExporterSettings settings)
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var temporary = SettingsPath + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            JsonFile.Write(temporary, settings);
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);
        }
    }

    public sealed class WanxiangApiKeyStore
    {
        public string KeyPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "wanxiang", "data-service-api-key");
            }
        }

        public string Load()
        {
            return File.Exists(KeyPath) ? File.ReadAllText(KeyPath, Encoding.UTF8).Trim() : string.Empty;
        }

        public void Save(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ValidationException("Wanxiang API key 不能为空。");
            var directory = Path.GetDirectoryName(KeyPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var temporary = KeyPath + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            File.WriteAllText(temporary, apiKey.Trim() + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(KeyPath)) File.Replace(temporary, KeyPath, null);
            else File.Move(temporary, KeyPath);
        }
    }
}
