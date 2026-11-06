using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

[DataContract] public sealed class PropertySyncPair
{
    [DataMember] public string DestinationName;
    [DataMember] public LayoutModel Source;
    [DataMember] public LayoutModel Latest;
}
[DataContract] public sealed class PropertySyncPlan
{
    [DataMember] public int SourceProcessId;
    [DataMember] public string SourceRoot;
    [DataMember] public string Directory;
    [DataMember] public string DestinationDirectory;
    [DataMember] public string RootName;
    [DataMember] public List<PropertySyncPair> Pairs;
}
[DataContract] public sealed class PropertySyncResult
{
    [DataMember] public List<LayoutModel> Models = new List<LayoutModel>();
    [DataMember] public List<string> ChangedFiles = new List<string>();
    [DataMember] public Dictionary<string,List<string>> OriginalReferences;
    [DataMember] public Dictionary<string,string> GeometryBefore;
    [DataMember] public Dictionary<string,string> GeometryAfter;
    [DataMember] public Dictionary<string,double[]> GeometryMetricsBefore;
    [DataMember] public Dictionary<string,double[]> GeometryMetricsAfterReopen;
    [DataMember] public List<LayoutOccurrence> ComponentsBefore;
    [DataMember] public List<LayoutOccurrence> ComponentsAfter;
    [DataMember] public string BindingStorageBefore;
    [DataMember] public string BindingStorageAfter;
}
internal static partial class OrganizeLayoutModels
{
    static void CheckPropertySyncTarget(string planPath)
    {
        var plan=JsonFile.Read<PropertySyncPlan>(planPath);var app=FindProcess(plan.SourceProcessId);
        if(app==null)throw new Exception("Original session unavailable; review open model state before publication");
        var docs=(app.GetDocuments() as object[]??new object[0]).OfType<ModelDoc2>().ToArray();
        var prefix=Path.GetFullPath(plan.DestinationDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(docs.Any(d=>d.GetPathName().StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new Exception("A destination model is open; cannot replace its file under the active document");
        Console.WriteLine("PASS_DESTINATION_MODELS_NOT_OPEN");
        foreach(var doc in docs.Where(d=>d.GetSaveFlag()))Console.WriteLine("PRESERVED_UNSAVED="+doc.GetPathName());
    }
    static string ByteHash(byte[] bytes)
    { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",""); }
    static string BindingStorageHash(ModelDoc2 doc)
    {
        object raw=null;
        try
        {
            raw=doc.IGet3rdPartyStorage("SWAssetExporter.Bindings.v1",false); if(raw==null) return "absent";
            var stream=(IStream)raw;System.Runtime.InteropServices.ComTypes.STATSTG stat;stream.Stat(out stat,1);
            if(stat.cbSize<0||stat.cbSize>16*1024*1024) throw new Exception("Invalid binding storage length");
            var bytes=new byte[(int)stat.cbSize];var count=Marshal.AllocCoTaskMem(4);
            try {stream.Seek(0,0,IntPtr.Zero);stream.Read(bytes,bytes.Length,count);if(Marshal.ReadInt32(count)!=bytes.Length)throw new Exception("Incomplete binding storage read");}
            finally {Marshal.FreeCoTaskMem(count);}
            return ByteHash(bytes);
        }
        finally {if(raw!=null&&Marshal.IsComObject(raw))Marshal.ReleaseComObject(raw);doc.IRelease3rdPartyStorage("SWAssetExporter.Bindings.v1");}
    }
    static Dictionary<string,string> Geometry(SldWorks app,string directory)
    {
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in Directory.GetFiles(directory,"*.SLDPRT").Where(p=>!Path.GetFileName(p).StartsWith("~$")).OrderBy(p=>p))
        using(var lease=SwModelDocumentLease.Open(app,path,""))
        {
            var doc=lease.Document; if(!Same(doc.GetPathName(),path))throw new Exception("Wrong geometry document");
            var triangles=((PartDoc)doc).GetTessTriangles(true) as float[];
            if(triangles==null||triangles.Length==0)throw new Exception("Cannot fingerprint part geometry: "+path);
            var bytes=new byte[triangles.Length*sizeof(float)];Buffer.BlockCopy(triangles,0,bytes,0,bytes.Length);
            hashes.Add(Path.GetFileName(path)+" | "+doc.ConfigurationManager.ActiveConfiguration.Name,ByteHash(bytes));
        }
        return hashes;
    }
    static Dictionary<string,double[]> GeometryMetrics(SldWorks app,string directory)
    {
        var result=new Dictionary<string,double[]>();
        foreach(var path in Directory.GetFiles(directory,"*.SLDPRT").Where(p=>!Path.GetFileName(p).StartsWith("~$")).OrderBy(p=>p))
        using(var lease=SwModelDocumentLease.Open(app,path,""))
        {
            var doc=lease.Document;
            var bodies=(((PartDoc)doc).GetBodies2(-1,false) as object[]??new object[0]).OfType<Body2>().OrderBy(b=>b.Name).ToArray();
            var data=new List<double>{bodies.Length};
            foreach(var body in bodies)
            {
                data.Add(body.GetType());data.Add(body.GetFaceCount());data.Add(body.GetEdgeCount());
                foreach(var values in new[]{body.GetBodyBox() as double[],body.GetMassProperties(1.0) as double[]})
                { data.Add(values==null?0:values.Length);if(values!=null)data.AddRange(values); }
            }
            result.Add(Path.GetFileName(path)+" | "+doc.ConfigurationManager.ActiveConfiguration.Name,data.ToArray());
        }
        return result;
    }
    static void SameGeometry(Dictionary<string,double[]> before,Dictionary<string,double[]> after)
    {
        if(before.Count!=after.Count)throw new Exception("Part geometry count changed");
        foreach(var p in before)
        {
            double[] values;
            if(!after.TryGetValue(p.Key,out values)||values.Length!=p.Value.Length)throw new Exception("Body topology changed: "+p.Key);
            for(int i=0;i<values.Length;i++) if(double.IsNaN(values[i])||Math.Abs(values[i]-p.Value[i])>1e-12+1e-9*Math.Max(Math.Abs(values[i]),Math.Abs(p.Value[i])))
                throw new Exception("Body geometry changed: "+p.Key+" value="+i+" before="+p.Value[i].ToString("R")+" after="+values[i].ToString("R"));
        }
    }
    static void SameLayout(List<LayoutOccurrence> before,List<LayoutOccurrence> after)
    {
        if(before.Count!=after.Count)throw new Exception("Latest assembly component count changed");
        foreach(var b in before)
        {
            var a=after.SingleOrDefault(c=>c.Context==b.Context);
            if(a==null||a.Name!=b.Name||a.Path!=b.Path||a.Configuration!=b.Configuration||a.Reference!=b.Reference||a.Suppression!=b.Suppression)
                throw new Exception("Latest assembly structure changed: "+b.Name);
            if((a.Transform==null)!=(b.Transform==null)||(a.Transform!=null&&(a.Transform.Length!=b.Transform.Length||a.Transform.Where((v,i)=>Math.Abs(v-b.Transform[i])>1e-9).Any())))
                throw new Exception("Latest assembly placement changed: "+b.Name);
        }
    }
    static void SyncProperties(string planPath,string reportPath)
    {
        var plan=JsonFile.Read<PropertySyncPlan>(planPath);
        // Read the current source session again; do not silently synchronize a stale snapshot.
        var sourceApp=FindProcess(plan.SourceProcessId);
        if(sourceApp==null)throw new Exception("Source SOLIDWORKS session is unavailable");
        foreach(var pair in plan.Pairs)
        using(var lease=SwModelDocumentLease.Open(sourceApp,pair.Source.Path,""))
        {
            var raw=new SwInterfacePropertyTarget(lease.Document).Read().Raw;
            if(raw.Count!=pair.Source.Raw.Count||pair.Source.Raw.Any(p=>!raw.ContainsKey(p.Key)||raw[p.Key]!=p.Value))
                throw new Exception("Source properties changed since mapping: "+pair.Source.Path);
        }
        SldWorks app=null;int pid=0;
        try
        {
            app=StartIsolated(plan.SourceProcessId);pid=app.GetProcessID();
            if(pid==plan.SourceProcessId)throw new Exception("Source session is protected");
            app.Visible=false;app.UserControl=false;
            var result=new PropertySyncResult();
            var files=Directory.GetFiles(plan.Directory).Where(p=>!Path.GetFileName(p).StartsWith("~$")&&new[]{".SLDASM",".SLDPRT"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).OrderBy(p=>p).ToList();
            var referenceReport=reportPath+".original-references.json";
            result.OriginalReferences=File.Exists(referenceReport)?JsonFile.Read<Dictionary<string,List<string>>>(referenceReport):files.ToDictionary(Path.GetFileName,p=>Dependencies(app,p));
            if(!File.Exists(referenceReport))JsonFile.Write(referenceReport,result.OriginalReferences);
            var attempt=reportPath+"."+DateTime.Now.ToString("yyyyMMdd-HHmmss");
            LocalizeWith(app,plan.Directory,attempt+".temporary-references.json");
            var rootPath=Path.Combine(plan.Directory,plan.RootName);var root=OpenRoot(app,rootPath);
            result.ComponentsBefore=Occurrences(root);result.GeometryBefore=Geometry(app,plan.Directory);result.GeometryMetricsBefore=GeometryMetrics(app,plan.Directory);
            result.BindingStorageBefore=BindingStorageHash(root);
            JsonFile.Write(attempt+".before.json",result);
            var expected=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in plan.Pairs.OrderBy(p=>Path.GetExtension(p.DestinationName).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)?0:p.DestinationName==plan.RootName?2:1))
            using(var lease=SwModelDocumentLease.Open(app,Path.Combine(plan.Directory,pair.DestinationName),""))
            {
                var doc=lease.Document;var current=Read(doc);
                if(current.Created!=pair.Source.Created||!Same(doc.GetPathName(),Path.Combine(plan.Directory,pair.DestinationName)))throw new Exception("Wrong destination identity: "+pair.DestinationName);
                var target=new SwInterfacePropertyTarget(doc);var desired=new Dictionary<string,string>(current.Raw,StringComparer.OrdinalIgnoreCase);
                foreach(var p in pair.Source.Raw)
                {
                    var value=p.Value;
                    // Preserve a self-reference after a confirmed file rename; descriptive text is copied verbatim.
                    var self="@"+Path.GetFileName(pair.Source.Path)+"\"";
                    if(value.StartsWith("\"SW-",StringComparison.OrdinalIgnoreCase)&&value.EndsWith(self,StringComparison.OrdinalIgnoreCase))
                        value=value.Substring(0,value.Length-self.Length)+"@"+pair.DestinationName+"\"";
                    desired[p.Key]=value;
                    if(!current.Raw.ContainsKey(p.Key)||current.Raw[p.Key]!=value||current.Types[p.Key]!=pair.Source.Types[p.Key])
                        target.Set(p.Key,value,pair.Source.Types[p.Key]);
                }
                expected.Add(pair.DestinationName,desired);
                if(doc.GetSaveFlag())Save(doc,plan.Directory);
                if(pair.Latest.Raw.Count!=desired.Count||desired.Any(p=>!pair.Latest.Raw.ContainsKey(p.Key)||pair.Latest.Raw[p.Key]!=p.Value)||pair.Source.Types.Any(p=>!pair.Latest.Types.ContainsKey(p.Key)||pair.Latest.Types[p.Key]!=p.Value))
                    result.ChangedFiles.Add(pair.DestinationName);
                var after=target.Read();
                if(after.Raw.Count!=desired.Count||desired.Any(p=>!after.Raw.ContainsKey(p.Key)||after.Raw[p.Key]!=p.Value))throw new Exception("Property readback mismatch: "+pair.DestinationName);
                Console.WriteLine("SYNC_CHECKED="+pair.DestinationName);
            }
            result.ComponentsAfter=Occurrences(root);SameLayout(result.ComponentsBefore,result.ComponentsAfter);
            result.GeometryAfter=Geometry(app,plan.Directory);
            if(result.GeometryBefore.Count!=result.GeometryAfter.Count||result.GeometryBefore.Any(p=>result.GeometryAfter[p.Key]!=p.Value))throw new Exception("Geometry changed during property synchronization");
            result.BindingStorageAfter=BindingStorageHash(root);
            if(result.BindingStorageAfter!=result.BindingStorageBefore)throw new Exception("Latest binding storage changed during property synchronization");
            app.CloseAllDocuments(true);root=OpenRoot(app,rootPath);
            SameLayout(result.ComponentsBefore,Occurrences(root));
            // SW may regenerate or reorder graphics tessellation during save. Compare native
            // body topology, extents and density-independent mass properties after reopening.
            result.GeometryMetricsAfterReopen=GeometryMetrics(app,plan.Directory);
            SameGeometry(result.GeometryMetricsBefore,result.GeometryMetricsAfterReopen);
            if(BindingStorageHash(root)!=result.BindingStorageBefore)throw new Exception("Saved binding storage mismatch");
            foreach(var path in files)
            using(var lease=SwModelDocumentLease.Open(app,path,""))
            {
                var name=Path.GetFileName(path);var model=Read(lease.Document);
                if(expected.ContainsKey(name)&&(model.Raw.Count!=expected[name].Count||expected[name].Any(p=>!model.Raw.ContainsKey(p.Key)||model.Raw[p.Key]!=p.Value)))throw new Exception("Saved properties mismatch: "+name);
                result.Models.Add(model);
            }
            app.CloseAllDocuments(true);
            // Restore the exact original stored paths, so this operation changes only custom properties.
            foreach(var file in files)
            {
                var original=result.OriginalReferences[Path.GetFileName(file)];
                foreach(var dependency in original.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var local=Path.Combine(plan.Directory,Path.GetFileName(dependency));
                    if(!Same(local,dependency)&&!app.ReplaceReferencedDocument(file,local,dependency))throw new Exception("Cannot restore original reference: "+file);
                }
                if(!Dependencies(app,file).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).SequenceEqual(original.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw new Exception("Original reference paths changed: "+file);
                result.Models.Single(m=>Same(m.Path,file)).Sha256=Hash(file);
            }
            JsonFile.Write(reportPath,result);
            File.WriteAllText(reportPath+".SUCCESS.txt","MATCHED="+plan.Pairs.Count+" CHANGED="+result.ChangedFiles.Count+" COMPONENTS_UNCHANGED="+result.ComponentsBefore.Count+" PART_GEOMETRIES_UNCHANGED="+result.GeometryBefore.Count+" BINDING_STORAGE_UNCHANGED=true REFERENCE_PATHS_UNCHANGED=true",Encoding.UTF8);
            Console.WriteLine(File.ReadAllText(reportPath+".SUCCESS.txt"));
        }
        finally{if(app!=null&&pid!=0&&pid!=plan.SourceProcessId){app.ExitApp();Marshal.FinalReleaseComObject(app);}}
    }
}
