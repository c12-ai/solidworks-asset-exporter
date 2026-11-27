using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class SwInterfacePropertyTarget : IInterfacePropertyTarget
    {
        private readonly ModelDoc2 _doc;
        private readonly CustomPropertyManager _manager;
        private readonly Func<bool> _dirty;
        public SwInterfacePropertyTarget(ModelDoc2 doc) : this(doc, doc.GetSaveFlag) { }
        internal SwInterfacePropertyTarget(ModelDoc2 doc, Func<bool> dirty)
        {
            _doc = doc;
            _dirty = dirty;
            _manager = (CustomPropertyManager)((dynamic)doc.Extension).CustomPropertyManager[""];
        }
        public bool ReadOnly { get { return _doc.IsOpenedReadOnly(); } }
        public bool Dirty { get { return _dirty(); } }
        public InterfacePropertySnapshot Read()
        {
            var result = new InterfacePropertySnapshot();
            foreach (var name in (_manager.GetNames() as string[]) ?? new string[0])
            {
                if (name.Equals(PropertyRules.LegacyCanContain, StringComparison.OrdinalIgnoreCase)) continue;
                string raw, resolved; bool wasResolved, linked;
                _manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
                result.Raw[name] = raw ?? ""; result.Values[name] = wasResolved && !string.IsNullOrEmpty(resolved) ? resolved : raw ?? "";
                result.Types[name] = _manager.GetType2(name);
                if (linked || (wasResolved && raw != resolved) || (raw ?? "").IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0 || (raw ?? "").StartsWith("=")) result.Linked.Add(name);
            }
            return result;
        }
        public void Set(string name, string value, int type)
        { if (_manager.Add3(name, type, value, 2) != 0) throw new ValidationException("无法写入属性：" + name); _doc.SetSaveFlag(); }
        public void Delete(string name)
        { if (((_manager.GetNames() as string[]) ?? new string[0]).Contains(name, StringComparer.OrdinalIgnoreCase) && _manager.Delete2(name) != 0) throw new ValidationException("无法恢复属性：" + name); }
        public void Save()
        { int e = 0, w = 0; if (!_doc.Save3(1, ref e, ref w) || e != 0 || _doc.GetSaveFlag()) throw new ValidationException("SW 保存失败，错误码 " + e + "，警告码 " + w); }
    }
    internal sealed class InterfaceAsset
    {
        private readonly SwCadNode _source;
        private readonly string _backupRoot;
        internal InterfacePropertySnapshot Snapshot { get; private set; }
        internal ExportSourceFileSnapshot SourceSnapshot;
        internal string Path { get; private set; }

        internal InterfaceAsset(SwCadNode source, string backupRoot = null)
        {
            _source = source;
            _backupRoot = backupRoot ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksAssetExporter", "PropertyBackups");
            Path = source.SourcePath;
        }

        internal void Reload(InterfacePropertySnapshot expected = null)
        {
            var sourceSnapshot = SourceSnapshot ?? ExportSourceFileSnapshot.Capture(Path, null);
            sourceSnapshot.ValidateUnchanged();
            InterfacePropertySnapshot properties;
            using (var lease = _source.OpenDocument()) properties = ReadTarget(lease.Document).Read();
            sourceSnapshot.ValidateUnchanged();
            if (expected != null) InterfaceEditRules.RequireUnchanged(expected, properties);
            Snapshot = properties;
            SourceSnapshot = sourceSnapshot;
        }

        private SwInterfacePropertyTarget ReadTarget(ModelDoc2 doc)
        {
            if (!string.Equals(doc.GetPathName(), Path, StringComparison.OrdinalIgnoreCase) ||
                (_source.Component != null && _source.Component.IsVirtual))
                throw new ValidationException("源文件身份不符或为内嵌组件，请重新选择外部 Asset。");
            return new SwInterfacePropertyTarget(doc, () => _source.HasUserChanges(doc));
        }

        internal void ValidateForSave(IDictionary<string, string> changes)
        {
            if (changes.Count == 0) return;
            SourceSnapshot.ValidateUnchanged();
            using (var lease = _source.OpenDocument())
                ValidateTarget(ReadTarget(lease.Document), changes);
        }

        private void ValidateTarget(SwInterfacePropertyTarget target, IDictionary<string, string> changes)
        {
            if (target.ReadOnly || target.Dirty) throw new ValidationException("源文件只读或有未保存修改：" + Path);
            InterfaceEditRules.RequireUnchanged(Snapshot, target.Read());
            if (changes.Keys.Any(Snapshot.Linked.Contains)) throw new ValidationException("待写入字段包含链接或表达式：" + Path);
            SourceSnapshot.ValidateUnchanged();
        }

        internal void Save(IDictionary<string, string> changes, IEnumerable<string> allowedFields = null)
        {
            if (changes.Count == 0) return;
            SourceSnapshot.ValidateUnchanged();
            using (var lease = _source.OpenDocument())
            {
                var doc = lease.Document;
                var target = ReadTarget(doc);
                ValidateTarget(target, changes);
                var backupDir = System.IO.Path.Combine(_backupRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backupDir); var backup = System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(Path)); File.Copy(Path, backup, false);
                SourceSnapshot.ValidateUnchanged();
                try
                {
                    InterfacePropertyWriter.Apply(target, Snapshot, changes, allowedFields ?? InterfaceEditRules.Fields);
                    Snapshot = target.Read();
                    SourceSnapshot = null;
                }
                catch (Exception ex)
                {
                    lease.RetainForEditing(); doc.Visible = true;
                    throw new ValidationException(ex.Message + "\n原文件备份：" + backup + "\n源文件已保留打开，请检查后保存。");
                }
            }
        }
    }
}
