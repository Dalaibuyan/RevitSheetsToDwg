using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace SheetMerge
{
    /// <summary>
    /// Revit sheet дээрх зургийг (лого, гарын үсэг гэх мэт) DWG-ийн хажууд тусдаа PNG/JPG болгон export хийж,
    /// DWG-д түр хавтасны бүтэн замаар холбодог. Тэр хавтас устахад зураг алга болно. Тиймээс зургуудыг
    /// гаралтын DWG-ийн хажуугийн "&lt;нэр&gt;_images" хавтсанд хуулж, дахин холбоно.
    /// Эх файлыг санах ойд л өөрчилнө.
    /// </summary>
    static class ImageFiles
    {
        public static int Relocate(Database src, string srcFile, string output, Action<string> log)
        {
            var dictId = RasterImageDef.GetImageDictionary(src);
            if (dictId.IsNull) return 0;

            var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            var folderName = Path.GetFileNameWithoutExtension(output) + "_images";
            var folder = Path.Combine(outDir, folderName);
            var srcDir = Path.GetDirectoryName(Path.GetFullPath(srcFile));
            int copied = 0;
            using (var tr = src.TransactionManager.StartTransaction())
            {
                foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead))
                {
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef def)) continue;
                    var file = Find(def, srcDir);
                    if (file == null)
                    {
                        log("  анхааруулга: зургийн файл олдсонгүй: " + def.SourceFileName);
                        continue;
                    }
                    Directory.CreateDirectory(folder);
                    var dest = Path.Combine(folder, Path.GetFileName(file));
                    // Ижил нэртэй өөр зураг байвал дугаар нэмнэ
                    for (int i = 2; File.Exists(dest) && !SameContent(file, dest); i++)
                        dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "_" + i + Path.GetExtension(file));
                    if (!File.Exists(dest)) File.Copy(file, dest);

                    // Бүтэн замаар холбоно: ачаалагдсан зурагт AutoCAD харьцангуй зам хүлээж авдаггүй, харин
                    // буулгасан (unloaded) төлөв DWG-д хадгалагдаж зураг харагдахгүй болдог.
                    def.UpgradeOpen();
                    def.SourceFileName = dest;
                    try { if (!def.IsLoaded) def.Load(); } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    copied++;
                }
                tr.Commit();
            }
            return copied;
        }

        /// <summary>Зургийн файлын жинхэнэ байршлыг олно.</summary>
        static string Find(RasterImageDef def, string srcDir)
        {
            foreach (var path in new[] { def.ActiveFileName, def.SourceFileName })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var name = Path.GetFileName(path);
                var candidates = new[] { path, Path.Combine(srcDir, name), Path.Combine(srcDir, FixCyrillic(name)) };
                var hit = candidates.FirstOrDefault(SafeExists);
                if (hit != null) return hit;

                // Revit кирилл нэрийг ANSI-гаар эвдэж бичдэг тул нэрийн латин сүүлээр (жишээ нь "-34144737.png") хайна
                var tail = new string(name.Reverse().TakeWhile(c => c < 128).Reverse().ToArray());
                if (tail.Length >= 8 && Directory.Exists(srcDir))
                {
                    var matches = Directory.GetFiles(srcDir, "*" + tail);
                    if (matches.Length == 1) return matches[0];
                }
            }
            return null;
        }

        /// <summary>Windows-1252 гэж буруу уншигдсан Windows-1251 кирилл текстийг сэргээнэ ("ÆÈØÝÝ" -> "ЖИШЭЭ").</summary>
        static string FixCyrillic(string s)
        {
            try
            {
#if NET
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
                if (s.Any(c => c > 255)) return s;
                return Encoding.GetEncoding(1251).GetString(Encoding.GetEncoding(1252).GetBytes(s));
            }
            catch (ArgumentException) { return s; }
            catch (NotSupportedException) { return s; }
        }

        static bool SafeExists(string path)
        {
            try { return File.Exists(path); } catch (ArgumentException) { return false; }
        }

        static bool SameContent(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
