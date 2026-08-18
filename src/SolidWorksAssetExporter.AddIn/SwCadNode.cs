using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class SwCadNode : ICadNode
    {
        private readonly Component2 _component;
        private readonly SldWorks _application;
        private readonly bool _isTraversalRoot;
        private ModelDescriptor _model;

        public SwCadNode(SldWorks application, Component2 component, string instancePath)
            : this(application, component, instancePath, false, null)
        {
        }

        internal SwCadNode(SldWorks application, Component2 component, string instancePath,
            bool isTraversalRoot, string traversalRootName)
        {
            _application = application; _component = component;
            _isTraversalRoot = isTraversalRoot;
            Name = isTraversalRoot ? traversalRootName : (component == null ? "<root>" : component.Name2);
            InstanceId = isTraversalRoot || component == null ? "root" : component.GetID().ToString();
            InstancePath = instancePath ?? "/";
        }

        public Component2 Component { get { return _component; } }
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

        public string InstanceId { get; private set; }
        public string Name { get; private set; }
        public string InstancePath { get; private set; }
        public bool IsVisible { get { return _isTraversalRoot || _component == null || !_component.IsHidden(false); } }
        public bool IsSuppressed { get { return !_isTraversalRoot && _component != null && _component.IsSuppressed(); } }
        public bool IsEnvelope { get { return !_isTraversalRoot && _component != null && _component.IsEnvelope(); } }
        public bool IsFixed { get { return !_isTraversalRoot && _component != null && _component.IsFixed(); } }
        public ModelDescriptor Model { get { return _model ?? (_model = ReadModel()); } }

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
                if (child != null) yield return new SwCadNode(_application, child, InstancePath + "/" + child.Name2);
            }
        }

        private ModelDescriptor ReadModel()
        {
            var document = Document;
            var path = document.GetPathName();
            var activeConfiguration = document.ConfigurationManager.ActiveConfiguration;
            var configuration = activeConfiguration.Name;
            var displayState = ReadDisplayState(activeConfiguration);
            var descriptor = new ModelDescriptor
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                InternalCreationTime = NormalizeCreationTime(Convert.ToString(document.get_SummaryInfo((int)swSummInfoField_e.swSumInfoCreateDate))),
                Configuration = configuration ?? string.Empty,
                DisplayState = displayState,
                DocumentKind = ToDocumentKind(document.GetType()),
                IsSaved = !string.IsNullOrWhiteSpace(path) && File.Exists(path),
                IsDirty = document.GetSaveFlag(),
                FileProperties = ReadProperties(((dynamic)document.Extension).CustomPropertyManager[string.Empty]),
                ConfigurationProperties = ReadProperties(((dynamic)document.Extension).CustomPropertyManager[configuration ?? string.Empty])
            };
            return descriptor;
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

        private static IDictionary<string, string> ReadProperties(CustomPropertyManager manager)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = manager == null ? null : manager.GetNames() as string[];
            if (names == null) return result;
            foreach (var name in names)
            {
                string raw, resolved; bool wasResolved, linked;
                manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
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
            var document = application.ActiveDoc as ModelDoc2;
            if (document == null || document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new ValidationException("请先打开一个 SOLIDWORKS 装配体。");
            var configuration = document.ConfigurationManager.ActiveConfiguration;
            var component = configuration.GetRootComponent3(true);
            if (component == null) throw new ValidationException("活动配置没有可用的根组件。");
            var rootName = Path.GetFileNameWithoutExtension(document.GetPathName());
            if (string.IsNullOrWhiteSpace(rootName))
                rootName = Path.GetFileNameWithoutExtension(document.GetTitle());
            if (string.IsNullOrWhiteSpace(rootName)) rootName = "<root>";
            return new SwCadNode(application, component, "/" + rootName, true, rootName);
        }
    }
}
