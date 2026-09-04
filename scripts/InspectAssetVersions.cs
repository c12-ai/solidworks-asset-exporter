using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

internal static class InspectAssetVersions
{
    private sealed class AssetVersionResult
    {
        public string InstancePath;
        public string SourcePath;
        public string RawVersion;
        public string Problem;
    }

    private static int _assetCount;
    private static readonly List<AssetVersionResult> Problems = new List<AssetVersionResult>();
    private static readonly List<string> ReadErrors = new List<string>();

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        SldWorks application;
        try
        {
            application = Marshal.GetActiveObject("SldWorks.Application") as SldWorks;
        }
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

        try
        {
            var document = application.ActiveDoc as ModelDoc2;
            if (document == null)
            {
                Console.Error.WriteLine("SOLIDWORKS 当前没有活动文档。");
                return 2;
            }

            Console.WriteLine("活动总装：" + document.GetPathName());
            Walk(SwAssemblyRoot.FromActiveDocument(application));
            Console.WriteLine("ASSET_COUNT=" + _assetCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("NON_ONE_COUNT=" + Problems.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var result in Problems)
            {
                Console.WriteLine("NON_ONE_ASSET");
                Console.WriteLine("  实例：" + result.InstancePath);
                Console.WriteLine("  文件：" + result.SourcePath);
                Console.WriteLine("  asset_version：" + result.RawVersion);
                Console.WriteLine("  问题：" + result.Problem);
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
    }

    private static void Walk(SwCadNode node)
    {
        IDictionary<string, string> properties;
        try
        {
            properties = PropertyRules.MergeForClassification(node.ClassificationModel);
        }
        catch (Exception ex)
        {
            ReadErrors.Add((node.InstancePath ?? node.Name) + " | " + node.SourcePath + " | " + ex.Message);
            WalkChildren(node);
            return;
        }

        if (PropertyRules.ReadIsAsset(properties))
        {
            _assetCount++;
            string raw;
            int version;
            if (!properties.TryGetValue(PropertyRules.AssetVersion, out raw))
            {
                AddProblem(node, "<缺失>", "缺少 asset_version");
            }
            else if (!int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out version) || version <= 0)
            {
                AddProblem(node, raw, "asset_version 不是正整数");
            }
            else if (version != 1)
            {
                AddProblem(node, raw, "asset_version 不等于 1");
            }
            return;
        }

        WalkChildren(node);
    }

    private static void WalkChildren(SwCadNode node)
    {
        foreach (var childValue in node.GetChildren())
        {
            var child = childValue as SwCadNode;
            if (child != null && AssemblyScanner.IsIncluded(child)) Walk(child);
        }
    }

    private static void AddProblem(SwCadNode node, string rawVersion, string problem)
    {
        Problems.Add(new AssetVersionResult
        {
            InstancePath = node.InstancePath ?? node.Name,
            SourcePath = node.SourcePath ?? string.Empty,
            RawVersion = string.IsNullOrWhiteSpace(rawVersion) ? "<空>" : rawVersion.Trim(),
            Problem = problem
        });
    }
}
