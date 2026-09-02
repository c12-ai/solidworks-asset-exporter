using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class SwCadNode : ICadNode, ICadSourceReference, ICadClassificationSource
    {
        private static readonly string[] ClassificationPropertyNames =
            { PropertyRules.IsAsset, PropertyRules.AssetVersion, PropertyRules.AssemblyVersion };
        private readonly Component2 _component;
        private readonly SldWorks _application;
        private readonly bool _isTraversalRoot;
        private readonly IDictionary<string, ModelDescriptor> _modelCache;
        private readonly SwPluginMutationTracker _mutationTracker;
        private ModelDescriptor _model;
        private ModelDescriptor _classificationModel;

        public SwCadNode(SldWorks application, Component2 component, string instancePath)
            : this(application, component, instancePath, false, null,
                new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase),
                new SwPluginMutationTracker(application))
        {
        }

        internal SwCadNode(SldWorks application, Component2 component, string instancePath,
            bool isTraversalRoot, string traversalRootName)
            : this(application, component, instancePath, isTraversalRoot, traversalRootName,
                new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase),
                new SwPluginMutationTracker(application))
        {
        }

        internal SwCadNode(SldWorks application, Component2 component, string instancePath,
            bool isTraversalRoot, string traversalRootName, SwPluginMutationTracker mutationTracker)
            : this(application, component, instancePath, isTraversalRoot, traversalRootName,
                new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase), mutationTracker)
        {
        }

        private SwCadNode(SldWorks application, Component2 component, string instancePath,
            bool isTraversalRoot, string traversalRootName, IDictionary<string, ModelDescriptor> modelCache,
            SwPluginMutationTracker mutationTracker)
        {
            _application = application; _component = component;
            _isTraversalRoot = isTraversalRoot;
            _modelCache = modelCache;
            _mutationTracker = mutationTracker ?? new SwPluginMutationTracker(application);
            Name = isTraversalRoot ? traversalRootName : (component == null ? "<root>" : component.Name2);
            InstanceId = isTraversalRoot || component == null ? "root" : component.GetID().ToString();
            InstancePath = instancePath ?? "/";
        }

        public Component2 Component { get { return _component; } }
        public string ReferencedConfiguration
        {
            get
            {
                if (_isTraversalRoot || _component == null)
                {
                    var document = _application.ActiveDoc as ModelDoc2;
                    var configuration = document == null || document.ConfigurationManager == null
                        ? null : document.ConfigurationManager.ActiveConfiguration;
                    return configuration == null ? string.Empty : configuration.Name;
                }
                try { return Convert.ToString(((dynamic)_component).ReferencedConfiguration) ?? string.Empty; }
                catch { return string.Empty; }
            }
        }
        public ModelDoc2 Document
        {
            get
            {
                var document = _isTraversalRoot || _component == null
                    ? _application.ActiveDoc as ModelDoc2
                    : _component.GetModelDoc2() as ModelDoc2;
                if (document == null) throw new ValidationException("组件 [" + Name + "] 的模型未解析或未加载。");
                return document;
            }
        }

        public SwModelDocumentLease OpenDocument()
        {
            if (_application == null) throw new ValidationException("没有可用的 SOLIDWORKS 应用程序实例。");
            if (_isTraversalRoot || _component == null)
            {
                var active = _application.ActiveDoc as ModelDoc2;
                if (active == null) throw new ValidationException("当前没有活动的 SOLIDWORKS 模型文档。");
                return SwModelDocumentLease.Borrow(_application, active);
            }
            var componentPath = _component.GetPathName();
            var loaded = _component.GetModelDoc2() as ModelDoc2;
            if (IsEmbeddedSessionComponent(_component, componentPath))
                return SwModelDocumentLease.ResolveComponent(_application, _component, _mutationTracker);
            // A resolved/flexible component can occasionally expose its owning assembly through
            // GetModelDoc2. Never borrow that document for an external component: Pack and Go on
            // it would enumerate the entire parent assembly instead of this component boundary.
            if (loaded != null && DocumentMatchesPath(loaded, componentPath))
                return SwModelDocumentLease.Borrow(_application, loaded);
            return SwModelDocumentLease.Open(_application, componentPath, ReferencedConfiguration);
        }

        private static bool DocumentMatchesPath(ModelDoc2 document, string expectedPath)
        {
            if (document == null || string.IsNullOrWhiteSpace(expectedPath)) return false;
            try
            {
                var actualPath = document.GetPathName();
                return !string.IsNullOrWhiteSpace(actualPath) && string.Equals(
                    Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsEmbeddedSessionComponent(Component2 component, string path)
        {
            try { if (component != null && component.IsVirtual) return true; }
            catch { }
            return AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path);
        }

        public string InstanceId { get; private set; }
        public string Name { get; private set; }
        public string InstancePath { get; private set; }
        public bool IsVisible { get { return _isTraversalRoot || _component == null || SwComponentState.IsVisible(_component); } }
        public bool IsSuppressed { get { return !_isTraversalRoot && _component != null && SwComponentState.IsSuppressed(_component); } }
        public bool IsEnvelope { get { return !_isTraversalRoot && _component != null && _component.IsEnvelope(); } }
        public bool IsFixed { get { return !_isTraversalRoot && _component != null && _component.IsFixed(); } }
        public ModelDescriptor Model { get { return _model ?? (_model = ReadModel()); } }
        public ModelDescriptor ClassificationModel
        {
            get { return _classificationModel ?? (_classificationModel = ReadClassificationModel()); }
        }
        public string SourcePath
        {
            get
            {
                if (!_isTraversalRoot && _component != null) return _component.GetPathName() ?? string.Empty;
                var document = _application == null ? null : _application.ActiveDoc as ModelDoc2;
                return document == null ? string.Empty : document.GetPathName();
            }
        }
        public DocumentKind SourceDocumentKind
        {
            get
            {
                var extension = Path.GetExtension(SourcePath);
                if (string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase)) return DocumentKind.Part;
                if (string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase)) return DocumentKind.Assembly;
                if (string.Equals(extension, ".SLDDRW", StringComparison.OrdinalIgnoreCase)) return DocumentKind.Drawing;
                return DocumentKind.Unknown;
            }
        }

        public Matrix4 WorldTransform
        {
            get
            {
                if (_isTraversalRoot || _component == null || _component.Transform2 == null) return Matrix4.Identity;
                var values = (double[])_component.Transform2.ArrayData;
                if (values == null || values.Length < 13) throw new ValidationException("组件变换矩阵格式无效: " + Name);
                var scale = values[12];
                if (Math.Abs(scale - 1d) > 1e-9) throw new ValidationException("组件包含非单位缩放，无法用刚体位姿表达: " + Name);
                var determinant = values[0] * (values[4] * values[8] - values[5] * values[7])
                    - values[1] * (values[3] * values[8] - values[5] * values[6])
                    + values[2] * (values[3] * values[7] - values[4] * values[6]);
                if (Math.Abs(determinant - 1d) > 1e-6)
                    throw new ValidationException("组件变换包含镜像或非刚体旋转，无法用四元数表达: " + Name);
                return new Matrix4(new[]
                {
                    values[0], values[3], values[6], values[9],
                    values[1], values[4], values[7], values[10],
                    values[2], values[5], values[8], values[11],
                    0d, 0d, 0d, 1d
                });
            }
        }

        public IEnumerable<ICadNode> GetChildren()
        {
            if (_component == null) yield break;
            var children = _component.GetChildren() as object[];
            if (children == null) yield break;
            foreach (var value in children)
            {
                var child = value as Component2;
                if (child != null) yield return new SwCadNode(_application, child, InstancePath + "/" + child.Name2,
                    false, null, _modelCache, _mutationTracker);
            }
        }

        private ModelDescriptor ReadModel()
        {
            var requestedPath = _isTraversalRoot || _component == null
                ? ((_application.ActiveDoc as ModelDoc2) == null ? string.Empty : ((ModelDoc2)_application.ActiveDoc).GetPathName())
                : _component.GetPathName();
            var requestedConfiguration = ReferencedConfiguration;
            var requestedDisplayState = ReferencedDisplayState();
            var cacheKey = Canonical.Join(requestedPath, requestedConfiguration, requestedDisplayState);
            ModelDescriptor cached;
            if (_modelCache.TryGetValue(cacheKey, out cached)) return cached;

            using (var lease = OpenDocument())
            {
                var document = lease.Document;
                var path = document.GetPathName();
                var activeConfiguration = document.ConfigurationManager == null ? null : document.ConfigurationManager.ActiveConfiguration;
                var configuration = string.IsNullOrWhiteSpace(requestedConfiguration)
                    ? (activeConfiguration == null ? string.Empty : activeConfiguration.Name)
                    : requestedConfiguration;
                var displayState = string.IsNullOrWhiteSpace(requestedDisplayState)
                    ? ReadDisplayState(activeConfiguration) : requestedDisplayState;
                var descriptor = new ModelDescriptor
                {
                    FullPath = path,
                    FileName = Path.GetFileName(path),
                    InternalCreationTime = NormalizeCreationTime(Convert.ToString(document.get_SummaryInfo((int)swSummInfoField_e.swSumInfoCreateDate))),
                    Configuration = configuration ?? string.Empty,
                    DisplayState = displayState,
                    DocumentKind = ToDocumentKind(document.GetType()),
                    IsSaved = !string.IsNullOrWhiteSpace(path) && File.Exists(path),
                    IsDirty = _mutationTracker.IsDirty(document, lease.OpenedHere, path),
                    FileProperties = ReadNodeProperties(document, string.Empty),
                    ConfigurationProperties = ReadNodeProperties(document, configuration ?? string.Empty)
                };
                _modelCache[cacheKey] = descriptor;
                return descriptor;
            }
        }

        private ModelDescriptor ReadClassificationModel()
        {
            var path = SourcePath;
            var requestedConfiguration = ReferencedConfiguration;
            var displayState = ReferencedDisplayState();
            var cacheKey = "classification|" + Canonical.Join(path, requestedConfiguration);
            ModelDescriptor cached;
            if (_modelCache.TryGetValue(cacheKey, out cached)) return cached;

            // IComponent2.CustomPropertyManager only exposes configuration-specific properties.
            // The Asset flag used by the property-tab template is a file-level property, so it
            // must be read through the referenced model's ModelDocExtension. Lightweight models
            // are opened invisibly one at a time and immediately closed by this lease.
            using (var lease = OpenDocument())
            {
                var document = lease.Document;
                var activeConfiguration = document.ConfigurationManager == null
                    ? null : document.ConfigurationManager.ActiveConfiguration;
                var configuration = string.IsNullOrWhiteSpace(requestedConfiguration)
                    ? (activeConfiguration == null ? string.Empty : activeConfiguration.Name)
                    : requestedConfiguration;
                var descriptor = new ModelDescriptor
                {
                    FullPath = path,
                    FileName = Path.GetFileName(path),
                    InternalCreationTime = NormalizeCreationTime(Convert.ToString(
                        document.get_SummaryInfo((int)swSummInfoField_e.swSumInfoCreateDate))),
                    Configuration = configuration ?? string.Empty,
                    DisplayState = displayState,
                    DocumentKind = SourceDocumentKind,
                    IsSaved = !string.IsNullOrWhiteSpace(path) && File.Exists(path),
                    IsDirty = _mutationTracker.IsDirty(document, lease.OpenedHere, path),
                    FileProperties = ReadClassificationProperties(document, string.Empty, true),
                    ConfigurationProperties = ReadClassificationProperties(document, configuration ?? string.Empty, true)
                };
                _modelCache[cacheKey] = descriptor;
                return descriptor;
            }
        }

        private IDictionary<string, string> ReadClassificationProperties(
            ModelDoc2 document, string configuration, bool useCached)
        {
            if (document == null)
                throw new ValidationException("组件没有可用于分类的模型引用：" + Name);
            return ReadClassificationPropertyValues(
                ((dynamic)document.Extension).CustomPropertyManager[configuration], useCached);
        }

        private string ReferencedDisplayState()
        {
            if (_isTraversalRoot || _component == null) return string.Empty;
            try { return Convert.ToString(((dynamic)_component).ReferencedDisplayState) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private IDictionary<string, string> ReadNodeProperties(ModelDoc2 document, string configuration)
        {
            if (document == null)
                throw new ValidationException("组件没有可用于读取属性的模型引用：" + Name);
            // Component2.CustomPropertyManager is configuration-specific and cannot represent
            // file-level properties. Use the model document for both scopes. Cached reads avoid
            // activating other configurations and marking virtual component documents dirty.
            return ReadProperties(
                ((dynamic)document.Extension).CustomPropertyManager[configuration], true);
        }

        private static string ReadDisplayState(Configuration configuration)
        {
            try
            {
                var names = configuration == null ? null : configuration.GetDisplayStates() as string[];
                return names == null || names.Length == 0 ? string.Empty : names[0];
            }
            catch { return string.Empty; }
        }

        private static string NormalizeCreationTime(string raw)
        {
            DateTime value;
            if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value) ||
                DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value))
                return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            return (raw ?? string.Empty).Trim();
        }

        private static IDictionary<string, string> ReadClassificationPropertyValues(
            CustomPropertyManager manager, bool useCached)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manager == null) return result;
            foreach (var name in ClassificationPropertyNames)
            {
                string raw, resolved; bool wasResolved, linked;
                var status = manager.Get6(name, useCached, out raw, out resolved, out wasResolved, out linked);
                if (status == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent) continue;
                result.Add(name, wasResolved && !string.IsNullOrEmpty(resolved) ? resolved : (raw ?? string.Empty));
            }
            return result;
        }

        private static IDictionary<string, string> ReadProperties(
            CustomPropertyManager manager, bool useCached)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = manager == null ? null : manager.GetNames() as string[];
            if (names == null) return result;
            foreach (var name in names)
            {
                string raw, resolved; bool wasResolved, linked;
                manager.Get6(name, useCached, out raw, out resolved, out wasResolved, out linked);
                result.Add(name, wasResolved && !string.IsNullOrEmpty(resolved) ? resolved : (raw ?? string.Empty));
            }
            return result;
        }

        private static DocumentKind ToDocumentKind(int type)
        {
            if (type == (int)swDocumentTypes_e.swDocPART) return DocumentKind.Part;
            if (type == (int)swDocumentTypes_e.swDocASSEMBLY) return DocumentKind.Assembly;
            if (type == (int)swDocumentTypes_e.swDocDRAWING) return DocumentKind.Drawing;
            return DocumentKind.Unknown;
        }
    }

    public static class SwAssemblyRoot
    {
        public static SwCadNode FromActiveDocument(SldWorks application)
        {
            return FromActiveDocument(application, new SwPluginMutationTracker(application));
        }

        public static SwCadNode FromActiveDocument(SldWorks application, SwPluginMutationTracker mutationTracker)
        {
            var document = application.ActiveDoc as ModelDoc2;
            if (document == null || document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new ValidationException("请先打开一个 SOLIDWORKS 装配体。");
            var configuration = document.ConfigurationManager.ActiveConfiguration;
            var component = configuration.GetRootComponent3(false);
            if (component == null) throw new ValidationException("活动配置没有可用的根组件。");
            var rootName = Path.GetFileNameWithoutExtension(document.GetPathName());
            if (string.IsNullOrWhiteSpace(rootName))
                rootName = Path.GetFileNameWithoutExtension(document.GetTitle());
            if (string.IsNullOrWhiteSpace(rootName)) rootName = "<root>";
            return new SwCadNode(application, component, "/" + rootName, true, rootName, mutationTracker);
        }

    }

    internal static class SwComponentState
    {
        public static bool IsVisible(Component2 component)
        {
            return component != null && component.Visible == (int)swComponentVisibilityState_e.swComponentVisible;
        }

        public static bool IsSuppressed(Component2 component)
        {
            if (component == null) return false;
            var state = component.GetSuppression2();
            if (state == (int)swComponentSuppressionState_e.swComponentInternalIdMismatch)
                throw new ValidationException("组件内部 ID 不匹配，无法判断抑制状态：" + component.Name2);
            return state == (int)swComponentSuppressionState_e.swComponentSuppressed;
        }
    }
}
