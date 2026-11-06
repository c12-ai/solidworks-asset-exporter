using System;
using System.Collections.Generic;
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

// Real SOLIDWORKS mutation tests. Every file changed here belongs to the newly
// generated workspace fixture; source user documents are never edited.
internal static class InstanceBindingLiveChecks
{
    private static SldWorks App;
    private static string RackPath, TubePath;
    public sealed class State { public ModelDoc2 Root; public SwInstanceBindings Session; }
    public static State Run(SldWorks app, ModelDoc2 root, ModelDoc2 group, ModelDoc2 rack, ModelDoc2 tube, SwInstanceBindings session)
    {
        App = app; RackPath = rack.GetPathName(); TubePath = tube.GetPathName();
        var configA = root.ConfigurationManager.ActiveConfiguration.Name;
        var plan = Plan(); var p = Pick(plan, RackPath, 0, 0); var b = Pick(plan, RackPath, 0, 1);
        var t1 = Pick(plan, TubePath, 0, 0); var t2 = Pick(plan, TubePath, 0, 1);
        var other = Pick(plan, TubePath, 1, 0);
        Apply(session, plan, b, false, t1); Save(root);
        Check(session.Export(plan).Relations.Count(r => r.ChildNodeId == t1.Id) == 2, "AT07 multiple parents preserved");
        Apply(session, plan, b, false, t1); Check(!session.Pending, "AT07 shared child idempotence");
        Apply(session, plan, b, true, t1); Check(session.Pending && root.GetSaveFlag(), "AT15 reparent dirty"); Save(root);
        Check(session.Export(plan).Relations.Single(r => r.ChildNodeId == t1.Id).ParentNodeId == b.Id, "AT07 explicit reparent");
        Apply(session, plan, p, true, t1); Save(root);
        Apply(session, plan, p, false, t1); Check(!session.Pending && !root.GetSaveFlag(), "AT06 native idempotence");
        var t1Context = SwInstanceBindings.Context(((SwCadNode)t1.Source).Component);
        session.Commit(session.Current.Relations.Where(r => r.Child.Context != t1Context).ToList());
        Check(session.Pending && root.GetSaveFlag() && session.Current.Relations.Count == 2, "AT06 AT15 specific unbind dirty"); Save(root);
        Apply(session, plan, p, false, t1); Save(root);

        // All connections are made legal before testing graph cycles, so an interface
        // failure cannot masquerade as cycle detection.
        Prop(rack, "connection_interface", "binding-test-D20"); Prop(tube, "accepts_interfaces", "binding-test-D20"); Save(rack); Save(tube); Save(root);
        plan = Plan(); p = Pick(plan, RackPath, 0, 0); t1 = Pick(plan, TubePath, 0, 0); t2 = Pick(plan, TubePath, 0, 1);
        Reject(() => Apply(session, plan, p, false, p), "AT09 self");
        Apply(session, plan, t1, true, t2); Save(root);
        Reject(() => Apply(session, plan, t2, false, p), "AT09 long cycle");
        Check(session.Current.Relations.Count == 3, "AT09 graph unchanged after rejection");
        Apply(session, plan, p, true, t2); Save(root);

        // Cross different ordinary CAD parents, with a separately rotated second group.
        other = Pick(plan, TubePath, 1, 0); Apply(session, plan, p, false, other); Save(root);
        var relation = session.Export(plan).Relations.Single(r => r.ChildNodeId == other.Id);
        Near(.8 * Math.Sin(.4) + 2.1 * Math.Cos(.4) - 2, relation.Pose.Tx, "AT23 cross-group x");
        Near(-(2 + .8 * Math.Cos(.4) - 2.1 * Math.Sin(.4)), relation.Pose.Ty, "AT23 cross-group y");
        Near(Math.Sin(.2), relation.Pose.Qz, "AT23 cross-group qz");
        var groups = plan.Roots.OrderBy(n => n.Name).ToList();
        var g1 = ((SwCadNode)groups[0].Source).Component; var g2 = ((SwCadNode)groups[1].Source).Component;
        var original1 = g1.Transform2; var original2 = g2.Transform2;
        g2.Transform2 = Transform(3, 0, 0, Math.PI / 2);
        var pose = session.Export(plan).Relations.Single(r => r.ChildNodeId == other.Id).Pose;
        Near(Math.Sqrt(.5), Math.Abs(pose.Qz), "AT24 relative quarter turn qz"); Near(Math.Sqrt(.5), Math.Abs(pose.Qw), "AT24 relative quarter turn qw");
        var outer = Transform(6, -4, 2, -.63);
        g1.Transform2 = Multiply(outer, original1); g2.Transform2 = Multiply(outer, Transform(3, 0, 0, Math.PI / 2));
        var moved = session.Export(plan).Relations.Single(r => r.ChildNodeId == other.Id).Pose;
        Near(pose.Tx, moved.Tx, "AT24 outer-invariant x"); Near(pose.Ty, moved.Ty, "AT24 outer-invariant y"); Near(pose.Tz, moved.Tz, "AT24 outer-invariant z"); Near(pose.Qz, moved.Qz, "AT24 outer-invariant qz");
        g1.Transform2 = original1; g2.Transform2 = original2;
        session.Commit(session.Current.Relations.Where(r => r.Child.Context != SwInstanceBindings.Context(((SwCadNode)other.Source).Component)).ToList()); Save(root);

        Prop(tube, "connection_interface", ""); Save(tube); Save(root); Reject(() => session.Export(Plan()), "AT11 missing interface after bind");
        Prop(tube, "connection_interface", "mismatch"); Save(tube); Save(root); Reject(() => session.Export(Plan()), "AT11 mismatching interface after bind");
        Prop(tube, "connection_interface", "binding-test-D20"); Save(tube);
        Prop(rack, "class", "station"); Prop(rack, "is_quick_changer_rack", "1"); Save(rack); Save(root); Reject(() => session.Export(Plan()), "AT11 forbidden role after bind");
        Prop(rack, "class", "movable"); Prop(rack, "is_quick_changer_rack", "0"); Prop(rack, "connection_interface", ""); Save(rack); Save(root);
        Check(session.Export(Plan()).Relations.Count == 3, "AT11 parent own interface optional");

        plan = Plan(); var child = session.ResolveComponent(session.Current.Relations[0].Child);
        child.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
        Reject(() => session.Export(plan), "AT19 hidden"); child.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
        var suppression = child.GetSuppression2(); child.SetSuppression2((int)swComponentSuppressionState_e.swComponentSuppressed);
        Reject(() => session.Export(plan), "AT19 suppressed"); child.SetSuppression2(suppression);
        root.ClearSelection2(true); child.Select4(false, null, false);
        Check(((AssemblyDoc)root).CompConfigProperties6(2, 0, true, true, child.ReferencedConfiguration, false, true, 0), "set native envelope");
        Check(child.IsEnvelope(), "native envelope state"); Reject(() => session.Export(plan), "AT19 envelope");
        root.ClearSelection2(true); child.Select4(false, null, false);
        Check(((AssemblyDoc)root).CompConfigProperties6(2, 0, true, true, child.ReferencedConfiguration, false, false, 0), "restore envelope");
        root.ClearSelection2(true); Save(group); Save(root);
        Prop(tube, "is_asset", "0"); Save(tube); Save(root); Reject(() => session.Export(Plan()), "AT19 lost independent Asset");
        Prop(tube, "is_asset", "1"); Save(tube); Save(root);
        Check(session.Export(Plan()).Relations.Count == 3, "AT19 records retained and recoverable");

        Activate(rack); Reject(() => new InstanceBindingDialog(App), "AT01 part document"); Activate(root);
        plan = Plan(); p = Pick(plan, RackPath, 0, 0); b = Pick(plan, RackPath, 0, 1); t1 = Pick(plan, TubePath, 0, 0);
        Reject(() => session.Capture(plan.Roots[0]), "AT03 group endpoint");
        Prop(tube, "class", "robot"); Prop(tube, "设计目的", "binding-test-robot"); Save(tube); Save(root);
        var robotPlan = Plan(); Reject(() => session.Capture(SwInstanceBindings.Flatten(robotPlan.Roots).First(n => n.Kind == ExportNodeKind.Robot)), "AT03 robot endpoint");
        Prop(tube, "class", "movable"); Prop(tube, "设计目的", "实例绑定隔离验收"); Save(tube); Save(root);
        Prop(group, "is_asset", "1"); Prop(group, "class", "movable"); Prop(group, "asset_version", "1"); Save(group); Save(root);
        Check(SwInstanceBindings.Flatten(Plan().Roots).Count() == 2, "AT03 opaque Asset boundary not expanded");
        Reject(() => session.Export(Plan()), "AT03 internal endpoints unavailable");
        Prop(group, "is_asset", "0"); Save(group); Save(root);

        // Rename the occurrence while keeping the persistence identity unchanged.
        plan = Plan(); p = Pick(plan, RackPath, 0, 0); var parentComponent = ((SwCadNode)p.Source).Component;
        var oldName = parentComponent.Name2; parentComponent.Name2 = "binding-at-rack-renamed"; root.EditRebuild3(); Save(group); Save(root);
        Check(session.Export(Plan()).Relations.Count == 3, "AT16 rename and rebuild preserve original occurrence");
        parentComponent.Name2 = oldName.Substring(oldName.LastIndexOf('/') + 1); Save(group); Save(root);

        var beforeDiscard = session.Current.Relations.ToList();
        session.Commit(new List<InstanceBinding>()); Check(session.Pending, "AT08 pending before discard");
        var rootPath = root.GetPathName(); session.Dispose(); App.CloseDoc(root.GetTitle()); root = Open(rootPath, false); Activate(root);
        session = SwInstanceBindings.ForDocument(root); Check(session.Current.Relations.Count == 3, "AT08 real close without save discards edits");

        // Make a real save fail while the document was opened writable; retain pending state.
        var attributes = File.GetAttributes(rootPath);
        session.Commit(new List<InstanceBinding>()); File.SetAttributes(rootPath, attributes | FileAttributes.ReadOnly);
        try
        {
            int errors = 0, warnings = 0; var saved = root.Save3(1, ref errors, ref warnings);
            Check(!saved || errors != 0, "AT15 controlled native save failure");
            Check(session.Pending, "AT15 failure retains pending revision");
        }
        finally { File.SetAttributes(rootPath, attributes); }
        Save(root); Check(!session.Pending, "AT15 retry successfully persists");
        session.Commit(beforeDiscard); Save(root);

        // Both configurations now contain intentionally different, persisted graphs.
        root.ShowConfiguration2("Binding-Config-B"); Save(root); Check(session.Current.Relations.Count == 0, "AT17 B starts empty");
        plan = Plan(); var bp = Pick(plan, RackPath, 0, 1);
        Apply(session, plan, bp, false, SwInstanceBindings.Flatten(new[] { plan.Roots.OrderBy(n => n.Name).First() }).Where(n => n.Kind == ExportNodeKind.Asset && ((SwCadNode)n.Source).SourcePath == TubePath).ToArray()); Save(root);
        var bParents = session.Export(plan).Relations.Select(r => r.ParentNodeId).Distinct().ToList(); Check(bParents.Count == 1 && bParents[0] == bp.Id, "AT17 B binds to B");
        root.ShowConfiguration2(configA); Save(root); Check(session.Current.Relations.Count == 3, "AT17 A graph retained");
        session.Dispose(); App.CloseDoc(root.GetTitle()); root = Open(rootPath, false); Activate(root); session = SwInstanceBindings.ForDocument(root);
        Check(session.Current.Relations.Count == 3, "AT17 A graph survives reopen"); root.ShowConfiguration2("Binding-Config-B"); Check(session.Current.Relations.Count == 3, "AT17 B graph survives reopen");
        root.ShowConfiguration2(configA); Save(root);

        // Open read-only and prove the editor cannot write before any declarations change.
        session.Dispose(); App.CloseDoc(root.GetTitle()); root = Open(rootPath, true); Activate(root); session = SwInstanceBindings.ForDocument(root);
        Check(root.IsOpenedReadOnly(), "native read-only flag"); Reject(() => session.RequireWritable(), "AT01 read-only write guard");
        session.Dispose(); App.CloseDoc(root.GetTitle()); root = Open(rootPath, false); Activate(root); session = SwInstanceBindings.ForDocument(root);
        Check(session.Current.Relations.Count == 3, "AT01 guard preserves saved graph");
        VersionChecks(root, session);
        Console.WriteLine("ALL_EXTENDED_NATIVE_CHECKS_PASSED");
        return new State { Root = root, Session = session };
    }

    private static void VersionChecks(ModelDoc2 root, SwInstanceBindings session)
    {
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://localhost:18876/"); listener.Start();
            var server = Task.Run(async delegate
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { break; }
                    const string json = "{\"schema_version\":\"1.0\",\"assets\":[]}"; var bytes = Encoding.UTF8.GetBytes(json);
                    c.Response.Headers["X-Registry-Exists"] = "false"; c.Response.Headers["ETag"] = "\"" + FileHash.Sha256Text(json) + "\"";
                    c.Response.ContentLength64 = bytes.Length; c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close();
                }
            });
            var directory = Path.Combine(Path.GetDirectoryName(root.GetPathName()), "version-check");
            var settings = new ExporterSettings { AssetLibraryRoot = Path.Combine(directory, "assets"), ProjectExportRoot = Path.Combine(directory, "assembly_package"),
                WanxiangBaseUrl = "http://localhost:18876", WanxiangApiKey = "local-test", UploadAfterExport = false };
            var coordinator = new ExportCoordinator(App); var preview = coordinator.Analyze(settings); Check(preview.CanExport, "AT28 initial preview");
            var baseline = session.Current.Relations.ToList(); session.Commit(baseline.Skip(1).ToList());
            Reject(() => coordinator.Export(preview, settings), "AT29 pending binding rejects old preview"); Save(root);
            Reject(() => coordinator.Export(preview, settings), "AT29 saved revision rejects old preview");
            session.Commit(baseline); Save(root); preview = coordinator.Analyze(settings); var exported = coordinator.Export(preview, settings);
            session.Commit(baseline.Skip(1).ToList()); Save(root); var changed = coordinator.Analyze(settings);
            Check(!changed.CanExport && !string.IsNullOrEmpty(changed.ProjectVersionError), "AT28 binding change requires new package version");
            Prop(root, "assembly_version", "2"); Save(root); var upgraded = coordinator.Analyze(settings);
            Check(upgraded.CanExport, "AT28 upgraded version accepted"); coordinator.Export(upgraded, settings);
            session.Commit(baseline); Prop(root, "assembly_version", "3"); Save(root);
            listener.Stop(); server.Wait();
        }
    }

    private static AssemblyExportPlan Plan() { return new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(App)), ProjectMeshFormat.Step); }
    private static ExportNode Pick(AssemblyExportPlan plan, string path, int groupIndex, int instanceIndex)
    { return SwInstanceBindings.Flatten(new[] { plan.Roots.OrderBy(n => n.Name).ElementAt(groupIndex) }).Where(n => n.Source is SwCadNode && ((SwCadNode)n.Source).SourcePath == path).OrderBy(n => n.Name).ElementAt(instanceIndex); }
    private static void Apply(SwInstanceBindings s, AssemblyExportPlan p, ExportNode parent, bool replace, params ExportNode[] children)
    { s.Commit(InstanceBindingRules.Apply(s.Current.Relations, s.Capture(parent), children.Select(s.Capture), e => s.Resolve(e, p), replace)); }
    private static void Prop(ModelDoc2 doc, string key, string value) { doc.Extension.CustomPropertyManager[""].Add3(key, 30, value, 2); }
    private static void Save(ModelDoc2 doc) { int e = 0, w = 0; if (!doc.Save3(1, ref e, ref w) || e != 0) throw new Exception("Save failed: " + doc.GetTitle() + " error=" + e); }
    private static ModelDoc2 Open(string path, bool readOnly) { int e = 0, w = 0; var d = App.OpenDoc6(path, 2, readOnly ? 3 : 1, "", ref e, ref w) as ModelDoc2; if (d == null) throw new Exception("Open failed " + e); return d; }
    private static void Activate(ModelDoc2 doc) { int e = 0; App.ActivateDoc3(doc.GetTitle(), false, 0, ref e); }
    private static MathTransform Transform(double x, double y, double z, double angle)
    { var c = Math.Cos(angle); var s = Math.Sin(angle); return (MathTransform)((MathUtility)App.GetMathUtility()).CreateTransform(new[] { c, s, 0, -s, c, 0, 0, 0, 1, x, y, z, 1, 0, 0, 0 }); }
    private static MathTransform Multiply(MathTransform left, MathTransform right)
    {
        // SW stores row-vector matrices; applying the outer left column transform is
        // represented by right.Multiply(left) in the native MathTransform convention.
        return (MathTransform)right.Multiply(left);
    }
    private static void Reject(Action action, string name)
    { try { action(); } catch (ValidationException ex) { Console.WriteLine("PASS " + name + " blocked=" + ex.Message.Replace('\n', ' ')); return; } throw new Exception("FAIL expected rejection: " + name); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
    private static void Near(double expected, double actual, string name) { Check(Math.Abs(expected - actual) < 1e-9, name + " expected=" + expected.ToString("R") + " actual=" + actual.ToString("R")); }
}
