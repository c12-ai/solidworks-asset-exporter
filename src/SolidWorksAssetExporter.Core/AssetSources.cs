using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SolidWorksAssetExporter.Core
{
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
            var model = node.Model;
            if (model == null || (model.DocumentKind != DocumentKind.Part && model.DocumentKind != DocumentKind.Assembly))
                throw new ValidationException("Asset 层级包含不支持的模型类型: " + node.Name);
            if (!model.IsSaved || string.IsNullOrWhiteSpace(model.FullPath))
                throw new ValidationException("Asset 层级包含未保存的模型: " + node.Name);
            if (model.IsDirty) throw new ValidationException("Asset 层级包含未保存修改的模型: " + node.Name);
            var expectedExtension = model.DocumentKind == DocumentKind.Part ? ".SLDPRT" : ".SLDASM";
            if (!string.Equals(Path.GetExtension(model.FullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Asset 层级模型类型与扩展名不一致: " + model.FullPath);
            if (seen.Add(model.FullPath)) files.Add(model.FullPath);
            if (model.DocumentKind != DocumentKind.Assembly) return;
            foreach (var child in node.GetChildren() ?? Enumerable.Empty<ICadNode>()) AddModel(child, files, seen);
        }
    }
}
