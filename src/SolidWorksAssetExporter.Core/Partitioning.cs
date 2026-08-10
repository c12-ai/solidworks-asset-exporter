using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SolidWorksAssetExporter.Core
{
    public sealed class AssemblyScanner
    {
        public ScanNode Scan(ICadNode root)
        {
            if (root == null) throw new ArgumentNullException("root");
            return ScanIncluded(root);
        }

        private static ScanNode ScanIncluded(ICadNode node)
        {
            ModelRules.ValidateExportable(node.Model);
            var properties = PropertyRules.Merge(node.Model);
            var result = new ScanNode { Source = node, Properties = properties };

            // This return is deliberately before GetChildren. An Asset is an opaque semantic boundary.
            if (PropertyRules.ReadIsAsset(properties))
            {
                result.Classification = ScanClassification.AssetBoundary;
                result.AssetVersion = PropertyRules.RequirePositiveInteger(properties, PropertyRules.AssetVersion, node.Name);
                return result;
            }

            var visibleChildren = (node.GetChildren() ?? Enumerable.Empty<ICadNode>())
                .Where(IsIncluded)
                .OrderBy(child => child.InstancePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var child in visibleChildren) result.Children.Add(ScanIncluded(child));

            result.Classification = result.Children.Any(child => child.Classification != ScanClassification.NoAsset)
                ? ScanClassification.ContainsAsset
                : ScanClassification.NoAsset;
            return result;
        }

        public static bool IsIncluded(ICadNode node)
        {
            return node != null && node.IsVisible && !node.IsSuppressed && !node.IsEnvelope;
        }
    }

    public sealed class ExportPlanBuilder
    {
        public AssemblyExportPlan Build(ScanNode root, ProjectMeshFormat meshFormat)
        {
            if (root == null || root.Source == null) throw new ArgumentNullException("root");
            var version = PropertyRules.RequirePositiveInteger(root.Properties, PropertyRules.AssemblyVersion, root.Source.Name);
            var assemblyUuid = IdentityService.AssemblyUuid(root.Source.Model);
            var plan = new AssemblyExportPlan
            {
                AssemblyUuid = assemblyUuid.ToString("D"),
                AssemblyVersion = version,
                MeshFormat = meshFormat
            };

            if (root.Classification == ScanClassification.AssetBoundary || root.Classification == ScanClassification.NoAsset)
            {
                plan.Roots.Add(BuildNode(root, null, root.Source.WorldTransform, assemblyUuid, meshFormat));
            }
            else
            {
                var children = root.Children.OrderByDescending(child => child.Source.IsFixed)
                    .ThenBy(child => child.Source.InstancePath ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                if (children.Count == 0 || !children.Any(child => child.Source.IsFixed))
                    throw new ValidationException("混合导出的总装配体没有可见、未抑制且非包络的固定顶层组件。");
                foreach (var child in children)
                    plan.Roots.Add(BuildNode(child, null, root.Source.WorldTransform, assemblyUuid, meshFormat));
            }

            ExportPlanValidator.Validate(plan);
            return plan;
        }

        private static ExportNode BuildNode(ScanNode scan, string parentId, Matrix4 parentWorld,
            Guid assemblyUuid, ProjectMeshFormat meshFormat)
        {
            var world = scan.Source.WorldTransform ?? Matrix4.Identity;
            var parent = parentWorld ?? Matrix4.Identity;
            var relative = parent.InverseRigid().Multiply(world);
            var node = new ExportNode
            {
                Id = IdentityService.ExportNodeUuid(assemblyUuid, scan.Source.InstancePath ?? scan.Source.InstanceId).ToString("D"),
                ParentId = parentId ?? string.Empty,
                Name = scan.Source.Name,
                Pose = Pose.FromTransform(relative),
                Source = scan.Source,
                Properties = new Dictionary<string, string>(scan.Properties, StringComparer.OrdinalIgnoreCase)
            };

            if (scan.Classification == ScanClassification.AssetBoundary)
            {
                var assetUuid = IdentityService.AssetUuid(scan.Source.Model);
                node.Kind = ExportNodeKind.Asset;
                node.GeometryUuid = assetUuid.ToString("D");
                node.AssetId = IdentityService.AssetId(assetUuid, scan.AssetVersion.Value);
                return node;
            }
            if (scan.Classification == ScanClassification.NoAsset)
            {
                var projectUuid = IdentityService.ProjectUnitUuid(assemblyUuid, scan.Source.Model);
                node.Kind = ExportNodeKind.Project;
                node.GeometryUuid = projectUuid.ToString("D");
                node.MeshFile = string.Format(CultureInfo.InvariantCulture, "meshes/{0}/model.{1}",
                    node.GeometryUuid, meshFormat == ProjectMeshFormat.Step ? "step" : "stl");
                return node;
            }

            node.Kind = ExportNodeKind.Group;
            foreach (var child in scan.Children.OrderBy(child => child.Source.InstancePath ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(BuildNode(child, node.Id, world, assemblyUuid, meshFormat));
            return node;
        }
    }

    public static class ExportPlanValidator
    {
        public static void Validate(AssemblyExportPlan plan)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in plan.Roots) ValidateNode(root, ids);
        }

        private static void ValidateNode(ExportNode node, ISet<string> ids)
        {
            if (node == null) throw new ValidationException("导出树包含空节点。");
            if (!ids.Add(node.Id)) throw new ValidationException("导出树包含重复节点 ID: [" + node.Id + "]。");
            if (node.Kind == ExportNodeKind.Group)
            {
                if (!string.IsNullOrEmpty(node.AssetId) || !string.IsNullOrEmpty(node.MeshFile) || !string.IsNullOrEmpty(node.GeometryUuid))
                    throw new ValidationException("Group 节点不能包含 mesh 或几何标识。");
                if (node.Children.Count == 0) throw new ValidationException("Group 节点必须包含子节点。");
            }
            else
            {
                if (node.Children.Count != 0) throw new ValidationException(node.Kind + " 节点必须是叶节点。");
                if (node.Kind == ExportNodeKind.Asset && string.IsNullOrWhiteSpace(node.AssetId))
                    throw new ValidationException("Asset 节点缺少 asset_id。");
                if (node.Kind == ExportNodeKind.Project && string.IsNullOrWhiteSpace(node.MeshFile))
                    throw new ValidationException("Project 节点缺少 mesh 文件。");
            }
            foreach (var child in node.Children)
            {
                if (!string.Equals(child.ParentId, node.Id, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("节点 parent_id 与树层级不一致。");
                ValidateNode(child, ids);
            }
        }
    }
}
