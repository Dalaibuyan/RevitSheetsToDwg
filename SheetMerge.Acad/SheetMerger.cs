using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;

namespace SheetMerge
{
    /// <summary>
    /// Revit-ийн sheet тус бүрийн DWG-г нэг DWG-ийн model space-д цаасны масштабаар (1:1 sheet) тороор байрлуулна.
    /// Sheet бүр нэг block болно: title block (paper space) + viewport бүрийн model-ийн block reference (масштаб, XCLIP-тэй).
    /// </summary>
    public static class SheetMerger
    {
        class ViewportInfo
        {
            public Matrix3d ModelToPaper;
            public List<Point3d> Boundary;
            public bool IsPlan;
        }

        class PaperSpaceInfo
        {
            public ObjectIdCollection EntityIds = new ObjectIdCollection();
            public List<ViewportInfo> Viewports = new List<ViewportInfo>();
            public Extents3d Extents;
            public bool HasExtents;

            public void Add(Point3d p)
            {
                if (HasExtents) Extents.AddPoint(p);
                else { Extents = new Extents3d(p, p); HasExtents = true; }
            }

            public void Add(Entity ent)
            {
                try { var e = ent.GeometricExtents; Add(e.MinPoint); Add(e.MaxPoint); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { } // хоосон текст гэх мэт extents-гүй объект
            }
        }

        public static int Merge(MergeJob job, Action<string> log)
        {
            if (job.Files.Count == 0) throw new InvalidOperationException("Нэгтгэх DWG файл алга.");
            if (string.IsNullOrWhiteSpace(job.Output)) throw new InvalidOperationException("Гаралтын файлын зам хоосон байна.");

            var previous = HostApplicationServices.WorkingDatabase;
            int placed = 0;
            using (var target = new Database(true, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = target;
                    double gap = job.Gap, x = 0, rowTop = 0, rowHeight = 0;
                    int column = 0;

                    foreach (var file in job.Files)
                    {
                        log("Боловсруулж байна: " + Path.GetFileName(file));
                        using (var src = new Database(false, true))
                        {
                            src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                            src.CloseInput(true);
                            DimensionTextFix.Apply(src, out _, out _);
                            NameConflicts.Resolve(src, target, NameConflicts.SuffixFor(file));
                            ImageFiles.Relocate(src, file, job.Output, log);
                            if (placed == 0) target.Insunits = src.Insunits;

                            var name = UniqueBlockName(target, Path.GetFileNameWithoutExtension(file));
                            var info = ReadPaperSpace(src);
                            ObjectId sheetId = info != null
                                ? BuildSheetFromLayout(target, src, name, info, log)
                                : BuildSheetFromModel(target, src, name, out info);

                            if (!info.HasExtents) { log("  алгасав: хоосон sheet"); continue; }

                            var ext = info.Extents;
                            double w = ext.MaxPoint.X - ext.MinPoint.X, h = ext.MaxPoint.Y - ext.MinPoint.Y;
                            if (gap <= 0) gap = Math.Max(w, h) * 0.05;
                            if (column == job.Columns)
                            {
                                column = 0; x = 0; rowTop -= rowHeight + gap; rowHeight = 0;
                            }
                            var position = new Point3d(x - ext.MinPoint.X, rowTop - ext.MaxPoint.Y, 0);
                            AppendToModelSpace(target, new BlockReference(position, sheetId));
                            x += w + gap;
                            rowHeight = Math.Max(rowHeight, h);
                            column++;
                            placed++;
                        }
                    }

                    if (placed == 0) throw new InvalidOperationException("Нэг ч sheet байрлуулж чадсангүй.");
                    ZoomExtents(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output)));
                    if (File.Exists(job.Output)) File.Delete(job.Output);
                    target.SaveAs(job.Output, DwgVersion.Current);
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
            return placed;
        }

        /// <summary>Агуулгатай эхний (tab order-оор) layout-ийг уншина. Layout байхгүй бол null.</summary>
        static PaperSpaceInfo ReadPaperSpace(Database src)
        {
            using (var tr = src.TransactionManager.StartTransaction())
            {
                Layout layout = null;
                var layouts = (DBDictionary)tr.GetObject(src.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layouts)
                {
                    var l = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (l.ModelType) continue;
                    var b = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                    if (!b.Cast<ObjectId>().Any()) continue;
                    if (layout == null || l.TabOrder < layout.TabOrder) layout = l;
                }
                if (layout == null) return null;

                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var ids = btr.Cast<ObjectId>().ToList();
                var viewports = ids.Select(id => tr.GetObject(id, OpenMode.ForRead) as Viewport).Where(v => v != null).ToList();

                var overall = FindPaperSpaceViewport(tr, btr);
                var clipEntities = new HashSet<ObjectId>(viewports.Where(v => v.NonRectClipOn).Select(v => v.NonRectClipEntityId));

                var info = new PaperSpaceInfo();
                foreach (var id in ids)
                {
                    if (id == overall || clipEntities.Contains(id)) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    if (ent is Viewport vp)
                    {
                        var vi = ToViewportInfo(tr, vp);
                        info.Viewports.Add(vi);
                        foreach (var p in vi.Boundary) info.Add(p);
                    }
                    else
                    {
                        info.EntityIds.Add(id);
                        info.Add(ent);
                    }
                }
                tr.Commit();
                return info;
            }
        }

        /// <summary>
        /// Layout-ийн өөрийн (paper space) viewport: харах цэг нь эх дээр, өөрийн төв рүүгээ хардаг.
        /// Масштаб нь заавал 1 биш (Revit-ийн export-д 0.75 байдаг).
        /// Layout.GetViewports()-ийн дараалал үүнийг баталгаажуулдаггүй тул шинж чанараар нь танина.
        /// </summary>
        internal static ObjectId FindPaperSpaceViewport(Transaction tr, BlockTableRecord layoutBtr) =>
            layoutBtr.Cast<ObjectId>()
                .Select(id => tr.GetObject(id, OpenMode.ForRead) as Viewport)
                .FirstOrDefault(v => v != null && LooksLikePaperSpaceViewport(v))?.ObjectId ?? ObjectId.Null;

        static bool LooksLikePaperSpaceViewport(Viewport vp) =>
            vp.ViewTarget.DistanceTo(Point3d.Origin) < 1e-6 &&
            vp.ViewCenter.GetDistanceTo(new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y)) < 1e-3;

        static ViewportInfo ToViewportInfo(Transaction tr, Viewport vp)
        {
            List<Point3d> boundary = null;
            if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull)
            {
                var clip = tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) as Entity;
                if (clip is Polyline pl)
                    boundary = Enumerable.Range(0, pl.NumberOfVertices).Select(pl.GetPoint3dAt).ToList();
                else if (clip != null)
                    boundary = Rectangle(clip.GeometricExtents.MinPoint, clip.GeometricExtents.MaxPoint);
            }
            if (boundary == null)
            {
                var half = new Vector3d(vp.Width / 2, vp.Height / 2, 0);
                boundary = Rectangle(vp.CenterPoint - half, vp.CenterPoint + half);
            }
            return new ViewportInfo
            {
                ModelToPaper = ModelToPaper(vp),
                Boundary = boundary,
                IsPlan = vp.ViewDirection.GetNormal().IsEqualTo(Vector3d.ZAxis)
            };
        }

        /// <summary>Model space (WCS) -> paper space хувиргалт: WCS -> DCS -> PSDCS.</summary>
        internal static Matrix3d ModelToPaper(Viewport vp)
        {
            var dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
                           Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
                           Matrix3d.PlaneToWorld(vp.ViewDirection);
            var dcsToPaper = Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint) *
                             Matrix3d.Displacement(vp.CenterPoint - new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0));
            return dcsToPaper * dcsToWcs.Inverse();
        }

        static ObjectId BuildSheetFromLayout(Database target, Database src, string name, PaperSpaceInfo info, Action<string> log)
        {
            // Model space бүхэлдээ нэг block болно; viewport бүр түүнийг масштаблаж, clip хийж харуулна
            var modelBlockId = target.Insert(UniqueBlockName(target, name + "_MODEL"), src, true);

            ObjectId sheetId;
            using (var tr = target.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(target.BlockTableId, OpenMode.ForWrite);
                var sheet = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
                sheetId = bt.Add(sheet);
                tr.AddNewlyCreatedDBObject(sheet, true);
                tr.Commit();
            }

            if (info.EntityIds.Count > 0)
                src.WblockCloneObjects(info.EntityIds, sheetId, new IdMapping(), DuplicateRecordCloning.Ignore, false);

            using (var tr = target.TransactionManager.StartTransaction())
            {
                var sheet = (BlockTableRecord)tr.GetObject(sheetId, OpenMode.ForWrite);
                var model = (BlockTableRecord)tr.GetObject(modelBlockId, OpenMode.ForRead);
                foreach (var vp in info.Viewports)
                {
                    // Block-ийн base point дээр байрлуулбал хувиргалт нь яг ModelToPaper болно
                    var br = new BlockReference(model.Origin, modelBlockId);
                    sheet.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    br.TransformBy(vp.ModelToPaper);
                    if (vp.IsPlan) Clip(tr, br, vp.Boundary);
                    else log("  анхааруулга: plan биш viewport тул clip хийгээгүй");
                }
                tr.Commit();
            }
            return sheetId;
        }

        static ObjectId BuildSheetFromModel(Database target, Database src, string name, out PaperSpaceInfo info)
        {
            info = new PaperSpaceInfo();
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(src), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent) info.Add(ent);
                tr.Commit();
            }
            return target.Insert(name, src, true);
        }

        /// <summary>XCLIP: хил нь block-ийн өөрийн координатаар өгөгдөнө.</summary>
        static void Clip(Transaction tr, BlockReference br, IEnumerable<Point3d> paperBoundary)
        {
            var toBlock = br.BlockTransform.Inverse();
            var pts = new Point2dCollection();
            foreach (var p in paperBoundary)
            {
                var q = p.TransformBy(toBlock);
                pts.Add(new Point2d(q.X, q.Y));
            }
            var filter = new SpatialFilter
            {
                Definition = new SpatialFilterDefinition(pts, Vector3d.ZAxis, 0.0, 0.0, 0.0, true)
            };

            if (br.ExtensionDictionary.IsNull) br.CreateExtensionDictionary();
            var extDict = (DBDictionary)tr.GetObject(br.ExtensionDictionary, OpenMode.ForWrite);
            var filters = new DBDictionary();
            extDict.SetAt("ACAD_FILTER", filters);
            tr.AddNewlyCreatedDBObject(filters, true);
            filters.SetAt("SPATIAL", filter);
            tr.AddNewlyCreatedDBObject(filter, true);
        }

        static void AppendToModelSpace(Database db, Entity ent)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                tr.Commit();
            }
        }

        internal static void ZoomExtents(Database db)
        {
            db.UpdateExt(true);
            var min = db.Extmin; var max = db.Extmax;
            if (min.X > max.X) return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var vt = (ViewportTable)tr.GetObject(db.ViewportTableId, OpenMode.ForRead);
                foreach (ObjectId id in vt)
                {
                    var vtr = (ViewportTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    double aspect = vtr.Width > 0 && vtr.Height > 0 ? vtr.Width / vtr.Height : 1.6;
                    double height = Math.Max(max.Y - min.Y, (max.X - min.X) / aspect) * 1.05;
                    vtr.CenterPoint = new Point2d((min.X + max.X) / 2, (min.Y + max.Y) / 2);
                    vtr.Height = height;
                    vtr.Width = height * aspect;
                }
                tr.Commit();
            }
        }

        static List<Point3d> Rectangle(Point3d min, Point3d max) => new List<Point3d>
        {
            new Point3d(min.X, min.Y, 0), new Point3d(max.X, min.Y, 0),
            new Point3d(max.X, max.Y, 0), new Point3d(min.X, max.Y, 0)
        };

        static string UniqueBlockName(Database db, string baseName)
        {
            var clean = new string(baseName.Select(c => "<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c).ToArray()).Trim();
            if (clean.Length == 0) clean = "SHEET";
            if (clean.Length > 200) clean = clean.Substring(0, 200);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var name = clean;
                for (int i = 2; bt.Has(name); i++) name = clean + "_" + i;
                tr.Commit();
                return name;
            }
        }
    }
}
