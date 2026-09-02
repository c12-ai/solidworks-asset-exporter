using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class SwPluginMutationTracker
    {
        private readonly SldWorks _app;
        private readonly IDictionary<string, int> _pluginDirtyStamps =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public SwPluginMutationTracker(SldWorks app)
        {
            _app = app;
        }

        public IDisposable BeginMutation(params ModelDoc2[] affectedDocuments)
        {
            return new MutationScope(this, SnapshotAffectedDocuments(affectedDocuments));
        }

        public bool IsDirty(ModelDoc2 document, bool openedHere, string sourcePath)
        {
            if (document == null) return false;
            bool dirty;
            try { dirty = document.GetSaveFlag(); }
            catch { return true; }
            var path = NormalizePath(document.GetPathName(), sourcePath);
            if (!dirty)
            {
                if (!string.IsNullOrWhiteSpace(path)) _pluginDirtyStamps.Remove(path);
                return false;
            }
            // A model loaded solely by this operation comes from its saved disk file. Embedded
            // IC~~/VC~~ documents are persisted by their owning assembly, not as standalone files.
            if (openedHere || AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path)) return false;
            int pluginStamp;
            return string.IsNullOrWhiteSpace(path) || !_pluginDirtyStamps.TryGetValue(path, out pluginStamp) ||
                SafeUpdateStamp(document) != pluginStamp;
        }

        private IDictionary<string, DocumentState> SnapshotAffectedDocuments(IEnumerable<ModelDoc2> affectedDocuments)
        {
            var result = new Dictionary<string, DocumentState>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in affectedDocuments ?? new ModelDoc2[0])
            {
                AddDocument(result, document);
            }
            AddActiveDocument(result);
            return result;
        }

        private static void AddDocument(IDictionary<string, DocumentState> result, ModelDoc2 document)
        {
            try
            {
                if (document == null) return;
                var path = NormalizePath(document.GetPathName(), null);
                if (string.IsNullOrWhiteSpace(path) || result.ContainsKey(path)) return;
                result[path] = new DocumentState
                {
                    Document = document,
                    Dirty = document.GetSaveFlag(),
                    UpdateStamp = SafeUpdateStamp(document)
                };
            }
            catch { }
        }

        private void AddActiveDocument(IDictionary<string, DocumentState> result)
        {
            try
            {
                AddDocument(result, _app == null ? null : _app.ActiveDoc as ModelDoc2);
            }
            catch { }
        }

        private void CompleteMutation(IDictionary<string, DocumentState> before)
        {
            var after = SnapshotAffectedDocuments(before.Values.Select(value => value.Document).ToArray());
            foreach (var pair in after)
            {
                var state = pair.Value;
                if (!state.Dirty)
                {
                    _pluginDirtyStamps.Remove(pair.Key);
                    continue;
                }
                DocumentState original;
                int knownPluginStamp;
                var originallyPluginOnly = before.TryGetValue(pair.Key, out original) && original.Dirty &&
                    _pluginDirtyStamps.TryGetValue(pair.Key, out knownPluginStamp) &&
                    original.UpdateStamp == knownPluginStamp;
                if ((original != null && !original.Dirty) || originallyPluginOnly ||
                    (original == null && AssetSourcePathPolicy.IsSessionEmbeddedModelPath(pair.Key)))
                    _pluginDirtyStamps[pair.Key] = state.UpdateStamp;
            }
        }

        private static int SafeUpdateStamp(ModelDoc2 document)
        {
            try { return document.GetUpdateStamp(); }
            catch { return int.MinValue; }
        }

        private static string NormalizePath(string primary, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try { return Path.GetFullPath(value); }
            catch { return value; }
        }

        private sealed class DocumentState
        {
            public ModelDoc2 Document { get; set; }
            public bool Dirty { get; set; }
            public int UpdateStamp { get; set; }
        }

        private sealed class MutationScope : IDisposable
        {
            private SwPluginMutationTracker _owner;
            private IDictionary<string, DocumentState> _before;

            public MutationScope(SwPluginMutationTracker owner, IDictionary<string, DocumentState> before)
            {
                _owner = owner;
                _before = before;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                var owner = _owner;
                var before = _before;
                _owner = null;
                _before = null;
                owner.CompleteMutation(before);
            }
        }
    }

    public sealed class SwModelDocumentLease : IDisposable
    {
        private readonly SldWorks _app;
        private readonly bool _openedHere;
        private readonly Component2 _resolvedComponent;
        private readonly int _originalSuppressionState;
        private readonly bool _restoreSuppressionState;
        private readonly IDisposable _mutationScope;

        private SwModelDocumentLease(SldWorks app, ModelDoc2 document, bool openedHere)
            : this(app, document, openedHere, null, 0, false, null)
        {
        }

        private SwModelDocumentLease(SldWorks app, ModelDoc2 document, bool openedHere,
            Component2 resolvedComponent, int originalSuppressionState, bool restoreSuppressionState,
            IDisposable mutationScope)
        {
            _app = app;
            Document = document;
            _openedHere = openedHere;
            _resolvedComponent = resolvedComponent;
            _originalSuppressionState = originalSuppressionState;
            _restoreSuppressionState = restoreSuppressionState;
            _mutationScope = mutationScope;
        }

        public ModelDoc2 Document { get; private set; }
        public bool OpenedHere { get { return _openedHere; } }

        public static SwModelDocumentLease Borrow(SldWorks app, ModelDoc2 document)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (document == null) throw new ArgumentNullException("document");
            return new SwModelDocumentLease(app, document, false);
        }

        public static SwModelDocumentLease Open(SldWorks app, string path, string configuration)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (string.IsNullOrWhiteSpace(path)) throw new ValidationException("模型路径为空，无法按需打开。");
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new ValidationException("模型文件不存在：" + path);

            var existing = app.GetOpenDocumentByName(path) as ModelDoc2;
            if (existing != null) return Borrow(app, existing);

            var type = DocumentType(path);
            var previousVisible = app.GetDocumentVisible(type);
            int errors = 0, warnings = 0;
            ModelDoc2 document = null;
            try
            {
                app.DocumentVisible(false, type);
                // Referenced components can remain loaded in the parent assembly after their standalone
                // window closes. Do not force ReadOnly, otherwise that retained in-memory document blocks edits.
                var options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
                if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    options |= (int)swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight |
                        (int)swOpenDocOptions_e.swOpenDocOptions_LoadLightweight |
                        (int)swOpenDocOptions_e.swOpenDocOptions_DontLoadHiddenComponents |
                        (int)swOpenDocOptions_e.swOpenDocOptions_LoadExternalReferencesInMemory;
                }
                document = app.OpenDoc6(path, type, options, configuration ?? string.Empty,
                    ref errors, ref warnings) as ModelDoc2;
            }
            finally
            {
                app.DocumentVisible(previousVisible, type);
            }

            if (document == null || errors != 0)
            {
                if (document != null) CloseOwnedDocument(app, document, path);
                throw new ValidationException("无法按需打开模型：" + path + "; errors=" + errors + ", warnings=" + warnings);
            }
            return new SwModelDocumentLease(app, document, true);
        }

        public static SwModelDocumentLease ResolveComponent(SldWorks app, Component2 component,
            SwPluginMutationTracker mutationTracker)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (component == null) throw new ArgumentNullException("component");
            var original = component.GetSuppression2();
            if (original == (int)swComponentSuppressionState_e.swComponentSuppressed)
                throw new ValidationException("不能读取已抑制的内嵌组件：" + component.Name2);

            var document = component.GetModelDoc2() as ModelDoc2;
            if (document != null) return Borrow(app, document);

            var mutation = mutationTracker == null ? null : mutationTracker.BeginMutation();
            try
            {
                var status = component.SetSuppression2((int)swComponentSuppressionState_e.swComponentResolved);
                document = component.GetModelDoc2() as ModelDoc2;
                if (document == null)
                {
                    try { component.SetSuppression2(original); } catch { }
                    throw new ValidationException("无法在父装配体内解析内嵌组件：" + component.Name2 +
                        "；status=" + status);
                }
                return new SwModelDocumentLease(app, document, false, component, original, true, mutation);
            }
            catch
            {
                if (mutation != null) mutation.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_restoreSuppressionState && _resolvedComponent != null)
            {
                try
                {
                    Document = null;
                    var status = _resolvedComponent.SetSuppression2(_originalSuppressionState);
                    var current = _resolvedComponent.GetSuppression2();
                    if (!EquivalentSuppressionState(_originalSuppressionState, current))
                        throw new ValidationException("无法恢复内嵌组件的轻化状态：" + _resolvedComponent.Name2 +
                            "；status=" + status + ", current=" + current);
                }
                finally
                {
                    if (_mutationScope != null) _mutationScope.Dispose();
                }
                return;
            }
            if (!_openedHere || Document == null) return;
            var document = Document;
            var path = document.GetPathName();
            Document = null;
            CloseOwnedDocument(_app, document, path);
        }

        private static bool EquivalentSuppressionState(int expected, int actual)
        {
            if (expected == actual) return true;
            var expectedLightweight = expected == (int)swComponentSuppressionState_e.swComponentLightweight ||
                expected == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
            var actualLightweight = actual == (int)swComponentSuppressionState_e.swComponentLightweight ||
                actual == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
            return expectedLightweight && actualLightweight;
        }

        private static int DocumentType(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                return (int)swDocumentTypes_e.swDocPART;
            if (string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase))
                return (int)swDocumentTypes_e.swDocASSEMBLY;
            if (string.Equals(extension, ".SLDDRW", StringComparison.OrdinalIgnoreCase))
                return (int)swDocumentTypes_e.swDocDRAWING;
            throw new ValidationException("不支持的 SOLIDWORKS 文档类型：" + path);
        }

        private static void CloseOwnedDocument(SldWorks app, ModelDoc2 document, string path)
        {
            var title = document.GetTitle();
            Exception closeFailure = null;
            try { app.CloseDoc(title); }
            catch (Exception ex) { closeFailure = ex; }

            var remaining = string.IsNullOrWhiteSpace(path) ? null : app.GetOpenDocumentByName(path) as ModelDoc2;
            if (remaining != null)
            {
                try { app.QuitDoc(title); }
                catch (Exception ex) { closeFailure = closeFailure ?? ex; }
                remaining = app.GetOpenDocumentByName(path) as ModelDoc2;
            }
            // GetOpenDocumentByName can keep returning a component document that the parent assembly
            // retains in memory. That is not an open standalone window and is safe if it is not read-only.
            if (remaining != null && (remaining.Visible || remaining.IsOpenedReadOnly()))
                throw new ValidationException("插件按需打开的模型窗口未能关闭，或模型仍处于只读状态：" + path +
                    (closeFailure == null ? string.Empty : "；" + closeFailure.Message));
        }
    }

    public sealed class SwReferencedConfigurationScope : IDisposable
    {
        private readonly ModelDoc2 _document;
        private readonly string _originalConfiguration;
        private readonly bool _changed;
        private readonly IDisposable _mutationScope;

        public SwReferencedConfigurationScope(ModelDoc2 document, string targetConfiguration)
            : this(document, targetConfiguration, null)
        {
        }

        public SwReferencedConfigurationScope(ModelDoc2 document, string targetConfiguration,
            SwPluginMutationTracker mutationTracker)
        {
            if (document == null) throw new ArgumentNullException("document");
            _document = document;
            var active = document.ConfigurationManager == null ? null : document.ConfigurationManager.ActiveConfiguration;
            _originalConfiguration = active == null ? string.Empty : active.Name;
            _changed = !string.IsNullOrWhiteSpace(targetConfiguration) &&
                !string.Equals(_originalConfiguration, targetConfiguration, StringComparison.OrdinalIgnoreCase);
            _mutationScope = _changed && mutationTracker != null ? mutationTracker.BeginMutation(document) : null;
            try
            {
                if (_changed && !document.ShowConfiguration2(targetConfiguration))
                    throw new ValidationException("无法切换到组件引用的配置：" + targetConfiguration + "；" + document.GetPathName());
            }
            catch
            {
                if (_mutationScope != null) _mutationScope.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_changed && !string.IsNullOrWhiteSpace(_originalConfiguration) &&
                    !_document.ShowConfiguration2(_originalConfiguration))
                    throw new ValidationException("无法恢复模型原配置：" + _originalConfiguration + "；" + _document.GetPathName());
            }
            finally
            {
                if (_mutationScope != null) _mutationScope.Dispose();
            }
        }
    }

    public sealed class SwActiveDocumentScope : IDisposable
    {
        private readonly SldWorks _app;
        private readonly string _originalActivationName;
        private readonly string _targetActivationName;
        private readonly string _targetTitle;
        private readonly bool _restoreOriginal;
        private readonly bool _closeTarget;

        public SwActiveDocumentScope(SldWorks app, ModelDoc2 target)
            : this(app, target, false)
        {
        }

        public SwActiveDocumentScope(SldWorks app, ModelDoc2 target, bool closeTargetIfInitiallyHidden)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (target == null) throw new ArgumentNullException("target");

            _app = app;
            var original = app.ActiveDoc as ModelDoc2;
            _originalActivationName = original == null ? string.Empty : ActivationName(original);
            _targetActivationName = ActivationName(target);
            _targetTitle = target.GetTitle();
            var sameDocument = IsSameDocument(original, target);
            _restoreOriginal = original != null && !sameDocument;
            _closeTarget = closeTargetIfInitiallyHidden && !sameDocument && !target.Visible;

            if (sameDocument) return;
            try
            {
                var activated = Activate(_targetActivationName);
                if (!IsSameDocument(activated, target))
                    throw new ValidationException("激活后的模型不是目标文档：" + _targetTitle);
            }
            catch
            {
                CleanupAfterFailedActivation();
                throw;
            }
        }

        public void Dispose()
        {
            Exception failure = null;
            if (_restoreOriginal && !string.IsNullOrWhiteSpace(_originalActivationName))
            {
                try { Activate(_originalActivationName); }
                catch (Exception ex) { failure = ex; }
            }

            if (_closeTarget)
            {
                try { _app.CloseDoc(_targetTitle); }
                catch (Exception ex) { failure = failure ?? ex; }
            }

            if (failure != null)
                throw new ValidationException("无法恢复 SOLIDWORKS 文档窗口状态：" + failure.Message);
        }

        private void CleanupAfterFailedActivation()
        {
            try
            {
                if (_restoreOriginal && !string.IsNullOrWhiteSpace(_originalActivationName))
                    Activate(_originalActivationName);
            }
            catch { }
            try
            {
                if (_closeTarget) _app.CloseDoc(_targetTitle);
            }
            catch { }
        }

        private ModelDoc2 Activate(string title)
        {
            int errors = 0;
            var document = _app.ActivateDoc3(title, false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
            var fatal = (errors & (int)swActivateDocError_e.swGenericActivateError) != 0;
            if (document == null || fatal)
                throw new ValidationException("无法无重建地激活模型文档：" + title + "; errors=" + errors);
            return document;
        }

        private static bool IsSameDocument(ModelDoc2 left, ModelDoc2 right)
        {
            if (left == null || right == null) return false;
            if (ReferenceEquals(left, right)) return true;
            var leftPath = left.GetPathName();
            var rightPath = right.GetPathName();
            if (!string.IsNullOrWhiteSpace(leftPath) && !string.IsNullOrWhiteSpace(rightPath))
                return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
            return string.Equals(left.GetTitle(), right.GetTitle(), StringComparison.OrdinalIgnoreCase);
        }

        private static string ActivationName(ModelDoc2 document)
        {
            // ActivateDoc3 expects the document name shown by SOLIDWORKS. Prefer the title;
            // full paths are retained only as a fallback for documents without a title.
            var title = document.GetTitle();
            return string.IsNullOrWhiteSpace(title) ? document.GetPathName() : title;
        }
    }
}
