using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            if (failure != null && throwOnFailure) throw new ValidationException("无法完整恢复 SOLIDWORKS 导出设置: " + failure.Message);
        }
    }

    public sealed class SwModelStateScope : IDisposable
    {
        private readonly ModelDoc2 _document;
        private readonly string _originalConfiguration;
        private readonly string _originalDisplayState;

        public SwModelStateScope(ModelDoc2 document, string targetConfiguration, string targetDisplayState)
        {
            _document = document;
            _originalConfiguration = document.ConfigurationManager.ActiveConfiguration.Name;
            _originalDisplayState = FirstDisplayState(document.ConfigurationManager.ActiveConfiguration);
            try
            {
                if (!string.IsNullOrWhiteSpace(targetConfiguration) && !document.ShowConfiguration2(targetConfiguration))
                    throw new ValidationException("无法激活配置: " + targetConfiguration);
                ApplyDisplayState(document.ConfigurationManager.ActiveConfiguration, targetDisplayState);
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(_originalConfiguration)) document.ShowConfiguration2(_originalConfiguration);
                ApplyDisplayState(document.ConfigurationManager.ActiveConfiguration, _originalDisplayState);
                throw;
            }
        }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(_originalConfiguration)) _document.ShowConfiguration2(_originalConfiguration);
            ApplyDisplayState(_document.ConfigurationManager.ActiveConfiguration, _originalDisplayState);
        }

        private static string FirstDisplayState(Configuration configuration)
        {
            try
            {
                var root = configuration.GetRootComponent3(true);
                if (root != null && !string.IsNullOrWhiteSpace(root.ReferencedDisplayState2)) return root.ReferencedDisplayState2;
                var names = configuration.GetDisplayStates() as string[];
                return names == null || names.Length == 0 ? string.Empty : names[0];
            }
            catch { return string.Empty; }
        }

        private static void ApplyDisplayState(Configuration configuration, string displayState)
        {
            if (configuration != null && !string.IsNullOrWhiteSpace(displayState) && !configuration.ApplyDisplayState(displayState))
                throw new ValidationException("无法激活显示状态: " + displayState);
        }
    }

    public sealed class SwSelectionScope : IDisposable
    {
        private readonly ModelDoc2 _document;
        private readonly IList<SelectedObject> _selected = new List<SelectedObject>();

        public SwSelectionScope(ModelDoc2 document)
        {
            _document = document;
            var manager = document.SelectionManager;
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
                    dynamic data = _document.SelectionManager.CreateSelectData(); data.Mark = item.Mark;
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
            using (new SwModelStateScope(node.Document, node.Model.Configuration, node.Model.DisplayState))
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
            if (selected == 0) throw new ValidationException("装配体没有可导出的可见、未抑制、非包络实体组件: " + document.GetTitle());
        }

        private static int SelectVisibleLeaves(Component2 parent)
        {
            var children = parent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                var model = parent.GetModelDoc2() as ModelDoc2;
                if (model == null) throw new ValidationException("可见组件未解析或未加载: " + parent.Name2);
                if (model.GetType() == (int)swDocumentTypes_e.swDocPART)
                {
                    if (!parent.Select4(true, null)) throw new ValidationException("无法选择可见组件用于几何导出: " + parent.Name2);
                    return 1;
                }
                throw new ValidationException("可见子装配体没有可遍历组件，请先完全解析: " + parent.Name2);
            }
            var count = 0;
            foreach (var value in children)
            {
                var child = value as Component2;
                if (child == null || child.IsSuppressed() || child.IsEnvelope() || child.IsHidden(false)) continue;
                count += SelectVisibleLeaves(child);
            }
            return count;
        }

        private static void Save(ModelDoc2 document, string path)
        {
            int errors = 0, warnings = 0;
            dynamic extension = document.Extension;
            var ok = (bool)extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
            if (!ok || errors != 0 || !File.Exists(path))
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                    "几何导出失败: {0}; errors={1}, warnings={2}", path, errors, warnings));
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
            if (document == null || errors != 0) throw new ValidationException("无法无重建地激活模型文档: " + title + "; errors=" + errors);
        }
    }

    public sealed class SwSourcePackager
    {
        private readonly SldWorks _app;
        public SwSourcePackager(SldWorks app) { _app = app; }

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
                if (open != null && open.GetSaveFlag()) throw new ValidationException("依赖模型存在未保存修改: " + file);
            }
            return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string ContentFingerprint(SwCadNode node)
        {
            var entries = DependencyFiles(node.Document).Select(path =>
                Canonical.Join(Path.GetFileName(path), new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), FileHash.Sha256(path)));
            return FileHash.Sha256Text(Canonical.Join(IdentityService.ModelSeed(node.Model), string.Join("\n", entries)));
        }

        public void Pack(ModelDoc2 document, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            var packAndGo = document.Extension.GetPackAndGo();
            packAndGo.IncludeDrawings = false;
            packAndGo.IncludeSuppressed = true;
            packAndGo.IncludeToolboxComponents = true;
            packAndGo.IncludeSimulationResults = false;
            packAndGo.FlattenToSingleFolder = false;
#pragma warning disable 618
            if (!packAndGo.SetSaveToName(true, destinationDirectory + Path.DirectorySeparatorChar))
#pragma warning restore 618
                throw new ValidationException("Pack and Go 无法设置 Asset 源文件目录。");
            var statusObject = document.Extension.SavePackAndGo(packAndGo);
            var statuses = statusObject as int[];
            if (statuses == null || statuses.Any(value => value != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed))
                throw new ValidationException("Pack and Go 未能完整保存 Asset 根模型及依赖。");
        }
    }

    public sealed class SwDrawingExporter
    {
        private readonly SldWorks _app;
        public SwDrawingExporter(SldWorks app) { _app = app; }

        public IList<string> ExportDirectDrawings(SwCadNode asset, IEnumerable<string> extraDirectories,
            string sourceDestination, string pdfDestination)
        {
            var candidates = FindDirectDrawingFiles(asset, extraDirectories);
            var exported = new List<string>();
            foreach (var drawingPath in candidates)
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

        public IList<string> FindDirectDrawingFiles(SwCadNode asset, IEnumerable<string> extraDirectories)
        {
            return FindCandidates(asset.Model.FullPath, extraDirectories)
                .Where(path => DirectlyReferences(path, asset.Model.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IEnumerable<string> FindCandidates(string modelPath, IEnumerable<string> extraDirectories)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modelDirectory = Path.GetDirectoryName(modelPath); if (Directory.Exists(modelDirectory)) directories.Add(modelDirectory);
            foreach (var value in extraDirectories ?? Enumerable.Empty<string>()) if (Directory.Exists(value)) directories.Add(Path.GetFullPath(value));
            try
            {
                var search = _app.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType);
                foreach (var value in (search ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Directory.Exists(value.Trim())) directories.Add(Path.GetFullPath(value.Trim()));
            }
            catch { }
            foreach (var directory in directories)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(directory, "*.SLDDRW", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var file in files) yield return file;
            }
        }

        private bool DirectlyReferences(string drawingPath, string modelPath)
        {
            bool openedHere; var document = OpenDrawing(drawingPath, out openedHere);
            try
            {
                var drawing = document as DrawingDoc;
                var sheetGroups = drawing == null ? null : drawing.GetViews() as object[];
                if (sheetGroups == null) return false;
                foreach (var group in sheetGroups)
                {
                    var views = group as object[]; if (views == null) continue;
                    foreach (var item in views)
                    {
                        var view = item as View; if (view == null) continue;
                        var reference = view.GetReferencedModelName();
                        if (SameModelReference(reference, drawingPath, modelPath)) return true;
                    }
                }
                return false;
            }
            finally { if (openedHere) _app.CloseDoc(document.GetTitle()); }
        }

        private void ExportAllSheetsPdf(string drawingPath, string destination)
        {
            bool openedHere; var document = OpenDrawing(drawingPath, out openedHere);
            try
            {
                var data = (ExportPdfData)_app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData);
                if (data == null || !data.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null))
                    throw new ValidationException("无法设置 PDF 全页导出: " + drawingPath);
                int errors = 0, warnings = 0; dynamic extension = document.Extension;
                var ok = (bool)extension.SaveAs3(destination, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, data, null, ref errors, ref warnings);
                if (!ok || errors != 0 || !File.Exists(destination))
                    throw new ValidationException("图纸 PDF 导出失败: " + drawingPath);
            }
            finally { if (openedHere) _app.CloseDoc(document.GetTitle()); }
        }

        private ModelDoc2 OpenDrawing(string path, out bool openedHere)
        {
            var existing = _app.GetOpenDocumentByName(path) as ModelDoc2;
            if (existing != null)
            {
                if (existing.GetSaveFlag()) throw new ValidationException("图纸存在未保存修改: " + path);
                openedHere = false; return existing;
            }
            int errors = 0, warnings = 0;
            var document = _app.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings) as ModelDoc2;
            if (document == null || errors != 0) throw new ValidationException("无法打开图纸: " + path);
            if (document.GetSaveFlag())
            {
                _app.CloseDoc(document.GetTitle());
                throw new ValidationException("图纸打开后存在未保存修改或需要重建: " + path);
            }
            openedHere = true; return document;
        }

        private static bool SameModelReference(string reference, string drawingPath, string modelPath)
        {
            if (string.IsNullOrWhiteSpace(reference)) return false;
            try
            {
                var resolved = Path.IsPathRooted(reference) ? Path.GetFullPath(reference) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(drawingPath), reference));
                return string.Equals(resolved, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
