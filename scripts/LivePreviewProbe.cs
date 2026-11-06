using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;

internal static class LivePreviewProbe
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var app = Marshal.GetActiveObject("SldWorks.Application") as SldWorks;
            if (app == null) throw new InvalidOperationException("No SOLIDWORKS session in this user context.");
            var documents = (app.GetDocuments() as object[] ?? new object[0])
                .OfType<ModelDoc2>().ToList();
            if (args.Length != 0 && args[0] == "--dirty")
            {
                foreach (var doc in documents)
                {
                    bool dirty;
                    try { dirty = doc.GetSaveFlag(); }
                    catch { continue; }
                    if (dirty && doc.GetPathName().StartsWith(@"C:\Users\C12\Desktop\LCMS\",
                        StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("UNSAVED_DOC=" + doc.GetPathName());
                }
                return 0;
            }
            if (args.Length != 0 && args[0] == "--list")
            {
                var activeDocument = app.ActiveDoc as ModelDoc2;
                Console.WriteLine("ACTIVE_DOC=" + (activeDocument == null ? string.Empty : activeDocument.GetPathName()));
                foreach (var doc in documents)
                    if (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY &&
                        doc.GetPathName().StartsWith(@"C:\Users\C12\Desktop\LCMS\", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("OPEN_ASSEMBLY=" + doc.GetPathName());
                return 0;
            }
            var assemblyDocuments = documents.Where(doc => doc.GetType() ==
                (int)swDocumentTypes_e.swDocASSEMBLY).ToList();
            var requestedPath = args.Length == 0 ? string.Empty : (args[0] ?? string.Empty);
            ModelDoc2 target;
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                if (assemblyDocuments.Count != 1)
                    throw new InvalidOperationException("Specify one open assembly; count=" + assemblyDocuments.Count);
                target = assemblyDocuments[0];
            }
            else
            {
                target = assemblyDocuments.FirstOrDefault(doc => string.Equals(doc.GetPathName(),
                    requestedPath, StringComparison.OrdinalIgnoreCase));
                if (target == null) throw new InvalidOperationException("Assembly is not open: " + requestedPath);
            }

            var previous = app.ActiveDoc as ModelDoc2;
            if (previous == null) throw new InvalidOperationException("There is no active SOLIDWORKS document.");
            try
            {
                if (!string.Equals(previous.GetPathName(), target.GetPathName(), StringComparison.OrdinalIgnoreCase))
                {
                    int errors = 0;
                    if (app.ActivateDoc3(target.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) == null)
                        throw new InvalidOperationException("Could not activate assembly; error=" + errors);
                }
                var settings = new SettingsStore().Load();
                settings.WanxiangApiKey = new WanxiangApiKeyStore().Load();
                settings.UploadAfterExport = false;
                var result = new ExportCoordinator(app).Analyze(settings);
                Console.WriteLine("TEST_DOCUMENT=" + target.GetPathName());
                Console.WriteLine("CAN_EXPORT=" + result.CanExport);
                Console.WriteLine("PREVIEW_LENGTH=" + (result.Preview ?? string.Empty).Length);
                foreach (var line in (result.Preview ?? string.Empty).Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries).Take(12))
                    Console.WriteLine("PREVIEW_HEAD=" + line);
                foreach (var line in (result.Preview ?? string.Empty).Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries))
                    if (line.IndexOf("快换底座吸盘", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("通风橱底板", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("必须升级", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("实际几何未变", StringComparison.Ordinal) >= 0)
                        Console.WriteLine("PREVIEW=" + line);
                foreach (var error in result.ValidationErrors) Console.WriteLine("VALIDATION=" + error);
                return 0;
            }
            finally
            {
                var active = app.ActiveDoc as ModelDoc2;
                if (active != null && !string.Equals(previous.GetPathName(), active.GetPathName(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    int errors = 0;
                    app.ActivateDoc3(previous.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LIVE_PREVIEW_ERROR=" + ex);
            return 2;
        }
    }
}
