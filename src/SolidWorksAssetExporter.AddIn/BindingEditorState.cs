using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal enum BindingAssetRole { Clicked, DirectParent, DirectChild, SameAsset, AssetParent, AssetChild }

    internal sealed class BindingAssetOccurrence
    {
        internal BindingAssetOccurrence(string context, string assetId)
        { Context = context; AssetId = assetId; }

        internal string Context { get; private set; }
        internal string AssetId { get; private set; }
    }

    internal sealed class BindingAssetRoles
    {
        internal readonly IDictionary<string, BindingAssetRole> Roles =
            new Dictionary<string, BindingAssetRole>(StringComparer.Ordinal);
        internal readonly IList<InstanceBinding> DirectRelations = new List<InstanceBinding>();
        internal readonly IList<InstanceBinding> AssetRelations = new List<InstanceBinding>();
        internal string AssetId { get; set; }
    }

    // Unsaved UI draft only. Persisted graph mutations remain in InstanceBindingRules.
    public sealed class BindingEditorState
    {
        public BindingEndpoint Parent { get; private set; }
        public readonly List<BindingEndpoint> Children = new List<BindingEndpoint>();
        public readonly List<BindingEndpoint> NextParents = new List<BindingEndpoint>();
        public void SetParent(BindingEndpoint parent) { Parent = parent; NextParents.Clear(); }
        public void AddChildren(IEnumerable<BindingEndpoint> endpoints)
        {
            var contexts = new HashSet<string>(Children.Select(c => c.Context), StringComparer.Ordinal);
            if (Parent != null) contexts.Add(Parent.Context);
            foreach (var e in endpoints)
                if (e != null && contexts.Add(e.Context)) Children.Add(e);
        }
        public void Applied(bool chain)
        {
            var applied = Children.ToList(); Clear();
            if (!chain) return;
            if (applied.Count == 1) Parent = applied[0];
            else NextParents.AddRange(applied);
        }
        public void ContinueFrom(BindingEndpoint endpoint)
        {
            if (!NextParents.Any(p => p.Context == endpoint.Context)) throw new ValidationException("请选择本轮已绑定的子组件作为下一父组件。");
            SetParent(endpoint);
        }
        public void Clear() { Parent = null; Children.Clear(); NextParents.Clear(); }
        public void RefreshEndpoints(Func<BindingEndpoint, BindingEndpoint> refresh)
        {
            var parent = Parent == null ? null : refresh(Parent);
            var children = Children.Select(refresh).ToList(); var next = NextParents.Select(refresh).ToList();
            Parent = parent; Children.Clear(); Children.AddRange(children); NextParents.Clear(); NextParents.AddRange(next);
        }
        public static IEnumerable<IGrouping<string, InstanceBinding>> Group(IEnumerable<InstanceBinding> records)
        { return records.GroupBy(r => r.Parent.Context, StringComparer.Ordinal); }
        public static List<BindingEndpoint> Parents(IEnumerable<InstanceBinding> records, BindingEndpoint child)
        { return records.Where(r => r.Child.Context == child.Context).Select(r => r.Parent).GroupBy(p => p.Context, StringComparer.Ordinal).Select(g => g.First()).ToList(); }

        internal static IList<InstanceBinding> RelationsForPick(ILookup<string, InstanceBinding> relations, string context, out string endpointContext)
        {
            while (!string.IsNullOrEmpty(context))
            {
                if (relations.Contains(context))
                {
                    endpointContext = context;
                    return relations[context].ToList();
                }
                var separator = context.LastIndexOf('/');
                context = separator < 0 ? null : context.Substring(0, separator);
            }
            endpointContext = null;
            return new InstanceBinding[0];
        }

        internal static ILookup<string, InstanceBinding> IndexRelatedInstances(IEnumerable<InstanceBinding> records)
        {
            return records.SelectMany(relation => new[] { relation.Parent.Context, relation.Child.Context }
                .Distinct(StringComparer.Ordinal).Select(context => new { Context = context, Relation = relation }))
                .ToLookup(item => item.Context, item => item.Relation, StringComparer.Ordinal);
        }

        internal static BindingAssetRoles AssetVisualization(IEnumerable<BindingAssetOccurrence> occurrences,
            string clickedContext, IEnumerable<InstanceBinding> relations)
        {
            var result = new BindingAssetRoles();
            var assetIds = occurrences.Where(item => item != null && !string.IsNullOrEmpty(item.Context) && !string.IsNullOrEmpty(item.AssetId))
                .GroupBy(item => item.Context, StringComparer.Ordinal)
                .Select(group => new { Context = group.Key, AssetIds = group.Select(item => item.AssetId).Distinct(StringComparer.Ordinal).Take(2).ToArray() })
                .Where(group => group.AssetIds.Length == 1)
                .ToDictionary(group => group.Context, group => group.AssetIds[0], StringComparer.Ordinal);
            string selectedAssetId;
            if (string.IsNullOrEmpty(clickedContext) || !assetIds.TryGetValue(clickedContext, out selectedAssetId)) return result;

            result.AssetId = selectedAssetId;
            result.Roles.Add(clickedContext, BindingAssetRole.Clicked);
            var directParents = new HashSet<string>(StringComparer.Ordinal);
            var directChildren = new HashSet<string>(StringComparer.Ordinal);
            var parentAssets = new HashSet<string>(StringComparer.Ordinal);
            var childAssets = new HashSet<string>(StringComparer.Ordinal);
            // The caller verifies the stored references. Use each verified occurrence's current
            // identity because its asset version may have advanced since the edge was saved.
            foreach (var relation in relations)
            {
                var parent = relation.Parent.Context;
                var child = relation.Child.Context;
                string parentAssetId, childAssetId;
                if (!assetIds.TryGetValue(parent, out parentAssetId) || !assetIds.TryGetValue(child, out childAssetId)) continue;

                if (parent == clickedContext) directChildren.Add(child);
                if (child == clickedContext) directParents.Add(parent);
                if (parent == clickedContext || child == clickedContext) result.DirectRelations.Add(relation);

                if (parentAssetId == selectedAssetId) childAssets.Add(childAssetId);
                if (childAssetId == selectedAssetId) parentAssets.Add(parentAssetId);
                if (parentAssetId == selectedAssetId || childAssetId == selectedAssetId) result.AssetRelations.Add(relation);
            }

            foreach (var occurrence in assetIds)
            {
                var context = occurrence.Key;
                if (context == clickedContext) continue;
                if (directParents.Contains(context)) result.Roles.Add(context, BindingAssetRole.DirectParent);
                else if (directChildren.Contains(context)) result.Roles.Add(context, BindingAssetRole.DirectChild);
                else if (occurrence.Value == selectedAssetId) result.Roles.Add(context, BindingAssetRole.SameAsset);
                else if (parentAssets.Contains(occurrence.Value)) result.Roles.Add(context, BindingAssetRole.AssetParent);
                else if (childAssets.Contains(occurrence.Value)) result.Roles.Add(context, BindingAssetRole.AssetChild);
            }
            return result;
        }

        public static string OwnerContext(string context)
        { var i = (context ?? "").LastIndexOf('/'); return i < 0 ? "" : context.Substring(0, i); }
        public static bool SameSiblingAsset(BindingEndpoint seed, BindingEndpoint other)
        {
            return !string.IsNullOrEmpty(seed.SourceIdentity) && seed.SourceIdentity == other.SourceIdentity &&
                string.Equals(seed.SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase) && seed.AssetId == other.AssetId &&
                OwnerContext(seed.Context) == OwnerContext(other.Context);
        }
    }
}
