// Regression checks used during development (command line only):
//   EasyLib.exe --regress <folder with EasyEDA json> --lib <out folder> [--png <folder>] [--steps <folder>]
//   EasyLib.exe --regress-kicad <kicad-symbols.zip> --lib <out folder> [--every N]
// Every part is converted, previewed, measured and written to a library; problems are printed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    static class SelfTest
    {
        public static int Regress(string jsonDir, string libDir, string pngDir, string stepDir)
        {
            Directory.CreateDirectory(libDir);
            if (pngDir != null) Directory.CreateDirectory(pngDir);
            string sch = Path.Combine(libDir, "EasyLib.SchLib"), pcb = Path.Combine(libDir, "EasyLib.PcbLib");
            int ok = 0, failed = 0, warned = 0;
            string[] files = Directory.GetFiles(jsonDir, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                List<string> warn = new List<string>();
                try
                {
                    object j = JsonUtil.Parse(File.ReadAllText(f, Encoding.UTF8));
                    Dictionary<string, object> res = Get.D(j, "result") ?? j as Dictionary<string, object>;
                    if (res == null || Get.D(res, "dataStr") == null) { Console.WriteLine("SKIP\t" + name + "\tno CAD data"); continue; }
                    Part p = Converter.Convert(res);
                    Check(p, warn);
                    using (Bitmap b = Preview.Symbol(p, 400, 300))
                        if (pngDir != null) b.Save(Path.Combine(pngDir, name + ".sym.png"));
                    using (Bitmap b = Preview.Footprint(p, 400, 300))
                        if (pngDir != null) b.Save(Path.Combine(pngDir, name + ".fp.png"));
                    if (p.Pads.Count > 0)
                    {
                        FpAnalysis an = FpAnalysis.Analyze(p);
                        foreach (CheckRow r in an.Name) if (r.State == 2) warn.Add("name check: " + r.Label + " / " + r.Value);
                        DimView v = new DimView();
                        v.Size = new Size(900, 600);
                        v.SetPart(p);
                        using (Bitmap b = new Bitmap(900, 600))
                        {
                            using (Graphics g = Graphics.FromImage(b)) v.RenderTo(g);
                            if (pngDir != null) b.Save(Path.Combine(pngDir, name + ".dims.png"));
                        }
                        v.Dispose();
                    }
                    byte[] step = null; ModelPlacement mp = null;
                    if (stepDir != null && p.ModelUuid != "")
                    {
                        string sf = Path.Combine(stepDir, p.ModelUuid + ".step");
                        if (File.Exists(sf)) { step = File.ReadAllBytes(sf); mp = Step.Compute(p, step); }
                    }
                    LibResult lr = AltiumLib.Add(p, sch, pcb, step, step != null ? (p.ModelTitle != "" ? p.ModelTitle : name) + ".step" : null, mp);
                    if (lr.LibRef == "" && !p.NoSymbol) warn.Add("no symbol written");
                    Console.WriteLine((warn.Count > 0 ? "WARN" : "OK") + "\t" + name + "\t" + p.Title + "\t" + lr.LibRef + " / " + lr.Footprint +
                                      (lr.SymbolExisted ? " (sym exists)" : "") + (lr.FootprintExisted ? " (fp exists)" : "") + (lr.ModelAdded ? " +3D" : "") +
                                      "\tpins=" + p.Pins.Count + " pads=" + p.Pads.Count + " units=" + p.PartCount +
                                      (warn.Count > 0 ? "\t" + string.Join("; ", warn.ToArray()) : ""));
                    if (warn.Count > 0) warned++; else ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL\t" + name + "\t" + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' ') + "\t" + FirstFrame(ex));
                }
            }
            Console.WriteLine("SUMMARY\tok=" + ok + " warn=" + warned + " fail=" + failed);
            return failed > 0 ? 1 : 0;
        }

        static string FirstFrame(Exception ex)
        {
            string st = ex.StackTrace ?? "";
            int i = st.IndexOf(" at ", StringComparison.Ordinal);
            string line = i >= 0 ? st.Substring(i + 4) : st;
            int n = line.IndexOf('\n');
            return (n > 0 ? line.Substring(0, n) : line).Trim();
        }

        // sanity checks of a converted part
        public static void Check(Part p, List<string> warn)
        {
            if (!p.NoSymbol)
            {
                if (p.Pins.Count == 0) warn.Add("symbol without pins");
                foreach (SPin pin in p.Pins)
                {
                    if (double.IsNaN(pin.X) || double.IsNaN(pin.Y) || double.IsInfinity(pin.X) || double.IsInfinity(pin.Y)) { warn.Add("pin " + pin.Num + " at NaN"); break; }
                    if (pin.Len <= 0) { warn.Add("pin " + pin.Num + " has zero length"); break; }
                    if (pin.Unit < 1 || pin.Unit > p.PartCount) { warn.Add("pin " + pin.Num + " in unit " + pin.Unit + " of " + p.PartCount); break; }
                }
                foreach (SShape s in p.Shapes)
                {
                    foreach (PointD q in s.Pts)
                        if (double.IsNaN(q.X) || double.IsNaN(q.Y) || Math.Abs(q.X) > 1e6 || Math.Abs(q.Y) > 1e6) { warn.Add("shape " + s.Kind + " with bad point"); goto shapesDone; }
                    if ((s.Kind == "RECT" && s.Pts.Count < 2) || ((s.Kind == "LINE" || s.Kind == "POLY") && s.Pts.Count < 2)) { warn.Add("shape " + s.Kind + " with too few points"); goto shapesDone; }
                }
            shapesDone:
                // pins of a pad-less symbol are fine; otherwise every pin should have a pad
                if (p.Pads.Count > 0)
                {
                    HashSet<string> pads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (FPad pad in p.Pads) pads.Add(pad.Name);
                    List<string> missing = new List<string>();
                    foreach (SPin pin in p.Pins) if (pin.Num != "" && !pads.Contains(pin.Num) && !missing.Contains(pin.Num)) missing.Add(pin.Num);
                    if (missing.Count > 0) warn.Add("pins without pads: " + string.Join(",", missing.GetRange(0, Math.Min(8, missing.Count)).ToArray()));
                }
            }
            foreach (FPad pad in p.Pads)
            {
                if (double.IsNaN(pad.X) || double.IsNaN(pad.Y) || Math.Abs(pad.X) > 1e5 || Math.Abs(pad.Y) > 1e5) { warn.Add("pad " + pad.Name + " at a bad position"); break; }
                if (pad.Shape != 'P' && (pad.SX <= 0 || pad.SY <= 0) && !(pad.Hole > 0)) { warn.Add("pad " + pad.Name + " has zero size"); break; }
                if (pad.Shape == 'P' && (pad.Poly == null || pad.Poly.Count < 3)) { warn.Add("polygon pad " + pad.Name + " without outline"); break; }
            }
        }

        // all symbols of the KiCad library archive: convert every one, write every N-th to a library
        public static int RegressKiCad(string zipPath, string libDir, int every)
        {
            Directory.CreateDirectory(libDir);
            string sch = Path.Combine(libDir, "EasyLib.SchLib"), pcb = Path.Combine(libDir, "EasyLib.PcbLib");
            int total = 0, failed = 0, warned = 0, written = 0;
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                // group the entries by library (a .kicad_sym file or a .kicad_symdir folder)
                Dictionary<string, List<ZipArchiveEntry>> libs = new Dictionary<string, List<ZipArchiveEntry>>();
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    if (!e.FullName.EndsWith(".kicad_sym", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = e.FullName.Split('/');
                    string lib = parts.Length >= 2 && parts[parts.Length - 2].EndsWith(".kicad_symdir") ? parts[parts.Length - 2] : parts[parts.Length - 1];
                    List<ZipArchiveEntry> l;
                    if (!libs.TryGetValue(lib, out l)) libs[lib] = l = new List<ZipArchiveEntry>();
                    l.Add(e);
                }
                List<string> names = new List<string>(libs.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string lib in names)
                {
                    Dictionary<string, SNode> syms = new Dictionary<string, SNode>();
                    foreach (ZipArchiveEntry e in libs[lib])
                        using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8))
                            foreach (SNode s in KiCad.SymbolsOf(r.ReadToEnd())) if (!syms.ContainsKey(s.S(1))) syms[s.S(1)] = s;
                    Func<string, SNode> lookup = delegate(string n) { SNode s; return syms.TryGetValue(n, out s) ? s : null; };
                    foreach (KeyValuePair<string, SNode> kv in syms)
                    {
                        total++;
                        List<string> warn = new List<string>();
                        try
                        {
                            Part p = new Part();
                            p.Source = "kicad"; p.Official = false;
                            KiCad.ConvertSymbol(p, kv.Value, lookup);
                            if (p.Prefix.StartsWith("#")) continue;
                            Check(p, warn);
                            if (total % every == 0)
                            {
                                using (Bitmap b = Preview.Symbol(p, 300, 240)) { }
                                p.Title = lib.Replace(".kicad_symdir", "").Replace(".kicad_sym", "") + "_" + p.Title;
                                LibResult lr = AltiumLib.Add(p, sch, pcb, null, null, null);
                                if (!lr.SymbolExisted) written++;
                            }
                            if (warn.Count > 0)
                            {
                                warned++;
                                if (warned <= 200) Console.WriteLine("WARN\t" + lib + ":" + kv.Key + "\t" + string.Join("; ", warn.ToArray()));
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            if (failed <= 200) Console.WriteLine("FAIL\t" + lib + ":" + kv.Key + "\t" + ex.GetType().Name + ": " + ex.Message + "\t" + FirstFrame(ex));
                        }
                    }
                }
            }
            Console.WriteLine("SUMMARY\tsymbols=" + total + " warn=" + warned + " fail=" + failed + " written=" + written);
            return failed > 0 ? 1 : 0;
        }
    }
}
