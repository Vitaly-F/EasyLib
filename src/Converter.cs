// EasyEDA (LCSC) component JSON -> intermediate part model.
// Data source: JLCEDA/EasyEDA Official Library (https://easyeda.com, https://lceda.cn).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EasyLib
{
    // ======================================================================
    // EasyEDA -> intermediate model
    // ======================================================================

    static class Converter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string SourceText = "JLCEDA/EasyEDA Official Library (https://easyeda.com, https://lceda.cn)";
        const double SymScale = 10.0;   // 1 EasyEDA symbol unit = 10 mil
        const double FpScale = 10.0;    // 1 EasyEDA footprint unit = 10 mil

        static string S(object o, string k) { return Get.S(o, k); }

        public static Part Convert(Dictionary<string, object> res)
        {
            Part p = new Part();
            Dictionary<string, object> ds = Get.D(res, "dataStr");
            Dictionary<string, object> head = Get.D(ds, "head");
            Dictionary<string, object> cp = Get.D(head, "c_para") ?? new Dictionary<string, object>();
            Dictionary<string, object> lcsc = Get.D(res, "lcsc");

            p.Title = Clean(S(res, "title"));
            if (p.Title == "") p.Title = Clean(S(cp, "name"));
            p.Uuid = S(res, "uuid");
            p.Official = lcsc != null && S(lcsc, "number") != "";
            p.Lcsc = lcsc != null ? S(lcsc, "number") : "";
            if (p.Lcsc == "") p.Lcsc = SupplierPart(cp);
            Dictionary<string, object> owner = Get.D(res, "owner");
            p.Author = Clean(owner != null ? S(owner, "username") : "");
            if (p.Author == "") p.Author = Clean(S(cp, "Contributor"));
            // "R12", "U?", "C1?" -> "R?", "U?", "C?"
            p.Prefix = Regex.Replace(Clean(S(cp, "pre")), "[0-9?]+$", "");
            if (p.Prefix == "") p.Prefix = "U";
            p.Prefix += "?";
            p.Desc = Clean(S(res, "description"));

            string mpn = Clean(FirstOf(cp, "Manufacturer Part", "Manufacturer Part Number", "BOM_Manufacturer Part", "MPN"));
            string man = Clean(FirstOf(cp, "Manufacturer", "BOM_Manufacturer"));
            string value = Clean(S(cp, "Value"));
            string cls = p.Official ? Clean(S(cp, "JLCPCB Part Class")) : "";
            string url = p.Official ? S(lcsc, "url") : (p.Uuid != "" ? "https://easyeda.com/component/" + p.Uuid : "");
            string datasheet = p.Official && p.Lcsc != "" ? "https://www.lcsc.com/datasheet/" + p.Lcsc + ".pdf" : "";
            if (datasheet == "")
            {
                string link = FirstOf(cp, "link", "Datasheet", "datasheet");
                if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) datasheet = link;
                else if (p.Lcsc != "") datasheet = "https://www.lcsc.com/datasheet/" + p.Lcsc + ".pdf";
            }

            if (value != "") p.Params["Value"] = value;
            if (man != "") p.Params["Manufacturer"] = man;
            if (mpn != "") p.Params["Manufacturer Part Number"] = mpn;
            if (p.Lcsc != "") { p.Params["LCSC Part"] = p.Lcsc; p.Params["Supplier"] = "LCSC"; p.Params["Supplier Part Number"] = p.Lcsc; }
            if (cls != "") p.Params["JLCPCB Part Class"] = cls;
            if (datasheet != "") p.Params["Datasheet"] = datasheet;
            if (p.Official && url != "") p.Params["LCSC URL"] = url;
            if (p.Official) p.Params["Source"] = Converter.SourceText;
            else
            {
                p.Params["Source"] = "EasyEDA user library" + (p.Author != "" ? " (author: " + p.Author + ")" : "") + (url != "" ? ", " + url : "");
                if (p.Uuid != "") p.Params["EasyEDA Component"] = p.Uuid;
            }

            p.Info["LCSC"] = p.Lcsc;
            p.Info["Title"] = p.Title;
            p.Info["MPN"] = p.Official ? mpn : p.Title;
            p.Info["Manufacturer"] = man;
            p.Info["Value"] = value;
            p.Info["Class"] = p.Official ? (cls.StartsWith("Basic") ? "Basic" : cls.StartsWith("Extended") ? "Extended" : cls) : "Сообщество";
            p.Info["Description"] = p.Desc;
            p.Info["Datasheet"] = datasheet;
            p.Info["URL"] = url;
            p.Info["Official"] = p.Official ? "yes" : "no";
            p.Info["Author"] = p.Author;
            if (lcsc != null)
            {
                p.Info["Stock"] = S(lcsc, "stock");
                p.Info["Price"] = S(lcsc, "price");
                p.Info["MinQty"] = S(lcsc, "min");
            }

            // ---- symbol ----
            object[] subparts = Get.A(res, "subparts");
            if (subparts.Length > 0)
            {
                p.PartCount = subparts.Length;
                Dictionary<string, object> h0 = Get.D(Get.D(subparts[0], "dataStr"), "head");
                double ox = Get.F(S(h0, "x")), oy = Get.F(S(h0, "y"));
                List<object[]> units = new List<object[]>();
                for (int i = 0; i < subparts.Length; i++)
                    units.Add(Get.A(Get.D(subparts[i], "dataStr"), "shape"));
                ConvertSymbol(p, units, ox, oy);
            }
            else
            {
                double ox = Get.F(S(head, "x")), oy = Get.F(S(head, "y"));
                List<object[]> units = new List<object[]>();
                units.Add(Get.A(ds, "shape"));
                ConvertSymbol(p, units, ox, oy);
            }

            // EasyEDA often marks passive parts' pins as "input"; in Altium that only
            // produces ERC noise, so keep electrical types for ICs (U?) only.
            if (!p.Prefix.StartsWith("U", StringComparison.OrdinalIgnoreCase))
                foreach (SPin pin in p.Pins) pin.Elec = 0;

            // ---- footprint ----
            Dictionary<string, object> pkg = Get.D(res, "packageDetail");
            if (pkg != null)
            {
                Dictionary<string, object> pds = Get.D(pkg, "dataStr");
                Dictionary<string, object> ph = Get.D(pds, "head");
                Dictionary<string, object> pcp = Get.D(ph, "c_para") ?? new Dictionary<string, object>();
                p.Footprint = Clean(S(pcp, "package"));
                if (p.Footprint == "") p.Footprint = Clean(S(pkg, "title"));
                if (p.Footprint == "") p.Footprint = Clean(S(cp, "package"));
                double fox = Get.F(S(ph, "x")), foy = Get.F(S(ph, "y"));
                ConvertFootprint(p, Get.A(pds, "shape"), fox, foy);
            }
            p.Info["Footprint"] = p.Footprint;
            p.Info["Pins"] = p.Pins.Count.ToString(Inv);
            p.Info["Pads"] = p.Pads.Count.ToString(Inv);
            p.Info["Model3D"] = p.ModelUuid != "" ? "yes" : "no";
            return p;
        }

        public static string FirstOf(Dictionary<string, object> cp, params string[] keys)
        {
            if (cp == null) return "";
            foreach (string k in keys)
            {
                string v = Get.S(cp, k);
                if (!string.IsNullOrEmpty(v) && v.Trim() != "") return v.Trim();
            }
            return "";
        }

        // LCSC number from the parameters of a user part ("Supplier Part", "LCSC Part #", ...)
        public static string SupplierPart(Dictionary<string, object> cp)
        {
            string v = FirstOf(cp, "Supplier Part", "BOM_Supplier Part", "LCSC Part #", "LCSC_PART", "LCSC Part", "LCSC");
            v = v.Trim().ToUpperInvariant();
            return Regex.IsMatch(v, "^C\\d+$") ? v : "";
        }

        public static string Clean(string s)
        {
            if (s == null) return "";
            s = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
            s = Regex.Replace(s, "[\\(\uff08][^\\)\uff09]*[\\u3000-\\u9fff\\uff00-\\uffef][^\\)\uff09]*[\\)\uff09]", "");
            s = Regex.Replace(s, "[\\u3000-\\u9fff]", "");
            s = Regex.Replace(s, "\\s+", " ").Trim();
            return s;
        }

        // ---------------- symbol ----------------

        static PointD SP(double x, double y, double ox, double oy)
        {
            return new PointD((x - ox) * SymScale, -(y - oy) * SymScale);
        }

        static void ConvertSymbol(Part p, List<object[]> units, double ox, double oy)
        {
            // Snap origin so that pin hot ends land on the 100 mil grid.
            double snapX = 0, snapY = 0;
            bool found = false;
            foreach (object[] shapes in units)
            {
                foreach (object o in shapes)
                {
                    string line = o as string;
                    if (line == null || !line.StartsWith("P~")) continue;
                    string[] seg0 = line.Split(new string[] { "^^" }, StringSplitOptions.None)[0].Split('~');
                    if (seg0.Length < 6) continue;
                    double hx = Get.F(seg0[4]), hy = Get.F(seg0[5]);
                    snapX = Rem10(hx - ox);
                    snapY = Rem10(hy - oy);
                    found = true;
                    break;
                }
                if (found) break;
            }
            ox += snapX; oy += snapY;

            for (int u = 0; u < units.Count; u++)
            {
                List<SShape> unitShapes = new List<SShape>();
                foreach (object o in units[u])
                {
                    string line = o as string;
                    if (string.IsNullOrEmpty(line)) continue;
                    string kind = line.Split('~')[0];
                    try
                    {
                        switch (kind)
                        {
                            case "P": AddPin(p, line, u + 1, ox, oy); break;
                            case "R": AddRect(unitShapes, line, u + 1, ox, oy); break;
                            case "E": AddEllipse(unitShapes, line, u + 1, ox, oy, true); break;
                            case "C": AddEllipse(unitShapes, line, u + 1, ox, oy, false); break;
                            case "PL": AddPoly(unitShapes, line, u + 1, ox, oy, false); break;
                            case "PG": AddPoly(unitShapes, line, u + 1, ox, oy, true); break;
                            case "PT": AddPath(unitShapes, line, u + 1, ox, oy); break;
                            case "A": AddArc(unitShapes, line, u + 1, ox, oy); break;
                            case "T": AddText(unitShapes, line, u + 1, ox, oy); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("symbol shape skipped: " + kind + ": " + ex.Message);
                    }
                }
                // The rectangle the pins are attached to (the largest one if several) is the component
                // body: give it the Altium body colour. A rectangle without pins keeps its own fill.
                SShape biggest = null; double bigArea = 0; int bigPins = 0;
                bool unitHasPins = false;
                foreach (SPin pin in p.Pins) if (pin.Unit == u + 1) { unitHasPins = true; break; }
                foreach (SShape s in unitShapes)
                {
                    if (s.Kind != "RECT") continue;
                    double a = Math.Abs((s.Pts[1].X - s.Pts[0].X) * (s.Pts[1].Y - s.Pts[0].Y));
                    int n = PinsOn(p, u + 1, s);
                    if (n > bigPins || (n == bigPins && a > bigArea)) { bigArea = a; biggest = s; bigPins = n; }
                }
                if (biggest != null && (bigPins > 0 || !unitHasPins)) biggest.Fill = 1;
                p.Shapes.AddRange(unitShapes);
            }
        }

        // number of pins whose body end lies on the border of a rectangle
        static int PinsOn(Part p, int unit, SShape r)
        {
            double x0 = Math.Min(r.Pts[0].X, r.Pts[1].X), x1 = Math.Max(r.Pts[0].X, r.Pts[1].X);
            double y0 = Math.Min(r.Pts[0].Y, r.Pts[1].Y), y1 = Math.Max(r.Pts[0].Y, r.Pts[1].Y);
            const double tol = 12;
            int n = 0;
            foreach (SPin pin in p.Pins)
            {
                if (pin.Unit != unit) continue;
                bool inX = pin.X >= x0 - tol && pin.X <= x1 + tol, inY = pin.Y >= y0 - tol && pin.Y <= y1 + tol;
                bool onV = inY && (Math.Abs(pin.X - x0) <= tol || Math.Abs(pin.X - x1) <= tol);
                bool onH = inX && (Math.Abs(pin.Y - y0) <= tol || Math.Abs(pin.Y - y1) <= tol);
                if (onV || onH) n++;
            }
            return n;
        }

        static double Rem10(double v)
        {
            double r = v - Math.Round(v / 10.0) * 10.0;
            return Math.Abs(r) < 0.01 ? 0 : r;
        }

        static int FillOf(string fill)
        {
            if (string.IsNullOrEmpty(fill)) return 0;
            string f = fill.Trim().ToLowerInvariant();
            if (f == "none" || f == "transparent") return 0;
            if (f.StartsWith("#") && f.Length == 7)
            {
                int r = System.Convert.ToInt32(f.Substring(1, 2), 16);
                int g = System.Convert.ToInt32(f.Substring(3, 2), 16);
                int b = System.Convert.ToInt32(f.Substring(5, 2), 16);
                if (r + g + b > 600) return 1;   // light fill -> body colour
            }
            return 2;
        }

        static void AddPin(Part p, string line, int unit, double ox, double oy)
        {
            string[] segs = line.Split(new string[] { "^^" }, StringSplitOptions.None);
            string[] st = segs[0].Split('~');
            SPin pin = new SPin();
            pin.Unit = unit;
            pin.Hidden = st.Length > 1 && st[1] == "hide";
            int type = (int)Get.F(st.Length > 2 ? st[2] : "0");
            pin.Elec = (type >= 0 && type <= 4) ? type : 0;
            string spice = st.Length > 3 ? st[3] : "";
            double hx = Get.F(st.Length > 4 ? st[4] : "0"), hy = Get.F(st.Length > 5 ? st[5] : "0");

            string[] nameSeg = segs.Length > 3 ? segs[3].Split('~') : new string[0];
            string[] numSeg = segs.Length > 4 ? segs[4].Split('~') : new string[0];
            pin.Name = Clean(nameSeg.Length > 4 ? nameSeg[4] : "");
            pin.NameVis = !(nameSeg.Length > 0 && nameSeg[0] == "0");
            pin.Num = Clean(numSeg.Length > 4 ? numSeg[4] : "");
            pin.NumVis = !(numSeg.Length > 0 && numSeg[0] == "0");
            if (pin.Num == "") pin.Num = Clean(spice);
            string[] dotSeg = segs.Length > 5 ? segs[5].Split('~') : new string[0];
            pin.Dot = dotSeg.Length > 0 && dotSeg[0] == "1";
            string[] clkSeg = segs.Length > 6 ? segs[6].Split('~') : new string[0];
            pin.Clock = clkSeg.Length > 0 && clkSeg[0] == "1";

            // pin line: find the end opposite to the hot end
            string path = segs.Length > 2 ? segs[2].Split('~')[0] : "";
            List<PointD> pts = PathPoints(path);
            PointD body = null; double best = -1;
            foreach (PointD q in pts)
            {
                double d = Math.Abs(q.X - hx) + Math.Abs(q.Y - hy);
                if (d > best) { best = d; body = q; }
            }
            if (body == null || best < 0.01) body = new PointD(hx - 10, hy);

            PointD hot = SP(hx, hy, ox, oy);
            PointD b = SP(body.X, body.Y, ox, oy);
            double dx = hot.X - b.X, dy = hot.Y - b.Y;
            if (Math.Abs(dx) >= Math.Abs(dy)) pin.Orient = dx >= 0 ? 0 : 2;
            else pin.Orient = dy >= 0 ? 1 : 3;
            pin.Len = Math.Max(Math.Abs(dx), Math.Abs(dy));
            pin.X = b.X; pin.Y = b.Y;
            // keep body end consistent with an axis-aligned pin
            if (pin.Orient == 0 || pin.Orient == 2) pin.Y = hot.Y; else pin.X = hot.X;
            p.Pins.Add(pin);
        }

        static void AddRect(List<SShape> list, string line, int unit, double ox, double oy)
        {
            string[] parts = line.Split('~');
            // R~x~y~rx~ry~w~h~stroke~sw~style~fill~id~locked  (rx/ry may be empty)
            double x = Get.F(parts[1]), y = Get.F(parts[2]);
            double w = Get.F(parts[5]), h = Get.F(parts[6]);
            string fill = parts.Length > 10 ? parts[10] : "none";
            SShape s = new SShape();
            s.Kind = "RECT"; s.Unit = unit; s.Fill = FillOf(fill);
            PointD a = SP(x, y + h, ox, oy), c = SP(x + w, y, ox, oy);
            s.Pts.Add(a); s.Pts.Add(c);
            list.Add(s);
        }

        static void AddEllipse(List<SShape> list, string line, int unit, double ox, double oy, bool ellipse)
        {
            string[] parts = line.Split('~');
            SShape s = new SShape();
            s.Kind = "ELL"; s.Unit = unit;
            s.Pts.Add(SP(Get.F(parts[1]), Get.F(parts[2]), ox, oy));
            if (ellipse)
            {
                s.R = Get.F(parts[3]) * SymScale; s.R2 = Get.F(parts[4]) * SymScale;
                s.Fill = FillOf(parts.Length > 8 ? parts[8] : "none");
            }
            else
            {
                s.R = s.R2 = Get.F(parts[3]) * SymScale;
                s.Fill = FillOf(parts.Length > 7 ? parts[7] : "none");
            }
            list.Add(s);
        }

        static void AddPoly(List<SShape> list, string line, int unit, double ox, double oy, bool polygon)
        {
            string[] parts = line.Split('~');
            List<double> n = Nums(parts[1]);
            SShape s = new SShape();
            s.Kind = polygon ? "POLY" : "LINE"; s.Unit = unit;
            s.Fill = polygon ? FillOf(parts.Length > 5 ? parts[5] : "none") : 0;
            for (int i = 0; i + 1 < n.Count; i += 2) s.Pts.Add(SP(n[i], n[i + 1], ox, oy));
            if (s.Pts.Count >= 2) list.Add(s);
        }

        static void AddPath(List<SShape> list, string line, int unit, double ox, double oy)
        {
            string[] parts = line.Split('~');
            int fill = FillOf(parts.Length > 5 ? parts[5] : "none");
            foreach (List<PointD> sub in PathPolylines(parts[1], 8))
            {
                if (sub.Count < 2) continue;
                SShape s = new SShape();
                s.Unit = unit;
                bool closed = sub.Count > 2 && Dist(sub[0], sub[sub.Count - 1]) < 0.01;
                s.Kind = (fill != 0 && closed) ? "POLY" : "LINE";
                s.Fill = s.Kind == "POLY" ? fill : 0;
                foreach (PointD q in sub) s.Pts.Add(SP(q.X, q.Y, ox, oy));
                list.Add(s);
            }
        }

        static void AddArc(List<SShape> list, string line, int unit, double ox, double oy)
        {
            string[] parts = line.Split('~');
            string path = parts[1];
            ArcInfo arc = ParseSingleArc(path);
            if (arc != null && Math.Abs(arc.Rx - arc.Ry) < 0.01)
            {
                PointD p1 = SP(arc.X1, arc.Y1, ox, oy), p2 = SP(arc.X2, arc.Y2, ox, oy);
                double cx, cy, a1, a2;
                if (Geo.ArcFromEndpoints(p1, p2, arc.Rx * SymScale, arc.Large, !arc.Sweep, out cx, out cy, out a1, out a2))
                {
                    SShape s = new SShape();
                    s.Kind = "ARC"; s.Unit = unit;
                    s.Pts.Add(new PointD(cx, cy));
                    s.R = arc.Rx * SymScale; s.A1 = a1; s.A2 = a2;
                    list.Add(s);
                    return;
                }
            }
            foreach (List<PointD> sub in PathPolylines(path, 12))
            {
                if (sub.Count < 2) continue;
                SShape s = new SShape();
                s.Kind = "LINE"; s.Unit = unit;
                foreach (PointD q in sub) s.Pts.Add(SP(q.X, q.Y, ox, oy));
                list.Add(s);
            }
        }

        static void AddText(List<SShape> list, string line, int unit, double ox, double oy)
        {
            string[] parts = line.Split('~');
            // T~type~x~y~rot~color~font~size~stroke~baseline~anchor~role~text~display~...
            if (parts.Length < 13 || parts[12] == "") return;
            if (parts.Length > 13 && parts[13] == "none") return;
            SShape s = new SShape();
            s.Kind = "TEXT"; s.Unit = unit;
            s.Pts.Add(SP(Get.F(parts[2]), Get.F(parts[3]), ox, oy));
            s.Rot = Get.F(parts[4]);
            string fs = parts[7].Replace("pt", "");
            s.Size = Get.F(fs, 7);
            s.Text = Clean(parts[12]);
            if (s.Text != "") list.Add(s);
        }

        // ---------------- footprint ----------------

        static PointD FP(double x, double y, double ox, double oy)
        {
            return new PointD((x - ox) * FpScale, -(y - oy) * FpScale);
        }

        static string MapLayer(int id)
        {
            switch (id)
            {
                case 1: return "T";
                case 2: return "B";
                case 3: return "TO";
                case 4: return "BO";
                case 13: return "ASM";
                case 14: return "ASM";
                case 99: return "CRT";
                default: return null;
            }
        }

        static void ConvertFootprint(Part p, object[] shapes, double ox, double oy)
        {
            foreach (object o in shapes)
            {
                string line = o as string;
                if (string.IsNullOrEmpty(line)) continue;
                string[] f = line.Split('~');
                string kind = f[0];
                try
                {
                    switch (kind)
                    {
                        case "PAD": AddPad(p, f, ox, oy); break;
                        case "HOLE":
                            {
                                FPad h = new FPad();
                                PointD c = FP(Get.F(f[1]), Get.F(f[2]), ox, oy);
                                h.X = c.X; h.Y = c.Y;
                                h.Shape = 'C'; h.Layer = "M"; h.Plated = false;
                                h.Hole = Get.F(f[3]) * 2 * FpScale; h.SX = h.SY = h.Hole;
                                p.Pads.Add(h);
                                break;
                            }
                        case "VIA":
                            {
                                FPad v = new FPad();
                                PointD c = FP(Get.F(f[1]), Get.F(f[2]), ox, oy);
                                v.X = c.X; v.Y = c.Y; v.Shape = 'C'; v.Layer = "M";
                                v.SX = v.SY = Get.F(f[3]) * FpScale;
                                v.Hole = Get.F(f[5]) * 2 * FpScale;
                                p.Pads.Add(v);
                                break;
                            }
                        case "TRACK":
                            {
                                string layer = MapLayer((int)Get.F(f[2]));
                                if (layer == null) break;
                                double w = Get.F(f[1]) * FpScale;
                                List<double> n = Nums(f[4]);
                                for (int i = 0; i + 3 < n.Count; i += 2)
                                {
                                    FGfx g = new FGfx();
                                    g.Kind = "TRK"; g.Layer = layer; g.W = w;
                                    g.Pts.Add(FP(n[i], n[i + 1], ox, oy));
                                    g.Pts.Add(FP(n[i + 2], n[i + 3], ox, oy));
                                    p.Gfx.Add(g);
                                }
                                break;
                            }
                        case "CIRCLE":
                            {
                                string layer = MapLayer((int)Get.F(f[5]));
                                if (layer == null) break;
                                FGfx g = new FGfx();
                                g.Kind = "ARC"; g.Layer = layer;
                                PointD c = FP(Get.F(f[1]), Get.F(f[2]), ox, oy);
                                g.CX = c.X; g.CY = c.Y; g.R = Get.F(f[3]) * FpScale;
                                g.W = Get.F(f[4]) * FpScale; g.A1 = 0; g.A2 = 360;
                                p.Gfx.Add(g);
                                break;
                            }
                        case "ARC":
                            {
                                string layer = MapLayer((int)Get.F(f[2]));
                                if (layer == null) break;
                                double w = Get.F(f[1]) * FpScale;
                                ArcInfo arc = ParseSingleArc(f[4]);
                                bool done = false;
                                if (arc != null && Math.Abs(arc.Rx - arc.Ry) < 0.01)
                                {
                                    PointD p1 = FP(arc.X1, arc.Y1, ox, oy), p2 = FP(arc.X2, arc.Y2, ox, oy);
                                    double cx, cy, a1, a2;
                                    if (Geo.ArcFromEndpoints(p1, p2, arc.Rx * FpScale, arc.Large, !arc.Sweep, out cx, out cy, out a1, out a2))
                                    {
                                        FGfx g = new FGfx();
                                        g.Kind = "ARC"; g.Layer = layer; g.W = w;
                                        g.CX = cx; g.CY = cy; g.R = arc.Rx * FpScale; g.A1 = a1; g.A2 = a2;
                                        p.Gfx.Add(g);
                                        done = true;
                                    }
                                }
                                if (!done)
                                {
                                    foreach (List<PointD> sub in PathPolylines(f[4], 16))
                                        for (int i = 0; i + 1 < sub.Count; i++)
                                        {
                                            FGfx g = new FGfx();
                                            g.Kind = "TRK"; g.Layer = layer; g.W = w;
                                            g.Pts.Add(FP(sub[i].X, sub[i].Y, ox, oy));
                                            g.Pts.Add(FP(sub[i + 1].X, sub[i + 1].Y, ox, oy));
                                            p.Gfx.Add(g);
                                        }
                                }
                                break;
                            }
                        case "RECT":
                            {
                                // RECT~x~y~w~h~layer~id~locked~strokeWidth~fill
                                string layer = MapLayer((int)Get.F(f[5]));
                                if (layer == null) break;
                                double x = Get.F(f[1]), y = Get.F(f[2]), w = Get.F(f[3]), h = Get.F(f[4]);
                                double sw = f.Length > 8 ? Get.F(f[8]) * FpScale : 0;
                                if (sw <= 0) sw = 6;
                                PointD[] c = { FP(x, y, ox, oy), FP(x + w, y, ox, oy), FP(x + w, y + h, ox, oy), FP(x, y + h, ox, oy) };
                                for (int i = 0; i < 4; i++)
                                {
                                    FGfx g = new FGfx();
                                    g.Kind = "TRK"; g.Layer = layer; g.W = sw;
                                    g.Pts.Add(c[i]); g.Pts.Add(c[(i + 1) % 4]);
                                    p.Gfx.Add(g);
                                }
                                break;
                            }
                        case "SOLIDREGION":
                            {
                                int lid = (int)Get.F(f[1]);
                                string layer = MapLayer(lid);
                                if (layer == null) break;
                                string rtype = f.Length > 4 ? f[4] : "solid";
                                if (rtype != "solid") break;
                                foreach (List<PointD> sub in PathPolylines(f[3], 12))
                                {
                                    if (sub.Count < 3) continue;
                                    List<PointD> pts = new List<PointD>();
                                    foreach (PointD q in sub) pts.Add(FP(q.X, q.Y, ox, oy));
                                    if (layer == "CRT" || layer == "ASM")
                                    {
                                        // outlines only on mechanical layers
                                        for (int i = 0; i < pts.Count; i++)
                                        {
                                            PointD a = pts[i], b = pts[(i + 1) % pts.Count];
                                            if (Dist(a, b) < 0.01) continue;
                                            FGfx g = new FGfx();
                                            g.Kind = "TRK"; g.Layer = layer; g.W = 2;
                                            g.Pts.Add(a); g.Pts.Add(b);
                                            p.Gfx.Add(g);
                                        }
                                    }
                                    else
                                    {
                                        FGfx g = new FGfx();
                                        g.Kind = "REG"; g.Layer = layer;
                                        g.Pts = pts;
                                        p.Gfx.Add(g);
                                    }
                                }
                                break;
                            }
                        case "SVGNODE":
                            Add3D(p, line, ox, oy);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("footprint shape skipped: " + kind + ": " + ex.Message);
                }
            }
        }

        static void AddPad(Part p, string[] f, double ox, double oy)
        {
            // PAD~shape~cx~cy~w~h~layer~net~number~holeR~points~rot~id~holeLen~slotPts~plated~...
            FPad pad = new FPad();
            string shape = f[1];
            PointD c = FP(Get.F(f[2]), Get.F(f[3]), ox, oy);
            pad.X = c.X; pad.Y = c.Y;
            double w = Get.F(f[4]) * FpScale, h = Get.F(f[5]) * FpScale;
            int layer = (int)Get.F(f[6]);
            pad.Name = Clean(f.Length > 8 ? f[8] : "");
            double holeR = Get.F(f.Length > 9 ? f[9] : "0") * FpScale;
            string points = f.Length > 10 ? f[10] : "";
            double rot = Get.F(f.Length > 11 ? f[11] : "0");
            double holeLen = Get.F(f.Length > 13 ? f[13] : "0") * FpScale;
            string plated = f.Length > 15 ? f[15] : "Y";
            pad.Plated = plated != "N";

            pad.Layer = layer == 2 ? "B" : (layer == 11 ? "M" : "T");
            if (holeR > 0) { pad.Layer = "M"; pad.Hole = holeR * 2; }

            List<PointD> poly = new List<PointD>();
            List<double> n = Nums(points);
            for (int i = 0; i + 1 < n.Count; i += 2) poly.Add(FP(n[i], n[i + 1], ox, oy));

            if (shape == "RECT" && poly.Count >= 4)
            {
                // derive true size and orientation from the corner points
                PointD p0 = poly[0], p1 = poly[1], p2 = poly[2];
                double e1 = Dist(p0, p1), e2 = Dist(p1, p2);
                double ang = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180 / Math.PI;
                pad.Shape = 'R';
                SetSizeRot(pad, e1, e2, ang);
            }
            else if (shape == "RECT")
            {
                pad.Shape = 'R';
                SetSizeRot(pad, w, h, -rot);
            }
            else if (shape == "ELLIPSE")
            {
                pad.Shape = 'C';
                SetSizeRot(pad, w, h, -rot);
            }
            else if (shape == "OVAL")
            {
                pad.Shape = 'O';
                double ang;
                if (poly.Count >= 2 && Dist(poly[0], poly[1]) > 0.01)
                    ang = Math.Atan2(poly[1].Y - poly[0].Y, poly[1].X - poly[0].X) * 180 / Math.PI;
                else
                    ang = (w >= h ? 0 : 90) - rot;
                SetSizeRot(pad, Math.Max(w, h), Math.Min(w, h), ang);
            }
            else if (shape == "POLYGON")
            {
                List<PointD> clean = Geo.CleanPolygon(poly);
                double rx, ry, rw, rh;
                if (clean.Count == 4 && Geo.IsAxisRect(clean, out rx, out ry, out rw, out rh))
                {
                    pad.Shape = 'R';
                    pad.X = rx; pad.Y = ry; pad.SX = rw; pad.SY = rh; pad.Rot = 0;
                }
                else if (clean.Count >= 3)
                {
                    pad.Shape = 'P';
                    pad.Poly = clean;
                    double minx, miny, maxx, maxy;
                    Geo.BBox(clean, out minx, out miny, out maxx, out maxy);
                    PointD cen = Geo.Centroid(clean);
                    pad.X = cen.X; pad.Y = cen.Y;
                    double m = Math.Min(maxx - minx, maxy - miny) * 0.5;
                    pad.SX = pad.SY = Math.Max(m, 2);
                }
                else
                {
                    pad.Shape = 'R'; pad.SX = w; pad.SY = h;
                }
            }
            else
            {
                pad.Shape = 'R'; pad.SX = w; pad.SY = h;
            }

            if (holeLen > 0 && pad.Hole > 0 && holeLen > pad.Hole)
            {
                pad.SlotLen = holeLen;
                // slot runs along the long axis of the pad
                pad.SlotRot = pad.SX >= pad.SY ? pad.Rot : pad.Rot + 90;
            }
            if (pad.SX < pad.Hole) pad.SX = pad.Hole;
            if (pad.SY < pad.Hole) pad.SY = pad.Hole;
            p.Pads.Add(pad);
        }

        static void SetSizeRot(FPad pad, double sx, double sy, double ang)
        {
            ang = Geo.Norm(ang);
            double r = Math.Round(ang / 90.0);
            if (Math.Abs(ang - r * 90) < 0.05)
            {
                // axis aligned: avoid pad rotation, swap sizes instead
                int q = ((int)r) % 4;
                if (q == 1 || q == 3) { double t = sx; sx = sy; sy = t; }
                ang = 0;
            }
            pad.SX = sx; pad.SY = sy; pad.Rot = ang;
        }

        static void Add3D(Part p, string line, double ox, double oy)
        {
            int idx = line.IndexOf('~');
            string json = line.Substring(idx + 1);
            // JSON may be followed by more ~ fields; find the matching closing brace
            int depth = 0, end = -1;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end > 0) json = json.Substring(0, end + 1);
            object node = JsonUtil.Parse(json);
            Dictionary<string, object> attrs = Get.D(node, "attrs");
            if (attrs == null) return;
            p.ModelUuid = Get.S(attrs, "uuid");
            p.ModelTitle = Clean(Get.S(attrs, "title"));
            string[] rot = Get.S(attrs, "c_rotation").Split(',');
            p.ModelRX = rot.Length > 0 ? Get.F(rot[0]) : 0;
            p.ModelRY = rot.Length > 1 ? Get.F(rot[1]) : 0;
            p.ModelRZ = rot.Length > 2 ? Get.F(rot[2]) : 0;
            p.ModelTZ = Get.F(Get.S(attrs, "z")) * FpScale;

            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (object ch in Get.A(node, "childNodes"))
            {
                List<double> n = Nums(Get.S(Get.D(ch, "attrs"), "points"));
                for (int i = 0; i + 1 < n.Count; i += 2)
                {
                    minx = Math.Min(minx, n[i]); maxx = Math.Max(maxx, n[i]);
                    miny = Math.Min(miny, n[i + 1]); maxy = Math.Max(maxy, n[i + 1]);
                }
            }
            if (minx < maxx)
            {
                PointD c = FP((minx + maxx) / 2, (miny + maxy) / 2, ox, oy);
                p.ModelTX = c.X; p.ModelTY = c.Y; p.HasOutline = true;
            }
            else
            {
                string[] co = Get.S(attrs, "c_origin").Split(',');
                if (co.Length == 2)
                {
                    PointD c = FP(Get.F(co[0]), Get.F(co[1]), ox, oy);
                    p.ModelTX = c.X; p.ModelTY = c.Y;
                }
            }
        }

        // ---------------- path helpers ----------------

        public class ArcInfo
        {
            public double X1, Y1, X2, Y2, Rx, Ry, XRot;
            public bool Large, Sweep;
        }

        static readonly Regex TokRe = new Regex("[MmLlHhVvAaZzCcQqSsTt]|-?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?");

        public static List<double> Nums(string s)
        {
            List<double> r = new List<double>();
            if (s == null) return r;
            foreach (Match m in Regex.Matches(s, "-?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?"))
                r.Add(double.Parse(m.Value, NumberStyles.Float, Inv));
            return r;
        }

        static double Dist(PointD a, PointD b)
        {
            return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        // all vertices (used for pin lines)
        static List<PointD> PathPoints(string path)
        {
            List<PointD> all = new List<PointD>();
            foreach (List<PointD> sub in PathPolylines(path, 1)) all.AddRange(sub);
            return all;
        }

        static ArcInfo ParseSingleArc(string path)
        {
            List<string> t = Tokens(path);
            // expect: M x y A rx ry rot large sweep x y
            int i = 0;
            if (t.Count < 11) return null;
            if (t[i] != "M" && t[i] != "m") return null;
            ArcInfo a = new ArcInfo();
            a.X1 = Num(t[1]); a.Y1 = Num(t[2]);
            if (t[3] != "A" && t[3] != "a") return null;
            a.Rx = Math.Abs(Num(t[4])); a.Ry = Math.Abs(Num(t[5])); a.XRot = Num(t[6]);
            a.Large = Num(t[7]) != 0; a.Sweep = Num(t[8]) != 0;
            a.X2 = Num(t[9]); a.Y2 = Num(t[10]);
            if (t[3] == "a") { a.X2 += a.X1; a.Y2 += a.Y1; }
            if (t.Count > 11) return null;   // more than one segment
            return a;
        }

        static double Num(string s) { return double.Parse(s, NumberStyles.Float, Inv); }

        static List<string> Tokens(string path)
        {
            List<string> t = new List<string>();
            if (path == null) return t;
            foreach (Match m in TokRe.Matches(path)) t.Add(m.Value);
            return t;
        }

        // Converts an SVG path into polylines (arcs and curves are flattened).
        public static List<List<PointD>> PathPolylines(string path, int arcSegs)
        {
            List<List<PointD>> result = new List<List<PointD>>();
            List<string> t = Tokens(path);
            List<PointD> cur = null;
            double x = 0, y = 0, sx = 0, sy = 0;
            string cmd = "";
            int i = 0;
            while (i < t.Count)
            {
                string tok = t[i];
                if (char.IsLetter(tok[0])) { cmd = tok; i++; if (cmd == "Z" || cmd == "z") { if (cur != null) { cur.Add(new PointD(sx, sy)); } x = sx; y = sy; cur = null; continue; } }
                if (cmd == "") { i++; continue; }
                bool rel = char.IsLower(cmd[0]);
                switch (char.ToUpperInvariant(cmd[0]))
                {
                    case 'M':
                        {
                            if (i + 1 >= t.Count) { i = t.Count; break; }
                            double nx = Num(t[i]), ny = Num(t[i + 1]); i += 2;
                            if (rel) { nx += x; ny += y; }
                            x = nx; y = ny; sx = x; sy = y;
                            cur = new List<PointD>(); cur.Add(new PointD(x, y)); result.Add(cur);
                            cmd = rel ? "l" : "L";
                            break;
                        }
                    case 'L':
                        {
                            if (i + 1 >= t.Count) { i = t.Count; break; }
                            double nx = Num(t[i]), ny = Num(t[i + 1]); i += 2;
                            if (rel) { nx += x; ny += y; }
                            x = nx; y = ny;
                            if (cur == null) { cur = new List<PointD>(); result.Add(cur); }
                            cur.Add(new PointD(x, y));
                            break;
                        }
                    case 'H':
                        {
                            double nx = Num(t[i]); i += 1;
                            x = rel ? x + nx : nx;
                            if (cur == null) { cur = new List<PointD>(); result.Add(cur); }
                            cur.Add(new PointD(x, y));
                            break;
                        }
                    case 'V':
                        {
                            double ny = Num(t[i]); i += 1;
                            y = rel ? y + ny : ny;
                            if (cur == null) { cur = new List<PointD>(); result.Add(cur); }
                            cur.Add(new PointD(x, y));
                            break;
                        }
                    case 'A':
                        {
                            if (i + 6 >= t.Count) { i = t.Count; break; }
                            double rx = Math.Abs(Num(t[i])), ry = Math.Abs(Num(t[i + 1])), xr = Num(t[i + 2]);
                            bool large = Num(t[i + 3]) != 0, sweep = Num(t[i + 4]) != 0;
                            double nx = Num(t[i + 5]), ny = Num(t[i + 6]); i += 7;
                            if (rel) { nx += x; ny += y; }
                            if (cur == null) { cur = new List<PointD>(); cur.Add(new PointD(x, y)); result.Add(cur); }
                            Geo.FlattenArc(cur, x, y, rx, ry, xr, large, sweep, nx, ny, arcSegs);
                            x = nx; y = ny;
                            break;
                        }
                    case 'C':
                        {
                            if (i + 5 >= t.Count) { i = t.Count; break; }
                            double x1 = Num(t[i]), y1 = Num(t[i + 1]), x2 = Num(t[i + 2]), y2 = Num(t[i + 3]), nx = Num(t[i + 4]), ny = Num(t[i + 5]); i += 6;
                            if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; nx += x; ny += y; }
                            if (cur == null) { cur = new List<PointD>(); cur.Add(new PointD(x, y)); result.Add(cur); }
                            for (int k = 1; k <= 8; k++)
                            {
                                double tt = k / 8.0, mt = 1 - tt;
                                cur.Add(new PointD(mt * mt * mt * x + 3 * mt * mt * tt * x1 + 3 * mt * tt * tt * x2 + tt * tt * tt * nx,
                                                   mt * mt * mt * y + 3 * mt * mt * tt * y1 + 3 * mt * tt * tt * y2 + tt * tt * tt * ny));
                            }
                            x = nx; y = ny;
                            break;
                        }
                    case 'Q':
                        {
                            if (i + 3 >= t.Count) { i = t.Count; break; }
                            double x1 = Num(t[i]), y1 = Num(t[i + 1]), nx = Num(t[i + 2]), ny = Num(t[i + 3]); i += 4;
                            if (rel) { x1 += x; y1 += y; nx += x; ny += y; }
                            if (cur == null) { cur = new List<PointD>(); cur.Add(new PointD(x, y)); result.Add(cur); }
                            for (int k = 1; k <= 8; k++)
                            {
                                double tt = k / 8.0, mt = 1 - tt;
                                cur.Add(new PointD(mt * mt * x + 2 * mt * tt * x1 + tt * tt * nx, mt * mt * y + 2 * mt * tt * y1 + tt * tt * ny));
                            }
                            x = nx; y = ny;
                            break;
                        }
                    default:
                        i++;
                        break;
                }
            }
            return result;
        }
    }

    // ======================================================================
    // geometry
    // ======================================================================

    static class Geo
    {
        public static double Norm(double a)
        {
            a = a % 360.0;
            if (a < 0) a += 360.0;
            if (a >= 359.9995) a = 0;
            return a;
        }

        // Circular arc through p1 -> p2 (y-up coordinates). ccw = direction of travel.
        // Returns Altium-style start/end angles (drawn counter-clockwise from a1 to a2).
        public static bool ArcFromEndpoints(PointD p1, PointD p2, double r, bool large, bool ccw,
                                            out double cx, out double cy, out double a1, out double a2)
        {
            cx = cy = a1 = a2 = 0;
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || r <= 0) return false;
            if (d > 2 * r) r = d / 2;
            double mx = (p1.X + p2.X) / 2, my = (p1.Y + p2.Y) / 2;
            double h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
            // unit normal (left of p1->p2)
            double nx = -dy / d, ny = dx / d;
            // centre on the left gives a CCW minor arc
            bool left = (ccw != large);
            cx = mx + (left ? nx : -nx) * h;
            cy = my + (left ? ny : -ny) * h;
            double t1 = Math.Atan2(p1.Y - cy, p1.X - cx) * 180 / Math.PI;
            double t2 = Math.Atan2(p2.Y - cy, p2.X - cx) * 180 / Math.PI;
            if (ccw) { a1 = Norm(t1); a2 = Norm(t2); }
            else { a1 = Norm(t2); a2 = Norm(t1); }
            return true;
        }

        // SVG endpoint arc flattening (any coordinate orientation)
        public static void FlattenArc(List<PointD> outPts, double x1, double y1, double rx, double ry, double phiDeg,
                                      bool large, bool sweep, double x2, double y2, int segs)
        {
            if (rx < 1e-9 || ry < 1e-9) { outPts.Add(new PointD(x2, y2)); return; }
            double phi = phiDeg * Math.PI / 180;
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx2 = (x1 - x2) / 2, dy2 = (y1 - y2) / 2;
            double x1p = cos * dx2 + sin * dy2, y1p = -sin * dx2 + cos * dy2;
            double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (large == sweep) co = -co;
            double cxp = co * rx * y1p / ry, cyp = -co * ry * x1p / rx;
            double cx = cos * cxp - sin * cyp + (x1 + x2) / 2;
            double cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
            double th1 = Ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double dth = Ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            else if (sweep && dth < 0) dth += 2 * Math.PI;
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(dth) / (2 * Math.PI) * segs * 4));
            for (int k = 1; k <= n; k++)
            {
                double th = th1 + dth * k / n;
                double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                outPts.Add(new PointD(cos * ex - sin * ey + cx, sin * ex + cos * ey + cy));
            }
        }

        static double Ang(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }

        public static List<PointD> CleanPolygon(List<PointD> pts)
        {
            List<PointD> r = new List<PointD>();
            foreach (PointD p in pts)
            {
                if (r.Count > 0 && Math.Abs(r[r.Count - 1].X - p.X) < 0.05 && Math.Abs(r[r.Count - 1].Y - p.Y) < 0.05) continue;
                r.Add(p);
            }
            if (r.Count > 1 && Math.Abs(r[0].X - r[r.Count - 1].X) < 0.05 && Math.Abs(r[0].Y - r[r.Count - 1].Y) < 0.05)
                r.RemoveAt(r.Count - 1);
            // remove collinear points
            bool changed = true;
            while (changed && r.Count > 3)
            {
                changed = false;
                for (int i = 0; i < r.Count; i++)
                {
                    PointD a = r[(i + r.Count - 1) % r.Count], b = r[i], c = r[(i + 1) % r.Count];
                    double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                    double la = Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y), lc = Math.Abs(c.X - b.X) + Math.Abs(c.Y - b.Y);
                    if (Math.Abs(cross) < 0.01 * Math.Max(1, la + lc))
                    {
                        r.RemoveAt(i); changed = true; break;
                    }
                }
            }
            return r;
        }

        public static bool IsAxisRect(List<PointD> p, out double cx, out double cy, out double w, out double h)
        {
            double minx, miny, maxx, maxy;
            BBox(p, out minx, out miny, out maxx, out maxy);
            cx = (minx + maxx) / 2; cy = (miny + maxy) / 2; w = maxx - minx; h = maxy - miny;
            foreach (PointD q in p)
            {
                bool onX = Math.Abs(q.X - minx) < 0.1 || Math.Abs(q.X - maxx) < 0.1;
                bool onY = Math.Abs(q.Y - miny) < 0.1 || Math.Abs(q.Y - maxy) < 0.1;
                if (!onX || !onY) return false;
            }
            return true;
        }

        public static void BBox(List<PointD> p, out double minx, out double miny, out double maxx, out double maxy)
        {
            minx = miny = double.MaxValue; maxx = maxy = double.MinValue;
            foreach (PointD q in p)
            {
                minx = Math.Min(minx, q.X); maxx = Math.Max(maxx, q.X);
                miny = Math.Min(miny, q.Y); maxy = Math.Max(maxy, q.Y);
            }
        }

        public static PointD Centroid(List<PointD> p)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < p.Count; i++)
            {
                PointD u = p[i], v = p[(i + 1) % p.Count];
                double cr = u.X * v.Y - v.X * u.Y;
                a += cr; cx += (u.X + v.X) * cr; cy += (u.Y + v.Y) * cr;
            }
            if (Math.Abs(a) < 1e-9)
            {
                double minx, miny, maxx, maxy;
                BBox(p, out minx, out miny, out maxx, out maxy);
                return new PointD((minx + maxx) / 2, (miny + maxy) / 2);
            }
            a *= 0.5;
            return new PointD(cx / (6 * a), cy / (6 * a));
        }
    }

    // ======================================================================
    // shared JSON getters (for classes other than Program)
    // ======================================================================

    static class JsonUtil
    {
        public static object Parse(string s)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.DeserializeObject(s);
        }
    }

    static class Get
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Dictionary<string, object> D(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || !d.ContainsKey(key)) return null;
            return d[key] as Dictionary<string, object>;
        }

        public static object[] A(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || !d.ContainsKey(key) || d[key] == null) return new object[0];
            object v = d[key];
            object[] arr = v as object[];
            if (arr != null) return arr;
            ArrayList al = v as ArrayList;
            if (al != null) return al.ToArray();
            return new object[0];
        }

        public static string S(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || !d.ContainsKey(key) || d[key] == null) return "";
            object v = d[key];
            if (v is string) return (string)v;
            if (v is decimal) return ((decimal)v).ToString(Inv);
            if (v is double) return ((double)v).ToString(Inv);
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, Inv);
        }

        public static double F(string s, double def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            double v;
            if (double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v)) return v;
            return def;
        }

        public static double F(string s) { return F(s, 0); }
    }

}
