using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(SheetMerge.Commands))]

namespace SheetMerge
{
    public class Commands
    {
        public const string JobEnvVar = "SHEETMERGE_JOB";

        /// <summary>
        /// Revit add-in нь SHEETMERGE_JOB орчны хувьсагчаар ажлын файл дамжуулна.
        /// Хувьсагч байхгүй бол (AutoCAD дотроос гараар ажиллуулахад) хавтас асууна.
        /// </summary>
        [CommandMethod("MERGESHEETS")]
        public void MergeSheets()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            void Log(string s) => ed?.WriteMessage("\n" + s);

            var jobPath = Environment.GetEnvironmentVariable(JobEnvVar);
            bool fromRevit = !string.IsNullOrEmpty(jobPath);
            try
            {
                var job = fromRevit ? MergeJob.Load(jobPath) : PromptJob(ed);
                if (job == null) return;
                int count = job.Layouts ? LayoutMerger.Merge(job, Log) : SheetMerger.Merge(job, Log);
                Log($"Амжилттай: {count} sheet -> {job.Output}");
                if (fromRevit) File.WriteAllText(jobPath + ".result", "OK\n" + count, Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                Log("Алдаа: " + ex);
                if (fromRevit) File.WriteAllText(jobPath + ".result", "ERROR\n" + ex, Encoding.UTF8);
            }
        }

        static MergeJob PromptJob(Editor ed)
        {
            if (ed == null) return null;
            var folder = ed.GetString(new PromptStringOptions("\nRevit-ээс гаргасан DWG-үүдийн хавтас: ") { AllowSpaces = true });
            if (folder.Status != PromptStatus.OK) return null;
            var dir = folder.StringResult.Trim().Trim('"');
            if (!Directory.Exists(dir)) { ed.WriteMessage("\nХавтас олдсонгүй."); return null; }

            var columns = ed.GetInteger(new PromptIntegerOptions("\nНэг мөрөнд хэдэн sheet")
            {
                DefaultValue = 4, UseDefaultValue = true, LowerLimit = 1, UpperLimit = 100
            });
            if (columns.Status != PromptStatus.OK) return null;

            var output = Path.Combine(dir, "Combined_Sheets.dwg");
            var job = new MergeJob { Output = output, Columns = columns.Value };
            job.Files.AddRange(Directory.GetFiles(dir, "*.dwg")
                .Where(f => !string.Equals(f, output, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            return job;
        }
    }
}
