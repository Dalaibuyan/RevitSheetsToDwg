using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SheetMerge
{
    /// <summary>Revit add-in-аас дамжуулах ажлын файл (KEY=VALUE мөрүүд).</summary>
    public class MergeJob
    {
        public string Output;
        public int Columns = 4;
        public double Gap; // 0 = автомат
        public bool Layouts = true; // true: sheet бүр тусдаа layout, false: бүгд model space-д block болж
        public List<string> Files = new List<string>();

        public static MergeJob Load(string path)
        {
            var job = new MergeJob();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                int i = raw.IndexOf('=');
                if (i <= 0) continue;
                var key = raw.Substring(0, i).Trim().ToUpperInvariant();
                var value = raw.Substring(i + 1).Trim();
                switch (key)
                {
                    case "OUTPUT": job.Output = value; break;
                    case "COLUMNS": int.TryParse(value, out job.Columns); break;
                    case "GAP": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out job.Gap); break;
                    case "FILE": job.Files.Add(value); break;
                    case "MODE": job.Layouts = !value.Equals("MODEL", System.StringComparison.OrdinalIgnoreCase); break;
                }
            }
            if (job.Columns < 1) job.Columns = 1;
            return job;
        }
    }
}
