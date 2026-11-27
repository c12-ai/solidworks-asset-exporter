using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class BindingAssetVisualization
    {
        internal readonly List<ExportNode> Assets = new List<ExportNode>();
        internal readonly List<Component2> NonAssetLeaves = new List<Component2>();
    }

    // Reuse classification metadata and occurrence ancestry within one binding operation.
    internal sealed class SwBindingEndpoints
    {
        private readonly SldWorks _app;
        private readonly SwInstanceBindings _session;
        private readonly SwPluginMutationTracker _tracker;
        private readonly SwCadNode _root;
        private readonly IDictionary<string, ModelDescriptor> _models =
            new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Branch> _branches = new Dictionary<string, Branch>(StringComparer.Ordinal);
        private Branch _rootBranch;
        private Guid _assemblyUuid;

        internal SwBindingEndpoints(SldWorks app, SwInstanceBindings session, SwPluginMutationTracker tracker = null)
        {
            _app = app;
            _session = session;
            _tracker = tracker ?? new SwPluginMutationTracker(app, true);
            _root = SwAssemblyRoot.FromActiveDocument(app, _tracker);
            Resolve = SwInstanceBindings.CacheResolver(ResolveStored);
            ResolveVisualization = SwInstanceBindings.CacheResolver(ResolveVisualizationEndpoint);
        }

        internal Func<BindingEndpoint, ExportNode> Resolve { get; private set; }
        internal Func<BindingEndpoint, ExportNode> ResolveVisualization { get; private set; }

        internal static Component2 SelectedComponent(SelectionMgr selection, int index, int mark = -1)
        {
            var component = selection.GetSelectedObjectsComponent4(index, mark) as Component2;
            if (component != null) return component;
            var item = selection.GetSelectedObject6(index, mark);
            var entity = item as Entity;
            return item as Component2 ?? (entity == null ? null : entity.GetComponent() as Component2);
        }

        internal static IEnumerable<Component2> ReadSelection(SelectionMgr selection, int count)
        {
            for (var index = 1; index <= count; index++)
                yield return SelectedComponent(selection, index);
        }

        internal static Component2 SinglePickedComponent(SelectionMgr selection, int highlightMark, out bool wholeComponent)
        {
            return SinglePickedComponent(selection, mark => mark == highlightMark, out wholeComponent);
        }

        internal static Component2 SinglePickedComponent(SelectionMgr selection, Predicate<int> isAutomaticMark, out bool wholeComponent)
        {
            wholeComponent = false;
            Component2 picked = null;
            string pickedName = null;
            var includesWholeComponent = false;
            var count = selection.GetSelectedObjectCount2(-1);
            for (var index = 1; index <= count; index++)
            {
                if (isAutomaticMark(selection.GetSelectedObjectMark(index))) continue;
                var item = selection.GetSelectedObject6(index, -1);
                var componentItem = item as Component2;
                var component = componentItem ?? selection.GetSelectedObjectsComponent4(index, -1) as Component2 ??
                    (item is Entity ? ((Entity)item).GetComponent() as Component2 : null);
                if (component == null) return null;
                var name = component.Name2;
                if (picked != null && !string.Equals(pickedName, name, StringComparison.Ordinal))
                    return null;
                picked = component;
                pickedName = name;
                includesWholeComponent |= componentItem != null;
            }
            wholeComponent = includesWholeComponent;
            return picked;
        }

        internal ExportNode FindSelectedAsset(Component2 component)
        {
            if (component == null || Equals(component, _root.Component))
                throw new ValidationException("请选择装配中的普通 Asset 实例。");
            var branch = ReadBranch(component, 0);
            if (branch.Asset == null)
                throw new ValidationException("组件 [" + component.Name2 + "] 不属于可绑定的普通 Asset。");
            return branch.Asset;
        }

        internal BindingEndpoint Capture(ExportNode asset) { return _session.Capture(asset); }

        internal IEnumerable<ExportNode> SameAssetInstances(ExportNode seed)
        {
            return SameAssetInstances(new[] { seed });
        }

        internal IEnumerable<ExportNode> SameAssetInstances(IEnumerable<ExportNode> seeds)
        {
            // The filename is part of the asset UUID. Filter before opening model documents so a
            // click in a large assembly only classifies likely matches and their ancestor chains.
            var matches = seeds.GroupBy(seed => seed.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
            if (matches.Count == 0) return matches;
            var seen = new HashSet<string>(matches.Select(seed => seed.Id), StringComparer.Ordinal);
            var assetIds = new HashSet<string>(matches.Select(seed => seed.AssetId), StringComparer.Ordinal);
            var fileNames = new HashSet<string>(matches.Select(seed =>
                Canonical.Normalize(Path.GetFileName(((SwCadNode)seed.Source).SourcePath))), StringComparer.Ordinal);
            var assembly = _app.ActiveDoc as AssemblyDoc;
            object[] components = null;
            try { if (assembly != null) components = assembly.GetComponents(false) as object[]; }
            catch (COMException) { }
            if (components == null || components.Length == 0)
            {
                matches.AddRange(ReadVisualization().Assets.Where(asset => assetIds.Contains(asset.AssetId) && seen.Add(asset.Id)));
                return matches;
            }

            foreach (var component in components.OfType<Component2>())
            {
                try
                {
                    if (!fileNames.Contains(Canonical.Normalize(Path.GetFileName(component.GetPathName())))) continue;
                    var asset = ReadBranch(component, 0).Asset;
                    if (asset != null && assetIds.Contains(asset.AssetId) && asset.Name == component.Name2 && seen.Add(asset.Id))
                        matches.Add(asset);
                }
                catch (ValidationException) { }
                catch (COMException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return matches;
        }

        internal BindingAssetVisualization ReadVisualization()
        {
            var visualization = new BindingAssetVisualization();
            CollectVisualization(_root.Component, ReadRoot(), visualization, 0);
            return visualization;
        }

        private void CollectVisualization(Component2 owner, Branch parent, BindingAssetVisualization visualization, int depth)
        {
            if (depth >= 1024) return;
            object[] children;
            try { children = owner.GetChildren() as object[]; }
            catch (COMException) { return; }
            if (children == null) return;

            foreach (var component in children.OfType<Component2>())
            {
                try
                {
                    var name = component.Name2;
                    Branch branch;
                    if (!_branches.TryGetValue(name, out branch)) branch = ReadChildBranch(component, name, parent);
                    if (branch.Asset != null)
                        visualization.Assets.Add(branch.Asset);
                    else if (branch.DocumentKind == DocumentKind.Part)
                        visualization.NonAssetLeaves.Add(component);
                    else if (branch.DocumentKind == DocumentKind.Assembly)
                        CollectVisualization(component, branch, visualization, depth + 1);
                }
                // An unavailable branch may be an asset boundary. Do not expose its descendants.
                catch (ValidationException) { }
                catch (COMException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal IEnumerable<BindingEndpoint> Siblings(BindingEndpoint seed)
        {
            var source = (SwCadNode)Resolve(seed).Source;
            var parent = source.Component.GetParent() as Component2 ?? _root.Component;
            foreach (var sibling in (parent.GetChildren() as object[] ?? new object[0]).OfType<Component2>())
            {
                if (!string.Equals(seed.SourcePath, sibling.GetPathName(), StringComparison.OrdinalIgnoreCase) ||
                    !SwComponentState.IsVisible(sibling) || SwComponentState.IsSuppressed(sibling) || sibling.IsEnvelope())
                    continue;
                var asset = FindSelectedAsset(sibling);
                if (asset.Source.Name != sibling.Name2 || asset.AssetId != seed.AssetId) continue;
                var candidate = Capture(asset);
                if (BindingEditorState.SameSiblingAsset(seed, candidate)) yield return candidate;
            }
        }

        private ExportNode ResolveStored(BindingEndpoint endpoint)
        {
            return SwInstanceBindings.ValidateEndpoint(endpoint, ResolveStoredOccurrence(endpoint));
        }

        private ExportNode ResolveVisualizationEndpoint(BindingEndpoint endpoint)
        {
            var asset = ResolveStoredOccurrence(endpoint);
            var source = (SwCadNode)asset.Source;
            if (IdentityService.AssetIdentitySeed(source.ClassificationModel) != endpoint.SourceIdentity ||
                !string.Equals(source.SourcePath, endpoint.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "] 的源模型已被替换，请解除后重新绑定。");
            return asset;
        }

        private ExportNode ResolveStoredOccurrence(BindingEndpoint endpoint)
        {
            var component = _session.ResolveComponent(endpoint);
            var asset = FindSelectedAsset(component);
            if (!string.Equals(asset.Source.Name, component.Name2, StringComparison.Ordinal))
                throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "] 已位于其他 Asset 内部。");
            return asset;
        }

        private Branch ReadBranch(Component2 component, int depth)
        {
            if (component == null || Equals(component, _root.Component)) return ReadRoot();
            if (depth >= 1024) throw new ValidationException("组件层级过深或包含循环。");
            var name = component.Name2;
            Branch branch;
            if (_branches.TryGetValue(name, out branch)) return branch;

            var parent = ReadBranch(component.GetParent() as Component2, depth + 1);
            return ReadChildBranch(component, name, parent);
        }

        private Branch ReadChildBranch(Component2 component, string name, Branch parent)
        {
            // Components under an Asset belong to its outermost occurrence for selection.
            if (parent.Asset != null)
            {
                _branches.Add(name, parent);
                return parent;
            }
            var source = new SwCadNode(_app, component, parent.Path + "/" + name, false, null, _models, _tracker);
            if (!AssemblyScanner.IsIncluded(source))
                throw new ValidationException("组件 [" + name + "] 已隐藏、抑制或设为包络。");
            var model = source.ClassificationModel;
            ModelRules.ValidateClassifiable(model);
            var properties = PropertyRules.MergeForClassification(model);
            if (PropertyRules.ReadIsWorkstation(properties, model.DocumentKind))
                PropertyRules.RequireWorkstationName(properties, name);
            var branch = new Branch { Path = source.InstancePath, DocumentKind = model.DocumentKind };
            if (PropertyRules.ReadIsAsset(properties))
            {
                if (PropertyRules.IsRobotClass(properties))
                    throw new ValidationException("机器人 [" + name + "] 不能参与承载关系。");
                ModelRules.ValidateExportable(model);
                PropertyRules.RequireWanxiangAssetClass(properties, name);
                var version = PropertyRules.ReadManagedVersion(properties, PropertyRules.AssetVersion);
                var asset = IdentityService.AssetUuid(model);
                branch.Asset = new ExportNode
                {
                    Id = IdentityService.ExportNodeUuid(_assemblyUuid, source.InstancePath).ToString("D"),
                    Name = name,
                    Kind = ExportNodeKind.Asset,
                    Source = source,
                    GeometryUuid = asset.ToString("D"),
                    AssetId = IdentityService.AssetId(asset, version),
                    Properties = properties
                };
            }
            _branches.Add(name, branch);
            return branch;
        }

        private Branch ReadRoot()
        {
            if (_rootBranch != null) return _rootBranch;
            var model = _root.ClassificationModel;
            ModelRules.ValidateClassifiable(model);
            var properties = PropertyRules.MergeForClassification(model);
            if (PropertyRules.ReadIsAsset(properties))
                throw new ValidationException("当前总装配被标记为 Asset，内部组件不能参与承载关系。");
            if (PropertyRules.ReadIsWorkstation(properties, model.DocumentKind))
                PropertyRules.RequireWorkstationName(properties, _root.Name);
            _assemblyUuid = IdentityService.AssemblyUuid(model);
            return _rootBranch = new Branch { Path = _root.InstancePath };
        }

        private sealed class Branch
        {
            public string Path;
            public ExportNode Asset;
            public DocumentKind DocumentKind;
        }
    }
}
