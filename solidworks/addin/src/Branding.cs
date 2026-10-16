// UCASE icon for the add-in dialogs (icons/ucase.ico next to the DLL).
using System;
using System.Drawing;
using System.IO;

namespace LaserCAM.SolidWorks
{
    public static class Branding
    {
        static Icon _icon;
        public static Icon AppIcon
        {
            get
            {
                if (_icon != null) return _icon;
                try
                {
                    string dir = Path.GetDirectoryName(typeof(Branding).Assembly.Location) ?? "";
                    string ico = Path.Combine(dir, "icons", "ucase.ico");
                    if (File.Exists(ico)) _icon = new Icon(ico);
                }
                catch { }
                return _icon;
            }
        }
    }
}
