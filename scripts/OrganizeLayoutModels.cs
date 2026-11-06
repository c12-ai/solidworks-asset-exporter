using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

[DataContract] public sealed class LayoutSnapshot
{
    [DataMember] public string Root;
    [DataMember] public string Configuration;
    [DataMember] public int ProcessId;
    [DataMember] public List<string> DirtyBefore = new List<string>();
    [DataMember] public List<LayoutModel> Models = new List<LayoutModel>();
    [DataMember] public List<LayoutOccurrence> Components = new List<LayoutOccurrence>();
}
[DataContract] public sealed class LayoutModel
{
    [DataMember] public string Path;
    [DataMember] public string Copy;
    [DataMember] public string Sha256;
    [DataMember] public bool Dirty;
    [DataMember] public string Created;
    [DataMember] public string[] Configurations;
    [DataMember] public Dictionary<string,string> Raw;
    [DataMember] public Dictionary<string,int> Types;
}
[DataContract] public sealed class LayoutOccurrence
{
    [DataMember] public string Name;
    [DataMember] public string Context;
    [DataMember] public string Path;
    [DataMember] public string Configuration;
    [DataMember] public string Reference;
    [DataMember] public double[] Transform;
    [DataMember] public int Suppression;
}
[DataContract] public sealed class LayoutUpdate
{
    [DataMember] public string Name;
    [DataMember] public string LatestSource;
    [DataMember] public string LatestSha256;
    [DataMember] public string OriginalPath;
    [DataMember] public string Created;
    [DataMember] public bool Renamed;
}
[DataContract] public sealed class LayoutUpdatePlan
{
    [DataMember] public string Directory;
    [DataMember] public LayoutSnapshot Snapshot;
    [DataMember] public List<LayoutUpdate> Updates;
    [DataMember] public Dictionary<string,string> AssetVersions;
    [DataMember] public string AssemblyVersion;
    [DataMember] public int ExpectedBindingCount;
}
internal static partial class OrganizeLayoutModels
{
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx context);
    static SldWorks FindProcess(int pid)
    {
        IRunningObjectTable table; IBindCtx context;
        GetRunningObjectTable(0,out table); CreateBindCtx(0,out context);
        IEnumMoniker names; table.EnumRunning(out names);
        try
        {
            var item=new IMoniker[1];
            while(names.Next(1,item,IntPtr.Zero)==0)
            {
                try
                {
                    string name; item[0].GetDisplayName(context,null,out name);
                    if(name.IndexOf("sldworks",StringComparison.OrdinalIgnoreCase)<0 && name.IndexOf("solidworks",StringComparison.OrdinalIgnoreCase)<0) continue;
                    object value; table.GetObject(item[0],out value);
                    var app=value as SldWorks;
                    if(app!=null && app.GetProcessID()==pid) return app;
                }
                catch(COMException) { }
                finally { Marshal.ReleaseComObject(item[0]); }
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(names); Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(table); }
    }
    static SldWorks StartIsolated(int originalPid)
    {
        var process=Process.Start(new ProcessStartInfo(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe") {
            UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden
        });
        if(process==null || process.Id==originalPid) throw new Exception("Cannot start isolated SOLIDWORKS");
        Console.WriteLine("STARTED_PID="+process.Id);
        for(int i=0;i<240;i++)
        {
            var app=FindProcess(process.Id); if(app!=null) return app;
            if(process.HasExited) throw new Exception("Isolated process exited before COM registration");
            Thread.Sleep(500);
        }
        throw new Exception("Isolated instance did not register in 120 seconds; PID="+process.Id);
    }
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 3 && args[0] == "--snapshot") Snapshot(args[1], args[2]);
            else if (args.Length == 4 && args[0] == "--inspect-isolated") InspectIsolated(args[1], args[2], int.Parse(args[3]),0);
            else if (args.Length == 5 && args[0] == "--inspect-owned") InspectIsolated(args[1], args[2], int.Parse(args[3]),int.Parse(args[4]));
            else if (args.Length == 3 && args[0] == "--finish") Finish(args[1],args[2]);
            else if (args.Length == 4 && args[0] == "--inspect-tree") InspectTree(args[1],args[2],int.Parse(args[3]));
            else if (args.Length == 4 && args[0] == "--audit-current") AuditCurrent(args[1],args[2],int.Parse(args[3]));
            else if (args.Length == 3 && args[0] == "--sync-properties") SyncProperties(args[1],args[2]);
            else if (args.Length == 2 && args[0] == "--check-property-sync-target") CheckPropertySyncTarget(args[1]);
            else if (args.Length == 3 && args[0] == "--inspect-workstations") InspectWorkstations(args[1],args[2]);
            else if (args.Length == 3 && args[0] == "--restructure-workstations") RestructureWorkstations(args[1],args[2]);
            else if (args.Length == 3 && args[0] == "--finish-workstations") FinishWorkstations(args[1],args[2]);
            else if (args.Length == 4 && args[0] == "--prepare-workstations") PrepareWorkstations(args[1],args[2],args[3]);
            else if (args.Length == 4 && args[0] == "--verify-workstations") VerifyPublishedWorkstations(args[1],args[2],args[3]);
            else if (args.Length == 3 && args[0] == "--restore-plugin-session") RestorePluginSession(args[1],args[2]);
            else if (args.Length == 3 && args[0] == "--localize") Localize(args[1], args[2]);
            else throw new Exception("Usage: --snapshot expected-root new-directory | --localize model-directory report.json | --inspect-isolated model-directory new-report.json original-process-id");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static SldWorks Current() { return (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
    static void RestorePluginSession(string rootPath,string reportPath)
    {
        var app=StartIsolated(0);app.Visible=true;app.UserControl=true;
        var root=OpenRoot(app,rootPath);
        var addin=app.GetAddInObject("SolidWorksAssetExporter.AddIn");
        if(addin==null)throw new Exception("Restored session did not load the add-in");
        var build=(string)addin.GetType().InvokeMember("GetBuildInfo",System.Reflection.BindingFlags.InvokeMethod,null,addin,new object[0]);
        if(!build.StartsWith("instance-bindings-v4|",StringComparison.Ordinal))throw new Exception("Unexpected loaded add-in: "+build);
        File.WriteAllText(reportPath,"PROCESS="+app.GetProcessID()+System.Environment.NewLine+"ACTIVE="+root.GetPathName()+System.Environment.NewLine+"BUILD="+build+System.Environment.NewLine+"DIRTY="+root.GetSaveFlag());
        Console.WriteLine("RESTORED="+root.GetPathName());Console.WriteLine("BUILD="+build);
        Marshal.ReleaseComObject(app);
    }
    static bool Same(string a, string b) { return string.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
    static string Hash(string path)
    {
        using(var f = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(f)).Replace("-","");
    }
    static void Copy(string source, string target)
    {
        using(var a = new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var b = new FileStream(target,FileMode.CreateNew,FileAccess.Write)) a.CopyTo(b);
        if(Hash(source)!=Hash(target)) throw new Exception("Copy verification failed: "+source);
    }
    static List<string> Dependencies(SldWorks app, string path)
    {
        var raw=app.GetDocumentDependencies2(path,false,false,false) as string[];
        if(raw==null) return new List<string>();
        if(raw.Length%2!=0) throw new Exception("Invalid dependencies: "+path);
        return Enumerable.Range(0,raw.Length/2).Select(i=>raw[2*i+1]).ToList();
    }
    static LayoutModel Read(ModelDoc2 doc)
    {
        var s=new SwInterfacePropertyTarget(doc).Read();
        return new LayoutModel { Path=doc.GetPathName(), Dirty=doc.GetSaveFlag(), Raw=s.Raw, Types=s.Types,
            Created=doc.get_SummaryInfo((int)swSummInfoField_e.swSumInfoCreateDate), Configurations=doc.GetConfigurationNames() as string[] };
    }
    static List<LayoutOccurrence> Occurrences(ModelDoc2 root)
    {
        return (((AssemblyDoc)root).GetComponents(false) as object[] ?? new object[0]).OfType<Component2>().Select(c=>new LayoutOccurrence {
            Name=c.Name2, Context=SwInstanceBindings.Context(c), Path=c.GetPathName(), Configuration=c.ReferencedConfiguration,
            Reference=Convert.ToBase64String(root.Extension.GetPersistReference3(c) as byte[] ?? new byte[0]),
            Transform=c.Transform2==null?null:c.Transform2.ArrayData as double[], Suppression=c.GetSuppression()
        }).OrderBy(c=>c.Context).ToList();
    }
    static void Snapshot(string expectedRoot, string directory)
    {
        expectedRoot=Path.GetFullPath(expectedRoot); directory=Path.GetFullPath(directory);
        var app=Current(); var root=app.ActiveDoc as ModelDoc2;
        if(root==null || !Same(root.GetPathName(),expectedRoot)) throw new Exception("Wrong active assembly");
        if(Directory.Exists(directory)) throw new Exception("Snapshot directory must be new");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory,"disk")); Directory.CreateDirectory(Path.Combine(directory,"memory"));
        var snapshot=new LayoutSnapshot {Root=expectedRoot,Configuration=root.ConfigurationManager.ActiveConfiguration.Name,ProcessId=app.GetProcessID(),Components=Occurrences(root)};
        snapshot.DirtyBefore=(app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Where(d=>d.GetSaveFlag()).Select(d=>d.GetPathName()).ToList();
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){expectedRoot};
        foreach(var c in snapshot.Components) if(File.Exists(c.Path)) paths.Add(c.Path);
        var raw=app.GetDocumentDependencies2(expectedRoot,true,false,false) as string[];
        if(raw==null || raw.Length%2!=0) throw new Exception("Cannot enumerate dependencies");
        for(int i=1;i<raw.Length;i+=2) paths.Add(raw[i]);
        if(paths.GroupBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)) throw new Exception("Duplicate file names require a non-flat snapshot");
        foreach(var path in paths.OrderBy(p=>p))
        {
            if(!File.Exists(path)) throw new Exception("Missing model "+path);
            using(var lease=SwModelDocumentLease.Open(app,path,""))
            {
                var model=Read(lease.Document); model.Sha256=Hash(path); model.Copy=Path.Combine(directory,"disk",Path.GetFileName(path));
                Copy(path,model.Copy); snapshot.Models.Add(model);
            }
        }
        JsonFile.Write(Path.Combine(directory,"before.json"),snapshot);
        foreach(var model in snapshot.Models.Where(m=>m.Dirty).OrderBy(m=>Same(m.Path,expectedRoot)?1:0))
        {
            var doc=(app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Single(d=>Same(d.GetPathName(),model.Path));
            var copy=Path.Combine(directory,"memory",Path.GetFileName(model.Path));
            int a=0; app.ActivateDoc3(doc.GetTitle(),false,0,ref a);
            doc.ClearSelection2(true);
            int e=0,w=0; if(!doc.Extension.SaveAs(copy,0,3,null,ref e,ref w)||e!=0||!File.Exists(copy)) throw new Exception("Recovery copy failed "+model.Path+" errors="+e);
            if(!Same(doc.GetPathName(),model.Path)) throw new Exception("Copy changed source document path");
            if(!doc.GetSaveFlag()) doc.SetSaveFlag();
            Console.WriteLine("MEMORY_COPY="+model.Path+" warnings="+w);
        }
        int activation=0; app.ActivateDoc3(root.GetTitle(),false,0,ref activation);
        var after=Occurrences(root);
        if(after.Count!=snapshot.Components.Count || after.Any(c=>!snapshot.Components.Any(b=>c.Context==b.Context&&c.Path==b.Path&&c.Reference==b.Reference))) throw new Exception("Original tree changed during snapshot");
        foreach(var m in snapshot.Models) if(Hash(m.Path)!=m.Sha256) throw new Exception("Original file changed during snapshot: "+m.Path);
        JsonFile.Write(Path.Combine(directory,"after-components.json"),after);
        File.WriteAllText(Path.Combine(directory,"SUCCESS.txt"),"FILES="+snapshot.Models.Count+" COMPONENTS="+snapshot.Components.Count+" DIRTY_COPIES="+snapshot.Models.Count(m=>m.Dirty),Encoding.UTF8);
        Console.WriteLine(File.ReadAllText(Path.Combine(directory,"SUCCESS.txt")));
    }
    static void Localize(string directory,string reportPath)
    {
        LocalizeWith(Current(),directory,reportPath);
    }
    static void LocalizeWith(SldWorks app,string directory,string reportPath)
    {
        directory=Path.GetFullPath(directory);
        var allowed=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..","..","..","artifacts"))+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new Exception("Reference edits are restricted to workspace artifacts");
        var files=Directory.GetFiles(directory).Where(p=>new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).ToArray();
        var open=(app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Select(d=>d.GetPathName()).ToArray();
        if(files.Any(f=>open.Contains(f,StringComparer.OrdinalIgnoreCase))) throw new Exception("All target files must be closed");
        var changes=new List<string>();
        foreach(var file in files)
        {
            foreach(var dep in Dependencies(app,file).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(AssetSourcePathPolicy.IsSessionEmbeddedModelPath(dep)) continue;
                var target=Path.Combine(directory,Path.GetFileName(dep));
                if(!File.Exists(target)) throw new Exception("Missing local dependency: "+dep);
                if(Same(dep,target)) continue;
                if(!app.ReplaceReferencedDocument(file,dep,target)) throw new Exception("Reference replacement failed: "+file+" -> "+dep);
                changes.Add(file+" | "+dep+" -> "+target);
            }
            var remaining=Dependencies(app,file).Where(p=>!AssetSourcePathPolicy.IsSessionEmbeddedModelPath(p)&&!Same(Path.GetDirectoryName(p),directory)).ToList();
            if(remaining.Count!=0) throw new Exception("External references remain: "+file);
        }
        JsonFile.Write(reportPath,changes);
        Console.WriteLine("LOCALIZED_REFERENCES="+changes.Count+" FILES="+files.Length);
    }
    static void InspectIsolated(string directory,string reportPath,int originalPid,int ownedPid)
    {
        SldWorks app=null; int pid=0;
        try
        {
            app=ownedPid==0?StartIsolated(originalPid):FindProcess(ownedPid);
            if(app==null) throw new Exception("Owned inspection process is unavailable: "+ownedPid);
            pid=app.GetProcessID();
            if(pid==originalPid) throw new Exception("Isolated instance unexpectedly reused the original session");
            app.Visible=false; app.UserControl=false;
            Console.WriteLine("ISOLATED_PID="+pid);
            LocalizeWith(app,directory,reportPath+".references.json");
            var report=new LayoutSnapshot {Root=directory,ProcessId=pid};
            foreach(var path in Directory.GetFiles(directory).Where(p=>new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).OrderBy(p=>Path.GetExtension(p)).ThenBy(p=>p))
            {
                using(var lease=SwModelDocumentLease.Open(app,path,""))
                {
                    var entry=Read(lease.Document); entry.Sha256=Hash(path); report.Models.Add(entry);
                }
                if(report.Models.Count%10==0) Console.WriteLine("INSPECTED="+report.Models.Count);
            }
            JsonFile.Write(reportPath,report); Console.WriteLine("INSPECTED_TOTAL="+report.Models.Count);
        }
        finally { if(app!=null && pid!=0 && pid!=originalPid) { app.ExitApp(); Marshal.FinalReleaseComObject(app); } }
    }
    static ModelDoc2 OpenRoot(SldWorks app,string path)
    {
        app.DocumentVisible(true,2);
        int e=0,w=0;
        var root=app.OpenDoc6(path,2,(int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight),"",ref e,ref w) as ModelDoc2;
        if(root==null || e!=0 || !Same(root.GetPathName(),path)) throw new Exception("Cannot open isolated layout: "+e+" warnings="+w);
        int a=0; app.ActivateDoc3(root.GetTitle(),false,0,ref a);
        ((AssemblyDoc)root).ResolveAllLightWeightComponents(false);
        Console.WriteLine("OPENED_LAYOUT warnings="+w);
        return root;
    }
    static void InspectTree(string rootPath,string reportPath,int originalPid)
    {
        SldWorks app=null; int pid=0;
        try
        {
            app=StartIsolated(originalPid); pid=app.GetProcessID();
            if(pid==originalPid) throw new Exception("Original process is protected");
            app.Visible=false;app.UserControl=false;
            LocalizeWith(app,Path.GetDirectoryName(rootPath),reportPath+".references.json");
            var root=OpenRoot(app,rootPath);JsonFile.Write(reportPath,Occurrences(root));
            using(var owner=SwInstanceBindings.ForDocument(root)) {JsonFile.Write(reportPath+".bindings.json",owner.Current.Relations); Console.WriteLine("BINDINGS="+owner.Current.Relations.Count);}
            Console.WriteLine("TREE_INSPECTED");
        }
        finally{if(app!=null&&pid!=0&&pid!=originalPid){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
    static void Save(ModelDoc2 doc,string directory)
    {
        if(!Same(Path.GetDirectoryName(doc.GetPathName()),directory)) throw new Exception("Save target escaped working directory");
        int e=0,w=0;
        if(!doc.Save3(1,ref e,ref w)||e!=0) throw new Exception("Cannot save "+doc.GetPathName()+" errors="+e);
        if(w!=0) Console.WriteLine("SAVE_WARNING="+Path.GetFileName(doc.GetPathName())+" "+w);
    }
    static List<string> Diagnostics(SldWorks app,IEnumerable<string> files)
    {
        var issues=new List<string>();
        foreach(var path in files)
        using(var lease=SwModelDocumentLease.Open(app,path,""))
        {
            var doc=lease.Document; var count=doc.Extension.GetWhatsWrongCount(); if(count==0) continue;
            object features,codes,warnings;
            if(!doc.Extension.GetWhatsWrong(out features,out codes,out warnings)) throw new Exception("Cannot read diagnostics: "+path);
            var f=(Array)features; var c=(Array)codes; var w=(Array)warnings;
            for(int i=0;i<f.Length;i++)
            {
                var feature=f.GetValue(i) as Feature;
                issues.Add(Path.GetFileName(path)+" | "+(feature==null?Convert.ToString(f.GetValue(i)):feature.Name)+" | code="+c.GetValue(i)+" | warning="+w.GetValue(i));
            }
        }
        return issues.OrderBy(s=>s).ToList();
    }
    static void AuditCurrent(string snapshotPath,string reportPath,int expectedPid)
    {
        var snapshot=JsonFile.Read<LayoutSnapshot>(snapshotPath); var app=FindProcess(expectedPid);
        if(app==null || app.GetProcessID()!=expectedPid) throw new Exception("Original session unavailable");
        var issues=Diagnostics(app,snapshot.Models.Select(m=>m.Path)); JsonFile.Write(reportPath,issues);
        Console.WriteLine("ORIGINAL_DIAGNOSTIC_ITEMS="+issues.Count);
    }
    static Dictionary<string,string> Desired(LayoutUpdatePlan plan, LayoutModel model)
    {
        var desired=new Dictionary<string,string>(model.Raw,StringComparer.OrdinalIgnoreCase);
        var name=Path.GetFileName(model.Path);
        if(plan.AssetVersions.ContainsKey(name)) desired["asset_version"]=plan.AssetVersions[name];
        if(Same(model.Path,plan.Snapshot.Root)) desired["assembly_version"]=plan.AssemblyVersion;
        return desired;
    }
    static void CheckProperties(LayoutUpdatePlan plan,LayoutModel model,InterfacePropertySnapshot actual)
    {
        var desired=Desired(plan,model);
        if(desired.Count!=actual.Raw.Count || desired.Any(p=>!actual.Raw.ContainsKey(p.Key)||actual.Raw[p.Key]!=p.Value)) throw new Exception("Metadata mismatch: "+model.Path);
        if(model.Types.Any(p=>!actual.Types.ContainsKey(p.Key)||actual.Types[p.Key]!=p.Value)) throw new Exception("Property type mismatch: "+model.Path);
    }
    static void CheckTree(LayoutUpdatePlan plan,ModelDoc2 root,string reportPath)
    {
        var actual=Occurrences(root); JsonFile.Write(reportPath,actual);
        if(actual.Count!=plan.Snapshot.Components.Count) throw new Exception("Component count changed: "+actual.Count);
        foreach(var before in plan.Snapshot.Components)
        {
            var a=actual.SingleOrDefault(c=>c.Context==before.Context);
            if(a==null||a.Name!=before.Name||a.Reference!=before.Reference||a.Configuration!=before.Configuration||a.Suppression!=before.Suppression)
                throw new Exception("Component identity/configuration changed: "+before.Name);
            if(!Same(a.Path,Path.Combine(plan.Directory,Path.GetFileName(before.Path)))) throw new Exception("Component references an unexpected file: "+a.Path);
            if(!before.Context.Contains("/") && ((a.Transform==null)!=(before.Transform==null) || (a.Transform!=null && (a.Transform.Length!=before.Transform.Length || a.Transform.Where((v,i)=>Math.Abs(v-before.Transform[i])>1e-8).Any()))))
                throw new Exception("Component position changed: "+before.Name);
        }
        Console.WriteLine("PASS_COMPONENT_IDENTITIES="+actual.Count+" TOP_LEVEL_POSITIONS="+actual.Count(c=>!c.Context.Contains("/")));
    }
    static void Finish(string planPath,string reportPath)
    {
        var plan=JsonFile.Read<LayoutUpdatePlan>(planPath); SldWorks app=null; int pid=0;
        try
        {
            foreach(var u in plan.Updates) if(Hash(u.LatestSource)!=u.LatestSha256) throw new Exception("Latest source changed: "+u.Name);
            app=StartIsolated(plan.Snapshot.ProcessId); pid=app.GetProcessID();
            if(pid==plan.Snapshot.ProcessId) throw new Exception("Cannot operate on original process");
            app.Visible=false; app.UserControl=false; Console.WriteLine("WORKING_PID="+pid);
            LocalizeWith(app,plan.Directory,reportPath+".references.json");
            var rootPath=Path.Combine(plan.Directory,Path.GetFileName(plan.Snapshot.Root));
            var root=OpenRoot(app,rootPath);
            if((root.GetConfigurationNames() as string[]).Length!=1 || root.ConfigurationManager.ActiveConfiguration.Name!=plan.Snapshot.Configuration) throw new Exception("Unexpected configuration set");
            CheckTree(plan,root,reportPath+".tree-before.json");
            using(var owner=SwInstanceBindings.ForDocument(root))
            {
                var records=owner.Current.Relations;
                if(records.Count!=plan.ExpectedBindingCount) throw new Exception("The recovery copy did not preserve the latest unsaved binding count: "+records.Count);
                JsonFile.Write(reportPath+".bindings-before.json",records);
                var ids=records.Select(r=>r.Id).ToArray();
                foreach(var model in plan.Snapshot.Models.OrderBy(m=>Path.GetExtension(m.Path).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)?0:Same(m.Path,plan.Snapshot.Root)?2:1))
                {
                    var path=Path.Combine(plan.Directory,Path.GetFileName(model.Path));
                    using(var lease=SwModelDocumentLease.Open(app,path,""))
                    {
                        var doc=lease.Document;
                        if(!Same(doc.GetPathName(),path) || Read(doc).Created!=model.Created) throw new Exception("Model identity mismatch: "+path);
                        var target=new SwInterfacePropertyTarget(doc); var actual=target.Read(); var desired=Desired(plan,model);
                        foreach(var name in actual.Raw.Keys.Where(k=>!desired.ContainsKey(k)).ToArray()) target.Delete(name);
                        foreach(var p in desired) if(!actual.Raw.ContainsKey(p.Key)||actual.Raw[p.Key]!=p.Value)
                            target.Set(p.Key,p.Value,model.Types.ContainsKey(p.Key)?model.Types[p.Key]:30);
                        CheckProperties(plan,model,target.Read());
                        if(!Same(path,rootPath)&&doc.GetSaveFlag()) Save(doc,plan.Directory);
                    }
                }
                int a=0; app.ActivateDoc3(root.GetTitle(),false,0,ref a);
                root.EditRebuild3();
                CheckTree(plan,root,reportPath+".tree-updated.json");
                var issues=Diagnostics(app,plan.Snapshot.Models.Select(m=>Path.Combine(plan.Directory,Path.GetFileName(m.Path))));
                JsonFile.Write(reportPath+".diagnostics.json",issues);
                var baselineIssues=JsonFile.Read<List<string>>(Path.Combine(Path.GetDirectoryName(planPath),"original-diagnostics.json"));
                var addedIssues=issues.Except(baselineIssues).ToList();
                if(addedIssues.Count!=0) throw new Exception("New native model diagnostics: "+string.Join("; ",addedIssues.Take(8)));
                // Relocate only endpoints proven by the original persistent reference, context,
                // file name and creation identity. Never infer a replacement occurrence by name.
                foreach(var endpoint in records.SelectMany(r=>new[]{r.Parent,r.Child}))
                {
                    var component=owner.ResolveComponent(endpoint);
                    var before=plan.Snapshot.Components.Single(c=>c.Context==endpoint.Context);
                    if(!Same(before.Path,endpoint.SourcePath)||before.Reference!=endpoint.Reference) throw new Exception("Binding was not part of the captured layout");
                    var model=new SwCadNode(app,component,endpoint.DisplayPath).Model;
                    if(IdentityService.AssetIdentitySeed(model)!=endpoint.SourceIdentity) throw new Exception("Binding model identity changed: "+endpoint.DisplayName);
                    endpoint.SourcePath=Path.Combine(plan.Directory,Path.GetFileName(before.Path));
                    if(!Same(endpoint.SourcePath,component.GetPathName())) throw new Exception("Binding path relocation mismatch");
                }
                owner.Commit(new List<InstanceBinding>()); owner.Commit(records);
                Save(root,plan.Directory); owner.RequireSaved();
                var exportPlan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)),ProjectMeshFormat.Step,true);
                var relations=owner.Export(exportPlan);
                if(relations.Relations.Count!=plan.ExpectedBindingCount||!owner.Current.Relations.Select(r=>r.Id).SequenceEqual(ids)) throw new Exception("Binding verification failed");
                JsonFile.Write(reportPath+".bindings-after.json",owner.Current.Relations);
                JsonFile.Write(reportPath+".export-relations.json",relations);
                Console.WriteLine("PASS_BINDINGS_AND_EXPORT="+relations.Relations.Count);
            }
            // Reopen the saved files to verify persistence rather than only the current COM state.
            app.CloseAllDocuments(true);
            root=OpenRoot(app,rootPath); CheckTree(plan,root,reportPath+".tree-reopened.json");
            using(var owner=SwInstanceBindings.ForDocument(root))
            {
                var exportPlan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)),ProjectMeshFormat.Step,true);
                if(owner.Current.Relations.Count!=plan.ExpectedBindingCount||owner.Export(exportPlan).Relations.Count!=plan.ExpectedBindingCount) throw new Exception("Saved bindings failed reopening validation");
            }
            var result=new LayoutSnapshot {Root=rootPath,Configuration=plan.Snapshot.Configuration,ProcessId=pid,Components=Occurrences(root)};
            foreach(var model in plan.Snapshot.Models)
            {
                var path=Path.Combine(plan.Directory,Path.GetFileName(model.Path));
                using(var lease=SwModelDocumentLease.Open(app,path,""))
                {
                    CheckProperties(plan,model,new SwInterfacePropertyTarget(lease.Document).Read());
                    var after=Read(lease.Document); after.Sha256=Hash(path); result.Models.Add(after);
                }
            }
            JsonFile.Write(reportPath,result);
            File.WriteAllText(reportPath+".SUCCESS.txt","PART_UPDATES="+plan.Updates.Count+" COMPONENTS="+result.Components.Count+" BINDINGS="+plan.ExpectedBindingCount+" ASSEMBLY_VERSION="+plan.AssemblyVersion,Encoding.UTF8);
            Console.WriteLine("PASS_SAVED_AND_REOPENED_LAYOUT");
        }
        finally { if(app!=null&&pid!=0&&pid!=plan.Snapshot.ProcessId) {app.ExitApp();Marshal.FinalReleaseComObject(app);} }
    }
}
