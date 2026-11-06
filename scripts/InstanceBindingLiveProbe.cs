using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class InstanceBindingLiveProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var doc = app.ActiveDoc as ModelDoc2;
            Console.WriteLine("SW=" + app.RevisionNumber());
            Console.WriteLine("ACTIVE=" + (doc == null ? "none" : doc.GetPathName()));
            if (args.Contains("--exit-if-saved"))
            {
                var dirty = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>().Where(d => d.GetSaveFlag()).ToList();
                if (dirty.Count != 0) throw new Exception("Cannot restart: unsaved documents exist: " + string.Join(", ", dirty.Select(d => d.GetTitle())));
                app.ExitApp(); Console.WriteLine("EXIT_REQUESTED_ALL_DOCUMENTS_SAVED"); return 0;
            }
            if (args.Contains("--invoke-bindings"))
            {
                var addin = app.GetAddInObject("SolidWorksAssetExporter.AddIn");
                if (addin == null) throw new Exception("The production add-in is not loaded.");
                addin.GetType().InvokeMember("OnInstanceBindings", System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[0]);
                Console.WriteLine("BINDING_COMMAND_DISPATCHED"); return 0;
            }
            if (args.Contains("--fixture-version"))
            {
                var safeRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts")) + Path.DirectorySeparatorChar;
                if (doc == null || !doc.GetPathName().StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Only workspace fixtures may be changed.");
                var owner = SwInstanceBindings.ForDocument(doc);
                try
                {
                    try { owner.Capture(new ExportNode { Id = "root", Kind = ExportNodeKind.Asset, Source = SwAssemblyRoot.FromActiveDocument(app) }); throw new Exception("Root endpoint was accepted."); }
                    catch (ValidationException) { Console.WriteLine("PASS_NATIVE_ROOT_ENDPOINT_GUARD"); }
                }
                finally { owner.Dispose(); }
                var group = (doc.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[]).OfType<Component2>().OrderBy(c => c.Name2).First();
                var tubeComponent = (group.GetChildren() as object[]).OfType<Component2>().First(c => c.GetPathName().EndsWith("binding-at-tube.SLDPRT", StringComparison.OrdinalIgnoreCase));
                var tubeDoc = tubeComponent.GetModelDoc2() as ModelDoc2;
                if (tubeDoc == null || !tubeDoc.GetPathName().StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("The test tube must be loaded from the workspace.");
                var version = int.Parse(args[1], CultureInfo.InvariantCulture);
                tubeDoc.Extension.CustomPropertyManager[""].Add3("asset_version", 30, version.ToString(CultureInfo.InvariantCulture), 2);
                int e = 0, w = 0; if (!tubeDoc.Save3(1, ref e, ref w) || e != 0) throw new Exception("Fixture part save failed " + e);
                if (version > 0) doc.Extension.CustomPropertyManager[""].Add3("assembly_version", 30, "5", 2);
                e = 0; w = 0; if (!doc.Save3(1, ref e, ref w) || e != 0) throw new Exception("Fixture root save failed " + e);
                Console.WriteLine("FIXTURE_ASSET_VERSION=" + version); return 0;
            }
            if (args.Contains("--recovery-copies"))
            {
                var workspaceArtifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts"));
                var recovery = Path.Combine(workspaceArtifacts, "session-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(recovery);
                var captured = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var round = 0; round < 3; round++)
                {
                    var dirty = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>().Where(d => d.GetSaveFlag() && !captured.Contains(d.GetPathName())).ToList();
                    if (dirty.Count == 0) break;
                    foreach (var dirtyDoc in dirty)
                    {
                        var originalPath = dirtyDoc.GetPathName();
                        if (string.IsNullOrWhiteSpace(originalPath)) throw new Exception("An unsaved unnamed document requires manual recovery.");
                        var copy = Path.Combine(recovery, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(originalPath));
                        int a = 0; app.ActivateDoc3(dirtyDoc.GetTitle(), false, 0, ref a);
                        int e = 0, w = 0;
                        if (!dirtyDoc.Extension.SaveAs(copy, 0, 3, null, ref e, ref w) || e != 0 || !File.Exists(copy) || new FileInfo(copy).Length == 0)
                            throw new Exception("Cannot preserve the dirty document: " + originalPath + " error=" + e);
                        if (dirtyDoc.GetPathName() != originalPath) throw new Exception("Copy changed the original document path.");
                        captured.Add(originalPath); Console.WriteLine("RECOVERY=" + copy + " SOURCE=" + originalPath);
                    }
                }
                var remaining = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>().Count(d => d.GetSaveFlag() && !captured.Contains(d.GetPathName()));
                Console.WriteLine("UNBACKED_DIRTY=" + remaining); if (remaining != 0) throw new Exception("Incomplete recovery.");
                if (doc != null) { int a = 0; app.ActivateDoc3(doc.GetTitle(), false, 0, ref a); }
                return 0;
            }
            if (args.Contains("--save-fixture"))
            {
                var fixtureRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts")) + Path.DirectorySeparatorChar;
                if (doc == null || !Path.GetFullPath(doc.GetPathName()).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Only generated workspace fixtures may be saved by this probe.");
                int e = 0, w = 0; if (!doc.Save3(1, ref e, ref w) || e != 0) throw new Exception("Save failed " + e);
                Console.WriteLine("SAVED_FIXTURE=" + doc.GetPathName() + " dirty=" + doc.GetSaveFlag()); return 0;
            }
            if (args.Contains("--select-fixture"))
            {
                var target = app.GetOpenDocumentByName(args[1]) as ModelDoc2;
                if (target == null) throw new Exception("The generated fixture must already be open.");
                int e = 0; app.ActivateDoc3(target.GetTitle(), false, 0, ref e); target.ClearSelection2(true);
                var group = (target.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[]).OfType<Component2>().OrderBy(c => c.Name2).First();
                var children = (group.GetChildren() as object[]).OfType<Component2>().OrderBy(c => c.Name2).ToList();
                var mode = args[2];
                var picked = mode == "children" ? children.Where(c => c.GetPathName().EndsWith("binding-test-tube.SLDPRT", StringComparison.OrdinalIgnoreCase)).ToList() :
                    mode == "group" ? new System.Collections.Generic.List<Component2> { group } :
                    children.Where(c => c.GetPathName().EndsWith("binding-test-rack.SLDPRT", StringComparison.OrdinalIgnoreCase)).Skip(mode == "parent-b" ? 1 : 0).Take(1).ToList();
                foreach (var c in picked) if (!c.Select4(true, null, false)) throw new Exception("Selection failed.");
                Console.WriteLine("SELECTED_COUNT=" + ((SelectionMgr)target.SelectionManager).GetSelectedObjectCount2(-1));
                foreach (var c in picked) Console.WriteLine("SELECTED=" + c.Name2);
                return 0;
            }
            if (args.Contains("--selection"))
            {
                var mgr = (SelectionMgr)doc.SelectionManager;
                Console.WriteLine("SELECTION_COUNT=" + mgr.GetSelectedObjectCount2(-1));
                for (var i = 1; i <= mgr.GetSelectedObjectCount2(-1); i++)
                { var c = mgr.GetSelectedObject6(i, -1) as Component2; Console.WriteLine("SELECTED=" + (c == null ? "non-component" : c.Name2 + " context=" + SwInstanceBindings.Context(c))); }
                return 0;
            }
            if (args.Contains("--build-info"))
            {
                var addin = app.GetAddInObject("SolidWorksAssetExporter.AddIn");
                if (addin == null) throw new Exception("The production add-in is not loaded.");
                Console.WriteLine("BUILD_INFO=" + addin.GetType().InvokeMember("GetBuildInfo", System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[0]));
                return 0;
            }
            if (args.Contains("--open"))
            {
                var target = app.GetOpenDocumentByName(args[1]) as ModelDoc2;
                if (target == null) { int e = 0, w = 0; target = app.OpenDoc6(args[1], 2, 1, "", ref e, ref w) as ModelDoc2; if(target == null) throw new Exception("Open failed: " + e); }
                int error = 0; app.ActivateDoc3(target.GetTitle(), false, 0, ref error); Console.WriteLine("OPENED=" + target.GetPathName()); return 0;
            }
            if (args.Contains("--load-addin"))
            {
                Console.WriteLine("LOAD_ADDIN=" + app.LoadAddIn(typeof(SwAddin).Assembly.Location));
                Console.WriteLine("ADDIN_OBJECT=" + (app.GetAddInObject("SolidWorksAssetExporter.AddIn") != null)); return 0;
            }
            if (args.Contains("--window"))
            {
                if (args.Length > 1)
                {
                    var requested = app.GetOpenDocumentByName(args[1]) as ModelDoc2;
                    if (requested == null) throw new Exception("Requested fixture is not open.");
                    int activateError = 0; app.ActivateDoc3(requested.GetTitle(), false, 0, ref activateError);
                }
                Application.EnableVisualStyles(); Application.Run(new InstanceBindingDialog(app)); return 0;
            }
            foreach (var d in (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>())
                if (d.GetType() == 2 || d.GetSaveFlag()) Console.WriteLine("DOCUMENT=" + d.GetPathName() + " dirty=" + d.GetSaveFlag() + " visible=" + d.Visible);
            if (args.Contains("--documents")) return 0;
            if (doc == null) return 2;
            var session = SwInstanceBindings.ForDocument(doc);
            Console.WriteLine("CONFIG=" + session.Current.ConfigurationName + " id=" + session.Current.ConfigurationId);
            Console.WriteLine("SAVED_RELATIONS=" + session.Current.Relations.Count);
            if (args.Contains("--saved-relations-only"))
            {
                Console.WriteLine("ROOT_DIRTY=" + doc.GetSaveFlag());
                foreach (var relation in session.Current.Relations)
                    Console.WriteLine("RELATION=" + relation.Id + " | " + relation.Parent.DisplayName + " -> " + relation.Child.DisplayName);
                session.Dispose(); return 0;
            }
            var plan = new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)), ProjectMeshFormat.Step, false);
            var nodes = SwInstanceBindings.Flatten(plan.Roots).ToList();
            Console.WriteLine("NODES=" + nodes.Count + " ASSETS=" + nodes.Count(n => n.Kind == ExportNodeKind.Asset));
            foreach (var n in nodes.Where(n => n.Kind == ExportNodeKind.Asset).Take(12))
            {
                try
                {
                    var e = session.Capture(n);
                    var resolved = session.Resolve(e, plan);
                    string connection, accepts;
                    n.Properties.TryGetValue(PropertyRules.ConnectionInterface, out connection);
                    n.Properties.TryGetValue(PropertyRules.AcceptsInterfaces, out accepts);
                    Console.WriteLine("ASSET=" + n.Source.InstancePath + " node=" + n.Id + " asset=" + n.AssetId + " ref_bytes=" + Convert.FromBase64String(e.Reference).Length +
                        " context=" + e.Context + " connection=" + connection + " accepts=" + accepts + " roundtrip=" + (resolved.Id == n.Id));
                }
                catch (Exception ex) { Console.WriteLine("ASSET_ERROR=" + n.Source.InstancePath + " " + ex.Message); }
            }
            Console.WriteLine("PASS_READ_ONLY_PROBE");
            session.Dispose();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("LIVE_BINDING_ERROR=" + ex); return 1; }
    }
}
