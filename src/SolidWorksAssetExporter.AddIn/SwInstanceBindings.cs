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
        private const int CurrentComponentConfiguration = 0; // swAddComponentConfigOptions_CurrentSelectedConfig
        private const int SpecifiedConfiguration = 3; // swSpecifyConfiguration
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
        internal IEnumerable<ConfigurationBindings> StoredConfigurations
        {
            get { EnsureReadable(); return _data.Configurations.AsReadOnly(); }
        }

        internal int RequiredSlotCapacity(string sourcePath, IEnumerable<InstanceBinding> activeRelations)
        {
            var activeId = Current.ConfigurationId;
            return StoredConfigurations.Where(config => config.ConfigurationId != activeId).Select(config => config.Relations.AsEnumerable())
                .Concat(new[] { activeRelations }).SelectMany(records => records
                    .Where(record => string.Equals(record.Parent.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(record => StoredEndpointKey(record.Parent))
                    .Select(CountBoundSlots))
                .DefaultIfEmpty().Max();
        }

        internal static int CountBoundSlots(IEnumerable<InstanceBinding> records)
        {
            return records.Select(record => record.SlotId == null ? "binding:" + record.Id : "slot:" + record.SlotId)
                .Distinct(StringComparer.Ordinal).Count();
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

        internal void Save(bool silent)
        {
            RequireWritable();
            int error = 0, warning = 0;
            if (!_document.Save3(silent ? 1 : 0, ref error, ref warning) || error != 0)
                throw new ValidationException("总装配保存未完成，错误码：" + error + "。" + LastSaveError);
            RequireSaved();
        }
        public void Commit(List<InstanceBinding> records)
        {
            RequireWritable();
            records = DistinctStoredPairs(records);
            var current = Current;
            InstanceBindingRules.RejectNewMultipleParents(current.Relations, records);
            SetRelations(current, records);
        }

        private void SetRelations(ConfigurationBindings current, List<InstanceBinding> records)
        {
            if (SameRelations(current.Relations, records)) return;
            // SetSaveFlag is outside every internal model-resolution mutation exemption.
            _document.SetSaveFlag();
            if (!_data.Configurations.Contains(current)) _data.Configurations.Add(current);
            current.ConfigurationName = _document.ConfigurationManager.ActiveConfiguration.Name;
            current.Relations = records;
            var all = _data.Configurations.SelectMany(configuration => configuration.Relations);
            _data.SchemaVersion = all.Any(relation => relation.SlotType != null || relation.Engagement != null)
                ? BindingDocument.CouplingSchemaVersion
                : all.Any(relation => relation.SlotId != null) ? BindingDocument.SharedSlotSchemaVersion : BindingDocument.CurrentSchemaVersion;
            _revision++;
        }

        private static bool SameRelations(IList<InstanceBinding> first, IList<InstanceBinding> second)
        {
            return first.Count == second.Count && first.Zip(second, (left, right) => left.Id == right.Id &&
                SameEndpoint(left.Parent, right.Parent) && SameEndpoint(left.Child, right.Child) && left.SlotId == right.SlotId &&
                left.SlotType == right.SlotType && left.Engagement == right.Engagement &&
                SamePose(left.SlotPose, right.SlotPose) && SamePose(left.SlotOffset, right.SlotOffset)).All(same => same);
        }

        private static bool SameEndpoint(BindingEndpoint left, BindingEndpoint right)
        {
            return StoredEndpointKey(left).Equals(StoredEndpointKey(right)) && left.DisplayName == right.DisplayName &&
                left.DisplayPath == right.DisplayPath && left.AssetId == right.AssetId;
        }

        private static bool SamePose(RelationPose left, RelationPose right)
        {
            return left == null ? right == null : right != null && left.CanonicalValue() == right.CanonicalValue();
        }

        internal sealed class DuplicatePlan
        {
            internal BindingEndpoint Parent, Original;
            internal ExportSourceFileSnapshot Assembly, ParentSource, ChildSource;
            internal int ConfigurationId, UpdateStamp, OccupiedSlots, RequiredSlots;
            internal int? Capacity;
            internal long Revision;
            internal string Configuration, SourceConfiguration, ReplacedId, ReuseSlotId;
            internal string[] OtherConfigurations;
            internal List<InstanceBinding> Before;
            internal SortedDictionary<string, double[]> Poses;
        }

        internal sealed class DuplicateResult
        {
            internal InstanceBinding Relation;
            internal string BackupPath;
        }

        internal DuplicatePlan PrepareDuplicate(SldWorks app, BindingEndpoint parent, BindingEndpoint original, string reuseSlotId)
        {
            var stage = "核对总装编辑目标";
            try { return PrepareDuplicate(app, parent, original, reuseSlotId, ref stage); }
            catch (COMException ex) { throw DuplicateComError(stage, ex); }
        }

        private DuplicatePlan PrepareDuplicate(SldWorks app, BindingEndpoint parent, BindingEndpoint original, string reuseSlotId, ref string stage)
        {
            RequireDuplicateContext(app);
            var current = Current;
            var stamp = _document.GetUpdateStamp();
            stage = "读取端点和源配置";
            var sourceComponent = ResolveComponent(original);
            var childSource = RequireDuplicateSource(sourceComponent, original.SourcePath);
            var parentSource = RequireDuplicateSource(ResolveComponent(parent), parent.SourcePath);
            var operation = new SwBindingEndpoints(app, this);
            var sourceNode = operation.Resolve(original);
            var parentNode = operation.Resolve(parent);
            var sourceConfiguration = sourceComponent.ReferencedConfiguration;
            if (string.IsNullOrWhiteSpace(sourceConfiguration))
                throw new ValidationException("无法确定将复制的实例所引用的源配置。");
            var preview = new BindingEndpoint { Context = "preview:" + original.Context, SourceIdentity = original.SourceIdentity,
                SourcePath = original.SourcePath, DisplayName = original.DisplayName };
            var previewNode = new ExportNode { Id = "preview:" + sourceNode.Id, Source = sourceNode.Source, Kind = sourceNode.Kind,
                Name = sourceNode.Name, AssetId = sourceNode.AssetId, Properties = sourceNode.Properties };
            stage = "校验现有和拟迁移的承载关系";
            var proposed = InstanceBindingRules.BindDuplicate(current.Relations, parent, original, preview,
                endpoint => ReferenceEquals(endpoint, preview) ? previewNode : operation.Resolve(endpoint), reuseSlotId);
            string capacityText;
            int slots;
            var capacity = parentNode.Properties.TryGetValue(PropertyRules.SlotsNum, out capacityText) &&
                int.TryParse((capacityText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out slots) ? (int?)slots : null;
            var proposedId = proposed.Single(record => ReferenceEquals(record.Child, preview)).Id;
            stage = "读取总装配置";
            var otherConfigurations = (_document.GetConfigurationNames() as Array ?? new string[0]).Cast<string>()
                .Where(name => name != current.ConfigurationName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            stage = "读取待复制实例及其子组件的原始位姿";
            var plan = new DuplicatePlan
            {
                Parent = parent, Original = original, Configuration = current.ConfigurationName, ConfigurationId = current.ConfigurationId,
                Revision = Revision, UpdateStamp = stamp, SourceConfiguration = sourceConfiguration,
                Assembly = ExportSourceFileSnapshot.Capture(_document.GetPathName(), FileHash.Sha256),
                ParentSource = parentSource, ChildSource = childSource, Before = current.Relations.ToList(),
                ReplacedId = current.Relations.Any(record => record.Id == proposedId) ? proposedId : null, ReuseSlotId = reuseSlotId,
                Capacity = capacity, RequiredSlots = RequiredSlotCapacity(parent.SourcePath, proposed),
                OccupiedSlots = CountBoundSlots(proposed.Where(record => operation.Resolve(record.Parent).Id == parentNode.Id)),
                OtherConfigurations = otherConfigurations,
                Poses = CaptureDuplicatePoses(sourceComponent)
            };
            stage = "复核预检时的装配和端点";
            ValidateDuplicatePlan(app, plan);
            return plan;
        }

        internal static ValidationException DuplicateComError(string stage, COMException error)
        {
            return new ValidationException("原位复制 [" + stage + "] 失败，HRESULT 0x" + error.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) + "。" + error.Message);
        }

        internal DuplicateResult ApplyDuplicate(SldWorks app, DuplicatePlan plan, string backupRoot = null)
        {
            ValidateDuplicatePlan(app, plan);
            var directory = Path.Combine(backupRoot ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksAssetExporter", "PropertyBackups"), "duplicate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backup = Path.Combine(directory, Path.GetFileName(plan.Assembly.Path));
            File.Copy(plan.Assembly.Path, backup, false);
            if (FileHash.Sha256(backup) != plan.Assembly.Sha256)
                throw new ValidationException("总装备份与计划的原件不一致，请重新生成计划。");
            ValidateDuplicatePlan(app, plan);

            var source = ResolveComponent(plan.Original);
            var assembly = (AssemblyDoc)_document;
            var existing = new HashSet<int>(TopLevelComponents(assembly).Select(component => component.GetID()));
            var otherBindings = StoredConfigurations.Where(configuration => configuration.ConfigurationId != plan.ConfigurationId).ToArray();
            Component2 clone = null;
            try
            {
                clone = assembly.AddComponent5(plan.Original.SourcePath, CurrentComponentConfiguration, string.Empty, true,
                    plan.SourceConfiguration, 0d, 0d, 0d);
                if (clone == null || existing.Contains(clone.GetID()))
                    throw new ValidationException("SOLIDWORKS 没有返回独立的总装复制实例。");
                clone.ReferencedConfiguration = plan.SourceConfiguration;
                clone.Transform2 = source.Transform2;
                SuppressDuplicateOutsidePlan(assembly, clone, plan.OtherConfigurations);
                if (!_document.EditRebuild3()) throw new ValidationException("原位复制后的总装重建失败。");

                RequireSameDuplicatePoses(plan.Poses, ResolveComponent(plan.Original));
                RequireSameDuplicatePoses(plan.Poses, clone);
                if (!string.Equals(clone.GetPathName(), plan.Original.SourcePath, StringComparison.OrdinalIgnoreCase) ||
                    clone.ReferencedConfiguration != plan.SourceConfiguration)
                    throw new ValidationException("复制实例的源文件或引用配置与计划不一致。");
                var operation = new SwBindingEndpoints(app, this);
                var cloneNode = operation.FindSelectedAsset(clone);
                if (cloneNode.Source.Name != clone.Name2) throw new ValidationException("复制实例没有独立 Asset 边界。");
                var duplicate = Capture(cloneNode);
                var proposed = InstanceBindingRules.BindDuplicate(plan.Before, plan.Parent, plan.Original, duplicate, operation.Resolve, plan.ReuseSlotId);
                if (RequiredSlotCapacity(plan.Parent.SourcePath, proposed) != plan.RequiredSlots)
                    throw new ValidationException("槽位占用已经变化，请重新生成原位复制计划。");
                if (!Equals(app.ActiveDoc, _document) || Current.ConfigurationId != plan.ConfigurationId || Revision != plan.Revision)
                    throw new ValidationException("原位复制期间装配配置或关系已改变。");
                ValidateDuplicateFile(plan.ParentSource);
                ValidateDuplicateFile(plan.ChildSource);
                ValidateDuplicateFile(plan.Assembly);
                var relation = proposed.Single(record => ReferenceEquals(record.Child, duplicate));
                Commit(proposed);
                Save(true);
                if (FileHash.Sha256(plan.Assembly.Path) == plan.Assembly.Sha256)
                    throw new ValidationException("保存后总装文件没有更新。");
                var readback = ReadBindingStorage();
                if (!SameRelations(proposed, readback.ForConfiguration(plan.ConfigurationId, plan.Configuration).Relations) ||
                    !SameConfigurations(otherBindings, readback.Configurations.Where(item => item.ConfigurationId != plan.ConfigurationId)))
                    throw new ValidationException("保存后的承载关系与原位复制计划不一致。");
                RequireSameDuplicatePoses(plan.Poses, ResolveComponent(plan.Original));
                var savedClone = ResolveComponent(duplicate);
                RequireSameDuplicatePoses(plan.Poses, savedClone);
                RequireSuppressedDuplicateOutsidePlan(savedClone, plan.OtherConfigurations);
                ValidateDuplicateFile(plan.ParentSource);
                ValidateDuplicateFile(plan.ChildSource);
                return new DuplicateResult { Relation = relation, BackupPath = backup };
            }
            catch (Exception failure)
            {
                var outcome = "请用备份恢复总装，并重新核对实例和关系。";
                try
                {
                    if (Equals(app.ActiveDoc, _document) && Current.ConfigurationId == plan.ConfigurationId &&
                        FileHash.Sha256(plan.Assembly.Path) == plan.Assembly.Sha256)
                    {
                        var additions = TopLevelComponents(assembly).Where(component => !existing.Contains(component.GetID())).ToArray();
                        if (additions.Length == 1 && (clone == null || additions[0].GetID() == clone.GetID()) &&
                            string.Equals(additions[0].GetPathName(), plan.Original.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            _document.ClearSelection2(true);
                            try
                            {
                                if (!additions[0].Select4(false, null, false) || !_document.Extension.DeleteSelection2(0))
                                    throw new ValidationException("无法撤销复制实例。");
                            }
                            finally { _document.ClearSelection2(true); }
                        }
                        if (!existing.SetEquals(TopLevelComponents(assembly).Select(component => component.GetID())))
                            throw new ValidationException("无法确认复制前的总装根组件集合已恢复。");
                        SetRelations(Current, plan.Before);
                        outcome = "已恢复装配实例和承载关系。可重新打开磁盘总装以清除未保存状态。";
                    }
                }
                catch (Exception rollback) { outcome += " " + rollback.Message; }
                throw new ValidationException(failure.Message + "\n" + outcome + "\n总装备份：" + backup);
            }
        }

        private void RequireDuplicateContext(SldWorks app)
        {
            RequireWritable();
            RequireSaved();
            if (!Equals(app.ActiveDoc, _document) || _document.GetSaveFlag() || string.IsNullOrWhiteSpace(_document.GetPathName()))
                throw new ValidationException("请激活并保存总装配后再原位复制实例。");
            if (!_document.IsEditingSelf())
                throw new ValidationException("请退出子组件编辑后再原位复制到总装配。");
        }

        private void ValidateDuplicatePlan(SldWorks app, DuplicatePlan plan)
        {
            RequireDuplicateContext(app);
            if (Current.ConfigurationId != plan.ConfigurationId || Revision != plan.Revision || _document.GetUpdateStamp() != plan.UpdateStamp ||
                !SameRelations(Current.Relations, plan.Before))
                throw new ValidationException("装配或承载关系已变化，请重新生成原位复制计划。");
            ValidateDuplicateFile(plan.Assembly);
            ValidateDuplicateFile(plan.ParentSource);
            ValidateDuplicateFile(plan.ChildSource);
            var source = ResolveComponent(plan.Original);
            RequireCleanDuplicateSource(source, plan.ChildSource.Path);
            RequireCleanDuplicateSource(ResolveComponent(plan.Parent), plan.ParentSource.Path);
            if (source.ReferencedConfiguration != plan.SourceConfiguration)
                throw new ValidationException("原实例的引用配置已变化，请重新生成计划。");
            RequireSameDuplicatePoses(plan.Poses, source);
        }

        private static ExportSourceFileSnapshot RequireDuplicateSource(Component2 component, string sourcePath)
        {
            RequireCleanDuplicateSource(component, sourcePath);
            return ExportSourceFileSnapshot.Capture(sourcePath, FileHash.Sha256);
        }

        private static void RequireCleanDuplicateSource(Component2 component, string sourcePath)
        {
            var loaded = component.GetModelDoc2() as ModelDoc2;
            if (component.IsVirtual || component.IsSuppressed() || loaded == null || loaded.GetSaveFlag() ||
                !string.Equals(loaded.GetPathName(), sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("请解析并保存复制计划涉及的源模型：" + sourcePath);
        }

        private static void ValidateDuplicateFile(ExportSourceFileSnapshot snapshot)
        {
            snapshot.ValidateUnchanged();
            if (FileHash.Sha256(snapshot.Path) != snapshot.Sha256) throw new ValidationException("预检后模型内容已变化：" + snapshot.Path);
        }

        private void SuppressDuplicateOutsidePlan(AssemblyDoc assembly, Component2 clone, IEnumerable<string> otherConfigurations)
        {
            foreach (var name in otherConfigurations)
            {
                var configuration = _document.GetConfigurationByName(name) as Configuration;
                if (configuration == null) throw new ValidationException("无法读取装配配置：" + name);
                if (!configuration.GetComponentSuppressionState(clone.Name2))
                {
                    assembly.SetComponentState((int)swComponentSuppressionState_e.swComponentSuppressed,
                        new[] { new DispatchWrapper(clone) }, SpecifiedConfiguration, name, false);
                }
                if (!configuration.GetComponentSuppressionState(clone.Name2))
                    throw new ValidationException("无法将复制实例限制为当前配置：" + name);
            }
        }

        private void RequireSuppressedDuplicateOutsidePlan(Component2 clone, IEnumerable<string> otherConfigurations)
        {
            foreach (var name in otherConfigurations)
            {
                var configuration = _document.GetConfigurationByName(name) as Configuration;
                if (configuration == null || !configuration.GetComponentSuppressionState(clone.Name2))
                    throw new ValidationException("保存后复制实例在其他配置中未保持抑制状态：" + name);
            }
        }

        private static IEnumerable<Component2> TopLevelComponents(AssemblyDoc assembly)
        {
            return (assembly.GetComponents(true) as object[] ?? new object[0]).OfType<Component2>();
        }

        private static bool SameConfigurations(IEnumerable<ConfigurationBindings> previous, IEnumerable<ConfigurationBindings> next)
        {
            var expected = previous.OrderBy(item => item.ConfigurationId).ToArray();
            var actual = next.OrderBy(item => item.ConfigurationId).ToArray();
            return expected.Length == actual.Length && expected.Zip(actual, (left, right) => left.ConfigurationId == right.ConfigurationId &&
                left.ConfigurationName == right.ConfigurationName && SameRelations(left.Relations, right.Relations)).All(same => same);
        }

        private static SortedDictionary<string, double[]> CaptureDuplicatePoses(Component2 component)
        {
            var poses = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
            CaptureDuplicatePoses(component, string.Empty, poses);
            return poses;
        }

        private static void CaptureDuplicatePoses(Component2 component, string key, IDictionary<string, double[]> poses)
        {
            if (component.IsSuppressed()) { poses.Add(key, null); return; }
            var transform = component.Transform2;
            var values = transform == null ? null : transform.ArrayData as double[];
            if (values == null || values.Length < 13 || values.Take(13).Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new ValidationException("无法读取组件完整位姿：" + component.Name2);
            poses.Add(key, values.Take(13).ToArray());
            if (!string.Equals(Path.GetExtension(component.GetPathName()), ".SLDASM", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var child in (component.GetChildren() as object[] ?? new object[0]).OfType<Component2>())
            {
                var name = child.Name2;
                CaptureDuplicatePoses(child, key + "/" + name.Substring(name.LastIndexOf('/') + 1), poses);
            }
        }

        private static void RequireSameDuplicatePoses(SortedDictionary<string, double[]> expected, Component2 component)
        {
            var actual = CaptureDuplicatePoses(component);
            if (expected.Count != actual.Count || !expected.All(pair =>
            {
                double[] pose;
                return actual.TryGetValue(pair.Key, out pose) && (pair.Value == null ? pose == null :
                    pose != null && pair.Value.Zip(pose, (left, right) => Math.Abs(left - right) <= 1e-8).All(same => same));
            })) throw new ValidationException("复制实例或其内部组件无法保持原位：" + component.Name2);
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
            if (!string.Equals(resolved.Name2, sw.Name, StringComparison.Ordinal))
                throw new ValidationException("持久引用未返回所选实例：" + sw.InstancePath);
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
            if (!ReferenceEquals(_indexedPlan, plan))
            {
                _nodeIndex = IndexComponents(plan);
                _indexedPlan = plan;
            }
            ExportNode node; _nodeIndex.TryGetValue(component.Name2, out node);
            return ValidateEndpoint(endpoint, node);
        }
        internal static ExportNode ValidateEndpoint(BindingEndpoint endpoint, ExportNode node)
        {
            InstanceBindingRules.RequireAsset(node);
            if (IdentityService.AssetIdentitySeed(node.Source.Model) != endpoint.SourceIdentity ||
                !string.Equals(((SwCadNode)node.Source).SourcePath, endpoint.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("引用失效：[" + endpoint.DisplayPath + "] 的源模型已被替换，请解除后重新绑定。");
            // Use fresh file-level business metadata, not the classification-only subset.
            node.Properties = PropertyRules.Merge(node.Source.Model);
            return node;
        }
        internal Func<BindingEndpoint, ExportNode> CreateResolver(AssemblyExportPlan plan)
        { return CacheResolver(endpoint => Resolve(endpoint, plan)); }

        internal static Func<BindingEndpoint, ExportNode> CacheResolver(Func<BindingEndpoint, ExportNode> resolve)
        {
            // A saved graph contains separate endpoint objects even for the same occurrence.
            // Reuse the resolution only within one pass over the current plan.
            var nodes = new Dictionary<Tuple<string, string, string, string>, ExportNode>();
            var errors = new Dictionary<Tuple<string, string, string, string>, string>();
            return endpoint =>
            {
                if (endpoint == null) return resolve(endpoint);
                var key = StoredEndpointKey(endpoint);
                string error;
                if (errors.TryGetValue(key, out error)) throw new ValidationException(error);
                ExportNode node;
                if (nodes.TryGetValue(key, out node)) return node;
                try { node = resolve(endpoint); }
                catch (ValidationException ex) { errors.Add(key, ex.Message); throw; }
                nodes.Add(key, node);
                return node;
            };
        }
        private static Tuple<string, string, string, string> StoredEndpointKey(BindingEndpoint endpoint)
        { return Tuple.Create(endpoint.Context, endpoint.Reference, endpoint.SourceIdentity, endpoint.SourcePath); }

        private static List<InstanceBinding> DistinctStoredPairs(IEnumerable<InstanceBinding> records)
        {
            return records.GroupBy(r => Tuple.Create(StoredEndpointKey(r.Parent), StoredEndpointKey(r.Child)))
                .Select(group => group.First()).ToList();
        }

        public AssemblyRelations Export(AssemblyExportPlan plan)
        {
            EnsureReadable();
            var placements = Current.Relations.Select(record => record.SlotType == null && record.Engagement == null ? record : new InstanceBinding
            {
                Id = record.Id, Parent = record.Parent, Child = record.Child,
                SlotId = record.SlotId, SlotPose = record.SlotPose, SlotOffset = record.SlotOffset
            });
            return InstanceBindingRules.Resolve(placements, CreateResolver(plan), plan);
        }
        internal static Dictionary<string, ExportNode> IndexComponents(AssemblyExportPlan plan)
        {
            var nodes = new Dictionary<string, ExportNode>(StringComparer.Ordinal);
            foreach (var node in Flatten(plan.Roots))
            {
                var source = node.Source as SwCadNode;
                if (source == null || source.InstanceId == "root") continue;
                // Name is the full Component2.Name2 captured during traversal.
                // Looking it up does not read IDs from COM objects retained by the plan.
                if (nodes.ContainsKey(source.Name))
                    throw new ValidationException("当前总装配存在重复的组件实例名称：" + source.Name);
                nodes.Add(source.Name, node);
            }
            return nodes;
        }
        public static IEnumerable<ExportNode> Flatten(IEnumerable<ExportNode> roots)
        {
            foreach (var n in roots) { yield return n; foreach (var c in Flatten(n.Children)) yield return c; }
        }

        private void ReadStorage()
        {
            try { _data = ReadBindingStorage(); }
            catch (Exception ex) { Error = "承载关系存储无法读取，禁止忽略已有记录：" + ex.Message; }
        }

        private BindingDocument ReadBindingStorage()
        {
            object raw = null;
            try
            {
                raw = _document.IGet3rdPartyStorage(StreamName, false);
                if (raw == null) return new BindingDocument();
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
                BindingDocument data;
                using (var memory = new MemoryStream(bytes)) data = (BindingDocument)new DataContractSerializer(typeof(BindingDocument)).ReadObject(memory);
                data.ForConfiguration(int.MinValue, "validation");
                foreach (var configuration in data.Configurations)
                    configuration.Relations = DistinctStoredPairs(configuration.Relations);
                return data;
            }
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
