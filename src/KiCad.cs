// KiCad -> EasyLib part model: symbols (.kicad_sym of KiCad 6..10 and legacy .lib/.dcm),
// footprints (.kicad_mod, KiCad 5 "module" and 6+ "footprint") and the placement of STEP models.
// Used for the official KiCad library and for ZIPs downloaded from SnapEDA, Ultra Librarian,
// Component Search Engine (SamacSys) and similar sites in KiCad format.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    // ------------------------------------------------------------------ S-expressions

    class SNode
    {
        public string Atom;             // null for a list
        public List<SNode> Items;

        public bool IsList { get { return Items != null; } }

        public string Head
        {
            get { return Items != null && Items.Count > 0 && Items[0].Atom != null ? Items[0].Atom : ""; }
        }

        public SNode Find(string head)
        {
            if (Items == null) return null;
            foreach (SNode n in Items) if (n.IsList && n.Head == head) return n;
            return null;
        }

        public List<SNode> All(string head)
        {
            List<SNode> r = new List<SNode>();
            if (Items == null) return r;
            foreach (SNode n in Items) if (n.IsList && n.Head == head) r.Add(n);
            return r;
        }

        public string S(int i)
        {
            return Items != null && i < Items.Count && Items[i].Atom != null ? Items[i].Atom : "";
        }

        public double N(int i)
        {
            double v;
            return double.TryParse(S(i), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        public bool HasAtom(string a)
        {
            if (Items == null) return false;
            for (int i = 1; i < Items.Count; i++) if (Items[i].Atom == a) return true;
            return false;
        }

        // "hide" as a bare atom (KiCad 6..8) or "(hide yes)" (KiCad 9+)
        public bool Hidden
        {
            get
            {
                if (HasAtom("hide")) return true;
                SNode h = Find("hide");
                return h != null && (h.S(1) == "" || h.S(1) == "yes");
            }
        }
    }

    static class SExpr
    {
        public static SNode Parse(string text)
        {
            Stack<SNode> stack = new Stack<SNode>();
            SNode root = new SNode { Items = new List<SNode>() };
            stack.Push(root);
            int i = 0, n = text.Length;
            StringBuilder sb = new StringBuilder();
            while (i < n)
            {
                char c = text[i];
                if (c == '(')
                {
                    SNode l = new SNode { Items = new List<SNode>() };
                    stack.Peek().Items.Add(l);
                    stack.Push(l);
                    i++;
                }
                else if (c == ')')
                {
                    if (stack.Count > 1) stack.Pop();
                    i++;
                }
                else if (c == '"')
                {
                    sb.Length = 0;
                    i++;
                    while (i < n && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < n)
                        {
                            char e = text[i + 1];
                            sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                            i += 2;
                        }
                        else sb.Append(text[i++]);
                    }
                    i++;
                    stack.Peek().Items.Add(new SNode { Atom = sb.ToString() });
                }
                else if (char.IsWhiteSpace(c)) i++;
                else
                {
                    int s = i;
                    while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')') i++;
                    stack.Peek().Items.Add(new SNode { Atom = text.Substring(s, i - s) });
                }
            }
            return root.Items.Count == 1 ? root.Items[0] : root;
        }
    }

    // 3D model reference of a KiCad footprint
    class KiModel
    {
        public string Path = "";
        public double OffX, OffY, OffZ;           // mm (3D frame, Y up)
        public double RotX, RotY, RotZ;           // degrees, KiCad convention
        public double ScaleX = 1, ScaleY = 1, ScaleZ = 1;
    }

    static class KiCad
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const double MilPerMm = 1000.0 / 25.4;

        static double Mil(double mm) { return mm * MilPerMm; }

        // ================================================================ symbols (KiCad 6+)

        // all top-level symbols of a .kicad_sym text
        public static List<SNode> SymbolsOf(string text)
        {
            SNode root = SExpr.Parse(text);
            List<SNode> r = new List<SNode>();
            if (root.Head == "kicad_symbol_lib") r.AddRange(root.All("symbol"));
            else if (root.Head == "symbol") r.Add(root);
            return r;
        }

        public static Dictionary<string, string> Props(SNode sym)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SNode p in sym.All("property")) d[p.S(1)] = p.S(2);
            return d;
        }

        static string Get(Dictionary<string, string> d, params string[] keys)
        {
            foreach (string k in keys)
            {
                string v;
                if (d.TryGetValue(k, out v) && !string.IsNullOrEmpty(v) && v != "~") return v.Trim();
            }
            return "";
        }

        // resolves "extends": parent lookup by name (same library); returns the chain child..root
        static List<SNode> Chain(SNode sym, Func<string, SNode> lookup)
        {
            List<SNode> chain = new List<SNode>();
            SNode cur = sym;
            while (cur != null && chain.Count < 8)
            {
                chain.Add(cur);
                SNode ext = cur.Find("extends");
                cur = ext != null && lookup != null ? lookup(ext.S(1)) : null;
            }
            return chain;
        }

        public static Dictionary<string, string> MergedProps(SNode sym, Func<string, SNode> lookup)
        {
            List<SNode> chain = Chain(sym, lookup);
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = chain.Count - 1; i >= 0; i--)
                foreach (KeyValuePair<string, string> kv in Props(chain[i]))
                    if (kv.Value != "" || !d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
            return d;
        }

        public static string FootprintName(string fpProp)
        {
            if (string.IsNullOrEmpty(fpProp)) return "";
            int c = fpProp.IndexOf(':');
            return c >= 0 ? fpProp.Substring(c + 1) : fpProp;
        }

        public static string FootprintLib(string fpProp)
        {
            int c = (fpProp ?? "").IndexOf(':');
            return c > 0 ? fpProp.Substring(0, c) : "";
        }

        // "~{RESET}" (KiCad overbar) -> "R\E\S\E\T\" (Altium overline); "~" alone means empty
        public static string PinText(string s)
        {
            if (s == null || s == "~") return "";
            if (s.IndexOf("~{", StringComparison.Ordinal) < 0) return s;
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '~' && i + 1 < s.Length && s[i + 1] == '{')
                {
                    int e = s.IndexOf('}', i + 2);
                    if (e < 0) e = s.Length;
                    for (int k = i + 2; k < e; k++) { sb.Append(s[k]); sb.Append('\\'); }
                    i = e + 1;
                }
                else sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        static int ElecOf(string t)
        {
            switch (t)
            {
                case "input": return 1;
                case "output": return 2;
                case "bidirectional": return 3;
                case "power_in": case "power_out": return 4;
                case "open_collector": return 5;
                case "tri_state": return 6;
                case "open_emitter": return 7;
                default: return 0;      // passive, free, unspecified, no_connect
            }
        }

        static double[] XY(SNode n)
        {
            return n == null ? new double[] { 0, 0 } : new double[] { n.N(1), n.N(2) };
        }

        static int FillOf(SNode el)
        {
            SNode f = el.Find("fill");
            if (f == null) return 0;
            SNode t = f.Find("type");
            string v = t != null ? t.S(1) : f.S(1);
            if (v == "outline") return 2;
            if (v == "background" || v == "color") return 1;
            return 0;
        }

        static void ArcFrom3(double sx, double sy, double mx, double my, double ex, double ey,
                             out double cx, out double cy, out double r, out double a1, out double a2)
        {
            double d = 2 * (sx * (my - ey) + mx * (ey - sy) + ex * (sy - my));
            if (Math.Abs(d) < 1e-12)
            {
                cx = (sx + ex) / 2; cy = (sy + ey) / 2; r = Math.Sqrt((sx - ex) * (sx - ex) + (sy - ey) * (sy - ey)) / 2;
                a1 = 0; a2 = 180;
                return;
            }
            double s2 = sx * sx + sy * sy, m2 = mx * mx + my * my, e2 = ex * ex + ey * ey;
            cx = (s2 * (my - ey) + m2 * (ey - sy) + e2 * (sy - my)) / d;
            cy = (s2 * (ex - mx) + m2 * (sx - ex) + e2 * (mx - sx)) / d;
            r = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            double as_ = Ang(sx - cx, sy - cy), am = Ang(mx - cx, my - cy), ae = Ang(ex - cx, ey - cy);
            // counter-clockwise from start to end if the mid point lies on that path
            if (Norm(am - as_) <= Norm(ae - as_)) { a1 = as_; a2 = ae; }
            else { a1 = ae; a2 = as_; }
        }

        static double Ang(double x, double y) { return Norm(Math.Atan2(y, x) * 180 / Math.PI); }

        static double Norm(double a) { a %= 360; if (a < 0) a += 360; return a; }

        // converts one KiCad 6+ symbol (with its "extends" chain) into p
        public static void ConvertSymbol(Part p, SNode sym, Func<string, SNode> lookup)
        {
            List<SNode> chain = Chain(sym, lookup);
            SNode body = chain[chain.Count - 1];
            Dictionary<string, string> props = MergedProps(sym, lookup);

            bool namesHidden = false, numbersHidden = false;
            foreach (SNode s in chain)
            {
                SNode pn = s.Find("pin_names");
                if (pn != null) { namesHidden = pn.Hidden; break; }
            }
            foreach (SNode s in chain)
            {
                SNode pn = s.Find("pin_numbers");
                if (pn != null) { numbersHidden = pn.Hidden; break; }
            }

            string bodyName = body.S(1);
            List<KeyValuePair<int, SNode>> units = new List<KeyValuePair<int, SNode>>();
            int maxUnit = 1;
            foreach (SNode sub in body.All("symbol"))
            {
                Match m = Regex.Match(sub.S(1), "_(\\d+)_(\\d+)$");
                int u = 0, st = 1;
                if (m.Success) { u = int.Parse(m.Groups[1].Value, Inv); st = int.Parse(m.Groups[2].Value, Inv); }
                if (st > 1) continue;                  // alternate (De Morgan) body style
                units.Add(new KeyValuePair<int, SNode>(u, sub));
                maxUnit = Math.Max(maxUnit, u);
            }
            p.PartCount = maxUnit;
            foreach (KeyValuePair<int, SNode> kv in units)
            {
                List<int> targets = new List<int>();
                if (kv.Key == 0) for (int u = 1; u <= maxUnit; u++) targets.Add(u);
                else targets.Add(kv.Key);
                foreach (int u in targets) ConvertUnit(p, kv.Value, u, namesHidden, numbersHidden);
            }

            string reference = Get(props, "Reference");
            p.Prefix = Regex.Replace(reference, "[0-9?]+$", "");
            if (p.Prefix == "" || p.Prefix.StartsWith("#")) p.Prefix = "U";
            p.Prefix += "?";
            p.Title = sym.S(1);
            p.Desc = Get(props, "Description", "ki_description");
            p.Footprint = FootprintName(Get(props, "Footprint"));
            string mpn = Get(props, "MPN", "MP", "Manufacturer_Part_Number", "Manufacturer Part Number", "MANUFACTURER_PART_NUMBER", "Mfr. Part #", "PartNumber", "Part Number");
            string man = Get(props, "Manufacturer", "MF", "Manufacturer_Name", "MANUFACTURER", "Mfr.", "Mfr");
            string ds = Get(props, "Datasheet");
            if (man != "") p.Params["Manufacturer"] = man;
            if (mpn != "") p.Params["Manufacturer Part Number"] = mpn;
            if (ds.StartsWith("http", StringComparison.OrdinalIgnoreCase)) p.Params["Datasheet"] = ds;
            p.Info["MPN"] = mpn != "" ? mpn : p.Title;
            p.Info["Manufacturer"] = man;
            p.Info["Datasheet"] = ds.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? ds : "";
            p.Info["Description"] = p.Desc;
            p.Info["Keywords"] = Get(props, "ki_keywords");
        }

        static void ConvertUnit(Part p, SNode unit, int u, bool namesHidden, bool numbersHidden)
        {
            foreach (SNode el in unit.Items)
            {
                if (!el.IsList) continue;
                switch (el.Head)
                {
                    case "rectangle":
                        {
                            double[] a = XY(el.Find("start")), b = XY(el.Find("end"));
                            SShape s = new SShape { Kind = "RECT", Unit = u, Fill = FillOf(el) };
                            s.Pts.Add(new PointD(Mil(a[0]), Mil(a[1])));
                            s.Pts.Add(new PointD(Mil(b[0]), Mil(b[1])));
                            p.Shapes.Add(s);
                            break;
                        }
                    case "polyline":
                    case "bezier":
                        {
                            SNode pts = el.Find("pts");
                            if (pts == null) break;
                            List<PointD> l = new List<PointD>();
                            foreach (SNode xy in pts.All("xy")) l.Add(new PointD(Mil(xy.N(1)), Mil(xy.N(2))));
                            if (el.Head == "bezier" && l.Count == 4) l = Bezier(l);
                            if (l.Count < 2) break;
                            int fill = FillOf(el);
                            SShape s = new SShape { Kind = fill != 0 && l.Count >= 3 ? "POLY" : "LINE", Unit = u, Fill = fill, Pts = l };
                            p.Shapes.Add(s);
                            break;
                        }
                    case "circle":
                        {
                            double[] c = XY(el.Find("center"));
                            SNode rn = el.Find("radius");
                            double r = rn != null ? rn.N(1) : 0;
                            SShape s = new SShape { Kind = "ELL", Unit = u, Fill = FillOf(el), R = Mil(r), R2 = Mil(r) };
                            s.Pts.Add(new PointD(Mil(c[0]), Mil(c[1])));
                            p.Shapes.Add(s);
                            break;
                        }
                    case "arc":
                        {
                            SNode st = el.Find("start"), mi = el.Find("mid"), en = el.Find("end");
                            double cx, cy, r, a1, a2;
                            if (st != null && mi != null && en != null)
                                ArcFrom3(st.N(1), st.N(2), mi.N(1), mi.N(2), en.N(1), en.N(2), out cx, out cy, out r, out a1, out a2);
                            else
                            {
                                SNode rad = el.Find("radius");
                                if (rad == null) break;
                                double[] at = XY(rad.Find("at"));
                                SNode ln = rad.Find("length"), an = rad.Find("angles");
                                cx = at[0]; cy = at[1]; r = ln != null ? ln.N(1) : 0;
                                a1 = an != null ? an.N(1) : 0; a2 = an != null ? an.N(2) : 90;
                            }
                            SShape s = new SShape { Kind = "ARC", Unit = u, R = Mil(r), A1 = a1, A2 = a2 };
                            s.Pts.Add(new PointD(Mil(cx), Mil(cy)));
                            p.Shapes.Add(s);
                            break;
                        }
                    case "text":
                        {
                            SNode at = el.Find("at");
                            if (at == null) break;
                            SShape s = new SShape { Kind = "TEXT", Unit = u, Text = el.S(1), Rot = at.N(3) };
                            SNode eff = el.Find("effects");
                            SNode font = eff != null ? eff.Find("font") : null;
                            SNode size = font != null ? font.Find("size") : null;
                            s.Size = Mil(size != null ? size.N(1) : 1.27) / 10;
                            s.Pts.Add(new PointD(Mil(at.N(1)), Mil(at.N(2))));
                            if (s.Text != "") p.Shapes.Add(s);
                            break;
                        }
                    case "pin":
                        {
                            SNode at = el.Find("at");
                            if (at == null) break;
                            double hx = Mil(at.N(1)), hy = Mil(at.N(2)), rot = at.N(3);
                            SNode ln = el.Find("length");
                            double len = Mil(ln != null ? ln.N(1) : 2.54);
                            int q = ((int)Math.Round(Norm(rot) / 90.0)) % 4;
                            double dx = q == 0 ? 1 : q == 2 ? -1 : 0, dy = q == 1 ? 1 : q == 3 ? -1 : 0;
                            SPin pin = new SPin();
                            pin.Unit = u;
                            pin.Elec = ElecOf(el.S(1));
                            string shape = el.S(2);
                            pin.Dot = shape == "inverted" || shape == "inverted_clock";
                            pin.Clock = shape == "clock" || shape == "inverted_clock" || shape == "clock_low";
                            pin.X = hx + dx * len;
                            pin.Y = hy + dy * len;
                            pin.Len = len;
                            pin.Orient = (q + 2) % 4;
                            SNode nm = el.Find("name"), num = el.Find("number");
                            pin.Name = PinText(nm != null ? nm.S(1) : "");
                            pin.Num = num != null ? num.S(1) : "";
                            pin.Hidden = el.Hidden;
                            pin.NameVis = !namesHidden && pin.Name != "";
                            pin.NumVis = !numbersHidden;
                            p.Pins.Add(pin);
                            break;
                        }
                }
            }
        }

        static List<PointD> Bezier(List<PointD> c)
        {
            List<PointD> r = new List<PointD>();
            for (int i = 0; i <= 16; i++)
            {
                double t = i / 16.0, a = (1 - t) * (1 - t) * (1 - t), b = 3 * (1 - t) * (1 - t) * t, d = 3 * (1 - t) * t * t, e = t * t * t;
                r.Add(new PointD(a * c[0].X + b * c[1].X + d * c[2].X + e * c[3].X, a * c[0].Y + b * c[1].Y + d * c[2].Y + e * c[3].Y));
            }
            return r;
        }

        // ================================================================ legacy symbols (KiCad 5 .lib / .dcm)

        public class LegacySym
        {
            public string Name = "", Ref = "U", Footprint = "", Datasheet = "";
            public bool ShowNum = true, ShowName = true;
            public int Units = 1;
            public List<string> Aliases = new List<string>();
            public List<string> Draw = new List<string>();
        }

        public static List<LegacySym> ParseLegacyLib(string text)
        {
            List<LegacySym> list = new List<LegacySym>();
            LegacySym cur = null;
            bool draw = false, fplist = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] t = Tok(line);
                if (t.Length == 0) continue;
                if (t[0] == "DEF" && t.Length >= 9)
                {
                    cur = new LegacySym { Name = t[1].TrimStart('~'), Ref = t[2] };
                    cur.ShowNum = t[5] == "Y";
                    cur.ShowName = t[6] == "Y";
                    int un;
                    if (int.TryParse(t[7], NumberStyles.Integer, Inv, out un)) cur.Units = Math.Max(1, un);
                    draw = false;
                    continue;
                }
                if (cur == null) continue;
                if (t[0] == "ENDDEF") { list.Add(cur); cur = null; continue; }
                if (t[0] == "$FPLIST") { fplist = true; continue; }
                if (t[0] == "$ENDFPLIST") { fplist = false; continue; }
                if (fplist) continue;
                if (t[0] == "DRAW") { draw = true; continue; }
                if (t[0] == "ENDDRAW") { draw = false; continue; }
                if (draw) { cur.Draw.Add(line); continue; }
                if (t[0] == "ALIAS") { for (int i = 1; i < t.Length; i++) cur.Aliases.Add(t[i]); continue; }
                if (t[0] == "F2" && t.Length > 1) cur.Footprint = t[1];
                if (t[0] == "F3" && t.Length > 1) cur.Datasheet = t[1];
                if (t[0] == "F0" && t.Length > 1 && cur.Ref == "") cur.Ref = t[1];
            }
            return list;
        }

        // name -> (description, keywords, datasheet)
        public static Dictionary<string, string[]> ParseDcm(string text)
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            string name = null;
            string[] cur = null;
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("$CMP ")) { name = line.Substring(5).Trim(); cur = new string[] { "", "", "" }; continue; }
                if (line.StartsWith("$ENDCMP")) { if (name != null) d[name] = cur; name = null; continue; }
                if (name == null) continue;
                if (line.StartsWith("D ")) cur[0] = line.Substring(2).Trim();
                else if (line.StartsWith("K ")) cur[1] = line.Substring(2).Trim();
                else if (line.StartsWith("F ")) cur[2] = line.Substring(2).Trim();
            }
            return d;
        }

        static string[] Tok(string line)
        {
            List<string> r = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i])) { i++; continue; }
                if (line[i] == '"')
                {
                    int e = line.IndexOf('"', i + 1);
                    if (e < 0) e = line.Length;
                    r.Add(line.Substring(i + 1, e - i - 1));
                    i = e + 1;
                }
                else
                {
                    int s = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                    r.Add(line.Substring(s, i - s));
                }
            }
            return r.ToArray();
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : 0;
        }

        static int I(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, Inv, out v) ? v : 0;
        }

        static int LegacyFill(string f) { return f == "F" ? 2 : f == "f" ? 1 : 0; }

        public static void ConvertLegacy(Part p, LegacySym s, string name, string[] dcm)
        {
            p.PartCount = s.Units;
            foreach (string line in s.Draw)
            {
                string[] t = Tok(line);
                if (t.Length == 0) continue;
                try
                {
                    switch (t[0])
                    {
                        case "S":
                            {
                                if (t.Length < 8 || I(t[6]) > 1) break;
                                foreach (int u in UnitsOf(I(t[5]), s.Units))
                                {
                                    SShape sh = new SShape { Kind = "RECT", Unit = u, Fill = LegacyFill(t.Length > 8 ? t[8] : "N") };
                                    sh.Pts.Add(new PointD(D(t[1]), D(t[2])));
                                    sh.Pts.Add(new PointD(D(t[3]), D(t[4])));
                                    p.Shapes.Add(sh);
                                }
                                break;
                            }
                        case "P":
                            {
                                int n = I(t[1]);
                                if (t.Length < 5 + 2 * n || I(t[3]) > 1) break;
                                List<PointD> pts = new List<PointD>();
                                for (int k = 0; k < n; k++) pts.Add(new PointD(D(t[5 + 2 * k]), D(t[6 + 2 * k])));
                                int fill = LegacyFill(t.Length > 5 + 2 * n ? t[5 + 2 * n] : "N");
                                if (pts.Count < 2) break;
                                foreach (int u in UnitsOf(I(t[2]), s.Units))
                                    p.Shapes.Add(new SShape { Kind = fill != 0 && pts.Count >= 3 ? "POLY" : "LINE", Unit = u, Fill = fill, Pts = new List<PointD>(pts) });
                                break;
                            }
                        case "C":
                            {
                                if (t.Length < 7 || I(t[5]) > 1) break;
                                foreach (int u in UnitsOf(I(t[4]), s.Units))
                                {
                                    SShape sh = new SShape { Kind = "ELL", Unit = u, R = D(t[3]), R2 = D(t[3]), Fill = LegacyFill(t.Length > 7 ? t[7] : "N") };
                                    sh.Pts.Add(new PointD(D(t[1]), D(t[2])));
                                    p.Shapes.Add(sh);
                                }
                                break;
                            }
                        case "A":
                            {
                                if (t.Length < 9 || I(t[7]) > 1) break;
                                double a1 = Norm(D(t[4]) / 10.0), a2 = Norm(D(t[5]) / 10.0);
                                if (Norm(a2 - a1) > 180) { double tmp = a1; a1 = a2; a2 = tmp; }
                                foreach (int u in UnitsOf(I(t[6]), s.Units))
                                {
                                    SShape sh = new SShape { Kind = "ARC", Unit = u, R = D(t[3]), A1 = a1, A2 = a2 };
                                    sh.Pts.Add(new PointD(D(t[1]), D(t[2])));
                                    p.Shapes.Add(sh);
                                }
                                break;
                            }
                        case "T":
                            {
                                if (t.Length < 9 || I(t[7]) > 1) break;
                                foreach (int u in UnitsOf(I(t[6]), s.Units))
                                {
                                    SShape sh = new SShape { Kind = "TEXT", Unit = u, Rot = D(t[1]) / 10.0, Size = D(t[4]) / 10.0, Text = t[8].Replace('~', ' ') };
                                    sh.Pts.Add(new PointD(D(t[2]), D(t[3])));
                                    p.Shapes.Add(sh);
                                }
                                break;
                            }
                        case "X":
                            {
                                if (t.Length < 12 || I(t[10]) > 1) break;
                                double hx = D(t[3]), hy = D(t[4]), len = D(t[5]);
                                string o = t[6];
                                int q = o == "R" ? 0 : o == "U" ? 1 : o == "L" ? 2 : 3;
                                double dx = q == 0 ? 1 : q == 2 ? -1 : 0, dy = q == 1 ? 1 : q == 3 ? -1 : 0;
                                string et = t[11], shp = t.Length > 12 ? t[12] : "";
                                foreach (int u in UnitsOf(I(t[9]), s.Units))
                                {
                                    SPin pin = new SPin();
                                    pin.Unit = u;
                                    pin.Name = LegacyText(t[1]);
                                    pin.Num = t[2] == "~" ? "" : t[2];
                                    pin.X = hx + dx * len; pin.Y = hy + dy * len; pin.Len = len;
                                    pin.Orient = (q + 2) % 4;
                                    pin.Elec = et == "I" ? 1 : et == "O" ? 2 : et == "B" ? 3 : et == "W" || et == "w" ? 4 :
                                               et == "C" ? 5 : et == "T" ? 6 : et == "E" ? 7 : 0;
                                    pin.Hidden = shp.StartsWith("N");
                                    string vs = shp.TrimStart('N');
                                    pin.Dot = vs == "I" || vs == "CI";
                                    pin.Clock = vs == "C" || vs == "CI" || vs == "CL";
                                    pin.NameVis = s.ShowName && pin.Name != "";
                                    pin.NumVis = s.ShowNum;
                                    p.Pins.Add(pin);
                                }
                                break;
                            }
                    }
                }
                catch { }
            }
            p.Prefix = Regex.Replace(s.Ref ?? "", "[0-9?]+$", "");
            if (p.Prefix == "" || p.Prefix.StartsWith("#")) p.Prefix = "U";
            p.Prefix += "?";
            p.Title = name;
            p.Desc = dcm != null ? dcm[0] : "";
            p.Footprint = FootprintName(s.Footprint);
            string ds = dcm != null && dcm[2] != "" ? dcm[2] : s.Datasheet;
            if (ds.StartsWith("http", StringComparison.OrdinalIgnoreCase)) p.Params["Datasheet"] = ds;
            p.Info["MPN"] = name;
            p.Info["Description"] = p.Desc;
            p.Info["Datasheet"] = ds.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? ds : "";
            p.Info["Keywords"] = dcm != null ? dcm[1] : "";
        }

        static string LegacyText(string s)
        {
            if (s == "~") return "";
            if (s.IndexOf('~') < 0) return s;
            // ~ toggles the overbar in KiCad 5
            StringBuilder sb = new StringBuilder();
            bool bar = false;
            foreach (char c in s)
            {
                if (c == '~') { bar = !bar; continue; }
                sb.Append(c);
                if (bar) sb.Append('\\');
            }
            return sb.ToString();
        }

        static IEnumerable<int> UnitsOf(int u, int units)
        {
            if (u == 0) { for (int i = 1; i <= Math.Max(1, units); i++) yield return i; }
            else yield return u;
        }

        // ================================================================ footprints

        static string LayerOf(string kl)
        {
            switch (kl)
            {
                case "F.SilkS": case "F.Silkscreen": return "TO";
                case "B.SilkS": case "B.Silkscreen": return "BO";
                case "F.Fab": return "ASM";
                case "F.CrtYd": case "F.Courtyard": return "CRT";
                case "F.Cu": return "T";
                case "B.Cu": return "B";
                default: return null;
            }
        }

        static double WidthOf(SNode el)
        {
            SNode w = el.Find("width");
            if (w != null) return w.N(1);
            SNode st = el.Find("stroke");
            if (st != null && st.Find("width") != null) return st.Find("width").N(1);
            return 0.12;
        }

        static bool Filled(SNode el, bool def)
        {
            SNode f = el.Find("fill");
            if (f == null) return def;
            string v = f.S(1);
            if (v == "") { SNode t = f.Find("type"); v = t != null ? t.S(1) : ""; }
            return v == "solid" || v == "yes" || v == "true";
        }

        // KiCad footprint coordinates (mm, Y down) -> mil, Y up
        static PointD F(double x, double y) { return new PointD(Mil(x), -Mil(y)); }

        // returns the footprint name; fills pads and graphics of p; model gets the first 3D model
        public static string ConvertFootprint(Part p, string text, out KiModel model)
        {
            model = null;
            SNode root = SExpr.Parse(text);
            if (root.Head != "footprint" && root.Head != "module") throw new Exception("не похоже на футпринт KiCad");
            bool legacy = root.Head == "module";
            string name = root.S(1);
            SNode descr = root.Find("descr");
            if (descr != null) p.FootprintDesc = descr.S(1);

            foreach (SNode el in root.Items)
            {
                if (!el.IsList) continue;
                try
                {
                    switch (el.Head)
                    {
                        case "pad": ConvertPad(p, el); break;
                        case "fp_line":
                            {
                                string l = LayerOf(el.Find("layer") != null ? el.Find("layer").S(1) : "");
                                if (l == null) break;
                                double[] a = XY(el.Find("start")), b = XY(el.Find("end"));
                                AddTrack(p, l, WidthOf(el), F(a[0], a[1]), F(b[0], b[1]));
                                break;
                            }
                        case "fp_rect":
                            {
                                string l = LayerOf(el.Find("layer") != null ? el.Find("layer").S(1) : "");
                                if (l == null) break;
                                double[] a = XY(el.Find("start")), b = XY(el.Find("end"));
                                List<PointD> c = new List<PointD> { F(a[0], a[1]), F(b[0], a[1]), F(b[0], b[1]), F(a[0], b[1]) };
                                AddOutline(p, l, WidthOf(el), c, Filled(el, false));
                                break;
                            }
                        case "fp_poly":
                            {
                                string l = LayerOf(el.Find("layer") != null ? el.Find("layer").S(1) : "");
                                if (l == null) break;
                                List<PointD> c = PolyPoints(el.Find("pts"), 0, 0, 0);
                                if (c.Count >= 2) AddOutline(p, l, WidthOf(el), c, Filled(el, true));
                                break;
                            }
                        case "fp_circle":
                            {
                                string l = LayerOf(el.Find("layer") != null ? el.Find("layer").S(1) : "");
                                if (l == null) break;
                                double[] c = XY(el.Find("center")), e = XY(el.Find("end"));
                                double r = Math.Sqrt((e[0] - c[0]) * (e[0] - c[0]) + (e[1] - c[1]) * (e[1] - c[1]));
                                PointD cc = F(c[0], c[1]);
                                if (Filled(el, false))
                                {
                                    List<PointD> poly = new List<PointD>();
                                    for (int i = 0; i < 36; i++) poly.Add(new PointD(cc.X + Mil(r) * Math.Cos(i * Math.PI / 18), cc.Y + Mil(r) * Math.Sin(i * Math.PI / 18)));
                                    p.Gfx.Add(new FGfx { Kind = "REG", Layer = l, Pts = poly });
                                }
                                else p.Gfx.Add(new FGfx { Kind = "ARC", Layer = l, W = Mil(WidthOf(el)), CX = cc.X, CY = cc.Y, R = Mil(r), A1 = 0, A2 = 360 });
                                break;
                            }
                        case "fp_arc":
                            {
                                string l = LayerOf(el.Find("layer") != null ? el.Find("layer").S(1) : "");
                                if (l == null) break;
                                FGfx g = new FGfx { Kind = "ARC", Layer = l, W = Mil(WidthOf(el)) };
                                SNode mid = el.Find("mid");
                                if (mid != null && !legacy)
                                {
                                    double[] s = XY(el.Find("start")), m = XY(mid), e = XY(el.Find("end"));
                                    PointD ps = F(s[0], s[1]), pm = F(m[0], m[1]), pe = F(e[0], e[1]);
                                    double cx, cy, r, a1, a2;
                                    ArcFrom3(ps.X, ps.Y, pm.X, pm.Y, pe.X, pe.Y, out cx, out cy, out r, out a1, out a2);
                                    g.CX = cx; g.CY = cy; g.R = r; g.A1 = a1; g.A2 = a2;
                                }
                                else
                                {
                                    // KiCad 5: start = centre, end = arc start, angle = sweep (clockwise on screen)
                                    double[] c = XY(el.Find("start")), s = XY(el.Find("end"));
                                    SNode an = el.Find("angle");
                                    double sweep = an != null ? an.N(1) : 90;
                                    PointD pc = F(c[0], c[1]), ps = F(s[0], s[1]);
                                    double r = Math.Sqrt((ps.X - pc.X) * (ps.X - pc.X) + (ps.Y - pc.Y) * (ps.Y - pc.Y));
                                    double as_ = Ang(ps.X - pc.X, ps.Y - pc.Y);
                                    g.CX = pc.X; g.CY = pc.Y; g.R = r;
                                    if (sweep >= 0) { g.A1 = Norm(as_ - sweep); g.A2 = as_; }
                                    else { g.A1 = as_; g.A2 = Norm(as_ - sweep); }
                                }
                                if (g.R > 0.01) p.Gfx.Add(g);
                                break;
                            }
                        case "model":
                            {
                                if (model != null || el.Hidden) break;
                                KiModel m = new KiModel { Path = el.S(1) };
                                SNode off = el.Find("offset"), at = el.Find("at"), sc = el.Find("scale"), rot = el.Find("rotate");
                                SNode xyz;
                                if (off != null && (xyz = off.Find("xyz")) != null) { m.OffX = xyz.N(1); m.OffY = xyz.N(2); m.OffZ = xyz.N(3); }
                                else if (at != null && (xyz = at.Find("xyz")) != null) { m.OffX = xyz.N(1) * 25.4; m.OffY = xyz.N(2) * 25.4; m.OffZ = xyz.N(3) * 25.4; }
                                if (sc != null && (xyz = sc.Find("xyz")) != null) { m.ScaleX = xyz.N(1); m.ScaleY = xyz.N(2); m.ScaleZ = xyz.N(3); }
                                if (rot != null && (xyz = rot.Find("xyz")) != null) { m.RotX = xyz.N(1); m.RotY = xyz.N(2); m.RotZ = xyz.N(3); }
                                model = m;
                                break;
                            }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("kicad footprint item skipped: " + el.Head + ": " + ex.Message); }
            }
            if (p.Footprint == "") p.Footprint = name;
            p.Info["Footprint"] = p.Footprint;
            return name;
        }

        static void AddTrack(Part p, string layer, double wmm, PointD a, PointD b)
        {
            FGfx g = new FGfx { Kind = "TRK", Layer = layer, W = Mil(wmm > 0 ? wmm : 0.1) };
            g.Pts.Add(a); g.Pts.Add(b);
            p.Gfx.Add(g);
        }

        static void AddOutline(Part p, string layer, double wmm, List<PointD> pts, bool fill)
        {
            if (fill && pts.Count >= 3) { p.Gfx.Add(new FGfx { Kind = "REG", Layer = layer, Pts = pts }); return; }
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts.Count == 2 && i == 1) break;
                AddTrack(p, layer, wmm, pts[i], pts[(i + 1) % pts.Count]);
            }
        }

        // polygon points (with optional arcs) of a "pts" node; offsets/rotation for pad-local primitives
        static List<PointD> PolyPoints(SNode pts, double ox, double oy, double rotDeg)
        {
            List<PointD> r = new List<PointD>();
            if (pts == null) return r;
            foreach (SNode n in pts.Items)
            {
                if (!n.IsList) continue;
                if (n.Head == "xy") r.Add(new PointD(n.N(1), n.N(2)));
                else if (n.Head == "arc")
                {
                    double[] s = XY(n.Find("start")), m = XY(n.Find("mid")), e = XY(n.Find("end"));
                    double cx, cy, rr, a1, a2;
                    ArcFrom3(s[0], s[1], m[0], m[1], e[0], e[1], out cx, out cy, out rr, out a1, out a2);
                    double sa = Ang(s[0] - cx, s[1] - cy), ea = Ang(e[0] - cx, e[1] - cy), ma = Ang(m[0] - cx, m[1] - cy);
                    double sweep = Norm(ea - sa);
                    bool ccw = Norm(ma - sa) <= sweep;
                    if (!ccw) sweep = sweep - 360;
                    for (int i = 0; i <= 12; i++)
                    {
                        double a = (sa + sweep * i / 12.0) * Math.PI / 180;
                        r.Add(new PointD(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a)));
                    }
                }
            }
            // to board coordinates (mil, Y up)
            List<PointD> o = new List<PointD>();
            double rad = rotDeg * Math.PI / 180;
            foreach (PointD q in r)
            {
                double x = q.X, y = -q.Y;                       // Y up, still pad-local
                double xr = x * Math.Cos(rad) - y * Math.Sin(rad), yr = x * Math.Sin(rad) + y * Math.Cos(rad);
                o.Add(new PointD(Mil(xr + ox), Mil(yr - oy)));
            }
            return o;
        }

        static void ConvertPad(Part p, SNode el)
        {
            string num = el.S(1), type = el.S(2), shape = el.S(3);
            SNode at = el.Find("at"), size = el.Find("size"), drill = el.Find("drill"), layers = el.Find("layers");
            if (at == null || size == null) return;
            FPad pad = new FPad();
            pad.Name = num;
            PointD c = F(at.N(1), at.N(2));
            pad.X = c.X; pad.Y = c.Y;
            pad.Rot = Norm(at.N(3));
            pad.SX = Mil(size.N(1)); pad.SY = Mil(size.N(2));
            bool front = false, back = false, all = false;
            if (layers != null)
                for (int i = 1; i < layers.Items.Count; i++)
                {
                    string l = layers.S(i);
                    if (l == "F.Cu") front = true;
                    else if (l == "B.Cu") back = true;
                    else if (l == "*.Cu") all = true;
                }
            if (type == "thru_hole" || type == "np_thru_hole" || all || (front && back)) pad.Layer = "M";
            else pad.Layer = back && !front ? "B" : "T";
            pad.Plated = type != "np_thru_hole";
            if (drill != null)
            {
                if (drill.S(1) == "oval")
                {
                    double w = drill.N(2), h = drill.N(3);
                    if (h <= 0) h = w;
                    pad.Hole = Mil(Math.Min(w, h));
                    pad.SlotLen = Mil(Math.Max(w, h));
                    pad.SlotRot = Norm(pad.Rot + (h > w ? 90 : 0));
                    if (Math.Abs(w - h) < 1e-6) pad.SlotLen = 0;
                }
                else if (drill.Items.Count > 1)
                {
                    double d = drill.N(1);
                    if (d <= 0) d = drill.N(2);
                    pad.Hole = Mil(d);
                }
            }
            if (pad.Layer != "M") { pad.Hole = 0; pad.SlotLen = 0; }
            else if (pad.Hole <= 0) pad.Hole = Math.Min(pad.SX, pad.SY) * 0.6;
            switch (shape)
            {
                case "circle": pad.Shape = 'C'; pad.SY = pad.SX; break;
                case "oval": pad.Shape = Math.Abs(pad.SX - pad.SY) < 0.01 ? 'C' : 'O'; break;
                case "roundrect":
                    {
                        SNode rr = el.Find("roundrect_rratio");
                        double ratio = rr != null ? rr.N(1) : 0.25;
                        pad.Shape = ratio >= 0.49 ? (Math.Abs(pad.SX - pad.SY) < 0.01 ? 'C' : 'O') : 'R';
                        break;
                    }
                case "custom":
                    {
                        pad.Shape = 'R';
                        SNode prim = el.Find("primitives");
                        if (prim != null)
                        {
                            SNode poly = prim.Find("gr_poly");
                            if (poly != null)
                            {
                                List<PointD> pts = PolyPoints(poly.Find("pts"), at.N(1), at.N(2), pad.Rot);
                                if (pts.Count >= 3) { pad.Shape = 'P'; pad.Poly = pts; }
                            }
                        }
                        SNode opt = el.Find("options");
                        SNode anchor = opt != null ? opt.Find("anchor") : null;
                        if (pad.Shape != 'P' && anchor != null && anchor.S(1) == "circle") pad.Shape = 'C';
                        break;
                    }
                default: pad.Shape = 'R'; break;
            }
            if (type == "np_thru_hole" && pad.SX <= pad.Hole + 0.01) { pad.SX = pad.SY = pad.Hole; }
            p.Pads.Add(pad);
        }

        // ================================================================ 3D placement

        // Altium placement of a STEP model referenced by a KiCad footprint
        public static ModelPlacement Place(byte[] step, KiModel m)
        {
            ModelPlacement mp = new ModelPlacement();
            mp.RotX = Norm(-m.RotX); mp.RotY = Norm(-m.RotY); mp.RotZ = Norm(-m.RotZ);
            mp.OriginX = Mil(m.OffX);
            mp.OriginY = Mil(m.OffY);
            mp.Dz = Mil(m.OffZ);
            List<double[]> pts = Step.Points(step);
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            foreach (double[] v in pts)
            {
                double[] r = Step.Rotate(new double[] { v[0] * m.ScaleX, v[1] * m.ScaleY, v[2] * m.ScaleZ }, mp.RotX, mp.RotY, mp.RotZ);
                minx = Math.Min(minx, r[0]); maxx = Math.Max(maxx, r[0]);
                miny = Math.Min(miny, r[1]); maxy = Math.Max(maxy, r[1]);
                minz = Math.Min(minz, r[2]); maxz = Math.Max(maxz, r[2]);
            }
            if (pts.Count == 0) { minx = maxx = miny = maxy = minz = maxz = 0; }
            mp.CenterX = mp.OriginX + Mil((minx + maxx) / 2);
            mp.CenterY = mp.OriginY + Mil((miny + maxy) / 2);
            mp.SizeX = Mil(maxx - minx);
            mp.SizeY = Mil(maxy - miny);
            mp.BottomZ = mp.Dz + Mil(minz);
            mp.TopZ = mp.Dz + Mil(maxz);
            return mp;
        }

        public static string ModelBaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/');
            int i = s.LastIndexOf('/');
            if (i >= 0) s = s.Substring(i + 1);
            int d = s.LastIndexOf('.');
            return d > 0 ? s.Substring(0, d) : s;
        }

        // "${KICAD9_3DMODEL_DIR}/Package_QFP.3dshapes/LQFP-48.wrl" -> "Package_QFP.3dshapes/LQFP-48.step"
        public static string OfficialModelPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/');
            Match m = Regex.Match(s, "([^/]+\\.3dshapes)/([^/]+)$");
            if (!m.Success) return "";
            return m.Groups[1].Value + "/" + ModelBaseName(m.Groups[2].Value) + ".step";
        }

        public static void FinishInfo(Part p)
        {
            p.Info["Title"] = p.Title;
            p.Info["Footprint"] = p.Footprint;
            p.Info["Pins"] = p.Pins.Count.ToString(Inv);
            p.Info["Pads"] = p.Pads.Count.ToString(Inv);
            if (!p.Info.ContainsKey("Model3D")) p.Info["Model3D"] = p.KiModel != null || p.StepData != null ? "yes" : "no";
            if (!p.Info.ContainsKey("Class")) p.Info["Class"] = "KiCad";
        }
    }
}
