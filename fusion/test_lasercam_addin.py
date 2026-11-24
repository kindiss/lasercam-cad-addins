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
import tempfile
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
        self.latest = None         # ką atsako /addins/version: (statusas, žodynas) arba None = serveris nepasiekiamas
        self.now = 1800000000.0    # valdomas laikas (sekundės)
        self.asked = []            # ask_yes_no klausimai
        self.answer = True         # ką atsako naudotojas
        w = self
        fd, self.state_path = tempfile.mkstemp(suffix=".json", prefix="lc-update-")
        os.close(fd)
        os.remove(self.state_path)
        m.UPDATE_STATE_PATH = self.state_path
        m._now = lambda: w.now
        m._run_in_background = lambda fn: fn()

        m.load_settings = lambda: dict(w.settings)
        self.saved = None          # paskutinį kartą įrašyti nustatymai
        m.save_settings = lambda s: setattr(w, "saved", dict(s))

        def http(method, url, body=None, headers=None, timeout=600):
            w.http_calls.append((method, url, body))
            if url.endswith("/addins/version"):
                if w.latest is None:
                    return 0, "timeout"
                return w.latest[0], json.dumps(w.latest[1])
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

        def ask_yes_no(text, title="LaserCAM"):
            w.asked.append(text)
            return w.answer
        m.ask_yes_no = ask_yes_no

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


class UpdateCheck(unittest.TestCase):
    """Priedas pasiūlo atsinaujinti, kai serveris žino naujesnę versiją. Klausia tik mūsų serverio, ne dažniau kaip kartą per parą."""
    NEWER = (200, {"version": "99.0.0", "url": "https://github.com/kindiss/lasercam-cad-addins/releases/download/v99.0.0/LaserCAM-Setup-99.0.0.exe"})

    def world(self, latest):
        w = World(LC)
        w.latest = latest
        return w

    def version_calls(self, w):
        return [u for (meth, u, b) in w.http_calls if u.endswith("/addins/version")]

    def test_first_command_only_checks_second_command_offers_the_download(self):
        w = self.world(self.NEWER)
        w.click_send()
        self.assertEqual(len(self.version_calls(w)), 1)
        self.assertEqual(self.version_calls(w)[0], "https://laser.ucase.eu/api/addins/version")
        self.assertEqual(w.asked, [], "pirmą kartą tik sužinoma, klausimas - kitos komandos gale")
        w.click_send()
        self.assertEqual(len(w.asked), 1)
        self.assertIn("99.0.0", w.asked[0])
        self.assertIn(w.m.SUITE_VERSION, w.asked[0])
        self.assertEqual(w.opened[-1], self.NEWER[1]["url"])
        self.assertEqual(len(self.version_calls(w)), 1, "per parą serverio klausiama vieną kartą")

    def test_same_or_older_version_is_never_offered(self):
        w = self.world((200, {"version": "0.0.1", "url": "https://github.com/kindiss/lasercam-cad-addins/releases/latest"}))
        w.click_send().click_send()
        self.assertEqual(w.asked, [])
        w2 = self.world((200, {"version": None}))
        w2.click_send().click_send()
        self.assertEqual(w2.asked, [])

    def test_declined_version_is_not_asked_again_for_a_week(self):
        w = self.world(self.NEWER)
        w.answer = False
        w.click_send().click_send()
        self.assertEqual(len(w.asked), 1)
        self.assertNotIn(self.NEWER[1]["url"], w.opened)
        w.now += 6 * 86400
        w.click_send()
        self.assertEqual(len(w.asked), 1)
        w.now += 2 * 86400
        w.click_send()
        self.assertEqual(len(w.asked), 2)

    def test_server_down_or_old_server_changes_nothing(self):
        for latest in (None, (404, {"error": "not found"}), (200, {"version": "bad"})):
            w = self.world(latest)
            w.click_send().click_send()
            self.assertEqual(w.texts("crash") + w.texts("err"), [])
            self.assertEqual(w.asked, [])
            self.assertEqual(len(w.posts("/job")), 2, "siuntimas veikia kaip visada")

    def test_foreign_download_address_is_replaced_by_our_releases_page(self):
        w = self.world((200, {"version": "99.0.0", "url": "https://evil.example/setup.exe"}))
        w.click_send().click_send()
        self.assertEqual(w.opened[-1], "https://github.com/kindiss/lasercam-cad-addins/releases/latest")

    def test_check_repeats_after_a_day(self):
        w = self.world((200, {"version": "0.0.1", "url": ""}))
        w.click_send()
        w.now += 23 * 3600
        w.click_send()
        self.assertEqual(len(self.version_calls(w)), 1)
        w.now += 2 * 3600
        w.click_send()
        self.assertEqual(len(self.version_calls(w)), 2)

    def test_check_can_be_switched_off_in_settings(self):
        s = dict(LC)
        s["CHECK_UPDATES"] = False
        w = World(s)
        w.latest = self.NEWER
        w.click_send().click_send()
        self.assertEqual(self.version_calls(w), [], "išjungus serverio neklausiama visai")
        self.assertEqual(w.asked, [])
        self.assertEqual(len(w.posts("/job")), 2)

    def test_version_comparison_is_numeric(self):
        m = load_addin()
        self.assertGreater(m.version_cmp("1.3.11", "1.3.9"), 0)
        self.assertEqual(m.version_cmp("1.3.11", "1.3.11"), 0)
        self.assertLess(m.version_cmp("1.3.11", "1.4"), 0)


class NumberIsNeverChangedByTheAddin(unittest.TestCase):
    """Savininkas 2026-11-23: „programa pati neturi keisti, ji turi tik pasakyti kad ne toks numeris". Iki tol,
    pasirinkus Odoo produktą su kitu kodu, priedas tyliai perrašydavo komponento Part Number."""

    PICK = {"mode": "product", "pid": 55, "tmpl": 9, "code": "LC-100-BEND"}

    def test_picking_a_product_with_another_code_leaves_the_part_number_and_says_so(self):
        comp = Comp("Bracket v3", "LC-100")
        w = World(dict(LC, **ODOO), comp)
        pid, tmpl, number, created, template_tmpl = w.m._resolve_product(w.settings, comp, "Bracket", "LC-100", dict(self.PICK))
        self.assertEqual(comp.partNumber, "LC-100", "komponento Part Number nekeičiamas")
        self.assertEqual((pid, tmpl, number, created), (55, 9, "LC-100-BEND", False), "siuntimas eina į pasirinktą produktą")
        told = w.texts("warn") + w.texts("msg")
        self.assertEqual(len(told), 1, "naudotojui pasakoma vieną kartą")
        self.assertIn('"LC-100"', told[0])
        self.assertIn('"LC-100-BEND"', told[0])
        self.assertIn("NOT changed", told[0])

    def test_the_same_code_says_nothing(self):
        comp = Comp("Bracket v3", "LC-100")
        w = World(dict(LC, **ODOO), comp)
        w.m._resolve_product(w.settings, comp, "Bracket", "LC-100", dict(self.PICK, code="LC-100"))
        self.assertEqual(comp.partNumber, "LC-100")
        self.assertEqual(w.texts("warn") + w.texts("msg"), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
