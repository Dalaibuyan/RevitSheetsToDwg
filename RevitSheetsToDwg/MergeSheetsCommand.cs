using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitSheetsToDwg
{
    /// <summary>Sheet бүр AutoCAD-ийн тусдаа layout болно (Revit-ийн export шиг, гэхдээ нэг файлд).</summary>
    [Transaction(TransactionMode.Manual)]
    public class MergeSheetsLayoutsCommand : MergeSheetsCommandBase
    {
        protected override bool Layouts => true;
    }

    /// <summary>Бүх sheet model space-д тороор, sheet бүр нэг block болно.</summary>
    [Transaction(TransactionMode.Manual)]
    public class MergeSheetsCommand : MergeSheetsCommandBase
    {
        protected override bool Layouts => false;
    }

    /// <summary>
    /// 1) Сонгосон sheet бүрийг тусдаа DWG болгон export хийнэ (view-үүдийг нэгтгэсэн, xref-гүй).
    /// 2) AutoCAD Core Console дээр SheetMerge.Acad.dll-ээр бүгдийг нэг DWG болгоно.
    /// </summary>
    public abstract class MergeSheetsCommandBase : IExternalCommand
    {
        protected abstract bool Layouts { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                return Run(commandData, ref message);
            }
            catch (Exception ex)
            {
                // Revit-ийн ерөнхий "Command Failure" цонхны оронд бодит шалтгааныг харуулна
                TaskDialog.Show("Sheets → DWG", "Алдаа гарлаа:\n\n" + ex);
                return Result.Cancelled;
            }
        }

        Result Run(ExternalCommandData commandData, ref string message)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { message = "Нээлттэй Revit файл алга."; return Result.Failed; }

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && s.CanBePrinted)
                .OrderBy(s => s.SheetNumber, NaturalComparer.Instance)
                .ToList();
            if (sheets.Count == 0)
            {
                TaskDialog.Show("Sheets → DWG", "Энэ төсөлд export хийх sheet алга.");
                return Result.Cancelled;
            }

            var addinDir = Path.GetDirectoryName(typeof(MergeSheetsCommand).Assembly.Location);

            // Project Browser-т сонгосон sheet-үүд байвал тэдгээрийг урьдчилан чагтална
            var sheetIds = new HashSet<ElementId>(sheets.Select(s => s.Id));
            var preselected = uidoc.Selection.GetElementIds().Where(sheetIds.Contains).ToList();

            var settings = Settings.Load();
            var lastDir = PathUtil.DirectoryOf(settings.OutputPath);
            var defaultOutput = Path.Combine(
                Directory.Exists(lastDir) ? lastDir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                PathUtil.SafeFileName(doc.Title) + (Layouts ? "_Layouts.dwg" : "_Sheets.dwg"));

            List<ViewSheet> selected;
            string setup, output, accore;
            int columns;
            double gap;
            bool skip3D;
            using (var form = new SheetPickerForm(sheets, BaseExportOptions.GetPredefinedSetupNames(doc), settings, preselected, defaultOutput, Layouts))
            {
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                selected = form.SelectedSheets;
                setup = form.SetupName;
                output = form.OutputPath;
                accore = form.AccorePath;
                columns = form.Columns;
                gap = form.Gap;
                skip3D = form.Skip3D;
            }

            var mergeDll = AccoreRunner.MergeDllFor(accore, addinDir);
            if (!File.Exists(mergeDll)) { message = "SheetMerge.Acad.dll олдсонгүй: " + mergeDll; return Result.Failed; }

            settings.OutputPath = output;
            settings.AccorePath = accore;
            settings.Setup = setup;
            settings.Columns = columns;
            settings.Gap = gap;
            settings.Skip3D = skip3D;
            settings.Save();

            var workDir = Path.Combine(Path.GetTempPath(), "RevitSheetsToDwg", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(workDir);
            try
            {
                var files = skip3D ? ExportWithout3DViews(doc, selected, setup, workDir) : ExportSheets(doc, selected, setup, workDir);
                var jobPath = Path.Combine(workDir, "job.txt");
                AccoreRunner.WriteJob(jobPath, output, Layouts, columns, gap, files);

                var error = AccoreRunner.Run(accore, mergeDll, jobPath, workDir, TimeSpan.FromMinutes(10 + files.Length));
                if (error != null)
                {
                    TaskDialog.Show("Sheets → DWG", "Нэгтгэхэд алдаа гарлаа:\n\n" + error + "\n\nТүр файлууд: " + workDir);
                    return Result.Failed;
                }

                Directory.Delete(workDir, true);
                ShowDone(output, files.Length);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Sheets → DWG", "Алдаа гарлаа:\n\n" + ex.Message + "\n\nТүр файлууд: " + workDir);
                return Result.Cancelled;
            }
        }

        /// <summary>
        /// Sheet-үүд дээрх 3D view-ийн viewport-уудыг түр устгаад export хийж, дараа нь бүх өөрчлөлтийг
        /// буцаана (TransactionGroup.RollBack) - Revit файл өөрчлөгдөхгүй.
        /// </summary>
        static string[] ExportWithout3DViews(Document doc, List<ViewSheet> sheets, string setup, string workDir)
        {
            using (var group = new TransactionGroup(doc, "Sheets → DWG: 3D view-гүй export"))
            {
                group.Start();
                try
                {
                    using (var t = new Transaction(doc, "3D viewport-уудыг түр хасах"))
                    {
                        t.Start();
                        foreach (var sheet in sheets)
                            foreach (var id in sheet.GetAllViewports())
                                if (doc.GetElement(id) is Viewport vp && doc.GetElement(vp.ViewId) is View3D)
                                    doc.Delete(id);
                        t.Commit();
                    }
                    return ExportSheets(doc, sheets, setup, workDir);
                }
                finally
                {
                    group.RollBack();
                }
            }
        }

        static string[] ExportSheets(Document doc, List<ViewSheet> sheets, string setup, string workDir)
        {
            var files = new List<string>();
            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                var options = (setup == null ? null : DWGExportOptions.GetPredefinedOptions(doc, setup)) ?? new DWGExportOptions();
                options.MergedViews = true;              // view-үүдийг xref биш, нэг файлд
                options.FileVersion = ACADVersion.R2018; // AutoCAD 2018-2024 уншина

                // Файлын нэр нь DWG доторх block-ийн нэр болно
                var name = (i + 1).ToString("000") + "_" + PathUtil.SafeFileName(sheet.SheetNumber + " - " + sheet.Name);
                if (!doc.Export(workDir, name, new List<ElementId> { sheet.Id }, options))
                    throw new InvalidOperationException("Sheet export амжилтгүй: " + sheet.SheetNumber);

                var path = Path.Combine(workDir, name + ".dwg");
                if (!File.Exists(path))
                    path = Directory.GetFiles(workDir, name + "*.dwg").FirstOrDefault()
                           ?? throw new FileNotFoundException("Export хийсэн DWG олдсонгүй: " + name);
                files.Add(path);
            }
            return files.ToArray();
        }

        static void ShowDone(string output, int count)
        {
            var dialog = new TaskDialog("Sheets → DWG")
            {
                MainInstruction = count + " sheet нэг DWG-д нэгтгэгдлээ",
                MainContent = output,
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "DWG файлыг нээх");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Хавтсыг нээх");
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
                    break;
                case TaskDialogResult.CommandLink2:
                    Process.Start("explorer.exe", "/select,\"" + output + "\"");
                    break;
            }
        }
    }
}
