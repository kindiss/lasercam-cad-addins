Attribute VB_Name = "ExportToOdoo"
' ============================================================================
'  LaserCAM - "Export to Odoo" (SolidWorks VBA macro, pilot version)
'
'  One click on the active sheet metal part:
'    1. Exports the flat pattern DXF to a temp file (same as Export DXF).
'    2. Reads thickness (sheet metal feature), material (part material) and the Odoo
'       number (custom property "Number" - see NUMBER_PROPERTY).
'    3. LaserCAM server /api/quote computes from YOUR account database: net/gross kg,
'       cutting time, price, parts per sheet.
'    4. Odoo: finds the product by Internal Reference = Number (creates it if missing),
'       writes weight (net kg), attaches the DXF, writes a quote summary into the Studio
'       field x_studio_lasercam_quote (skipped silently if the field does not exist).
'    5. Shows the result and writes it into the part custom properties.
'
'  Install: SolidWorks -> Tools -> Macro -> New (.swp) -> in the VBA editor: File -> Import File ->
'  this .bas; remove the default Module1; Tools -> References -> check "Microsoft XML, v6.0".
'  Fill in the SETTINGS below. Run: Tools -> Macro -> Run -> the .swp -> ExportToOdoo.Main.
'
'  BOM line (gross kg) and "Laser" operation (time) in Odoo are the next step on the Odoo
'  connector side; this macro writes only standard product fields + attachment + summary.
' ============================================================================
Option Explicit

Private Declare PtrSafe Sub Sleep Lib "kernel32" (ByVal ms As Long)

' ---------------- SETTINGS (fill in) ----------------
Const LC_API As String = "https://laser.ucase.eu/api"
Const LC_PID As String = "0"                 ' Odoo contact id (res.partner) that holds the LaserCAM plan
Const LC_KEY As String = ""                  ' LaserCAM key from that contact (x_studio_lasercam_key)
Const ODOO_URL As String = ""                ' your Odoo address, e.g. https://mycompany.odoo.com
Const ODOO_API_KEY As String = ""            ' Odoo user API key (My Profile -> Account Security -> New API Key)
Const ODOO_DB As String = ""                 ' empty = default database (Odoo Online)
Const NUMBER_PROPERTY As String = "Number"   ' custom property holding the Odoo number
Const TEMPLATE_NUMBER As String = ""          ' Odoo product (Internal Reference) whose BOM sheet line is used when the part's product has no BOM yet
Const USE_ODOO_SHEET As Boolean = True       ' True: material / thickness / sheet size come from the Odoo BOM sheet line, SolidWorks names are ignored
Const DEFAULT_GAS As String = "O2"           ' O2 / N2 / air (used when the part has no "LC_Gas" property)
Const SHEET_W As Double = 1250
Const SHEET_H As Double = 2500
Const SHEET_MARGIN As Double = 5
Const SPACING As Double = 5
Const PRICE_PER_KG As Double = 0.9
Const QUANTITY As Long = 1                   ' quantity for the quote (sheet count); weight/time are per piece
Const OPEN_APP As Boolean = True             ' True: open LaserCAM in the browser with the part; False: silent /api/quote + write to Odoo from here
Const SEND_ODOO_FROM_APP As Boolean = True   ' True: LaserCAM's "Save to Odoo" button writes to Odoo directly (credentials travel with the job, server memory only)
Const WAIT_FOR_RESULT As Boolean = False     ' True: after opening the browser keep waiting and copy the result into the part custom properties
Const APP_WAIT_MIN As Long = 30              ' how long to wait for the result from the browser (minutes)
' -----------------------------------------------------

Private g_token As String
Private g_tmpl As Long   ' product.template id of the found/created product (attachments + Studio fields live there)
' Odoo BOM sheet line (e.g. "DC01 3x1250x2500"): material text, thickness, sheet size
Private g_sheetName As String, g_sheetThick As Double, g_sheetW As Double, g_sheetH As Double, g_sheetProductId As Long

Sub Main()
    Dim swApp As Object, swModel As Object
    g_tmpl = 0: g_sheetName = "": g_sheetThick = 0: g_sheetW = 0: g_sheetH = 0: g_sheetProductId = 0
    Set swApp = Application.SldWorks
    Set swModel = swApp.ActiveDoc
    If swModel Is Nothing Then MsgBox "Open a sheet metal part first.", vbExclamation: Exit Sub
    If swModel.GetType <> 1 Then MsgBox "The active document is not a part.", vbExclamation: Exit Sub

    Dim number As String, thick As Double, material As String, gas As String
    Dim title As String: title = CleanTitle(swModel.GetTitle)   ' without .SLDPRT
    number = CustomProp(swModel, NUMBER_PROPERTY)
    If number = "" Then
        number = InputBox("The part has no custom property """ & NUMBER_PROPERTY & """. Enter the Odoo number (Internal Reference):", "LaserCAM")
        If number = "" Then Exit Sub
        SetCustomProp swModel, NUMBER_PROPERTY, number
    End If
    Dim swThick As Double: swThick = SheetThickness(swModel)

    ' Odoo product first: its BOM sheet line (or the template product's) defines material + thickness + sheet.
    Dim pid As Long: pid = OdooFindProduct(number)
    Dim created As Boolean
    If pid = 0 Then
        If MsgBox("No Odoo product with number """ & number & """. Create it?", vbYesNo + vbQuestion, "LaserCAM") <> vbYes Then Exit Sub
        pid = OdooCreateProduct(number, title)
        created = True
    End If
    If pid = 0 Then MsgBox "Could not find or create the Odoo product.", vbCritical: Exit Sub
    If g_tmpl = 0 Then g_tmpl = OdooTemplateOf(pid)
    Dim sheetSource As String
    If USE_ODOO_SHEET Then
        If OdooSheetOf(g_tmpl) Then
            sheetSource = "product BOM"
        ElseIf TEMPLATE_NUMBER <> "" Then
            Dim tp As Long: tp = OdooFindProduct(TEMPLATE_NUMBER)
            If tp > 0 Then If OdooSheetOf(OdooTemplateOf(tp)) Then sheetSource = "template " & TEMPLATE_NUMBER
        End If
    End If

    If sheetSource <> "" Then
        material = g_sheetName
        thick = g_sheetThick
        If swThick > 0 And Abs(swThick - thick) > 0.05 Then
            If MsgBox("SolidWorks thickness " & NumStr(swThick) & " mm differs from the Odoo sheet " & NumStr(thick) & " mm (" & g_sheetName & ")." & vbCrLf & "Use the Odoo sheet thickness?", vbYesNo + vbQuestion, "LaserCAM") = vbNo Then thick = swThick
        End If
    Else
        thick = swThick
        If thick <= 0 Then
            thick = Val(InputBox("Sheet metal thickness not found. Enter thickness in mm:", "LaserCAM", "2"))
            If thick <= 0 Then Exit Sub
        End If
        material = CustomProp(swModel, "LC_Material")   ' optional override: LaserCAM material name/alias
        If material = "" Then
            Dim matDb As String
            material = swModel.GetMaterialPropertyName2("", matDb)   ' 2nd arg is ByRef out - needs a variable
        End If
        If material = "" Then material = InputBox("The part has no material. Enter the material name from the LaserCAM database:", "LaserCAM", "Carbon steel")
        sheetSource = "SolidWorks"
    End If
    gas = CustomProp(swModel, "LC_Gas")
    If gas = "" Then gas = DEFAULT_GAS
    Dim shW As Double, shH As Double
    shW = IIf(g_sheetW > 0, g_sheetW, SHEET_W): shH = IIf(g_sheetH > 0, g_sheetH, SHEET_H)

    ' 1. Flat pattern DXF
    Dim dxfPath As String
    dxfPath = Environ("TEMP") & "\lasercam_" & SafeName(number) & ".dxf"
    If Not ExportFlatDxf(swModel, dxfPath) Then MsgBox "Flat pattern DXF export failed (does the part have a flat pattern?).", vbCritical: Exit Sub
    Dim dxf As String: dxf = ReadTextFile(dxfPath)

    ' 2. LaserCAM /api/quote
    If Not EnsureToken() Then Exit Sub
    Dim body As String
    body = "{""parts"":[{""id"":" & JsonStr(number) & ",""name"":" & JsonStr(title) & ",""dxf"":" & JsonStr(dxf) & _
           ",""quantity"":" & QUANTITY & ",""thicknessMm"":" & NumStr(thick) & ",""material"":" & JsonStr(material) & ",""gas"":" & JsonStr(gas) & "}]," & _
           """sheet"":{""width"":" & NumStr(shW) & ",""height"":" & NumStr(shH) & ",""margin"":" & NumStr(SHEET_MARGIN) & "}," & _
           """spacing"":" & NumStr(SPACING) & ",""pricePerKg"":" & NumStr(PRICE_PER_KG) & ",""fast"":true}"
    Dim status As Long, resp As String
    If OPEN_APP Then
        ' Job for the browser: LaserCAM opens with the part, user nests and clicks "Send result";
        ' we poll the result (weight / time / price per part) and write it to Odoo.
        Dim jobBody As String
        jobBody = Left(body, Len(body) - 1) & ",""number"":" & JsonStr(number) & ",""source"":""SolidWorks"""
        If SEND_ODOO_FROM_APP Then jobBody = jobBody & ",""odoo"":{""url"":" & JsonStr(ODOO_URL) & ",""apiKey"":" & JsonStr(ODOO_API_KEY) & IIf(ODOO_DB <> "", ",""db"":" & JsonStr(ODOO_DB), "") & "}"
        If g_sheetProductId > 0 Then jobBody = jobBody & ",""sheetProductId"":" & g_sheetProductId
        If WAIT_FOR_RESULT Then jobBody = jobBody & ",""wait"":true"   ' LaserCAM shows "Send result to SolidWorks" only then
        jobBody = jobBody & "}"
        resp = Http("POST", LC_API & "/job", jobBody, "Bearer " & g_token, status)
        If status = 401 Then
            g_token = ""
            If EnsureToken() Then resp = Http("POST", LC_API & "/job", jobBody, "Bearer " & g_token, status)
        End If
        If status <> 200 Then MsgBox "LaserCAM /api/job error " & status & ": " & Left(resp, 300), vbCritical: Exit Sub
        Dim jobId As String, jobUrl As String
        jobId = JsonString(resp, "id"): jobUrl = JsonString(resp, "url")
        If jobId = "" Or jobUrl = "" Then MsgBox "LaserCAM did not return a job id.", vbCritical: Exit Sub
        CreateObject("WScript.Shell").Run jobUrl, 1, False
        If Not WAIT_FOR_RESULT Then
            MsgBox "LaserCAM opened in the browser with " & number & " (" & material & ", " & NumStr(thick) & " mm, sheet " & NumStr(shW) & " x " & NumStr(shH) & ")." & vbCrLf & vbCrLf & _
                   IIf(SEND_ODOO_FROM_APP, "Nest it there and click ""Save to Odoo"" - weight, DXF, BOM sheet quantity and laser time go straight to Odoo.", "Nest it there and click ""Send result to SolidWorks"", then run this macro with WAIT_FOR_RESULT = True to copy the numbers into the part."), vbInformation, "LaserCAM"
            Exit Sub
        End If
        Dim t0 As Single: t0 = Timer
        Do
            Sleep 3000
            DoEvents
            resp = Http("GET", LC_API & "/job/" & jobId & "/result", "", "Bearer " & g_token, status)
            If status = 200 Then Exit Do
            If status <> 204 Then MsgBox "LaserCAM result error " & status & ": " & Left(resp, 300), vbCritical: Exit Sub
            If Timer - t0 > APP_WAIT_MIN * 60 Or Timer < t0 Then MsgBox "No result from LaserCAM after " & APP_WAIT_MIN & " minutes. Run the macro again when the nesting is done.", vbExclamation: Exit Sub
        Loop
    Else
        resp = Http("POST", LC_API & "/quote", body, "Bearer " & g_token, status)
        If status = 401 Then
            g_token = ""
            If EnsureToken() Then resp = Http("POST", LC_API & "/quote", body, "Bearer " & g_token, status)
        End If
        If status <> 200 Then MsgBox "LaserCAM /api/quote error " & status & ": " & Left(resp, 300), vbCritical: Exit Sub
    End If

    Dim netKg As String, usedKg As String, secs As String, price As String, perSheet As String, algo As String, warn As String
    netKg = JsonNum(resp, "netKgPerPart"): usedKg = JsonNum(resp, "usedKgPerPart"): secs = JsonNum(resp, "secPerPart")
    price = JsonNum(resp, "pricePerPart"): perSheet = JsonNum(resp, "partsPerSheet"): algo = JsonString(resp, "algo")
    If OPEN_APP Then perSheet = JsonNum(resp, "sheets"): algo = "sheets used in LaserCAM"
    warn = JsonString(resp, "warnings")

    ' 3. Odoo: write results (skipped when LaserCAM already saved to Odoo itself)
    Dim odooByApp As Boolean: odooByApp = OPEN_APP And InStr(resp, """odooDone"":{") > 0
    If Not odooByApp Then OdooWrite "product.product", pid, "{""weight"":" & netKg & "}"
    Dim quoteJson As String
    quoteJson = "{""netKg"":" & netKg & ",""usedKg"":" & usedKg & ",""sec"":" & secs & ",""price"":" & price & ",""partsPerSheet"":" & perSheet & _
                ",""thicknessMm"":" & NumStr(thick) & ",""material"":" & JsonStr(material) & ",""gas"":" & JsonStr(gas) & ",""algo"":" & JsonStr(algo) & ",""date"":" & JsonStr(Format(Now, "yyyy-mm-dd hh:nn")) & "}"
    If Not odooByApp Then
        On Error Resume Next
        OdooWrite "product.template", g_tmpl, "{""x_studio_lasercam_quote"":" & JsonStr(quoteJson) & "}"   ' field may not exist - skip silently
        On Error GoTo 0
        OdooAttach "product.template", g_tmpl, SafeName(number) & ".dxf", dxfPath
    End If

    ' 4. Into the part - visible in SolidWorks too
    SetCustomProp swModel, "LC_NetKg", netKg
    SetCustomProp swModel, "LC_UsedKg", usedKg
    SetCustomProp swModel, "LC_CutSec", secs
    SetCustomProp swModel, "LC_Price", price

    MsgBox "Odoo product " & IIf(created, "CREATED", "updated") & IIf(odooByApp, " (by LaserCAM)", "") & ": " & number & vbCrLf & vbCrLf & _
           "Thickness " & NumStr(thick) & " mm, " & material & ", " & gas & "  [" & sheetSource & "]" & vbCrLf & _
           "Sheet " & NumStr(shW) & " x " & NumStr(shH) & " mm" & vbCrLf & _
           "Net weight: " & netKg & " kg" & vbCrLf & _
           "With waste: " & usedKg & " kg" & vbCrLf & _
           "Cutting time: " & secs & " s" & vbCrLf & _
           "Price: " & price & " EUR" & vbCrLf & _
           "Parts per sheet: " & perSheet & " (" & algo & ")" & vbCrLf & _
           IIf(warn <> "", vbCrLf & "Warning: " & warn, ""), vbInformation, "LaserCAM -> Odoo"
End Sub

' ---------------- SolidWorks ----------------
Private Function ExportFlatDxf(swModel As Object, path As String) As Boolean
    ' ExportToDWG2: Action 1 = sheet metal flat pattern; SheetMetalOptions bits: 1 geometry, 4 bend lines.
    ' Alignment must be a 12-double array (origin + X/Y/Z axes), Views an empty variant.
    Dim opts As Long: opts = 1
    Dim align(11) As Double
    align(3) = 1#: align(7) = 1#: align(11) = 1#
    Dim varAlign As Variant: varAlign = align
    Dim varViews As Variant
    On Error Resume Next
    If Len(Dir(path)) > 0 Then Kill path
    ExportFlatDxf = swModel.ExportToDWG2(path, swModel.GetPathName, 1, True, varAlign, False, False, opts, varViews)
    If Err.Number <> 0 Then ExportFlatDxf = False
    On Error GoTo 0
    If ExportFlatDxf Then ExportFlatDxf = (Len(Dir(path)) > 0)
End Function

Private Function SheetThickness(swModel As Object) As Double
    ' First "SheetMetal" feature -> SheetMetalFeatureData.Thickness (meters -> mm).
    Dim f As Object: Set f = swModel.FirstFeature
    Do While Not f Is Nothing
        If f.GetTypeName2 = "SheetMetal" Then
            Dim d As Object: Set d = f.GetDefinition
            If Not d Is Nothing Then SheetThickness = Round(d.Thickness * 1000, 3): Exit Function
        End If
        Set f = f.GetNextFeature
    Loop
    SheetThickness = 0
End Function

Private Function CustomProp(swModel As Object, name As String) As String
    Dim mgr As Object: Set mgr = swModel.Extension.CustomPropertyManager("")
    Dim v As String, r As String
    On Error Resume Next
    mgr.Get4 name, False, v, r
    On Error GoTo 0
    CustomProp = Trim(r)
    If CustomProp = "" Then CustomProp = Trim(v)
End Function

Private Sub SetCustomProp(swModel As Object, name As String, value As String)
    Dim mgr As Object: Set mgr = swModel.Extension.CustomPropertyManager("")
    On Error Resume Next
    mgr.Add3 name, 30, value, 1   ' 30 = swCustomInfoText, 1 = overwrite existing
    On Error GoTo 0
End Sub

' ---------------- LaserCAM ----------------
Private Function EnsureToken() As Boolean
    If g_token <> "" Then EnsureToken = True: Exit Function
    If LC_KEY = "" Or LC_PID = "0" Then MsgBox "Fill in LC_PID and LC_KEY in the macro settings.", vbExclamation: Exit Function
    Dim status As Long, resp As String
    resp = Http("POST", LC_API & "/session", "{""pid"":" & LC_PID & ",""key"":" & JsonStr(LC_KEY) & "}", "", status)
    If status <> 200 Then MsgBox "LaserCAM session failed (" & status & "): check LC_PID / LC_KEY and the plan.", vbCritical: Exit Function
    g_token = JsonString(resp, "token")
    EnsureToken = (g_token <> "")
End Function

' ---------------- Odoo (JSON-2 API, Odoo 17+/Online; bearer API key) ----------------
Private Function OdooCall(model As String, method As String, jsonArgs As String) As String
    Dim status As Long, resp As String
    resp = HttpOdoo(ODOO_URL & "/json/2/" & model & "/" & method, jsonArgs, status)
    If status < 200 Or status >= 300 Then Err.Raise vbObjectError + 1, , "Odoo " & model & "." & method & " -> " & status & ": " & Left(resp, 300)
    OdooCall = resp
End Function

Private Function OdooFindProduct(code As String) As Long
    Dim r As String
    r = OdooCall("product.product", "search_read", "{""domain"":[[""default_code"",""="",""" & EscapeJson(code) & """]],""fields"":[""id""],""limit"":1}")
    OdooFindProduct = Val(JsonNum(r, "id"))
    If OdooFindProduct = 0 Then
        ' fallback: product name starts with the number
        r = OdooCall("product.product", "search_read", "{""domain"":[[""name"",""=ilike"",""" & EscapeJson(code) & "%""]],""fields"":[""id""],""limit"":1}")
        OdooFindProduct = Val(JsonNum(r, "id"))
    End If
End Function

' product.template id for a product.product id (the product form and its chatter/attachments are the template).
Private Function OdooTemplateOf(pid As Long) As Long
    Dim r As String
    r = OdooCall("product.product", "search_read", "{""domain"":[[""id"",""=""," & pid & "]],""fields"":[""product_tmpl_id""],""limit"":1}")
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = """product_tmpl_id""\s*:\s*\[?\s*(\d+)": re.Global = False
    If re.Test(r) Then OdooTemplateOf = Val(re.Execute(r)(0).SubMatches(0)) Else OdooTemplateOf = 0
End Function

' BOM of a product template -> first component whose name looks like "T x W x H" (any order; thickness = smallest).
' Sets g_sheetName / g_sheetThick / g_sheetW / g_sheetH. Returns False when the product has no such BOM line.
' NOTE: ids go unquoted in the domain - a quoted id on a many2one field makes Odoo search by NAME (never matches).
Private Function OdooSheetOf(tmplId As Long) As Boolean
    OdooSheetOf = False
    If tmplId = 0 Then Exit Function
    Dim r As String
    r = OdooCall("mrp.bom", "search_read", "{""domain"":[[""product_tmpl_id"",""=""," & tmplId & "]],""fields"":[""bom_line_ids""],""limit"":1}")
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = """bom_line_ids""\s*:\s*\[([^\]]*)\]": re.Global = False
    If Not re.Test(r) Then Exit Function
    Dim ids As String: ids = Trim(re.Execute(r)(0).SubMatches(0))
    If ids = "" Then Exit Function
    r = OdooCall("mrp.bom.line", "search_read", "{""domain"":[[""id"",""in"",[" & ids & "]]],""fields"":[""product_id""]}")
    ' product_id: [id, "Display Name"] -> first that parses as a sheet wins (id kept for BOM creation in the app)
    re.pattern = """product_id""\s*:\s*\[\s*(\d+)\s*,\s*""([^""]*)""": re.Global = True
    Dim m As Object
    For Each m In re.Execute(r)
        If ParseSheet(m.SubMatches(1)) Then g_sheetProductId = Val(m.SubMatches(0)): OdooSheetOf = True: Exit Function
    Next
End Function

' "DC01 3x1250x2500", "[LAK] Lakstas S235JR 2.0 x 1000 x 2000" -> thickness (smallest), W, H; name = full text.
Private Function ParseSheet(text As String) As Boolean
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = "(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)": re.Global = False
    If Not re.Test(text) Then Exit Function
    Dim m As Object: Set m = re.Execute(text)(0)
    Dim a As Double, b As Double, c As Double
    a = Val(Replace(m.SubMatches(0), ",", ".")): b = Val(Replace(m.SubMatches(1), ",", ".")): c = Val(Replace(m.SubMatches(2), ",", "."))
    Dim t As Double: t = a: If b < t Then t = b
    If c < t Then t = c
    If t <= 0 Or t > 100 Then Exit Function
    Dim d1 As Double, d2 As Double
    If t = a Then d1 = b: d2 = c Else If t = b Then d1 = a: d2 = c Else d1 = a: d2 = b
    g_sheetName = text: g_sheetThick = t
    g_sheetW = IIf(d1 < d2, d1, d2): g_sheetH = IIf(d1 < d2, d2, d1)
    ParseSheet = True
End Function

' Product name: the title if it already starts with the number, otherwise "number title".
Private Function OdooCreateProduct(code As String, title As String) As Long
    Dim r As String, nm As String
    If LCase(Left(title, Len(code))) = LCase(code) Then nm = title Else nm = Trim(code & " " & title)
    r = OdooCall("product.product", "create", "{""vals_list"":[{""name"":" & JsonStr(nm) & ",""default_code"":" & JsonStr(code) & ",""type"":""consu""}]}")
    OdooCreateProduct = Val(JsonNum(r, ""))
End Function

Private Sub OdooWrite(model As String, id As Long, valsJson As String)
    OdooCall model, "write", "{""ids"":[" & id & "],""vals"":" & valsJson & "}"
End Sub

Private Sub OdooAttach(model As String, id As Long, fileName As String, path As String)
    Dim b64 As String: b64 = FileToBase64(path)
    OdooCall "ir.attachment", "create", "{""vals_list"":[{""name"":" & JsonStr(fileName) & ",""res_model"":" & JsonStr(model) & ",""res_id"":" & id & ",""datas"":" & JsonStr(b64) & ",""mimetype"":""application/dxf""}]}"
End Sub

' ---------------- HTTP ----------------
Private Function Http(method As String, url As String, body As String, auth As String, ByRef status As Long) As String
    Dim h As Object: Set h = CreateObject("MSXML2.ServerXMLHTTP.6.0")
    h.Open method, url, False
    h.setRequestHeader "Content-Type", "application/json"
    If auth <> "" Then h.setRequestHeader "Authorization", auth
    h.setTimeouts 10000, 10000, 60000, 600000
    If method = "GET" Then h.send Else h.send body
    status = h.Status
    Http = h.responseText
End Function

Private Function HttpOdoo(url As String, body As String, ByRef status As Long) As String
    Dim h As Object: Set h = CreateObject("MSXML2.ServerXMLHTTP.6.0")
    h.Open "POST", url, False
    h.setRequestHeader "Content-Type", "application/json"
    h.setRequestHeader "Authorization", "bearer " & ODOO_API_KEY
    If ODOO_DB <> "" Then h.setRequestHeader "X-Odoo-Database", ODOO_DB
    h.setTimeouts 10000, 10000, 60000, 120000
    h.send body
    status = h.Status
    HttpOdoo = h.responseText
End Function

' ---------------- JSON (minimal) ----------------
Private Function JsonStr(s As String) As String
    JsonStr = """" & EscapeJson(s) & """"
End Function
Private Function EscapeJson(s As String) As String
    Dim t As String: t = Replace(s, "\", "\\")
    t = Replace(t, """", "\""")
    t = Replace(t, vbCrLf, "\n"): t = Replace(t, vbLf, "\n"): t = Replace(t, vbCr, "\n"): t = Replace(t, vbTab, "\t")
    EscapeJson = t
End Function
Private Function NumStr(d As Double) As String
    NumStr = Replace(CStr(d), ",", ".")
End Function
' First numeric value for a key ("" = first number in the response, e.g. create -> [123]).
Private Function JsonNum(json As String, key As String) As String
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = IIf(key = "", "-?\d+(\.\d+)?", """" & key & """\s*:\s*(-?\d+(\.\d+)?)")
    re.Global = False
    Dim m As Object
    If re.Test(json) Then
        Set m = re.Execute(json)(0)
        JsonNum = IIf(key = "", m.Value, m.SubMatches(0))
    Else
        JsonNum = "0"
    End If
End Function
Private Function JsonString(json As String, key As String) As String
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    If key = "warnings" Then
        re.pattern = """warnings""\s*:\s*\[([^\]]*)\]"
    Else
        re.pattern = """" & key & """\s*:\s*""([^""]*)"""
    End If
    re.Global = False
    If re.Test(json) Then JsonString = Replace(re.Execute(json)(0).SubMatches(0), """", "") Else JsonString = ""
End Function

' ---------------- files ----------------
Private Function ReadTextFile(path As String) As String
    Dim st As Object: Set st = CreateObject("ADODB.Stream")
    st.Type = 2: st.Charset = "utf-8": st.Open: st.LoadFromFile path
    ReadTextFile = st.ReadText: st.Close
End Function
Private Function FileToBase64(path As String) As String
    Dim st As Object: Set st = CreateObject("ADODB.Stream")
    st.Type = 1: st.Open: st.LoadFromFile path
    Dim bytes() As Byte: bytes = st.Read: st.Close
    Dim xml As Object: Set xml = CreateObject("MSXML2.DOMDocument.6.0")
    Dim node As Object: Set node = xml.createElement("b64")
    node.DataType = "bin.base64": node.nodeTypedValue = bytes
    FileToBase64 = Replace(Replace(node.Text, vbLf, ""), vbCr, "")
End Function
' Document title without the file extension (.SLDPRT / .sldprt) and surrounding spaces.
Private Function CleanTitle(t As String) As String
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = "\.(sldprt|sldasm|slddrw)$": re.IgnoreCase = True: re.Global = False
    CleanTitle = Trim(re.Replace(Trim(t), ""))
End Function

Private Function SafeName(s As String) As String
    Dim re As Object: Set re = CreateObject("VBScript.RegExp")
    re.pattern = "[^A-Za-z0-9_.-]": re.Global = True
    SafeName = re.Replace(s, "_")
End Function
