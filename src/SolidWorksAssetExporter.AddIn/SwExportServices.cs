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
        private readonly SwPluginMutationTracker _mutationTracker;
        public SwGeometryExporter(SldWorks app, SwPluginMutationTracker mutationTracker)
        {
            _app = app; _mutationTracker = mutationTracker;
        }

        public void ExportBoth(SwCadNode node, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            using (var lease = node.OpenDocument())
            using (new SwActiveDocumentScope(_app, lease.Document, !lease.OpenedHere))
            using (new SwReferencedConfigurationScope(lease.Document, node.ReferencedConfiguration, _mutationTracker))
            using (new SwSelectionScope(lease.Document))
            {
                SelectVisibleGeometry(lease.Document);
                Save(lease.Document, Path.Combine(destinationDirectory, "model.step"));
                SelectVisibleGeometry(lease.Document);
                Save(lease.Document, Path.Combine(destinationDirectory, "model.stl"));
            }
        }

        private static void SelectVisibleGeometry(ModelDoc2 document)
        {
            document.ClearSelection2(true);
            if (document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;
            var root = document.ConfigurationManager.ActiveConfiguration.GetRootComponent3(false);
            if (root == null) throw new ValidationException("活动子装配配置缺少根组件。");
            var selected = SelectVisibleLeaves(root);
            if (selected == 0) throw new ValidationException("装配体没有可导出的可见、未抑制、非包络实体组件：" + document.GetTitle());
        }

        private static int SelectVisibleLeaves(Component2 parent)
        {
            var children = parent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                var extension = Path.GetExtension(parent.GetPathName());
                if (string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!parent.Select4(true, null, false)) throw new ValidationException("无法选择可见组件用于几何导出：" + parent.Name2);
                    return 1;
                }
                throw new ValidationException("可见子装配体没有可遍历组件：" + parent.Name2);
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

    public sealed class SwSourcePackager
    {
        private readonly SldWorks _app;
        private readonly SwPluginMutationTracker _mutationTracker;
        public SwSourcePackager(SldWorks app, SwPluginMutationTracker mutationTracker)
        {
            _app = app; _mutationTracker = mutationTracker;
        }

        public IList<string> AssetModelFiles(SwCadNode node)
        {
            var files = AssetSourcePlanner.CollectModelFiles(node).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var file in files)
            {
                if (!File.Exists(file)) throw new ValidationException("Asset 源模型文件不存在：" + file);
                var open = _app.GetOpenDocumentByName(file) as ModelDoc2;
                if (open != null && _mutationTracker.IsDirty(open, false, file))
                    throw new ValidationException("Asset 层级模型存在未保存修改：" + file);
            }
            return files;
        }

        public string ContentFingerprint(SwCadNode node)
        {
            return ContentFingerprint(node, FileHash.Sha256);
        }

        public string ContentFingerprint(SwCadNode node, Func<string, string> hashFile)
        {
            if (node == null) throw new ArgumentNullException("node");
            if (hashFile == null) throw new ArgumentNullException("hashFile");
            // AssemblyPackage geometry units never use Pack and Go. ExportNodeKind.Project
            // remains the compatible XML/wire name for a terminal non-Asset unit whose fingerprint is based directly on its own saved source
            // document, whether that document is a part or an assembly. Pack and Go remains
            // reserved exclusively for assembly Assets in PackAsset below.
            var path = Path.GetFullPath(node.SourcePath);
            if (!File.Exists(path)) throw new ValidationException("AssemblyPackage 几何源模型文件不存在：" + path);
            var open = _app == null ? null : _app.GetOpenDocumentByName(path) as ModelDoc2;
            if (open != null && _mutationTracker.IsDirty(open, false, path))
                throw new ValidationException("AssemblyPackage 几何源模型存在未保存修改：" + path);
            var entry = Canonical.Join(Path.GetFileName(path),
                new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), hashFile(path));
            return FileHash.Sha256Text(Canonical.Join(
                IdentityService.ModelSeed(node.ClassificationModel), entry));
        }

        public void PackAsset(SwCadNode node, IEnumerable<string> modelFiles, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            if (AssetSourcePathPolicy.IsSessionEmbeddedModelPath(node.SourcePath))
                throw new ValidationException("Asset 根是 SOLIDWORKS 虚拟/内嵌组件，无法生成独立可复用的源模型包。" +
                    "请先在 SOLIDWORKS 中将该 Asset 根保存为外部 SLDASM/SLDPRT 文件，然后重新预览。");
            var files = (modelFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) throw new ValidationException("Asset 没有可打包的源模型文件。");
            if (node.SourceDocumentKind == DocumentKind.Part)
            {
                if (files.Count != 1) throw new ValidationException("零件 Asset 只能包含自身源模型。");
                File.Copy(files[0], Path.Combine(destinationDirectory, Path.GetFileName(files[0])), false);
                return;
            }
            if (node.SourceDocumentKind != DocumentKind.Assembly)
                throw new ValidationException("不支持的 Asset 模型类型：" + node.SourceDocumentKind);

            using (var lease = node.OpenDocument())
            using (new SwActiveDocumentScope(_app, lease.Document, !lease.OpenedHere))
            using (new SwReferencedConfigurationScope(lease.Document, node.ReferencedConfiguration, _mutationTracker))
            {
                EnsureAssetRootDocument(lease.Document, node.SourcePath);
                EnsureAssetRootDocument(_app.ActiveDoc as ModelDoc2, node.SourcePath);
                PackAssembly(lease.Document, files, destinationDirectory);
            }
        }

        private static void EnsureAssetRootDocument(ModelDoc2 document, string expectedPath)
        {
            if (document == null) throw new ValidationException("Asset 根模型文档为空。");
            string actualPath;
            try { actualPath = document.GetPathName(); }
            catch (Exception ex)
            {
                throw new ValidationException("无法读取 Asset 根模型文档路径：" + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(expectedPath) || string.IsNullOrWhiteSpace(actualPath) ||
                !string.Equals(Path.GetFullPath(expectedPath), Path.GetFullPath(actualPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Asset 打包文档与 Asset 根模型不一致，已停止以避免把父装配体装入 Asset。" +
                    "\r\nAsset 根：" + expectedPath + "\r\n当前文档：" + actualPath);
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
            // IC~~/VC~~ models are stored inside their owning SLDASM. SOLIDWORKS exposes
            // temporary paths for inspection, but Pack and Go correctly omits those paths
            // from GetDocumentNames because there is no independent source file to copy.
            var missing = AssetSourcePathPolicy.MissingExternalFiles(allowed, packaged);
            if (missing.Count != 0)
                throw new ValidationException("Pack and Go 未发现 Asset 层级模型：" + SummarizePaths(missing));
            if (originalNames.Any(string.IsNullOrWhiteSpace))
                throw new ValidationException("Pack and Go 返回了空的源文件名。");
            var included = originalNames.Select((path, index) => new
                {
                    OriginalName = path,
                    FullPath = Path.GetFullPath(path),
                    Index = index
                })
                .Where(item => allowed.Contains(item.FullPath) &&
                    !AssetSourcePathPolicy.IsSessionEmbeddedModelPath(item.FullPath)).ToList();
            var duplicateNames = originalNames.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1 && group.Any(path => allowed.Contains(Path.GetFullPath(path))))
                .Select(group => group.Key).ToList();
            if (duplicateNames.Count != 0)
                throw new ValidationException("Pack and Go 扁平打包时 Asset 文件与其他依赖重名：" +
                    string.Join("; ", duplicateNames));

            // Some SOLIDWORKS sessions reject SetDocumentSaveToNames even when the official
            // same-length empty-entry removal contract is followed. Isolate the complete Pack
            // and Go result inside this export transaction, then promote only files explicitly
            // collected from the Asset subtree. Context references never reach source/, the
            // manifest, or the remote archive.
            var workingDirectory = Path.Combine(Path.GetDirectoryName(destinationDirectory),
                ".pack-and-go-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            try
            {
                var workingRoot = workingDirectory.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!packAndGo.SetSaveToName2(true, workingRoot))
                    throw new ValidationException("Pack and Go 无法设置隔离输出目录。");
                object saveNamesObject, documentStatusesObject;
                if (!packAndGo.GetDocumentSaveToNames(out saveNamesObject, out documentStatusesObject))
                    throw new ValidationException("Pack and Go 无法读取实际输出文件名。");
                var saveNames = ToStrings(saveNamesObject);
                if (saveNames.Count != originalNames.Count)
                    throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                        "Pack and Go 实际输出清单数量异常：原始清单 {0}，输出清单 {1}。",
                        originalNames.Count, saveNames.Count));
                var includedOutputs = included.Select(item => new
                    {
                        item.OriginalName,
                        SavePath = ResolvePackAndGoOutputPath(saveNames[item.Index], workingDirectory)
                    }).ToList();
                var duplicateOutputs = includedOutputs.GroupBy(item => Path.GetFileName(item.SavePath),
                        StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
                    .Select(group => group.Key).ToList();
                if (duplicateOutputs.Count != 0)
                    throw new ValidationException("Pack and Go 的 Asset 实际输出文件重名：" +
                        string.Join("; ", duplicateOutputs));
                var statuses = document.Extension.SavePackAndGo(packAndGo) as int[];
                if (statuses == null)
                    throw new ValidationException("Pack and Go 未返回保存状态。");
                // SOLIDWORKS documents SavePackAndGo only as returning an array of status
                // codes; it does not guarantee that the array length always matches the
                // earlier GetDocumentNames result. Some assemblies omit a context/reference
                // status even though the required files were saved. Only use per-file status
                // values when the one-to-one mapping is unambiguous. In every case, the
                // authoritative Asset-boundary check below requires every included output to
                // physically exist before it can be promoted into source/.
                if (statuses.Length == originalNames.Count)
                {
                    foreach (var item in included)
                        EnsurePackAndGoSucceeded(statuses[item.Index], item.OriginalName);
                }

                var missingOutputs = includedOutputs.Where(item => !File.Exists(item.SavePath))
                    .Select(item => item.SavePath).ToList();
                if (missingOutputs.Count != 0)
                    throw new ValidationException(string.Format(CultureInfo.InvariantCulture,
                        "Pack and Go 缺少 Asset 内输出文件（原始清单 {0}，Asset 内文件 {1}，返回状态 {2}）：{3}",
                        originalNames.Count, included.Count, statuses.Length, SummarizePaths(missingOutputs)));
                foreach (var item in includedOutputs)
                {
                    var source = item.SavePath;
                    var destination = Path.Combine(destinationDirectory, Path.GetFileName(item.SavePath));
                    if (File.Exists(destination))
                        throw new ValidationException("Asset 源文件目录存在同名模型，无法安全覆盖：" + destination);
                    File.Copy(source, destination, false);
                }
            }
            finally
            {
                DeleteWorkingDirectory(workingDirectory);
            }
        }

        private static void DeleteWorkingDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }
            Directory.Delete(directory, true);
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

        private static string ResolvePackAndGoOutputPath(string saveName, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(saveName))
                throw new ValidationException("Pack and Go 返回了空的实际输出文件名。");
            var fullPath = Path.IsPathRooted(saveName)
                ? Path.GetFullPath(saveName)
                : Path.GetFullPath(Path.Combine(workingDirectory, saveName));
            PathPolicy.RelativeTo(workingDirectory, fullPath);
            return fullPath;
        }

        private static string SummarizePaths(IEnumerable<string> paths)
        {
            var values = (paths ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            const int maximum = 10;
            var summary = string.Join("; ", values.Take(maximum));
            return values.Count <= maximum ? summary : summary + "; ...（共 " +
                values.Count.ToString(CultureInfo.InvariantCulture) + " 个）";
        }

    }

    public sealed class SwDrawingExporter
    {
        public SwDrawingExporter() { }

        public IList<string> CopyDrawingSources(IEnumerable<string> drawingFiles, string destinationDirectory)
        {
            var copied = new List<string>();
            Directory.CreateDirectory(destinationDirectory);
            foreach (var drawingPath in (drawingFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(drawingPath));
                if (File.Exists(destination))
                    throw new ValidationException("Asset 源文件目录存在同名图纸，无法安全覆盖：" + destination);
                File.Copy(drawingPath, destination, false);
                copied.Add(drawingPath);
            }
            return copied;
        }

        public IList<string> FindDirectDrawingFiles(IEnumerable<string> modelFiles)
        {
            var drawings = new List<string>();
            foreach (var modelPath in (modelFiles ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Preview only needs the exact same lookup rule as SOLIDWORKS "Open Drawing":
                // same directory and same file name. Reading the path directly avoids opening any
                // model or drawing window. Export only copies the original SLDDRW beside the
                // packaged SLDASM/SLDPRT files; PDF generation is intentionally disabled.
                var expectedDrawing = Path.ChangeExtension(modelPath, ".SLDDRW");
                if (!File.Exists(expectedDrawing)) continue;
                drawings.Add(Path.GetFullPath(expectedDrawing));
            }
            return drawings.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

    }
}
