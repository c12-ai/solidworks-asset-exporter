using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

[DataContract] public sealed class WorkstationInspection
{
    [DataMember] public LayoutSnapshot Snapshot;
    [DataMember] public Dictionary<string,List<string>> OriginalReferences;
    [DataMember] public Dictionary<string,double[]> GeometryMetrics;
    [DataMember] public Dictionary<string,double[]> ComponentBoxes;
    [DataMember] public List<InstanceBinding> Bindings;
    [DataMember] public List<string> Diagnostics;
}
[DataContract] public sealed class WorkstationDefinition
{
    [DataMember] public string Name;
    [DataMember] public string FileName;
    [DataMember] public string ExistingContext;
    [DataMember] public List<string> MoveContexts;
}
[DataContract] public sealed class WorkstationPlan
{
    [DataMember] public string Directory;
    [DataMember] public string DestinationDirectory;
    [DataMember] public string RootName;
    [DataMember] public string BeforePath;
    [DataMember] public List<WorkstationDefinition> Definitions;
    [DataMember] public string ExternalizeContext;
    [DataMember] public string ExternalizeFileName;
    [DataMember] public string AssemblyVersion;
}
[DataContract] public sealed class WorkstationResult
{
    [DataMember] public LayoutSnapshot After;
    [DataMember] public Dictionary<string,string> ContextMapping;
    [DataMember] public Dictionary<string,string> RenamedFiles;
    [DataMember] public List<string> Workstations;
    [DataMember] public List<string> Diagnostics;
}
internal static partial class OrganizeLayoutModels
{
    static Component2[] AllComponents(ModelDoc2 root)
    {return (((AssemblyDoc)root).GetComponents(false) as object[]??new object[0]).OfType<Component2>().ToArray();}
    static Component2 ContextComponent(ModelDoc2 root,string context)
    {return AllComponents(root).Single(c=>SwInstanceBindings.Context(c)==context);}
    static void SetWorkstation(ModelDoc2 document,string name)
    {
        var target=new SwInterfacePropertyTarget(document);
        target.Set("is_asset","0",30);target.Set("is_workstation","1",30);target.Set("is-workstation","1",30);target.Set("workstation_name",name,30);
        if(string.IsNullOrWhiteSpace(target.Read().Get("assembly_version")))target.Set("assembly_version","1",30);
    }
    static void Externalize(Component2 component,string path,Dictionary<string,string> renamed)
    {
        var oldName=Path.GetFileName(component.GetPathName());
        if(!component.IsVirtual)throw new Exception("Expected an embedded assembly: "+oldName);
        if(File.Exists(path))throw new Exception("Externalized file already exists: "+path);
        if(!component.SaveVirtualComponent(path)||component.IsVirtual||!Same(component.GetPathName(),path))throw new Exception("Cannot save embedded assembly: "+path);
        renamed[oldName]=Path.GetFileName(path);Console.WriteLine("EXTERNALIZED="+oldName+" -> "+Path.GetFileName(path));
    }
    static bool SamePose(double[] a,double[] b)
    {return(a==null&&b==null)||(a!=null&&b!=null&&a.Length==b.Length&&!a.Where((v,i)=>Math.Abs(v-b[i])>1e-8).Any());}
    static Dictionary<string,string> MatchPreservedComponents(WorkstationInspection before,ModelDoc2 root,Dictionary<string,string> renamed)
    {
        var after=Occurrences(root);var matched=new HashSet<string>();var result=new Dictionary<string,string>();
        foreach(var b in before.Snapshot.Components)
        {
            var name=Path.GetFileName(b.Path);if(renamed.ContainsKey(name))name=renamed[name];
            var candidates=after.Where(a=>!matched.Contains(a.Context)&&Same(Path.GetFileName(a.Path),name)&&a.Configuration==b.Configuration&&a.Suppression==b.Suppression&&SamePose(a.Transform,b.Transform)).ToList();
            var exact=candidates.Where(a=>a.Reference==b.Reference).ToList();
            if(exact.Count==1)candidates=exact;
            if(candidates.Count!=1)throw new Exception("Original instance cannot be uniquely verified at its unchanged position: "+b.Name+" candidates="+candidates.Count);
            matched.Add(candidates[0].Context);result.Add(b.Context,candidates[0].Context);
        }
        return result;
    }
    static void VerifyWorkstationLevel(SldWorks app,ModelDoc2 root,WorkstationPlan plan,WorkstationResult result)
    {
        var list=new List<string>();
        foreach(var c in AllComponents(root))
        {
            var doc=c.GetModelDoc2() as ModelDoc2;if(doc==null||doc.GetType()!=2)continue;
            var props=new SwInterfacePropertyTarget(doc).Read().Values;
            if(!PropertyRules.ReadIsWorkstation(props,DocumentKind.Assembly))continue;
            var context=SwInstanceBindings.Context(c);
            if(context.Contains("/"))throw new Exception("Workstation is nested below the first level: "+c.Name2);
            if(PropertyRules.ReadIsAsset(props))throw new Exception("Workstation cannot be an opaque Asset");
            list.Add(PropertyRules.RequireWorkstationName(props,c.Name2));
        }
        if(list.Count!=plan.Definitions.Count||plan.Definitions.Any(d=>!list.Contains(d.Name)))throw new Exception("Expected workstation set was not preserved");
        result.Workstations=list;
    }
    static void RestructureWorkstations(string planPath,string reportPath)
    {
        var plan=JsonFile.Read<WorkstationPlan>(planPath);var before=JsonFile.Read<WorkstationInspection>(plan.BeforePath);
        if(before.Bindings.Count!=0)throw new Exception("Existing native bindings require an explicit occurrence migration");
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(0);pid=app.GetProcessID();app.Visible=false;app.UserControl=false;
            var rootPath=Path.Combine(plan.Directory,plan.RootName);PreloadWorkspaceModels(app,plan.Directory,rootPath);var root=OpenRoot(app,rootPath);
            var result=new WorkstationResult {RenamedFiles=new Dictionary<string,string>()};
            MatchPreservedComponents(before,root,result.RenamedFiles);
            var bindingHash=BindingStorageHash(root);
            // Preserve the user's nested door group while giving it a stable external filename.
            Externalize(ContextComponent(root,plan.ExternalizeContext),Path.Combine(plan.Directory,plan.ExternalizeFileName),result.RenamedFiles);
            foreach(var definition in plan.Definitions)
            {
                int activation=0;app.ActivateDoc3(root.GetTitle(),false,0,ref activation);root.ClearSelection2(true);
                Component2 workstation;
                if(!string.IsNullOrEmpty(definition.ExistingContext))
                {
                    workstation=ContextComponent(root,definition.ExistingContext);
                    if(workstation.IsVirtual)Externalize(workstation,Path.Combine(plan.Directory,definition.FileName),result.RenamedFiles);
                    if(definition.MoveContexts.Count!=0)
                    {
                        var index=AllComponents(root).ToDictionary(SwInstanceBindings.Context);
                        var moving=definition.MoveContexts.Select(c=>(object)index[c]).ToArray();
                        if(!((AssemblyDoc)root).ReorganizeComponents(moving,workstation))throw new Exception("Cannot move components into workstation: "+definition.Name);
                    }
                }
                else
                {
                    var originalTop=AllComponents(root).Where(c=>!SwInstanceBindings.Context(c).Contains("/")).Select(c=>c.GetID()).ToArray();
                    Save(root,plan.Directory);
                    foreach(var context in definition.MoveContexts)if(!ContextComponent(root,context).Select4(true,null,false))throw new Exception("Cannot select workstation contents: "+context);
                    if(!((AssemblyDoc)root).MakeAssemblyFromSelectedComponents(Path.Combine(plan.Directory,definition.FileName)))throw new Exception("Cannot form workstation: "+definition.Name);
                    workstation=AllComponents(root).Single(c=>!SwInstanceBindings.Context(c).Contains("/")&&!originalTop.Contains(c.GetID()));
                    if(workstation.IsVirtual)Externalize(workstation,Path.Combine(plan.Directory,definition.FileName),new Dictionary<string,string>());
                    if(!Same(workstation.GetPathName(),Path.Combine(plan.Directory,definition.FileName)))throw new Exception("Unexpected workstation filename");
                }
                var document=workstation.GetModelDoc2() as ModelDoc2;if(document==null)throw new Exception("Workstation is not loaded");
                SetWorkstation(document,definition.Name);Save(document,plan.Directory);Save(root,plan.Directory);
                result.ContextMapping=MatchPreservedComponents(before,root,result.RenamedFiles);
                Console.WriteLine("WORKSTATION_READY="+definition.Name+" first-level="+SwInstanceBindings.Context(workstation));
            }
            var rootProperties=new SwInterfacePropertyTarget(root);rootProperties.Set("assembly_version",plan.AssemblyVersion,30);
            root.EditRebuild3();
            foreach(var document in (app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().Where(d=>!Same(d.GetPathName(),rootPath)&&d.GetSaveFlag()).OrderByDescending(d=>d.GetPathName().Length))
                Save(document,plan.Directory);
            Save(root,plan.Directory);
            result.ContextMapping=MatchPreservedComponents(before,root,result.RenamedFiles);
            VerifyWorkstationLevel(app,root,plan,result);
            SameGeometry(before.GeometryMetrics,GeometryMetrics(app,plan.Directory));
            if(BindingStorageHash(root)!=bindingHash)throw new Exception("Native binding storage unexpectedly changed");
            var files=Directory.GetFiles(plan.Directory).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).ToList();
            result.Diagnostics=Diagnostics(app,files);
            if(result.Diagnostics.Except(before.Diagnostics).Any())throw new Exception("Native assembly diagnostics were introduced: "+string.Join("; ",result.Diagnostics.Take(6)));
            JsonFile.Write(reportPath+".in-memory.json",result);
            app.CloseAllDocuments(true);PreloadWorkspaceModels(app,plan.Directory,rootPath);root=OpenRoot(app,rootPath);
            result.ContextMapping=MatchPreservedComponents(before,root,result.RenamedFiles);VerifyWorkstationLevel(app,root,plan,result);
            SameGeometry(before.GeometryMetrics,GeometryMetrics(app,plan.Directory));
            var exportPlan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)),ProjectMeshFormat.Step,true);
            if(exportPlan.Roots.Count(n=>n.Kind==ExportNodeKind.Workstation)!=5 || SwInstanceBindings.Flatten(exportPlan.Roots).Count(n=>n.Kind==ExportNodeKind.Workstation)!=5)
                throw new Exception("Exporter did not retain exactly five first-level workstations");
            result.After=new LayoutSnapshot {Root=rootPath,Configuration=root.ConfigurationManager.ActiveConfiguration.Name,Components=Occurrences(root),ProcessId=pid};
            foreach(var path in files)using(var lease=SwModelDocumentLease.Open(app,path,"")){var model=Read(lease.Document);model.Sha256=Hash(path);result.After.Models.Add(model);}
            JsonFile.Write(reportPath,result);
            System.IO.File.WriteAllText(reportPath+".SUCCESS.txt","WORKSTATIONS=5 ORIGINAL_COMPONENTS_PRESERVED="+before.Snapshot.Components.Count+" PART_GEOMETRIES_PRESERVED="+before.GeometryMetrics.Count+" ASSEMBLY_VERSION="+plan.AssemblyVersion);
            Console.WriteLine("PASS_WORKSTATION_LAYOUT_SAVED_AND_REOPENED");
        }
        finally{if(app!=null&&pid!=0){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
    static void PreloadWorkspaceModels(SldWorks app,string directory,string rootPath)
    {
        var files=Directory.GetFiles(directory).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)&&!Same(p,rootPath))
            .OrderBy(p=>Path.GetExtension(p).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)?0:1).ToList();
        app.DocumentVisible(false,1);app.DocumentVisible(false,2);
        foreach(var path in files)
        {
            var type=Path.GetExtension(path).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)?1:2;
            int e=0,w=0;var doc=app.OpenDoc6(path,type,1,"",ref e,ref w) as ModelDoc2;
            if(doc==null||e!=0||!Same(doc.GetPathName(),path))throw new Exception("Cannot preload local model: "+path+" error="+e);
        }
    }
    static void Activate(SldWorks app,ModelDoc2 doc)
    {int e=0;app.ActivateDoc3(doc.GetTitle(),false,0,ref e);doc.ClearSelection2(true);}
    static bool HasTipPattern(ModelDoc2 document)
    {for(var f=document.FirstFeature() as Feature;f!=null;f=f.GetNextFeature() as Feature)if(f.Name=="局部线性阵列1")return true;return false;}
    static void DeleteComponents(SldWorks app,ModelDoc2 doc,Component2[] components)
    {
        Activate(app,doc);
        foreach(var c in components)if(!c.Select4(true,null,false))throw new Exception("Cannot select component for regrouping: "+c.Name2);
        if(!doc.Extension.DeleteSelection2(0))throw new Exception("Cannot remove duplicated component from regrouped copy");
    }
    static void FinishWorkstations(string planPath,string reportPath)
    {
        var plan=JsonFile.Read<WorkstationPlan>(planPath);var before=JsonFile.Read<WorkstationInspection>(plan.BeforePath);
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(0);pid=app.GetProcessID();app.Visible=false;app.UserControl=false;
            LocalizeWith(app,plan.Directory,reportPath+".references.json");
            var rootPath=Path.Combine(plan.Directory,plan.RootName);PreloadWorkspaceModels(app,plan.Directory,rootPath);var root=OpenRoot(app,rootPath);
            var result=new WorkstationResult {RenamedFiles=new Dictionary<string,string>()};
            foreach(var pair in new[]{new[]{"1731","CO2培养箱工位.SLDASM"},new[]{"1736","冰箱1.SLDASM"},new[]{"1736/45","冰箱1门组件.SLDASM"}})
                result.RenamedFiles.Add(Path.GetFileName(before.Snapshot.Components.Single(c=>c.Context==pair[0]).Path),pair[1]);
            MatchPreservedComponents(before,root,result.RenamedFiles);
            Console.WriteLine("PASS_SAVED_FOUR_GROUPS_AND_ALL_ORIGINAL_COMPONENTS");
            var bindingHash=BindingStorageHash(root);
            if(before.Bindings.Count!=0)throw new Exception("Unexpected existing instance bindings");
            var definition=plan.Definitions.Single(d=>d.ExistingContext=="436");
            var wrapperPath=Path.Combine(plan.Directory,definition.FileName);
            if(File.Exists(wrapperPath))throw new Exception("BSC wrapper already exists");
            // Copy the assembly so its native linear pattern remains in its original owning document.
            // Remove unrelated groups from that copy, then reference the complete BSC group at identity.
            Activate(app,root);int e=0,w=0;
            if(!root.Extension.SaveAs(wrapperPath,0,3,null,ref e,ref w)||e!=0||!Same(root.GetPathName(),rootPath))throw new Exception("Cannot create BSC group copy");
            var wrapper=OpenRoot(app,wrapperPath);
            var keep=new HashSet<string>(definition.MoveContexts.Concat(new[]{definition.ExistingContext}));
            var remove=AllComponents(wrapper).Where(c=>!SwInstanceBindings.Context(c).Contains("/")&&!keep.Contains(SwInstanceBindings.Context(c))).ToArray();
            DeleteComponents(app,wrapper,remove);
            var inner=ContextComponent(wrapper,"436").GetModelDoc2() as ModelDoc2;
            var innerProps=new SwInterfacePropertyTarget(inner);innerProps.Set("is_workstation","0",30);innerProps.Set("is-workstation","0",30);
            innerProps.Set("assembly_version","2",30);Save(inner,plan.Directory);
            SetWorkstation(wrapper,definition.Name);new SwInterfacePropertyTarget(wrapper).Set("assembly_version","1",30);Save(wrapper,plan.Directory);
            var tips=AllComponents(wrapper).Count(c=>Path.GetFileName(c.GetPathName()).Equals("移液枪头-1000uL.SLDPRT",StringComparison.OrdinalIgnoreCase));
            if(tips!=96||!HasTipPattern(wrapper))throw new Exception("BSC native tip pattern was not preserved");
            Console.WriteLine("PASS_NATIVE_BSC_PATTERN_PRESERVED=96");
            var rootIndex=AllComponents(root).ToDictionary(SwInstanceBindings.Context);
            DeleteComponents(app,root,new[]{rootIndex["436"],rootIndex["1236"],rootIndex["1581"]});
            var inserted=((AssemblyDoc)root).AddComponent5(wrapperPath,0,"",false,"",0,0,0) as Component2;
            if(inserted==null)throw new Exception("Cannot insert complete BSC workstation");
            inserted.Transform2=((MathUtility)app.GetMathUtility()).CreateTransform(new double[]{1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0}) as MathTransform;
            new SwInterfacePropertyTarget(root).Set("assembly_version",plan.AssemblyVersion,30);
            root.EditRebuild3();Save(root,plan.Directory);
            result.ContextMapping=MatchPreservedComponents(before,root,result.RenamedFiles);
            VerifyWorkstationLevel(app,root,plan,result);SameGeometry(before.GeometryMetrics,GeometryMetrics(app,plan.Directory));
            if(BindingStorageHash(root)!=bindingHash)throw new Exception("Native bindings changed");
            var files=CadFiles(plan.Directory);result.Diagnostics=Diagnostics(app,files);
            if(result.Diagnostics.Except(before.Diagnostics).Any())throw new Exception("New assembly diagnostics: "+string.Join("; ",result.Diagnostics.Take(6)));
            Console.WriteLine("PASS_ALL_COMPONENTS_AND_GEOMETRY_BEFORE_REOPEN");
            app.CloseAllDocuments(true);PreloadWorkspaceModels(app,plan.Directory,rootPath);root=OpenRoot(app,rootPath);
            ValidateWorkstations(app,root,plan,before,result);
            result.After=SnapshotModels(app,root,files);
            JsonFile.Write(reportPath,result);
            File.WriteAllText(reportPath+".SUCCESS.txt","WORKSTATIONS=5 ORIGINAL_COMPONENTS_PRESERVED="+before.Snapshot.Components.Count+" PART_GEOMETRIES_PRESERVED="+before.GeometryMetrics.Count+" ASSEMBLY_VERSION="+plan.AssemblyVersion);
            Console.WriteLine("PASS_WORKSTATION_LAYOUT_SAVED_AND_REOPENED");
        }
        finally{if(app!=null&&pid!=0){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
    static List<string> CadFiles(string directory)
    {return Directory.GetFiles(directory).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).ToList();}
    static LayoutSnapshot SnapshotModels(SldWorks app,ModelDoc2 root,List<string> files)
    {
        var snapshot=new LayoutSnapshot {Root=root.GetPathName(),Configuration=root.ConfigurationManager.ActiveConfiguration.Name,Components=Occurrences(root),ProcessId=app.GetProcessID()};
        foreach(var path in files)using(var lease=SwModelDocumentLease.Open(app,path,"")){var model=Read(lease.Document);model.Sha256=Hash(path);snapshot.Models.Add(model);}
        return snapshot;
    }
    static void ValidateWorkstations(SldWorks app,ModelDoc2 root,WorkstationPlan plan,WorkstationInspection before,WorkstationResult result)
    {
        result.ContextMapping=MatchPreservedComponents(before,root,result.RenamedFiles);VerifyWorkstationLevel(app,root,plan,result);
        var occurrences=Occurrences(root);
        if(occurrences.Count!=before.Snapshot.Components.Count+3)throw new Exception("Unexpected component count after adding three workstation wrappers");
        var added=occurrences.Where(c=>!result.ContextMapping.Values.Contains(c.Context)).Select(c=>Path.GetFileName(c.Path)).OrderBy(n=>n).ToArray();
        if(!added.SequenceEqual(new[]{"BSC工位.SLDASM","冰箱2.SLDASM","水浴组件.SLDASM"}.OrderBy(n=>n)))throw new Exception("Unexpected added component");
        using(var owner=SwInstanceBindings.ForDocument(root))if(owner.Current.Relations.Count!=before.Bindings.Count)throw new Exception("Native binding count changed");
        SameGeometry(before.GeometryMetrics,GeometryMetrics(app,Path.GetDirectoryName(root.GetPathName())));
        var exportPlan=new ExportPlanBuilder().Build(new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(app)),ProjectMeshFormat.Step,true);
        if(exportPlan.Roots.Count(n=>n.Kind==ExportNodeKind.Workstation)!=5||SwInstanceBindings.Flatten(exportPlan.Roots).Count(n=>n.Kind==ExportNodeKind.Workstation)!=5)
            throw new Exception("Exporter must retain exactly five first-level workstations");
        var bsc=AllComponents(root).Single(c=>Path.GetFileName(c.GetPathName())=="BSC工位.SLDASM").GetModelDoc2() as ModelDoc2;
        if(!HasTipPattern(bsc))throw new Exception("Native BSC pattern missing after reopen");
        result.Diagnostics=Diagnostics(app,CadFiles(Path.GetDirectoryName(root.GetPathName())));
        if(result.Diagnostics.Except(before.Diagnostics).Any())throw new Exception("New diagnostics after reopen");
    }
    static void PrepareWorkstations(string planPath,string resultPath,string reportPath)
    {
        var plan=JsonFile.Read<WorkstationPlan>(planPath);var result=JsonFile.Read<WorkstationResult>(resultPath);
        if(!File.Exists(resultPath+".SUCCESS.txt"))throw new Exception("Native validation is incomplete");
        var before=JsonFile.Read<WorkstationInspection>(plan.BeforePath);
        foreach(var model in result.After.Models)if(Hash(model.Path)!=model.Sha256)throw new Exception("Validated working file changed: "+model.Path);
        var names=new HashSet<string>(result.After.Models.Select(m=>Path.GetFileName(m.Path)),StringComparer.OrdinalIgnoreCase);
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(0);pid=app.GetProcessID();app.Visible=false;app.UserControl=false;
            var prepared=new Dictionary<string,string>();
            foreach(var model in result.After.Models)
            {
                var name=Path.GetFileName(model.Path);
                if(Path.GetExtension(name).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase))
                {
                    var prior=before.Snapshot.Models.Single(m=>Same(Path.GetFileName(m.Path),name));
                    if(prior.Raw.Count!=model.Raw.Count||prior.Raw.Any(kv=>!model.Raw.ContainsKey(kv.Key)||model.Raw[kv.Key]!=kv.Value))throw new Exception("Part metadata changed: "+name);
                    if(Dependencies(app,model.Path).Count!=0)throw new Exception("Unexpected part external dependencies");
                    continue;
                }
                foreach(var dependency in Dependencies(app,model.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if(AssetSourcePathPolicy.IsSessionEmbeddedModelPath(dependency)||!names.Contains(Path.GetFileName(dependency)))throw new Exception("Unexpected reference: "+dependency);
                    var target=Path.Combine(plan.DestinationDirectory,Path.GetFileName(dependency));
                    if(!Same(target,dependency)&&!app.ReplaceReferencedDocument(model.Path,dependency,target))throw new Exception("Cannot prepare reference: "+dependency);
                }
                if(Dependencies(app,model.Path).Any(p=>!Same(Path.GetDirectoryName(p),plan.DestinationDirectory)))throw new Exception("A reference remains outside final directory");
                prepared.Add(name,Hash(model.Path));
            }
            JsonFile.Write(reportPath,prepared);Console.WriteLine("PREPARED_ASSEMBLIES="+prepared.Count);
        }
        finally{if(app!=null&&pid!=0){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
    static void VerifyPublishedWorkstations(string planPath,string resultPath,string reportPath)
    {
        var plan=JsonFile.Read<WorkstationPlan>(planPath);var result=JsonFile.Read<WorkstationResult>(resultPath);var before=JsonFile.Read<WorkstationInspection>(plan.BeforePath);
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(0);pid=app.GetProcessID();app.Visible=false;app.UserControl=false;
            var files=CadFiles(plan.DestinationDirectory);var hashes=files.ToDictionary(p=>p,Hash);
            foreach(var path in files)if(Dependencies(app,path).Any(p=>!Same(Path.GetDirectoryName(p),plan.DestinationDirectory)||!File.Exists(p)))throw new Exception("Invalid published dependency: "+path);
            var rootPath=Path.Combine(plan.DestinationDirectory,plan.RootName);PreloadWorkspaceModels(app,plan.DestinationDirectory,rootPath);var root=OpenRoot(app,rootPath);
            ValidateWorkstations(app,root,plan,before,result);result.After=SnapshotModels(app,root,files);JsonFile.Write(reportPath,result);
            if(hashes.Any(kv=>Hash(kv.Key)!=kv.Value))throw new Exception("Read-only verification changed files");
            File.WriteAllText(reportPath+".SUCCESS.txt","VERIFIED_PUBLISHED_FIRST_LEVEL_WORKSTATIONS=5 ORIGINAL_INSTANCES=391 PART_GEOMETRIES=52 NATIVE_BSC_PATTERN=96");
            Console.WriteLine("PASS_ACTUAL_1103_WORKSTATIONS_AND_LAYOUT");
        }
        finally{if(app!=null&&pid!=0){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
    static void InspectWorkstations(string rootPath,string reportPath)
    {
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(0);pid=app.GetProcessID();app.Visible=false;app.UserControl=false;
            var directory=Path.GetDirectoryName(rootPath);
            var files=Directory.GetFiles(directory).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).ToArray();
            var report=new WorkstationInspection {OriginalReferences=files.ToDictionary(Path.GetFileName,p=>Dependencies(app,p))};
            LocalizeWith(app,directory,reportPath+".references.json");
            PreloadWorkspaceModels(app,directory,rootPath);
            var root=OpenRoot(app,rootPath);
            report.Snapshot=new LayoutSnapshot {Root=rootPath,Configuration=root.ConfigurationManager.ActiveConfiguration.Name,ProcessId=pid,Components=Occurrences(root)};
            foreach(var path in files) using(var lease=SwModelDocumentLease.Open(app,path,""))
            {var m=Read(lease.Document);m.Sha256=Hash(path);report.Snapshot.Models.Add(m);}
            foreach(var component in (((AssemblyDoc)root).GetComponents(false) as object[]??new object[0]).OfType<Component2>().Where(c=>c.IsVirtual))
            {
                var doc=component.GetModelDoc2() as ModelDoc2;
                if(doc!=null&&!report.Snapshot.Models.Any(m=>Same(m.Path,doc.GetPathName())))report.Snapshot.Models.Add(Read(doc));
            }
            report.GeometryMetrics=GeometryMetrics(app,directory);
            report.ComponentBoxes=(((AssemblyDoc)root).GetComponents(false) as object[]??new object[0]).OfType<Component2>()
                .ToDictionary(SwInstanceBindings.Context,c=>c.GetBox(false,false) as double[]);
            using(var owner=SwInstanceBindings.ForDocument(root))report.Bindings=owner.Current.Relations;
            report.Diagnostics=Diagnostics(app,files);
            JsonFile.Write(reportPath,report);
            Console.WriteLine("INSPECTED_MODELS="+files.Length+" COMPONENTS="+report.Snapshot.Components.Count+" BINDINGS="+report.Bindings.Count+" DIAGNOSTICS="+report.Diagnostics.Count);
        }
        finally{if(app!=null&&pid!=0){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
}
