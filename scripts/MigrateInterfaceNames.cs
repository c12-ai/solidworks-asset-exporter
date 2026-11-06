using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

[DataContract]
public sealed class InterfaceInventory
{
    [DataMember] public string Root;
    [DataMember] public string Configuration;
    [DataMember] public string CapturedUtc;
    [DataMember] public List<InterfaceModelInventory> Models = new List<InterfaceModelInventory>();
    [DataMember] public List<string> Errors = new List<string>();
    [DataMember] public List<string> DirtyBefore = new List<string>();
}
[DataContract]
public sealed class InterfaceModelInventory
{
    [DataMember] public string Path;
    [DataMember] public Dictionary<string, string> Raw;
    [DataMember] public Dictionary<string, string> Values;
    [DataMember] public Dictionary<string, int> Types;
    [DataMember] public List<string> Linked;
    [DataMember] public Dictionary<string, Dictionary<string, string>> ConfigurationInterfaces = new Dictionary<string, Dictionary<string, string>>();
    [DataMember] public bool Dirty;
    [DataMember] public bool ReadOnly;
    [DataMember] public string Sha256;
}
[DataContract]
public sealed class InterfaceMigrationPlan
{
    [DataMember] public InterfaceInventory Inventory;
    [DataMember] public Dictionary<string, string> Mapping;
    [DataMember] public List<InterfaceMigrationFile> Files = new List<InterfaceMigrationFile>();
}
[DataContract]
public sealed class InterfaceMigrationFile
{
    [DataMember] public InterfaceModelInventory Before;
    [DataMember] public Dictionary<string, string> Changes;
}
internal static class MigrateInterfaceNames
{
    static readonly string[] Fields = { "connection_interface", "accepts_interfaces" };
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 4 && args[0] == "--plan") { MakePlan(args[1], args[2], args[3]); return 0; }
            if (args.Length == 4 && args[0] == "--save-reviewed-dirty") { SaveReviewedDirty(args[1], args[2], args[3]); return 0; }
            if (args.Length == 3 && args[0] == "--apply") { Apply(args[1], args[2]); return 0; }
            if (args.Length != 3 || args[0] != "--inspect") throw new Exception("Usage: --inspect root.sldasm inventory.json | --plan inventory.json mapping.json plan.json | --apply plan.json new-backup-directory");
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var root = Path.GetFullPath(args[1]);
            var doc = app.ActiveDoc as ModelDoc2;
            if (doc == null || !string.Equals(doc.GetPathName(), root, StringComparison.OrdinalIgnoreCase)) throw new Exception("The requested assembly must be active.");
            var report = new InterfaceInventory { Root = root, Configuration = doc.ConfigurationManager.ActiveConfiguration.Name, CapturedUtc = DateTime.UtcNow.ToString("o") };
            report.DirtyBefore = (app.GetDocuments() as object[] ?? new object[0]).OfType<ModelDoc2>().Where(d => d.GetSaveFlag()).Select(d => d.GetPathName()).ToList();
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var dependencies = app.GetDocumentDependencies2(root, true, false, false) as string[];
            if (dependencies == null || dependencies.Length % 2 != 0) throw new Exception("Cannot enumerate assembly dependencies.");
            for (int i = 1; i < dependencies.Length; i += 2)
                if (new[] { ".SLDPRT", ".SLDASM" }.Contains(Path.GetExtension(dependencies[i]), StringComparer.OrdinalIgnoreCase)) paths.Add(Path.GetFullPath(dependencies[i]));
            Console.WriteLine("DEPENDENT_MODEL_FILES=" + paths.Count);
            foreach (var path in paths)
            {
                try
                {
                    using (var lease = SwModelDocumentLease.Open(app, path, ""))
                    {
                        var target = new SwInterfacePropertyTarget(lease.Document); var snapshot = target.Read();
                        var entry = new InterfaceModelInventory { Path = path, Raw = snapshot.Raw, Values = snapshot.Values, Types = snapshot.Types, Linked = snapshot.Linked.ToList(), Dirty = target.Dirty, ReadOnly = target.ReadOnly };
                        foreach (var configuration in lease.Document.GetConfigurationNames() as string[] ?? new string[0])
                        {
                            var manager = (CustomPropertyManager)((dynamic)lease.Document.Extension).CustomPropertyManager[configuration];
                            var properties = new Dictionary<string, string>();
                            foreach (var field in Fields)
                            {
                                string raw, resolved; bool wasResolved, linked;
                                manager.Get6(field, true, out raw, out resolved, out wasResolved, out linked);
                                if (!string.IsNullOrWhiteSpace(raw)) properties[field] = raw;
                            }
                            if (properties.Count > 0) entry.ConfigurationInterfaces[configuration] = properties;
                        }
                        if (Fields.Any(f => !string.IsNullOrWhiteSpace(snapshot.Get(f)))) entry.Sha256 = Hash(path);
                        report.Models.Add(entry);
                        if (Fields.Any(f => !string.IsNullOrWhiteSpace(snapshot.Get(f))))
                            Console.WriteLine("INTERFACES=" + Path.GetFileName(path) + " | v=" + snapshot.Get("asset_version") + " | connection=" + snapshot.Get(Fields[0]) + " | accepts=" + snapshot.Get(Fields[1]) + " | dirty=" + target.Dirty);
                    }
                }
                catch (Exception ex) { report.Errors.Add(path + " | " + ex.Message); Console.WriteLine("READ_ERROR=" + path + " | " + ex.Message); }
            }
            JsonFile.Write(args[2], report);
            Console.WriteLine("MODELS=" + report.Models.Count + " ERRORS=" + report.Errors.Count + " DIRTY_BEFORE=" + report.DirtyBefore.Count);
            return report.Errors.Count == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static string Hash(string path)
    { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    static void SaveReviewedDirty(string planPath, string targetPath, string backupDirectory)
    {
        var plan = JsonFile.Read<InterfaceMigrationPlan>(planPath);
        targetPath = Path.GetFullPath(targetPath); backupDirectory = Path.GetFullPath(backupDirectory);
        var file = plan.Files.Single(f => string.Equals(f.Before.Path, targetPath, StringComparison.OrdinalIgnoreCase));
        if (!file.Before.Dirty) throw new Exception("This target was not identified as an existing dirty document.");
        var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        var doc = app.GetOpenDocumentByName(targetPath) as ModelDoc2;
        if (doc == null || !string.Equals(doc.GetPathName(), targetPath, StringComparison.OrdinalIgnoreCase)) throw new Exception("Reviewed source document is not open.");
        var target = new SwInterfacePropertyTarget(doc);
        if (target.ReadOnly) throw new Exception("Reviewed target is read-only.");
        InterfaceEditRules.RequireUnchanged(Snapshot(file.Before), target.Read());
        if (Hash(targetPath) != file.Before.Sha256) throw new Exception("Reviewed source file changed on disk.");
        if (Directory.Exists(backupDirectory)) throw new Exception("Use a new backup directory.");
        Directory.CreateDirectory(backupDirectory);
        var copyPath = Path.Combine(backupDirectory, Path.GetFileName(targetPath));
        using (var source = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var copy = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(copy);
        if (Hash(copyPath) != file.Before.Sha256) throw new Exception("Reviewed source backup mismatch.");
        JsonFile.Write(Path.Combine(backupDirectory, "memory-properties-before-save.json"), file.Before);
        if (target.Dirty) target.Save();
        Console.WriteLine("REVIEWED_TARGET_SAVED=" + targetPath + " DIRTY=" + target.Dirty + " BACKUP=" + backupDirectory);
    }
    static string Value(InterfaceModelInventory item, string key)
    { string value; return item.Values.TryGetValue(key, out value) ? value : ""; }
    static string Rename(string value, bool list, IDictionary<string, string> mapping)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        var names = list ? InterfaceEditRules.Split(value) : new List<string> { InterfaceEditRules.Name(value) };
        return string.Join(";", names.Select(name => { string next; return mapping.TryGetValue(name, out next) ? next : name; }));
    }
    static void MakePlan(string inventoryPath, string mappingPath, string planPath)
    {
        var inventory = JsonFile.Read<InterfaceInventory>(inventoryPath);
        var mapping = JsonFile.Read<Dictionary<string, string>>(mappingPath);
        if (inventory.Errors.Count > 0) throw new Exception("Complete the inventory before migration.");
        if (mapping.Count == 0 || mapping.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count) throw new Exception("Mappings must be one-to-one; merging distinct interfaces is not allowed.");
        foreach (var entry in mapping) { InterfaceEditRules.Name(entry.Key); InterfaceEditRules.Name(entry.Value); if (entry.Value.IndexOf('.') < 0) throw new Exception("New name must use dotted format: " + entry.Value); }
        var allNames = inventory.Models.SelectMany(m => Fields.SelectMany(f => InterfaceEditRules.Split(Value(m, f)))).Distinct(StringComparer.Ordinal).ToList();
        if (allNames.Any(n => !mapping.ContainsKey(n) && !mapping.Values.Contains(n, StringComparer.Ordinal))) throw new Exception("Unmapped interface names remain.");
        var destinations = allNames.Select(n => mapping.ContainsKey(n) ? mapping[n] : n).ToList();
        if (destinations.Distinct(StringComparer.Ordinal).Count() != allNames.Count) throw new Exception("Migration would merge existing interface identities.");
        var plan = new InterfaceMigrationPlan { Inventory = inventory, Mapping = mapping };
        foreach (var model in inventory.Models)
        {
            if (model.ConfigurationInterfaces.Count > 0) throw new Exception("Configuration-specific interface values require separate review: " + model.Path);
            var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in Fields)
            {
                var next = Rename(Value(model, field), field == "accepts_interfaces", mapping);
                if (next != Value(model, field)) changes[field] = next;
            }
            if (changes.Count == 0) continue;
            // Robot versions identify separately managed URDF resources. Non-assets have no asset release.
            if (PropertyRules.ReadIsAsset(model.Values) && !PropertyRules.IsRobotClass(model.Values))
                changes["asset_version"] = checked(PropertyRules.RequirePositiveInteger(model.Values, "asset_version", model.Path) + 1).ToString(CultureInfo.InvariantCulture);
            if (changes.Keys.Any(k => model.Linked.Contains(k, StringComparer.OrdinalIgnoreCase))) throw new Exception("A managed field is linked: " + model.Path);
            plan.Files.Add(new InterfaceMigrationFile { Before = model, Changes = changes });
        }
        if (plan.Files.Count == 0) throw new Exception("Nothing to migrate.");
        var root = inventory.Models.Single(m => string.Equals(m.Path, inventory.Root, StringComparison.OrdinalIgnoreCase));
        if (plan.Files.Any(f => f.Before == root)) throw new Exception("Root interfaces need separate handling.");
        root.Sha256 = Hash(root.Path);
        plan.Files.Add(new InterfaceMigrationFile { Before = root, Changes = new Dictionary<string, string> { { "assembly_version", checked(PropertyRules.RequirePositiveInteger(root.Values, "assembly_version", root.Path) + 1).ToString(CultureInfo.InvariantCulture) } } });
        // The injective map must preserve every pre-existing exact-name compatibility pair.
        int pairs = 0;
        foreach (var parent in inventory.Models)
        foreach (var child in inventory.Models)
        {
            var oldConnection = Value(child, "connection_interface").Trim();
            var oldAccepts = InterfaceEditRules.Split(Value(parent, "accepts_interfaces"));
            if (oldConnection.Length == 0 || !oldAccepts.Contains(oldConnection, StringComparer.Ordinal)) continue;
            var connection = Rename(oldConnection, false, mapping);
            if (!InterfaceEditRules.Split(Rename(Value(parent, "accepts_interfaces"), true, mapping)).Contains(connection, StringComparer.Ordinal)) throw new Exception("Compatibility pair lost.");
            pairs++;
        }
        JsonFile.Write(planPath, plan);
        Console.WriteLine("PLAN_FILES=" + plan.Files.Count + " INTERFACE_NAMES=" + mapping.Count + " PRESERVED_COMPATIBILITY_PAIRS=" + pairs);
        foreach (var file in plan.Files)
            Console.WriteLine("PLAN=" + Path.GetFileName(file.Before.Path) + " | " + string.Join(" | ", file.Changes.Select(p => p.Key + ": " + Value(file.Before, p.Key) + " -> " + p.Value)) + " | dirty=" + file.Before.Dirty);
    }
    static InterfacePropertySnapshot Snapshot(InterfaceModelInventory model)
    {
        var s = new InterfacePropertySnapshot();
        foreach (var p in model.Raw) s.Raw.Add(p.Key, p.Value);
        foreach (var p in model.Values) s.Values.Add(p.Key, p.Value);
        foreach (var p in model.Types) s.Types.Add(p.Key, p.Value);
        foreach (var p in model.Linked) s.Linked.Add(p);
        return s;
    }
    static string BindingSignature(ModelDoc2 doc)
    {
        using (var session = SwInstanceBindings.ForDocument(doc))
        {
            var relations = session.Current.Relations.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            return string.Join("\n", relations.Select(r => r.Id + "|" + r.Parent.Reference + "|" + r.Parent.Context + "|" + r.Child.Reference + "|" + r.Child.Context));
        }
    }
    static void Apply(string planPath, string backupDirectory)
    {
        var plan = JsonFile.Read<InterfaceMigrationPlan>(planPath);
        var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        var root = app.ActiveDoc as ModelDoc2;
        if (root == null || !string.Equals(root.GetPathName(), plan.Inventory.Root, StringComparison.OrdinalIgnoreCase) || root.ConfigurationManager.ActiveConfiguration.Name != plan.Inventory.Configuration) throw new Exception("Restore the inspected assembly and configuration first.");
        var allowedRoot = Path.GetDirectoryName(plan.Inventory.Root) + Path.DirectorySeparatorChar;
        backupDirectory = Path.GetFullPath(backupDirectory);
        if (Directory.Exists(backupDirectory)) throw new Exception("Backup directory already exists; use a new directory.");
        var leases = new List<SwModelDocumentLease>();
        var documents = new Dictionary<string, ModelDoc2>(StringComparer.OrdinalIgnoreCase);
        var blockers = new List<string>();
        try
        {
            foreach (var file in plan.Files)
            {
                var path = Path.GetFullPath(file.Before.Path);
                if (!path.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Target outside the assembly directory: " + path);
                var allowedFields = path.Equals(plan.Inventory.Root, StringComparison.OrdinalIgnoreCase) ? new[] { "assembly_version" } : new[] { "connection_interface", "accepts_interfaces", "asset_version" };
                if (file.Changes.Count == 0 || file.Changes.Keys.Any(k => !allowedFields.Contains(k, StringComparer.Ordinal))) throw new Exception("Unexpected property mutation.");
                var lease = SwModelDocumentLease.Open(app, path, ""); leases.Add(lease); documents.Add(path, lease.Document);
                var target = new SwInterfacePropertyTarget(lease.Document);
                if (target.Dirty || target.ReadOnly) blockers.Add(path + (target.Dirty ? " has unsaved changes" : " is read-only"));
                InterfaceEditRules.RequireUnchanged(Snapshot(file.Before), target.Read());
                if (Hash(path) != file.Before.Sha256) throw new Exception("Source file changed since inspection: " + path);
            }
            if (blockers.Count > 0) throw new Exception("No model was changed. Resolve these files first:\n" + string.Join("\n", blockers));
            var bindingsBefore = BindingSignature(root);
            Directory.CreateDirectory(backupDirectory);
            JsonFile.Write(Path.Combine(backupDirectory, "plan.json"), plan);
            File.WriteAllText(Path.Combine(backupDirectory, "bindings-before.txt"), bindingsBefore, Encoding.UTF8);
            for (int i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i]; var destination = Path.Combine(backupDirectory, i.ToString("D3") + "-" + Path.GetFileName(file.Before.Path));
                using (var source = new FileStream(file.Before.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var copy = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(copy);
                if (Hash(destination) != file.Before.Sha256) throw new Exception("Backup mismatch: " + file.Before.Path);
            }
            Console.WriteLine("BACKUP=" + backupDirectory);
            // Write children before assemblies; save the root version last. Never save unrelated dirty docs.
            foreach (var file in plan.Files.OrderBy(f => f.Before.Path.Equals(plan.Inventory.Root, StringComparison.OrdinalIgnoreCase) ? 2 : Path.GetExtension(f.Before.Path).Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var target = new SwInterfacePropertyTarget(documents[file.Before.Path]);
                InterfaceEditRules.RequireUnchanged(Snapshot(file.Before), target.Read());
                if (target.Dirty) throw new Exception("Target became dirty before its migration: " + file.Before.Path);
                foreach (var change in file.Changes) target.Set(change.Key, change.Value, file.Before.Types.ContainsKey(change.Key) ? file.Before.Types[change.Key] : 30);
                VerifyProperties(file, target.Read());
                target.Save();
                VerifyProperties(file, target.Read());
                File.AppendAllText(Path.Combine(backupDirectory, "journal.txt"), "SAVED\t" + file.Before.Path + "\t" + Hash(file.Before.Path) + System.Environment.NewLine, Encoding.UTF8);
                Console.WriteLine("SAVED=" + Path.GetFileName(file.Before.Path));
            }
            if (BindingSignature(root) != bindingsBefore) throw new Exception("Binding records changed during migration; review the backup.");
            foreach (var file in plan.Files) VerifyProperties(file, new SwInterfacePropertyTarget(documents[file.Before.Path]).Read());
            File.WriteAllText(Path.Combine(backupDirectory, "SUCCESS.txt"), "All planned properties saved and read back. Other raw custom properties and current-configuration binding records preserved.", Encoding.UTF8);
            Console.WriteLine("PASS_ALL_PROPERTIES_AND_BINDINGS_PRESERVED SAVED_FILES=" + plan.Files.Count);
        }
        catch
        {
            // Preserve any open dirty source for recovery; backups/journal describe committed files.
            foreach (var lease in leases) if (lease.Document.GetSaveFlag()) lease.RetainForEditing();
            throw;
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }
    static void VerifyProperties(InterfaceMigrationFile file, InterfacePropertySnapshot after)
    {
        var expected = new Dictionary<string, string>(file.Before.Raw, StringComparer.OrdinalIgnoreCase);
        foreach (var change in file.Changes) expected[change.Key] = change.Value;
        if (expected.Count != after.Raw.Count || expected.Any(p => !after.Raw.ContainsKey(p.Key) || after.Raw[p.Key] != p.Value)) throw new Exception("Unexpected property readback: " + file.Before.Path);
        if (file.Before.Types.Any(p => !after.Types.ContainsKey(p.Key) || after.Types[p.Key] != p.Value)) throw new Exception("Property type changed: " + file.Before.Path);
    }
}
