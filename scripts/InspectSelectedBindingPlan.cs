using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;
class InspectSelectedBindingPlan
{
    static IEnumerable<Component2> Descendants(Component2 parent)
    { foreach(var child in (parent.GetChildren() as object[]??new object[0]).OfType<Component2>()) { yield return child; foreach(var nested in Descendants(child))yield return nested; } }
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try
        {
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");var root=app.ActiveDoc as ModelDoc2;
            Console.WriteLine("ACTIVE="+root.GetPathName());
            var rootProps=new SwInterfacePropertyTarget(root).Read();Console.WriteLine("ROOT_PROPERTIES="+string.Join(";",rootProps.Values.Select(p=>p.Key+"="+p.Value)));
            var manager=(SelectionMgr)root.SelectionManager;
            Component2 selected;
            if(args.Contains("--medical-waste"))
                selected=Descendants(root.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false)).Single(c=>c.GetPathName().EndsWith("\\医疗垃圾桶.SLDPRT",StringComparison.OrdinalIgnoreCase));
            else if(args.Contains("--root-pipette"))
                selected=(root.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false).GetChildren() as object[]??new object[0]).OfType<Component2>().Single(c=>c.GetPathName().EndsWith("\\25ml移液管.SLDPRT",StringComparison.OrdinalIgnoreCase));
            else
            {
                if(manager.GetSelectedObjectCount2(-1)!=1)throw new Exception("Exactly one selected component is required.");
                var raw=manager.GetSelectedObject6(1,-1) as Component2;
                var owner=manager.GetSelectedObjectsComponent4(1,-1) as Component2;
                Console.WriteLine("RAW="+(raw==null?"null":raw.Name2+" CONTEXT="+SwInstanceBindings.Context(raw)));
                Console.WriteLine("OWNER="+(owner==null?"null":owner.Name2+" CONTEXT="+SwInstanceBindings.Context(owner)));
                selected=raw??owner;
            }
            if(selected==null)throw new Exception("No selected component.");
            var context=SwInstanceBindings.Context(selected);var path=selected.GetPathName();var contexts=new List<string>();
            Console.WriteLine("SELECTED="+selected.Name2+" CONTEXT="+context);
            for(var c=selected;c!=null;c=c.GetParent() as Component2)
            {
                contexts.Add(SwInstanceBindings.Context(c));
                var direct=new SwCadNode(app,c,"/"+c.Name2);var props=direct.ClassificationModel.FileProperties;
                Console.WriteLine("STATE="+c.Name2+" VISIBLE="+direct.IsVisible+" SUPPRESSED="+direct.IsSuppressed+" ENVELOPE="+direct.IsEnvelope);
                Console.WriteLine("CLASSIFICATION="+c.Name2+" PATH="+direct.ClassificationModel.FullPath+" IS_ASSET="+PropertyRules.ReadIsAsset(props)+" PROPS="+string.Join(";",props.Select(p=>p.Key+"="+p.Value)));
                var document=c.GetModelDoc2() as ModelDoc2;
                if(document!=null)
                {
                    CustomPropertyManager properties=((dynamic)document.Extension).CustomPropertyManager[""];
                    foreach(var name in ((properties.GetNames() as string[])??new string[0]).Where(n=>n.IndexOf("asset",StringComparison.OrdinalIgnoreCase)>=0))
                    { string raw,resolved;bool was,linked;var status=properties.Get6(name,true,out raw,out resolved,out was,out linked);Console.WriteLine("EXACT_KEY=["+name+"] STATUS="+status+" RAW="+raw+" RESOLVED="+resolved+" WAS_RESOLVED="+was); }
                }
            }
            var tracker=(SwPluginMutationTracker)Activator.CreateInstance(typeof(SwPluginMutationTracker),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,new object[]{app,true},null);
            var plan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app,tracker)),ProjectMeshFormat.Step,false);
            var nodes=SwInstanceBindings.Flatten(plan.Roots).ToList();
            foreach(var n in nodes.Where(n=>n.Source is SwCadNode && contexts.Contains(SwInstanceBindings.Context(((SwCadNode)n.Source).Component))))
                Console.WriteLine("ANCESTOR_PLAN_NODE="+n.Name+" KIND="+n.Kind+" CONTEXT="+SwInstanceBindings.Context(((SwCadNode)n.Source).Component));
            foreach(var n in nodes.Where(n=>n.Source is SwCadNode && string.Equals(((SwCadNode)n.Source).SourcePath,path,StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("SAME_SOURCE_NODE="+n.Name+" KIND="+n.Kind+" CONTEXT="+SwInstanceBindings.Context(((SwCadNode)n.Source).Component));
            var match=nodes.SingleOrDefault(n=>n.Source is SwCadNode && n.Source.InstanceId!="root" && SwInstanceBindings.Context(((SwCadNode)n.Source).Component)==context);
            Console.WriteLine("PLAN_MATCH="+(match==null?"MISSING":match.Kind.ToString()));
            InstanceBindingRules.RequireAsset(match);Console.WriteLine("PASS_SELECTION_ELIGIBILITY");
            if(args.Contains("--consumables"))
            {
                match.Properties=PropertyRules.Merge(match.Source.Model);
                foreach(var file in new[]{"25ml移液管.SLDPRT","移液枪头-1000uL.SLDPRT"})
                {
                    var children=nodes.Where(n=>n.Source is SwCadNode && string.Equals(System.IO.Path.GetFileName(((SwCadNode)n.Source).SourcePath),file,StringComparison.OrdinalIgnoreCase)).ToList();
                    var passed=0;
                    foreach(var child in children)
                    {
                        InstanceBindingRules.RequireAsset(child);child.Properties=PropertyRules.Merge(child.Source.Model);
                        InstanceBindingRules.ValidateConnection(match,child);passed++;
                    }
                    Console.WriteLine("CHILD_COMPATIBILITY="+file+" EXPORTED="+children.Count+" PASSED="+passed);
                }
            }
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
