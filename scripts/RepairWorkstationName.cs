using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

[DataContract] public class WorkstationNameFix
{
    [DataMember] public string Target;
    [DataMember] public string Name;
    [DataMember] public bool WasDirty;
    [DataMember] public Dictionary<string,string> Before;
    [DataMember] public Dictionary<string,string> After;
    [DataMember] public string OriginalHash;
    [DataMember] public string DiskBackup;
    [DataMember] public string MemoryBackup;
}
class RepairWorkstationName
{
    const string Root=@"C:\Users\C12\Desktop\1103\（部分）layout_all 20261103.SLDASM";
    const string Target=@"C:\Users\C12\Desktop\1103\冰箱3.SLDASM";
    static string Hash(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(f)).Replace("-","");}
    static void Copy(string a,string b){using(var source=new FileStream(a,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var destination=new FileStream(b,FileMode.CreateNew,FileAccess.Write))source.CopyTo(destination);if(Hash(a)!=Hash(b))throw new Exception("Backup verification failed");}
    static bool SameProperties(Dictionary<string,string> a,Dictionary<string,string> b){return a.Count==b.Count&&a.All(kv=>b.ContainsKey(kv.Key)&&b[kv.Key]==kv.Value);}
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try
        {
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");var root=app.GetOpenDocumentByName(Root) as ModelDoc2;
            if(root==null||!string.Equals(root.GetPathName(),Root,StringComparison.OrdinalIgnoreCase))throw new Exception("Expected current 1103 layout");
            var top=(((AssemblyDoc)root).GetComponents(true) as object[]??new object[0]).OfType<Component2>().ToList();
            if(args[0]=="--audit")
            {
                foreach(var c in top){var d=c.GetModelDoc2() as ModelDoc2;if(d==null||d.GetType()!=2)continue;var p=new SwInterfacePropertyTarget(d).Read();if(!PropertyRules.ReadIsWorkstation(p.Values,DocumentKind.Assembly))continue;
                    Console.WriteLine("WORKSTATION="+c.Name2+" PATH="+d.GetPathName()+" NAME="+p.Get("workstation_name")+" LEGACY_NAME="+p.Get("workstationname")+" DIRTY="+d.GetSaveFlag());}
                return 0;
            }
            if(args[0]=="--verify-plan")
            {
                int activation=0;app.ActivateDoc3(root.GetTitle(),false,0,ref activation);
                var plan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)),ProjectMeshFormat.Step,false);
                Console.WriteLine("PASS_BINDING_SELECTION_PLAN ASSETS="+SwInstanceBindings.Flatten(plan.Roots).Count(n=>n.Kind==ExportNodeKind.Asset)+" WORKSTATIONS="+SwInstanceBindings.Flatten(plan.Roots).Count(n=>n.Kind==ExportNodeKind.Workstation));return 0;
            }
            var doc=app.GetOpenDocumentByName(Target) as ModelDoc2;
            if(doc==null||!top.Any(c=>string.Equals(c.GetPathName(),Target,StringComparison.OrdinalIgnoreCase)))throw new Exception("Expected top-level fridge3 is not loaded");
            var property=new SwInterfacePropertyTarget(doc);if(property.ReadOnly)throw new Exception("Target is read-only");
            var report=Path.GetFullPath(args[1]);
            if(args[0]=="--prepare")
            {
                if(Directory.Exists(report))throw new Exception("Use a new backup directory");Directory.CreateDirectory(report);
                var before=property.Read();if(!PropertyRules.ReadIsWorkstation(before.Values,DocumentKind.Assembly)||!string.IsNullOrWhiteSpace(before.Get("workstation_name"))||!string.IsNullOrWhiteSpace(before.Get("workstationname")))throw new Exception("Expected a workstation with missing name");
                var fix=new WorkstationNameFix {Target=Target,Name="冰箱3",WasDirty=doc.GetSaveFlag(),Before=before.Raw,OriginalHash=Hash(Target),DiskBackup=Path.Combine(report,"冰箱3-before.SLDASM")};
                Copy(Target,fix.DiskBackup);
                if(fix.WasDirty)
                {
                    fix.MemoryBackup=Path.Combine(report,"冰箱3-unsaved-recovery.SLDASM");int activation=0;app.ActivateDoc3(doc.GetTitle(),false,0,ref activation);doc.ClearSelection2(true);int e=0,w=0;
                    try{if(!doc.Extension.SaveAs(fix.MemoryBackup,0,3,null,ref e,ref w)||e!=0||!File.Exists(fix.MemoryBackup)||!string.Equals(doc.GetPathName(),Target,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsaved recovery copy failed");if(!doc.GetSaveFlag())doc.SetSaveFlag();}
                    finally{app.ActivateDoc3(root.GetTitle(),false,0,ref activation);}
                }
                property.Set("workstation_name",fix.Name,30);fix.After=property.Read().Raw;
                if(PropertyRules.RequireWorkstationName(property.Read().Values,"冰箱3-1")!=fix.Name)throw new Exception("Name verification failed");
                if(Hash(Target)!=fix.OriginalHash)throw new Exception("Original changed while preparing repair");
                JsonFile.Write(Path.Combine(report,"prepared.json"),fix);Console.WriteLine("PREPARED_FILE_PROPERTY workstation_name=冰箱3 WAS_DIRTY="+fix.WasDirty+" BACKUP="+report);return 0;
            }
            if(args[0]=="--save-approved")
            {
                var fix=JsonFile.Read<WorkstationNameFix>(Path.Combine(report,"prepared.json"));
                if(fix.Target!=Target||!SameProperties(fix.After,property.Read().Raw)||Hash(Target)!=fix.OriginalHash||Hash(fix.DiskBackup)!=fix.OriginalHash)throw new Exception("Prepared repair changed; review before saving");
                var files=Directory.GetFiles(Path.GetDirectoryName(Target)).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&(p.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase))&&!string.Equals(p,Target,StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p,Hash);
                property.Save();if(!SameProperties(fix.After,property.Read().Raw)||doc.GetSaveFlag())throw new Exception("Saved property verification failed");
                if(files.Any(kv=>Hash(kv.Key)!=kv.Value))throw new Exception("An unrelated model file changed during save");
                File.WriteAllText(Path.Combine(report,"saved.txt"),"SAVED="+Target+System.Environment.NewLine+"workstation_name=冰箱3"+System.Environment.NewLine+"SHA256="+Hash(Target)+System.Environment.NewLine+"UNRELATED_MODEL_FILES_UNCHANGED="+files.Count,Encoding.UTF8);
                Console.WriteLine("PASS_SAVED_WORKSTATION_NAME_ONLY_TARGET_FILE="+Target);return 0;
            }
            throw new Exception("Unknown mode");
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
