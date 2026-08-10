using System;
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
            try
            {
                _application = (SldWorks)thisSw; _cookie = cookie;
                if (!_application.SetAddinCallbackInfo2(0, this, _cookie)) return false;
                AddCommands(); return true;
            }
            catch { return false; }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (_dialog != null) { _dialog.Close(); _dialog.Dispose(); _dialog = null; }
                if (_commands != null) _commands.RemoveCommandGroup2(CommandGroupId, true);
                if (_commands != null && Marshal.IsComObject(_commands)) Marshal.FinalReleaseComObject(_commands);
                if (_application != null && Marshal.IsComObject(_application)) Marshal.FinalReleaseComObject(_application);
                _commands = null; _application = null; return true;
            }
            catch { return false; }
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
            var group = _commands.CreateCommandGroup2(CommandGroupId, "Asset / Project 导出", "导出 Asset 库和 Project 装配 XML",
                "Asset / Project 导出", -1, true, ref errors);
            if (group == null || errors != (int)swCreateCommandGroupErrors.swCreateCommandGroup_Success)
                throw new InvalidOperationException("无法创建 SOLIDWORKS 命令组，错误码: " + errors);
            group.AddCommandItem2("Asset / Project 导出", -1, "分类预览并导出", "Asset / Project 导出", -1,
                "OnExport", "CanExport", 0, (int)swCommandItemType_e.swMenuItem);
            group.HasMenu = true; group.HasToolbar = false; group.Activate();
        }

        [ComRegisterFunction]
        public static void Register(Type type)
        {
            var id = "{" + type.GUID.ToString().ToUpperInvariant() + "}";
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + id))
            {
                key.SetValue(null, 1, RegistryValueKind.DWord);
                key.SetValue("Title", "Asset / Project 混合导出");
                key.SetValue("Description", "按 Asset 边界和最大无 Asset 子树导出 SOLIDWORKS 装配体");
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
