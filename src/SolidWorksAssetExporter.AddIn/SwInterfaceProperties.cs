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
        public SwInterfacePropertyTarget(ModelDoc2 doc)
        { _doc = doc; _manager = (CustomPropertyManager)((dynamic)doc.Extension).CustomPropertyManager[""]; }
        public bool ReadOnly { get { return _doc.IsOpenedReadOnly(); } }
        public bool Dirty { get { return _doc.GetSaveFlag(); } }
        public InterfacePropertySnapshot Read()
        {
            var result = new InterfacePropertySnapshot();
            foreach (var name in (_manager.GetNames() as string[]) ?? new string[0])
            {
                string raw, resolved; bool wasResolved, linked;
                _manager.Get6(name, true, out raw, out resolved, out wasResolved, out linked);
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
    public sealed class InterfaceAsset
    {
        public BindingEndpoint Endpoint;
        public SwCadNode Source;
        public InterfacePropertySnapshot Snapshot;
        public int InstanceCount;
        public readonly List<BindingEndpoint> Children = new List<BindingEndpoint>();
        public string Path { get { return Endpoint.SourcePath; } }
        public override string ToString() { return System.IO.Path.GetFileName(Path) + "（所选 " + InstanceCount + " 实例）"; }
        public void Reload()
        { using (var lease = Source.OpenDocument()) Snapshot = new SwInterfacePropertyTarget(lease.Document).Read(); }
        public string Save(IDictionary<string, string> changes)
        {
            if (changes.Count == 0) return "无接口变更，未写入文件。";
            using (var lease = Source.OpenDocument())
            {
                var doc = lease.Document;
                if (!string.Equals(doc.GetPathName(), Path, StringComparison.OrdinalIgnoreCase) || Source.Component.IsVirtual)
                    throw new ValidationException("源文件身份不符或为内嵌组件，请重新选择外部 Asset。");
                var target = new SwInterfacePropertyTarget(doc);
                if (target.ReadOnly || target.Dirty) throw new ValidationException("源文件只读或有未保存修改，请先处理该文件后重新打开接口编辑窗口。");
                InterfaceEditRules.RequireUnchanged(Snapshot, target.Read());
                var backupDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidWorksAssetExporter", "PropertyBackups", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backupDir); var backup = System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(Path)); File.Copy(Path, backup, false);
                try { InterfacePropertyWriter.Apply(target, Snapshot, changes); Snapshot = target.Read(); }
                catch (Exception ex)
                {
                    lease.RetainForEditing(); doc.Visible = true;
                    throw new ValidationException(ex.Message + "\n原文件备份：" + backup + "\n源文件已保留打开，请检查后保存。");
                }
                return "已保存 " + System.IO.Path.GetFileName(Path) + "；影响该文件的所有实例。请保存总装配并重新预览。";
            }
        }
    }
}
