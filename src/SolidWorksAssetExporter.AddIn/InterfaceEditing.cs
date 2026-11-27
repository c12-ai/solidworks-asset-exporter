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
        public int Version { get { return PropertyRules.ReadManagedVersion(Values, PropertyRules.AssetVersion); } }
    }
    public static class InterfaceEditRules
    {
        public static readonly string[] Fields = { PropertyRules.ConnectionInterface, PropertyRules.AcceptsInterfaces,
            PropertyRules.SlotsNum, PropertyRules.SlotType, PropertyRules.AssetEngagement,
            PropertyRules.AssetVersion, PropertyRules.AssemblyVersion };
        public static List<string> Split(string value)
        { return (value ?? "").Split(';', '；').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList(); }
        public static string Name(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.IndexOfAny(new[] { ';', '；', '\r', '\n' }) >= 0 || value.Any(char.IsControl))
                throw new ValidationException("每个接口需要一个非空名称，不能含分号或换行。");
            return value;
        }
        public static string MechanicalName(string value)
        {
            var name = Name(value);
            if (PropertyRules.IsContentsInterface(name))
                throw new ValidationException("[" + name + "] 是内容物，请在容器资产上填写 " + PropertyRules.CanContain + "。");
            return name;
        }
        public static List<string> Suggest(string existing, IEnumerable<string> childConnections)
        {
            var result = PropertyRules.MechanicalInterfaceNames(existing);
            foreach (var raw in childConnections)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = MechanicalName(raw);
                var collision = result.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase) && s != name);
                if (collision != null) throw new ValidationException("接口仅大小写不同：" + collision + " / " + name + "。请先统一父子接口名称。");
                if (!result.Contains(name, StringComparer.Ordinal)) result.Add(name);
            }
            return result;
        }
        internal static string BootstrapConnection(InterfacePropertySnapshot child, string confirmedInterface, string instance)
        {
            var existing = PropertyRules.MechanicalConnectionInterface(child.Get(PropertyRules.ConnectionInterface));
            if (existing.Length == 0)
            {
                if (confirmedInterface == null)
                    throw new ValidationException("子组件 [" + instance + "] 缺少接口，请先根据设计依据确认 connection_interface。");
                return MechanicalName(confirmedInterface);
            }
            if (confirmedInterface != null && !existing.Equals(confirmedInterface, StringComparison.Ordinal))
                throw new ValidationException("子组件 [" + instance + "] 已声明接口 [" + existing +
                    "]，请按现有接口分组，或通过 asset_update_interfaces 显式更改。");
            return MechanicalName(existing);
        }
        internal static int? NextVersionIfPublished(IEnumerable<AssetRegistration> registrations, string uuid, int version)
        {
            var versions = registrations.Where(asset => string.Equals(asset.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.Version).ToList();
            if (!versions.Contains(version)) return null;
            var latest = versions.Max();
            if (latest == int.MaxValue) throw new ValidationException("当前版本已发布，Wanxiang 版本号已达到上限。");
            return latest + 1;
        }

        public static Dictionary<string, string> Changes(InterfacePropertySnapshot snapshot, string connection, IEnumerable<string> accepts, int version, bool unpublished)
        {
            return Changes(snapshot, connection ?? "", accepts, null, version, unpublished);
        }

        internal static Dictionary<string, string> Changes(InterfacePropertySnapshot snapshot, string connection,
            IEnumerable<string> accepts, int? slots, int version, bool unpublished)
        {
            return Changes(snapshot, connection, accepts, slots, null, null, version, unpublished);
        }

        internal static Dictionary<string, string> Changes(InterfacePropertySnapshot snapshot, string connection,
            IEnumerable<string> accepts, int? slots, string slotType, string engagement, int version, bool unpublished)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (connection != null)
            {
                var proposed = string.IsNullOrWhiteSpace(connection) ? "" : MechanicalName(connection);
                if (snapshot.Get(PropertyRules.ConnectionInterface).Trim() != proposed)
                    result.Add(PropertyRules.ConnectionInterface, proposed);
            }
            if (accepts != null)
            {
                var proposed = string.Join(";", accepts.Select(MechanicalName).Distinct(StringComparer.Ordinal));
                if (snapshot.Get(PropertyRules.AcceptsInterfaces).Trim() != proposed)
                    result.Add(PropertyRules.AcceptsInterfaces, proposed);
            }
            if (slots.HasValue)
            {
                if (slots.Value < 0) throw new ValidationException("slots_num 必须为非负整数。");
                var capacity = slots.Value.ToString(CultureInfo.InvariantCulture);
                if (snapshot.Get(PropertyRules.SlotsNum).Trim() != capacity) result.Add(PropertyRules.SlotsNum, capacity);
                if (slots.Value == 0 && snapshot.Get(PropertyRules.SlotType).Trim().Length != 0)
                    result.Add(PropertyRules.SlotType, "");
            }
            if (slotType != null || engagement != null)
            {
                if (!PropertyRules.ReadIsAsset(snapshot.Values) || PropertyRules.IsRobotClass(snapshot.Values))
                    throw new ValidationException("请选择装配中的普通 Asset 实例来编辑槽位类型或接合方式。");
            }
            if (slotType != null)
            {
                var effectiveCapacity = slots ?? PropertyRules.ReadSlotCapacity(snapshot.Values);
                if (effectiveCapacity.GetValueOrDefault() <= 0)
                    throw new ValidationException("总槽位容量大于 0 时才可设置槽位类型。");
                if (!PropertyRules.AssetSlotTypes.Contains(slotType, StringComparer.Ordinal))
                    throw new ValidationException("请从下拉列表中选择槽位类型。");
                if (snapshot.Get(PropertyRules.SlotType).Trim() != slotType) result.Add(PropertyRules.SlotType, slotType);
            }
            if (engagement != null)
            {
                if (!PropertyRules.AssetEngagementValues.Contains(engagement, StringComparer.Ordinal))
                    throw new ValidationException("请从下拉列表中选择接合方式。");
                if (snapshot.Get(PropertyRules.AssetEngagement).Trim() != engagement) result.Add(PropertyRules.AssetEngagement, engagement);
            }
            if (result.Count > 0)
            {
                var proposed = new Dictionary<string, string>(snapshot.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var change in result) proposed[change.Key] = change.Value;
                var issues = PropertyRules.ValidateAssetConnectionProperties(proposed);
                if (issues.Count > 0) throw new ValidationException(string.Join("\n", issues));
            }
            AddVersionChange(snapshot, version, unpublished, result);
            return result;
        }

        internal static Dictionary<string, string> VersionChanges(InterfacePropertySnapshot snapshot, int version,
            string field = PropertyRules.AssetVersion)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (version <= 0) throw new ValidationException(field + " 必须是正整数。");
            int persistedVersion;
            if (!PropertyRules.TryReadPositiveInteger(snapshot.Values, field, out persistedVersion) || persistedVersion != version)
                result.Add(field, version.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        private static void AddVersionChange(InterfacePropertySnapshot snapshot, int version, bool unpublished,
            IDictionary<string, string> result)
        {
            var currentVersion = snapshot.Version;
            if (result.Count == 0 && version == currentVersion) return;
            if (version <= 0) throw new ValidationException("asset_version 必须是正整数。");
            if (!unpublished && version <= currentVersion)
                throw new ValidationException("当前资产版本已发布或发布状态不确定，请提升 asset_version。");
            int persistedVersion;
            if (!PropertyRules.TryReadPositiveInteger(snapshot.Values, PropertyRules.AssetVersion, out persistedVersion) ||
                version != persistedVersion)
                result.Add(PropertyRules.AssetVersion, version.ToString(CultureInfo.InvariantCulture));
            foreach (var key in result.Keys) if (snapshot.Linked.Contains(key)) throw new ValidationException(key + " 使用链接或表达式，请先在 SW 中解除链接后编辑。");
        }
        public static void RequireUnchanged(InterfacePropertySnapshot expected, InterfacePropertySnapshot current)
        {
            // Role changes are relevant too: do not apply an old proposal to a newly classified source.
            if (expected.Raw.Count != current.Raw.Count || expected.Raw.Any(p => !current.Raw.ContainsKey(p.Key) || current.Raw[p.Key] != p.Value) ||
                expected.Types.Any(p => !current.Types.ContainsKey(p.Key) || current.Types[p.Key] != p.Value) ||
                !expected.Linked.SetEquals(current.Linked) || Fields.Any(name => expected.Get(name) != current.Get(name)))
                throw new ValidationException("资产属性在编辑期间发生变化，请重新读取文件属性。");
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
            Apply(target, expected, changes, InterfaceEditRules.Fields);
        }

        internal static void Apply(IInterfacePropertyTarget target, InterfacePropertySnapshot expected,
            IDictionary<string, string> changes, IEnumerable<string> allowedFields)
        {
            if (changes.Count == 0) return;
            if (target.ReadOnly) throw new ValidationException("该源文件为只读，不能修改属性。");
            if (target.Dirty) throw new ValidationException("该源文件已有未保存修改，请先在 SW 保存该文件。");
            var current = target.Read(); InterfaceEditRules.RequireUnchanged(expected, current);
            if (changes.Keys.Any(k => !allowedFields.Contains(k))) throw new ValidationException("包含插件不维护的属性。");
            if (changes.Keys.Any(current.Linked.Contains)) throw new ValidationException("插件维护的字段包含链接或表达式，请先解除链接。");
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
                throw new ValidationException("属性保存未完成：" + ex.Message + (failures.Count == 0 ? "\n已恢复编辑前的内存属性，文件仍需检查并保存。" : "\n部分内存属性无法恢复：" + string.Join("; ", failures)));
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
        { return LoadStored().Where(name => !PropertyRules.IsContentsInterface(name)).ToList(); }

        private List<string> LoadStored()
        {
            if (!File.Exists(FilePath)) return new List<string>();
            try { var data = JsonFile.Read<InterfaceCatalogData>(FilePath);
                if (data.Schema != "1.0" || data.Names == null) throw new InvalidDataException("格式版本无效。");
                return data.Names.Select(InterfaceEditRules.Name).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(); }
            catch (Exception ex) { throw new ValidationException("接口库无法读取（原文件未覆盖）：" + ex.Message); }
        }
        public void Merge(IEnumerable<string> names)
        {
            var requested = names.Select(InterfaceEditRules.MechanicalName).ToArray();
            var directory = Path.GetDirectoryName(FilePath); Directory.CreateDirectory(directory);
            // Do not overwrite another SW process's catalogue update.
            using (var gate = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var all = LoadStored().Concat(requested).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { JsonFile.Write(temp, new InterfaceCatalogData { Names = all }); if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
