using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class InspectAssetClasses
{
    private static readonly HashSet<string> SeenAssets =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ReadErrors = new List<string>();
    private static int _assetInstanceCount;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        SldWorks application;
        try { application = Marshal.GetActiveObject("SldWorks.Application") as SldWorks; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法连接当前 SOLIDWORKS 会话：" + ex.Message);
            return 2;
        }
        if (application == null)
        {
            Console.Error.WriteLine("当前没有可用的 SOLIDWORKS 会话。");
            return 2;
        }

        var originalDocument = application.ActiveDoc as ModelDoc2;
        try
        {
            var document = originalDocument;
            var targetPath = args.Length == 0 ? string.Empty : (args[0] ?? string.Empty).Trim();
            if (targetPath.Length > 0)
            {
                document = application.GetOpenDocumentByName(targetPath) as ModelDoc2;
                if (document == null)
                {
                    Console.Error.WriteLine("指定总装配体当前没有打开：" + targetPath);
                    return 2;
                }
                int activationErrors = 0;
                document = application.ActivateDoc3(document.GetTitle(), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                    ref activationErrors) as ModelDoc2;
                if (document == null)
                {
                    Console.Error.WriteLine("无法激活指定总装配体：" + targetPath +
                        "; errors=" + activationErrors.ToString(CultureInfo.InvariantCulture));
                    return 2;
                }
            }
            if (document == null)
            {
                Console.Error.WriteLine("SOLIDWORKS 当前没有活动文档。");
                return 2;
            }

            Console.WriteLine("ACTIVE_DOCUMENT=" + Clean(document.GetPathName()));
            Console.WriteLine("COLUMNS=零件名\tclass\tasset_version\tis_tool\tis_fixture\tis_quick_changer\tquick_changer_side\tis_quick_changer_rack\tconnection_interface\taccepts_interfaces\tis_adjustable\tslots_num\t设计目的\t文件");
            Walk(SwAssemblyRoot.FromActiveDocument(application));
            Console.WriteLine("ASSET_INSTANCE_COUNT=" + _assetInstanceCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("UNIQUE_ASSET_COUNT=" + SeenAssets.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("READ_ERROR_COUNT=" + ReadErrors.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var error in ReadErrors) Console.WriteLine("READ_ERROR=" + Clean(error));
            return ReadErrors.Count == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("检查失败：" + ex);
            return 2;
        }
        finally
        {
            try
            {
                if (originalDocument != null && application.ActiveDoc != originalDocument)
                {
                    int restoreErrors = 0;
                    application.ActivateDoc3(originalDocument.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                        ref restoreErrors);
                }
            }
            catch { }
        }
    }

    private static void Walk(SwCadNode node)
    {
        IDictionary<string, string> classification;
        try { classification = PropertyRules.MergeForClassification(node.ClassificationModel); }
        catch (Exception ex)
        {
            ReadErrors.Add((node.InstancePath ?? node.Name) + " | " + node.SourcePath +
                " | 分类属性读取失败：" + ex.Message);
            WalkChildren(node);
            return;
        }

        if (!PropertyRules.ReadIsAsset(classification))
        {
            WalkChildren(node);
            return;
        }

        _assetInstanceCount++;
        ModelDescriptor model;
        try { model = node.Model; }
        catch (Exception ex)
        {
            ReadErrors.Add((node.InstancePath ?? node.Name) + " | " + node.SourcePath +
                " | 完整属性读取失败：" + ex.Message);
            return;
        }

        var key = Canonical.Join(node.SourcePath, model.Configuration);
        if (!SeenAssets.Add(key)) return;
        var properties = model.FileProperties ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("ASSET\t" + string.Join("\t", new[]
        {
            Value(properties, "零件名"),
            Value(properties, "class"),
            Value(properties, "asset_version"),
            Value(properties, "is_tool"),
            Value(properties, "is_fixture"),
            Value(properties, "is_quick_changer"),
            Value(properties, "quick_changer_side"),
            Value(properties, "is_quick_changer_rack"),
            Value(properties, "connection_interface"),
            Value(properties, "accepts_interfaces"),
            Value(properties, "is_adjustable"),
            Value(properties, "slots_num"),
            Value(properties, "设计目的"),
            node.SourcePath ?? string.Empty
        }).Replace("\r", " ").Replace("\n", " "));
    }

    private static void WalkChildren(SwCadNode node)
    {
        foreach (var childValue in node.GetChildren())
        {
            var child = childValue as SwCadNode;
            if (child != null && AssemblyScanner.IsIncluded(child)) Walk(child);
        }
    }

    private static string Value(IDictionary<string, string> properties, string name)
    {
        string value;
        return properties.TryGetValue(name, out value) ? Clean(value) : string.Empty;
    }

    private static string Clean(string value)
    {
        return (value ?? string.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
