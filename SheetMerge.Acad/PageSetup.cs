using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SheetMerge
{
    /// <summary>
    /// Revit-ийн DWG export layout-д хэвлэлийн тохиргоо бичдэггүй (AutoCAD-ийн анхны 12x9 цаас харагдана).
    /// Layout бүрд "DWG To PDF.pc3", Revit sheet-ийн (хүрээний) хэмжээтэй цаас, хүрээгээр цонх, 1:1 масштаб онооно.
    /// </summary>
    static class PageSetup
    {
        const string Device = "DWG To PDF.pc3";
        const double Tolerance = 1.0; // мм

        /// <summary>Sheet-ийн хүрээ: paper space дахь хамгийн том block (title block), олдохгүй бол бүх объектын хүрээ.</summary>
        public static Extents2d? SheetExtents(Transaction tr, BlockTableRecord paper)
        {
            Extents3d? best = null, all = null;
            double bestArea = 0;
            foreach (ObjectId id in paper)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || ent is Viewport) continue;
                Extents3d e;
                try { e = ent.GeometricExtents; } catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                if (all == null) all = e; else { var a = all.Value; a.AddExtents(e); all = a; }
                double area = (e.MaxPoint.X - e.MinPoint.X) * (e.MaxPoint.Y - e.MinPoint.Y);
                if (ent is BlockReference && area > bestArea) { best = e; bestArea = area; }
            }
            var pick = best ?? all;
            if (pick == null) return null;
            return new Extents2d(pick.Value.MinPoint.X, pick.Value.MinPoint.Y, pick.Value.MaxPoint.X, pick.Value.MaxPoint.Y);
        }

        public static void Apply(Layout layout, Extents2d sheet)
        {
            double w = sheet.MaxPoint.X - sheet.MinPoint.X, h = sheet.MaxPoint.Y - sheet.MinPoint.Y;
            if (w <= 0 || h <= 0) return;
            var psv = PlotSettingsValidator.Current;
            try
            {
                psv.SetPlotConfigurationName(layout, Device, null);
                psv.RefreshLists(layout);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return; } // DWG To PDF байхгүй бол анхных хэвээр

            var media = PickMedia(psv.GetCanonicalMediaNameList(layout).Cast<string>(), w, h, out bool exact, out bool rotate);
            if (media == null) return;
            void Try(Action a) { try { a(); } catch (Autodesk.AutoCAD.Runtime.Exception) { } }
            Try(() => psv.SetPlotConfigurationName(layout, Device, media));
            Try(() => psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters));
            Try(() => psv.SetPlotRotation(layout, rotate ? PlotRotation.Degrees090 : PlotRotation.Degrees000));
            Try(() => psv.SetPlotWindowArea(layout, sheet));
            Try(() => psv.SetPlotType(layout, PlotType.Window));
            Try(() => psv.SetPlotCentered(layout, true));
            if (exact)
            {
                Try(() => psv.SetUseStandardScale(layout, true));
                Try(() => psv.SetStdScaleType(layout, StdScaleType.StdScale1To1));
            }
            else
            {
                // Тохирох стандарт цаас байхгүй бол ойролцоо цаасанд багтаана
                Try(() => psv.SetUseStandardScale(layout, true));
                Try(() => psv.SetStdScaleType(layout, StdScaleType.ScaleToFit));
            }
        }

        /// <summary>
        /// Хүрээтэй тэнцүү хэмжээтэй цаас (захгүй "full bleed"-ийг илүүд үзнэ); байхгүй бол хүрээ багтах хамгийн бага цаас.
        /// </summary>
        static string PickMedia(System.Collections.Generic.IEnumerable<string> names, double w, double h, out bool exact, out bool rotate)
        {
            var sizes = names
                .Select(n => new { Name = n, Size = ParseSize(n) })
                .Where(x => x.Size != null)
                .Select(x => new { x.Name, W = x.Size.Value.X, H = x.Size.Value.Y, Bleed = x.Name.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0 })
                .ToList();

            bool Same(double a, double b) => Math.Abs(a - b) <= Tolerance;
            var hit = sizes.Where(s => Same(s.W, w) && Same(s.H, h)).OrderByDescending(s => s.Bleed).FirstOrDefault();
            if (hit != null) { exact = true; rotate = false; return hit.Name; }
            hit = sizes.Where(s => Same(s.W, h) && Same(s.H, w)).OrderByDescending(s => s.Bleed).FirstOrDefault();
            if (hit != null) { exact = true; rotate = true; return hit.Name; }

            exact = false;
            bool landscape = w >= h;
            var fit = sizes.Where(s => (s.W >= s.H) == landscape && s.W >= w && s.H >= h)
                .OrderBy(s => s.W * s.H).ThenByDescending(s => s.Bleed).FirstOrDefault();
            rotate = false;
            return fit?.Name;
        }

        /// <summary>"ISO_full_bleed_A2_(594.00_x_420.00_MM)" -> (594, 420) мм.</summary>
        static Point2d? ParseSize(string name)
        {
            var m = Regex.Match(name, @"\(([\d.]+)_x_([\d.]+)_(MM|Inches)\)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double k = m.Groups[3].Value.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1 : 25.4;
            return new Point2d(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * k,
                               double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * k);
        }
    }
}
