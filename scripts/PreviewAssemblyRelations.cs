using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal sealed class PreviewAssemblyRelations
{
    private const string StreamName = "SWAssetExporter.Bindings.v1";
    private const string MatchUniqueMovedOption = "--match-unique-moved";
    private const string OpenReadOnlyOption = "--open-readonly";
    private const string ReviewedMapOption = "--reviewed-map";
    private readonly SldWorks _app;
    private readonly ModelDoc2 _document;
    private readonly Configuration _configuration;
    private readonly string _path;
    private readonly Guid _assemblyUuid;
    private readonly Dictionary<string, ModelDescriptor> _models =
        new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase);
    private readonly List<Occurrence> _occurrences = new List<Occurrence>();
    private readonly Dictionary<string, string> _verifiedReferences =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private ILookup<string, Occurrence> _byId;
    private ILookup<string, Occurrence> _byName;

    private sealed class Occurrence
    {
        public Component2 Component;
        public Occurrence Owner;
        public string Name, Path, NodeId, Excluded, AssetError;
        public ModelDescriptor Model;
        public ExportNode Asset;
        public bool AssetRead;
    }

    private sealed class SourceNode
    {
        public string Id, Name, AssetId;
        public Occurrence Match;
        public string Error, Note;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var matchUniqueMoved = args.Length >= 4 && args[3] == MatchUniqueMovedOption;
            var hasTarget = args.Length >= 6 && matchUniqueMoved && args[4] == OpenReadOnlyOption;
            var hasReviewedMap = args.Length == 8 && hasTarget && args[6] == ReviewedMapOption;
            if (args.Length != 3 && !(args.Length == 4 && matchUniqueMoved) &&
                !(args.Length == 6 && hasTarget) && !hasReviewedMap)
                throw new ArgumentException("Usage: PreviewAssemblyRelations.exe <relations.json> <assembly.xml> <new-report.txt> [" +
                    MatchUniqueMovedOption + " [" + OpenReadOnlyOption + " <target.sldasm> [" + ReviewedMapOption + " <mapping.tsv>]]]");
            AssemblyRelations incoming;
            using (var file = File.OpenRead(args[0]))
                incoming = (AssemblyRelations)new DataContractJsonSerializer(typeof(AssemblyRelations)).ReadObject(file);
            XDocument xml;
            using (var reader = XmlReader.Create(args[1], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                xml = XDocument.Load(reader);
            incoming.Validate(xml, incoming.AssemblyUuid, incoming.AssemblyVersion);
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            if (hasTarget) OpenTargetReadOnly(app, args[5]);
            var preview = new PreviewAssemblyRelations(app);
            preview.Run(incoming, xml, args[2], matchUniqueMoved, hasReviewedMap ? args[7] : null);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void OpenTargetReadOnly(SldWorks app, string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到上次预览的装配。", path);
        var active = app.ActiveDoc as ModelDoc2;
        if (active != null)
        {
            if (!string.Equals(active.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("活动文档已切换，无法确认预览目标。");
            return;
        }
        int errors = 0, warnings = 0;
        var options = swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly |
            swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight;
        var doc = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY, (int)options, "", ref errors, ref warnings) as ModelDoc2;
        if (doc == null || !doc.IsOpenedReadOnly())
            throw new InvalidOperationException("无法以只读方式打开装配，错误码 " + errors + "，警告码 " + warnings + "。");
        var opened = app.ActiveDoc as ModelDoc2;
        if (!Equals(opened, doc)) throw new InvalidOperationException("只读装配未成为活动文档。");
    }

    private PreviewAssemblyRelations(SldWorks app)
    {
        _app = app;
        _document = app.ActiveDoc as ModelDoc2;
        if (_document == null || _document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            throw new InvalidOperationException("当前活动文档不是装配体。");
        _path = _document.GetPathName();
        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            throw new InvalidOperationException("请选择一个已保存的顶层装配体。");
        _configuration = _document.ConfigurationManager.ActiveConfiguration;
        if (_configuration == null) throw new InvalidOperationException("活动装配没有可读取的配置。");
        var model = ReadModel(_document);
        var states = _configuration.GetDisplayStates() as string[];
        model.Configuration = _configuration.Name;
        model.DisplayState = states == null ? "" : states.FirstOrDefault() ?? "";
        ModelRules.ValidateExportable(model);
        _assemblyUuid = IdentityService.AssemblyUuid(model);
        if (PropertyRules.ReadIsAsset(PropertyRules.Merge(model)))
            throw new InvalidOperationException("当前活动装配本身是 Asset，内部组件无法作为承载端点。");
    }

    private void Run(AssemblyRelations incoming, XDocument xml, string reportPath, bool matchUniqueMoved, string reviewedMap)
    {
        var beforeStamp = _document.GetUpdateStamp();
        var beforeSaveFlag = _document.GetSaveFlag();
        var readOnly = _document.IsOpenedReadOnly();
        var beforeWriteTime = File.GetLastWriteTimeUtc(_path);
        var configurationId = _configuration.GetID();
        var current = ReadStorage().ForConfiguration(configurationId, _configuration.Name).Relations;
        var root = _configuration.GetRootComponent3(false);
        if (root == null) throw new InvalidOperationException("当前配置的根组件未加载。");
        IndexChildren(root, "/" + Path.GetFileNameWithoutExtension(_path), null);
        Console.WriteLine("INDEXED_OCCURRENCES=" + _occurrences.Count);
        _byId = _occurrences.ToLookup(o => o.NodeId, StringComparer.Ordinal);
        _byName = _occurrences.ToLookup(o => o.Name, StringComparer.Ordinal);

        var sourceIds = new HashSet<string>(incoming.Relations.SelectMany(r => new[] { r.ParentNodeId, r.ChildNodeId }), StringComparer.Ordinal);
        var source = xml.Descendants("node").Where(n => sourceIds.Contains((string)n.Attribute("id")))
            .Select(n => new SourceNode {
                Id = (string)n.Attribute("id"), Name = (string)n.Attribute("name"),
                AssetId = (string)n.Elements("mesh").Attributes("asset_id").SingleOrDefault()
            }).ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var node in source.Values) Match(node);
        foreach (var collision in source.Values.Where(n => n.Match != null).GroupBy(n => n.Match).Where(g => g.Count() > 1))
        {
            foreach (var node in collision)
            {
                node.Error = "多个附件端点指向同一当前实例 " + node.Match.Name;
                node.Match = null;
            }
        }
        var remapCandidates = FindRemapCandidates(source.Values);
        var remapped = new List<string>();
        var ambiguous = new List<string>();
        var reviewed = new List<string>();
        if (matchUniqueMoved)
        {
            var reserved = new HashSet<Occurrence>(source.Values.Where(n => n.Match != null).Select(n => n.Match));
            MatchUniqueMoved(remapCandidates, reserved, remapped, ambiguous);
        }
        if (reviewedMap != null) ApplyReviewedMap(reviewedMap, source.Values, remapCandidates, reviewed);
        var remapHints = remapCandidates.Where(pair => pair.Key.Match == null)
            .Select(pair => DescribeCandidates(pair.Key, pair.Value)).ToList();
        Console.WriteLine("MAPPED_ENDPOINTS=" + source.Values.Count(n => n.Match != null));

        var existing = new HashSet<string>(StringComparer.Ordinal);
        var graph = new List<AssemblyRelation>();
        var existingIssues = new List<string>();
        var resolvedSaved = new Dictionary<string, Occurrence>(StringComparer.Ordinal);
        foreach (var relation in current)
        {
            try
            {
                var parent = ResolveSaved(relation.Parent, resolvedSaved);
                var child = ResolveSaved(relation.Child, resolvedSaved);
                existing.Add(EdgeKey(parent, child));
                graph.Add(ToEdge(parent, child));
            }
            catch (Exception ex)
            {
                if (!(ex is ValidationException || ex is COMException || ex is FormatException)) throw;
                existingIssues.Add(relation.Parent.DisplayName + " → " + relation.Child.DisplayName + " | " + ex.Message);
            }
        }
        Console.WriteLine("CHECKED_SAVED_RELATIONS=" + current.Count);

        var added = new List<string>();
        var already = new List<string>();
        var unmatched = new List<string>();
        var rejected = new List<string>();
        foreach (var edge in incoming.Relations)
        {
            var p = source[edge.ParentNodeId];
            var c = source[edge.ChildNodeId];
            var label = p.Name + " → " + c.Name;
            if (p.Match == null || c.Match == null)
            {
                unmatched.Add(label + " | " + string.Join("；", new[] {
                    p.Error == null ? null : "父端：" + p.Error,
                    c.Error == null ? null : "子端：" + c.Error
                }.Where(e => e != null)));
                continue;
            }
            var matchNotes = new[] { p.Note, c.Note }.Where(n => !string.IsNullOrEmpty(n)).Distinct();
            var detail = label + string.Concat(matchNotes.Select(n => " | " + n));
            if (existing.Contains(EdgeKey(p.Match, c.Match)))
            {
                already.Add(detail);
                continue;
            }
            try
            {
                VerifyReference(p.Match);
                VerifyReference(c.Match);
                graph.Add(ToEdge(p.Match, c.Match));
                added.Add(detail);
            }
            catch (Exception ex)
            {
                if (!(ex is ValidationException || ex is COMException)) throw;
                rejected.Add(detail + " | " + ex.Message);
            }
        }

        string graphError = null;
        try { InstanceBindingRules.ValidateGraph(graph); }
        catch (ValidationException ex) { graphError = ex.Message; }
        var latest = _app.ActiveDoc as ModelDoc2;
        var activeConfiguration = latest == null ? null : latest.ConfigurationManager.ActiveConfiguration;
        var afterStamp = _document.GetUpdateStamp();
        var afterSaveFlag = _document.GetSaveFlag();
        if (latest == null || !string.Equals(latest.GetPathName(), _path, StringComparison.OrdinalIgnoreCase) ||
            activeConfiguration == null || activeConfiguration.GetID() != configurationId ||
            activeConfiguration.Name != _configuration.Name || beforeWriteTime != File.GetLastWriteTimeUtc(_path))
            throw new InvalidOperationException("预览期间活动装配路径、配置或磁盘修改时间改变。");
        if (beforeSaveFlag != afterSaveFlag || (!readOnly && beforeStamp != afterStamp))
            throw new InvalidOperationException("预览期间装配会话状态改变，保存标记 " + beforeSaveFlag + " → " +
                afterSaveFlag + "，更新戳 " + beforeStamp + " → " + afterStamp + "。");

        var text = new StringBuilder();
        text.AppendLine("SOLIDWORKS 承载关系只读预览");
        text.AppendLine("当前装配  " + _path);
        text.AppendLine("活动配置  " + _configuration.Name + "（ID " + configurationId + "）");
        text.AppendLine("附件装配 UUID  " + incoming.AssemblyUuid);
        text.AppendLine("当前装配 UUID  " + _assemblyUuid.ToString("D"));
        text.AppendLine("当前配置遍历到的组件实例  " + _occurrences.Count);
        text.AppendLine("匹配的附件端点  " + source.Values.Count(n => n.Match != null) + " / " + source.Count);
        if (matchUniqueMoved)
        {
            text.AppendLine("一对一改路径映射端点  " + remapped.Count);
            if (reviewedMap != null) text.AppendLine("根据工位和层级复核映射端点  " + reviewed.Count);
            text.AppendLine("仍有歧义的端点  " + remapHints.Count);
            text.AppendLine("没有改路径候选的未匹配端点  " + source.Values.Count(n => n.Match == null && !remapCandidates.ContainsKey(n)));
        }
        text.AppendLine("活动配置已保存关系  " + current.Count);
        text.AppendLine("附件关系  " + incoming.Relations.Count);
        text.AppendLine("新增候选  " + added.Count);
        text.AppendLine("已存在  " + already.Count);
        text.AppendLine("无法匹配  " + unmatched.Count);
        text.AppendLine("匹配成功但校验失败  " + rejected.Count);
        text.AppendLine("已有关系待修复  " + existingIssues.Count);
        if (graphError != null) text.AppendLine("合并图校验失败  " + graphError);
        text.AppendLine("装配保存标记和磁盘修改时间前后一致。未提交或保存。");
        text.AppendLine("装配会话更新戳  " + beforeStamp + " → " + afterStamp + (readOnly ? "（只读会话）" : ""));
        AppendList(text, "新增候选", added);
        AppendList(text, "已存在", already);
        AppendList(text, "无法匹配", unmatched);
        AppendList(text, "匹配成功但校验失败", rejected);
        AppendList(text, "已有关系待修复", existingIssues);
        if (matchUniqueMoved)
        {
            AppendList(text, "已在预览中按一对一映射的端点", remapped);
            if (reviewedMap != null) AppendList(text, "根据工位和层级复核映射的端点", reviewed);
            AppendList(text, "仍有歧义的端点及其当前候选", reviewedMap == null ? ambiguous : remapHints);
        }
        else
            AppendList(text, "同资产 UUID 且末级实例名相同的改路径候选（需人工确认）", remapHints);
        AppendList(text, "附件端点所在的顶层分支", source.Values.Select(n => n.Name.Split('/')[0])
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList());
        AppendList(text, "当前配置的顶层组件", _occurrences.Where(o => o.Owner == null).Select(o => o.Name).ToList());
        using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file, new UTF8Encoding(true))) writer.Write(text.ToString());
        Console.WriteLine("TARGET=" + _path + " | " + _configuration.Name);
        Console.WriteLine("PACKAGE_UUID=" + incoming.AssemblyUuid + " TARGET_UUID=" + _assemblyUuid.ToString("D"));
        Console.WriteLine("SAVED=" + current.Count + " SOURCE=" + incoming.Relations.Count + " NEW=" + added.Count +
            " EXISTING=" + already.Count + " UNMATCHED=" + unmatched.Count + " REJECTED=" + rejected.Count +
            " SAVED_ISSUES=" + existingIssues.Count + " GRAPH_OK=" + (graphError == null));
        Console.WriteLine("OCCURRENCES=" + _occurrences.Count + " MATCHED_ENDPOINTS=" +
            source.Values.Count(n => n.Match != null) + "/" + source.Count + " REMAP_HINTS=" + remapHints.Count);
        if (matchUniqueMoved)
            Console.WriteLine("REMAPPED=" + remapped.Count + " REVIEWED=" + reviewed.Count +
                " AMBIGUOUS=" + remapHints.Count + " NO_CANDIDATE=" +
                source.Values.Count(n => n.Match == null && !remapCandidates.ContainsKey(n)));
        Console.WriteLine("REPORT=" + Path.GetFullPath(reportPath));
    }

    private void IndexChildren(Component2 owner, string ownerPath, Occurrence parent)
    {
        foreach (var component in (owner.GetChildren() as object[] ?? new object[0]).OfType<Component2>())
        {
            var name = component.Name2;
            var path = ownerPath + "/" + name;
            var excluded = parent == null ? null : parent.Excluded;
            if (excluded == null)
            {
                if (component.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                    excluded = name + " 已抑制";
                else if (component.Visible != (int)swComponentVisibilityState_e.swComponentVisible)
                    excluded = name + " 已隐藏";
                else if (component.IsEnvelope())
                    excluded = name + " 是包络";
            }
            var item = new Occurrence { Component = component, Owner = parent, Name = name, Path = path, Excluded = excluded,
                NodeId = IdentityService.ExportNodeUuid(_assemblyUuid, path).ToString("D") };
            _occurrences.Add(item);
            IndexChildren(component, path, item);
        }
    }

    private void Match(SourceNode source)
    {
        var candidates = _byId[source.Id].ToList();
        if (candidates.Count == 0) candidates = _byName[source.Name].ToList();
        if (candidates.Count == 0) { source.Error = "当前配置中没有同路径实例"; return; }
        if (candidates.Count != 1) { source.Error = "当前配置有多个实例符合 ID 或完整路径"; return; }
        var occurrence = candidates[0];
        try
        {
            var asset = ReadAsset(occurrence);
            if (!string.Equals(BaseUuid(asset.AssetId), BaseUuid(source.AssetId), StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("当前路径的资产 UUID 不符（附件 " + source.AssetId + "，当前 " + asset.AssetId + "）");
            source.Match = occurrence;
            if (!string.Equals(asset.AssetId, source.AssetId, StringComparison.OrdinalIgnoreCase))
                source.Note = occurrence.Name + " 资产版本 " + source.AssetId + " → " + asset.AssetId;
        }
        catch (Exception ex)
        {
            if (!(ex is ValidationException || ex is COMException)) throw;
            source.Error = ex.Message;
        }
    }

    private Dictionary<SourceNode, List<Occurrence>> FindRemapCandidates(IEnumerable<SourceNode> sources)
    {
        var byLeaf = _occurrences.ToLookup(o => LeafName(o.Name), StringComparer.Ordinal);
        var matches = new Dictionary<SourceNode, List<Occurrence>>();
        foreach (var source in sources.Where(n => n.Match == null).OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            var candidates = new List<Occurrence>();
            foreach (var occurrence in byLeaf[LeafName(source.Name)])
            {
                try
                {
                    var asset = ReadAsset(occurrence);
                    if (string.Equals(BaseUuid(asset.AssetId), BaseUuid(source.AssetId), StringComparison.OrdinalIgnoreCase))
                        candidates.Add(occurrence);
                }
                catch (ValidationException) { }
            }
            if (candidates.Count > 0)
                matches.Add(source, candidates);
        }
        return matches;
    }

    private static void MatchUniqueMoved(Dictionary<SourceNode, List<Occurrence>> candidates,
        HashSet<Occurrence> reserved, List<string> mapped, List<string> ambiguous)
    {
        var claimants = candidates.SelectMany(pair => pair.Value).ToLookup(o => o);
        foreach (var pair in candidates.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            var source = pair.Key;
            var options = pair.Value;
            var detail = DescribeCandidates(source, options);
            if (options.Count == 1 && !reserved.Contains(options[0]) && claimants[options[0]].Count() == 1)
            {
                source.Match = options[0];
                source.Error = null;
                source.Note = "路径映射 " + detail;
                mapped.Add(detail);
                continue;
            }
            source.Error = options.Count > 1 ? "有 " + options.Count + " 个同资产同末级名候选" :
                reserved.Contains(options[0]) ? "唯一候选已被同路径端点占用" : "唯一候选同时被其他附件端点指向";
            ambiguous.Add(detail + " | " + source.Error);
        }
    }

    private static string DescribeCandidates(SourceNode source, IEnumerable<Occurrence> candidates)
    {
        return source.Name + " [" + source.AssetId + "] → " +
            string.Join("；", candidates.OrderBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => o.Name + " [" + o.Asset.AssetId + "]"));
    }

    private static void ApplyReviewedMap(string path, IEnumerable<SourceNode> sources,
        Dictionary<SourceNode, List<Occurrence>> candidates, List<string> reviewed)
    {
        var byName = sources.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var used = new HashSet<Occurrence>(sources.Where(s => s.Match != null).Select(s => s.Match));
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var columns = line.Split('\t');
            SourceNode source;
            List<Occurrence> options;
            if (columns.Length != 3 || !byName.TryGetValue(columns[0], out source) ||
                source.Match != null || !candidates.TryGetValue(source, out options))
                throw new ValidationException("复核映射的旧端点不在当前歧义清单中：" + line);
            var target = options.SingleOrDefault(o => o.Name == columns[1]);
            if (target == null || !used.Add(target))
                throw new ValidationException("复核映射目标不在当前候选中或已被使用：" + line);
            source.Match = target;
            source.Error = null;
            var detail = DescribeCandidates(source, new[] { target }) + " | " + columns[2];
            source.Note = "名称复核映射 " + detail;
            reviewed.Add(detail);
        }
    }

    private ExportNode ReadAsset(Occurrence occurrence)
    {
        if (!occurrence.AssetRead)
        {
            try
            {
                if (occurrence.Excluded != null) throw new ValidationException(occurrence.Excluded);
                for (var owner = occurrence.Owner; owner != null; owner = owner.Owner)
                    if (PropertyRules.ReadIsAsset(PropertyRules.Merge(ReadOccurrenceModel(owner))))
                        throw new ValidationException("当前实例处于 Asset 边界内部：" + owner.Name);
                var model = ReadOccurrenceModel(occurrence);
                var properties = PropertyRules.Merge(model);
                if (!PropertyRules.ReadIsAsset(properties) || PropertyRules.IsRobotClass(properties))
                    throw new ValidationException("当前实例不是可绑定的普通 Asset");
                ModelRules.ValidateExportable(model);
                PropertyRules.RequireWanxiangAssetClass(properties, occurrence.Name);
                var version = PropertyRules.RequirePositiveInteger(properties, PropertyRules.AssetVersion, occurrence.Name);
                occurrence.Asset = new ExportNode {
                    Id = occurrence.NodeId, Name = occurrence.Name, Kind = ExportNodeKind.Asset,
                    AssetId = IdentityService.AssetId(IdentityService.AssetUuid(model), version), Properties = properties,
                    Source = new SwCadNode(_app, occurrence.Component, occurrence.Path)
                };
            }
            catch (Exception ex)
            {
                if (!(ex is ValidationException || ex is COMException)) throw;
                occurrence.AssetError = ex.Message;
            }
            occurrence.AssetRead = true;
        }
        if (occurrence.AssetError != null) throw new ValidationException(occurrence.AssetError);
        return occurrence.Asset;
    }

    private ModelDescriptor ReadModel(Component2 component)
    {
        var path = component.GetPathName() ?? "";
        ModelDescriptor model;
        if (_models.TryGetValue(path, out model)) return model;
        var doc = component.GetModelDoc2() as ModelDoc2;
        if ((doc == null || !string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase)) && path.Length > 0)
            doc = _app.GetOpenDocumentByName(path) as ModelDoc2;
        if (doc == null || !string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("当前会话未加载源模型，无法只读确认资产身份：" + component.Name2);
        model = ReadModel(doc);
        _models.Add(path, model);
        return model;
    }

    private ModelDescriptor ReadOccurrenceModel(Occurrence occurrence)
    {
        return occurrence.Model ?? (occurrence.Model = ReadModel(occurrence.Component));
    }

    private static ModelDescriptor ReadModel(ModelDoc2 doc)
    {
        var path = doc.GetPathName();
        var creation = Convert.ToString(doc.get_SummaryInfo((int)swSummInfoField_e.swSumInfoCreateDate)) ?? "";
        DateTime time;
        if (DateTime.TryParse(creation, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time) ||
            DateTime.TryParse(creation, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time))
            creation = time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        return new ModelDescriptor {
            FullPath = path, FileName = Path.GetFileName(path), InternalCreationTime = creation.Trim(),
            DocumentKind = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? DocumentKind.Assembly : DocumentKind.Part,
            IsSaved = !string.IsNullOrEmpty(path) && File.Exists(path), IsDirty = doc.GetSaveFlag(),
            FileProperties = new SwInterfacePropertyTarget(doc).Read().Values,
            ConfigurationProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private BindingDocument ReadStorage()
    {
        object raw = null;
        try
        {
            raw = _document.IGet3rdPartyStorage(StreamName, false);
            if (raw == null) return new BindingDocument();
            var stream = (IStream)raw;
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stream.Stat(out stat, 1);
            if (stat.cbSize <= 0 || stat.cbSize > 16 * 1024 * 1024)
                throw new InvalidDataException("当前装配的承载关系流长度无效。");
            var bytes = new byte[(int)stat.cbSize];
            var count = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                stream.Seek(0, 0, IntPtr.Zero);
                stream.Read(bytes, bytes.Length, count);
                if (Marshal.ReadInt32(count) != bytes.Length) throw new EndOfStreamException();
            }
            finally { Marshal.FreeCoTaskMem(count); }
            using (var memory = new MemoryStream(bytes))
                return (BindingDocument)new DataContractSerializer(typeof(BindingDocument)).ReadObject(memory);
        }
        finally
        {
            if (raw != null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
            _document.IRelease3rdPartyStorage(StreamName);
        }
    }

    private Occurrence ResolveSaved(BindingEndpoint endpoint, IDictionary<string, Occurrence> cache)
    {
        var key = string.Join("\n", endpoint.Reference, endpoint.Context, endpoint.SourceIdentity, endpoint.SourcePath);
        Occurrence occurrence;
        if (cache.TryGetValue(key, out occurrence)) return occurrence;
        int error;
        var component = _document.Extension.GetObjectByPersistReference3(Convert.FromBase64String(endpoint.Reference), out error) as Component2;
        if (component == null || error != 0 || SwInstanceBindings.Context(component) != endpoint.Context)
            throw new ValidationException("持久引用失效：" + endpoint.DisplayName);
        var occurrences = _byName[component.Name2].ToList();
        if (occurrences.Count != 1) throw new ValidationException("现有端点在当前配置中不唯一：" + component.Name2);
        occurrence = occurrences[0];
        ReadAsset(occurrence);
        if (IdentityService.AssetIdentitySeed(ReadOccurrenceModel(occurrence)) != endpoint.SourceIdentity ||
            !string.Equals(component.GetPathName(), endpoint.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("现有端点的源模型身份已变更：" + endpoint.DisplayName);
        cache.Add(key, occurrence);
        return occurrence;
    }

    private void VerifyReference(Occurrence occurrence)
    {
        string failure;
        if (!_verifiedReferences.TryGetValue(occurrence.NodeId, out failure))
        {
            var bytes = _document.Extension.GetPersistReference3(occurrence.Component) as byte[];
            if (bytes == null || bytes.Length == 0)
                failure = "无法取得当前实例的持久引用：" + occurrence.Name;
            else
            {
                int error;
                var component = _document.Extension.GetObjectByPersistReference3(bytes, out error) as Component2;
                if (component == null || error != 0 || component.Name2 != occurrence.Name ||
                    SwInstanceBindings.Context(component) != SwInstanceBindings.Context(occurrence.Component))
                    failure = "持久引用无法回读到当前实例：" + occurrence.Name;
            }
            _verifiedReferences.Add(occurrence.NodeId, failure);
        }
        if (failure != null) throw new ValidationException(failure);
    }

    private static AssemblyRelation ToEdge(Occurrence parent, Occurrence child)
    {
        InstanceBindingRules.ValidateConnection(parent.Asset, child.Asset);
        var relative = parent.Asset.Source.WorldTransform.InverseRigid().Multiply(child.Asset.Source.WorldTransform);
        var pose = RelationPose.From(Pose.FromTransform(relative));
        pose.Validate();
        return new AssemblyRelation { ParentNodeId = parent.NodeId, ChildNodeId = child.NodeId, Pose = pose };
    }

    private static string EdgeKey(Occurrence parent, Occurrence child) { return parent.NodeId + "/" + child.NodeId; }
    private static string BaseUuid(string assetId) { return (assetId ?? "").Split(':')[0]; }
    private static string LeafName(string name) { return name.Substring(name.LastIndexOf('/') + 1); }

    private static void AppendList(StringBuilder report, string title, IList<string> items)
    {
        report.AppendLine().AppendLine(title + "（" + items.Count + "）");
        for (var index = 0; index < items.Count; index++)
            report.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture) + ". " + items[index]);
    }
}
