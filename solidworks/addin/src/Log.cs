// Tiny diagnostic log: %APPDATA%\LaserCAM\solidworks-addin.log (append, best effort).
using System;
using System.IO;

namespace LaserCAM.SolidWorks
{
    public static class Log
    {
        public static string Path
        {
            get { return System.IO.Path.Combine(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM"), "solidworks-addin.log"); }
        }
        public static void Write(string line)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
