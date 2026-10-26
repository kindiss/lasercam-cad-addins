# ============================================================================
#  LaserCAM - "Send to Odoo" (Autodesk Fusion add-in, Python)
#
#  One-to-one port of the SolidWorks VBA macro solidworks/ExportToOdoo.bas.
#  One click on the active sheet metal component:
#    1. Exports the flat pattern DXF to a temp file.
#    2. Reads thickness (sheet metal rule), material (body material) and the Odoo
#       number (component Part Number).
#    3. Odoo: finds the product by Internal Reference = number (creates it if missing),
#       reads the BOM sheet line ("T x W x H") for material / thickness / sheet size.
#    4. OPEN_APP = true: creates a LaserCAM job and opens it in the browser; the user nests
#       there and clicks "Save to Odoo".
#       OPEN_APP = false: silent /api/quote, then weight + DXF + quote summary are written
#       to Odoo from here.
#
#  Settings: %APPDATA%\LaserCAM\fusion-settings.json (command "LaserCAM Settings").
# ============================================================================
import adsk.core
import adsk.fusion
import base64
import datetime
import json
import os
import re
import tempfile
import traceback
import urllib.error
import urllib.request
import webbrowser

_app = None
_ui = None
_handlers = []          # keep handler references alive for the life of the add-in
_token = ""             # LaserCAM session token (cached per Fusion session)

ADDIN_NAME = "LaserCAM"
CMD_SEND_ID = "LaserCAM_SendToOdoo"
CMD_SETTINGS_ID = "LaserCAM_Settings"
PANEL_ID = "LaserCAM_Panel"
PANEL_NAME = "LaserCAM"
WORKSPACE_ID = "FusionSolidEnvironment"
TAB_IDS = ["SolidTab", "SheetMetalTab"]
RESOURCES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "resources")

# ---------------- SETTINGS ----------------
SETTINGS_PATH = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "LaserCAM", "fusion-settings.json")
DEFAULTS = {
    "LC_API": "https://laser.ucase.eu/api",
    "LC_PID": 0,                  # Odoo contact id (res.partner) that holds the LaserCAM plan
    "LC_KEY": "",                 # LaserCAM key from that contact (x_studio_lasercam_key)
    "ODOO_URL": "",               # your Odoo address, e.g. https://mycompany.odoo.com
    "ODOO_API_KEY": "",           # Odoo user API key (My Profile -> Account Security -> New API Key)
    "ODOO_DB": "",                # empty = default database (Odoo Online)
    "NUMBER_PROPERTY": "Number",  # unused in Fusion: the Odoo number is the component Part Number
    "TEMPLATE_NUMBER": "",        # product whose BOM sheet line is used when the part's product has no BOM yet
    "USE_ODOO_SHEET": True,       # material / thickness / sheet from the Odoo BOM sheet line
    "DEFAULT_GAS": "O2",          # O2 / N2 / air
    "SHEET_W": 1250.0,
    "SHEET_H": 2500.0,
    "SHEET_MARGIN": 5.0,
    "SPACING": 5.0,
    "PRICE_PER_KG": 0.9,
    "QUANTITY": 1,
    "SEND_MODE": "lasercam",      # last choice in the Send dialog: lasercam | odoo (files straight to the product)
    "ATTACH_DXF": True,           # Send dialog defaults (direct send): DXF flat pattern
    "ATTACH_STEP": True,          # STEP export
    "ATTACH_CAD": True,           # Fusion archive (.f3d)
    "OPEN_APP": True,             # true: open LaserCAM in the browser; false: silent /api/quote + write to Odoo from here
    "SEND_ODOO_FROM_APP": True,   # true: LaserCAM's "Save to Odoo" writes to Odoo directly (credentials travel with the job)
}
SECRET_KEYS = ("LC_KEY", "ODOO_API_KEY")


# ---- secrets at rest: Windows DPAPI (current user) via ctypes; "dpapi:<base64>" in the JSON ----
_SECRET_KEYS = ("LC_KEY", "ODOO_API_KEY")
_DPAPI_PREFIX = "dpapi:"
_DPAPI_ENTROPY = b"LaserCAM-settings-v1"


def _dpapi(data, protect):
    """CryptProtectData / CryptUnprotectData (Windows only). Returns bytes or None."""
    import ctypes
    import ctypes.wintypes as wt
    if os.name != "nt":
        return None

    class BLOB(ctypes.Structure):
        _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    def blob(b):
        buf = ctypes.create_string_buffer(b, len(b))
        return BLOB(len(b), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf
    inp, _keep1 = blob(data)
    ent, _keep2 = blob(_DPAPI_ENTROPY)
    out = BLOB()
    fn = ctypes.windll.crypt32.CryptProtectData if protect else ctypes.windll.crypt32.CryptUnprotectData
    ok = fn(ctypes.byref(inp), None, ctypes.byref(ent), None, None, 0, ctypes.byref(out))
    if not ok:
        return None
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)


def protect_secret(plain):
    if not plain:
        return ""
    raw = _dpapi(plain.encode("utf-8"), True)
    return (_DPAPI_PREFIX + base64.b64encode(raw).decode("ascii")) if raw else plain


def unprotect_secret(stored):
    if not stored or not str(stored).startswith(_DPAPI_PREFIX):
        return stored or ""
    raw = _dpapi(base64.b64decode(stored[len(_DPAPI_PREFIX):]), False)
    return raw.decode("utf-8") if raw else ""   # other user / PC -> empty, the dialog asks again


def load_settings():
    """Read the settings JSON; create it with defaults on first run. Unknown keys are kept."""
    s = dict(DEFAULTS)
    try:
        if os.path.isfile(SETTINGS_PATH):
            with open(SETTINGS_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict):
                s.update(data)
                had_plain = False
                for k in _SECRET_KEYS:
                    v = s.get(k) or ""
                    if v and not str(v).startswith(_DPAPI_PREFIX):
                        had_plain = True
                    s[k] = unprotect_secret(v)
                if had_plain:
                    try:
                        save_settings(s)   # upgrade: encrypt the old plain-text keys
                    except Exception:
                        pass
        else:
            save_settings(s)
    except Exception:
        pass
    return s


def save_settings(s):
    os.makedirs(os.path.dirname(SETTINGS_PATH), exist_ok=True)
    out = dict(s)
    for k in _SECRET_KEYS:
        out[k] = protect_secret(str(s.get(k) or ""))
    with open(SETTINGS_PATH, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)


def mask(secret):
    """Never show a key in full: only the last 4 characters."""
    secret = str(secret or "")
    if not secret:
        return "(not set)"
    return "****" + secret[-4:] if len(secret) > 4 else "****"


# ---------------- helpers ----------------
def num_str(d):
    """Like VBA NumStr: 3.0 -> '3', 2.5 -> '2.5'."""
    try:
        d = float(d)
    except Exception:
        return str(d)
    if d == int(d):
        return str(int(d))
    return repr(round(d, 6)).rstrip("0").rstrip(".")


def safe_name(s):
    return re.sub(r"[^A-Za-z0-9_.-]", "_", s)


def clean_title(t):
    """Component name without a file extension, the Fusion " vN" version suffix and surrounding spaces."""
    t = re.sub(r"\.(sldprt|sldasm|slddrw|f3d|step|stp|iges|igs|x_t|sat)$", "", t.strip(), flags=re.IGNORECASE).strip()
    return re.sub(r"\s+v\d+$", "", t, flags=re.IGNORECASE).strip()


def to_float(v, default=0.0):
    try:
        return float(str(v).replace(",", "."))
    except Exception:
        return default


def to_int(v, default=0):
    try:
        return int(float(str(v).replace(",", ".")))
    except Exception:
        return default


def msg(text, title="LaserCAM", icon=None):
    if icon is None:
        icon = adsk.core.MessageBoxIconTypes.InformationIconType
    _ui.messageBox(text, title, adsk.core.MessageBoxButtonTypes.OKButtonType, icon)


def warn(text, title="LaserCAM"):
    msg(text, title, adsk.core.MessageBoxIconTypes.WarningIconType)


def err(text, title="LaserCAM"):
    msg(text, title, adsk.core.MessageBoxIconTypes.CriticalIconType)


def ask_yes_no(text, title="LaserCAM"):
    r = _ui.messageBox(text, title, adsk.core.MessageBoxButtonTypes.YesNoButtonType,
                       adsk.core.MessageBoxIconTypes.QuestionIconType)
    return r == adsk.core.DialogResults.DialogYes


def ask_text(prompt, default=""):
    """inputBox returns (text, cancelled). Empty string when cancelled."""
    result, cancelled = _ui.inputBox(prompt, "LaserCAM", default)
    if cancelled:
        return ""
    return (result or "").strip()


# ---------------- HTTP ----------------
def http(method, url, body=None, headers=None, timeout=600):
    """Returns (status, text). Never raises on HTTP errors; raises on network errors."""
    data = None
    # Cloudflare in front of laser.ucase.eu answers 403 to the default "Python-urllib" agent.
    hdrs = {"Content-Type": "application/json", "User-Agent": "LaserCAM-Fusion/1.0", "Accept": "application/json"}
    if headers:
        hdrs.update(headers)
    if body is not None:
        data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        try:
            text = e.read().decode("utf-8", "replace")
        except Exception:
            text = ""
        return e.code, text
    except (urllib.error.URLError, OSError) as e:
        # no connection / wrong host / timeout -> status 0, the callers turn it into a plain-language message
        return 0, str(getattr(e, "reason", e))


# ---------------- LaserCAM ----------------
def ensure_token(s):
    global _token
    if _token:
        return True
    pid = to_int(s.get("LC_PID"))
    key = str(s.get("LC_KEY") or "")
    if not key or pid <= 0:
        warn("Sending to LaserCAM (nesting, weight, time) needs your LaserCAM key: LaserCAM Settings -> LC_PID and LC_KEY (Basic plan, laser.ucase.eu). Sending files straight to the Odoo product works without it.")
        return False
    status, resp = http("POST", s["LC_API"] + "/session", {"pid": pid, "key": key}, timeout=60)
    if status != 200:
        err("LaserCAM session failed (%s): check LC_PID / LC_KEY and the plan." % status)
        return False
    try:
        _token = json.loads(resp).get("token") or ""
    except Exception:
        _token = ""
    return bool(_token)


def lc_post(s, path, body):
    """POST to LaserCAM with the bearer token; on 401 re-create the session once. Returns (status, text)."""
    global _token
    if not ensure_token(s):
        return None, ""
    status, resp = http("POST", s["LC_API"] + path, body, {"Authorization": "Bearer " + _token})
    if status == 401:
        _token = ""
        if ensure_token(s):
            status, resp = http("POST", s["LC_API"] + path, body, {"Authorization": "Bearer " + _token})
    return status, resp


# ---------------- Odoo (JSON-2 API, Odoo 17+/Online; bearer API key) ----------------
def odoo_http_message(s, status, resp, what):
    """Plain-language text for a failed Odoo request (shown to the user instead of the raw HTTP status)."""
    url = str(s.get("ODOO_URL") or "")
    if status == 0:
        return ("Cannot reach Odoo at %s (%s).\n\nCheck ODOO_URL in LaserCAM Settings (e.g. https://company.odoo.com), "
                "the internet connection and VPN." % (url or "(ODOO_URL is empty)", resp[:120]))
    if status in (401, 403):
        if not str(s.get("ODOO_API_KEY") or "").strip():
            return ("Odoo rejected the request because the API key is empty (HTTP %s).\n\nLaserCAM Settings -> ODOO_API_KEY: "
                    "in Odoo open My profile > Account security > New API key, copy the key and paste it there. "
                    "Keys are stored encrypted and cannot be viewed again - if lost, create a new one." % status)
        return ("Odoo rejected the API key (HTTP %s).\n\nThe key in LaserCAM Settings is wrong, revoked or belongs to another Odoo. "
                "Create a new key in Odoo (My profile > Account security > New API key) and paste it into ODOO_API_KEY. "
                "On-premise Odoo with several databases also needs ODOO_DB." % status)
    if status == 404:
        return ("Odoo did not answer at %s (HTTP 404, %s).\n\nCheck ODOO_URL in LaserCAM Settings. This direct connection needs Odoo 19 or newer; "
                "for older Odoo use the LaserCAM Connector module (Odoo App Store)." % (url, what))
    if status >= 500:
        return "Odoo server error (HTTP %s) on %s:\n%s" % (status, what, resp[:300])
    return "Odoo %s -> HTTP %s: %s" % (what, status, resp[:300])


def odoo_ready(s):
    """First-run guard: without the Odoo address or API key every request would fail with a bare 401 -
    explain what is missing and open LaserCAM Settings instead."""
    url = str(s.get("ODOO_URL") or "").strip()
    key = str(s.get("ODOO_API_KEY") or "").strip()
    if url and key:
        return True
    missing = [name for name, ok in (("the Odoo address (ODOO_URL)", bool(url)), ("the Odoo API key (ODOO_API_KEY)", bool(key))) if not ok]
    warn("LaserCAM is not connected to Odoo yet - %s %s missing.\n\n"
         "Fill in LaserCAM Settings:\n"
         "- ODOO_URL: your Odoo address, e.g. https://company.odoo.com\n"
         "- ODOO_API_KEY: in Odoo open My profile > Account security > New API key, copy it once and paste it here "
         "(keys are stored encrypted and cannot be viewed again)\n"
         "- ODOO_DB: only for on-premise Odoo with several databases\n\n"
         "The Settings dialog opens now." % (" and ".join(missing), "are" if len(missing) > 1 else "is"))
    try:
        cd = _ui.commandDefinitions.itemById(CMD_SETTINGS_ID)
        if cd:
            cd.execute()
    except Exception:
        pass
    return False


def odoo_call(s, model, method, kwargs):
    headers = {"Authorization": "bearer " + str(s.get("ODOO_API_KEY") or "")}
    if s.get("ODOO_DB"):
        headers["X-Odoo-Database"] = s["ODOO_DB"]
    url = s["ODOO_URL"].rstrip("/") + "/json/2/" + model + "/" + method
    status, resp = http("POST", url, kwargs, headers, timeout=120)
    if status < 200 or status >= 300:
        raise RuntimeError(odoo_http_message(s, status, resp, "%s.%s" % (model, method)))
    try:
        return json.loads(resp)
    except Exception:
        return resp


def first_id(rows):
    if isinstance(rows, list) and rows and isinstance(rows[0], dict):
        return to_int(rows[0].get("id"))
    return 0


def odoo_find_product(s, code):
    r = odoo_call(s, "product.product", "search_read",
                  {"domain": [["default_code", "=", code]], "fields": ["id"], "limit": 1})
    pid = first_id(r)
    if pid == 0:
        # fallback: product name starts with the number
        r = odoo_call(s, "product.product", "search_read",
                      {"domain": [["name", "=ilike", code + "%"]], "fields": ["id"], "limit": 1})
        pid = first_id(r)
    return pid


def odoo_template_of(s, pid):
    """product.template id for a product.product id (attachments + Studio fields live there)."""
    r = odoo_call(s, "product.product", "search_read",
                  {"domain": [["id", "=", pid]], "fields": ["product_tmpl_id"], "limit": 1})
    if isinstance(r, list) and r:
        v = r[0].get("product_tmpl_id")
        if isinstance(v, list) and v:
            return to_int(v[0])
        return to_int(v)
    return 0


SHEET_RE = re.compile(r"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)")


def parse_sheet(text):
    """'DC01 3x1250x2500' -> dict(name, thick, w, h) or None. Thickness = smallest of the three."""
    m = SHEET_RE.search(text or "")
    if not m:
        return None
    a, b, c = (to_float(m.group(i)) for i in (1, 2, 3))
    t = min(a, b, c)
    if t <= 0 or t > 100:
        return None
    if t == a:
        d1, d2 = b, c
    elif t == b:
        d1, d2 = a, c
    else:
        d1, d2 = a, b
    return {"name": text, "thick": t, "w": min(d1, d2), "h": max(d1, d2)}


def odoo_sheet_of(s, tmpl_id):
    """BOM of a product template -> first component whose display name looks like 'T x W x H'.
    NOTE: ids go unquoted in the domain - a quoted id on a many2one field makes Odoo search by NAME."""
    if not tmpl_id:
        return None
    r = odoo_call(s, "mrp.bom", "search_read",
                  {"domain": [["product_tmpl_id", "=", int(tmpl_id)]], "fields": ["bom_line_ids"], "limit": 1})
    if not (isinstance(r, list) and r):
        return None
    ids = r[0].get("bom_line_ids") or []
    if not ids:
        return None
    lines = odoo_call(s, "mrp.bom.line", "search_read",
                      {"domain": [["id", "in", [int(i) for i in ids]]], "fields": ["product_id"]})
    for line in lines if isinstance(lines, list) else []:
        p = line.get("product_id")
        if isinstance(p, list) and len(p) >= 2:
            sheet = parse_sheet(str(p[1]))
            if sheet:
                sheet["productId"] = to_int(p[0])
                return sheet
    return None


def odoo_create_product(s, code, title):
    """Product name: the title if it already starts with the number, otherwise 'number title'."""
    if title.lower().startswith(code.lower()):
        nm = title
    else:
        nm = (code + " " + title).strip()
    r = odoo_call(s, "product.product", "create",
                  {"vals_list": [{"name": nm, "default_code": code, "type": "consu"}]})
    if isinstance(r, list) and r:
        return to_int(r[0])
    return to_int(r)


def odoo_write(s, model, rec_id, vals):
    odoo_call(s, model, "write", {"ids": [int(rec_id)], "vals": vals})


def odoo_attach(s, model, rec_id, file_name, path, mimetype="application/dxf"):
    """Attach a file to an Odoo record; an attachment with the same name is replaced (no duplicates)."""
    with open(path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    old = odoo_call(s, "ir.attachment", "search_read", {
        "domain": [["res_model", "=", model], ["res_id", "=", int(rec_id)], ["name", "=", file_name]],
        "fields": ["id"], "limit": 1})
    if old:
        odoo_call(s, "ir.attachment", "write", {"ids": [int(old[0]["id"])], "vals": {"datas": b64}})
        return
    odoo_call(s, "ir.attachment", "create", {"vals_list": [{
        "name": file_name, "res_model": model, "res_id": int(rec_id),
        "type": "binary", "datas": b64, "mimetype": mimetype}]})


def upload_attachments(s, design, comp, body, tmpl, number, progress, with_dxf, with_step, with_cad):
    """Ticked files (DXF flat pattern, STEP, .f3d) -> Odoo product attachments. Returns a summary string."""
    if not tmpl:
        return ""
    files = []
    stem = safe_name(number)
    em = design.exportManager
    if with_dxf:
        progress(40, "Exporting the flat pattern DXF...")
        path = os.path.join(tempfile.gettempdir(), "lasercam_" + stem + ".dxf")
        try:
            fp = ensure_flat_pattern(comp, body)
            if export_flat_dxf(design, fp, path):
                files.append((stem + ".dxf", path, "application/dxf"))
        except Exception as e:
            files.append((stem + ".dxf", None, str(e)))
    if with_cad:
        progress(72, "Exporting the Fusion archive (.f3d)...")
        path = os.path.join(tempfile.gettempdir(), "lasercam_" + stem + ".f3d")
        try:
            if os.path.isfile(path):
                os.remove(path)
            opts = em.createFusionArchiveExportOptions(path)
            if em.execute(opts) and os.path.isfile(path):
                files.append((stem + ".f3d", path, "application/octet-stream"))
        except Exception as e:
            files.append((stem + ".f3d", None, str(e)))
    if with_step:
        progress(78, "Exporting STEP...")
        path = os.path.join(tempfile.gettempdir(), "lasercam_" + stem + ".step")
        try:
            if os.path.isfile(path):
                os.remove(path)
            opts = em.createSTEPExportOptions(path, comp)
            if em.execute(opts) and os.path.isfile(path):
                files.append((stem + ".step", path, "application/step"))
        except Exception as e:
            files.append((stem + ".step", None, str(e)))
    done = []
    n = len([f for f in files if f[1]])
    i = 0
    for name, path, mime in files:
        if not path:
            done.append("%s FAILED: %s" % (name, mime))
            continue
        i += 1
        progress(80 + int(18.0 * i / max(n, 1)), "Uploading to Odoo (%d/%d): %s..." % (i, n, name))
        try:
            odoo_attach(s, "product.template", tmpl, name, path, mime)
            done.append("%s (%d KB)" % (name, os.path.getsize(path) // 1024))
        except Exception as e:
            done.append("%s FAILED: %s" % (name, e))
    return ", ".join(done)


# ---------------- Fusion ----------------
def get_design():
    design = adsk.fusion.Design.cast(_app.activeProduct)
    if not design:
        err("Open a sheet metal design first.")
    return design


def sheet_metal_body(comp):
    for i in range(comp.bRepBodies.count):
        body = comp.bRepBodies.item(i)
        try:
            if body.isSheetMetal:
                return body
        except Exception:
            pass
    return None


def find_sheet_metal(design):
    """(component, sheet metal body) to export, or (None, None) with an error shown.
    Order: selected body -> selected occurrence's component -> root component -> the ONLY
    component in the design that has a sheet metal body (parts are often nested in a sub-component)."""
    sel = _ui.activeSelections
    if sel.count == 1:
        ent = sel.item(0).entity
        body = adsk.fusion.BRepBody.cast(ent)
        if body:
            try:
                if body.isSheetMetal:
                    return body.parentComponent, body
            except Exception:
                pass
            err("The selected body is not sheet metal. Convert it first (Sheet Metal -> Convert to Sheet Metal).")
            return None, None
        occ = adsk.fusion.Occurrence.cast(ent)
        if occ:
            b = sheet_metal_body(occ.component)
            if b:
                return occ.component, b
            err("No sheet metal body in the selected component \"%s\"." % occ.component.name)
            return None, None
    root = design.rootComponent
    b = sheet_metal_body(root)
    if b:
        return root, b
    found = []
    try:
        comps = design.allComponents
        for i in range(comps.count):
            c = comps.item(i)
            if c == root:
                continue
            b = sheet_metal_body(c)
            if b:
                found.append((c, b))
    except Exception:
        pass
    if len(found) == 1:
        return found[0]
    if len(found) > 1:
        err("Several components have sheet metal bodies: %s. Select the body (or the component) to send, then click Send to Odoo again." % ", ".join(c.name for c, _ in found))
        return None, None
    nbodies = root.bRepBodies.count
    err("No sheet metal body found in \"%s\" (%d solid bodies). The part must be a Fusion sheet metal body: select the body, Sheet Metal -> Convert to Sheet Metal (imported SolidWorks / STEP bodies are plain solids), then click Send to Odoo again." % (root.name, nbodies))
    return None, None


def sheet_thickness_mm(comp, flat_pattern):
    """Sheet metal rule thickness (cm -> mm); fallback: measured on the flat pattern body. 0 = unknown."""
    try:
        rule = comp.activeSheetMetalRule
        if rule and rule.thickness:
            v = rule.thickness.value * 10.0
            if v > 0:
                return round(v, 3)
    except Exception:
        pass
    try:
        if flat_pattern:
            body = flat_pattern.flatBody
            res = body.findThicknessAtFace(flat_pattern.topFace)   # preview API: returns (ok, thickness_cm)
            if isinstance(res, (list, tuple)) and len(res) >= 2 and res[0]:
                return round(float(res[1]) * 10.0, 3)
    except Exception:
        pass
    return 0.0


def body_material_name(body):
    try:
        if body and body.material:
            return body.material.name or ""
    except Exception:
        pass
    return ""


def largest_planar_face(body):
    best, best_area = None, -1.0
    for i in range(body.faces.count):
        face = body.faces.item(i)
        try:
            if face.geometry.surfaceType == adsk.core.SurfaceTypes.PlaneSurfaceType and face.area > best_area:
                best, best_area = face, face.area
        except Exception:
            pass
    return best


def ensure_flat_pattern(comp, body):
    fp = comp.flatPattern
    if fp:
        return fp
    face = largest_planar_face(body)
    if not face:
        raise RuntimeError("No planar face found on the sheet metal body to create the flat pattern.")
    fp = comp.createFlatPattern(face)
    if not fp:
        raise RuntimeError("createFlatPattern failed.")
    return fp


def export_flat_dxf(design, flat_pattern, path):
    """Flat pattern -> DXF (geometry only, mm), via ExportManager.createDXFFlatPatternExportOptions."""
    try:
        if os.path.isfile(path):
            os.remove(path)
    except Exception:
        pass
    em = design.exportManager
    opts = em.createDXFFlatPatternExportOptions(path, flat_pattern)
    if not opts:
        raise RuntimeError("createDXFFlatPatternExportOptions returned null.")
    try:
        opts.units = adsk.fusion.DistanceUnits.MillimeterDistanceUnits
        opts.isCenterLinesExported = False
        opts.isExtentLinesExported = False
    except Exception:
        pass
    ok = em.execute(opts)
    return bool(ok) and os.path.isfile(path)


def set_attributes(comp, values):
    """Results back into the component as attributes (group 'LaserCAM'); best effort."""
    try:
        for k, v in values.items():
            comp.attributes.add("LaserCAM", k, str(v))
    except Exception:
        pass


# ---------------- main flow (= VBA Main) ----------------
class _Progress(object):
    """Fusion progress dialog wrapper: the user sees each step instead of waiting for the browser."""
    def __init__(self, title):
        self.dlg = None
        try:
            self.dlg = _ui.createProgressDialog()
            self.dlg.isCancelButtonShown = False
            self.dlg.show("LaserCAM - sending " + title, "Starting...", 0, 100, 0)
        except Exception:
            self.dlg = None

    def __call__(self, percent, text):
        if not self.dlg:
            return
        try:
            self.dlg.message = text
            self.dlg.progressValue = max(0, min(100, int(percent)))
            adsk.doEvents()
        except Exception:
            pass

    def hide(self):
        if self.dlg:
            try:
                self.dlg.hide()
            except Exception:
                pass


def component_number(comp):
    """Odoo number = component Part Number. Fusion defaults it to the component name, and an unsaved document
    gets a date/time stamp - both count as "not set": ask, suggesting the component NAME (never the stamp)."""
    number = (comp.partNumber or "").strip()
    stamp = re.match(r"^\d{4}-\d{2}-\d{2}", number) is not None
    if not number or number == comp.name or stamp:
        default = clean_title(comp.name) if (stamp or not number) else number
        if re.match(r"^\d{4}-\d{2}-\d{2}", default):
            default = ""
        number = ask_text("The component Part Number is not set. Enter the Odoo number (Internal Reference):", default)
        if not number:
            return ""
        comp.partNumber = number
    return number


def send_to_odoo(pick=None, pending=None):
    """pick: None (find by number / create) or {"mode": product|template|new, "pid", "tmpl", "code"} from the picker."""
    if pending:
        s, design, comp, body, title, number = pending["s"], pending["design"], pending["comp"], pending["body"], pending["title"], pending["number"]
    else:
        s = load_settings()
        design = get_design()
        if not design:
            return
        comp, body = find_sheet_metal(design)
        if not comp:
            return
        title = clean_title(comp.name)
        number = component_number(comp)
        if not number:
            return
    progress = _Progress(title)
    try:
        if pick and pick.get("action") == "odoo":
            _direct_send(s, design, comp, body, title, number, progress, pick)
        else:
            _send_steps(s, design, comp, body, title, number, progress, pick)
    finally:
        progress.hide()


def _resolve_product(s, comp, title, number, pick):
    """Odoo product from the picker result: (pid, tmpl, number, created, template_tmpl)."""
    created = False
    template_tmpl = 0
    tmpl = 0
    if pick:
        pid = int(pick.get("pid") or 0) if pick.get("mode") == "product" else odoo_find_product(s, number)
        if pick.get("mode") == "product":
            tmpl = int(pick.get("tmpl") or 0)
            code = (pick.get("code") or "").strip()
            if code and code != number:
                number = code
                comp.partNumber = number
        elif pick.get("mode") == "template":
            template_tmpl = int(pick.get("tmpl") or 0)
        if pid == 0:
            pid = odoo_create_product(s, number, title)
            created = True
    else:
        pid = odoo_find_product(s, number)
        if pid == 0:
            if not ask_yes_no("No Odoo product with number \"%s\". Create it?" % number):
                return 0, 0, number, False, 0
            pid = odoo_create_product(s, number, title)
            created = True
    if pid and not tmpl:
        tmpl = odoo_template_of(s, pid)
    return pid, tmpl, number, created, template_tmpl


def _direct_send(s, design, comp, body, title, number, progress, pick):
    """Files straight to the Odoo product - no nesting, no LaserCAM job."""
    progress(10, "Looking up the Odoo product %s..." % number)
    try:
        pid, tmpl, number, created, _ = _resolve_product(s, comp, title, number, pick)
    except Exception as e:
        err("Odoo error: %s" % e)
        return
    if not pid:
        err("Could not find or create the Odoo product.")
        return
    files = pick.get("files") or {}
    attached = upload_attachments(s, design, comp, body, tmpl, number, progress, files.get("dxf"), files.get("step"), files.get("cad"))
    progress(100, "Done.")
    progress.hide()
    msg("Odoo product %s: %s\n\nAttached: %s" % ("CREATED" if created else "updated", number, attached or "nothing (no file could be exported)"))


def _send_steps(s, design, comp, body, title, number, progress, pick=None):
    progress(3, "Reading the component...")

    # 1. Flat pattern DXF (created first: the thickness fallback measures it)
    progress(15, "Creating the flat pattern...")
    try:
        fp = ensure_flat_pattern(comp, body)
    except Exception as e:
        err("Flat pattern failed (is the body sheet metal with a planar face?): %s" % e)
        return
    fusion_thick = sheet_thickness_mm(comp, fp)

    # Odoo product first: its BOM sheet line (or the template product's) defines material + thickness + sheet.
    progress(25, "Looking up the Odoo product %s..." % number)
    try:
        pid, tmpl, number, created, template_tmpl = _resolve_product(s, comp, title, number, pick)
        if pid == 0:
            if pick:
                err("Could not find or create the Odoo product.")
            return
        sheet, sheet_source = None, ""
        if s.get("USE_ODOO_SHEET"):
            sheet = odoo_sheet_of(s, tmpl)
            if sheet:
                sheet_source = "product BOM"
            elif template_tmpl and odoo_sheet_of(s, template_tmpl):
                sheet = odoo_sheet_of(s, template_tmpl)
                sheet_source = "chosen template"
            elif s.get("TEMPLATE_NUMBER"):
                tp = odoo_find_product(s, s["TEMPLATE_NUMBER"])
                if tp > 0:
                    sheet = odoo_sheet_of(s, odoo_template_of(s, tp))
                    if sheet:
                        sheet_source = "template " + s["TEMPLATE_NUMBER"]
    except Exception as e:
        err("Odoo error: %s" % e)
        return

    sheet_product_id = 0
    if sheet:
        material = sheet["name"]
        thick = sheet["thick"]
        sheet_product_id = sheet.get("productId", 0)
        if fusion_thick > 0 and abs(fusion_thick - thick) > 0.05:
            if not ask_yes_no("Fusion thickness %s mm differs from the Odoo sheet %s mm (%s).\nUse the Odoo sheet thickness?"
                              % (num_str(fusion_thick), num_str(thick), sheet["name"])):
                thick = fusion_thick
    else:
        thick = fusion_thick
        if thick <= 0:
            thick = to_float(ask_text("Sheet metal thickness not found. Enter thickness in mm:", "2"))
            if thick <= 0:
                return
        material = body_material_name(body)
        if not material:
            material = ask_text("The body has no material. Enter the material name from the LaserCAM database:", "Carbon steel")
            if not material:
                return
        sheet_source = "Fusion"
    gas = s.get("DEFAULT_GAS") or "O2"
    sh_w = sheet["w"] if sheet and sheet["w"] > 0 else to_float(s.get("SHEET_W"), 1250)
    sh_h = sheet["h"] if sheet and sheet["h"] > 0 else to_float(s.get("SHEET_H"), 2500)

    progress(40, "Exporting the flat pattern DXF...")
    dxf_path = os.path.join(tempfile.gettempdir(), "lasercam_" + safe_name(number) + ".dxf")
    try:
        if not export_flat_dxf(design, fp, dxf_path):
            err("Flat pattern DXF export failed (does the component have a flat pattern?).")
            return
    except Exception as e:
        err("Flat pattern DXF export failed: %s" % e)
        return
    with open(dxf_path, "r", encoding="utf-8", errors="replace") as f:
        dxf = f.read()

    # 2. LaserCAM
    body_json = {
        "parts": [{"id": number, "name": title, "dxf": dxf, "quantity": to_int(s.get("QUANTITY"), 1),
                   "thicknessMm": thick, "material": material, "gas": gas, "odooProductId": pid}],
        "sheet": {"width": sh_w, "height": sh_h, "margin": to_float(s.get("SHEET_MARGIN"), 5)},
        "spacing": to_float(s.get("SPACING"), 5),
        "pricePerKg": to_float(s.get("PRICE_PER_KG"), 0),
        "fast": True,
    }
    if s.get("OPEN_APP"):
        job = dict(body_json)
        job["number"] = number
        job["source"] = "Fusion"
        if s.get("SEND_ODOO_FROM_APP"):
            odoo = {"url": s["ODOO_URL"], "apiKey": s.get("ODOO_API_KEY") or ""}
            if s.get("ODOO_DB"):
                odoo["db"] = s["ODOO_DB"]
            job["odoo"] = odoo
        if sheet_product_id > 0:
            job["sheetProductId"] = sheet_product_id
        progress(55, "Creating the LaserCAM job (uploading the DXF)...")
        status, resp = lc_post(s, "/job", job)
        if status is None:
            return
        if status == 403:
            try:
                e = json.loads(resp).get("error") or ""
            except Exception:
                e = resp[:200]
            err("Your LaserCAM plan does not include this: %s (laser.ucase.eu)" % (e[5:].strip() if e.startswith("plan") else e))
            return
        if status != 200:
            err("LaserCAM /api/job error %s: %s" % (status, resp[:300]))
            return
        try:
            data = json.loads(resp)
        except Exception:
            data = {}
        job_id, job_url = data.get("id") or "", data.get("url") or ""
        if not job_id or not job_url:
            err("LaserCAM did not return a job id.")
            return
        progress(65, "Opening LaserCAM in the browser...")
        webbrowser.open(job_url)
        progress(100, "Done.")
        progress.hide()
        msg("LaserCAM opened in the browser with %s (%s, %s mm, sheet %s x %s).\n\n%s"
            % (number, material, num_str(thick), num_str(sh_w), num_str(sh_h),
               "Nest it there and click \"Save to Odoo\" - weight, DXF, BOM sheet quantity and laser time go straight to Odoo."
               if s.get("SEND_ODOO_FROM_APP") else
               "Nest it there and click \"Save to Odoo\"; LaserCAM writes the result to Odoo."))
        return

    # Silent quote
    progress(55, "Computing the quote on the LaserCAM server...")
    status, resp = lc_post(s, "/quote", body_json)
    if status is None:
        return
    if status != 200:
        err("LaserCAM /api/quote error %s: %s" % (status, resp[:300]))
        return
    try:
        data = json.loads(resp)
    except Exception:
        data = {}
    part = (data.get("parts") or [{}])[0]
    net_kg = part.get("netKgPerPart", 0)
    used_kg = part.get("usedKgPerPart", 0)
    secs = part.get("secPerPart", 0)
    price = part.get("pricePerPart", 0)
    per_sheet = part.get("partsPerSheet", 0)
    algo = part.get("algo") or ""
    warnings = part.get("warnings") or []
    warn_text = ", ".join(str(w) for w in warnings) if isinstance(warnings, list) else str(warnings)

    # 3. Odoo: write results
    try:
        odoo_write(s, "product.product", pid, {"weight": net_kg})
        quote = {"netKg": net_kg, "usedKg": used_kg, "sec": secs, "price": price, "partsPerSheet": per_sheet,
                 "thicknessMm": thick, "material": material, "gas": gas, "algo": algo,
                 "date": datetime.datetime.now().strftime("%Y-%m-%d %H:%M")}
        try:
            odoo_write(s, "product.template", tmpl, {"x_studio_lasercam_quote": json.dumps(quote)})   # field may not exist
        except Exception:
            pass
        odoo_attach(s, "product.template", tmpl, safe_name(number) + ".dxf", dxf_path)
    except Exception as e:
        err("Odoo write error: %s" % e)
        return

    # 4. Into the component - visible in Fusion too (attributes, best effort)
    set_attributes(comp, {"LC_NetKg": net_kg, "LC_UsedKg": used_kg, "LC_CutSec": secs, "LC_Price": price})

    msg("Odoo product %s: %s\n\nThickness %s mm, %s, %s  [%s]\nSheet %s x %s mm\nNet weight: %s kg\nWith waste: %s kg\n"
        "Cutting time: %s s\nPrice: %s EUR\nParts per sheet: %s (%s)%s"
        % ("CREATED" if created else "updated", number, num_str(thick), material, gas, sheet_source,
           num_str(sh_w), num_str(sh_h), net_kg, used_kg, secs, price, per_sheet, algo,
           ("\n\nWarning: " + warn_text) if warn_text else ""), "LaserCAM -> Odoo")


# ---------------- commands ----------------
# ---------------- Odoo product / template picker (same list as the Odoo Connector) ----------------
_pending = {}   # data gathered in commandCreated, used by the execute handler

_SHEET_RE = re.compile(r"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)")


def picker_query(s, q, limit=60):
    """Odoo products (with or without BOM) + sheet line, laser work center, DXF flag."""
    domain = []
    if q:
        domain = ["|", ["default_code", "ilike", q], ["name", "ilike", q]]
    prods = odoo_call(s, "product.product", "search_read", {"domain": domain, "fields": ["id", "default_code", "name", "product_tmpl_id"], "limit": limit})
    rows = []
    for p in prods:
        rows.append({"pid": int(p["id"]), "tmpl": m2o_id(p.get("product_tmpl_id")), "code": (p.get("default_code") or "") if p.get("default_code") else "",
                     "name": p.get("name") or "", "bom": False, "sheet": "", "wc": "", "dxf": False})
    rows.sort(key=lambda r: r["code"])
    tmpl_ids = sorted(set(r["tmpl"] for r in rows if r["tmpl"]))
    if not tmpl_ids:
        return rows
    boms = odoo_call(s, "mrp.bom", "search_read", {"domain": [["product_tmpl_id", "in", tmpl_ids]], "fields": ["id", "product_tmpl_id"], "limit": 500})
    bom_ids = [int(b["id"]) for b in boms]
    lines = odoo_call(s, "mrp.bom.line", "search_read", {"domain": [["bom_id", "in", bom_ids]], "fields": ["bom_id", "product_id"], "limit": 5000}) if bom_ids else []
    ops = odoo_call(s, "mrp.routing.workcenter", "search_read", {"domain": [["bom_id", "in", bom_ids]], "fields": ["bom_id", "workcenter_id"], "limit": 5000}) if bom_ids else []
    atts = odoo_call(s, "ir.attachment", "search_read", {"domain": [["res_model", "=", "product.template"], ["res_id", "in", tmpl_ids], ["name", "=ilike", "%.dxf"]], "fields": ["res_id"], "limit": 5000})
    dxf_tmpls = set(int(a["res_id"]) for a in atts)
    for r in rows:
        bom = next((b for b in boms if m2o_id(b.get("product_tmpl_id")) == r["tmpl"]), None)
        if bom:
            r["bom"] = True
            bid = int(bom["id"])
            for l in lines:
                if m2o_id(l.get("bom_id")) == bid and _SHEET_RE.search(m2o_name(l.get("product_id"))):
                    r["sheet"] = m2o_name(l.get("product_id"))
                    break
            op = next((o for o in ops if m2o_id(o.get("bom_id")) == bid and re.search("la[sz]er", m2o_name(o.get("workcenter_id")), re.I)), None) or \
                 next((o for o in ops if m2o_id(o.get("bom_id")) == bid), None)
            if op:
                r["wc"] = m2o_name(op.get("workcenter_id"))
        r["dxf"] = r["tmpl"] in dxf_tmpls
    return rows


def m2o_id(v):
    if isinstance(v, (list, tuple)) and v:
        return int(v[0])
    return int(v) if isinstance(v, int) else 0


def m2o_name(v):
    if isinstance(v, (list, tuple)) and len(v) > 1:
        return str(v[1] or "")
    return ""


def _fill_table(inputs, table, rows, select_pid=0):
    table.clear()
    for i, r in enumerate(rows):
        cells = [r["code"], r["name"], r["sheet"] or ("BOM" if r["bom"] else "-"), "yes" if r["dxf"] else "-"]
        for c, text in enumerate(cells):
            tb = inputs.addTextBoxCommandInput("cell_%d_%d" % (i, c), "", text, 1, True)
            table.addCommandInput(tb, i, c)
        if select_pid and r["pid"] == select_pid:
            table.selectedRow = i
    if rows and table.selectedRow < 0 and len(rows) == 1:
        table.selectedRow = 0


class PickerInputChangedHandler(adsk.core.InputChangedEventHandler):
    def notify(self, args):
        try:
            if args.input.id != "search_btn":
                return
            inputs = args.inputs
            q = (inputs.itemById("search").value or "").strip()
            rows = picker_query(_pending["s"], q)
            _pending["rows"] = rows
            _fill_table(inputs, inputs.itemById("products"), rows)
            inputs.itemById("hint").text = "%d products%s" % (len(rows), " (first 60 - narrow the search)" if len(rows) >= 60 else "")
        except Exception:
            _ui.messageBox("LaserCAM failed:\n" + traceback.format_exc())


class PickerExecuteHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        try:
            inputs = args.command.commandInputs
            mode_in = inputs.itemById("mode")
            sel = mode_in.selectedItem.name if mode_in and mode_in.selectedItem else "This is the product"
            mode = "product" if sel.startswith("This is") else ("template" if sel.startswith("Use as") else "new")
            act_in = inputs.itemById("action")
            act = act_in.selectedItem.name if act_in and act_in.selectedItem else ""
            action = "odoo" if act.startswith("Send files") else "lasercam"
            files = {"dxf": bool(inputs.itemById("f_dxf").value), "step": bool(inputs.itemById("f_step").value), "cad": bool(inputs.itemById("f_cad").value)}
            if action == "odoo" and not any(files.values()):
                err("Tick at least one file to send.")
                return
            s = _pending["s"]
            s["SEND_MODE"] = action
            s["ATTACH_DXF"], s["ATTACH_STEP"], s["ATTACH_CAD"] = files["dxf"], files["step"], files["cad"]
            try:
                save_settings(s)
            except Exception:
                pass
            pick = {"mode": mode, "pid": 0, "tmpl": 0, "code": "", "action": action, "files": files}
            if mode in ("product", "template"):
                table = inputs.itemById("products")
                row = table.selectedRow if table else -1
                rows = _pending.get("rows") or []
                if row < 0 or row >= len(rows):
                    err("Select a product row, or choose \"New product (no template)\".")
                    return
                r = rows[row]
                pick.update({"pid": r["pid"], "tmpl": r["tmpl"], "code": r["code"]})
            send_to_odoo(pick, _pending)
        except Exception:
            _ui.messageBox("LaserCAM failed:\n" + traceback.format_exc())


class SendExecuteHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        try:
            send_to_odoo(None, _pending if _pending.get("number") else None)
        except Exception:
            _ui.messageBox("LaserCAM failed:\n" + traceback.format_exc())


class SendCreatedHandler(adsk.core.CommandCreatedEventHandler):
    def notify(self, args):
        try:
            cmd = args.command
            _pending.clear()
            s = load_settings()
            if not odoo_ready(s):
                return
            design = get_design()
            if not design:
                return
            comp, body = find_sheet_metal(design)
            if not comp:
                return
            title = clean_title(comp.name)
            number = component_number(comp)
            if not number:
                return
            pid = 0
            try:
                pid = odoo_find_product(s, number)
            except Exception as e:
                err("Odoo error: %s" % e)
                return
            _pending.update({"s": s, "design": design, "comp": comp, "body": body, "title": title, "number": number, "pid": pid})
            # The Send dialog (always): what to do + files + the Odoo product / template list
            cmd.okButtonText = "Send"
            cmd.setDialogMinimumSize(720, 560)
            inputs = cmd.commandInputs
            action = inputs.addRadioButtonGroupCommandInput("action", "What to do")
            direct = (s.get("SEND_MODE") == "odoo")
            action.listItems.add("Send to LaserCAM: nesting, weight and time; \"Save to Odoo\" there", not direct)
            action.listItems.add("Send files straight to the Odoo product (no nesting)", direct)
            inputs.addBoolValueInput("f_dxf", "DXF flat pattern (direct send)", True, "", bool(s.get("ATTACH_DXF", True)))
            inputs.addBoolValueInput("f_step", "STEP (direct send)", True, "", bool(s.get("ATTACH_STEP", True)))
            inputs.addBoolValueInput("f_cad", "Fusion archive .f3d (direct send)", True, "", bool(s.get("ATTACH_CAD", True)))
            head = ("Odoo product \"%s\" found (selected). Confirm it, or pick another row." % number) if pid > 0 else \
                   ("No Odoo product has the Internal Reference \"%s\" (part: %s). Pick the row that IS this part, or a TEMPLATE for a new product." % (number, title))
            inputs.addTextBoxCommandInput("head", "", head, 3, True)
            m = re.search(r"\d{3,}", number)
            inputs.addStringValueInput("search", "Search code / name", m.group(0) if m else number)
            inputs.addBoolValueInput("search_btn", "Search", False, "", False)
            table = inputs.addTableCommandInput("products", "Odoo products", 4, "2:6:5:1")
            table.maximumVisibleRows = 12
            rows = picker_query(s, m.group(0) if m else number)
            _pending["rows"] = rows
            _fill_table(inputs, table, rows, pid)
            inputs.addTextBoxCommandInput("hint", "", "%d products. Columns: code | product | sheet (raw material) | DXF" % len(rows), 1, True)
            mode = inputs.addRadioButtonGroupCommandInput("mode", "The selected row is")
            mode.listItems.add("This is the product (update it; its reference goes into the Part Number)", True)
            mode.listItems.add("Use as template (new product; material, thickness and sheet from its BOM)", False)
            mode.listItems.add("New product (no template)", False)
            h1 = PickerInputChangedHandler()
            cmd.inputChanged.add(h1)
            h2 = PickerExecuteHandler()
            cmd.execute.add(h2)
            _handlers.extend([h1, h2])
        except Exception:
            _ui.messageBox("LaserCAM failed:\n" + traceback.format_exc())


SETTINGS_FIELDS = [
    # (key, label, kind) kind: str | secret | bool | num | int
    ("LC_API", "LaserCAM API URL", "str"),
    ("LC_PID", "LaserCAM contact id (LC_PID)", "int"),
    ("LC_KEY", "LaserCAM key (LC_KEY)", "secret"),
    ("ODOO_URL", "Odoo URL", "str"),
    ("ODOO_API_KEY", "Odoo API key", "secret"),
    ("ODOO_DB", "Odoo database (empty = default)", "str"),
    ("TEMPLATE_NUMBER", "Template product number", "str"),
    ("USE_ODOO_SHEET", "Use Odoo BOM sheet line", "bool"),
    ("ATTACH_DXF", "Send dialog default: DXF flat pattern", "bool"),
    ("ATTACH_STEP", "Send dialog default: STEP export", "bool"),
    ("ATTACH_CAD", "Send dialog default: Fusion archive (.f3d)", "bool"),
    ("DEFAULT_GAS", "Default gas (O2 / N2 / air)", "str"),
    ("SHEET_W", "Sheet width mm", "num"),
    ("SHEET_H", "Sheet height mm", "num"),
    ("SHEET_MARGIN", "Sheet margin mm", "num"),
    ("SPACING", "Spacing mm", "num"),
    ("PRICE_PER_KG", "Price per kg EUR", "num"),
    ("QUANTITY", "Quantity", "int"),
    ("OPEN_APP", "Open LaserCAM in the browser", "bool"),
    ("SEND_ODOO_FROM_APP", "Save to Odoo from LaserCAM app", "bool"),
]


class SettingsExecuteHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        try:
            s = load_settings()
            inputs = args.command.commandInputs
            for key, _label, kind in SETTINGS_FIELDS:
                inp = inputs.itemById(key)
                if not inp:
                    continue
                if kind == "bool":
                    s[key] = bool(inp.value)
                elif kind == "secret":
                    v = (inp.value or "").strip()
                    if v:                      # empty = keep the stored key
                        s[key] = v
                elif kind == "num":
                    s[key] = to_float(inp.value, DEFAULTS[key])
                elif kind == "int":
                    s[key] = to_int(inp.value, DEFAULTS[key])
                else:
                    s[key] = (inp.value or "").strip()
            save_settings(s)
            msg("Settings saved to\n%s\nLC_KEY %s, Odoo API key %s" % (SETTINGS_PATH, mask(s["LC_KEY"]), mask(s["ODOO_API_KEY"])))
        except Exception:
            _ui.messageBox("LaserCAM Settings failed:\n" + traceback.format_exc())


class SettingsCreatedHandler(adsk.core.CommandCreatedEventHandler):
    def notify(self, args):
        try:
            cmd = args.command
            cmd.okButtonText = "Save"
            inputs = cmd.commandInputs
            s = load_settings()
            inputs.addTextBoxCommandInput("LC_info", "", "Stored in " + SETTINGS_PATH, 2, True)
            for key, label, kind in SETTINGS_FIELDS:
                if kind == "bool":
                    inputs.addBoolValueInput(key, label, True, "", bool(s.get(key)))
                elif kind == "secret":
                    inputs.addStringValueInput(key, label + " [now " + mask(s.get(key)) + "; empty = keep]", "")
                elif kind in ("num", "int"):
                    inputs.addStringValueInput(key, label, num_str(s.get(key, DEFAULTS[key])))
                else:
                    inputs.addStringValueInput(key, label, str(s.get(key) or ""))
            h = SettingsExecuteHandler()
            cmd.execute.add(h)
            _handlers.append(h)
        except Exception:
            _ui.messageBox("LaserCAM Settings failed:\n" + traceback.format_exc())


def _add_to_tabs(cmd_defs):
    """Panel 'LaserCAM' on the SOLID tab (and SHEET METAL tab when present) of the DESIGN workspace."""
    ws = _ui.workspaces.itemById(WORKSPACE_ID)
    if not ws:
        return
    for tab_id in TAB_IDS:
        tab = ws.toolbarTabs.itemById(tab_id)
        if not tab:
            continue
        panel_id = PANEL_ID if tab_id == TAB_IDS[0] else PANEL_ID + "_" + tab_id
        panel = tab.toolbarPanels.itemById(panel_id)
        if not panel:
            panel = tab.toolbarPanels.add(panel_id, PANEL_NAME)
        for cd in cmd_defs:
            ctrl = panel.controls.itemById(cd.id)
            if not ctrl:
                ctrl = panel.controls.addCommand(cd)
            if ctrl and cd.id == CMD_SEND_ID:
                ctrl.isPromoted = True
                ctrl.isPromotedByDefault = True


def _remove_from_tabs():
    ws = _ui.workspaces.itemById(WORKSPACE_ID)
    if not ws:
        return
    for tab_id in TAB_IDS:
        tab = ws.toolbarTabs.itemById(tab_id)
        if not tab:
            continue
        panel_id = PANEL_ID if tab_id == TAB_IDS[0] else PANEL_ID + "_" + tab_id
        panel = tab.toolbarPanels.itemById(panel_id)
        if not panel:
            continue
        for cid in (CMD_SEND_ID, CMD_SETTINGS_ID):
            ctrl = panel.controls.itemById(cid)
            if ctrl:
                ctrl.deleteMe()
        panel.deleteMe()


def run(context):
    global _app, _ui
    try:
        _app = adsk.core.Application.get()
        _ui = _app.userInterface
        load_settings()   # creates the JSON with defaults on first run

        defs = _ui.commandDefinitions
        for cid in (CMD_SEND_ID, CMD_SETTINGS_ID):
            old = defs.itemById(cid)
            if old:
                old.deleteMe()
        send_def = defs.addButtonDefinition(
            CMD_SEND_ID, "Send to Odoo",
            "LaserCAM: export the sheet metal flat pattern DXF, find/create the Odoo product by Part Number "
            "and open LaserCAM (or quote silently) - see LaserCAM Settings.", RESOURCES)
        settings_def = defs.addButtonDefinition(
            CMD_SETTINGS_ID, "LaserCAM Settings",
            "LaserCAM / Odoo credentials, sheet size, spacing, price per kg, modes.", RESOURCES)
        h1 = SendCreatedHandler()
        send_def.commandCreated.add(h1)
        h2 = SettingsCreatedHandler()
        settings_def.commandCreated.add(h2)
        _handlers.extend([h1, h2])
        _add_to_tabs([send_def, settings_def])
    except Exception:
        if _ui:
            _ui.messageBox("LaserCAM add-in failed to start:\n" + traceback.format_exc())


def stop(context):
    try:
        _remove_from_tabs()
        defs = _ui.commandDefinitions
        for cid in (CMD_SEND_ID, CMD_SETTINGS_ID):
            cd = defs.itemById(cid)
            if cd:
                cd.deleteMe()
        del _handlers[:]
    except Exception:
        if _ui:
            _ui.messageBox("LaserCAM add-in failed to stop:\n" + traceback.format_exc())
