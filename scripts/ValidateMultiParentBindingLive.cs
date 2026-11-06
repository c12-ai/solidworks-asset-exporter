using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

// Read-only check of the current rack/changer/pipettor: mutations affect only local
// relation lists. It does not create a storage owner, save, select or activate models.
class ValidateMultiParentBindingLive
{
    static IEnumerable<Component2> Descendants(Component2 root)
    {
        foreach(var c in (root.GetChildren() as object[] ?? new object[0]).OfType<Component2>())
        { yield return c; foreach(var child in Descendants(c)) yield return child; }
    }
    [STAThread] static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var root = app.GetOpenDocumentByName(@"C:\Users\C12\Desktop\1103\（部分）layout_all 20261103.SLDASM") as ModelDoc2;
            if(root == null) throw new Exception("Expected layout must already be loaded.");
            var candidates = Descendants(root.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false))
                .Where(c => c.Name2.Contains("/移液器快换总装-1/")).ToList();
            var names = new[]{"快换支架总装.SLDASM", "MB0200308  AQT-020工具侧.SLDPRT", "移液器装配.SLDASM"};
            var nodes = new List<ExportNode>(); var endpoints = new List<BindingEndpoint>();
            foreach(var name in names)
            {
                var component = candidates.Single(c => string.Equals(Path.GetFileName(c.GetPathName()), name, StringComparison.OrdinalIgnoreCase));
                var doc = component.GetModelDoc2() as ModelDoc2;
                if(doc == null) throw new Exception("Source is not loaded: " + name);
                var properties = new SwInterfacePropertyTarget(doc).Read();
                if(!PropertyRules.ReadIsAsset(properties.Values)) throw new Exception("Expected Asset: " + name);
                var id = SwInstanceBindings.Context(component);
                nodes.Add(new ExportNode { Id = id, Name = component.Name2, Kind = ExportNodeKind.Asset,
                    Properties = properties.Values, Source = new SwCadNode(app, component, "/" + component.Name2) });
                endpoints.Add(new BindingEndpoint { Reference = id, Context = id, DisplayPath = component.Name2 });
                Console.WriteLine("MODEL=" + name + " ROLE=" + PropertyRules.DescribeAssetConnectionRole(properties.Values) + " CONNECTION=" + properties.Get(PropertyRules.ConnectionInterface) + " ACCEPTS=" + properties.Get(PropertyRules.AcceptsInterfaces));
            }
            Func<BindingEndpoint, ExportNode> resolve = e => nodes.Single(n => n.Id == e.Context);
            var relations = new List<InstanceBinding>();
            relations = InstanceBindingRules.Apply(relations, endpoints[0], new[]{endpoints[1], endpoints[2]}, resolve, false);
            relations = InstanceBindingRules.Apply(relations, endpoints[1], new[]{endpoints[2]}, resolve, false);
            var exported = InstanceBindingRules.Resolve(relations, resolve, null);
            if(exported.Relations.Count != 3 || exported.Relations.Count(r => r.ChildNodeId == nodes[2].Id) != 2)
                throw new Exception("Expected rack->changer, rack->tool and changer->tool.");
            foreach(var r in exported.Relations)
                Console.WriteLine("VALID_EDGE=" + nodes.Single(n => n.Id == r.ParentNodeId).Name + " -> " + nodes.Single(n => n.Id == r.ChildNodeId).Name + " POSE=" + r.Pose.CanonicalValue());
            Console.WriteLine("PASS_REAL_PROPERTIES_AND_TRANSFORMS; SHARED_TOOL_PARENTS=2; SCHEMA=" + exported.SchemaVersion);
            Console.WriteLine("NO_MODEL_OR_BINDING_CHANGES; ROOT_DIRTY=" + root.GetSaveFlag());
            var dirty = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>().Where(d => d.GetSaveFlag()).ToList();
            foreach(var doc in dirty) Console.WriteLine("UNSAVED=" + doc.GetPathName());
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
