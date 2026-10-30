using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSheetsToDwg
{
    /// <summary>AutoCAD Core Console-ийг далд ажиллуулж SheetMerge.Acad.dll-ийн MERGESHEETS командыг дуудна.</summary>
    static class AccoreRunner
    {
        /// <summary>Суусан AutoCAD-аас хамгийн шинэ, .NET Framework-тэй (2024 хүртэлх) хувилбарыг сонгоно.</summary>
        public static string FindAccoreConsole()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
            if (!Directory.Exists(root)) return null;
            var found = Directory.GetDirectories(root, "AutoCAD*")
                .Select(d => new { Path = Path.Combine(d, "accoreconsole.exe"), Year = Year(d) })
                .Where(x => File.Exists(x.Path))
                .OrderByDescending(x => x.Year <= 2024).ThenByDescending(x => x.Year)
                .FirstOrDefault();
            return found?.Path;
        }

        static int Year(string dir)
        {
            var m = Regex.Match(Path.GetFileName(dir), @"(\d{4})");
            return m.Success ? int.Parse(m.Value) : 0;
        }

        /// <summary>AutoCAD 2025+ нь .NET 8 дээр ажилладаг тул acad2025 хавтсан дахь хувилбарыг, бусад үед .NET Framework-ийнхыг өгнө.</summary>
        public static string MergeDllFor(string accorePath, string addinDir)
        {
            bool net8 = Year(Path.GetDirectoryName(accorePath ?? "") ?? "") >= 2025;
            return net8 ? Path.Combine(addinDir, "acad2025", "SheetMerge.Acad.dll") : Path.Combine(addinDir, "SheetMerge.Acad.dll");
        }

        public static void WriteJob(string jobPath, string output, bool layouts, int columns, double gap, string[] files)
        {
            var sb = new StringBuilder();
            sb.AppendLine("OUTPUT=" + output);
            sb.AppendLine("MODE=" + (layouts ? "LAYOUTS" : "MODEL"));
            sb.AppendLine("COLUMNS=" + columns);
            sb.AppendLine("GAP=" + gap.ToString(CultureInfo.InvariantCulture));
            foreach (var f in files) sb.AppendLine("FILE=" + f);
            File.WriteAllText(jobPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <returns>Амжилттай бол null, үгүй бол алдааны тайлбар.</returns>
        public static string Run(string accorePath, string mergeDll, string jobPath, string workDir, TimeSpan timeout)
        {
            var script = Path.Combine(workDir, "merge.scr");
            File.WriteAllText(script, string.Join("\r\n",
                "(setvar \"SECURELOAD\" 0)",
                "(setvar \"FILEDIA\" 0)",
                "(command \"_.NETLOAD\" \"" + mergeDll.Replace('\\', '/') + "\")",
                "MERGESHEETS",
                ""), new UTF8Encoding(false));

            var psi = new ProcessStartInfo(accorePath, "/s \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
                WorkingDirectory = workDir
            };
            psi.EnvironmentVariables["SHEETMERGE_JOB"] = jobPath;

            var logPath = Path.Combine(workDir, "accoreconsole.log");
            using (var process = Process.Start(psi))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return "AutoCAD Core Console хугацаа хэтэрсэн. Лог: " + logPath;
                }
                File.WriteAllText(logPath, output.Result.Replace("\0", ""), Encoding.UTF8);
            }

            var resultPath = jobPath + ".result";
            if (!File.Exists(resultPath))
                return "AutoCAD Core Console үр дүн бичсэнгүй (NETLOAD амжилтгүй байж магадгүй). Лог: " + logPath;
            var result = File.ReadAllText(resultPath, Encoding.UTF8);
            return result.StartsWith("OK") ? null : result;
        }
    }
}
