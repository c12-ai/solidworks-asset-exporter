using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

// Restore the assembly-package root's classification without saving any model or
// touching the in-process binding owner. The user retains the normal Save action.
class RepairRootAssetBoundary
{
    static string Hash(string path)
    { using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-",""); }
    [STAThread] static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try
        {
            if(args.Length!=1||args[0]!="--repair-memory")throw new Exception("Use --repair-memory.");
            const string path=@"C:\Users\C12\Desktop\1103\（部分）layout_all 20261103.SLDASM";
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var root=app.ActiveDoc as ModelDoc2;
            if(root==null||!string.Equals(root.GetPathName(),path,StringComparison.OrdinalIgnoreCase)||root.GetType()!=2)
                throw new Exception("Expected layout must be the active assembly.");
            var target=new SwInterfacePropertyTarget(root);var before=target.Read();
            if(target.ReadOnly)throw new Exception("Root is read-only.");
            if(!PropertyRules.ReadIsAsset(before.Values)){Console.WriteLine("ROOT_ALREADY_NON_ASSET");return 0;}
            if(before.Get(PropertyRules.IsAsset)!="1")throw new Exception("Root classification changed; re-inspect first.");
            var backupDir=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..","..","..","artifacts","root-asset-classification-fix-20261104",DateTime.Now.ToString("HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)));
            Directory.CreateDirectory(backupDir);
            var copy=Path.Combine(backupDir,Path.GetFileName(path));var diskHash=Hash(path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var output=new FileStream(copy,FileMode.CreateNew,FileAccess.Write))input.CopyTo(output);
            if(Hash(copy)!=diskHash||Hash(path)!=diskHash)throw new Exception("Disk snapshot changed while backing up.");
            JsonFile.Write(Path.Combine(backupDir,"file-properties-before.json"),before.Raw);
            var wasDirty=root.GetSaveFlag();
            InterfaceEditRules.RequireUnchanged(before,target.Read());
            target.Set(PropertyRules.IsAsset,"0",before.Types[PropertyRules.IsAsset]);
            var after=target.Read();
            if(PropertyRules.ReadIsAsset(after.Values)||before.Raw.Count!=after.Raw.Count||before.Raw.Any(p=>p.Key!=PropertyRules.IsAsset&&after.Raw[p.Key]!=p.Value))
                throw new Exception("Unexpected property changes; inspect the live document.");
            if(Hash(path)!=diskHash)throw new Exception("Disk file changed during the operation; inspect concurrent user saving.");
            File.WriteAllLines(Path.Combine(backupDir,"result.txt"),new[]{"SOURCE="+path,"ROOT_WAS_DIRTY="+wasDirty,"CHANGED=is_asset:1->0","ROOT_NOW_DIRTY="+root.GetSaveFlag(),"DISK_UNCHANGED="+diskHash,"NO_BINDING_RECORDS_CHANGED","NOT_SAVED"},Encoding.UTF8);
            Console.WriteLine("RESTORED_ROOT_IS_ASSET=0; OTHER_PROPERTIES_UNCHANGED; NOT_SAVED");
            Console.WriteLine("BACKUP="+backupDir);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
