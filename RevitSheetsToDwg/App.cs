using Autodesk.Revit.UI;

namespace RevitSheetsToDwg
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel("Sheets → DWG");
            var assembly = typeof(App).Assembly.Location;
            panel.AddItem(new PushButtonData("MergeSheetsToLayouts", "Sheets →\nLayouts",
                assembly, typeof(MergeSheetsLayoutsCommand).FullName)
            {
                ToolTip = "Сонгосон sheet-үүдийг нэг DWG болгоно: sheet бүр тусдаа layout (title block + viewport), Revit-ийн export шиг."
            });
            panel.AddItem(new PushButtonData("MergeSheetsToDwg", "Sheets →\nModel space",
                assembly, typeof(MergeSheetsCommand).FullName)
            {
                ToolTip = "Сонгосон sheet-үүдийг нэг DWG-ийн model space-д тороор байрлуулна (sheet бүр нэг block)."
            });
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
