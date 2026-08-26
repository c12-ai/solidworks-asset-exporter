using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class SwExportPreferenceScope : IDisposable
    {
        private readonly SldWorks _app;
        private readonly IDictionary<int, int> _integers = new Dictionary<int, int>();
        private readonly IDictionary<int, bool> _toggles = new Dictionary<int, bool>();
        private readonly IDictionary<int, string> _strings = new Dictionary<int, string>();

        public SwExportPreferenceScope(SldWorks app)
        {
            _app = app;
            RememberInteger(swUserPreferenceIntegerValue_e.swStepAP);
            RememberInteger(swUserPreferenceIntegerValue_e.swStepExportPreference);
            RememberInteger(swUserPreferenceIntegerValue_e.swExportStlUnits);
            RememberInteger(swUserPreferenceIntegerValue_e.swSTLQuality);
            RememberToggle(swUserPreferenceToggle_e.swStepExportAtomicSave);
            RememberToggle(swUserPreferenceToggle_e.swSTLBinaryFormat);
            RememberToggle(swUserPreferenceToggle_e.swSTLDontTranslateToPositive);
            RememberToggle(swUserPreferenceToggle_e.swSTLComponentsIntoOneFile);
            RememberToggle(swUserPreferenceToggle_e.swSTLShowInfoOnSave);
            RememberToggle(swUserPreferenceToggle_e.swSTLPreview);
            RememberToggle(swUserPreferenceToggle_e.swSTLCheckForInterference);
            RememberString(swUserPreferenceStringValue_e.swExportOutputCoordinateSystem);
            try { ApplyRequiredSettings(); }
            catch { Restore(false); throw; }
        }

        private void ApplyRequiredSettings()
        {
            _app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214);
            _app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepExportPreference,
                (int)swAcisOutputGeometryPreference_e.swAcisOutputAsSolidAndSurface);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swStepExportAtomicSave, false);
            _app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swExportStlUnits, (int)swLengthUnit_e.swMETER);
            _app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSTLQuality, (int)swSTLQuality_e.swSTLQuality_Fine);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLBinaryFormat, true);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLDontTranslateToPositive, true);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLComponentsIntoOneFile, true);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLShowInfoOnSave, false);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLPreview, false);
            _app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSTLCheckForInterference, false);
            _app.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swExportOutputCoordinateSystem, string.Empty);
        }

        private void RememberInteger(swUserPreferenceIntegerValue_e value) { _integers[(int)value] = _app.GetUserPreferenceIntegerValue((int)value); }
        private void RememberToggle(swUserPreferenceToggle_e value) { _toggles[(int)value] = _app.GetUserPreferenceToggle((int)value); }
        private void RememberString(swUserPreferenceStringValue_e value) { _strings[(int)value] = _app.GetUserPreferenceStringValue((int)value); }

        public void Dispose()
        {
            Restore(true);
        }

        private void Restore(bool throwOnFailure)
        {
            Exception failure = null;
            foreach (var value in _integers) try { _app.SetUserPreferenceIntegerValue(value.Key, value.Value); } catch (Exception ex) { failure = failure ?? ex; }
            foreach (var value in _toggles) try { _app.SetUserPreferenceToggle(value.Key, value.Value); } catch (Exception ex) { failure = failure ?? ex; }
            foreach (var value in _strings) try { _app.SetUserPreferenceStringValue(value.Key, value.Value); } catch (Exception ex) { failure = failure ?? ex; }
            if (failure != null && throwOnFailure) throw new ValidationException("无法完整恢复 SOLIDWORKS 导出设置：" + failure.Message);
        }
    }

    public sealed class SwSelectionScope : IDisposable
    {
        private readonly ModelDoc2 _document;
        private readonly IList<SelectedObject> _selected = new List<SelectedObject>();

        public SwSelectionScope(ModelDoc2 document)
        {
            _document = document;
            var manager = (SelectionMgr)document.SelectionManager;
            var count = manager.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count; i++)
                _selected.Add(new SelectedObject { Value = manager.GetSelectedObject6(i, -1), Mark = manager.GetSelectedObjectMark(i) });
            _document.ClearSelection2(true);
        }

        public void Dispose()
        {
            _document.ClearSelection2(true);
            foreach (var item in _selected)
            {
                try
                {
                    dynamic data = ((SelectionMgr)_document.SelectionManager).CreateSelectData(); data.Mark = item.Mark;
                    ((dynamic)item.Value).Select4(true, data);
                }
                catch { try { ((dynamic)item.Value).Select2(true, item.Mark); } catch { } }
            }
        }

        private sealed class SelectedObject { public object Value { get; set; } public int Mark { get; set; } }
    }

    public sealed class SwGeometryExporter
    {
        private readonly SldWorks _app;
        public SwGeometryExporter(SldWorks app) { _app = app; }

        public void ExportBoth(SwCadNode node, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            using (new SwActiveDocumentScope(_app, node.Document))
            using (new SwSelectionScope(node.Document))
            {
                SelectVisibleGeometry(node.Document);
                Save(node.Document, Path.Combine(destinationDirectory, "model.step"));
                SelectVisibleGeometry(node.Document);
                Save(node.Document, Path.Combine(destinationDirectory, "model.stl"));
            }
        }

        private static void SelectVisibleGeometry(ModelDoc2 document)
        {
            document.ClearSelection2(true);
            if (document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;
            var root = document.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true);
            if (root == null) throw new ValidationException("活动子装配配置缺少根组件。");
            var selected = SelectVisibleLeaves(root);
            if (selected == 0) throw new ValidationException("装配体没有可导出的可见、未抑制、非包络实体组件：" + document.GetTitle());
        }

        private static int SelectVisibleLeaves(Component2 parent)
        {
            var children = parent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                var model = parent.GetModelDoc2() as ModelDoc2;
                if (model == null) throw new ValidationException("可见组件未解析或未加载：" + parent.Name2);
                if (model.GetType() == (int)swDocumentTypes_e.swDocPART)
                {
                    if (!parent.Select4(true, null, false)) throw new ValidationException("无法选择可见组件用于几何导出：" + parent.Name2);
                    return 1;
                }
                throw new ValidationException("可见子装配体没有可遍历组件，请先完全解析：" + parent.Name2);
            }
            var count = 0;
            foreach (var value in children)
            {
                var child = value as Component2;
                if (child == null || SwComponentState.IsSuppressed(child) || child.IsEnvelope() || !SwComponentState.IsVisible(child)) continue;
                count += SelectVisibleLeaves(child);
            }
            return count;
        }

        private static void Save(ModelDoc2 document, string path)
        {
            int errors = 0, warnings = 0;
            var ok = document.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
            if (!ok || errors != 0 || !File.Exists(path))
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "几何导出失败：{0}; errors={1}, warnings={2}", path, errors, warnings));
        }
    }

    public sealed class SwActiveDocumentScope : IDisposable
    {
        private readonly SldWorks _app;
        private readonly string _originalTitle;
        private readonly string _targetTitle;

        public SwActiveDocumentScope(SldWorks app, ModelDoc2 target)
        {
            _app = app;
            var original = app.ActiveDoc as ModelDoc2;
            _originalTitle = original == null ? string.Empty : original.GetTitle();
            _targetTitle = target.GetTitle();
            if (!string.Equals(_originalTitle, _targetTitle, StringComparison.OrdinalIgnoreCase)) Activate(_targetTitle);
        }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(_originalTitle) && !string.Equals(_originalTitle, _targetTitle, StringComparison.OrdinalIgnoreCase))
                Activate(_originalTitle);
        }

        private void Activate(string title)
        {
            int errors = 0;
            var document = _app.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
            if (document == null || errors != 0) throw new ValidationException("无法无重建地激活模型文档：" + title + "; errors=" + errors);
        }
    }

    public sealed class SwSourcePackager
    {
        private readonly SldWorks _app;
        public SwSourcePackager(SldWorks app) { _app = app; }

        public IList<string> AssetModelFiles(SwCadNode node)
        {
            var files = AssetSourcePlanner.CollectModelFiles(node).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var file in files)
            {
                if (!File.Exists(file)) throw new ValidationException("Asset 源模型文件不存在：" + file);
                var open = _app.GetOpenDocumentByName(file) as ModelDoc2;
                if (open != null && open.GetSaveFlag()) throw new ValidationException("Asset 层级模型存在未保存修改：" + file);
            }
            return files;
        }

        public IList<string> DependencyFiles(ModelDoc2 document)
        {
            var packAndGo = document.Extension.GetPackAndGo();
            packAndGo.IncludeDrawings = false;
            packAndGo.IncludeSuppressed = true;
            object namesObject;
            if (!packAndGo.GetDocumentNames(out namesObject)) throw new ValidationException("Pack and Go 无法读取模型依赖。");
            var names = namesObject as object[];
            var files = names == null ? new List<string>() : names.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (File.Exists(document.GetPathName()) && !files.Contains(document.GetPathName(), StringComparer.OrdinalIgnoreCase)) files.Add(document.GetPathName());
            foreach (var file in files)
            {
                var open = _app == null ? null : _app.GetOpenDocumentByName(file) as ModelDoc2;
                if (open != null && open.GetSaveFlag()) throw new ValidationException("依赖模型存在未保存修改：" + file);
            }
            return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string ContentFingerprint(SwCadNode node)
        {
            var entries = DependencyFiles(node.Document).Select(path =>
                Canonical.Join(Path.GetFileName(path), new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), FileHash.Sha256(path)));
            return FileHash.Sha256Text(Canonical.Join(IdentityService.ModelSeed(node.Model), string.Join("\n", entries)));
        }

        public void PackAsset(SwCadNode node, IEnumerable<string> modelFiles, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            var files = (modelFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) throw new ValidationException("Asset 没有可打包的源模型文件。");
            if (node.Model.DocumentKind == DocumentKind.Part)
            {
                if (files.Count != 1) throw new ValidationException("零件 Asset 只能包含自身源模型。");
                File.Copy(files[0], Path.Combine(destinationDirectory, Path.GetFileName(files[0])), false);
                return;
            }
            if (node.Model.DocumentKind != DocumentKind.Assembly)
                throw new ValidationException("不支持的 Asset 模型类型：" + node.Model.DocumentKind);

            PackAssembly(node.Document, files, destinationDirectory);
        }

        private static void PackAssembly(ModelDoc2 document, IList<string> allowedFiles, string destinationDirectory)
        {
            var packAndGo = document.Extension.GetPackAndGo();
            packAndGo.IncludeDrawings = false;
            packAndGo.IncludeSuppressed = false;
            packAndGo.IncludeToolboxComponents = true;
            packAndGo.IncludeSimulationResults = false;
            packAndGo.FlattenToSingleFolder = true;

            object namesObject;
            if (!packAndGo.GetDocumentNames(out namesObject)) throw new ValidationException("Pack and Go 无法读取装配体依赖。");
            var originalNames = ToStrings(namesObject);
            var allowed = new HashSet<string>(allowedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var packaged = new HashSet<string>(originalNames.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var missing = allowed.Where(path => !packaged.Contains(path)).ToList();
            if (missing.Count != 0)
                throw new ValidationException("Pack and Go 未发现 Asset 层级模型：" + string.Join("; ", missing));

            var destinations = BuildDestinationNames(allowedFiles, destinationDirectory);
            var saveNames = new string[originalNames.Count];
            for (var i = 0; i < originalNames.Count; i++)
            {
                string destination;
                saveNames[i] = destinations.TryGetValue(Path.GetFullPath(originalNames[i]), out destination) ? destination : string.Empty;
            }
            if (!packAndGo.SetDocumentSaveToNames(saveNames))
                throw new ValidationException("Pack and Go 无法设置 Asset 层级文件清单。");
            var statuses = document.Extension.SavePackAndGo(packAndGo) as int[];
            if (statuses == null)
                throw new ValidationException("Pack and Go 未返回保存状态。");
            var selectedIndexes = Enumerable.Range(0, saveNames.Length)
                .Where(index => !string.IsNullOrWhiteSpace(saveNames[index])).ToList();
            if (statuses.Length == originalNames.Count)
            {
                foreach (var index in selectedIndexes)
                    EnsurePackAndGoSucceeded(statuses[index], originalNames[index]);
            }
            else if (statuses.Length == selectedIndexes.Count)
            {
                for (var i = 0; i < selectedIndexes.Count; i++)
                    EnsurePackAndGoSucceeded(statuses[i], originalNames[selectedIndexes[i]]);
            }
            else
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "Pack and Go 保存状态数量异常：原始文件 {0}，实际保存 {1}，返回状态 {2}。",
                    originalNames.Count, selectedIndexes.Count, statuses.Length));
            }
            var expectedOutputs = new HashSet<string>(destinations.Values.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var actualOutputs = new HashSet<string>(Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var missingOutputs = expectedOutputs.Where(path => !actualOutputs.Contains(path)).ToList();
            if (missingOutputs.Count != 0)
                throw new ValidationException("Pack and Go 缺少输出文件：" + string.Join("; ", missingOutputs));
            var unexpectedOutputs = actualOutputs.Where(path => !expectedOutputs.Contains(path)).ToList();
            if (unexpectedOutputs.Count != 0)
                throw new ValidationException("Pack and Go 输出了 Asset 层级外文件：" + string.Join("; ", unexpectedOutputs));
        }

        private static void EnsurePackAndGoSucceeded(int status, string originalName)
        {
            if (status != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed)
                throw new ValidationException("Pack and Go 未能保存 Asset 层级模型：" + originalName + "；status=" +
                    status.ToString(CultureInfo.InvariantCulture));
        }

        private static IList<string> ToStrings(object values)
        {
            var array = values as object[];
            if (array != null) return array.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).ToList();
            var strings = values as string[];
            return strings == null ? new List<string>() : strings.ToList();
        }

        private static IDictionary<string, string> BuildDestinationNames(IEnumerable<string> files, string destinationDirectory)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(source);
                if (!usedNames.Add(fileName))
                {
                    var stem = Path.GetFileNameWithoutExtension(fileName);
                    var extension = Path.GetExtension(fileName);
                    var suffix = FileHash.Sha256(source).Substring(0, 8);
                    fileName = stem + "_" + suffix + extension;
                    var counter = 2;
                    while (!usedNames.Add(fileName)) fileName = stem + "_" + suffix + "_" + (counter++).ToString(CultureInfo.InvariantCulture) + extension;
                }
                result.Add(source, Path.Combine(destinationDirectory, fileName));
            }
            return result;
        }
    }

    public sealed class SwDrawingExporter
    {
        // SolidWorks.Interop.swcommands.swCommands_e.swCommands_Open_Associated_Drw.
        private const int OpenAssociatedDrawingCommandId = 2458;
        private readonly SldWorks _app;
        public SwDrawingExporter(SldWorks app) { _app = app; }

        public IList<string> ExportDrawings(IEnumerable<string> drawingFiles, string sourceDestination, string pdfDestination)
        {
            var exported = new List<string>();
            foreach (var drawingPath in (drawingFiles ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(sourceDestination); Directory.CreateDirectory(pdfDestination);
                var sourceName = UniquePath(sourceDestination, Path.GetFileName(drawingPath));
                File.Copy(drawingPath, sourceName, false);
                var pdfName = UniquePath(pdfDestination, Path.GetFileNameWithoutExtension(drawingPath) + ".pdf");
                ExportAllSheetsPdf(drawingPath, pdfName);
                exported.Add(drawingPath);
            }
            return exported;
        }

        public IList<string> FindDirectDrawingFiles(IEnumerable<string> modelFiles)
        {
            var drawings = new List<string>();
            var original = _app.ActiveDoc as ModelDoc2;
            if (original == null) throw new ValidationException("查找工程图时没有活动的 SOLIDWORKS 文档。");

            foreach (var modelPath in (modelFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // SOLIDWORKS 的“打开工程图”只自动查找模型同目录下的同名图纸。
                // 先判断文件是否存在，避免命令在未找到时弹出全局浏览窗口。
                var expectedDrawing = Path.ChangeExtension(modelPath, ".SLDDRW");
                if (!File.Exists(expectedDrawing)) continue;
                OpenUsingSolidWorksCommand(modelPath, expectedDrawing, original);
                drawings.Add(Path.GetFullPath(expectedDrawing));
            }
            RestoreActiveDocument(original);
            return drawings.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void OpenUsingSolidWorksCommand(string modelPath, string expectedDrawing, ModelDoc2 original)
        {
            var model = _app.GetOpenDocumentByName(modelPath) as ModelDoc2;
            if (model == null) throw new ValidationException("Asset 模型未在 SOLIDWORKS 中加载：" + modelPath);
            var alreadyOpen = _app.GetOpenDocumentByName(expectedDrawing) as ModelDoc2;
            try
            {
                ActivateDocument(model);
                model.ClearSelection2(true);
                if (!_app.RunCommand(OpenAssociatedDrawingCommandId, "Asset Exporter: Open Drawing"))
                    throw new ValidationException("SOLIDWORKS“打开工程图”命令执行失败：" + modelPath);
                var opened = _app.GetOpenDocumentByName(expectedDrawing) as ModelDoc2;
                if (opened == null)
                    throw new ValidationException("SOLIDWORKS 未打开模型对应的工程图：" + expectedDrawing);
            }
            finally
            {
                if (alreadyOpen == null)
                {
                    var opened = _app.GetOpenDocumentByName(expectedDrawing) as ModelDoc2;
                    if (opened != null) _app.CloseDoc(opened.GetTitle());
                }
                RestoreActiveDocument(original);
            }
        }

        private void ActivateDocument(ModelDoc2 document)
        {
            int errors = 0;
            var active = _app.ActivateDoc3(document.GetTitle(), false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
            if (active == null || errors != 0)
                throw new ValidationException("无法激活模型以调用“打开工程图”：" + document.GetPathName() + "; errors=" + errors);
        }

        private void RestoreActiveDocument(ModelDoc2 document)
        {
            if (document == null) return;
            var active = _app.ActiveDoc as ModelDoc2;
            if (active != null && string.Equals(active.GetTitle(), document.GetTitle(), StringComparison.OrdinalIgnoreCase)) return;
            ActivateDocument(document);
        }

        private void ExportAllSheetsPdf(string drawingPath, string destination)
        {
            EnsureUnicodePdfFontAvailable();
            bool openedHere; var document = OpenDrawing(drawingPath, out openedHere);
            try
            {
                using (new SwActiveDocumentScope(_app, document))
                {
                    var data = (ExportPdfData)_app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData);
                    var drawing = document as DrawingDoc;
                    var sheetNames = DrawingSheetNames(drawing);
                    if (sheetNames.Count == 0)
                        throw new ValidationException("图纸不包含可导出的 Sheet：" + drawingPath);
                    var currentSheet = drawing.GetCurrentSheet() as Sheet;
                    var currentSheetName = currentSheet == null ? string.Empty : currentSheet.GetName();
                    var sheets = new DispatchWrapper[sheetNames.Count];
                    try
                    {
                        for (var i = 0; i < sheetNames.Count; i++)
                        {
                            if (!drawing.ActivateSheet(sheetNames[i]))
                                throw new ValidationException("无法激活图纸 Sheet：" + sheetNames[i] + "；" + drawingPath);
                            var sheet = drawing.GetCurrentSheet() as Sheet;
                            if (sheet == null) throw new ValidationException("无法读取图纸 Sheet：" + sheetNames[i] + "；" + drawingPath);
                            sheets[i] = new DispatchWrapper(sheet);
                        }
                    }
                    finally
                    {
                        if (!string.IsNullOrWhiteSpace(currentSheetName)) drawing.ActivateSheet(currentSheetName);
                    }
                    if (data == null || !data.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportSpecifiedSheets, sheets))
                        throw new ValidationException("无法设置 PDF 全页导出：" + drawingPath);
                    data.ViewPdfAfterSaving = false;
                    int errors = 0, warnings = 0;
                    var ok = document.Extension.SaveAs(destination, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, data, ref errors, ref warnings);
                    if (!ok || errors != 0 || !File.Exists(destination))
                        throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                            "图纸 PDF 导出失败：{0}；目标：{1}；errors={2}, warnings={3}, SaveAs={4}",
                            drawingPath, destination, errors, warnings, ok));
                }
            }
            finally { if (openedHere) _app.CloseDoc(document.GetTitle()); }
        }

        private static IList<string> DrawingSheetNames(DrawingDoc drawing)
        {
            if (drawing == null) return new List<string>();
            var values = drawing.GetSheetNames();
            var strings = values as string[];
            if (strings != null) return strings.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            var objects = values as object[];
            return objects == null ? new List<string>() : objects.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        }

        private static void EnsureUnicodePdfFontAvailable()
        {
            using (var fonts = new InstalledFontCollection())
            {
                if (fonts.Families.Any(font => string.Equals(font.Name, "Arial Unicode MS", StringComparison.OrdinalIgnoreCase))) return;
            }
            throw new ValidationException("系统缺少或未正确安装字体“Arial Unicode MS”，SOLIDWORKS 无法导出包含中文等非英文字符的 PDF。" +
                "请从有合法 Microsoft Office 授权的安装介质修复该字体，或先把图纸文字替换为系统已安装且支持中文的字体，然后重试。");
        }

        private ModelDoc2 OpenDrawing(string path, out bool openedHere)
        {
            var existing = _app.GetOpenDocumentByName(path) as ModelDoc2;
            if (existing != null)
            {
                if (existing.GetSaveFlag()) throw new ValidationException("图纸存在未保存修改：" + path);
                openedHere = false; return existing;
            }
            int errors = 0, warnings = 0;
            var document = _app.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                string.Empty, ref errors, ref warnings) as ModelDoc2;
            if (document == null || errors != 0) throw new ValidationException("无法打开图纸：" + path);
            openedHere = true; return document;
        }

        private static string UniquePath(string directory, string fileName)
        {
            var candidate = Path.Combine(directory, fileName); var index = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "_" + index + Path.GetExtension(fileName)); index++;
            }
            return candidate;
        }
    }
}
