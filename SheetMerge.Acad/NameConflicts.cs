using System;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace SheetMerge
{
    /// <summary>
    /// Revit sheet бүрт ижил нэртэй боловч өөр тохиргоотой dimension style, text style, block үүсгэдэг
    /// (view-ийн масштабаас хамаарч текстийн өндөр гэх мэт өөр). Хуулах үед эхнийх нь л үлдэх тул бусад
    /// sheet-ийн хэмжээс, текст буруу харагдана. Хуулахаас өмнө эх файлд зөрчилтэйг нь өөр нэрээр нэрлэнэ.
    /// </summary>
    static class NameConflicts
    {
        public static int Resolve(Database src, Database target, string suffix)
        {
            int renamed = 0;
            using (var srcTr = src.TransactionManager.StartTransaction())
            using (var tr = target.TransactionManager.StartTransaction())
            {
                renamed += ResolveTable(srcTr, tr, src.DimStyleTableId, target.DimStyleTableId, suffix,
                    (a, b) => DimSignature(srcTr, (DimStyleTableRecord)a) == DimSignature(tr, (DimStyleTableRecord)b));
                renamed += ResolveTable(srcTr, tr, src.TextStyleTableId, target.TextStyleTableId, suffix,
                    (a, b) => TextSignature((TextStyleTableRecord)a) == TextSignature((TextStyleTableRecord)b));
                renamed += ResolveTable(srcTr, tr, src.BlockTableId, target.BlockTableId, suffix,
                    (a, b) => IsSpecialBlock((BlockTableRecord)a) ||
                              BlockSignature(srcTr, (BlockTableRecord)a) == BlockSignature(tr, (BlockTableRecord)b));
                tr.Commit();
                srcTr.Commit();
            }
            return renamed;
        }

        /// <summary>"001_2-9 - Нэр.dwg" -> "2-9" (sheet-ийн дугаар), нэрэнд орж болохгүй тэмдэгтийг солино.</summary>
        public static string SuffixFor(string file)
        {
            var name = System.Text.RegularExpressions.Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(file), @"^\d{3}_", "");
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) name = name.Substring(0, dash);
            name = new string(name.Select(c => "<>/\\\":;?*|,=` ".IndexOf(c) >= 0 ? '_' : c).ToArray());
            return name.Length == 0 ? "S" : (name.Length > 30 ? name.Substring(0, 30) : name);
        }

        static int ResolveTable(Transaction srcTr, Transaction tr, ObjectId srcTableId, ObjectId targetTableId, string suffix,
            Func<SymbolTableRecord, SymbolTableRecord, bool> same)
        {
            var srcTable = (SymbolTable)srcTr.GetObject(srcTableId, OpenMode.ForRead);
            var targetTable = (SymbolTable)tr.GetObject(targetTableId, OpenMode.ForRead);
            int renamed = 0;
            foreach (ObjectId id in srcTable)
            {
                var rec = (SymbolTableRecord)srcTr.GetObject(id, OpenMode.ForRead);
                if (rec.IsDependent || !targetTable.Has(rec.Name)) continue;
                var existing = (SymbolTableRecord)tr.GetObject(targetTable[rec.Name], OpenMode.ForRead);
                if (same(rec, existing)) continue;

                var name = rec.Name + "_" + suffix;
                for (int i = 2; targetTable.Has(name) || srcTable.Has(name); i++) name = rec.Name + "_" + suffix + "_" + i;
                rec.UpgradeOpen();
                rec.Name = name; // эх файлыг санах ойд л өөрчилнө, хадгалахгүй
                renamed++;
            }
            return renamed;
        }

        static bool IsSpecialBlock(BlockTableRecord b) =>
            b.IsLayout || b.IsAnonymous || b.IsFromExternalReference || b.Name.StartsWith("*");

        static string DimSignature(Transaction tr, DimStyleTableRecord d) => string.Join("|",
            F(d.Dimlfac), F(d.Dimscale), F(d.Dimtxt), F(d.Dimasz), F(d.Dimexe), F(d.Dimexo), F(d.Dimgap),
            F(d.Dimdli), F(d.Dimtsz), F(d.Dimcen), F(d.Dimrnd), F(d.Dimtfac),
            d.Dimdec, d.Dimtad, d.Dimzin, d.Dimlunit, d.Dimtih, d.Dimtoh,
            SymbolName(tr, d.Dimtxsty), SymbolName(tr, d.Dimblk), SymbolName(tr, d.Dimblk1), SymbolName(tr, d.Dimblk2));

        static string TextSignature(TextStyleTableRecord t) => string.Join("|",
            F(t.TextSize), F(t.XScale), F(t.ObliquingAngle), t.FileName, t.BigFontFileName, t.Font.TypeFace);

        /// <summary>Block-ийн агуулгын хялбар тэмдэг: объектын тоо, төрөл, нийт хэмжээ.</summary>
        static string BlockSignature(Transaction tr, BlockTableRecord b)
        {
            int count = 0;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var types = new System.Collections.Generic.SortedDictionary<string, int>();
            foreach (ObjectId id in b)
            {
                count++;
                var name = id.ObjectClass.Name;
                types[name] = types.TryGetValue(name, out var n) ? n + 1 : 1;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                try
                {
                    var ext = e.GeometricExtents;
                    minX = Math.Min(minX, ext.MinPoint.X); minY = Math.Min(minY, ext.MinPoint.Y);
                    maxX = Math.Max(maxX, ext.MaxPoint.X); maxY = Math.Max(maxY, ext.MaxPoint.Y);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }
            return count + "|" + string.Join(",", types.Select(t => t.Key + t.Value)) + "|" + F(minX) + F(minY) + F(maxX) + F(maxY);
        }

        static string SymbolName(Transaction tr, ObjectId id) =>
            id.IsNull ? "" : ((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;

        static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }
}
