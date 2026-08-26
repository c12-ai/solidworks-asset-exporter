Attribute VB_Name = "PreviewCustomProperties"
Option Explicit

Dim swApp As Object

Sub main()
    Dim root As Object
    Dim schemaPath As String
    Dim allFields As Object, partFields As Object, assemblyFields As Object
    Dim documents As Object
    Dim reportPath As String
    Dim html As String
    Dim changedCount As Long, unchangedCount As Long, skippedCount As Long

    Set swApp = Application.SldWorks
    Set root = swApp.ActiveDoc
    If root Is Nothing Then
        MsgBox "Open a part or assembly in SOLIDWORKS first.", vbExclamation, "Custom Property Preview"
        Exit Sub
    End If

    schemaPath = InputBox("Enter the full path of the property schema CSV:", "Custom Property Preview", _
        "C:\Users\C12\Desktop\solidworks-asset-exporter\artifacts\CustomPropertyPreview\custom-properties.schema.csv")
    If Len(Trim$(schemaPath)) = 0 Then Exit Sub
    If Dir$(schemaPath) = "" Then
        MsgBox "Schema file not found: " & schemaPath, vbCritical, "Custom Property Preview"
        Exit Sub
    End If

    Set allFields = CreateDictionary()
    Set partFields = CreateDictionary()
    Set assemblyFields = CreateDictionary()
    If Not LoadSchema(schemaPath, allFields, partFields, assemblyFields) Then Exit Sub

    Set documents = CreateDictionary()
    CollectDocuments root, documents, skippedCount

    html = "<!doctype html><html><head><meta charset=""utf-8""><title>SOLIDWORKS Custom Property Preview</title>" & _
        "<style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:24px;color:#222}" & _
        "h1{font-size:22px}.file{margin:14px 0;padding:12px;border:1px solid #ddd;border-radius:6px}" & _
        ".change{border-left:5px solid #e67e22}.same{border-left:5px solid #27ae60}" & _
        ".path{font-family:Consolas,monospace;word-break:break-all}.add{color:#167c36}.remove{color:#b42318}" & _
        "ul{margin:7px 0}</style></head><body><h1>SOLIDWORKS Custom Property Sync Preview</h1>" & _
        "<p><b>Read-only preview:</b> no SLDPRT/SLDASM file was modified or saved.</p>" & _
        "<p>Schema: <span class=""path"">" & HtmlEncode(schemaPath) & "</span></p>"

    AppendDocumentPreview documents, allFields, partFields, assemblyFields, html, changedCount, unchangedCount, skippedCount

    html = html & "<hr><p>Changed: " & CStr(changedCount) & "; unchanged: " & CStr(unchangedCount) & _
        "; skipped: " & CStr(skippedCount) & ".</p></body></html>"
    reportPath = Environ$("TEMP") & "\SolidWorksCustomPropertyPreview.html"
    WriteUtf8 reportPath, html
    CreateObject("Shell.Application").ShellExecute reportPath
    MsgBox "Preview complete. No model was modified or saved." & vbCrLf & vbCrLf & _
        "Changed: " & changedCount & vbCrLf & "Unchanged: " & unchangedCount & vbCrLf & _
        "Skipped: " & skippedCount & vbCrLf & vbCrLf & "Report: " & reportPath, _
        vbInformation, "Custom Property Preview"
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
    MsgBox "Failed to read schema: " & Err.Description, vbCritical, "Custom Property Preview"
    LoadSchema = False
End Function

Sub CollectDocuments(ByVal root As Object, ByRef documents As Object, ByRef skippedCount As Long)
    Dim path As String, key As String
    Dim components As Variant
    Dim component As Object, document As Object
    Dim i As Long

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
            skippedCount = skippedCount + 1
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

Sub AppendDocumentPreview(ByVal documents As Object, ByVal allFields As Object, ByVal partFields As Object, _
    ByVal assemblyFields As Object, ByRef html As String, ByRef changedCount As Long, _
    ByRef unchangedCount As Long, ByRef skippedCount As Long)
    Dim key As Variant
    Dim document As Object, required As Object, existing As Object, manager As Object
    Dim names As Variant, name As Variant, field As Variant
    Dim additions As String, removals As String, kind As String
    Dim documentType As Long

    For Each key In documents.Keys
        Set document = documents.Item(key)
        documentType = CLng(document.GetType)
        If documentType = 1 Then
            kind = "Part"
        ElseIf documentType = 2 Then
            kind = "Assembly"
        Else
            skippedCount = skippedCount + 1
            GoTo NextDocument
        End If

        Set required = CreateDictionary()
        CopyFields allFields, required
        If kind = "Part" Then CopyFields partFields, required Else CopyFields assemblyFields, required
        Set existing = CreateDictionary()
        Set manager = document.Extension.CustomPropertyManager("")
        names = manager.GetNames
        If Not IsEmpty(names) Then
            For Each name In names
                existing(CStr(name)) = True
                If Not required.Exists(CStr(name)) Then
                    removals = removals & "<li class=""remove"">- " & HtmlEncode(CStr(name)) & "</li>"
                End If
            Next name
        End If
        For Each field In required.Keys
            If Not existing.Exists(CStr(field)) Then
                additions = additions & "<li class=""add"">+ " & HtmlEncode(CStr(field)) & " = " & _
                    HtmlEncode(CStr(required.Item(field))) & "</li>"
            End If
        Next field

        If Len(additions) > 0 Or Len(removals) > 0 Then
            changedCount = changedCount + 1
            html = html & "<div class=""file change""><b>CHANGE [" & kind & "]</b><div class=""path"">" & _
                HtmlEncode(CStr(key)) & "</div><ul>" & removals & additions & "</ul></div>"
        Else
            unchangedCount = unchangedCount + 1
            html = html & "<div class=""file same""><b>UNCHANGED [" & kind & "]</b><div class=""path"">" & _
                HtmlEncode(CStr(key)) & "</div></div>"
        End If
        additions = ""
        removals = ""
NextDocument:
    Next key
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

Function HtmlEncode(ByVal value As String) As String
    value = Replace(value, "&", "&amp;")
    value = Replace(value, "<", "&lt;")
    value = Replace(value, ">", "&gt;")
    value = Replace(value, Chr$(34), "&quot;")
    HtmlEncode = value
End Function

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
