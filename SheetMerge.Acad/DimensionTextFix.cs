using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace SheetMerge
{
    /// <summary>
    /// Revit хэмжээсийн дүрсийг тусгай нэртэй блокт (DIMBLOCKn-...) урьдчилан зурдаг бөгөөд dimension style нь
    /// Revit-ийн дотоод нэгжтэй (DIMLFAC=304.8). Хуулсан хэмжээсийг AutoCAD заавал дахин зурдаг тул:
    ///  - тексттэй хэмжээст Revit-ийн харуулсан текстийг тогтмол текст болгон онооно (хэмжээс хэвээр, засварлах боломжтой);
    ///  - текстгүй хэмжээсийг (арматурын тархалтын тэмдэг гэх мэт) Revit-ийн зурсан дүрсээр нь блок болгоно.
    /// Эх файлыг санах ойд л өөрчилнө.
    /// </summary>
    static class DimensionTextFix
    {
        public static void Apply(Database src, out int texts, out int converted)
        {
            texts = 0;
            converted = 0;
            var dimClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Dimension));
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(src.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in table)
                {
                    var space = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!space.IsLayout) continue;
                    var dimIds = space.Cast<ObjectId>().Where(id => id.ObjectClass.IsDerivedFrom(dimClass)).ToList();
                    foreach (var id in dimIds)
                    {
                        var dim = (Dimension)tr.GetObject(id, OpenMode.ForRead);
                        if (dim.DimBlockId.IsNull) continue;
                        var shown = BlockTexts(tr, dim.DimBlockId);
                        if (shown.Count == 1)
                        {
                            if (!string.IsNullOrEmpty(dim.DimensionText) && dim.DimensionText != "<>") continue;
                            dim.UpgradeOpen();
                            dim.DimensionText = shown[0];
                            texts++;
                        }
                        else if (shown.Count == 0)
                        {
                            ReplaceWithBlock(tr, space, dim);
                            converted++;
                        }
                    }
                }
                tr.Commit();
            }
        }

        static void ReplaceWithBlock(Transaction tr, BlockTableRecord space, Dimension dim)
        {
            var br = new BlockReference(dim.DimBlockPosition, dim.DimBlockId);
            br.SetPropertiesFrom(dim);
            space.UpgradeOpen();
            space.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            dim.UpgradeOpen();
            dim.Erase();
        }

        static List<string> BlockTexts(Transaction tr, ObjectId blockId)
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            return block.Cast<ObjectId>()
                .Select(id => tr.GetObject(id, OpenMode.ForRead))
                .Select(o => o is MText m ? m.Contents : o is DBText t ? t.TextString : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
    }
}
