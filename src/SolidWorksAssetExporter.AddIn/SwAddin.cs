using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace SolidWorksAssetExporter.AddIn
{
    [ComVisible(true)]
    [Guid("b5ec0c01-12dd-4afa-88ee-42e1178ba63d")]
    [ProgId("SolidWorksAssetExporter.AddIn")]
    public sealed class SwAddin : ISwAddin
    {
        private const int CommandGroupId = 8721;
        private SldWorks _application;
        private int _cookie;
        private CommandManager _commands;
        private ExportDialog _dialog;

        public bool ConnectToSW(object thisSw, int cookie)
        {
            var stage = "initialize";
            try
            {
                _application = (SldWorks)thisSw; _cookie = cookie;
                stage = "SetAddinCallbackInfo2";
                if (!_application.SetAddinCallbackInfo2(0, this, _cookie))
                {
                    WriteDiagnostic("ConnectToSW returned false at " + stage + ".");
                    return false;
                }
                stage = "AddCommands";
                AddCommands();
                WriteDiagnostic("ConnectToSW succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnostic("ConnectToSW failed at " + stage + ": " + ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            var stage = "close dialog";
            try
            {
                // FormClosed clears _dialog. Keep a local reference so shutdown never
                // dereferences the field after Close() has raised that event.
                var dialog = _dialog;
                _dialog = null;
                if (dialog != null)
                {
                    dialog.Close();
                    if (!dialog.IsDisposed) dialog.Dispose();
                }
                stage = "remove command group";
                if (_commands != null) _commands.RemoveCommandGroup2(CommandGroupId, true);
                // The host owns these RCWs. FinalReleaseComObject can invalidate another
                // live reference while SOLIDWORKS is shutting down and crash the native process.
                _commands = null;
                _application = null;
                WriteDiagnostic("DisconnectFromSW succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnostic("DisconnectFromSW failed at " + stage + ": " + ex);
                _commands = null;
                _application = null;
                return false;
            }
        }

        public void OnExport()
        {
            if (_dialog != null && !_dialog.IsDisposed) { _dialog.Activate(); return; }
            _dialog = new ExportDialog(_application); _dialog.FormClosed += delegate { _dialog = null; }; _dialog.Show();
        }

        public int CanExport()
        {
            try { return _application != null && _application.ActiveDoc != null ? 1 : 0; }
            catch { return 0; }
        }

        private void AddCommands()
        {
            _commands = _application.GetCommandManager(_cookie);
            int errors = 0;
            var group = _commands.CreateCommandGroup2(CommandGroupId, "Asset / assembly_package 导出", "导出 Asset 库和 assembly_package 装配包",
                "Asset / assembly_package 导出", -1, true, ref errors);
            if (group == null || errors != (int)swCreateCommandGroupErrors.swCreateCommandGroup_Success)
                throw new InvalidOperationException("无法创建 SOLIDWORKS 命令组，错误码: " + errors);
            group.AddCommandItem2("Asset / assembly_package 导出", -1, "分类预览并导出", "Asset / assembly_package 导出", -1,
                "OnExport", "CanExport", 0, (int)swCommandItemType_e.swMenuItem);
            group.HasMenu = true; group.HasToolbar = false; group.Activate();
        }

        private static void WriteDiagnostic(string message)
        {
            try
            {
                var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksAssetExporter");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "addin.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + System.Environment.NewLine);
            }
            catch { }
        }

        [ComRegisterFunction]
        public static void Register(Type type)
        {
            var id = "{" + type.GUID.ToString().ToUpperInvariant() + "}";
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + id))
            {
                key.SetValue(null, 1, RegistryValueKind.DWord);
                key.SetValue("Title", "Asset / assembly_package 混合导出");
                key.SetValue("Description", "导出 Asset 库和 SOLIDWORKS assembly_package（装配包）");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + id))
                key.SetValue(null, 1, RegistryValueKind.DWord);
        }

        [ComUnregisterFunction]
        public static void Unregister(Type type)
        {
            var id = "{" + type.GUID.ToString().ToUpperInvariant() + "}";
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\Addins\" + id, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + id, false);
        }
    }
}
