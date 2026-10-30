using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SheetMerge
{
    /// <summary>
    /// Revit-ийн sheet тус бүрийн DWG-г нэг DWG болгоно: sheet бүр өөрийн layout-тай (title block + жинхэнэ viewport),
    /// model агуулга нь model space-д давхцахгүйгээр зэрэгцэн байрлана. Viewport бүрийн ViewTarget-ийг тэр хэмжээгээр шилжүүлнэ.
    /// Идэвхтэй (нээлттэй) зураг дээр ажиллана: layout сэлгэх, viewport асаах нь зөвхөн тэнд зөв ажилладаг.
    /// </summary>
    public static class LayoutMerger
    {
        public static int Merge(MergeJob job, Action<string> log)
        {
            if (job.Files.Count == 0) throw new InvalidOperationException("Нэгтгэх DWG файл алга.");
            if (string.IsNullOrWhiteSpace(job.Output)) throw new InvalidOperationException("Гаралтын файлын зам хоосон байна.");

            var doc = Application.DocumentManager.MdiActiveDocument
                      ?? throw new InvalidOperationException("Нээлттэй зураг алга.");
            var target = doc.Database;
            if (!IsEmptyDrawing(target))
                throw new InvalidOperationException("Шинэ хоосон зураг дээр ажиллуулна уу (одоогийн зурагт объект байна).");

            var lm = LayoutManager.Current;
            var defaultLayouts = LayoutNames(target);
            // Шинэ layout-д AutoCAD өөрийн default viewport нэмэхгүй байх
            var createViewport = Application.GetSystemVariable("LAYOUTCREATEVIEWPORT");
            Application.SetSystemVariable("LAYOUTCREATEVIEWPORT", 0);
            try
            {
                return MergeInto(job, target, lm, defaultLayouts, log);
            }
            finally
            {
                Application.SetSystemVariable("LAYOUTCREATEVIEWPORT", createViewport);
            }
        }

        static int MergeInto(MergeJob job, Database target, LayoutManager lm, List<string> defaultLayouts, Action<string> log)
        {
            var created = new List<string>();
            double nextX = 0, gap = job.Gap;

            foreach (var file in job.Files)
            {
                log("Боловсруулж байна: " + Path.GetFileName(file));
                using (var src = new Database(false, true))
                {
                    src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    src.CloseInput(true);
                    DimensionTextFix.Apply(src, out var dimTexts, out var dimBlocks);
                    if (dimBlocks > 0) log("  " + dimBlocks + " текстгүй Revit хэмжээсийг дүрсээр нь блок болгов");
                    int renamed = NameConflicts.Resolve(src, target, NameConflicts.SuffixFor(file));
                    if (renamed > 0) log("  " + renamed + " style/block-ийг давхардлаас сэргийлж өөр нэрээр хуулав");
                    ImageFiles.Relocate(src, file, job.Output, log);
                    if (created.Count == 0) target.Insunits = src.Insunits;

                    // 1) Model space-ийг баруун тийш шилжүүлж хуулна
                    var offset = new Vector3d();
                    var modelIds = ModelEntities(src, out var ext, out var hasExt);
                    // Хөрш sheet-ийн объект viewport-д орж ирэхгүйн тулд viewport-уудын харах талбайг мөн тооцно
                    AddViewportWindows(src, ref ext, ref hasExt);
                    if (hasExt)
                    {
                        double width = ext.MaxPoint.X - ext.MinPoint.X;
                        if (gap <= 0) gap = Math.Max(width, ext.MaxPoint.Y - ext.MinPoint.Y) * 0.1;
                        offset = new Vector3d(nextX - ext.MinPoint.X, -ext.MinPoint.Y, 0);
                        nextX += width + gap;
                    }
                    if (modelIds.Count > 0)
                        Clone(src, target, modelIds, SymbolUtilityServices.GetBlockModelSpaceId(target), Matrix3d.Displacement(offset));

                    // 2) Sheet-ийн layout-ийг шинэ layout болгон хуулна
                    var layoutName = UniqueLayoutName(lm, SheetName(file));
                    if (!CopyLayout(src, target, lm, layoutName, offset))
                    {
                        log("  анхааруулга: layout олдсонгүй, зөвхөн model хуулав");
                        continue;
                    }
                    created.Add(layoutName);
                }
            }

            if (created.Count == 0) throw new InvalidOperationException("Нэг ч layout үүсгэж чадсангүй.");

            // Анхдагч хоосон Layout1/Layout2-ийг устгаж, эхний sheet-ийг нээлттэй үлдээнэ
            lm.CurrentLayout = created[0];
            foreach (var name in defaultLayouts)
                lm.DeleteLayout(name);
            using (var tr = target.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < created.Count; i++)
                    ((Layout)tr.GetObject(lm.GetLayoutId(created[i]), OpenMode.ForWrite)).TabOrder = i + 1;
                tr.Commit();
            }

            SheetMerger.ZoomExtents(target);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output)));
            if (File.Exists(job.Output)) File.Delete(job.Output);
            target.SaveAs(job.Output, DwgVersion.Current);
            return created.Count;
        }

        static bool CopyLayout(Database src, Database target, LayoutManager lm, string name, Vector3d offset)
        {
            ObjectIdCollection ids;
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var srcLayout = FindSheetLayout(tr, src);
                if (srcLayout == null) return false;
                var srcBtr = (BlockTableRecord)tr.GetObject(srcLayout.BlockTableRecordId, OpenMode.ForRead);
                // Эх layout-ийн paper space viewport-ийг хуулахгүй - шинэ layout өөрийнхтэй болно
                var srcPaperVp = SheetMerger.FindPaperSpaceViewport(tr, srcBtr);
                ids = new ObjectIdCollection(srcBtr.Cast<ObjectId>().Where(id => id != srcPaperVp).ToArray());

                var sheetExtents = PageSetup.SheetExtents(tr, srcBtr);
                var layoutId = lm.CreateLayout(name);
                if (sheetExtents.HasValue)
                {
                    using (var ttr = target.TransactionManager.StartTransaction())
                    {
                        var layout = (Layout)ttr.GetObject(layoutId, OpenMode.ForWrite);
                        PageSetup.Apply(layout, sheetExtents.Value);
                        ttr.Commit();
                    }
                }
                tr.Commit();
            }

            // Layout-ийг идэвхжүүлэхэд AutoCAD paper space viewport-ийг үүсгэнэ; дараа нь хуулсан viewport-ууд зөв бүртгэгдэнэ
            lm.CurrentLayout = name;
            ObjectId btrId;
            using (var tr = target.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead);
                btrId = layout.BlockTableRecordId;
                tr.Commit();
            }
            if (ids.Count == 0) return true;

            // Viewport-ийг хуулбал AutoCAD түүнийг layout-д бүртгэдэггүй (унтарсан үлддэг) тул
            // бусад объектыг хуулаад, viewport-уудыг ижил шинжтэйгээр шинээр үүсгэнэ
            var viewportIds = new ObjectIdCollection();
            var otherIds = new ObjectIdCollection();
            using (var tr = src.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                    (tr.GetObject(id, OpenMode.ForRead) is Viewport ? viewportIds : otherIds).Add(id);
                tr.Commit();
            }

            using (var mapping = new IdMapping())
            {
                if (otherIds.Count > 0)
                {
                    src.WblockCloneObjects(otherIds, btrId, mapping, DuplicateRecordCloning.Ignore, false);
                }
                CreateViewports(src, target, btrId, viewportIds, mapping, offset);
            }
            return true;
        }

        static void CreateViewports(Database src, Database target, ObjectId btrId, ObjectIdCollection viewportIds,
            IdMapping mapping, Vector3d offset)
        {
            using (var srcTr = src.TransactionManager.StartTransaction())
            using (var tr = target.TransactionManager.StartTransaction())
            {
                var paper = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                var layers = (LayerTable)tr.GetObject(target.LayerTableId, OpenMode.ForRead);
                var frameLayer = viewportIds.Count > 0 ? ViewportLayer(tr, layers) : ObjectId.Null;
                foreach (ObjectId id in viewportIds)
                {
                    var s = (Viewport)srcTr.GetObject(id, OpenMode.ForRead);
                    var vp = new Viewport { CenterPoint = s.CenterPoint, Width = s.Width, Height = s.Height, LayerId = frameLayer };
                    paper.AppendEntity(vp);
                    tr.AddNewlyCreatedDBObject(vp, true);

                    vp.ViewDirection = s.ViewDirection;
                    // Model агуулгыг шилжүүлсэн тул харах цэгийг мөн адил шилжүүлнэ
                    vp.ViewTarget = s.ViewTarget + offset;
                    vp.ViewCenter = s.ViewCenter;
                    vp.TwistAngle = s.TwistAngle;
                    vp.CustomScale = s.CustomScale;
                    if (s.NonRectClipOn && mapping.Contains(s.NonRectClipEntityId))
                    {
                        vp.NonRectClipEntityId = mapping[s.NonRectClipEntityId].Value;
                        vp.NonRectClipOn = true;
                    }
                    using (var srcFrozen = s.GetFrozenLayers())
                    using (var frozen = new ObjectIdCollection())
                    {
                        foreach (ObjectId layerId in srcFrozen)
                        {
                            var layerName = ((LayerTableRecord)srcTr.GetObject(layerId, OpenMode.ForRead)).Name;
                            if (layers.Has(layerName)) frozen.Add(layers[layerName]);
                        }
                        if (frozen.Count > 0) vp.FreezeLayersInViewport(frozen.GetEnumerator());
                    }
                    vp.On = true;
                    vp.Locked = s.Locked;
                }
                tr.Commit();
                srcTr.Commit();
            }
        }

        const string ViewportLayerName = "VIEWPORT-NPLT";

        /// <summary>Viewport-ын хүрээнд зориулсан, хэвлэгдэхгүй давхарга (байхгүй бол үүсгэнэ).</summary>
        static ObjectId ViewportLayer(Transaction tr, LayerTable layers)
        {
            if (layers.Has(ViewportLayerName)) return layers[ViewportLayerName];
            var layer = new LayerTableRecord
            {
                Name = ViewportLayerName,
                IsPlottable = false,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8),
                Description = "Viewport-ын хүрээ (хэвлэгдэхгүй)"
            };
            layers.UpgradeOpen();
            var id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        static void Clone(Database src, Database target, ObjectIdCollection ids, ObjectId ownerId, Matrix3d transform)
        {
            using (var mapping = new IdMapping())
            {
                src.WblockCloneObjects(ids, ownerId, mapping, DuplicateRecordCloning.Ignore, false);
                if (!transform.IsEqualTo(Matrix3d.Identity))
                {
                    using (var tr = target.TransactionManager.StartTransaction())
                    {
                        foreach (IdPair pair in mapping)
                        {
                            if (!pair.IsPrimary || !pair.IsCloned) continue;
                            if (tr.GetObject(pair.Value, OpenMode.ForWrite) is Entity ent) ent.TransformBy(transform);
                        }
                        tr.Commit();
                    }
                }
            }
        }

        static bool IsEmptyDrawing(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                bool empty = !ms.Cast<ObjectId>().Any();
                tr.Commit();
                return empty;
            }
        }

        static ObjectIdCollection ModelEntities(Database src, out Extents3d ext, out bool hasExt)
        {
            var ids = new ObjectIdCollection();
            ext = new Extents3d();
            hasExt = false;
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(src), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    ids.Add(id);
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    try
                    {
                        var e = ent.GeometricExtents;
                        if (hasExt) ext.AddExtents(e); else { ext = e; hasExt = true; }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                tr.Commit();
            }
            return ids;
        }

        /// <summary>Sheet-ийн viewport бүрийн model space дахь харах тэгш өнцөгтийг extents-д нэмнэ.</summary>
        static void AddViewportWindows(Database src, ref Extents3d ext, ref bool hasExt)
        {
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var layout = FindSheetLayout(tr, src);
                if (layout != null)
                {
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    var paperVp = SheetMerger.FindPaperSpaceViewport(tr, btr);
                    foreach (ObjectId id in btr)
                    {
                        if (id == paperVp || !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                        if (vp.CustomScale <= 0) continue;
                        var toModel = SheetMerger.ModelToPaper(vp).Inverse();
                        var half = new Vector3d(vp.Width / 2, vp.Height / 2, 0);
                        foreach (var p in new[] { vp.CenterPoint - half, vp.CenterPoint + half,
                                                  vp.CenterPoint + new Vector3d(half.X, -half.Y, 0), vp.CenterPoint + new Vector3d(-half.X, half.Y, 0) })
                        {
                            var q = p.TransformBy(toModel);
                            var flat = new Point3d(q.X, q.Y, 0);
                            if (hasExt) ext.AddPoint(flat); else { ext = new Extents3d(flat, flat); hasExt = true; }
                        }
                    }
                }
                tr.Commit();
            }
        }

        /// <summary>Агуулгатай эхний (tab order-оор) layout.</summary>
        static Layout FindSheetLayout(Transaction tr, Database db)
        {
            Layout found = null;
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var l = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (l.ModelType) continue;
                var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                if (!btr.Cast<ObjectId>().Any()) continue;
                if (found == null || l.TabOrder < found.TabOrder) found = l;
            }
            return found;
        }

        static List<string> LayoutNames(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var names = new List<string>();
                foreach (DBDictionaryEntry entry in layouts)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (!layout.ModelType) names.Add(layout.LayoutName);
                }
                tr.Commit();
                return names;
            }
        }

        /// <summary>"001_A101 - Plan.dwg" -> "A101 - Plan" (эрэмбийн угтварыг хасна).</summary>
        static string SheetName(string file) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(file), @"^\d{3}_", "");

        static string UniqueLayoutName(LayoutManager lm, string baseName)
        {
            var clean = new string(baseName.Select(c => "<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c).ToArray()).Trim();
            if (clean.Length == 0) clean = "Sheet";
            if (clean.Length > 200) clean = clean.Substring(0, 200);
            var name = clean;
            for (int i = 2; lm.LayoutExists(name); i++) name = clean + " (" + i + ")";
            return name;
        }
    }
}
