// EasyLib — LCSC / EasyEDA components for Altium Designer.
// Standalone app that writes native Altium libraries (EasyLib.SchLib / EasyLib.PcbLib).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("EasyLib")]
[assembly: System.Reflection.AssemblyProduct("EasyLib for Altium Designer")]
[assembly: System.Reflection.AssemblyVersion("2.8.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.8.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.5", FrameworkDisplayName = ".NET Framework 4.5")]

namespace EasyLib
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // command line mode (used for testing / batch import):
            //   EasyLib.exe --add <LCSC|raw.json> [...] --lib <folder> [--no3d]
            //   EasyLib.exe --render3d <raw.json> <obj> <out.png>
            if (args.Length > 0 && args[0].StartsWith("--")) return Cli(args);

            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.InitScale();
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "EasyLib — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.AddMessageFilter(new WheelFilter());
            Application.Run(new MainForm());
            return 0;
        }

        static Part LoadPart(string arg)
        {
            Dictionary<string, object> res;
            if (File.Exists(arg))
            {
                object j = JsonUtil.Parse(File.ReadAllText(arg));
                res = Get.D(j, "result") ?? (Dictionary<string, object>)j;
            }
            else res = EasyEda.Component(arg);
            if (res == null) throw new Exception("no CAD data: " + arg);
            return Converter.Convert(res);
        }

        static int Cli(string[] args)
        {
            try
            {
                if (args[0] == "--render3d")
                {
                    Part p = LoadPart(args[1]);
                    Scene s = Obj3D.Build(p, File.ReadAllText(args[2]), true);
                    int w = 640, h = 480;
                    using (System.Drawing.Bitmap b = Obj3D.ToBitmap(Obj3D.Render(s, w, h, -30, -58, 1, 0, 0), w, h))
                        b.Save(args[3], System.Drawing.Imaging.ImageFormat.Png);
                    using (System.Drawing.Bitmap b = Obj3D.ToBitmap(Obj3D.Render(s, w, h, 0, 0, 1, 0, 0), w, h))
                        b.Save(Path.ChangeExtension(args[3], ".top.png"), System.Drawing.Imaging.ImageFormat.Png);
                    using (System.Drawing.Bitmap b = Preview.Symbol(p, 400, 300)) b.Save(Path.ChangeExtension(args[3], ".sym.png"));
                    using (System.Drawing.Bitmap b = Preview.Footprint(p, 400, 300)) b.Save(Path.ChangeExtension(args[3], ".fp.png"));
                    return 0;
                }
                if (args[0] == "--search" && args.Length > 1)
                {
                    // --search <query> [--source all|lcsc|user|kicad] [--exact] [--page N]
                    string source = "all"; bool exact = false; int page = 1;
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i] == "--source" && i + 1 < args.Length) source = args[++i];
                        else if (args[i] == "--exact") exact = true;
                        else if (args[i] == "--page" && i + 1 < args.Length) page = int.Parse(args[++i]);
                    }
                    SearchResult r = MainForm.SearchAll(args[1], page, source, exact, null);
                    Console.WriteLine("pages=" + r.TotalPages + " items=" + r.Items.Count);
                    foreach (SearchItem it in r.Items)
                        Console.WriteLine(it.Source + "\t" + (it.Lcsc == "" ? "-" : it.Lcsc) + "\t" + it.Mpn + "\t" + it.Manufacturer + "\t" + it.Class +
                                          "\tscore=" + it.Score + (it.Direct ? "\tdirect" : "") + (it.NoCad ? "\tnocad" : "") + (it.Uuid != "" ? "\tuuid" : ""));
                    if (r.Items.Count > 0 && Array.IndexOf(args, "--load") > 0)
                    {
                        SearchItem it = r.Items[0];
                        Dictionary<string, object> res = EasyEda.Component(it);
                        Console.WriteLine("loaded: " + (res == null ? "null" : Converter.Convert(res).Title));
                    }
                    return 0;
                }
                if (args[0] == "--shops" && args.Length > 1)
                {
                    // --shops <part number> [LCSC number]: what every shop answers; --shops-file <shop> <html> <part number>: parse a saved page
                    foreach (string id in Shops.Ids)
                    {
                        ShopResult r = Shops.Lookup(id, args[1], args.Length > 2 ? args[2] : "");
                        Console.WriteLine(Shops.Name(id) + ": " + r.Text + (r.State == 1 ? "  [" + r.Offer.Title + " | " + r.Offer.Maker + " | " + r.Offer.Url + "]" : ""));
                    }
                    return 0;
                }
                if (args[0] == "--shops-file" && args.Length > 3)
                {
                    string html = File.ReadAllText(args[2], System.Text.Encoding.UTF8);
                    List<ShopOffer> offers = args[1] == "chipdip" ? Shops.ParseChipDip(html, "https://www.chipdip.ru") : Shops.ParsePromelec(html);
                    foreach (ShopOffer o in offers) Console.WriteLine(o.Title + " | " + o.Maker + " | stock " + o.Stock + " | order " + o.OnOrder + " | " + o.Price + " | " + o.Term + " | " + o.Url);
                    ShopResult r = new ShopResult();
                    Shops.Pick(r, offers, args[3]);
                    Console.WriteLine("=> " + r.Text);
                    return 0;
                }
                if (args[0] == "--lib-info" && args.Length > 1)
                {
                    LibReport r = LibMaint.Analyze(args[1]);
                    if (r.Error != "") Console.WriteLine("error: " + r.Error);
                    Console.WriteLine("SchLib " + r.SchFileBytes + " bytes, " + r.Symbols + " symbols; PcbLib " + r.PcbFileBytes + " bytes, " + r.Footprints +
                                      " footprints, " + r.ModelCount + " models (" + r.ModelBytes + " bytes), unused models " + r.UnusedModels +
                                      ", footprints without symbol " + r.FootprintsWithoutSymbol);
                    foreach (LibPart lp in r.Parts)
                        Console.WriteLine((lp.Symbol == "" ? "-" : lp.Symbol) + "\t" + (lp.Footprint == "" ? "-" : lp.Footprint) + "\t" + lp.Total + "\t3D=" + lp.Models +
                                          (lp.FootprintMissing ? "\tMISSING-FP" : "") + (lp.SharedFootprint > 0 ? "\tshared" : ""));
                    return 0;
                }
                if ((args[0] == "--lib-delete" || args[0] == "--lib-strip3d" || args[0] == "--lib-prune") && args.Length > 1)
                {
                    List<string> syms = new List<string>(), fps = new List<string>();
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i] == "--sym" && i + 1 < args.Length) syms.Add(args[++i]);
                        else if (args[i] == "--fp" && i + 1 < args.Length) fps.Add(args[++i]);
                    }
                    LibMaint.Result r = args[0] == "--lib-delete" ? LibMaint.Delete(args[1], syms, fps)
                                      : args[0] == "--lib-strip3d" ? LibMaint.Strip3D(args[1], fps) : LibMaint.PruneModels(args[1]);
                    Console.WriteLine("symbols " + r.Symbols + ", footprints " + r.Footprints + ", stripped " + r.Stripped + ", models " + r.Models +
                                      ", " + r.BytesBefore + " -> " + r.BytesAfter + " bytes, backup " + r.Backup);
                    return 0;
                }
                if (args[0] == "--regress" || args[0] == "--regress-kicad")
                {
                    string lib = null, png = null, steps = null; int every = 50;
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i] == "--lib" && i + 1 < args.Length) lib = args[++i];
                        else if (args[i] == "--png" && i + 1 < args.Length) png = args[++i];
                        else if (args[i] == "--steps" && i + 1 < args.Length) steps = args[++i];
                        else if (args[i] == "--every" && i + 1 < args.Length) every = Math.Max(1, int.Parse(args[++i]));
                    }
                    if (lib == null) { Console.WriteLine("--lib <folder> is required"); return 2; }
                    return args[0] == "--regress" ? SelfTest.Regress(args[1], lib, png, steps) : SelfTest.RegressKiCad(args[1], lib, every);
                }
                if (args[0] == "--preview" && args.Length > 2)
                {
                    // --preview <raw.json> <out base> [--mil] [--light] [--hover x y] [--ruler x1 y1 x2 y2 (mil)]
                    Part p = LoadPart(args[1]);
                    bool mil = Array.IndexOf(args, "--mil") >= 0;
                    if (Array.IndexOf(args, "--light") >= 0) Theme.Set("light");
                    using (System.Drawing.Bitmap b = Preview.Symbol(p, 500, 400, Theme.Current.Preview)) b.Save(args[2] + ".sym.png");
                    using (System.Drawing.Bitmap b = Preview.Footprint(p, 500, 400, Theme.Current.Preview)) b.Save(args[2] + ".fp.png");
                    foreach (int w in new int[] { 1000, 520 })
                    {
                        DimView v = new DimView();
                        v.Size = new System.Drawing.Size(w, w == 1000 ? 650 : 420);
                        v.Mm = !mil;
                        v.SetPart(p);
                        int hi = Array.IndexOf(args, "--hover");
                        if (hi > 0) v.SimulateMouse(new System.Drawing.Point(int.Parse(args[hi + 1]), int.Parse(args[hi + 2])));
                        int ri = Array.IndexOf(args, "--ruler");
                        if (ri > 0) v.SimulateRuler(new PointD(double.Parse(args[ri + 1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(args[ri + 2], System.Globalization.CultureInfo.InvariantCulture)),
                                                    new PointD(double.Parse(args[ri + 3], System.Globalization.CultureInfo.InvariantCulture), double.Parse(args[ri + 4], System.Globalization.CultureInfo.InvariantCulture)));
                        using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(v.Width, v.Height))
                        {
                            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(b)) v.RenderTo(g);
                            b.Save(args[2] + ".dims" + w + ".png");
                        }
                        if (w == 1000)
                        {
                            FpAnalysis an = v.Analysis;
                            if (an == null) { Console.WriteLine("no pads"); continue; }
                            Console.WriteLine("pads smd=" + an.Smd + " th=" + an.Th + " npth=" + an.Npth + " pitch=" + Fmt.Mm(an.Pitch * 0.0254) + (an.PitchIsRow ? " (row)" : "") +
                                              " rows=" + Fmt.Mm(an.RowSpan * 0.0254) + " cols=" + Fmt.Mm(an.ColSpan * 0.0254));
                            Console.WriteLine("pad extent " + Fmt.Mm((an.PMaxX - an.PMinX) * 0.0254) + " x " + Fmt.Mm((an.PMaxY - an.PMinY) * 0.0254) +
                                              (an.HasBody ? ", body " + Fmt.Mm((an.BMaxX - an.BMinX) * 0.0254) + " x " + Fmt.Mm((an.BMaxY - an.BMinY) * 0.0254) + " " + an.BodyLabel : ""));
                            foreach (PadGroup pg in an.Groups) Console.WriteLine("group " + Fmt.Mm(pg.W * 0.0254) + " x " + Fmt.Mm(pg.H * 0.0254) + " x" + pg.Count + " first=" + pg.First.Name);
                            foreach (CheckRow r in an.Name) Console.WriteLine("name " + (r.State == 1 ? "OK " : r.State == 2 ? "WARN " : "- ") + r.Label + " | " + r.Value);
                        }
                    }
                    int k = 0;
                    foreach (SShape sh in ShapeOrder.Sorted(p.Shapes)) Console.WriteLine("shape " + (k++) + " " + sh.Kind + " fill=" + sh.Fill);
                    return 0;
                }
                if (args[0] == "--import")
                {
                    string lib = Settings.DefaultLibDir, png = null;
                    bool with3D = true;
                    List<string> files = new List<string>();
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i] == "--lib" && i + 1 < args.Length) lib = args[++i];
                        else if (args[i] == "--png" && i + 1 < args.Length) png = args[++i];
                        else if (args[i] == "--no3d") with3D = false;
                        else files.Add(args[i]);
                    }
                    ImportResult res = KiImport.FromPaths(files);
                    foreach (string n in res.Notes) Console.WriteLine("note: " + n);
                    if (res.Altium.Count > 0)
                    {
                        List<string> done = new List<string>();
                        AltiumImport.Apply(res.Altium, Path.Combine(lib, "EasyLib.SchLib"), Path.Combine(lib, "EasyLib.PcbLib"), done);
                        foreach (string dn in done) Console.WriteLine("altium: " + dn);
                    }
                    Console.WriteLine("vendor: " + (res.Vendor == "" ? "-" : res.Vendor) + ", parts: " + res.Parts.Count);
                    foreach (Part p in res.Parts)
                    {
                        string note;
                        LibResult r = PartAdder.Add(p, Path.Combine(lib, "EasyLib.SchLib"), Path.Combine(lib, "EasyLib.PcbLib"), with3D, out note);
                        Console.WriteLine((p.NoSymbol ? "[fp] " : "") + p.Title + ": units=" + p.PartCount + " pins=" + p.Pins.Count + " pads=" + p.Pads.Count +
                                          " -> " + r.LibRef + " / " + r.Footprint + (r.ModelAdded ? " +3D" : "") + note);
                        if (png != null)
                        {
                            Directory.CreateDirectory(png);
                            string b = Path.Combine(png, Regex.Replace(p.Title, "[^A-Za-z0-9_.-]", "_"));
                            if (!p.NoSymbol) using (System.Drawing.Bitmap bm = Preview.Symbol(p, 500, 400)) bm.Save(b + ".sym.png");
                            using (System.Drawing.Bitmap bm = Preview.Footprint(p, 500, 400)) bm.Save(b + ".fp.png");
                        }
                    }
                    return 0;
                }
                if (args[0] == "--kicad-search" && args.Length > 1)
                {
                    KiCadLib.EnsureIndex(delegate(string m) { Console.Error.WriteLine(m); });
                    Console.WriteLine("index: " + KiCadLib.Count + " symbols");
                    SearchResult sr = KiCadLib.Search(args[1], 1);
                    foreach (SearchItem it in sr.Items) Console.WriteLine(it.Uuid + " | " + it.Package);
                    if (args.Length > 3 && args[2] == "--add" && sr.Items.Count > 0)
                    {
                        Part p = KiCadLib.Load(sr.Items[0]);
                        string note;
                        LibResult r = PartAdder.Add(p, Path.Combine(args[3], "EasyLib.SchLib"), Path.Combine(args[3], "EasyLib.PcbLib"), true, out note);
                        Console.WriteLine("added " + r.LibRef + " / " + r.Footprint + (r.ModelAdded ? " +3D" : "") + note);
                    }
                    return 0;
                }
                if (args[0] == "--repair" && args.Length > 1)
                {
                    int a = AltiumLib.RepairSchLib(Path.Combine(args[1], "EasyLib.SchLib"));
                    int b = AltiumLib.RepairPcbLib(Path.Combine(args[1], "EasyLib.PcbLib"));
                    Console.WriteLine("repaired: SchLib " + a + ", PcbLib " + b);
                    return 0;
                }
                if (args[0] == "--add")
                {
                    string lib = Settings.DefaultLibDir;
                    bool with3D = true;
                    List<string> parts = new List<string>();
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i] == "--lib" && i + 1 < args.Length) lib = args[++i];
                        else if (args[i] == "--no3d") with3D = false;
                        else parts.Add(args[i]);
                    }
                    foreach (string a in parts)
                    {
                        Part p = LoadPart(a);
                        byte[] step = null;
                        ModelPlacement mp = null;
                        string stepName = null;
                        string stepFile = Environment.GetEnvironmentVariable("EASYLIB_STEP_" + p.Lcsc);
                        if (with3D && p.ModelUuid != "")
                            step = stepFile != null ? File.ReadAllBytes(stepFile) : EasyEda.Step(p.ModelUuid);
                        if (step != null) { mp = Step.Compute(p, step); stepName = (p.ModelTitle != "" ? p.ModelTitle : p.Lcsc) + ".step"; }
                        LibResult r = AltiumLib.Add(p, Path.Combine(lib, "EasyLib.SchLib"), Path.Combine(lib, "EasyLib.PcbLib"), step, stepName, mp);
                        Console.WriteLine(p.Lcsc + ": " + r.LibRef + (r.SymbolExisted ? " (exists)" : "") + " / " + r.Footprint +
                                          (r.FootprintExisted ? " (exists)" : "") + (r.ModelAdded ? " +3D" : ""));
                    }
                    return 0;
                }
                Console.WriteLine("usage: EasyLib.exe [--add <LCSC|json>... --lib <dir> [--no3d]] [--render3d <json> <obj> <png>]");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
