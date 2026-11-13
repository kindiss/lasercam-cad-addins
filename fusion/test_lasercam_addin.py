# -*- coding: utf-8 -*-
"""Fusion priedo (fusion/LaserCAM/LaserCAM.py) testai be Fusion: `adsk` pakeistas tuščiais moduliais,
tinklas (`http`), langai (`msg` / `warn` / `err` / `ask_text`), naršyklė ir Fusion geometrija - netikri.

Tikrinama: kelias BE Odoo (tik LaserCAM) šalia kelio SU Odoo.
Paleidimas (iš LaserCAM/):  python fusion/test_lasercam_addin.py
"""
import importlib.util
import json
import os
import sys
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ADDIN = os.path.join(HERE, "LaserCAM", "LaserCAM.py")


def _fake_adsk():
    adsk = types.ModuleType("adsk")
    core = types.ModuleType("adsk.core")
    fusion = types.ModuleType("adsk.fusion")
    for name in ("CommandEventHandler", "CommandCreatedEventHandler", "InputChangedEventHandler"):
        setattr(core, name, type(name, (object,), {}))
    adsk.core, adsk.fusion = core, fusion
    sys.modules["adsk"], sys.modules["adsk.core"], sys.modules["adsk.fusion"] = adsk, core, fusion


def load_addin():
    _fake_adsk()
    spec = importlib.util.spec_from_file_location("lasercam_addin_under_test", ADDIN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class Comp(object):
    def __init__(self, name="Bracket v3", part_number="LC-100"):
        self.name = name
        self.partNumber = part_number


class Inputs(object):
    """Komandos langas: skaičiuojam, kiek laukų pridėta (0 = lango nėra, komanda vykdoma iškart)."""
    def __init__(self):
        self.added = []
        self.radio = {}            # pasirinkimų grupės id -> [(tekstas, ar pažymėtas)]

    def __getattr__(self, name):
        if not name.startswith("add"):
            raise AttributeError(name)

        def add(*a, **k):
            self.added.append((name, a))
            items = self.radio.setdefault(a[0], []) if name == "addRadioButtonGroupCommandInput" else None
            add_item = (lambda text, selected, *x: items.append((text, selected))) if items is not None else (lambda *x, **y: None)
            return types.SimpleNamespace(listItems=types.SimpleNamespace(add=add_item), maximumVisibleRows=0,
                                         selectedRow=-1, clear=lambda: None, addCommandInput=lambda *x: None)
        return add


class Event(object):
    def __init__(self):
        self.handlers = []

    def add(self, h):
        self.handlers.append(h)


class Cmd(object):
    def __init__(self):
        self.commandInputs = Inputs()
        self.execute = Event()
        self.inputChanged = Event()
        self.okButtonText = ""

    def setDialogMinimumSize(self, w, h):
        pass


class World(object):
    """Priedas su netikra aplinka. `settings` - kas būtų fusion-settings.json."""
    JOB_URL = "https://laser.ucase.eu/app?lcjob=JOB123"

    def __init__(self, settings, comp=None, job_status=200, job_resp=None):
        m = self.m = load_addin()
        self.comp = comp or Comp()
        self.settings = dict(m.DEFAULTS)
        self.settings.update(settings)
        self.http_calls = []       # (method, url, body)
        self.odoo_calls = []       # (model, method)
        self.dialogs = []          # (kind, text)
        self.prompts = []          # ask_text klausimai
        self.opened = []           # naršyklėje atidaryti adresai
        self.settings_opened = 0
        self.attributes = None
        self.job_status = job_status
        self.job_resp = job_resp
        w = self

        m.load_settings = lambda: dict(w.settings)
        self.saved = None          # paskutinį kartą įrašyti nustatymai
        m.save_settings = lambda s: setattr(w, "saved", dict(s))

        def http(method, url, body=None, headers=None, timeout=600):
            w.http_calls.append((method, url, body))
            if url.endswith("/session"):
                return 200, json.dumps({"token": "TOKEN"})
            if url.endswith("/job"):
                return w.job_status, w.job_resp if w.job_resp is not None else json.dumps({"id": "JOB123", "url": w.JOB_URL})
            if url.endswith("/quote"):
                return 200, json.dumps({"parts": [{"netKgPerPart": 1.25, "usedKgPerPart": 1.6, "secPerPart": 42,
                                                    "pricePerPart": 3.1, "partsPerSheet": 12, "algo": "grid"}]})
            raise AssertionError("netikėta užklausa: %s %s" % (method, url))
        m.http = http

        def odoo_call(s, model, method, kwargs):
            w.odoo_calls.append((model, method))
            return []
        m.odoo_call = odoo_call

        m.msg = lambda text, title="LaserCAM", icon=None: w.dialogs.append(("msg", text))
        m.warn = lambda text, title="LaserCAM": w.dialogs.append(("warn", text))
        m.err = lambda text, title="LaserCAM": w.dialogs.append(("err", text))
        m.ask_yes_no = lambda text, title="LaserCAM": True

        def ask_text(prompt, default=""):
            w.prompts.append(prompt)
            return default or "ASKED"
        m.ask_text = ask_text

        m.webbrowser = types.SimpleNamespace(open=lambda url: w.opened.append(url))
        m.get_design = lambda: object()
        m.find_sheet_metal = lambda design: (w.comp, object())
        m.ensure_flat_pattern = lambda comp, body: object()
        m.sheet_thickness_mm = lambda comp, fp: 2.0
        m.body_material_name = lambda body: "Steel"

        def export_flat_dxf(design, fp, path):
            with open(path, "w", encoding="utf-8") as f:
                f.write("0\nSECTION\n0\nENDSEC\n0\nEOF\n")
            return True
        m.export_flat_dxf = export_flat_dxf

        def set_attributes(comp, values):
            w.attributes = values
        m.set_attributes = set_attributes

        class Progress(object):
            def __init__(self, title):
                pass

            def __call__(self, pct, text):
                pass

            def hide(self):
                pass
        m._Progress = Progress

        def settings_cmd():
            w.settings_opened += 1
        m._ui = types.SimpleNamespace(
            commandDefinitions=types.SimpleNamespace(itemById=lambda cid: types.SimpleNamespace(execute=settings_cmd)),
            messageBox=lambda text, *a: w.dialogs.append(("crash", text)))

    def click_send(self):
        """Mygtukas „Send": commandCreated, o jei langas be laukų - Fusion iškart kviečia execute."""
        cmd = self.cmd = Cmd()
        self.m.SendCreatedHandler().notify(types.SimpleNamespace(command=cmd))
        if not cmd.commandInputs.added:
            for h in cmd.execute.handlers:
                h.notify(types.SimpleNamespace(command=cmd))
        return self

    def confirm(self, action_name, mode_name="This is the product"):
        """Lange „Send" pažymėtas veiksmas `action_name`, sąraše nieko nepasirinkta, spaudžiama OK."""
        radio = {"action": action_name, "mode": mode_name}

        def item(i):
            if i in radio:
                return types.SimpleNamespace(selectedItem=types.SimpleNamespace(name=radio[i]))
            if i == "products":
                return types.SimpleNamespace(selectedRow=-1)
            return types.SimpleNamespace(value=True)
        cmd = types.SimpleNamespace(commandInputs=types.SimpleNamespace(itemById=item))
        for h in self.cmd.execute.handlers:
            h.notify(types.SimpleNamespace(command=cmd))
        return self

    def posts(self, suffix):
        return [b for (meth, url, b) in self.http_calls if url.endswith(suffix)]

    def texts(self, kind):
        return [t for (k, t) in self.dialogs if k == kind]


LC = {"LC_PID": 7, "LC_KEY": "secret-key"}
ODOO = {"ODOO_URL": "https://x.odoo.com", "ODOO_API_KEY": "odoo-key"}


class WithoutOdoo(unittest.TestCase):
    def test_job_goes_to_lasercam_and_odoo_is_never_called(self):
        w = World(LC).click_send()
        self.assertEqual(w.texts("crash"), [])
        self.assertEqual(w.odoo_calls, [])
        self.assertEqual(w.cmd.commandInputs.added, [], "be Odoo produktų sąrašo lango neturi būti")
        jobs = w.posts("/job")
        self.assertEqual(len(jobs), 1)
        job = jobs[0]
        self.assertNotIn("odoo", job)
        self.assertEqual(job["source"], "Fusion")
        self.assertEqual(job["number"], "LC-100")
        part = job["parts"][0]
        self.assertNotIn("odooProductId", part)
        self.assertEqual((part["id"], part["material"], part["thicknessMm"]), ("LC-100", "Steel", 2.0))
        self.assertIn("EOF", part["dxf"])
        self.assertEqual(w.opened, [World.JOB_URL])
        self.assertEqual(w.texts("err") + w.texts("warn"), [])
        self.assertEqual(len(w.texts("msg")), 1)
        self.assertNotIn("Odoo", w.texts("msg")[0], "be Odoo naudotojui nesiūlom „Save to Odoo“")

    def test_part_number_is_not_asked_for_and_not_changed(self):
        for part_number in ("Bracket v3", "", "2026-11-12 10.33.01"):   # = pavadinimas, tuščias, neišsaugoto dokumento žyma
            comp = Comp("Bracket v3", part_number)
            w = World(LC, comp).click_send()
            self.assertEqual(w.prompts, [], "Part Number be Odoo nebūtinas (%r)" % part_number)
            self.assertEqual(comp.partNumber, part_number)
            self.assertEqual(w.posts("/job")[0]["parts"][0]["id"], "Bracket")   # pavadinimas be Fusion versijos „v3“

    def test_unsaved_component_without_any_name_still_gets_an_id(self):
        w = World(LC, Comp("2026-11-12 10.33.01", "2026-11-12 10.33.01")).click_send()
        self.assertEqual(w.prompts, [])
        self.assertEqual(w.posts("/job")[0]["parts"][0]["id"], "part")

    def test_quote_without_browser_shows_numbers_and_writes_nothing_to_odoo(self):
        s = dict(LC)
        s["OPEN_APP"] = False
        w = World(s).click_send()
        self.assertEqual(w.texts("crash") + w.texts("err"), [])
        self.assertEqual(len(w.posts("/quote")), 1)
        self.assertEqual(w.posts("/job"), [])
        self.assertEqual(w.odoo_calls, [])
        self.assertEqual(w.opened, [])
        self.assertEqual(w.attributes["LC_NetKg"], 1.25)
        self.assertIn("1.25", w.texts("msg")[0])
        self.assertNotIn("Odoo", w.texts("msg")[0])

    def test_free_plan_gets_a_plain_message(self):
        w = World(LC, job_status=403, job_resp=json.dumps({"error": "plan: sending to LaserCAM (nesting) needs the Basic plan"})).click_send()
        self.assertEqual(w.opened, [])
        self.assertIn("Basic plan", w.texts("err")[0])


class NotSetUp(unittest.TestCase):
    def test_nothing_filled_explains_both_ways_and_opens_settings(self):
        w = World({}).click_send()
        self.assertEqual(w.http_calls, [])
        self.assertEqual(w.odoo_calls, [])
        self.assertEqual(w.settings_opened, 1)
        text = " ".join(w.texts("warn"))
        self.assertIn("LC_KEY", text)
        self.assertIn("ODOO_URL", text)

    def test_half_filled_odoo_is_a_mistake_not_the_lasercam_only_path(self):
        s = dict(LC)
        s["ODOO_URL"] = "https://x.odoo.com"          # raktas pamirštas
        w = World(s).click_send()
        self.assertEqual(w.http_calls, [])
        self.assertEqual(w.settings_opened, 1)
        self.assertIn("ODOO_API_KEY", " ".join(w.texts("warn")))


class WithOdoo(unittest.TestCase):
    def test_odoo_path_is_unchanged_product_lookup_and_picker(self):
        s = dict(LC)
        s.update(ODOO)
        w = World(s).click_send()
        self.assertEqual(w.texts("crash"), [])
        self.assertIn(("product.product", "search_read"), w.odoo_calls)
        self.assertTrue(w.cmd.commandInputs.added, "su Odoo rodomas produktų sąrašo langas")
        self.assertEqual(w.http_calls, [], "kol langas nepatvirtintas, į LaserCAM nesiunčiama")

    def _dialog(self, extra=None):
        s = dict(LC)
        s.update(ODOO)
        s.update(extra or {})
        w = World(s).click_send()
        return w, w.cmd.commandInputs.radio["action"]

    def test_dialog_offers_lasercam_only_next_to_the_two_odoo_ways(self):
        w, actions = self._dialog()
        names = [n for (n, sel) in actions]
        self.assertEqual(len(names), 3)
        self.assertEqual(len([n for n in names if n.startswith("Send to LaserCAM only")]), 1)
        self.assertEqual(len([n for n in names if n.startswith("Send to LaserCAM and Odoo")]), 1)
        self.assertEqual(len([n for n in names if n.startswith("Send files")]), 1)
        self.assertEqual([sel for (n, sel) in actions].count(True), 1)

    def test_lasercam_only_choice_sends_the_job_and_leaves_odoo_alone(self):
        # Odoo adresas ir raktas įrašyti, bet naudotojas lange pasirinko „tik LaserCAM“: produktas nei ieškomas, nei kuriamas.
        w, actions = self._dialog()
        only = [n for (n, sel) in actions if n.startswith("Send to LaserCAM only")][0]
        del w.odoo_calls[:]        # lango atidarymas produktų sąrašui Odoo jau skaitė; po pasirinkimo - nebe
        w.confirm(only)
        self.assertEqual(w.texts("crash") + w.texts("err"), [], "eilutės sąraše rinktis nereikia")
        self.assertEqual(w.odoo_calls, [])
        jobs = w.posts("/job")
        self.assertEqual(len(jobs), 1)
        self.assertNotIn("odoo", jobs[0])
        self.assertNotIn("odooProductId", jobs[0]["parts"][0])
        self.assertEqual(w.opened, [World.JOB_URL])
        self.assertNotIn("Odoo", w.texts("msg")[0])
        self.assertEqual(w.saved["SEND_MODE"], "lasercam-only")

    def test_last_choice_lasercam_only_is_preselected_next_time(self):
        w, actions = self._dialog({"SEND_MODE": "lasercam-only"})
        self.assertEqual([n for (n, sel) in actions if sel][0][:21], "Send to LaserCAM only")

    def test_lasercam_and_odoo_choice_still_needs_a_product_row(self):
        w, actions = self._dialog()
        both = [n for (n, sel) in actions if n.startswith("Send to LaserCAM and Odoo")][0]
        w.confirm(both)
        self.assertIn("Select a product row", " ".join(w.texts("err")))
        self.assertEqual(w.posts("/job"), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
