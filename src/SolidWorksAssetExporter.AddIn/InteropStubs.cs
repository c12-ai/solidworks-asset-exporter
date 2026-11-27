// Compile-only contract stubs used by scripts/build-core.ps1 on machines without SOLIDWORKS.
// Production builds exclude this file and reference the official SOLIDWORKS Interop assemblies.
using System;

namespace SolidWorks.Interop.swpublished
{
    public interface ISwAddin { bool ConnectToSW(object thisSw, int cookie); bool DisconnectFromSW(); }
}

namespace SolidWorks.Interop.sldworks
{
    public interface DAssemblyDocEvents_Event
    {
        event Func<int> NewSelectionNotify;
        event Func<int> ClearSelectionsNotify;
        event Func<int> UserSelectionPostNotify;
        event Func<int> SaveToStorageNotify;
        event Func<int, string, int> FileSavePostNotify;
        event Func<int> FileSavePostCancelNotify;
        event Func<int, int> DestroyNotify2;
    }
    public class SldWorks
    {
        public virtual object ActiveDoc { get; set; }
        public bool SetAddinCallbackInfo2(int reserved, object addin, int cookie) { return true; }
        public CommandManager GetCommandManager(int cookie) { return null; }
        public Frame IFrameObject() { return null; }
        public int GetUserPreferenceIntegerValue(int value) { return 0; }
        public bool SetUserPreferenceIntegerValue(int value, int setting) { return true; }
        public bool GetUserPreferenceToggle(int value) { return false; }
        public bool SetUserPreferenceToggle(int value, bool setting) { return true; }
        public string GetUserPreferenceStringValue(int value) { return string.Empty; }
        public bool SetUserPreferenceStringValue(int value, string setting) { return true; }
        public object GetExportFileData(int type) { return null; }
        public virtual object GetModeler() { return null; }
        public virtual object GetOpenDocumentByName(string path) { return null; }
        public string GetSearchFolders(int type) { return string.Empty; }
        public bool RunCommand(int commandId, string newTitle) { return true; }
        public virtual object OpenDoc6(string path, int type, int options, string configuration, ref int errors, ref int warnings) { return null; }
        public virtual bool GetDocumentVisible(int type) { return true; }
        public virtual void DocumentVisible(bool visible, int type) { }
        public virtual object ActivateDoc3(string title, bool usePreferences, int option, ref int errors) { return null; }
        public virtual void CloseDoc(string title) { }
        public virtual void QuitDoc(string title) { }
    }

    public interface Component2
    {
        string Name2 { get; }
        string GetSelectByIDString();
        int GetID(); int Visible { get; }
        object GetParent();
        bool IsRoot();
        bool IsSuppressed(); int GetSuppression2(); int SetSuppression2(int state); bool IsEnvelope(); bool IsFixed(); bool IsHidden(bool considerSuppressed); bool Select4(bool append, object data, bool showPopup);
        object GetModelDoc2(); object GetChildren(); string GetPathName();
        object GetBox(bool includeRefPlanes, bool includeSketches);
        object GetBodies3(int type, out object bodyInfo);
        object GetMates();
        bool IsVirtual { get; }
        int Solving { get; }
        string ReferencedConfiguration { get; set; }
        string ReferencedDisplayState { get; }
        MathTransform Transform2 { get; set; }
    }
    public interface Frame { long GetHWndx64(); }

    public interface MathTransform { object ArrayData { get; } }

    public interface Body2
    {
        bool Visible { get; }
        object Copy();
        bool ApplyTransform(MathTransform transform);
        object GetMassProperties(double density);
        object Operations2(int operation, object tool, out int error);
        bool GetExtremePoint(double x, double y, double z, out double pointX, out double pointY, out double pointZ);
    }

    public interface Modeler
    {
        bool CheckInterferenceBetweenTwoBodies(object first, object second, bool coincident, out object firstFaces,
            out object secondFaces, out object intersection);
    }

    public interface Mate2
    {
        int Type { get; }
        int GetMateEntityCount();
        MateEntity2 MateEntity(int index);
    }

    public interface MateEntity2
    {
        Component2 ReferenceComponent { get; }
    }

    public interface ModelView
    {
        event Func<int> BufferSwapNotify;
        event Func<int> DestroyNotify;
        void GraphicsRedraw(object rect);
    }

    public interface ModelDoc2
    {
        int GetType(); string GetPathName(); string GetTitle(); bool GetSaveFlag(); int GetUpdateStamp(); string get_SummaryInfo(int fieldId);
        void SetSaveFlag(); object IGet3rdPartyStorage(string name, bool storing); void IRelease3rdPartyStorage(string name);
        bool IsOpenedReadOnly();
        bool Save3(int options, ref int errors, ref int warnings);
        bool Visible { get; set; }
        ConfigurationManager ConfigurationManager { get; }
        FeatureManager FeatureManager { get; }
        ModelDocExtension Extension { get; }
        object SelectionManager { get; }
        ModelView IActiveView { get; }
        void ClearSelection2(bool all); bool ShowConfiguration2(string name); void GraphicsRedraw2();
        object GetConfigurationNames(); object GetConfigurationByName(string name); bool EditRebuild3(); bool IsEditingSelf();
        double ClosestDistance(object first, object second, out object point1, out object point2);
    }

    public interface Entity
    {
        object GetComponent();
    }
    public interface Feature
    {
        string GetTypeName2();
        object IsSuppressed2(int configuration, object names);
    }

    public interface FeatureManager
    {
        TreeControlItem GetFeatureTreeRootItem2(int pane);
    }

    public interface TreeControlItem
    {
        string Text { get; }
        int ObjectType { get; }
        object Object { get; }
        TreeControlItem GetFirstChild();
        TreeControlItem GetNext();
    }

    public interface DrawingDoc : ModelDoc2
    {
        object GetViews(); object GetSheetNames(); object GetCurrentSheet(); bool ActivateSheet(string name);
    }
    public interface Sheet { string GetName(); }
    public interface AssemblyDoc : ModelDoc2
    {
        int GetLightWeightComponentCount(); int ResolveAllLightWeightComponents(bool warnUser);
        object GetComponents(bool topLevelOnly);
        Component2 AddComponent5(string path, int configOption, string newConfigName, bool useExistingConfig,
            string existingConfigName, double x, double y, double z);
        bool SetComponentState(int suppression, object components, int configurationOption, string configuration, bool saveClosedDocuments);
        bool Isolate(); bool ExitIsolate(); void SetIsolateVisibility(int option);
    }
    public interface View
    {
        ModelDoc2 ReferencedDocument { get; }
        string GetReferencedModelName(); object GetBaseView();
    }
    public interface ConfigurationManager { Configuration ActiveConfiguration { get; } }
    public interface Configuration
    {
        string Name { get; } Component2 GetRootComponent3(bool resolve); object GetDisplayStates();
        int GetID(); bool GetComponentSuppressionState(string componentName);
    }
    public interface ModelDocExtension
    {
        bool SelectByID2(string name, string type, double x, double y, double z, bool append, int mark, object callout, int option);
        int MultiSelect2(object objects, bool append, object data);
        object GetPersistReference3(object value); object GetObjectByPersistReference3(object bytes, out int error);
        bool DeleteSelection2(int options);
        PackAndGo GetPackAndGo(); object SavePackAndGo(PackAndGo value);
        bool SaveAs(string name, int version, int options, object exportData, ref int errors, ref int warnings);
        bool SaveAs3(string name, int version, int options, object exportData, object advancedSaveAsOptions,
            ref int errors, ref int warnings);
    }
    public interface CustomPropertyManager
    {
        int Add3(string name, int type, string value, int options); int Delete2(string name); int GetType2(string name);
        object GetNames(); int Get6(string name, bool cached, out string raw, out string resolved, out bool wasResolved, out bool linked);
    }
    public interface SelectionMgr
    {
        int GetSelectedObjectCount2(int mark); object GetSelectedObject6(int index, int mark);
        object GetSelectedObjectsComponent4(int index, int mark);
        int GetSelectedObjectMark(int index); SelectData CreateSelectData();
        int DeSelect2(object indices, int mark);
        void set_SelectionColor(int mark, int color);
    }
    public interface SelectData { int Mark { get; set; } }
    public interface ExportPdfData { bool SetSheets(int mode, object sheets); bool ViewPdfAfterSaving { get; set; } }
    public interface PackAndGo
    {
        bool IncludeDrawings { get; set; } bool IncludeSuppressed { get; set; }
        bool IncludeToolboxComponents { get; set; } bool IncludeSimulationResults { get; set; }
        bool FlattenToSingleFolder { get; set; }
          bool GetDocumentNames(out object names); bool GetDocumentSaveToNames(out object names, out object statuses);
          bool SetDocumentSaveToNames(object names); bool SetSaveToName(bool value, string path);
          bool SetSaveToName2(bool value, string path);
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
    public enum swFeatMgrPane_e { swFeatMgrPaneTop = 0, swFeatMgrPaneBottom = 1 }
    public enum swTreeControlItemType_e { swFeatureManagerItem_Feature = 1, swFeatureManagerItem_Component = 2 }
    public enum swComponentVisibilityState_e { swComponentHidden = 0, swComponentVisible = 1, swComponentUnknown = -1 }
    public enum swIsolateVisibility_e { swIsolateVisibility_HIDDEN = 2 }
    public enum swComponentSuppressionState_e { swComponentSuppressed = 0, swComponentLightweight = 1, swComponentFullyResolved = 2, swComponentResolved = 3, swComponentFullyLightweight = 4, swComponentInternalIdMismatch = 5 }
    public enum swComponentSolvingOption_e { swComponentRigidSolving = 0, swComponentFlexibleSolving = 1 }
    public enum swBodyType_e { swSolidBody = 0 }
    public enum swBodyInfo_e { swUserBody_e = 0, swNormalBody_e = 1 }
    public enum swBodyOperationType_e { SWBODYINTERSECT = 15901 }
    public enum swBodyOperationError_e
    { swBodyOperationNoError = 0, swBodyOperationDisjointBodies = 5, swBodyOperationNoIntersect = 1067, swBodyOperationUnknownError = -1 }
    public enum swComponentResolveStatus_e { swResolveOk = 0, swResolveAbortedByUser = 1, swResolveNotPerformed = 2, swResolveError = 3 }
    public enum swCustomInfoGetResult_e { swCustomInfoGetResult_CachedValue = 0, swCustomInfoGetResult_NotPresent = 1, swCustomInfoGetResult_ResolvedValue = 2 }
    public enum swSummInfoField_e { swSumInfoCreateDate = 6 }
    public enum swDocumentTypes_e { swDocPART = 1, swDocASSEMBLY = 2, swDocDRAWING = 3 }
    public enum swInConfigurationOpts_e { swThisConfiguration = 1 }
    public enum swUserPreferenceIntegerValue_e { swStepAP, swStepExportPreference, swExportStlUnits, swSTLQuality,
        swSystemColorsSelectedItem1, swSystemColorsSelectedItem2, swSystemColorsSelectedItem3,
        swSystemColorsSelectedItem4, swSystemColorsSelectedItem5, swSystemColorsSelectedItem6 }
    public enum swUserPreferenceToggle_e { swStepExportAtomicSave, swSTLBinaryFormat, swSTLDontTranslateToPositive, swSTLComponentsIntoOneFile, swSTLShowInfoOnSave, swSTLPreview, swSTLCheckForInterference }
    public enum swUserPreferenceStringValue_e { swExportOutputCoordinateSystem }
    public enum swAcisOutputGeometryPreference_e { swAcisOutputAsSolidAndSurface }
    public enum swLengthUnit_e { swMETER = 2 }
    public enum swSTLQuality_e { swSTLQuality_Fine = 2 }
    public enum swSaveAsVersion_e { swSaveAsCurrentVersion = 0 }
    public enum swSaveAsOptions_e { swSaveAsOptions_Silent = 1 }
    public enum swPackAndGoSaveStatus_e { swPackAndGoSaveStatus_Succeed = 0 }
    public enum swExportDataFileType_e { swExportPdfData = 1 }
    public enum swExportDataSheetsToExport_e { swExportData_ExportAllSheets = 1, swExportData_ExportSpecifiedSheets = 3 }
    [Flags] public enum swOpenDocOptions_e
    {
        swOpenDocOptions_Silent = 1, swOpenDocOptions_ReadOnly = 2,
        swOpenDocOptions_OverrideDefaultLoadLightweight = 64, swOpenDocOptions_LoadLightweight = 128,
        swOpenDocOptions_DontLoadHiddenComponents = 256, swOpenDocOptions_LoadExternalReferencesInMemory = 512
    }
    public enum swSearchFolderTypes_e { swDocumentType = 0 }
    public enum swRebuildOnActivation_e { swDontRebuildActiveDoc = 1 }
    [Flags] public enum swActivateDocError_e { swGenericActivateError = 1, swDocNeedsRebuildWarning = 2 }
    public enum swCreateCommandGroupErrors { swCreateCommandGroup_Failed = 0, swCreateCommandGroup_Success = 1, swCreateCommandGroup_Exceeds_ToolBarIDs = 2 }
    [Flags] public enum swCommandItemType_e { swMenuItem = 1, swToolbarItem = 2 }
}
