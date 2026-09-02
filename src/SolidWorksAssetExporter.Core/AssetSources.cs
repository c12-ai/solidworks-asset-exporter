using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SolidWorksAssetExporter.Core
{
    public static class AssetSourcePathPolicy
    {
        public static bool IsSessionEmbeddedModelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var normalized = Path.GetFullPath(path).Replace('/', '\\');
                var tempSwxRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\swx";
                var temporaryEmbedded = normalized.StartsWith(tempSwxRoot, StringComparison.OrdinalIgnoreCase) &&
                    (normalized.IndexOf("\\VC~~\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     normalized.IndexOf("\\IC~~\\", StringComparison.OrdinalIgnoreCase) >= 0);
                // SOLIDWORKS names a virtual component PartName^OwningAssemblyName.
                // It is stored inside the owning SLDASM and has no independent source file.
                var virtualName = Path.GetFileNameWithoutExtension(normalized)
                    .IndexOf('^') >= 0;
                return temporaryEmbedded || virtualName;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IList<string> MissingExternalFiles(IEnumerable<string> allowedFiles, IEnumerable<string> packagedFiles)
        {
            var packaged = new HashSet<string>((packagedFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            return (allowedFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Where(path => !packaged.Contains(path) && !IsSessionEmbeddedModelPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static class AssetSourcePlanner
    {
        public static IList<string> CollectModelFiles(ICadNode assetRoot)
        {
            if (assetRoot == null) throw new ArgumentNullException("assetRoot");
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddModel(assetRoot, files, seen);
            return files;
        }

        private static void AddModel(ICadNode node, IList<string> files, ISet<string> seen)
        {
            if (node.IsSuppressed) return;
            var source = node as ICadSourceReference;
            var model = source == null ? node.Model : null;
            var kind = source == null ? (model == null ? DocumentKind.Unknown : model.DocumentKind) : source.SourceDocumentKind;
            var path = source == null ? (model == null ? string.Empty : model.FullPath) : source.SourcePath;
            if (kind != DocumentKind.Part && kind != DocumentKind.Assembly)
                throw new ValidationException("Asset 层级包含不支持的模型类型: " + node.Name);
            if (string.IsNullOrWhiteSpace(path) || (model != null && !model.IsSaved))
                throw new ValidationException("Asset 层级包含未保存的模型: " + node.Name);
            if (model != null && model.IsDirty) throw new ValidationException("Asset 层级包含未保存修改的模型: " + node.Name);
            var expectedExtension = kind == DocumentKind.Part ? ".SLDPRT" : ".SLDASM";
            if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Asset 层级模型类型与扩展名不一致: " + path);
            if (!AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path) && seen.Add(path)) files.Add(path);
            if (kind != DocumentKind.Assembly) return;
            foreach (var child in node.GetChildren() ?? Enumerable.Empty<ICadNode>()) AddModel(child, files, seen);
        }
    }
}
