using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;
class InspectBindingSelection
{
    static void Print(ModelDoc2 doc,string label)
    {
        if(doc==null){Console.WriteLine(label+" UNLOADED");return;}
        var p=new SwInterfacePropertyTarget(doc).Read();
        Console.WriteLine(label+" PATH="+doc.GetPathName()+" DIRTY="+doc.GetSaveFlag()+" IS_ASSET="+PropertyRules.ReadIsAsset(p.Values));
        foreach(var key in new[]{"is_asset","is-asset","class","asset_version","connection_interface","accepts_interfaces","has_QRcode","QR_size","is_fixture","is_tool","is_quick_changer","quick_changer_side","is_tool_rack","设计目的"})
            Console.WriteLine("  "+key+"="+(p.Raw.ContainsKey(key)?p.Raw[key]:"<missing>"));
    }
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try{
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");var root=app.ActiveDoc as ModelDoc2;
            if(args.Contains("--layout"))root=app.GetOpenDocumentByName(@"C:\Users\C12\Desktop\1103\（部分）layout_all 20261103.SLDASM") as ModelDoc2;
            if(root==null)throw new Exception("No active model");Console.WriteLine("ACTIVE="+root.GetPathName());
            if(args.Contains("--quick-changers"))
            {
                foreach(var doc in (app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Where(d=>new[]{"AQT","快换","移液器装配","助吸器"}.Any(n=>Path.GetFileName(d.GetPathName()).Contains(n))))Print(doc,"QUICK_CHANGE_MODEL");
                return 0;
            }
            if(args.Contains("--conflicts"))
            {
                using(var session=SwInstanceBindings.ForDocument(root))
                foreach(var record in session.Current.Relations.Where(r=>r.Id=="2c0fa2f9-0908-49bf-be96-45d3fee1c0d2"||r.Id=="d8438b87-1cc7-41e5-b1c6-23170ea8d596"))
                {
                    Console.WriteLine("RECORD="+record.Id);
                    foreach(var endpoint in new[]{record.Parent,record.Child})
                    {
                        var component=session.ResolveComponent(endpoint);Print(component.GetModelDoc2() as ModelDoc2,"ENDPOINT="+component.Name2);
                        for(var c=component;c!=null;c=c.GetParent() as Component2)
                            Console.WriteLine("  CHAIN="+c.Name2+" VISIBLE="+(c.Visible==(int)swComponentVisibilityState_e.swComponentVisible)+" SUPPRESSION="+c.GetSuppression2()+" ENVELOPE="+c.IsEnvelope());
                    }
                }
                return 0;
            }
            var selection=root.SelectionManager as SelectionMgr;var count=selection.GetSelectedObjectCount2(-1);Console.WriteLine("SELECTIONS="+count);
            for(var i=1;i<=count;i++){
                var c=selection.GetSelectedObject6(i,-1) as Component2??selection.GetSelectedObjectsComponent4(i,-1) as Component2;
                for(var level=0;c!=null&&level<12;level++,c=c.GetParent() as Component2)
                {
                    Console.WriteLine("STATE="+c.Name2+" VISIBLE="+(c.Visible==(int)swComponentVisibilityState_e.swComponentVisible)+" SUPPRESSION="+c.GetSuppression2()+" ENVELOPE="+c.IsEnvelope()+" PATH="+c.GetPathName());
                    Print(c.GetModelDoc2() as ModelDoc2,"SELECTED_CHAIN_LEVEL="+level+" INSTANCE="+c.Name2);
                }
            }
            foreach(var doc in (app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Where(d=>new[]{"42mm二维码安装板.SLDPRT","28mm二维码安装板.SLDPRT","锥形瓶底板.SLDPRT","1.5ml离心管.SLDPRT"}.Contains(Path.GetFileName(d.GetPathName()))))Print(doc,"NAMED_MODEL");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
