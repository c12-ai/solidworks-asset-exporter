using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class InterfacePropertySnapshot
    {
        public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Get(string name) { string value; return Values.TryGetValue(name, out value) ? value : ""; }
        public int Version { get { return PropertyRules.RequirePositiveInteger(Values, PropertyRules.AssetVersion, "所选资产"); } }
    }
    public static class InterfaceEditRules
    {
        public static readonly string[] Fields = { PropertyRules.ConnectionInterface, PropertyRules.AcceptsInterfaces, PropertyRules.AssetVersion };
        public static List<string> Split(string value)
        { return (value ?? "").Split(';', '；').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList(); }
        public static string Name(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.IndexOfAny(new[] { ';', '；', '\r', '\n' }) >= 0 || value.Any(char.IsControl))
                throw new ValidationException("每个接口需要一个非空名称，不能含分号或换行。");
            return value;
        }
        public static List<string> Suggest(string existing, IEnumerable<string> childConnections)
        {
            var result = Split(existing);
            foreach (var raw in childConnections)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = Name(raw);
                var collision = result.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase) && s != name);
                if (collision != null) throw new ValidationException("接口仅大小写不同：" + collision + " / " + name + "。请先统一父子接口名称。");
                if (!result.Contains(name, StringComparer.Ordinal)) result.Add(name);
            }
            return result;
        }
        public static Dictionary<string, string> Changes(InterfacePropertySnapshot snapshot, string connection, IEnumerable<string> accepts, int version, bool unpublished)
        {
            connection = string.IsNullOrWhiteSpace(connection) ? "" : Name(connection);
            var names = accepts.Select(Name).Distinct(StringComparer.Ordinal).ToList();
            var proposed = new Dictionary<string, string>(snapshot.Values, StringComparer.OrdinalIgnoreCase);
            proposed[PropertyRules.ConnectionInterface] = connection; proposed[PropertyRules.AcceptsInterfaces] = string.Join(";", names);
            var issues = PropertyRules.ValidateAssetConnectionProperties(proposed);
            if (issues.Count > 0) throw new ValidationException(string.Join("\n", issues));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in Fields.Take(2)) if (snapshot.Get(key) != proposed[key]) result.Add(key, proposed[key]);
            if (result.Count == 0) return result; // Opening/suggesting/cancelling never bumps versions.
            if (version < snapshot.Version || (!unpublished && version <= snapshot.Version))
                throw new ValidationException("已发布或发布状态不确定的资产，修改接口后必须提升 asset_version。未发布资产也不能降版。");
            if (version != snapshot.Version) result.Add(PropertyRules.AssetVersion, version.ToString(CultureInfo.InvariantCulture));
            foreach (var key in result.Keys) if (snapshot.Linked.Contains(key)) throw new ValidationException(key + " 使用链接或表达式，请先在 SW 中解除链接后编辑。");
            return result;
        }
        public static void RequireUnchanged(InterfacePropertySnapshot expected, InterfacePropertySnapshot current)
        {
            // Role changes are relevant too: do not apply an old proposal to a newly classified source.
            if (expected.Raw.Count != current.Raw.Count || expected.Raw.Any(p => !current.Raw.ContainsKey(p.Key) || current.Raw[p.Key] != p.Value) ||
                expected.Types.Any(p => !current.Types.ContainsKey(p.Key) || current.Types[p.Key] != p.Value) ||
                !expected.Linked.SetEquals(current.Linked)) throw new ValidationException("资产属性在编辑期间发生变化，请关闭编辑窗口后重新读取。");
        }
    }
    public interface IInterfacePropertyTarget
    {
        bool ReadOnly { get; }
        bool Dirty { get; }
        InterfacePropertySnapshot Read();
        void Set(string name, string value, int type);
        void Delete(string name);
        void Save();
    }
    public static class InterfacePropertyWriter
    {
        public static void Apply(IInterfacePropertyTarget target, InterfacePropertySnapshot expected, IDictionary<string, string> changes)
        {
            if (changes.Count == 0) return;
            if (target.ReadOnly) throw new ValidationException("该源文件为只读，不能修改接口属性。");
            if (target.Dirty) throw new ValidationException("该源文件已有未保存修改，请先在 SW 保存该文件，再重新打开接口编辑窗口。");
            var current = target.Read(); InterfaceEditRules.RequireUnchanged(expected, current);
            if (changes.Keys.Any(k => !InterfaceEditRules.Fields.Contains(k))) throw new ValidationException("仅支持修改接口和资产版本。");
            var attempted = new List<string>();
            try
            {
                foreach (var change in changes) { attempted.Add(change.Key); target.Set(change.Key, change.Value, current.Types.ContainsKey(change.Key) ? current.Types[change.Key] : 30); }
                var written = target.Read();
                if (changes.Any(p => written.Get(p.Key) != p.Value)) throw new ValidationException("SW 属性写入后校验不一致。");
                target.Save();
            }
            catch (Exception ex)
            {
                var failures = new List<string>();
                foreach (var key in attempted.AsEnumerable().Reverse())
                    try { if (current.Raw.ContainsKey(key)) target.Set(key, current.Raw[key], current.Types[key]); else target.Delete(key); }
                    catch (Exception restore) { failures.Add(key + ": " + restore.Message); }
                throw new ValidationException("接口保存未完成：" + ex.Message + (failures.Count == 0 ? "\n已恢复编辑前的内存属性，文件仍需检查并保存。" : "\n部分内存属性无法恢复：" + string.Join("; ", failures)));
            }
        }
    }
    [DataContract]
    public sealed class InterfaceCatalogData
    {
        [DataMember(Name = "schema_version")] public string Schema = "1.0";
        [DataMember(Name = "interfaces")] public List<string> Names = new List<string>();
    }
    public sealed class InterfaceCatalogStore
    {
        public readonly string FilePath;
        public InterfaceCatalogStore(string path = null)
        { FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SolidWorksAssetExporter", "interfaces.json"); }
        public List<string> Load()
        {
            if (!File.Exists(FilePath)) return new List<string>();
            try { var data = JsonFile.Read<InterfaceCatalogData>(FilePath);
                if (data.Schema != "1.0" || data.Names == null) throw new InvalidDataException("格式版本无效。");
                return data.Names.Select(InterfaceEditRules.Name).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(); }
            catch (Exception ex) { throw new ValidationException("接口库无法读取（原文件未覆盖）：" + ex.Message); }
        }
        public void Merge(IEnumerable<string> names)
        {
            var directory = Path.GetDirectoryName(FilePath); Directory.CreateDirectory(directory);
            // Do not overwrite another SW process's catalogue update.
            using (var gate = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var all = Load().Concat(names.Select(InterfaceEditRules.Name)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { JsonFile.Write(temp, new InterfaceCatalogData { Names = all }); if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
