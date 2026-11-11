using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;

// Disposable UI fixture. Compile beside the production Release DLLs and pass
// "create", "inspect", or "tree" followed by a directory under the repository's artifacts folder.
internal static class BindingAssemblyTreeLiveFixture
{
    private const string InterfaceName = "tree-fixture-D20";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length != 2 || (args[0] != "create" && args[0] != "inspect" && args[0] != "tree"))
                throw new ArgumentException("Usage: BindingAssemblyTreeLiveFixture.exe create|inspect|tree <artifacts subdirectory>");
            var directory = FixtureDirectory(args[1]);
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            if (args[0] == "create") Create(app, directory);
            else Inspect(app, directory, args[0] == "tree");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string FixtureDirectory(string path)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts"));
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixture must be under the workspace artifacts directory.");
        for (var directory = new DirectoryInfo(fullPath); ; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The fixture directory cannot traverse a junction or symlink.");
            if (string.Equals(directory.FullName, workspace, StringComparison.OrdinalIgnoreCase)) break;
        }
        return fullPath;
    }

    private static void Create(SldWorks app, string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("Use a new or empty directory for this fixture.");
        Directory.CreateDirectory(directory);
        var prefix = "BindingTree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var partTemplate = Template(app, swUserPreferenceStringValue_e.swDefaultTemplatePart);
        var assemblyTemplate = Template(app, swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
        var parent = CreatePart(app, partTemplate, Path.Combine(directory, prefix + "-Z-Parent.SLDPRT"), true);
        var child = CreatePart(app, partTemplate, Path.Combine(directory, prefix + "-A-Child.SLDPRT"), false);

        var workstation = NewDocument(app, assemblyTemplate);
        SetProperty(workstation, "is_asset", "0");
        SetProperty(workstation, "is_workstation", "1");
        SetProperty(workstation, "workstation_name", "树验收工位");
        workstation.Extension.CustomPropertyManager[""].Delete2("is-workstation");
        workstation.Extension.CustomPropertyManager[""].Delete2("workstationname");
        Insert(workstation, parent, 0, 0);
        Insert(workstation, child, 0, 0.025);
        Insert(workstation, parent, 0.035, 0);
        Insert(workstation, child, 0.035, 0.025);
        SaveAs(workstation, Path.Combine(directory, prefix + "-Workstation.SLDASM"));

        var assembly = NewDocument(app, assemblyTemplate);
        SetProperty(assembly, "is_asset", "0");
        SetProperty(assembly, "is_workstation", "0");
        SetProperty(assembly, "assembly_version", "1");
        assembly.Extension.CustomPropertyManager[""].Delete2("is-workstation");
        Insert(assembly, workstation, 0, 0);
        SaveAs(assembly, Path.Combine(directory, prefix + "-Assembly.SLDASM"));
        Activate(app, assembly);
        Console.WriteLine("FIXTURE=" + assembly.GetPathName());
        Console.WriteLine("REPAIR_CHILD_CONNECTION=" + InterfaceName);
        ReadState(assembly);
    }

    private static void Inspect(SldWorks app, string directory, bool rawTree)
    {
        var path = Directory.GetFiles(directory, "BindingTree-*-Assembly.SLDASM").Single();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The fixture assembly cannot be a symlink.");
        var document = app.GetOpenDocumentByName(path) as ModelDoc2;
        if (document == null)
        {
            int error = 0, warning = 0;
            document = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                "", ref error, ref warning) as ModelDoc2;
            if (document == null) throw new InvalidOperationException("Open fixture failed: " + error);
        }
        if (!string.Equals(document.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SOLIDWORKS resolved a different document than the fixture.");
        Activate(app, document);
        if (rawTree) WriteTree(FeatureTree(document), 0);
        else ReadState(document);
    }

    private static void ReadState(ModelDoc2 assembly)
    {
        foreach (var component in Components(FeatureTree(assembly)))
        {
            var source = component.GetModelDoc2() as ModelDoc2;
            if (source == null) throw new InvalidOperationException("Fixture source is not loaded: " + component.Name2);
            var properties = new SwInterfacePropertyTarget(source).Read();
            Console.WriteLine("OCCURRENCE=" + component.Name2 + "|" + SwInstanceBindings.Context(component) + "|" +
                "connection=" + properties.Get("connection_interface") + "|accepts=" + properties.Get("accepts_interfaces"));
        }
        using (var bindings = SwInstanceBindings.ForDocument(assembly))
        {
            var records = bindings.Current.Relations;
            foreach (var edge in records)
                Console.WriteLine("RELATION=" + edge.Parent.DisplayPath + " -> " + edge.Child.DisplayPath + "|" + edge.Id);
            Console.WriteLine("RELATION_COUNT=" + records.Count);
        }
    }

    private static TreeControlItem FeatureTree(ModelDoc2 assembly)
    {
        var tree = assembly.FeatureManager.GetFeatureTreeRootItem2((int)swFeatMgrPane_e.swFeatMgrPaneBottom);
        if (tree == null) throw new InvalidOperationException("The fixture has no native FeatureManager tree.");
        return tree;
    }

    private static void WriteTree(TreeControlItem item, int depth)
    {
        var type = (swTreeControlItemType_e)item.ObjectType;
        var detail = "";
        if (type == swTreeControlItemType_e.swFeatureManagerItem_Feature)
        {
            var feature = item.Object as Feature;
            if (feature != null) detail = " | feature=" + feature.GetTypeName2();
        }
        else if (type == swTreeControlItemType_e.swFeatureManagerItem_Component)
        {
            var component = item.Object as Component2;
            if (component != null)
                detail = " | component=" + component.Name2 + " | context=" + SwInstanceBindings.Context(component);
        }
        Console.WriteLine(new string(' ', depth * 2) + item.Text + " | type=" + (int)type + detail);
        for (var child = item.GetFirstChild(); child != null; child = child.GetNext()) WriteTree(child, depth + 1);
    }

    private static IEnumerable<Component2> Components(TreeControlItem item)
    {
        if (item.ObjectType == (int)swTreeControlItemType_e.swFeatureManagerItem_Component)
        {
            var component = item.Object as Component2;
            if (component != null) yield return component;
        }
        for (var child = item.GetFirstChild(); child != null; child = child.GetNext())
            foreach (var component in Components(child)) yield return component;
    }

    private static ModelDoc2 CreatePart(SldWorks app, string template, string path, bool parent)
    {
        var document = NewDocument(app, template);
        var plane = document.FirstFeature() as Feature;
        while (plane != null && plane.GetTypeName2() != "RefPlane") plane = plane.GetNextFeature() as Feature;
        if (plane == null || !plane.Select2(false, 0)) throw new InvalidOperationException("Fixture part has no sketch plane.");
        document.SketchManager.InsertSketch(true);
        document.SketchManager.CreateCornerRectangle(-0.006, -0.006, 0, 0.006, 0.006, 0);
        document.SketchManager.InsertSketch(true);
        var sketch = document.FeatureByPositionReverse(0) as Feature;
        if (sketch == null || !sketch.Select2(false, 0)) throw new InvalidOperationException("Could not select the fixture sketch.");
        var solid = document.FeatureManager.FeatureExtrusion2(true, false, false,
            (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, 0.01, 0,
            false, false, false, false, 0, 0, false, false, false, false, true, true, true,
            (int)swStartConditions_e.swStartSketchPlane, 0, false);
        if (solid == null) throw new InvalidOperationException("Could not create fixture geometry.");
        SetProperty(document, "is_asset", "1");
        SetProperty(document, "class", "movable");
        SetProperty(document, "asset_version", "1");
        SetProperty(document, "零件名", parent ? "承载件" : "被承载件");
        SetProperty(document, "设计目的", "总装树交互测试");
        SetProperty(document, "is_fixture", parent ? "1" : "0");
        foreach (var flag in new[] { "is_tool", "is_quick_changer", "is_quick_changer_rack" }) SetProperty(document, flag, "0");
        SetProperty(document, "quick_changer_side", "");
        SetProperty(document, "connection_interface", "");
        SetProperty(document, "accepts_interfaces", parent ? InterfaceName : "");
        SaveAs(document, path);
        return document;
    }

    private static void SetProperty(ModelDoc2 document, string name, string value)
    {
        var result = document.Extension.CustomPropertyManager[""].Add3(name,
            (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
        if (result != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged)
            throw new InvalidOperationException("Could not set fixture property " + name + ": " + result);
    }

    private static string Template(SldWorks app, swUserPreferenceStringValue_e kind)
    {
        var path = app.GetUserPreferenceStringValue((int)kind);
        if (!File.Exists(path)) throw new FileNotFoundException("Set a SOLIDWORKS default template for " + kind + ".", path);
        return path;
    }

    private static ModelDoc2 NewDocument(SldWorks app, string template)
    {
        var document = app.NewDocument(template, 0, 0, 0) as ModelDoc2;
        if (document == null) throw new InvalidOperationException("Could not create a fixture document.");
        return document;
    }

    private static void Insert(ModelDoc2 assembly, ModelDoc2 source, double x, double y)
    {
        var component = ((AssemblyDoc)assembly).AddComponent5(source.GetPathName(),
            (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", x, y, 0);
        if (component == null) throw new InvalidOperationException("Could not insert fixture component: " + source.GetPathName());
    }

    private static void SaveAs(ModelDoc2 document, string path)
    {
        int error = 0, warning = 0;
        if (!document.Extension.SaveAs(path, 0, 1, null, ref error, ref warning) || error != 0)
            throw new InvalidOperationException("Could not save fixture document: " + path + " (" + error + ")");
    }

    private static void Activate(SldWorks app, ModelDoc2 document)
    {
        int error = 0;
        app.ActivateDoc3(document.GetTitle(), false, 0, ref error);
        if (!Equals(app.ActiveDoc, document)) throw new InvalidOperationException("Could not activate fixture document: " + error);
    }
}
