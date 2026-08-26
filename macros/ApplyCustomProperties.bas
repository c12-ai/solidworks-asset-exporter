Attribute VB_Name = "ApplyCustomProperties"
Option Explicit

Dim swApp As Object

Sub main()
    Dim root As Object
    Dim schemaPath As String, backupRoot As String, reportPath As String
    Dim allFields As Object, partFields As Object, assemblyFields As Object
    Dim documents As Object
    Dim skippedCount As Long, changedCount As Long, savedCount As Long
    Dim answer As VbMsgBoxResult

    Set swApp = Application.SldWorks
    Set root = swApp.ActiveDoc
    If root Is Nothing Then
        MsgBox "Open a part or assembly in SOLIDWORKS first.", vbExclamation, "Apply Custom Properties"
        Exit Sub
    End If

    schemaPath = InputBox("Enter the full path of the property schema CSV:", "Apply Custom Properties", _
        "C:\Users\C12\Desktop\solidworks-asset-exporter\artifacts\CustomPropertyPreview\custom-properties.schema.csv")
    If Len(Trim$(schemaPath)) = 0 Then Exit Sub
    If Dir$(schemaPath) = "" Then
        MsgBox "Schema file not found: " & schemaPath, vbCritical, "Apply Custom Properties"
        Exit Sub
    End If

    Set allFields = CreateDictionary()
    Set partFields = CreateDictionary()
    Set assemblyFields = CreateDictionary()
    If Not LoadSchema(schemaPath, allFields, partFields, assemblyFields) Then Exit Sub

    Set documents = CreateDictionary()
    CollectDocuments root, documents, skippedCount
    If skippedCount > 0 Then
        MsgBox CStr(skippedCount) & " component(s) are unresolved or unsaved." & vbCrLf & _
            "Resolve all components and run the preview again. No file was changed.", _
            vbCritical, "Apply Custom Properties"
        Exit Sub
    End If
    If Not ValidateDocuments(documents) Then Exit Sub

    changedCount = CountChangedDocuments(documents, allFields, partFields, assemblyFields)
    If changedCount = 0 Then
        MsgBox "All file-level custom-property fields already match the schema. No file was changed.", _
            vbInformation, "Apply Custom Properties"
        Exit Sub
    End If

    answer = MsgBox("This operation will update and save " & CStr(changedCount) & " document(s)." & vbCrLf & _
        "Existing values of fields with the same name will be preserved." & vbCrLf & _
        "Fields not listed in the CSV will be deleted." & vbCrLf & vbCrLf & _
        "A complete source-file backup will be created before any change." & vbCrLf & _
        "Continue?", vbYesNo + vbExclamation + vbDefaultButton2, "Apply Custom Properties")
    If answer <> vbYes Then Exit Sub

    backupRoot = Environ$("USERPROFILE") & "\Desktop\SolidWorksCustomPropertyBackup_" & Format$(Now, "yyyymmdd_hhnnss")
    If Not BackupDocuments(documents, backupRoot, schemaPath) Then Exit Sub

    On Error GoTo ApplyFailed
    ApplyToDocuments documents, allFields, partFields, assemblyFields, savedCount
    reportPath = backupRoot & "\SUCCESS.txt"
    WriteUtf8 reportPath, "Custom-property sync completed successfully." & vbCrLf & _
        "Schema: " & schemaPath & vbCrLf & "Documents saved: " & CStr(savedCount) & vbCrLf & _
        "Backup: " & backupRoot & vbCrLf
    CreateObject("Shell.Application").ShellExecute backupRoot
    MsgBox "Custom-property sync completed." & vbCrLf & vbCrLf & _
        "Documents saved: " & CStr(savedCount) & vbCrLf & "Backup: " & backupRoot, _
        vbInformation, "Apply Custom Properties"
    Exit Sub

ApplyFailed:
    reportPath = backupRoot & "\FAILED.txt"
    On Error Resume Next
    WriteUtf8 reportPath, "Custom-property sync failed after saving " & CStr(savedCount) & " document(s)." & vbCrLf & _
        "Error: " & Err.Description & vbCrLf & "Restore affected files from manifest.csv if required." & vbCrLf
    CreateObject("Shell.Application").ShellExecute backupRoot
    MsgBox "The operation failed after saving " & CStr(savedCount) & " document(s)." & vbCrLf & _
        "Error: " & Err.Description & vbCrLf & vbCrLf & "Backups: " & backupRoot, _
        vbCritical, "Apply Custom Properties"
End Sub

Function CreateDictionary() As Object
    Dim value As Object
    Set value = CreateObject("Scripting.Dictionary")
    value.CompareMode = 1
    Set CreateDictionary = value
End Function

Function LoadSchema(ByVal path As String, ByRef allFields As Object, ByRef partFields As Object, ByRef assemblyFields As Object) As Boolean
    Dim stream As Object
    Dim text As String
    Dim lines As Variant, columns As Variant
    Dim i As Long
    Dim scope As String, fieldName As String, defaultValue As String
    Dim target As Object

    On Error GoTo Failed
    Set stream = CreateObject("ADODB.Stream")
    stream.Type = 2
    stream.Charset = "utf-8"
    stream.Open
    stream.LoadFromFile path
    text = stream.ReadText
    stream.Close
    text = Replace(text, vbCrLf, vbLf)
    text = Replace(text, vbCr, vbLf)
    lines = Split(text, vbLf)

    For i = 1 To UBound(lines)
        If Len(Trim$(CStr(lines(i)))) > 0 Then
            columns = Split(CStr(lines(i)), ",")
            If UBound(columns) < 1 Then Err.Raise vbObjectError + 101, , "Invalid CSV format on line " & (i + 1) & "."
            scope = Trim$(CStr(columns(0)))
            fieldName = Trim$(CStr(columns(1)))
            defaultValue = ""
            If UBound(columns) >= 2 Then defaultValue = CStr(columns(2))
            If Len(fieldName) = 0 Then Err.Raise vbObjectError + 102, , "Empty property name on line " & (i + 1) & "."
            If StrComp(scope, "All", vbTextCompare) = 0 Then
                Set target = allFields
            ElseIf StrComp(scope, "Part", vbTextCompare) = 0 Then
                Set target = partFields
            ElseIf StrComp(scope, "Assembly", vbTextCompare) = 0 Then
                Set target = assemblyFields
            Else
                Err.Raise vbObjectError + 103, , "DocumentType on line " & (i + 1) & " must be All, Part, or Assembly."
            End If
            If target.Exists(fieldName) Then Err.Raise vbObjectError + 104, , "Duplicate field: " & fieldName
            target.Add fieldName, defaultValue
        End If
    Next i
    LoadSchema = True
    Exit Function
Failed:
    On Error Resume Next
    If Not stream Is Nothing Then stream.Close
    MsgBox "Failed to read schema: " & Err.Description, vbCritical, "Apply Custom Properties"
    LoadSchema = False
End Function

Sub CollectDocuments(ByVal root As Object, ByRef documents As Object, ByRef skippedCount As Long)
    Dim path As String, key As String
    Dim components As Variant
    Dim component As Object, document As Object
    Dim i As Long, suppression As Long

    path = CStr(root.GetPathName)
    key = path
    If Len(key) = 0 Then key = "<unsaved-root>" & CStr(root.GetTitle)
    documents.Add key, root
    If CLng(root.GetType) <> 2 Then Exit Sub

    components = root.GetComponents(False)
    If IsEmpty(components) Then Exit Sub
    For i = 0 To UBound(components)
        Set component = components(i)
        Set document = Nothing
        On Error Resume Next
        Set document = component.GetModelDoc2
        On Error GoTo 0
        If document Is Nothing Then
            suppression = -1
            On Error Resume Next
            suppression = CLng(component.GetSuppression2)
            On Error GoTo 0
            If suppression <> 0 Then skippedCount = skippedCount + 1
        Else
            path = CStr(document.GetPathName)
            If Len(path) = 0 Then
                skippedCount = skippedCount + 1
            ElseIf Not documents.Exists(path) Then
                documents.Add path, document
            End If
        End If
    Next i
End Sub

Function ValidateDocuments(ByVal documents As Object) As Boolean
    Dim key As Variant
    Dim document As Object
    Dim path As String, problems As String

    For Each key In documents.Keys
        Set document = documents.Item(key)
        path = CStr(document.GetPathName)
        If Len(path) = 0 Or Dir$(path) = "" Then
            problems = problems & vbCrLf & "Missing or unsaved: " & CStr(key)
        ElseIf CBool(document.GetSaveFlag) Then
            problems = problems & vbCrLf & "Unsaved changes: " & path
        ElseIf (GetAttr(path) And vbReadOnly) <> 0 Then
            problems = problems & vbCrLf & "Read-only: " & path
        End If
    Next key
    If Len(problems) > 0 Then
        MsgBox "Preflight failed. No file was changed:" & problems, vbCritical, "Apply Custom Properties"
        ValidateDocuments = False
    Else
        ValidateDocuments = True
    End If
End Function

Function RequiredFields(ByVal documentType As Long, ByVal allFields As Object, ByVal partFields As Object, ByVal assemblyFields As Object) As Object
    Dim result As Object
    Set result = CreateDictionary()
    CopyFields allFields, result
    If documentType = 1 Then
        CopyFields partFields, result
    ElseIf documentType = 2 Then
        CopyFields assemblyFields, result
    End If
    Set RequiredFields = result
End Function

Function ExistingFields(ByVal manager As Object) As Object
    Dim result As Object
    Dim names As Variant, name As Variant
    Set result = CreateDictionary()
    names = manager.GetNames
    If Not IsEmpty(names) Then
        For Each name In names
            result(CStr(name)) = True
        Next name
    End If
    Set ExistingFields = result
End Function

Function DocumentNeedsChange(ByVal document As Object, ByVal allFields As Object, ByVal partFields As Object, ByVal assemblyFields As Object) As Boolean
    Dim required As Object, existing As Object, manager As Object
    Dim key As Variant
    Set required = RequiredFields(CLng(document.GetType), allFields, partFields, assemblyFields)
    Set manager = document.Extension.CustomPropertyManager("")
    Set existing = ExistingFields(manager)
    For Each key In existing.Keys
        If Not required.Exists(CStr(key)) Then DocumentNeedsChange = True: Exit Function
    Next key
    For Each key In required.Keys
        If Not existing.Exists(CStr(key)) Then DocumentNeedsChange = True: Exit Function
    Next key
End Function

Function CountChangedDocuments(ByVal documents As Object, ByVal allFields As Object, ByVal partFields As Object, ByVal assemblyFields As Object) As Long
    Dim key As Variant
    Dim document As Object
    For Each key In documents.Keys
        Set document = documents.Item(key)
        If DocumentNeedsChange(document, allFields, partFields, assemblyFields) Then
            CountChangedDocuments = CountChangedDocuments + 1
        End If
    Next key
End Function

Function BackupDocuments(ByVal documents As Object, ByVal backupRoot As String, ByVal schemaPath As String) As Boolean
    Dim fso As Object
    Dim key As Variant
    Dim path As String, folder As String, destination As String
    Dim failureDetail As String, currentSource As String
    Dim index As Long
    Dim manifest As String

    On Error GoTo Failed
    Set fso = CreateObject("Scripting.FileSystemObject")
    fso.CreateFolder backupRoot
    manifest = "Index,OriginalPath,BackupPath" & vbCrLf
    For Each key In documents.Keys
        index = index + 1
        path = CStr(documents.Item(key).GetPathName)
        folder = backupRoot & "\" & Format$(index, "0000")
        fso.CreateFolder folder
        destination = folder & "\" & fso.GetFileName(path)
        currentSource = path
        failureDetail = ""
        If Not BackupOpenDocument(documents.Item(key), path, destination, fso, failureDetail) Then
            Err.Raise vbObjectError + 150, , failureDetail
        End If
        manifest = manifest & CStr(index) & ",""" & path & """,""" & destination & """" & vbCrLf
    Next key
    currentSource = schemaPath
    fso.CopyFile schemaPath, backupRoot & "\custom-properties.schema.csv", True
    WriteUtf8 backupRoot & "\manifest.csv", manifest
    BackupDocuments = True
    Exit Function
Failed:
    MsgBox "Backup failed. No model was changed." & vbCrLf & Err.Description & vbCrLf & _
        "Source: " & currentSource & vbCrLf & _
        "Backup folder: " & backupRoot, vbCritical, "Apply Custom Properties"
    BackupDocuments = False
End Function

Function BackupOpenDocument(ByVal document As Object, ByVal sourcePath As String, ByVal destination As String, _
    ByVal fso As Object, ByRef failureDetail As String) As Boolean
    Dim copyError As String
    Dim saveErrors As Long, saveWarnings As Long
    Dim saved As Boolean

    ' FileSystemObject can copy many open SOLIDWORKS files directly. If Windows
    ' denies the read because the document is open, SaveAs with the Copy option
    ' creates a backup without changing the document's active path.
    On Error Resume Next
    Err.Clear
    fso.CopyFile sourcePath, destination, False
    If Err.Number = 0 And fso.FileExists(destination) Then
        On Error GoTo 0
        BackupOpenDocument = True
        Exit Function
    End If
    copyError = Err.Description
    Err.Clear
    If fso.FileExists(destination) Then fso.DeleteFile destination, True
    On Error GoTo Failed

    saved = CBool(document.Extension.SaveAs(destination, 0, 2, Nothing, saveErrors, saveWarnings))
    If Not saved Or saveErrors <> 0 Or Not fso.FileExists(destination) Then
        failureDetail = "Could not create backup copy." & vbCrLf & _
            "Direct copy: " & copyError & vbCrLf & _
            "SOLIDWORKS SaveAs Copy errors=" & CStr(saveErrors) & "; warnings=" & CStr(saveWarnings)
        BackupOpenDocument = False
        Exit Function
    End If

    BackupOpenDocument = True
    Exit Function
Failed:
    failureDetail = "Could not create backup copy." & vbCrLf & _
        "Direct copy: " & copyError & vbCrLf & _
        "SOLIDWORKS SaveAs Copy: " & Err.Description
    BackupOpenDocument = False
End Function

Sub ApplyToDocuments(ByVal documents As Object, ByVal allFields As Object, ByVal partFields As Object, _
    ByVal assemblyFields As Object, ByRef savedCount As Long)
    Dim key As Variant
    Dim document As Object
    For Each key In documents.Keys
        Set document = documents.Item(key)
        If DocumentNeedsChange(document, allFields, partFields, assemblyFields) Then
            SyncDocument document, allFields, partFields, assemblyFields
            savedCount = savedCount + 1
        End If
    Next key
End Sub

Sub SyncDocument(ByVal document As Object, ByVal allFields As Object, ByVal partFields As Object, ByVal assemblyFields As Object)
    Dim required As Object, existing As Object, manager As Object
    Dim key As Variant
    Dim status As Long, errors As Long, warnings As Long
    Dim saved As Boolean, path As String

    path = CStr(document.GetPathName)
    Set required = RequiredFields(CLng(document.GetType), allFields, partFields, assemblyFields)
    Set manager = document.Extension.CustomPropertyManager("")
    Set existing = ExistingFields(manager)

    For Each key In required.Keys
        If Not existing.Exists(CStr(key)) Then
            status = CLng(manager.Add3(CStr(key), 30, CStr(required.Item(key)), 0))
            If status <> 0 Then Err.Raise vbObjectError + 201, , "Add3 failed for [" & CStr(key) & "] in " & path & "; status=" & status
        End If
    Next key
    For Each key In existing.Keys
        If Not required.Exists(CStr(key)) Then
            status = CLng(manager.Delete2(CStr(key)))
            If status <> 0 Then Err.Raise vbObjectError + 202, , "Delete2 failed for [" & CStr(key) & "] in " & path & "; status=" & status
        End If
    Next key

    saved = CBool(document.Save3(1, errors, warnings))
    If Not saved Or errors <> 0 Then
        Err.Raise vbObjectError + 203, , "Save3 failed for " & path & "; errors=" & errors & "; warnings=" & warnings
    End If
End Sub

Sub CopyFields(ByVal source As Object, ByRef destination As Object)
    Dim key As Variant
    For Each key In source.Keys
        If destination.Exists(CStr(key)) Then
            destination(CStr(key)) = source.Item(key)
        Else
            destination.Add CStr(key), source.Item(key)
        End If
    Next key
End Sub

Sub WriteUtf8(ByVal path As String, ByVal content As String)
    Dim stream As Object
    Set stream = CreateObject("ADODB.Stream")
    stream.Type = 2
    stream.Charset = "utf-8"
    stream.Open
    stream.WriteText content
    stream.SaveToFile path, 2
    stream.Close
End Sub
