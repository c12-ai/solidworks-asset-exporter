using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class InspectAssetTextProperty
{
    private sealed class BlankResult
    {
        public string InstancePath;
        public string SourcePath;
        public string Configuration;
        public string Reason;
    }

    private static string _propertyName;
    private static int _assetInstanceCount;
    private static int _uniqueAssetCount;
    private static readonly HashSet<string> SeenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly SortedSet<string> RelatedPropertyNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<BlankResult> Blanks = new List<BlankResult>();
    private static readonly List<string> ReadErrors = new List<string>();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _propertyName = args.Length == 0 ? "设计说明" : (args[0] ?? string.Empty).Trim();
        if (_propertyName.Length == 0)
        {
            Console.Error.WriteLine("属性名不能为空。");
            return 2;
        }

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
            var targetPath = args.Length < 2 ? string.Empty : (args[1] ?? string.Empty).Trim();
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
            Console.WriteLine("活动总装：" + document.GetPathName());
            Console.WriteLine("检查属性：" + _propertyName);
            Walk(SwAssemblyRoot.FromActiveDocument(application));
            Console.WriteLine("ASSET_INSTANCE_COUNT=" + _assetInstanceCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("UNIQUE_ASSET_COUNT=" + _uniqueAssetCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("BLANK_COUNT=" + Blanks.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var result in Blanks)
            {
                Console.WriteLine("BLANK_ASSET");
                Console.WriteLine("  实例：" + result.InstancePath);
                Console.WriteLine("  文件：" + result.SourcePath);
                Console.WriteLine("  配置：" + result.Configuration);
                Console.WriteLine("  原因：" + result.Reason);
            }
            Console.WriteLine("RELATED_PROPERTY_NAMES=" + string.Join(";", RelatedPropertyNames));
            Console.WriteLine("READ_ERROR_COUNT=" + ReadErrors.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var error in ReadErrors) Console.WriteLine("READ_ERROR=" + error);
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
            ReadErrors.Add((node.InstancePath ?? node.Name) + " | " + node.SourcePath + " | 分类属性读取失败：" + ex.Message);
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
            ReadErrors.Add((node.InstancePath ?? node.Name) + " | " + node.SourcePath + " | 完整属性读取失败：" + ex.Message);
            return;
        }

        var key = Canonical.Join(node.SourcePath, model.Configuration);
        if (!SeenAssets.Add(key)) return;
        _uniqueAssetCount++;

        var fileProperties = model.FileProperties ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configurationProperties = model.ConfigurationProperties ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileProperties.Keys.Concat(configurationProperties.Keys).Where(propertyName =>
            propertyName.IndexOf("设计", StringComparison.OrdinalIgnoreCase) >= 0 ||
            propertyName.IndexOf("说明", StringComparison.OrdinalIgnoreCase) >= 0))
            RelatedPropertyNames.Add(name);

        string fileValue, configurationValue;
        var hasFileValue = fileProperties.TryGetValue(_propertyName, out fileValue);
        var hasConfigurationValue = configurationProperties.TryGetValue(_propertyName, out configurationValue);
        if (hasFileValue && hasConfigurationValue)
        {
            Console.WriteLine("DUPLICATE_PROPERTY");
            Console.WriteLine("  实例：" + (node.InstancePath ?? node.Name));
            Console.WriteLine("  文件：" + (node.SourcePath ?? string.Empty));
            Console.WriteLine("  文件级值：" + (fileValue ?? string.Empty));
            Console.WriteLine("  配置级值：" + (configurationValue ?? string.Empty));
        }
        if (!hasFileValue && !hasConfigurationValue)
            AddBlank(node, model.Configuration, "属性缺失");
        else if ((!hasFileValue || string.IsNullOrWhiteSpace(fileValue)) &&
            (!hasConfigurationValue || string.IsNullOrWhiteSpace(configurationValue)))
            AddBlank(node, model.Configuration, "属性值为空白");
    }

    private static void WalkChildren(SwCadNode node)
    {
        foreach (var childValue in node.GetChildren())
        {
            var child = childValue as SwCadNode;
            if (child != null && AssemblyScanner.IsIncluded(child)) Walk(child);
        }
    }

    private static void AddBlank(SwCadNode node, string configuration, string reason)
    {
        Blanks.Add(new BlankResult
        {
            InstancePath = node.InstancePath ?? node.Name,
            SourcePath = node.SourcePath ?? string.Empty,
            Configuration = configuration ?? string.Empty,
            Reason = reason
        });
    }
}
