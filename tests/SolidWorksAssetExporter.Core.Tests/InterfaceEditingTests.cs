using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static InterfacePropertySnapshot InterfaceSnapshot()
        {
            var result = new InterfacePropertySnapshot();
            foreach (var p in new Dictionary<string, string> { { "class", "movable" }, { "is_fixture", "1" }, { "connection_interface", "" }, { "accepts_interfaces", "old" }, { "asset_version", "1" } })
            { result.Raw[p.Key] = p.Value; result.Values[p.Key] = p.Value; result.Types[p.Key] = 30; }
            return result;
        }
        private static void RunInterfaceEditingTests()
        {
            Run("Interface suggestions preserve existing capability and deduplicate 200 children", delegate {
                var result = InterfaceEditRules.Suggest("old;other", Enumerable.Repeat("tip-A", 200));
                Equal("old;other;tip-A", string.Join(";", result)); Equal("old;other", string.Join(";", InterfaceEditRules.Suggest("old;other", new string[0])));
                Throws<ValidationException>(() => InterfaceEditRules.Suggest("tip-A", new[] { "TIP-A" }));
                Throws<ValidationException>(() => InterfaceEditRules.Suggest("old", new[] { "two;values" }));
            });
            Run("Interface editing requires version bump except explicit unpublished sources", delegate {
                var s = InterfaceSnapshot(); Throws<ValidationException>(() => InterfaceEditRules.Changes(s, "", new[] { "old", "tip" }, 1, false));
                var changed = InterfaceEditRules.Changes(s, "", new[] { "old", "tip" }, 2, false); Equal("2", changed["asset_version"]);
                True(!InterfaceEditRules.Changes(s, "", new[] { "old", "tip" }, 1, true).ContainsKey("asset_version"));
                Throws<ValidationException>(() => InterfaceEditRules.Changes(s, "", new[] { "tip" }, 0, true));
                Equal(0, InterfaceEditRules.Changes(s, "", new[] { "old" }, 1, false).Count);
            });
            Run("Version-only edits save without rewriting interface properties", delegate {
                var target = new InterfaceTargetFake();
                target.State.Raw[PropertyRules.AcceptsInterfaces] = " old ";
                target.State.Values[PropertyRules.AcceptsInterfaces] = " old ";
                var before = target.Read();
                var increase = InterfaceEditRules.Changes(before, "", new[] { "old" }, 3, false);
                Equal(1, increase.Count);
                InterfacePropertyWriter.Apply(target, before, increase);
                Equal(3, target.State.Version);
                Equal(" old ", target.State.Raw[PropertyRules.AcceptsInterfaces]);
                var raised = target.Read();
                Throws<ValidationException>(() => InterfaceEditRules.Changes(raised, "", new[] { "old" }, 2, false));
                var correction = InterfaceEditRules.Changes(raised, "", new[] { "old" }, 2, true);
                InterfacePropertyWriter.Apply(target, raised, correction);
                Equal(2, target.State.Version);
                Equal(2, target.Saves);
            });
            Run("Interface editing checks the exact asset version in the published registry", delegate {
                var uuid = "aaaaaaaa-aaaa-5aaa-8aaa-aaaaaaaaaaaa";
                var registered = new[] { new AssetRegistration { Uuid = uuid, Version = 1 },
                    new AssetRegistration { Uuid = uuid, Version = 3 },
                    new AssetRegistration { Uuid = "bbbbbbbb-bbbb-5bbb-8bbb-bbbbbbbbbbbb", Version = 2 } };
                Equal((int?)4, InterfaceEditRules.NextVersionIfPublished(registered, uuid, 1));
                Equal((int?)null, InterfaceEditRules.NextVersionIfPublished(registered, uuid, 2));
                Equal((int?)null, InterfaceEditRules.NextVersionIfPublished(registered, uuid, 4));
            });
            Run("Interface editor preserves role validation and blocks linked fields", delegate {
                var s = InterfaceSnapshot();
                Equal("", InterfaceEditRules.Changes(s, "", new string[0], 2, false)[PropertyRules.AcceptsInterfaces]);
                var tool = InterfaceSnapshot(); tool.Values[PropertyRules.IsTool] = "1";
                Throws<ValidationException>(() => InterfaceEditRules.Changes(tool, "", new[] { "new" }, 2, false));
                s.Linked.Add("accepts_interfaces"); Throws<ValidationException>(() => InterfaceEditRules.Changes(s, "", new[] { "tip" }, 2, false));
                Throws<ValidationException>(() => InterfaceEditRules.Changes(InterfaceSnapshot(), "x;y", new[] { "old" }, 2, false));
            });
            Run("Interface transaction rejects read-only dirty and stale targets before writing", delegate {
                var expected = InterfaceSnapshot(); var changes = InterfaceEditRules.Changes(expected, "", new[] { "new" }, 2, false);
                var t = new InterfaceTargetFake { ReadOnly = true }; Throws<ValidationException>(() => InterfacePropertyWriter.Apply(t, expected, changes)); Equal(0, t.Writes);
                t.ReadOnly = false; t.Dirty = true; Throws<ValidationException>(() => InterfacePropertyWriter.Apply(t, expected, changes)); Equal(0, t.Writes);
                t.Dirty = false; t.State.Raw["class"] = "station"; Throws<ValidationException>(() => InterfacePropertyWriter.Apply(t, expected, changes)); Equal(0, t.Writes);
            });
            Run("Interface transaction saves once and rolls back partial failure", delegate {
                var s = InterfaceSnapshot(); var changes = InterfaceEditRules.Changes(s, "", new[] { "new" }, 2, false);
                var good = new InterfaceTargetFake(); InterfacePropertyWriter.Apply(good, s, changes); Equal(1, good.Saves); Equal("new", good.State.Get("accepts_interfaces"));
                var fail = new InterfaceTargetFake { FailSave = true }; Throws<ValidationException>(() => InterfacePropertyWriter.Apply(fail, s, changes));
                Equal("old", fail.State.Get("accepts_interfaces")); Equal("1", fail.State.Get("asset_version")); True(fail.Dirty);
                var partial = new InterfaceTargetFake { FailWrite = 2 }; Throws<ValidationException>(() => InterfacePropertyWriter.Apply(partial, s, changes));
                Equal("old", partial.State.Get("accepts_interfaces")); Equal("1", partial.State.Get("asset_version")); Equal(0, partial.Saves);
            });
            Run("Interface transaction restores originally absent property on failure", delegate {
                var s = InterfaceSnapshot(); s.Raw.Remove("connection_interface"); s.Values.Remove("connection_interface"); s.Types.Remove("connection_interface");
                var t = new InterfaceTargetFake { State = s, FailSave = true };
                Throws<ValidationException>(() => InterfacePropertyWriter.Apply(t, s, new Dictionary<string, string> { { "connection_interface", "new" } }));
                True(!t.State.Raw.ContainsKey("connection_interface"));
            });
            Run("Managed slot capacity is saved with the asset version and preserved when omitted", delegate {
                var target = new InterfaceTargetFake();
                var before = target.Read();
                Throws<ValidationException>(() => InterfaceEditRules.Changes(before, "", new[] { "old" }, -1, 2, false));
                var changes = InterfaceEditRules.Changes(before, "", new[] { "old" }, 24, 2, false);
                InterfacePropertyWriter.Apply(target, before, changes);
                Equal("24", target.State.Get(PropertyRules.SlotsNum));
                Equal(2, target.State.Version);
                Equal(1, target.Saves);
                True(!InterfaceEditRules.Changes(target.Read(), "", new[] { "old", "tip" }, 3, false)
                    .ContainsKey(PropertyRules.SlotsNum));
                Equal(0, InterfaceEditRules.Changes(target.Read(), "", new[] { "old" }, 24, 2, true).Count);
            });
            Run("Plugin slot definition saves both legacy defaults and preserves ordinary asset properties", delegate {
                var target = new InterfaceTargetFake();
                target.State.Raw[PropertyRules.IsAsset] = target.State.Values[PropertyRules.IsAsset] = "1";
                target.State.Types[PropertyRules.IsAsset] = 30;
                target.State.Raw[PropertyRules.SlotsNum] = target.State.Values[PropertyRules.SlotsNum] = "2";
                target.State.Types[PropertyRules.SlotsNum] = 30;
                target.State.Raw.Remove(PropertyRules.IsFixture);
                target.State.Values.Remove(PropertyRules.IsFixture);
                target.State.Types.Remove(PropertyRules.IsFixture);
                var before = target.Read();
                Throws<ValidationException>(() => InterfaceEditRules.Changes(before, null, null, null, "place", "loose", 1, false));
                var changes = InterfaceEditRules.Changes(before, null, null, null, "place", "loose", 2, false);
                Equal(3, changes.Count);
                InterfacePropertyWriter.Apply(target, before, changes);
                Equal("place", target.State.Get(PropertyRules.SlotType));
                Equal("loose", target.State.Get(PropertyRules.AssetEngagement));
                Equal("old", target.State.Get(PropertyRules.AcceptsInterfaces));
                Equal(2, target.State.Version);
                Equal(1, target.Saves);
                Equal(0, InterfaceEditRules.Changes(target.Read(), null, null, null, "place", "loose", 2, true).Count);
            });
            Run("Plugin slot definition uses supported pairs and protects linked or non-asset sources", delegate {
                var snapshot = InterfaceSnapshot();
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 1, "place", "loose", 1, true));
                snapshot.Values[PropertyRules.IsAsset] = "1";
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, null, "thread", "tight", 1, true));
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 1, "place", "tight", 1, true));
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 1, "thread", "", 1, true));
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 1, "freeform", "loose", 1, true));
                var changes = InterfaceEditRules.Changes(snapshot, null, null, 1, "thread", "tight", 1, true);
                Equal("thread", changes[PropertyRules.SlotType]);
                Equal("tight", changes[PropertyRules.AssetEngagement]);
                snapshot.Linked.Add(PropertyRules.AssetEngagement);
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 1, "thread", "tight", 1, true));
                True(!InterfaceEditRules.Changes(snapshot, "", new[] { "old", "tip" }, 1, true)
                    .ContainsKey(PropertyRules.SlotType));
            });
            Run("Plugin saves standalone asset engagement and clears the old slot type on explicit zero capacity", delegate {
                var snapshot = InterfaceSnapshot();
                snapshot.Values[PropertyRules.IsAsset] = "1";
                snapshot.Values[PropertyRules.SlotType] = "thread";
                var engagement = InterfaceEditRules.Changes(snapshot, null, null, null, null, "tight", 1, true);
                Equal(1, engagement.Count);
                Equal("tight", engagement[PropertyRules.AssetEngagement]);
                snapshot.Values[PropertyRules.SlotsNum] = "2";
                snapshot.Values[PropertyRules.AssetEngagement] = "tight";
                var changes = InterfaceEditRules.Changes(snapshot, null, null, 0, null, "tight", 1, true);
                Equal("0", changes[PropertyRules.SlotsNum]);
                Equal("", changes[PropertyRules.SlotType]);
                True(!changes.ContainsKey(PropertyRules.AssetEngagement));
                snapshot.Linked.Add(PropertyRules.SlotType);
                Throws<ValidationException>(() => InterfaceEditRules.Changes(snapshot, null, null, 0, null, "tight", 1, true));
            });
            Run("Plugin detects a native file property edit before saving overlapping draft values", delegate {
                var target = new InterfaceTargetFake();
                target.State.Values[PropertyRules.IsAsset] = "1";
                target.State.Raw[PropertyRules.AssetEngagement] = target.State.Values[PropertyRules.AssetEngagement] = "loose";
                var before = target.Read();
                var draft = InterfaceEditRules.Changes(before, null, null, null, null, "tight", 1, true);
                target.State.Values[PropertyRules.AssetEngagement] = "tight";
                Throws<ValidationException>(() => InterfacePropertyWriter.Apply(target, before, draft));
                Equal(0, target.Writes);
                Equal(0, target.Saves);
                Equal("tight", target.State.Get(PropertyRules.AssetEngagement));
                Equal(0, InterfaceEditRules.Changes(target.Read(), null, null, null, null, "tight", 1, true).Count);
            });
            Run("Plugin initializes legacy managed versions and repairs an explicitly supplied interface list", delegate {
                var snapshot = InterfaceSnapshot();
                snapshot.Values[PropertyRules.AssetVersion] = "legacy";
                snapshot.Values[PropertyRules.AcceptsInterfaces] = "old；tip";
                Equal(1, snapshot.Version);
                Equal("1", InterfaceEditRules.VersionChanges(snapshot, 1)[PropertyRules.AssetVersion]);
                Equal("1", InterfaceEditRules.VersionChanges(snapshot, 1, PropertyRules.AssemblyVersion)[PropertyRules.AssemblyVersion]);
                var changes = InterfaceEditRules.Changes(snapshot, null, new[] { "old", "tip" }, null, 1, true);
                Equal("old;tip", changes[PropertyRules.AcceptsInterfaces]);
                Equal("1", changes[PropertyRules.AssetVersion]);
                True(!changes.ContainsKey(PropertyRules.ConnectionInterface));
            });
            Run("Changing the root assembly package version preserves published asset semantics", delegate {
                var published = new Dictionary<string, string>
                {
                    { PropertyRules.AssetVersion, "1" }, { PropertyRules.AssemblyVersion, "1" }, { PropertyRules.SlotsNum, "24" }
                };
                var current = new Dictionary<string, string>(published);
                current[PropertyRules.AssemblyVersion] = "2";
                Equal(0, ExportCoordinator.RegisteredPropertyDifferences(published, current).Count);
                current[PropertyRules.SlotsNum] = "16";
                True(ExportCoordinator.RegisteredPropertyDifferences(published, current).Single().StartsWith(PropertyRules.SlotsNum));
            });
            Run("Interface catalogue merges persistently and never overwrites corrupted input", delegate {
                var folder = Path.Combine(Path.GetTempPath(), "interface-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                try { var path = Path.Combine(folder, "interfaces.json"); var store = new InterfaceCatalogStore(path);
                    Equal(0, store.Load().Count); store.Merge(new[] { "B", "A", "A" }); new InterfaceCatalogStore(path).Merge(new[] { "C" });
                    Equal("A;B;C", string.Join(";", store.Load())); File.WriteAllText(path, "damaged");
                    Throws<ValidationException>(() => store.Merge(new[] { "D" })); Equal("damaged", File.ReadAllText(path));
                } finally { Directory.Delete(folder, true); }
            });
            Run("Online interface catalogue adds exact names and uses validated content cache", delegate {
                var folder = TempDirectory();
                try
                {
                    var server = new CatalogueServer();
                    server.Put("现有接口");
                    server.Files[FileHash.Sha256Text("旧纯文本候选") + ".txt"] = Encoding.UTF8.GetBytes("旧纯文本候选");
                    var local = new InterfaceCatalogStore(Path.Combine(folder, "interfaces.json"));
                    local.Merge(new[] { "旧本机接口" });
                    var catalog = Catalogue(server, folder, "http://catalog.example/");
                    var before = catalog.Load();
                    Equal("online", before.Source);
                    Equal("现有接口", string.Join(";", before.Names));
                    Equal(1, server.FileReads);
                    Equal("现有接口", string.Join(";", Catalogue(server, folder, "http://CATALOG.example").Load().Names));
                    Equal(1, server.FileReads);

                    var merged = catalog.Merge(new[] { "  试管.D20 ", "现有接口", "试管.D20" });
                    Equal("online", merged.Source);
                    Equal("现有接口;试管.D20", string.Join(";", merged.Names));
                    Equal(1, server.FileWrites);
                    var bytes = server.Files[CatalogueServer.Marker("试管.D20")];
                    Equal((byte)'{', bytes[0]);
                    var json = new UTF8Encoding(false, true).GetString(bytes);
                    var marker = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                    Equal(2, marker.Count);
                    Equal("1.0", marker["schema_version"]);
                    Equal("试管.D20", marker["name"]);
                    True(json.IndexOf("\"schema_version\"", StringComparison.Ordinal) < json.IndexOf("\"name\"", StringComparison.Ordinal));
                    True(!server.Files.ContainsKey(FileHash.Sha256Text("试管.D20") + ".txt"));
                    catalog.Merge(new[] { "试管.D20" });
                    Equal(1, server.FileWrites);
                    Equal("旧本机接口", string.Join(";", local.Load()));
                }
                finally { Directory.Delete(folder, true); }
            });
            Run("Shared catalogue validates the whole batch and never falls back to local writes", delegate {
                var folder = TempDirectory();
                try
                {
                    var server = new CatalogueServer { FailWritesAfter = 1 };
                    var local = new InterfaceCatalogStore(Path.Combine(folder, "interfaces.json"));
                    local.Merge(new[] { "本地" });
                    var catalog = Catalogue(server, folder, "http://catalog.example");
                    Throws<ValidationException>(() => catalog.Merge(new[] { "one", "two;three" }));
                    Equal(0, server.Requests);
                    Throws<ValidationException>(() => catalog.Merge(new[] { "one", "two" }));
                    Equal(1, server.Files.Count);
                    Equal("本地", string.Join(";", local.Load()));
                    server.FailWritesAfter = int.MaxValue;
                    Equal("one;two", string.Join(";", catalog.Merge(new[] { "one", "two" }).Names));
                    Equal(2, server.FileWrites);
                }
                finally { Directory.Delete(folder, true); }
            });
            Run("Mechanical catalog excludes legacy contents and rejects a new contents entry atomically", delegate {
                var folder = TempDirectory();
                try
                {
                    var server = new CatalogueServer();
                    server.Put("细胞样本");
                    server.Put("溶液");
                    server.Put("样本.细胞");
                    server.Put("样本瓶盖");
                    server.Put("试剂瓶.50ml");
                    var catalog = Catalogue(server, folder, "http://catalog.example");
                    Equal("样本瓶盖;试剂瓶.50ml", string.Join(";", catalog.Load().Names));
                    var requests = server.Requests;
                    Throws<ValidationException>(() => catalog.Merge(new[] { "瓶盖", "内容物.缓冲液" }));
                    Equal(requests, server.Requests);
                    Equal(0, server.FileWrites);
                    Equal(5, server.Files.Count);
                    server.Offline = true;
                    Equal("样本瓶盖;试剂瓶.50ml", string.Join(";", catalog.Load().Names));
                }
                finally { Directory.Delete(folder, true); }
            });
            Run("Online interface cache rejects changed invalid content and stays scoped to its service", delegate {
                var folder = TempDirectory();
                try
                {
                    var server = new CatalogueServer();
                    server.Put("remote");
                    var local = new InterfaceCatalogStore(Path.Combine(folder, "interfaces.json"));
                    local.Merge(new[] { "legacy" });
                    var catalog = Catalogue(server, folder, "http://catalog.example");
                    Equal("remote", string.Join(";", catalog.Load().Names));
                    var invalidMarkers = new[]
                    {
                        Encoding.UTF8.GetBytes("{broken"), CatalogueServer.MarkerBytes("remote", "9.9"),
                        CatalogueServer.MarkerBytes("different-name"), new byte[] { 0xff, 0xfe }
                    };
                    foreach (var invalid in invalidMarkers)
                    {
                        server.Files[CatalogueServer.Marker("remote")] = invalid;
                        server.Stamp++;
                        var rejected = catalog.Load();
                        Equal("local", rejected.Source);
                        Equal("remote", string.Join(";", rejected.Names));
                        True(!string.IsNullOrEmpty(rejected.Warning));
                    }
                    Equal(1 + invalidMarkers.Length, server.FileReads);

                    server.Offline = true;
                    var localFallback = Catalogue(server, folder, "http://different.example").Load();
                    Equal("local", localFallback.Source);
                    Equal("legacy", string.Join(";", localFallback.Names));
                    True(!string.IsNullOrEmpty(localFallback.Warning));
                    Throws<ValidationException>(() => catalog.Merge(new[] { "not-local" }));
                    Equal("legacy", string.Join(";", local.Load()));
                }
                finally { Directory.Delete(folder, true); }
            });
            Run("Refreshing draft source versions does not mutate stored binding endpoints", delegate {
                var old = new BindingEndpoint { Context = "1", AssetId = "asset:1" }; var d = new BindingEditorState(); d.SetParent(old);
                d.RefreshEndpoints(e => new BindingEndpoint { Context = e.Context, AssetId = "asset:2" }); Equal("asset:1", old.AssetId); Equal("asset:2", d.Parent.AssetId);
            });
        }

        private static SharedInterfaceCatalog Catalogue(CatalogueServer server, string folder, string url)
        {
            return new SharedInterfaceCatalog(() => new ExporterSettings { WanxiangBaseUrl = url, WanxiangApiKey = "test-key" },
                () => new DelegateHttpHandler(server.Send), new InterfaceCatalogStore(Path.Combine(folder, "interfaces.json")),
                Path.Combine(folder, "cache"));
        }

        private sealed class CatalogueServer
        {
            private const string Scope = "asset-interface-catalog/v2";
            internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            internal int Requests, FileReads, FileWrites, Stamp;
            internal int FailWritesAfter = int.MaxValue;
            internal bool Offline;

            internal static string Marker(string name)
            {
                return FileHash.Sha256Text(name) + ".json";
            }

            internal static byte[] MarkerBytes(string name, string schema = "1.0")
            {
                var json = new JavaScriptSerializer().Serialize(new { schema_version = schema, name });
                return Encoding.UTF8.GetBytes(json);
            }

            internal void Put(string name)
            {
                Files.Add(Marker(name), MarkerBytes(name));
            }

            internal HttpResponseMessage Send(HttpRequestMessage request)
            {
                lock (Files)
                {
                    Requests++;
                    Equal("Bearer", request.Headers.Authorization.Scheme);
                    Equal("test-key", request.Headers.Authorization.Parameter);
                    if (Offline) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var path = request.RequestUri.AbsolutePath;
                    if (path == "/list/" + Scope && request.Method == HttpMethod.Get)
                    {
                        if (Files.Count == 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        return JsonResponse(HttpStatusCode.OK, Files.Select(pair =>
                            new { name = pair.Key, type = "file", size = pair.Value.LongLength, mtime = (double)Stamp }).ToArray());
                    }
                    True(path.StartsWith("/files/" + Scope + "/", StringComparison.Ordinal));
                    var file = path.Substring(path.LastIndexOf('/') + 1);
                    if (request.Method == HttpMethod.Get)
                    {
                        FileReads++;
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[file]) };
                    }
                    Equal(HttpMethod.Put, request.Method);
                    Equal("application/json", request.Content.Headers.ContentType.MediaType);
                    True(file.EndsWith(".json", StringComparison.Ordinal));
                    if (FileWrites >= FailWritesAfter) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    var overwritten = Files.ContainsKey(file);
                    Files[file] = bytes;
                    FileWrites++;
                    return JsonResponse(overwritten ? HttpStatusCode.OK : HttpStatusCode.Created,
                        new { path = Scope + "/" + file, size = bytes.LongLength, overwritten });
                }
            }

            private static HttpResponseMessage JsonResponse(HttpStatusCode status, object value)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(new JavaScriptSerializer().Serialize(value), Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class InterfaceTargetFake : IInterfacePropertyTarget
        {
            public bool ReadOnly { get; set; } public bool Dirty { get; set; }
            public InterfacePropertySnapshot State = InterfaceSnapshot(); public int Writes, Saves, FailWrite; public bool FailSave;
            public InterfacePropertySnapshot Read()
            {
                var s = new InterfacePropertySnapshot(); foreach (var p in State.Raw) s.Raw.Add(p.Key, p.Value);
                foreach (var p in State.Values) s.Values.Add(p.Key, p.Value); foreach (var p in State.Types) s.Types.Add(p.Key, p.Value); s.Linked.UnionWith(State.Linked); return s;
            }
            public void Set(string name, string value, int type) { Writes++; if (Writes == FailWrite) throw new Exception("write failure"); State.Raw[name] = value; State.Values[name] = value; State.Types[name] = type; Dirty = true; }
            public void Delete(string name) { State.Raw.Remove(name); State.Values.Remove(name); State.Types.Remove(name); }
            public void Save() { Saves++; if (FailSave) throw new Exception("save failure"); Dirty = false; }
        }
    }
}
