using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class VirtualAssetExternalizer
{
    private sealed class Candidate
    {
        public SwCadNode Node;
        public Component2 Component;
        public string SourcePath;
        public string TargetPath;
        public string OwnerPath;
        public ModelDoc2 OwnerDocument;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var assemblyPath = ReadArgument(args, "--assembly");
        var apply = args.Any(value => string.Equals(value, "--apply", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            Console.Error.WriteLine("必须指定 --assembly <SLDASM 路径>。");
            return 2;
        }

        assemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(assemblyPath) || !string.Equals(Path.GetExtension(assemblyPath), ".SLDASM",
            StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("装配体不存在或扩展名不是 SLDASM：" + assemblyPath);
            return 2;
        }

        SldWorks application = null;
        ModelDoc2 assembly = null;
        var createdApplication = false;
        var openedHere = false;
        try
        {
            try { application = Marshal.GetActiveObject("SldWorks.Application") as SldWorks; }
            catch (COMException) { }
            if (application == null)
            {
                application = Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application")) as SldWorks;
                if (application == null) throw new InvalidOperationException("无法启动 SOLIDWORKS。");
                createdApplication = true;
                application.Visible = false;
            }

            assembly = application.GetOpenDocumentByName(assemblyPath) as ModelDoc2;
            if (assembly == null)
            {
                int openErrors = 0, openWarnings = 0;
                assembly = application.OpenDoc6(assemblyPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty,
                    ref openErrors, ref openWarnings) as ModelDoc2;
                openedHere = assembly != null;
                if (assembly == null || openErrors != 0)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "SOLIDWORKS 无法打开装配体（errors={0}, warnings={1}）：{2}",
                        openErrors, openWarnings, assemblyPath));
            }

            if (assembly.IsOpenedReadOnly())
                throw new InvalidOperationException("总装配体以只读方式打开，不能外部化虚拟 Asset：" + assemblyPath);

            int activationErrors = 0;
            var active = application.ActivateDoc3(assembly.GetTitle(), false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors) as ModelDoc2;
            if (active == null) throw new InvalidOperationException("无法激活总装配体：" + assemblyPath);

            var scan = new AssemblyScanner().Scan(SwAssemblyRoot.FromActiveDocument(application));
            var candidates = AssetNodes(scan)
                .Select(value => value.Source as SwCadNode)
                .Where(value => value != null && IsVirtual(value))
                .GroupBy(value => value.SourcePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(values => CreateCandidate(values.First(), assemblyPath))
                .OrderBy(value => value.TargetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine("总装配体：" + assemblyPath);
            Console.WriteLine("检测到虚拟 Asset 根：" + candidates.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var candidate in candidates)
            {
                Console.WriteLine("- " + candidate.Node.Name);
                Console.WriteLine("  会话源：" + candidate.SourcePath);
                Console.WriteLine("  外部目标：" + candidate.TargetPath);
                Console.WriteLine("  所属装配体：" + candidate.OwnerPath);
            }

            if (candidates.Count == 0) return 0;
            ValidateCandidates(candidates);
            if (!apply)
            {
                Console.WriteLine("检查完成：没有目标重名或只读冲突；尚未修改文件。使用 --apply 执行保存。");
                return 0;
            }

            var backupDirectory = CreateBackups(assemblyPath, candidates);
            Console.WriteLine("备份目录：" + backupDirectory);
            var createdTargets = new List<string>();
            try
            {
                foreach (var candidate in candidates)
                {
                    if (!candidate.Component.SaveVirtualComponent(candidate.TargetPath))
                        throw new InvalidOperationException("SaveVirtualComponent 返回 false：" + candidate.Node.Name);
                    if (!File.Exists(candidate.TargetPath))
                        throw new InvalidOperationException("SOLIDWORKS 返回成功但目标文件不存在：" + candidate.TargetPath);
                    createdTargets.Add(candidate.TargetPath);
                    Console.WriteLine("已外部化：" + candidate.TargetPath);
                }

                SaveAffectedDocuments(assembly, assemblyPath, candidates);
                Console.WriteLine("已保存受影响装配体。重新打开后请执行一次分类预览进行最终业务校验。");
                return 0;
            }
            catch
            {
                foreach (var target in createdTargets)
                {
                    try { if (File.Exists(target)) File.Delete(target); }
                    catch { }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application != null && assembly != null && openedHere)
            {
                try { application.CloseDoc(assembly.GetTitle()); }
                catch { }
            }
            if (application != null && createdApplication)
            {
                try { application.ExitApp(); }
                catch { }
            }
        }
    }

    private static string ReadArgument(IList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static IEnumerable<ScanNode> AssetNodes(ScanNode node)
    {
        if (node == null) yield break;
        if (node.Classification == ScanClassification.AssetBoundary)
        {
            yield return node;
            yield break;
        }
        foreach (var child in node.Children)
            foreach (var asset in AssetNodes(child)) yield return asset;
    }

    private static bool IsVirtual(SwCadNode node)
    {
        try { if (node.Component != null && node.Component.IsVirtual) return true; }
        catch { }
        return AssetSourcePathPolicy.IsSessionEmbeddedModelPath(node.SourcePath);
    }

    private static Candidate CreateCandidate(SwCadNode node, string rootAssemblyPath)
    {
        var sourcePath = node.SourcePath;
        var extension = node.SourceDocumentKind == DocumentKind.Part ? ".SLDPRT" : ".SLDASM";
        var baseName = Path.GetFileNameWithoutExtension(sourcePath) ?? string.Empty;
        var caret = baseName.IndexOf('^');
        if (caret > 0) baseName = baseName.Substring(0, caret);
        baseName = baseName.Trim();
        if (baseName.Length == 0) throw new InvalidOperationException("无法取得虚拟组件外部文件名：" + node.Name);

        var ownerComponent = node.Component == null ? null : node.Component.GetParent();
        ModelDoc2 ownerDocument = null;
        string ownerPath = null;
        while (ownerComponent != null)
        {
            var path = ownerComponent.GetPathName();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                !AssetSourcePathPolicy.IsSessionEmbeddedModelPath(path))
            {
                ownerPath = Path.GetFullPath(path);
                ownerDocument = ownerComponent.GetModelDoc2() as ModelDoc2;
                break;
            }
            ownerComponent = ownerComponent.GetParent();
        }
        if (string.IsNullOrWhiteSpace(ownerPath)) ownerPath = rootAssemblyPath;
        var directory = Path.GetDirectoryName(ownerPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("无法确定外部保存目录：" + node.Name);
        return new Candidate
        {
            Node = node,
            Component = node.Component,
            SourcePath = sourcePath,
            TargetPath = Path.Combine(directory, baseName + extension),
            OwnerPath = ownerPath,
            OwnerDocument = ownerDocument
        };
    }

    private static void ValidateCandidates(IList<Candidate> candidates)
    {
        var duplicate = candidates.GroupBy(value => value.TargetPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(values => values.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException("多个虚拟 Asset 将写入同一目标，请先人工重命名：" + duplicate.Key);
        foreach (var candidate in candidates)
        {
            if (candidate.Component == null)
                throw new InvalidOperationException("无法取得虚拟组件对象：" + candidate.Node.Name);
            if (File.Exists(candidate.TargetPath))
                throw new InvalidOperationException("外部目标已存在，不会覆盖：" + candidate.TargetPath);
            if (candidate.OwnerDocument != null && candidate.OwnerDocument.IsOpenedReadOnly())
                throw new InvalidOperationException("所属装配体只读，不能修改引用：" + candidate.OwnerPath);
        }
    }

    private static string CreateBackups(string rootAssemblyPath, IEnumerable<Candidate> candidates)
    {
        var rootDirectory = Path.GetDirectoryName(rootAssemblyPath);
        var backupDirectory = Path.Combine(rootDirectory,
            "SolidWorksAssetExporter-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDirectory);
        var paths = candidates.Select(value => value.OwnerPath).Concat(new[] { rootAssemblyPath })
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in paths)
        {
            var hash = ShortHash(Path.GetFullPath(path));
            File.Copy(path, Path.Combine(backupDirectory, hash + "-" + Path.GetFileName(path)), false);
        }
        return backupDirectory;
    }

    private static string ShortHash(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static void SaveAffectedDocuments(ModelDoc2 rootDocument, string rootAssemblyPath,
        IEnumerable<Candidate> candidates)
    {
        var documents = new Dictionary<string, ModelDoc2>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
            if (candidate.OwnerDocument != null && !string.IsNullOrWhiteSpace(candidate.OwnerPath))
                documents[candidate.OwnerPath] = candidate.OwnerDocument;
        documents[rootAssemblyPath] = rootDocument;
        foreach (var pair in documents.OrderBy(value =>
            string.Equals(value.Key, rootAssemblyPath, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            int errors = 0, warnings = 0;
            var saved = pair.Value.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                ref errors, ref warnings);
            if (!saved || errors != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "保存装配体失败（errors={0}, warnings={1}）：{2}", errors, warnings, pair.Key));
            Console.WriteLine("已保存装配体：" + pair.Key);
        }
    }
}
