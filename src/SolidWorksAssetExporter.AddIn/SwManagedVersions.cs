using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class SwManagedVersions
    {
        private readonly SldWorks _app;

        internal SwManagedVersions(SldWorks app) { _app = app; }

        internal void RequireTarget(AnalysisResult analysis)
        {
            var doc = _app.ActiveDoc as ModelDoc2;
            var configuration = doc == null ? null : doc.ConfigurationManager.ActiveConfiguration;
            if (configuration == null || !string.Equals(doc.GetPathName(), analysis.ActiveDocumentPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(configuration.Name, analysis.ActiveConfigurationName, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("活动装配体或配置已切换，请重新预览。");
            var bindings = SwInstanceBindings.ForDocument(doc);
            if (bindings == null || bindings.Pending || bindings.Revision != analysis.BindingRevision)
                throw new ValidationException("承载关系在预览后已变化，请保存并重新预览。");
        }

        private static bool HasSuggestions(AnalysisResult analysis)
        {
            return analysis.SuggestedProjectVersion.HasValue ||
                analysis.AssetInspections.Values.Any(asset => SuggestedVersion(asset).HasValue);
        }

        internal static bool CanPrepare(AnalysisResult analysis)
        {
            if (analysis.CanExport) return true;
            return HasSuggestions(analysis) && analysis.UnsupportedAssetRoots.Count == 0 &&
                analysis.AssetInspections.Values.All(asset => asset.VersionDecision != null) &&
                analysis.ValidationErrors.Count == 0;
        }

        internal static int? SuggestedVersion(AssetInspection asset)
        {
            if (asset.VersionDecision == null) return null;
            if (asset.RequiresVersionUpgrade) return asset.SuggestedVersion;
            if (asset.VersionDecision.Kind == AssetVersionDecisionKind.ContentAlreadyRegisteredAtDifferentVersion)
                return asset.VersionDecision.RegisteredVersion;
            return null;
        }

        internal AnalysisResult Prepare(ExportCoordinator coordinator, ExporterSettings settings,
            WanxiangRegistrySnapshot registry, AnalysisResult analysis, IList<string> saved)
        {
            if (!CanPrepare(analysis)) return analysis;
            RequireTarget(analysis);
            try
            {
                var changes = AssetEdits(analysis).ToList();
                foreach (var change in changes)
                {
                    change.Item1.Save(change.Item2);
                    saved.Add(change.Item1.Path + " asset_version=" + change.Item2[PropertyRules.AssetVersion]);
                }
                if (changes.Count != 0)
                {
                    RequireTarget(analysis);
                    analysis = coordinator.Analyze(settings, registry);
                }

                if (settings.ExportProject && (analysis.CanExport || analysis.SuggestedProjectVersion.HasValue))
                {
                    var root = SwAssemblyRoot.FromActiveDocument(_app, coordinator.MutationTracker);
                    var asset = Open(root, analysis);
                    var version = analysis.SuggestedProjectVersion ?? analysis.Plan.AssemblyVersion;
                    var edits = InterfaceEditRules.VersionChanges(asset.Snapshot, version, PropertyRules.AssemblyVersion);
                    if (edits.Count != 0)
                    {
                        asset.Save(edits);
                        saved.Add(asset.Path + " assembly_version=" + version);
                        RequireTarget(analysis);
                        analysis = coordinator.Analyze(settings, registry);
                    }
                }
                return analysis;
            }
            catch (Exception ex)
            {
                if (saved.Count == 0) throw;
                throw new ValidationException(ex.Message + "\n已保存版本：" + string.Join("\n", saved));
            }
        }

        private static IEnumerable<Tuple<InterfaceAsset, IDictionary<string, string>>> AssetEdits(AnalysisResult analysis)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in SwInstanceBindings.Flatten(analysis.Plan.Roots).Where(node => node.Kind == ExportNodeKind.Asset))
            {
                AssetInspection inspection;
                if (!analysis.AssetInspections.TryGetValue(node.AssetId, out inspection) || inspection.VersionDecision == null) continue;
                var version = SuggestedVersion(inspection) ?? inspection.Version;
                var source = (SwCadNode)node.Source;
                int previous;
                if (seen.TryGetValue(source.SourcePath, out previous))
                {
                    if (previous != version) throw new ValidationException("同一个源文件得到了不同的版本建议：" + source.SourcePath);
                    continue;
                }
                seen.Add(source.SourcePath, version);
                var properties = PropertyRules.Merge(source.Model);
                int persistedVersion;
                if (PropertyRules.TryReadPositiveInteger(properties, PropertyRules.AssetVersion, out persistedVersion) &&
                    version == persistedVersion) continue;
                var asset = Open(source, analysis);
                var edits = InterfaceEditRules.VersionChanges(asset.Snapshot, version);
                if (edits.Count != 0) yield return Tuple.Create(asset, (IDictionary<string, string>)edits);
            }
        }

        private static InterfaceAsset Open(SwCadNode source, AnalysisResult analysis)
        {
            var asset = new InterfaceAsset(source);
            ExportSourceFileSnapshot snapshot;
            if (!analysis.SourceFileSnapshots.TryGetValue(source.SourcePath, out snapshot))
                throw new ValidationException("无法验证版本变更的源文件：" + source.SourcePath);
            asset.SourceSnapshot = snapshot;
            asset.Reload();
            return asset;
        }
    }
}
