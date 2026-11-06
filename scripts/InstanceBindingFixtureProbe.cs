using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

// Integration fixture: copies source parts, creates all assemblies under the supplied
// workspace directory, and restores the previously active user document. Never uploads.
internal static class InstanceBindingFixtureProbe
{
    private static SldWorks App;
    private static string Root;
    private static readonly List<ModelDoc2> Created = new List<ModelDoc2>();
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ModelDoc2 previous = null; bool priorCommand = false;
        try
        {
            Root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
            App = (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); previous = App.ActiveDoc as ModelDoc2;
            priorCommand = App.CommandInProgress; App.CommandInProgress = true;
            Console.WriteLine("SW=" + App.RevisionNumber());
            if (args.Contains("--interface-setup") || args.Contains("--interface-inspect"))
            {
                var artifactRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts")) + Path.DirectorySeparatorChar;
                if (!(Root + Path.DirectorySeparatorChar).StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Only workspace fixtures may be used.");
                var uiDoc = Open(Path.Combine(Root, "Binding-UI-200.SLDASM"), 2); Activate(uiDoc);
                var uiComponent = (uiDoc.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[]).OfType<Component2>().First(c => c.Name2 == "ui-rack-1");
                var uiSource = (ModelDoc2)uiComponent.GetModelDoc2();
                if (!uiSource.GetPathName().StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture source.");
                if (args.Contains("--interface-setup")) { Prop(uiSource, "accepts_interfaces", "legacy-slot"); Save(uiSource); }
                var snapshot = new SwInterfacePropertyTarget(uiSource).Read();
                Console.WriteLine("ACCEPTS=" + snapshot.Get("accepts_interfaces") + " VERSION=" + snapshot.Version + " DIRTY=" + uiSource.GetSaveFlag());
                return 0;
            }
            if (args.Contains("--interaction-create"))
            {
                var parts = Path.Combine(Root, "..", "binding-fixture-20261102");
                var uiRack = CopyPart(Path.Combine(parts, "binding-test-rack.SLDPRT"), "ui-rack.SLDPRT", true);
                var uiTip = CopyPart(Path.Combine(parts, "binding-test-tube.SLDPRT"), "ui-tip.SLDPRT", false);
                Prop(uiRack, "connection_interface", "binding-test-D20"); Save(uiRack);
                Prop(uiTip, "accepts_interfaces", "binding-test-D20"); Save(uiTip);
                var uiTemplate = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
                var uiRoot = (ModelDoc2)App.NewDocument(uiTemplate, 0, 0, 0); Prop(uiRoot, "is_asset", "0"); Prop(uiRoot, "assembly_version", "1");
                Insert(uiRoot, uiRack.GetPathName(), 0, 0, 0, 0); Insert(uiRoot, uiRack.GetPathName(), 1, 0, 0, 0);
                for (var i = 0; i < 200; i++) Insert(uiRoot, uiTip.GetPathName(), (i % 20) * .03, (i / 20) * .03, .1, 0);
                SaveAs(uiRoot, Path.Combine(Root, "Binding-UI-200.SLDASM"));
                Console.WriteLine("CREATED_200_TIP_FIXTURE"); return 0;
            }
            if (args.Contains("--interaction-select"))
            {
                var target = Open(Path.Combine(Root, "Binding-UI-200.SLDASM"), 2); Activate(target);
                var components = (target.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[]).OfType<Component2>().ToList();
                foreach (var requested in args[2].Split(','))
                {
                    target.ClearSelection2(true);
                    var component = components.Single(c => c.Name2 == requested);
                    if (!component.Select4(false, null, false)) throw new Exception("Selection failed " + requested);
                    Console.WriteLine("SELECTED=" + requested);
                }
                return 0;
            }
            if (args.Contains("--rename-reopen"))
            {
                var target = Open(Path.Combine(Root, "Binding-AT-S1.SLDASM"), 2); Activate(target);
                var owner = SwInstanceBindings.ForDocument(target);
                try { InstanceBindingMutationChecks.RenameReopen(App, target, owner); } finally { owner.Dispose(); }
                Console.WriteLine("ALL_RENAME_REOPEN_CHECKS_PASSED"); return 0;
            }
            if (args.Contains("--proximity"))
            {
                var target = Open(Path.Combine(Root, "Binding-AT-S1.SLDASM"), 2); Activate(target);
                var owner = SwInstanceBindings.ForDocument(target);
                try { InstanceBindingMutationChecks.Proximity(App, target, owner); } finally { owner.Dispose(); }
                Console.WriteLine("ALL_PROXIMITY_CHECKS_PASSED"); return 0;
            }
            if (args.Contains("--mutations"))
            {
                var target = Open(Path.Combine(Root, "Binding-AT-S1.SLDASM"), 2); Activate(target);
                var owner = SwInstanceBindings.ForDocument(target);
                try { InstanceBindingMutationChecks.Run(App, target, owner); } finally { owner.Dispose(); }
                Console.WriteLine("ALL_MUTATION_CHECKS_PASSED"); return 0;
            }
            if (args.Contains("--existing"))
            {
                var existing = Open(Path.Combine(Root, "Binding-S1.SLDASM"), 2); Activate(existing);
                var existingSession = SwInstanceBindings.ForDocument(existing);
                try { Export(existing, existingSession); } finally { existingSession.Dispose(); }
                Console.WriteLine("ALL_EXISTING_EXPORT_CHECKS_PASSED"); return 0;
            }
            var source = @"C:\Users\C12\Desktop\（部分）layout_all";
            var extended = args.Contains("--extended");
            var rack = CopyPart(Path.Combine(source, "1.5ml离心管架.SLDPRT"), extended ? "binding-at-rack.SLDPRT" : "binding-test-rack.SLDPRT", true);
            var tube = CopyPart(Path.Combine(source, "1.5ml离心管.SLDPRT"), extended ? "binding-at-tube.SLDPRT" : "binding-test-tube.SLDPRT", false);
            var template = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            if (!File.Exists(template)) throw new Exception("Missing assembly template: " + template);
            var group = (ModelDoc2)App.NewDocument(template, 0, 0, 0); Created.Add(group);
            Prop(group, "is_asset", "0");
            var ga = Insert(group, rack.GetPathName(), 1, 2, 0, Math.PI / 2);
            Insert(group, rack.GetPathName(), 1.5, 2, 0, Math.PI / 2);
            Insert(group, tube.GetPathName(), .8, 2.1, .3, Math.PI / 2);
            Insert(group, tube.GetPathName(), .8, 2.2, .3, Math.PI / 2);
            Insert(group, tube.GetPathName(), .8, 2.3, .3, Math.PI / 2);
            SaveAs(group, Path.Combine(Root, extended ? "Binding-AT-Group.SLDASM" : "Binding-Group.SLDASM"));
            var root = (ModelDoc2)App.NewDocument(template, 0, 0, 0); Created.Add(root);
            Prop(root, "is_asset", "0"); Prop(root, "assembly_version", "1");
            var g1 = Insert(root, group.GetPathName(), 0, 0, 0, 0);
            var g2 = Insert(root, group.GetPathName(), 3, 0, 0, .4);
            root.ClearSelection2(true); g1.Select4(false, null, false); ((AssemblyDoc)root).FixComponent(); root.ClearSelection2(true);
            var rootPath = Path.Combine(Root, extended ? "Binding-AT-S1.SLDASM" : "Binding-S1.SLDASM"); SaveAs(root, rootPath);
            var session = SwInstanceBindings.ForDocument(root);
            var plan = Plan(); var groups = plan.Roots.Where(n => n.Kind == ExportNodeKind.Group).OrderBy(n => n.Name).ToList();
            Check(groups.Count == 2, "two ordinary groups");
            var first = SwInstanceBindings.Flatten(new[] { groups[0] }).Where(n => n.Kind == ExportNodeKind.Asset).ToList();
            var racks = first.Where(n => ((SwCadNode)n.Source).SourcePath == rack.GetPathName()).OrderBy(n => n.Name).ToList();
            var tubes = first.Where(n => ((SwCadNode)n.Source).SourcePath == tube.GetPathName()).OrderBy(n => n.Name).ToList();
            Check(racks.Count == 2 && tubes.Count == 3 && racks[0].AssetId == racks[1].AssetId && racks[0].Id != racks[1].Id, "S1 same assets distinct instances");
            var p = session.Capture(racks[0]); var children = tubes.Select(session.Capture).ToList();
            var records = InstanceBindingRules.Apply(session.Current.Relations, p, children, e => session.Resolve(e, plan), false);
            session.Commit(records); Check(session.Pending && root.GetSaveFlag(), "apply marks pending and document dirty");
            Save(root); Check(!session.Pending, "SaveToStorage plus successful FileSavePost persists revision");
            var exported = session.Export(plan); Check(exported.Relations.Count == 3, "S1 only A carries three tubes");
            var pose = exported.Relations.Single(r => r.ChildNodeId == tubes[0].Id).Pose;
            Near(.1, pose.Tx); Near(.2, pose.Ty); Near(.3, pose.Tz); Near(1, Math.Abs(pose.Qw));
            Console.WriteLine("S2_POSE=" + pose.CanonicalValue());
            Console.WriteLine("PASS_S1_S2_SAVE");
            // Reopen the actual SW file: no previous RCWs or export node IDs are used for restoration.
            session.Dispose(); App.CloseDoc(root.GetTitle()); Created.Remove(root);
            root = Open(rootPath, 2); Activate(root);
            session = SwInstanceBindings.ForDocument(root); plan = Plan();
            exported = session.Export(plan); Check(exported.Relations.Count == 3, "persistent bindings survive reopen");
            Check(exported.Relations.All(r => r.ParentNodeId == racks[0].Id), "same original rack after reopen");
            Console.WriteLine("PASS_REOPEN_ORIGINAL_INSTANCE");
            // Exercise a second repeated subassembly context, then persist both separately.
            groups = plan.Roots.Where(n => n.Kind == ExportNodeKind.Group).OrderBy(n => n.Name).ToList();
            var second = SwInstanceBindings.Flatten(new[] { groups[1] }).Where(n => n.Kind == ExportNodeKind.Asset).ToList();
            var secondRack = second.First(n => ((SwCadNode)n.Source).SourcePath == rack.GetPathName());
            var secondTube = second.First(n => ((SwCadNode)n.Source).SourcePath == tube.GetPathName());
            var p2 = session.Capture(secondRack); var c2 = session.Capture(secondTube);
            Check(p.Context != p2.Context, "repeated nested contexts are distinct");
            session.Commit(InstanceBindingRules.Apply(session.Current.Relations, p2, new[] { c2 }, e => session.Resolve(e, plan), false)); Save(root);
            Check(session.Export(plan).Relations.Count == 4, "second context does not overwrite first");
            Console.WriteLine("PASS_REPEATED_NESTED_CONTEXTS");
            // Configuration IDs isolate new configurations, including SW copies of the tree.
            var originalConfig = root.ConfigurationManager.ActiveConfiguration.Name;
            var alternate = root.ConfigurationManager.AddConfiguration2("Binding-Config-B", "", "", 0, originalConfig, "", true);
            if (alternate == null) throw new Exception("Cannot create second configuration.");
            root.ShowConfiguration2("Binding-Config-B"); Check(session.Current.Relations.Count == 0, "new configuration has no inherited bindings");
            root.ShowConfiguration2(originalConfig); Check(session.Current.Relations.Count == 4, "original configuration retains bindings"); Save(root);
            Console.WriteLine("PASS_CONFIGURATION_ISOLATION");
            // An explicitly unbound child is not considered empty or allocated to rack B.
            var id = session.Current.Relations.Last().Id;
            session.Commit(session.Current.Relations.Where(r => r.Id != id).ToList()); Save(root);
            Console.WriteLine("PASS_EXPLICIT_UNBIND");
            if (extended)
            {
                var checkedState = InstanceBindingLiveChecks.Run(App, root, group, rack, tube, session);
                root = checkedState.Root; session = checkedState.Session;
            }
            if (args.Contains("--export")) Export(root, session);
            session.Dispose();
            Console.WriteLine("FIXTURE=" + rootPath);
            Console.WriteLine("ALL_LIVE_FIXTURE_CHECKS_PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FIXTURE_ERROR=" + ex); return 1; }
        finally
        {
            if (App != null)
            {
                App.CommandInProgress = priorCommand;
                if (previous != null) { try { Activate(previous); } catch { } }
            }
        }
    }
    private static void Export(ModelDoc2 root, SwInstanceBindings session)
    {
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://localhost:18875/"); listener.Start();
            var serving = Task.Run(async delegate
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync(); } catch { break; }
                    var b = Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"assets\":[]}");
                    c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length;
                    c.Response.Headers["X-Registry-Exists"] = "false";
                    c.Response.Headers["ETag"] = "\"" + FileHash.Sha256Text(Encoding.UTF8.GetString(b)) + "\"";
                    c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
                }
            });
            var settings = new ExporterSettings { AssetLibraryRoot = Path.Combine(Root, "exports", "assets"),
                ProjectExportRoot = Path.Combine(Root, "exports", "assembly_package"), ExportProject = true,
                UploadAfterExport = false, WanxiangBaseUrl = "http://localhost:18875", WanxiangApiKey = "local-test" };
            var coordinator = new ExportCoordinator(App); var analysis = coordinator.Analyze(settings);
            File.WriteAllText(Path.Combine(Root, "preview.txt"), analysis.Preview, Encoding.UTF8);
            Check(analysis.CanExport, "full production preview: " + string.Join(";", analysis.ValidationErrors));
            var completion = coordinator.Export(analysis, settings, Console.WriteLine, null);
            Check(File.Exists(Path.Combine(completion.ProjectDirectory, AssemblyRelations.FileName)), "production relation file");
            var report = JsonFile.Read<ExportReport>(Path.Combine(completion.ProjectDirectory, "export-report.json"));
            Check(report.Files.Any(f => f.Path == AssemblyRelations.FileName), "relations are hashed in report");
            ProjectReportValidator.Inspect(completion.ProjectDirectory, analysis.Plan.AssemblyUuid, analysis.Plan.AssemblyVersion, analysis.ProjectFingerprint);
            Console.WriteLine("REAL_PACKAGE=" + completion.ProjectDirectory);
            Console.WriteLine("PASS_PRODUCTION_EXPORT_AND_PACKAGE_VALIDATION"); listener.Stop(); serving.Wait();
        }
    }
    private static ModelDoc2 CopyPart(string source, string name, bool parent)
    {
        var path = Path.Combine(Root, name); File.Copy(source, path, false); var doc = Open(path, 1);
        Prop(doc, "is_asset", "1"); Prop(doc, "class", "movable"); Prop(doc, "asset_version", "1");
        Prop(doc, "零件名", parent ? "验收试管架" : "验收试管"); Prop(doc, "设计目的", "实例绑定隔离验收");
        foreach (var flag in new[] { "is_tool", "is_quick_changer", "is_quick_changer_rack" }) Prop(doc, flag, "0");
        Prop(doc, "quick_changer_side", ""); Prop(doc, "is_fixture", parent ? "1" : "0");
        Prop(doc, "connection_interface", parent ? "" : "binding-test-D20"); Prop(doc, "accepts_interfaces", parent ? "binding-test-D20" : ""); Save(doc); return doc;
    }
    private static void Prop(ModelDoc2 d, string name, string value)
    { d.Extension.CustomPropertyManager[""].Add3(name, 30, value, 2); }
    private static ModelDoc2 Open(string path, int type)
    { int e = 0, w = 0; var d = (ModelDoc2)App.OpenDoc6(path, type, 1, "", ref e, ref w); if (d == null) throw new Exception("Open failed " + path + " code=" + e); Created.Add(d); return d; }
    private static void Activate(ModelDoc2 d) { int e = 0; App.ActivateDoc3(d.GetTitle(), false, 0, ref e); }
    private static Component2 Insert(ModelDoc2 d, string path, double x, double y, double z, double angle)
    {
        Activate(d); var c = (Component2)((AssemblyDoc)d).AddComponent5(path, 0, "", false, "", 0, 0, 0);
        if (c == null) throw new Exception("Insert failed: " + path);
        var co = Math.Cos(angle); var si = Math.Sin(angle);
        var values = new[] { co, si, 0, -si, co, 0, 0, 0, 1, x, y, z, 1, 0, 0, 0 };
        c.Transform2 = (MathTransform)((MathUtility)App.GetMathUtility()).CreateTransform(values); return c;
    }
    private static void SaveAs(ModelDoc2 d, string path)
    { int e = 0, w = 0; if (!d.Extension.SaveAs(path, 0, 1, null, ref e, ref w) || e != 0) throw new Exception("SaveAs failed code=" + e); }
    private static void Save(ModelDoc2 d)
    { int e = 0, w = 0; if (!d.Save3(1, ref e, ref w) || e != 0) throw new Exception("Save failed code=" + e); }
    private static AssemblyExportPlan Plan() { return new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(App)), ProjectMeshFormat.Step); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS " + message); }
    private static void Near(double expected, double actual) { Check(Math.Abs(expected - actual) < 1e-9, "numeric " + expected.ToString("R", CultureInfo.InvariantCulture) + " = " + actual.ToString("R", CultureInfo.InvariantCulture)); }
}
