using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    [DataContract]
    public sealed class ExporterSettings
    {
        public ExporterSettings() { DrawingSearchDirectories = new List<string>(); ProjectMeshFormat = ProjectMeshFormat.Step; }
        [DataMember(Name = "asset_library_root", Order = 1)] public string AssetLibraryRoot { get; set; }
        [DataMember(Name = "project_export_root", Order = 2)] public string ProjectExportRoot { get; set; }
        [DataMember(Name = "project_mesh_format", Order = 3)] public ProjectMeshFormat ProjectMeshFormat { get; set; }
        [DataMember(Name = "drawing_search_directories", Order = 4)] public IList<string> DrawingSearchDirectories { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(AssetLibraryRoot)) throw new ValidationException("必须设置 Asset 资产库根目录。");
            if (string.IsNullOrWhiteSpace(ProjectExportRoot)) throw new ValidationException("必须设置 Project 导出根目录。");
            AssetLibraryRoot = Path.GetFullPath(AssetLibraryRoot);
            ProjectExportRoot = Path.GetFullPath(ProjectExportRoot);
            var asset = AssetLibraryRoot.TrimEnd('\\') + "\\";
            var project = ProjectExportRoot.TrimEnd('\\') + "\\";
            if (asset.StartsWith(project, StringComparison.OrdinalIgnoreCase) || project.StartsWith(asset, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Asset 资产库和 Project 导出目录不能相同或相互嵌套。");
        }
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
            return File.Exists(SettingsPath) ? JsonFile.Read<ExporterSettings>(SettingsPath) : new ExporterSettings();
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
}
