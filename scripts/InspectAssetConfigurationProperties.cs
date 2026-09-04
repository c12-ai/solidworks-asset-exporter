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

internal static class InspectAssetConfigurationProperties
{
    private sealed class AssetResult
    {
        public string InstancePath;
        public string SourcePath;
        public string Configuration;
        public List<KeyValuePair<string, string>> Properties;
    }

    private sealed class DuplicateResult
    {
        public string InstancePath;
        public string SourcePath;
        public string Configuration;
        public string PropertyName;
        public string FileValue;
        public string ConfigurationValue;
        public bool ValuesEqual;
    }

    private static int _assetInstanceCount;
    private static int _uniqueAssetCount;
    private static int _nonblankPropertyCount;
    private static readonly HashSet<string> SeenAssets =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AssetResult> Results = new List<AssetResult>();
    private static readonly List<DuplicateResult> Duplicates = new List<DuplicateResult>();
    private static readonly List<string> ReadErrors = new List<string>();

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

            Console.WriteLine("活动总装：" + document.GetPathName());
            Walk(SwAssemblyRoot.FromActiveDocument(application));
            Console.WriteLine("ASSET_INSTANCE_COUNT=" +
                _assetInstanceCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("UNIQUE_ASSET_COUNT=" +
                _uniqueAssetCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("ASSET_WITH_NONBLANK_CONFIGURATION_PROPERTIES=" +
                Results.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("NONBLANK_CONFIGURATION_PROPERTY_COUNT=" +
                _nonblankPropertyCount.ToString(CultureInfo.InvariantCulture));
            foreach (var result in Results)
            {
                Console.WriteLine("CONFIGURATION_PROPERTY_ASSET");
                Console.WriteLine("  实例：" + result.InstancePath);
                Console.WriteLine("  文件：" + result.SourcePath);
                Console.WriteLine("  配置：" + result.Configuration);
                foreach (var property in result.Properties)
                    Console.WriteLine("  属性：" + property.Key + " = " + property.Value);
            }
            Console.WriteLine("DUPLICATE_SCOPE_PROPERTY_COUNT=" +
                Duplicates.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("DIFFERENT_DUPLICATE_SCOPE_PROPERTY_COUNT=" +
                Duplicates.Count(result => !result.ValuesEqual).ToString(CultureInfo.InvariantCulture));
            foreach (var result in Duplicates)
            {
                Console.WriteLine("DUPLICATE_SCOPE_PROPERTY");
                Console.WriteLine("  实例：" + result.InstancePath);
                Console.WriteLine("  文件：" + result.SourcePath);
                Console.WriteLine("  配置：" + result.Configuration);
                Console.WriteLine("  属性：" + result.PropertyName);
                Console.WriteLine("  文件级值：" + DisplayValue(result.FileValue));
                Console.WriteLine("  配置级值：" + DisplayValue(result.ConfigurationValue));
                Console.WriteLine("  比较：" + (result.ValuesEqual ? "相同" : "不同"));
            }
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
                " | 配置属性读取失败：" + ex.Message);
            return;
        }

        var key = Canonical.Join(node.SourcePath, model.Configuration);
        if (!SeenAssets.Add(key)) return;
        _uniqueAssetCount++;

        var fileProperties = model.FileProperties ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configurationProperties = model.ConfigurationProperties ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in configurationProperties)
        {
            string fileValue;
            if (!fileProperties.TryGetValue(property.Key, out fileValue)) continue;
            var configurationValue = property.Value ?? string.Empty;
            Duplicates.Add(new DuplicateResult
            {
                InstancePath = node.InstancePath ?? node.Name,
                SourcePath = node.SourcePath ?? string.Empty,
                Configuration = model.Configuration ?? string.Empty,
                PropertyName = property.Key,
                FileValue = fileValue ?? string.Empty,
                ConfigurationValue = configurationValue,
                ValuesEqual = string.Equals((fileValue ?? string.Empty).Trim(),
                    configurationValue.Trim(), StringComparison.Ordinal)
            });
        }

        var properties = configurationProperties
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.Trim()))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (properties.Count == 0) return;

        _nonblankPropertyCount += properties.Count;
        Results.Add(new AssetResult
        {
            InstancePath = node.InstancePath ?? node.Name,
            SourcePath = node.SourcePath ?? string.Empty,
            Configuration = model.Configuration ?? string.Empty,
            Properties = properties
        });
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<空白>" : value.Trim();
    }

    private static void WalkChildren(SwCadNode node)
    {
        foreach (var childValue in node.GetChildren())
        {
            var child = childValue as SwCadNode;
            if (child != null && AssemblyScanner.IsIncluded(child)) Walk(child);
        }
    }
}
