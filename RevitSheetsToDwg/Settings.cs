using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitSheetsToDwg
{
    /// <summary>Сүүлд ашигласан тохиргоо (%APPDATA%\RevitSheetsToDwg\settings.txt).</summary>
    class Settings
    {
        public string OutputPath;
        public string AccorePath;
        public string Setup;
        public int Columns = 4;
        public double Gap;
        public bool Skip3D;

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitSheetsToDwg", "settings.txt");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var value = line.Substring(i + 1);
                    switch (line.Substring(0, i))
                    {
                        case "OutputPath": s.OutputPath = value; break;
                        case "AccorePath": s.AccorePath = value; break;
                        case "Setup": s.Setup = value; break;
                        case "Columns": int.TryParse(value, out s.Columns); break;
                        case "Skip3D": s.Skip3D = value == "1"; break;
                        case "Gap": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Gap); break;
                    }
                }
            }
            catch (IOException) { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "OutputPath=" + OutputPath, "AccorePath=" + AccorePath, "Setup=" + Setup,
                    "Columns=" + Columns, "Gap=" + Gap.ToString(CultureInfo.InvariantCulture), "Skip3D=" + (Skip3D ? "1" : "0")
                }, Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }

    /// <summary>Windows Explorer шиг "A2" &lt; "A10" эрэмбэлэлт.</summary>
    class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);
        public int Compare(string a, string b) => StrCmpLogicalW(a ?? "", b ?? "");
    }

    static class PathUtil
    {
        /// <summary>Хоосон эсвэл буруу замд алдаа шидэхгүйгээр null буцаана.</summary>
        public static string DirectoryOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetDirectoryName(path); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length > 120 ? clean.Substring(0, 120) : clean;
        }
    }
}
