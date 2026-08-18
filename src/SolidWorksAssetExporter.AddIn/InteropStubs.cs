// Compile-only contract stubs used by scripts/build-core.ps1 on machines without SOLIDWORKS.
// Production builds exclude this file and reference the official SOLIDWORKS Interop assemblies.
using System;

namespace SolidWorks.Interop.swpublished
{
    public interface ISwAddin { bool ConnectToSW(object thisSw, int cookie); bool DisconnectFromSW(); }
}

namespace SolidWorks.Interop.sldworks
{
    public class SldWorks
    {
        public object ActiveDoc { get; set; }
        public bool SetAddinCallbackInfo2(int reserved, object addin, int cookie) { return true; }
        public CommandManager GetCommandManager(int cookie) { return null; }
        public int GetUserPreferenceIntegerValue(int value) { return 0; }
        public bool SetUserPreferenceIntegerValue(int value, int setting) { return true; }
        public bool GetUserPreferenceToggle(int value) { return false; }
        public bool SetUserPreferenceToggle(int value, bool setting) { return true; }
        public string GetUserPreferenceStringValue(int value) { return string.Empty; }
        public bool SetUserPreferenceStringValue(int value, string setting) { return true; }
        public object GetExportFileData(int type) { return null; }
        public object GetOpenDocumentByName(string path) { return null; }
        public string GetSearchFolders(int type) { return string.Empty; }
        public object OpenDoc6(string path, int type, int options, string configuration, ref int errors, ref int warnings) { return null; }
        public object ActivateDoc3(string title, bool usePreferences, int option, ref int errors) { return null; }
        public void CloseDoc(string title) { }
    }

    public interface Component2
    {
        string Name2 { get; }
        int GetID(); int Visible { get; }
        bool IsSuppressed(); bool IsEnvelope(); bool IsFixed(); bool IsHidden(bool considerSuppressed); bool Select4(bool append, object data, bool showPopup);
        object GetModelDoc2(); object GetChildren();
        MathTransform Transform2 { get; }
    }

    public interface MathTransform { object ArrayData { get; } }

    public interface ModelDoc2
    {
        int GetType(); string GetPathName(); string GetTitle(); bool GetSaveFlag(); string get_SummaryInfo(int fieldId);
        ConfigurationManager ConfigurationManager { get; }
        ModelDocExtension Extension { get; }
        object SelectionManager { get; }
        void ClearSelection2(bool all);
    }

    public interface DrawingDoc : ModelDoc2 { object GetViews(); }
    public interface View
    {
        ModelDoc2 ReferencedDocument { get; }
        string GetReferencedModelName(); object GetBaseView();
    }
    public interface ConfigurationManager { Configuration ActiveConfiguration { get; } }
    public interface Configuration
    {
        string Name { get; } Component2 GetRootComponent3(bool resolve); object GetDisplayStates();
    }
    public interface ModelDocExtension
    {
        PackAndGo GetPackAndGo(); object SavePackAndGo(PackAndGo value);
        bool SaveAs3(string name, int version, int options, object exportData, object advancedSaveAsOptions,
            ref int errors, ref int warnings);
    }
    public interface CustomPropertyManager
    {
        object GetNames(); int Get6(string name, bool cached, out string raw, out string resolved, out bool wasResolved, out bool linked);
    }
    public interface SelectionMgr
    {
        int GetSelectedObjectCount2(int mark); object GetSelectedObject6(int index, int mark);
        int GetSelectedObjectMark(int index); object CreateSelectData();
    }
    public interface ExportPdfData { bool SetSheets(int mode, object sheets); }
    public interface PackAndGo
    {
        bool IncludeDrawings { get; set; } bool IncludeSuppressed { get; set; }
        bool IncludeToolboxComponents { get; set; } bool IncludeSimulationResults { get; set; }
        bool FlattenToSingleFolder { get; set; }
        bool GetDocumentNames(out object names); bool SetDocumentSaveToNames(object names); bool SetSaveToName(bool value, string path);
    }
    public interface CommandManager
    {
        CommandGroup CreateCommandGroup2(int id, string title, string tooltip, string hint, int position, bool ignorePrevious, ref int errors);
        bool RemoveCommandGroup2(int id, bool runtimeOnly);
    }
    public interface CommandGroup
    {
        int AddCommandItem2(string name, int position, string hint, string tooltip, int imageListIndex,
            string callback, string enable, int userId, int menuToolbarOption);
        bool HasMenu { get; set; } bool HasToolbar { get; set; } bool Activate();
    }
}

namespace SolidWorks.Interop.swconst
{
    public enum swComponentVisibilityState_e { swComponentHidden = 0, swComponentVisible = 1, swComponentUnknown = -1 }
    public enum swSummInfoField_e { swSumInfoCreateDate = 6 }
    public enum swDocumentTypes_e { swDocPART = 1, swDocASSEMBLY = 2, swDocDRAWING = 3 }
    public enum swUserPreferenceIntegerValue_e { swStepAP, swStepExportPreference, swExportStlUnits, swSTLQuality }
    public enum swUserPreferenceToggle_e { swStepExportAtomicSave, swSTLBinaryFormat, swSTLDontTranslateToPositive, swSTLComponentsIntoOneFile, swSTLShowInfoOnSave, swSTLPreview, swSTLCheckForInterference }
    public enum swUserPreferenceStringValue_e { swExportOutputCoordinateSystem }
    public enum swAcisOutputGeometryPreference_e { swAcisOutputAsSolidAndSurface }
    public enum swLengthUnit_e { swMETER = 2 }
    public enum swSTLQuality_e { swSTLQuality_Fine = 2 }
    public enum swSaveAsVersion_e { swSaveAsCurrentVersion = 0 }
    public enum swSaveAsOptions_e { swSaveAsOptions_Silent = 1 }
    public enum swPackAndGoSaveStatus_e { swPackAndGoSaveStatus_Succeed = 0 }
    public enum swExportDataFileType_e { swExportPdfData = 1 }
    public enum swExportDataSheetsToExport_e { swExportData_ExportAllSheets = 1 }
    public enum swOpenDocOptions_e { swOpenDocOptions_Silent = 1 }
    public enum swSearchFolderTypes_e { swDocumentType = 0 }
    public enum swRebuildOnActivation_e { swDontRebuildActiveDoc = 1 }
    public enum swCreateCommandGroupErrors { swCreateCommandGroup_Failed = 0, swCreateCommandGroup_Success = 1, swCreateCommandGroup_Exceeds_ToolBarIDs = 2 }
    [Flags] public enum swCommandItemType_e { swMenuItem = 1, swToolbarItem = 2 }
}
