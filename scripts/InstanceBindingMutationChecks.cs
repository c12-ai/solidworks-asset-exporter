using System;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class InstanceBindingMutationChecks
{
    private static SldWorks App;
    private static ModelDoc2 Root, Group;
    private static SwInstanceBindings Owner;
    public static void RenameReopen(SldWorks app, ModelDoc2 root, SwInstanceBindings owner)
    {
        App = app; Root = root; Owner = owner;
        Group = App.GetOpenDocumentByName(Path.Combine(Path.GetDirectoryName(root.GetPathName()), "Binding-AT-Group.SLDASM")) as ModelDoc2;
        Save(Group); Save(Root);
        var record = owner.Current.Relations.First(); var node = owner.Resolve(record.Parent, Plan());
        var component = ((SwCadNode)node.Source).Component;
        var originalName = Path.GetFileNameWithoutExtension(((SwCadNode)node.Source).SourcePath);
        var renamePreference = App.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames);
        App.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames, false);
        try
        {
        var renamed = Rename(Root, component, "Binding-Renamed-Rack");
        Check(renamed.IndexOf("Binding-Renamed-Rack", StringComparison.OrdinalIgnoreCase) >= 0, "AT16 actual native rename applied");
        Save(Group); Root.EditRebuild3(); Save(Root);
        var path = Root.GetPathName(); owner.Dispose(); App.CloseDoc(Root.GetTitle());
        int errors = 0, warnings = 0; Root = App.OpenDoc6(path, 2, 1, "", ref errors, ref warnings) as ModelDoc2;
        Check(Root != null, "AT16 reopen renamed fixture"); Activate(Root); Owner = SwInstanceBindings.ForDocument(Root);
        Console.WriteLine("OPEN_REBUILD_DIRTY=" + Root.GetSaveFlag()); Save(Root);
        try
        {
            var plan = Plan(); var resolved = Owner.Resolve(record.Parent, plan);
            Check(resolved.Source.Name.IndexOf("Binding-Renamed-Rack", StringComparison.OrdinalIgnoreCase) >= 0, "AT16 persistence resolves actual renamed occurrence");
            Check(Owner.Export(plan).Relations.Count == 3, "AT16 all relations survive renamed save/reopen");
            Rename(Root, ((SwCadNode)resolved.Source).Component, originalName);
            Save(Group); Root.EditRebuild3(); Save(Root);
            Check(Owner.Export(Plan()).Relations.Count == 3, "AT16 original display name restored");
        }
        finally { Owner.Dispose(); }
        }
        finally { App.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames, renamePreference); }
    }
    public static void Proximity(SldWorks app, ModelDoc2 root, SwInstanceBindings owner)
    {
        App = app; Root = root; Owner = owner;
        Group = App.GetOpenDocumentByName(Path.Combine(Path.GetDirectoryName(root.GetPathName()), "Binding-AT-Group.SLDASM")) as ModelDoc2;
        var plan = Plan(); var binding = owner.Current.Relations.First();
        var p = owner.Resolve(binding.Parent, plan); var c = owner.Resolve(binding.Child, plan);
        var competitor = SwInstanceBindings.Flatten(plan.Roots).Single(n => n.Kind == ExportNodeKind.Asset && n.ParentId == p.ParentId && n.AssetId == p.AssetId && n.Id != p.Id);
        var localB = GroupChildren().Single(n => n.GetID() == ((SwCadNode)competitor.Source).Component.GetID());
        var localC = GroupChildren().Single(n => n.GetID() == ((SwCadNode)c.Source).Component.GetID());
        var oldB = localB.Transform2; var oldC = localC.Transform2;
        var before = owner.Export(plan); var material = before.FingerprintMaterial();
        Activate(Group); localB.Transform2 = oldC; Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);
        var closer = owner.Export(Plan()); Check(material == closer.FingerprintMaterial(), "AT12 closer rack B cannot acquire children or alter A-relative poses");
        var values = (double[])oldC.ArrayData; values[9] += .01;
        localC.Transform2 = ((MathUtility)App.GetMathUtility()).CreateTransform(values) as MathTransform; Save(Group); Root.EditRebuild3(); Save(Root);
        var moved = owner.Export(Plan()); Check(material != moved.FingerprintMaterial(), "AT12 moving child changes relative pose");
        Check(before.Relations.Select(r => r.ParentNodeId + ":" + r.ChildNodeId).SequenceEqual(moved.Relations.Select(r => r.ParentNodeId + ":" + r.ChildNodeId)), "AT12 movement cannot change declared ownership");
        Activate(Group); localB.Transform2 = oldB; localC.Transform2 = oldC; Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);
        Check(material == owner.Export(Plan()).FingerprintMaterial(), "AT12 restored original positions");
        var baseline = owner.Current.Relations.ToList(); plan = Plan(); p = owner.Resolve(binding.Parent, plan); c = owner.Resolve(binding.Child, plan);
        var other = SwInstanceBindings.Flatten(plan.Roots).First(n => n.Kind == ExportNodeKind.Asset && n.ParentId != p.ParentId &&
            ((SwCadNode)n.Source).SourcePath.EndsWith("binding-at-tube.SLDPRT", StringComparison.OrdinalIgnoreCase));
        owner.Commit(InstanceBindingRules.Apply(owner.Current.Relations, owner.Capture(c), new[] { owner.Capture(other) }, e => owner.Resolve(e, plan), false)); Save(Root);
        var chained = owner.Export(plan);
        Check(chained.Relations.Any(r => r.ParentNodeId == p.Id && r.ChildNodeId == c.Id) && chained.Relations.Any(r => r.ParentNodeId == c.Id && r.ChildNodeId == other.Id), "AT10 real multilevel chain crosses different CAD parents");
        owner.Commit(baseline); Save(Root);
        var initialConfig = Root.ConfigurationManager.ActiveConfiguration.Name;
        Root.ShowConfiguration2("Binding-Config-B");
        var cloneName = "Binding-Copy-" + DateTime.Now.ToString("HHmmss");
        var clone = Root.ConfigurationManager.AddConfiguration2(cloneName, "", "", 0, "Binding-Config-B", "", true);
        Check(clone != null, "AT17 native derived configuration copy created"); Root.ShowConfiguration2(cloneName); Save(Root);
        Check(owner.Current.Relations.Count == 0, "AT17 copied configuration does not inherit bindings");
        var emptyPlan = Plan(); emptyPlan.Relations = owner.Export(emptyPlan);
        var proof = Path.Combine(Path.GetDirectoryName(Root.GetPathName()), "empty-configuration-" + cloneName);
        Directory.CreateDirectory(proof);
        AssemblyXmlWriter.Write(Path.Combine(proof, "assembly_" + emptyPlan.AssemblyUuid + "_v" + emptyPlan.AssemblyVersion + ".xml"), emptyPlan);
        JsonFile.Write(Path.Combine(proof, AssemblyRelations.FileName), emptyPlan.Relations);
        Check(JsonFile.Read<AssemblyRelations>(Path.Combine(proof, AssemblyRelations.FileName)).Relations.Count == 0, "AT26 actual empty configuration serializes an empty relation list");
        Root.ShowConfiguration2(initialConfig); Save(Root);
    }
    public static void Run(SldWorks app, ModelDoc2 root, SwInstanceBindings owner)
    {
        App = app; Root = root; Owner = owner;
        var config = root.ConfigurationManager.ActiveConfiguration.Name;
        var directory = Path.GetDirectoryName(root.GetPathName());
        Group = App.GetOpenDocumentByName(Path.Combine(directory, "Binding-AT-Group.SLDASM")) as ModelDoc2;
        if (Group == null) throw new Exception("Test group is not loaded.");
        var tubePath = Path.Combine(directory, "binding-at-tube.SLDPRT");
        var count = owner.Current.Relations.Count;
        Activate(Group); var inserted = Insert(tubePath); Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);
        var plan = Plan(); var newIds = SwInstanceBindings.Flatten(plan.Roots).Where(n => n.Source is SwCadNode && ((SwCadNode)n.Source).Component.GetID() == inserted.GetID()).Select(n => n.Id).ToList();
        var relations = owner.Export(plan);
        Check(relations.Relations.Count == count && !relations.Relations.Any(r => newIds.Contains(r.ChildNodeId) || newIds.Contains(r.ParentNodeId)), "AT20 inserted duplicate has no inherited binding");
        Activate(Group); Select(Group, inserted); Check(Group.Extension.DeleteSelection2(0), "delete unbound test duplicate"); Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);

        var lost = owner.Current.Relations.First();
        var endpoint = lost.Child; var selected = owner.ResolveComponent(endpoint);
        var local = GroupChildren().Single(c => c.GetID() == selected.GetID()); var oldName = local.Name2;
        Activate(Group); Select(Group, local); Check(Group.Extension.DeleteSelection2(0), "delete bound fixture component"); Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);
        Reject(() => owner.Export(Plan()), "AT18 deleted endpoint blocks package"); Check(owner.Current.Relations.Count == count, "AT18 invalid record not dropped");
        Activate(Group); var replacement = Insert(tubePath);
        var reinsertedName = replacement.Name2;
        Check(string.Equals(replacement.GetPathName(), tubePath, StringComparison.OrdinalIgnoreCase), "AT18 reinsert references exactly the same named source file and Asset");
        Console.WriteLine("REINSERT_OLD_INSTANCE=" + oldName + " NEW_INSTANCE=" + reinsertedName + " (SW allocates a new occurrence suffix)");
        Save(Group); Activate(Root); Root.EditRebuild3(); Save(Root);
        Reject(() => owner.Export(Plan()), "AT18 same-name same-asset insert does not repair old reference");
        RepairBoth(endpoint.Context, replacement.GetID(), config);
        Check(owner.Export(Plan()).Relations.Count == count, "AT18 explicit unbind/rebind repairs graph");

        // Replacement deliberately changes file identity while preserving the occurrence
        // when SOLIDWORKS permits it. No source UUID/name search is used by resolution.
        var changedPath = Path.Combine(directory, "binding-at-tube-replacement-" + DateTime.Now.ToString("HHmmss") + ".SLDPRT"); File.Copy(tubePath, changedPath, false);
        var before = owner.Current.Relations.Single(r => r.Child.Context.EndsWith("/" + replacement.GetID()));
        Activate(Group); var localReplacement = GroupChildren().Single(c => c.GetID() == replacement.GetID()); Select(Group, localReplacement);
        Check(((AssemblyDoc)Group).ReplaceComponents2(changedPath, "", false, 0, false), "native component replacement"); Save(Group);
        Activate(Root); Root.EditRebuild3(); Save(Root);
        Reject(() => owner.Export(Plan()), "AT18 source replacement blocks old binding");
        var after = GroupChildren().Single(c => string.Equals(c.GetPathName(), changedPath, StringComparison.OrdinalIgnoreCase));
        RepairBoth(before.Child.Context, after.GetID(), config);
        Check(owner.Export(Plan()).Relations.Count == count, "AT18 replacement repaired only by explicit rebind");
        Root.Extension.CustomPropertyManager[""].Add3("assembly_version", 30, "4", 2); Save(Root);
        Console.WriteLine("PASS_ALL_DELETION_REPLACEMENT_INSERTION_CHECKS");
    }
    private static Component2[] GroupChildren()
    { return (Group.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[] ?? new object[0]).OfType<Component2>().ToArray(); }
    private static void RepairBoth(string oldContext, int newLocalId, string initialConfiguration)
    {
        foreach (var configuration in new[] { initialConfiguration, "Binding-Config-B" })
        {
            Root.ShowConfiguration2(configuration); Save(Root); var prior = Owner.Current.Relations.Single(r => r.Child.Context == oldContext);
            Owner.Commit(Owner.Current.Relations.Where(r => r.Id != prior.Id).ToList()); Save(Root);
            var plan = Plan(); var parent = Owner.Resolve(prior.Parent, plan);
            var context = oldContext.Substring(0, oldContext.LastIndexOf('/') + 1) + newLocalId;
            var child = SwInstanceBindings.Flatten(plan.Roots).Single(n => n.Source is SwCadNode && SwInstanceBindings.Context(((SwCadNode)n.Source).Component) == context);
            Owner.Commit(InstanceBindingRules.Apply(Owner.Current.Relations, Owner.Capture(parent), new[] { Owner.Capture(child) }, e => Owner.Resolve(e, plan), false)); Save(Root);
            Check(Owner.Export(plan).Relations.Count == 3, "explicit repair in configuration " + configuration);
        }
        Root.ShowConfiguration2(initialConfiguration); Save(Root);
    }
    private static Component2 Insert(string path)
    { var c = ((AssemblyDoc)Group).AddComponent5(path, 0, "", false, "", 0, 0, 0) as Component2; if (c == null) throw new Exception("Insert failed."); return c; }
    private static void Select(ModelDoc2 doc, Component2 c) { doc.ClearSelection2(true); if (!c.Select4(false, null, false)) throw new Exception("Selection failed."); }
    private static string Rename(ModelDoc2 context, Component2 component, string name)
    {
        var preference = App.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames);
        try
        {
            App.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames, false);
            Activate(context); Select(context, component); component.Name2 = name;
            Console.WriteLine("RENAME_REQUEST=" + name + " ACTUAL=" + component.Name2);
            return component.Name2;
        }
        finally { App.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames, preference); }
    }
    private static void Activate(ModelDoc2 doc) { int e = 0; App.ActivateDoc3(doc.GetTitle(), false, 0, ref e); }
    private static void Save(ModelDoc2 doc) { int e = 0, w = 0; if (!doc.Save3(1, ref e, ref w) || e != 0) throw new Exception("Save failed " + e); }
    private static AssemblyExportPlan Plan() { return new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(App)), ProjectMeshFormat.Step); }
    private static void Reject(Action action, string name) { try { action(); } catch (ValidationException ex) { Console.WriteLine("PASS " + name + " blocked=" + ex.Message.Replace('\n', ' ')); return; } throw new Exception("FAIL " + name); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
