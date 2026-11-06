using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    // One owner per open top-level document, shared by the editor and every exporter dialog.
    public sealed class SwInstanceBindings : IDisposable
    {
        private const string StreamName = "SWAssetExporter.Bindings.v1";
        private static readonly List<SwInstanceBindings> Sessions = new List<SwInstanceBindings>();
        private readonly ModelDoc2 _document;
        private readonly DAssemblyDocEvents_Event _events;
        private BindingDocument _data = new BindingDocument();
        private long _revision, _savedRevision, _writtenRevision = -1;
        private bool _disposed;
        private bool _storageUnconfirmed;
        private AssemblyExportPlan _indexedPlan;
        private Dictionary<string, ExportNode> _nodeIndex;
        public string Error { get; private set; }
        public bool Pending { get { return _revision != _savedRevision || _storageUnconfirmed; } }
        public long Revision { get { return _revision; } }

        private SwInstanceBindings(ModelDoc2 document)
        {
            _document = document;
            _events = document as DAssemblyDocEvents_Event;
            if (_events == null) throw new ValidationException("当前 SOLIDWORKS 不支持所需的装配保存事件，不能编辑承载关系。");
            ReadStorage();
            _events.SaveToStorageNotify += SaveStorage;
            _events.FileSavePostNotify += Saved;
            _events.FileSavePostCancelNotify += SaveCancelled;
            _events.DestroyNotify2 += Destroyed;
        }

        public static SwInstanceBindings ForDocument(ModelDoc2 document)
        {
            if (document == null || document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return null;
            var existing = Sessions.FirstOrDefault(s => !s._disposed && Equals(s._document, document));
            if (existing != null) return existing;
            var session = new SwInstanceBindings(document); Sessions.Add(session); return session;
        }
        public static bool HasPending(ModelDoc2 document)
        { return Sessions.Any(s => !s._disposed && Equals(s._document, document) && (s.Pending || s.Error != null)); }
        public static void CloseAll()
        { foreach (var s in Sessions.ToArray()) s.Dispose(); }

        public ConfigurationBindings Current
        {
            get
            {
                EnsureReadable();
                var c = _document.ConfigurationManager.ActiveConfiguration;
                if (c == null) throw new ValidationException("没有活动配置。");
                return _data.ForConfiguration(c.GetID(), c.Name);
            }
        }
        public void RequireWritable()
        {
            EnsureReadable();
            if (_document.IsOpenedReadOnly()) throw new ValidationException("当前装配为只读，不能写入承载关系。");
        }
        private void EnsureReadable()
        {
            if (_disposed) throw new ValidationException("装配已关闭，请重新打开承载关系面板。");
            if (Error != null) throw new ValidationException(Error);
        }
        public void RequireSaved()
        {
            EnsureReadable();
            if (Pending) throw new ValidationException("承载关系已应用，待保存装配；请保存成功后重新预览。");
        }
        public void Commit(List<InstanceBinding> records)
        {
            RequireWritable();
            var current = Current;
            if (current.Relations.Select(r => r.Id).SequenceEqual(records.Select(r => r.Id))) return;
            // SetSaveFlag is outside every internal model-resolution mutation exemption.
            _document.SetSaveFlag();
            _data.SchemaVersion = BindingDocument.CurrentSchemaVersion;
            if (!_data.Configurations.Contains(current)) _data.Configurations.Add(current);
            current.ConfigurationName = _document.ConfigurationManager.ActiveConfiguration.Name;
            current.Relations = records;
            _revision++;
        }

        public BindingEndpoint Capture(ExportNode node)
        {
            InstanceBindingRules.RequireAsset(node);
            var sw = node.Source as SwCadNode;
            if (sw == null || sw.Component == null) throw new ValidationException("请选择当前装配中的组件实例。");
            var bytes = _document.Extension.GetPersistReference3(sw.Component) as byte[];
            if (bytes == null || bytes.Length == 0) throw new ValidationException("无法取得实例持久引用：" + node.Name);
            var endpoint = new BindingEndpoint
            {
                Reference = Convert.ToBase64String(bytes), Context = Context(sw.Component),
                SourceIdentity = IdentityService.AssetIdentitySeed(sw.Model), SourcePath = sw.SourcePath, DisplayName = node.Name,
                DisplayPath = sw.InstancePath, AssetId = node.AssetId
            };
            // Never accept a persist reference that does not round-trip in this top-level context.
            var resolved = ResolveComponent(endpoint);
            if (Context(resolved) != endpoint.Context) throw new ValidationException("持久引用未返回所选实例上下文。");
            return endpoint;
        }

        public static string Context(Component2 component)
        {
            var ids = new List<string>();
            while (component != null)
            {
                var id = component.GetID();
                // Depth, rather than names or paths, disambiguates repeated nested subassemblies.
                ids.Add(id.ToString(CultureInfo.InvariantCulture));
                if (ids.Count > 1024) throw new ValidationException("组件上下文过深或包含循环。");
                component = component.GetParent() as Component2;
            }
            ids.Reverse(); return string.Join("/", ids);
        }
        public Component2 ResolveComponent(BindingEndpoint endpoint)
        {
            if (endpoint == null || string.IsNullOrEmpty(endpoint.Reference)) throw new ValidationException("引用失效：缺少组件持久引用。");
            try
            {
                int error;
                var component = _document.Extension.GetObjectByPersistReference3(Convert.FromBase64String(endpoint.Reference), out error) as Component2;
                if (component == null || error != 0 || Context(component) != endpoint.Context)
                    throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "]，不能确认仍为原组件实例。");
                return component;
            }
            catch (ValidationException) { throw; }
            catch (Exception ex) { throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "]，" + ex.Message); }
        }
        public ExportNode Resolve(BindingEndpoint endpoint, AssemblyExportPlan plan)
        {
            var component = ResolveComponent(endpoint);
            var context = Context(component);
            if (!ReferenceEquals(_indexedPlan, plan))
            {
                _nodeIndex = new Dictionary<string, ExportNode>(StringComparer.Ordinal);
                foreach (var n in Flatten(plan.Roots).Where(n => n.Source is SwCadNode && n.Source.InstanceId != "root"))
                {
                    var key = Context(((SwCadNode)n.Source).Component);
                    if (_nodeIndex.ContainsKey(key)) throw new ValidationException("当前装配组件上下文不唯一，不能解析承载关系。");
                    _nodeIndex.Add(key, n);
                }
                _indexedPlan = plan;
            }
            ExportNode node; _nodeIndex.TryGetValue(context, out node);
            InstanceBindingRules.RequireAsset(node);
            if (IdentityService.AssetIdentitySeed(node.Source.Model) != endpoint.SourceIdentity ||
                !string.Equals(((SwCadNode)node.Source).SourcePath, endpoint.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "] 的源模型已被替换，请解除后重新绑定。");
            // Use fresh file-level business metadata, not the classification-only subset.
            node.Properties = PropertyRules.Merge(node.Source.Model);
            return node;
        }
        public AssemblyRelations Export(AssemblyExportPlan plan)
        { EnsureReadable(); return InstanceBindingRules.Resolve(Current.Relations, e => Resolve(e, plan), plan); }
        public static IEnumerable<ExportNode> Flatten(IEnumerable<ExportNode> roots)
        {
            foreach (var n in roots) { yield return n; foreach (var c in Flatten(n.Children)) yield return c; }
        }

        private void ReadStorage()
        {
            object raw = null;
            try
            {
                raw = _document.IGet3rdPartyStorage(StreamName, false);
                if (raw == null) return;
                var stream = (IStream)raw;
                System.Runtime.InteropServices.ComTypes.STATSTG stat; stream.Stat(out stat, 1);
                if (stat.cbSize <= 0 || stat.cbSize > 16 * 1024 * 1024) throw new InvalidDataException("存储长度无效。");
                var bytes = new byte[(int)stat.cbSize];
                var count = Marshal.AllocCoTaskMem(4);
                try
                {
                    stream.Seek(0, 0, IntPtr.Zero); stream.Read(bytes, bytes.Length, count);
                    if (Marshal.ReadInt32(count) != bytes.Length) throw new EndOfStreamException();
                }
                finally { Marshal.FreeCoTaskMem(count); }
                using (var memory = new MemoryStream(bytes)) _data = (BindingDocument)new DataContractSerializer(typeof(BindingDocument)).ReadObject(memory);
                _data.ForConfiguration(int.MinValue, "validation");
            }
            catch (Exception ex) { Error = "承载关系存储无法读取，禁止忽略已有记录：" + ex.Message; }
            finally
            {
                if (raw != null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
                _document.IRelease3rdPartyStorage(StreamName);
            }
        }
        private int SaveStorage()
        {
            _writtenRevision = -1;
            if (Error != null) return 1;
            if (_data.Configurations.Count == 0) { _writtenRevision = _revision; return 0; }
            object raw = null;
            try
            {
                byte[] bytes;
                using (var memory = new MemoryStream())
                { new DataContractSerializer(typeof(BindingDocument)).WriteObject(memory, _data); bytes = memory.ToArray(); }
                raw = _document.IGet3rdPartyStorage(StreamName, true);
                if (raw == null) throw new IOException("SOLIDWORKS 未提供第三方存储流。");
                var stream = (IStream)raw; stream.SetSize(bytes.Length); stream.Seek(0, 0, IntPtr.Zero);
                var count = Marshal.AllocCoTaskMem(4);
                try { stream.Write(bytes, bytes.Length, count); if (Marshal.ReadInt32(count) != bytes.Length) throw new IOException("关系写入不完整。"); }
                finally { Marshal.FreeCoTaskMem(count); }
                _writtenRevision = _revision;
                return 0;
            }
            catch (Exception ex)
            {
                // Keep the revision pending; a successful model save alone is insufficient.
                _storageUnconfirmed = true;
                LastSaveError = "承载关系保存失败，请重试：" + ex.Message;
                return 1;
            }
            finally
            {
                if (raw != null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
                _document.IRelease3rdPartyStorage(StreamName);
            }
        }
        public string LastSaveError { get; private set; }
        private int Saved(int saveType, string fileName)
        {
            // Saving a copy does not persist the source document's pending edits.
            if (_writtenRevision == _revision && Error == null && (saveType == 1 || saveType == 2))
            { _savedRevision = _revision; _storageUnconfirmed = false; LastSaveError = null; }
            else if (Pending) _document.SetSaveFlag();
            _writtenRevision = -1;
            return 0;
        }
        private int SaveCancelled()
        { _writtenRevision = -1; if (Pending) _document.SetSaveFlag(); return 0; }
        private int Destroyed(int type) { Dispose(); return 0; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Sessions.Remove(this);
            try
            {
                _events.SaveToStorageNotify -= SaveStorage; _events.FileSavePostNotify -= Saved;
                _events.FileSavePostCancelNotify -= SaveCancelled; _events.DestroyNotify2 -= Destroyed;
            }
            catch (COMException) { }
        }
    }
}
