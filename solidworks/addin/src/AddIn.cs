// LaserCAM SolidWorks add-in: "Send to Odoo" + "Settings" buttons (CommandManager tab + menu).
// Works with SolidWorks 2016 and newer (compiled against the 2016 interop assemblies with embedded types).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace LaserCAM.SolidWorks
{
    [Guid("8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]   // SolidWorks resolves the button callbacks by name through IDispatch
    public class AddIn : ISwAddin
    {
        public const string Title = "LaserCAM";
        public const string Description = "LaserCAM: send the sheet metal flat pattern to LaserCAM and Odoo";

        const int CMD_GROUP_ID = 7391;      // unique per add-in
        const int CMD_SEND = 0;
        const int CMD_SETTINGS = 1;

        ISldWorks _sw;
        int _cookie;
        ICommandManager _cmdMgr;
        ICommandGroup _cmdGroup;

        // ---------------- ISwAddin ----------------
        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            string step = "start";
            try
            {
                Log.Write("ConnectToSW: ThisSW=" + (ThisSW == null ? "null" : ThisSW.GetType().FullName) + " cookie=" + Cookie + " addin=" + typeof(AddIn).Assembly.Location + " v" + typeof(AddIn).Assembly.GetName().Version);
                step = "cast ISldWorks";
                _sw = (ISldWorks)ThisSW;
                _cookie = Cookie;
                step = "SetAddinCallbackInfo";
                _sw.SetAddinCallbackInfo(0, this, _cookie);
                step = "GetCommandManager";
                _cmdMgr = _sw.GetCommandManager(_cookie);
                step = "AddCommands";
                AddCommands();
                Log.Write("ConnectToSW: ok");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("ConnectToSW FAILED at " + step + ": " + ex);
                MessageBox.Show("LaserCAM add-in failed to load at step '" + step + "':\n" + ex.GetType().Name + ": " + ex.Message + "\n\n" + ex.StackTrace + "\n\nLog: " + Log.Path, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try { if (_cmdMgr != null) _cmdMgr.RemoveCommandGroup(CMD_GROUP_ID); } catch { }
            if (_cmdGroup != null) { Marshal.ReleaseComObject(_cmdGroup); _cmdGroup = null; }
            if (_cmdMgr != null) { Marshal.ReleaseComObject(_cmdMgr); _cmdMgr = null; }
            _sw = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // ---------------- commands ----------------
        void AddCommands()
        {
            int errors = 0;
            Log.Write("CreateCommandGroup2");
            _cmdGroup = _cmdMgr.CreateCommandGroup2(CMD_GROUP_ID, Title, "LaserCAM tools", "LaserCAM", -1, true, ref errors);
            Log.Write("CreateCommandGroup2 -> " + (_cmdGroup == null ? "null" : "ok") + " errors=" + errors);
            string dir = Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? "";
            string icons = Path.Combine(dir, "icons");
            _cmdGroup.SmallIconList = Path.Combine(icons, "toolbar16.bmp");
            _cmdGroup.LargeIconList = Path.Combine(icons, "toolbar24.bmp");
            _cmdGroup.SmallMainIcon = Path.Combine(icons, "main16.bmp");
            _cmdGroup.LargeMainIcon = Path.Combine(icons, "main24.bmp");
            try
            {
                // SolidWorks 2016+: several sizes (UCASE "U" badge) so the buttons stay sharp on high-DPI screens
                _cmdGroup.IconList = new[] { Path.Combine(icons, "toolbar16.bmp"), Path.Combine(icons, "toolbar24.bmp"), Path.Combine(icons, "toolbar32.bmp"), Path.Combine(icons, "toolbar48.bmp") };
                _cmdGroup.MainIconList = new[] { Path.Combine(icons, "main16.bmp"), Path.Combine(icons, "main24.bmp"), Path.Combine(icons, "main32.bmp"), Path.Combine(icons, "main48.bmp") };
            }
            catch { /* older interop without IconList */ }

            Log.Write("icons set");
            int menuToolbar = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            int idxSend = _cmdGroup.AddCommandItem2("Send to Odoo", -1, "Export the flat pattern and send it to LaserCAM and Odoo", "Send to Odoo", 0, "OnSendToOdoo", "OnEnableSend", CMD_SEND, menuToolbar);
            int idxSettings = _cmdGroup.AddCommandItem2("Settings...", -1, "LaserCAM settings (keys, Odoo, sheet defaults)", "Settings", 1, "OnSettings", "OnEnableAlways", CMD_SETTINGS, menuToolbar);
            Log.Write("items added");
            _cmdGroup.HasToolbar = true;
            _cmdGroup.HasMenu = true;
            _cmdGroup.Activate();
            Log.Write("group activated");

            // CommandManager tab "LaserCAM" in part documents
            int docType = (int)swDocumentTypes_e.swDocPART;
            Log.Write("GetCommandTab");
            ICommandTab tab = _cmdMgr.GetCommandTab(docType, Title);
            if (tab != null)
            {
                // rebuild every load so a changed button set is picked up
                _cmdMgr.RemoveCommandTab((CommandTab)tab);
                tab = null;
            }
            tab = _cmdMgr.AddCommandTab(docType, Title);
            if (tab != null)
            {
                ICommandTabBox box = tab.AddCommandTabBox();
                int[] ids = { _cmdGroup.get_CommandID(idxSend), _cmdGroup.get_CommandID(idxSettings) };
                int below = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                int[] textTypes = { below, below };
                box.AddCommands(ids, textTypes);
                Log.Write("tab commands added");
            }
        }

        // Callbacks (must be public; names are passed as strings to AddCommandItem2)
        public void OnSendToOdoo()
        {
            try
            {
                var model = _sw.ActiveDoc as IModelDoc2;
                var settings = Settings.Load();
                if (string.IsNullOrEmpty(settings.ODOO_API_KEY))
                {
                    // First use: the Odoo API key is needed for everything; the LaserCAM key only for nesting (Basic plan).
                    MessageBox.Show("Welcome to LaserCAM. Fill in your Odoo API key once (stored on this PC only). The LaserCAM key (LC_PID, LC_KEY) is needed only for sending to LaserCAM - nesting, weight and time (Basic plan); sending files straight to Odoo is free.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    using (var f = new SettingsForm(settings))
                    {
                        if (f.ShowDialog() != DialogResult.OK) return;
                        settings = f.Result;
                        settings.Save();
                    }
                }
                new Exporter(_sw, settings).Run(model);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title + " - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void OnSettings()
        {
            try
            {
                using (var f = new SettingsForm(Settings.Load()))
                {
                    if (f.ShowDialog() == DialogResult.OK) f.Result.Save();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title + " - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public int OnEnableSend()
        {
            try
            {
                var model = _sw.ActiveDoc as IModelDoc2;
                return (model != null && model.GetType() == (int)swDocumentTypes_e.swDocPART) ? 1 : 0;
            }
            catch { return 0; }
        }

        public int OnEnableAlways() { return 1; }

        // ---------------- COM registration (regasm /codebase) ----------------
        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                k.SetValue(null, 1);
                k.SetValue("Description", Description);
                k.SetValue("Title", Title);
            }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
            {
                k.SetValue(null, 1);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\Addins\" + guid, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + guid, false); } catch { }
        }
    }
}
