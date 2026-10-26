using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("LaserCAM Odoo Connector")]
[assembly: AssemblyDescription("Nest Odoo bills of materials in LaserCAM and save the result back (Odoo Enterprise / Online, no module)")]
[assembly: AssemblyCompany("UCASE MB")]
[assembly: AssemblyProduct("LaserCAM")]
[assembly: AssemblyCopyright("UCASE MB")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: ComVisible(false)]

namespace LaserCAM.OdooConnector
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
