using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

// Targeted repair of the missing interface fields reported for the AQT-020 tool-side part.
// Does not activate or save any assembly, or change any instance binding.
class RepairAqt020Interface
{
    const string Folder = @"C:\Users\C12\Desktop\1103";
    const string ParentName = "MB0200308  AQT-020工具侧.SLDPRT";
    const string ChildName = "移液器装配.SLDASM";
    const string Connection = "快换盘.AQT020";
    const string ToolConnection = "法兰.4xM4.DCP";
    static string Hash(string path)
    {
        using(var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using(var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }
    static void Backup(string source, string destination)
    {
        using(var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using(var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
        if(Hash(source) != Hash(destination)) throw new Exception("Backup verification failed: " + source);
    }
    static ExportNode Node(string name, InterfacePropertySnapshot snapshot)
    { return new ExportNode { Name = name, Properties = new Dictionary<string, string>(snapshot.Values, StringComparer.OrdinalIgnoreCase) }; }
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if(args.Length != 1 || args[0] != "--repair") throw new Exception("Expected --repair.");
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var parent = app.GetOpenDocumentByName(Path.Combine(Folder, ParentName)) as ModelDoc2;
            var child = app.GetOpenDocumentByName(Path.Combine(Folder, ChildName)) as ModelDoc2;
            if(parent == null || child == null) throw new Exception("The two intended models must already be loaded.");
            var target = new SwInterfacePropertyTarget(parent); var childTarget = new SwInterfacePropertyTarget(child);
            if(target.Dirty || target.ReadOnly || childTarget.Dirty) throw new Exception("A source has unsaved changes or is read-only; no changes made.");
            var before = target.Read(); var childBefore = childTarget.Read();
            if(!PropertyRules.ReadIsAsset(before.Values) || PropertyRules.DescribeAssetConnectionRole(before.Values) != "快换盘-工具端" ||
                !PropertyRules.ReadIsAsset(childBefore.Values) || PropertyRules.DescribeAssetConnectionRole(childBefore.Values) != "Tool" ||
                childBefore.Get(PropertyRules.ConnectionInterface) != ToolConnection) throw new Exception("Source classification/interface changed; re-inspection required.");
            if(before.Get(PropertyRules.ConnectionInterface).Length > 0 && before.Get(PropertyRules.ConnectionInterface) != Connection)
                throw new Exception("A connection interface has already been supplied; it will not be replaced.");
            var accepts = InterfaceEditRules.Suggest(before.Get(PropertyRules.AcceptsInterfaces), new[]{ToolConnection});
            var changes = InterfaceEditRules.Changes(before, Connection, accepts, checked(before.Version + 1), false);
            var proposed = Node(ParentName, before);
            foreach(var change in changes) proposed.Properties[change.Key] = change.Value;
            InstanceBindingRules.ValidateConnection(proposed, Node(ChildName, childBefore));
            if(changes.Count == 0) { Console.WriteLine("ALREADY_REPAIRED; CONNECTION_VALID"); return 0; }
            var protectedPaths = new[]{Path.Combine(Folder, "（部分）layout_all 20261103.SLDASM"), Path.Combine(Folder, "BSC工位.SLDASM"), child.GetPathName()};
            var protectedHashes = protectedPaths.ToDictionary(p=>p, Hash, StringComparer.OrdinalIgnoreCase);
            var backupDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "aqt020-interface-fix-20261104", DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8)));
            Directory.CreateDirectory(backupDir); Backup(parent.GetPathName(), Path.Combine(backupDir, ParentName));
            var catalog = new InterfaceCatalogStore();
            if(File.Exists(catalog.FilePath)) Backup(catalog.FilePath, Path.Combine(backupDir, "interfaces-before.json"));
            var log = Path.Combine(backupDir, "repair.txt");
            File.WriteAllLines(log, new[]{"Source=" + parent.GetPathName(), "BeforeHash=" + Hash(parent.GetPathName()), "BeforeVersion=" + before.Version, "Connection=" + Connection, "Accepts=" + string.Join(";", accepts)}, Encoding.UTF8);
            InterfaceEditRules.RequireUnchanged(childBefore, childTarget.Read());
            InterfacePropertyWriter.Apply(target, before, changes);
            var after = target.Read();
            if(target.Dirty || after.Version != before.Version + 1) throw new Exception("Saved property verification failed.");
            InstanceBindingRules.ValidateConnection(Node(ParentName, after), Node(ChildName, childTarget.Read()));
            catalog.Merge(new[]{Connection, ToolConnection});
            foreach(var entry in protectedHashes)
                if(Hash(entry.Key) != entry.Value) throw new Exception("Another model changed during the repair; inspect concurrent user edits: " + entry.Key);
            File.AppendAllLines(log, new[]{"AfterHash=" + Hash(parent.GetPathName()), "AfterVersion=" + after.Version, "CONNECTION_VALID", "PROTECTED_ASSEMBLIES_AND_CHILD_UNCHANGED", "SUCCESS"}, Encoding.UTF8);
            Console.WriteLine("SAVED=" + parent.GetPathName());
            Console.WriteLine("connection_interface=" + after.Get(PropertyRules.ConnectionInterface));
            Console.WriteLine("accepts_interfaces=" + after.Get(PropertyRules.AcceptsInterfaces));
            Console.WriteLine("asset_version=" + after.Version);
            Console.WriteLine("BACKUP_AND_REPORT=" + backupDir);
            Console.WriteLine("CONNECTION_VALID; PROTECTED_ASSEMBLIES_AND_CHILD_UNCHANGED");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
