using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SolidWorks.Interop.sldworks;

[DataContract] public sealed class AssemblyReferencesReport
{
    [DataMember] public string ActiveDocument;
    [DataMember] public List<string> DirtyDocuments;
    [DataMember] public List<AssemblyReferenceList> Assemblies = new List<AssemblyReferenceList>();
}
[DataContract] public sealed class AssemblyReferenceList
{
    [DataMember] public string Assembly;
    [DataMember] public List<string> Dependencies = new List<string>();
    [DataMember] public string Error;
}
internal static class InspectAssemblyReferences
{
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length != 2) throw new Exception("Usage: model-directory new-report.json");
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var active = app.ActiveDoc as ModelDoc2;
            var report = new AssemblyReferencesReport {
                ActiveDocument = active == null ? null : active.GetPathName(),
                DirtyDocuments = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>()
                    .Where(d => d.GetSaveFlag()).Select(d => d.GetPathName()).ToList()
            };
            foreach (var path in Directory.GetFiles(Path.GetFullPath(args[0]), "*.SLDASM").OrderBy(p => p))
            {
                var entry = new AssemblyReferenceList { Assembly = path };
                try
                {
                    var raw = app.GetDocumentDependencies2(path, false, false, false) as string[];
                    if (raw == null || raw.Length % 2 != 0) throw new Exception("Invalid dependency response");
                    for (int i = 1; i < raw.Length; i += 2) entry.Dependencies.Add(raw[i]);
                }
                catch (Exception ex) { entry.Error = ex.Message; }
                report.Assemblies.Add(entry);
            }
            using (var stream = new FileStream(args[1], FileMode.CreateNew))
                new DataContractJsonSerializer(typeof(AssemblyReferencesReport)).WriteObject(stream, report);
            Console.WriteLine("ASSEMBLIES=" + report.Assemblies.Count + " ERRORS=" + report.Assemblies.Count(a => a.Error != null));
            Console.WriteLine("ACTIVE=" + report.ActiveDocument);
            return report.Assemblies.Any(a => a.Error != null) ? 2 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
