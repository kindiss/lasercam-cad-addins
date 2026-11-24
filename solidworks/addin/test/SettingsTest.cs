// Tests of the add-in settings (src\Settings.cs) that need no SolidWorks: what the Odoo number is taken from.
// Owner 2026-11-24, looking at the settings window: "no, it must be a choice from a list" - one setting with three
// values (file name, title, custom property) instead of a tick box; a settings file written by an older version,
// which had the tick box ODOO_NUMBER_IS_FILE_NAME, must keep its meaning.
using System;
using System.Collections.Generic;
using LaserCAM.SolidWorks;

static class SettingsTest
{
    static int _failed, _run;

    static void Check(string name, bool ok, string detail = "")
    {
        _run++;
        if (!ok) _failed++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail != "" ? "  ->  " + detail : ""));
    }

    static Settings From(Dictionary<string, object> d)
    {
        var s = new Settings();
        s.Apply(d);
        return s;
    }

    static int Main()
    {
        Check("a new settings object takes the number from the custom property, as before", new Settings().NumberFrom == Settings.FromProperty);
        Check("the choice is read from the file", From(new Dictionary<string, object> { { "ODOO_NUMBER_FROM", "title" } }).NumberFrom == Settings.FromTitle);
        Check("old file with the tick box on: the file name", From(new Dictionary<string, object> { { "ODOO_NUMBER_IS_FILE_NAME", true } }).NumberFrom == Settings.FromFileName);
        Check("old file with the tick box off: the custom property", From(new Dictionary<string, object> { { "ODOO_NUMBER_IS_FILE_NAME", false } }).NumberFrom == Settings.FromProperty);
        Check("the new choice wins over the old tick box", From(new Dictionary<string, object> { { "ODOO_NUMBER_IS_FILE_NAME", true }, { "ODOO_NUMBER_FROM", "property" } }).NumberFrom == Settings.FromProperty);
        Check("a value nobody knows is read as the custom property", From(new Dictionary<string, object> { { "ODOO_NUMBER_FROM", "somewhere" } }).NumberFrom == Settings.FromProperty);
        var saved = new Settings { ODOO_NUMBER_FROM = Settings.FromFileName }.ToDictionary();
        Check("the file keeps the choice and no longer the old tick box", (string)saved["ODOO_NUMBER_FROM"] == "filename" && !saved.ContainsKey("ODOO_NUMBER_IS_FILE_NAME"));
        Check("the list offers the three sources, the custom property last", Settings.NumberSources.Length == 3 && Settings.NumberSources[0][0] == Settings.FromFileName && Settings.NumberSources[1][0] == Settings.FromTitle && Settings.NumberSources[2][0] == Settings.FromProperty);

        // Owner 2026-11-20: the "Read data..." button looks unnecessary - hidden unless switched on in the settings.
        Check("the Read data button is hidden unless the setting is switched on", !new Settings().SHOW_READ_DATA && From(new Dictionary<string, object> { { "SHOW_READ_DATA", true } }).SHOW_READ_DATA);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL " + _run + " PASSED" : _failed + " OF " + _run + " FAILED");
        return _failed;
    }
}
