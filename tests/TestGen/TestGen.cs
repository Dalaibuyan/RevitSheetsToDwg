using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(TestGen.Cmd))]

namespace TestGen
{
    /// <summary>Revit-ийн sheet export шиг туршилтын DWG-үүд (title block + 1 viewport) үүсгэнэ. TEST_OUT хавтсанд бичнэ.</summary>
    public class Cmd
    {
        [CommandMethod("MAKETESTSHEETS")]
        public void Make()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            var dir = Environment.GetEnvironmentVariable("TEST_OUT");
            try
            {
                Sheet(Path.Combine(dir, "001_A101 - Plan.dwg"), 0.01, new Point2d(5000, 4000), "PLAN A101");
                Sheet(Path.Combine(dir, "002_A102 - Detail.dwg"), 0.02, new Point2d(2000, 2000), "DETAIL A102");
                Sheet(Path.Combine(dir, "003_A103 - Plan2.dwg"), 0.01, new Point2d(5000, 4000), "PLAN A103");
                ed.WriteMessage("\nTESTGEN OK");
            }
            catch (System.Exception ex) { ed.WriteMessage("\nTESTGEN ERR " + ex); }
        }

        /// <summary>DUMP_FILE файлын layout бүрийн объект, viewport-уудын шинжийг хэвлэнэ.</summary>
        [CommandMethod("DUMPLAYOUTS")]
        public void Dump()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            var file = Environment.GetEnvironmentVariable("DUMP_FILE");
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        var l = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                        var vps = l.GetViewports();
                        ed.WriteMessage($"\nDUMP LAYOUT '{l.LayoutName}' tab={l.TabOrder} getViewports={vps?.Count}");
                        ed.WriteMessage($"\nDUMP   PLOT dev={l.PlotConfigurationName} media={l.CanonicalMediaName} paper={l.PlotPaperSize} margins={l.PlotPaperMargins.MinPoint}-{l.PlotPaperMargins.MaxPoint} rot={l.PlotRotation} type={l.PlotType} window={l.PlotWindowArea.MinPoint}-{l.PlotWindowArea.MaxPoint} origin={l.PlotOrigin} centered={l.PlotCentered} std={l.UseStandardScale}/{l.StdScaleType} units={l.PlotPaperUnits} limits={l.Limits.MinPoint}-{l.Limits.MaxPoint} style={l.CurrentStyleSheet}");
                        var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            if (ent is Viewport vp)
                                ed.WriteMessage($"\nDUMP   VP {id.Handle} on={vp.On} c={vp.CenterPoint} w={vp.Width} h={vp.Height} vc={vp.ViewCenter} vt={vp.ViewTarget} scale={vp.CustomScale} layer={vp.Layer} inList={(vps != null && vps.Contains(id))}");
                            else if (ent is BlockReference br)
                            {
                                string ext = "";
                                try { var e = br.GeometricExtents; ext = e.MinPoint + "-" + e.MaxPoint; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                                ed.WriteMessage($"\nDUMP   BlockReference {id.Handle} name={br.Name} ext={ext}");
                            }
                            else
                                ed.WriteMessage($"\nDUMP   {ent.GetType().Name} {id.Handle}");
                        }
                    }
                    tr.Commit();
                }
            }
        }

        /// <summary>DUMP_FILE-ийн dimension style-уудын гол шинжийг хэвлэнэ.</summary>
        [CommandMethod("DUMPDIMSTYLES")]
        public void DumpDimStyles()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(Environment.GetEnvironmentVariable("DUMP_FILE"), FileOpenMode.OpenForReadAndAllShare, true, "");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        var d = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        ed.WriteMessage($"\nDIM|{d.Name}|lfac={d.Dimlfac}|scale={d.Dimscale}|txt={d.Dimtxt}|asz={d.Dimasz}");
                    }
                    tr.Commit();
                }
            }
        }

        /// <summary>DUMP_FILE-ийн model space-ийн эхний хэмжээсүүдийн утгыг хэвлэнэ.</summary>
        [CommandMethod("DUMPDIMS")]
        public void DumpDims()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(Environment.GetEnvironmentVariable("DUMP_FILE"), FileOpenMode.OpenForReadAndAllShare, true, "");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    int n = 0;
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Dimension d)) continue;
                        // Блокт зурагдсан текст (Revit-ийн харуулж буй утга)
                        string shown = "";
                        if (!d.DimBlockId.IsNull)
                            foreach (ObjectId bid in (BlockTableRecord)tr.GetObject(d.DimBlockId, OpenMode.ForRead))
                            {
                                var o = tr.GetObject(bid, OpenMode.ForRead);
                                if (o is MText m) shown += m.Contents; else if (o is DBText t) shown += t.TextString;
                            }
                        string kinds = "";
                        if (!d.DimBlockId.IsNull)
                            foreach (ObjectId bid in (BlockTableRecord)tr.GetObject(d.DimBlockId, OpenMode.ForRead))
                            {
                                var o = tr.GetObject(bid, OpenMode.ForRead);
                                kinds += o.GetType().Name + (o is BlockReference br ? "(" + br.Name + ")" : "") + " ";
                            }
                        ed.WriteMessage($"\nDIMS|{d.DimensionText}|{shown}|{d.GetType().Name}|{kinds}");
                        if (++n >= 100000) break;
                    }
                    tr.Commit();
                }
            }
        }

        /// <summary>DUMP_FILE-ийн raster зургийн тодорхойлолт (файлын зам) болон зургийн объектуудыг хэвлэнэ.</summary>
        [CommandMethod("DUMPIMAGES")]
        public void DumpImages()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(Environment.GetEnvironmentVariable("DUMP_FILE"), FileOpenMode.OpenForReadAndAllShare, true, "");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dictId = RasterImageDef.GetImageDictionary(db);
                    if (!dictId.IsNull)
                        foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead))
                        {
                            var def = (RasterImageDef)tr.GetObject(e.Value, OpenMode.ForRead);
                            ed.WriteMessage($"\nDUMP DEF {e.Key}|src={def.SourceFileName}|active={def.ActiveFileName}|loaded={def.IsLoaded}");
                        }
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId btrId in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                            if (tr.GetObject(id, OpenMode.ForRead) is RasterImage img)
                                ed.WriteMessage($"\nDUMP IMG in={btr.Name} def={img.ImageDefId.Handle} origin={img.Orientation.Origin} shown={img.ShowImage}");
                    }
                    tr.Commit();
                }
            }
        }

        /// <summary>TAB_INDEX (1-ээс эхэлсэн tab order) дугаартай layout-ыг идэвхжүүлнэ.</summary>
        [CommandMethod("SETTAB")]
        public void SetTab()
        {
            int index = int.Parse(Environment.GetEnvironmentVariable("TAB_INDEX"));
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layouts)
                {
                    var l = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (l.TabOrder == index) LayoutManager.Current.CurrentLayout = l.LayoutName;
                }
                tr.Commit();
            }
        }

        static void Sheet(string path, double scale, Point2d viewCenter, string title)
        {
            var prev = HostApplicationServices.WorkingDatabase;
            using (var db = new Database(true, true))
            {
                HostApplicationServices.WorkingDatabase = db;
                try
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        Add(tr, ms, Rect(0, 0, 10000, 8000));
                        Add(tr, ms, new Line(new Point3d(0, 0, 0), new Point3d(10000, 8000, 0)));
                        Add(tr, ms, new Circle(new Point3d(5000, 4000, 0), Vector3d.ZAxis, 1500));
                        Add(tr, ms, new DBText { Position = new Point3d(500, 500, 0), Height = 400, TextString = "MODEL " + title });
                        // viewport-оос гадна байгаа объект
                        Add(tr, ms, new Circle(new Point3d(30000, 4000, 0), Vector3d.ZAxis, 3000));

                        var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId("Layout1"), OpenMode.ForWrite);
                        var ps = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                        Add(tr, ps, Rect(0, 0, 420, 297));
                        Add(tr, ps, Rect(10, 10, 410, 287));
                        Add(tr, ps, new DBText { Position = new Point3d(300, 20, 0), Height = 8, TextString = title });
                        var vp = new Viewport { CenterPoint = new Point3d(180, 150, 0), Width = 300, Height = 240 };
                        Add(tr, ps, vp);
                        vp.ViewDirection = Vector3d.ZAxis;
                        vp.ViewTarget = Point3d.Origin;
                        vp.ViewCenter = viewCenter;
                        vp.CustomScale = scale;
                        tr.Commit();
                    }
                    db.SaveAs(path, DwgVersion.Current);
                }
                finally { HostApplicationServices.WorkingDatabase = prev; }
            }
        }

        static Polyline Rect(double x0, double y0, double x1, double y1)
        {
            var p = new Polyline();
            p.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
            p.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
            p.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
            p.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
            p.Closed = true;
            return p;
        }

        static void Add(Transaction tr, BlockTableRecord b, Entity e)
        {
            b.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }
    }
}
