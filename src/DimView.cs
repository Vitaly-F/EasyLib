// Footprint dimension check: pad and body extents, pitch, row spacing, pad sizes,
// a check against the sizes written in the footprint name, pad info on hover and a ruler.
// Part coordinates are mil, Y up.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace EasyLib
{
    class PadGroup
    {
        public FPad First;
        public int Count;
        public double W, H;     // along the screen axes when the pad is rotated by a multiple of 90
        public bool Rotated;    // rotated by another angle: W, H are the pad's own sizes
    }

    class DimLine
    {
        public PointD A, B;      // measured points (mil)
        public bool Horizontal;  // measure along X (else along Y)
        public int Side;         // horizontal: +1 above, -1 below; vertical: +1 right, -1 left
        public int Level;        // 1, 2, 3: distance from the footprint
        public string Prefix = "";
        public int Kind;         // 0 overall pads, 1 body, 2 pitch / spacing
        public double Value { get { return Horizontal ? Math.Abs(B.X - A.X) : Math.Abs(B.Y - A.Y); } }
    }

    class CheckRow
    {
        public string Label, Value;
        public int State;        // 0 info, 1 ok, 2 warning
        public CheckRow(string l, string v, int s) { Label = l; Value = v; State = s; }
    }

    class FpAnalysis
    {
        public bool HasPads;
        public double PMinX, PMinY, PMaxX, PMaxY;     // copper extents
        public bool HasBody;
        public string BodyLabel = "";
        public double BMinX, BMinY, BMaxX, BMaxY;
        public double Pitch;                          // 0 = none
        public bool PitchIsRow;                       // pads in a row of 3+ (else: two pads)
        public FPad PitchA, PitchB;
        public bool PitchHorizontal;
        public double RowSpan, ColSpan;               // centre-to-centre distance of the outer rows / columns
        public double RowY1, RowY2, RowX;             // rows: y of both rows, x where to draw
        public double ColX1, ColX2, ColY;
        public List<PadGroup> Groups = new List<PadGroup>();
        public List<DimLine> Dims = new List<DimLine>();
        public List<CheckRow> Name = new List<CheckRow>();
        public int Smd, Th, Npth;

        const double Tol = 0.6;   // mil

        public static void PadBox(FPad pad, out double x0, out double y0, out double x1, out double y1)
        {
            if (pad.Shape == 'P' && pad.Poly != null && pad.Poly.Count > 2)
            {
                Geo.BBox(pad.Poly, out x0, out y0, out x1, out y1);
                return;
            }
            double hx = pad.SX / 2, hy = pad.SY / 2;
            if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01) { hx = hy = pad.Hole / 2; }
            if (pad.Shape == 'C') { hx = hy = Math.Max(hx, hy); }
            double a = pad.Rot * Math.PI / 180, c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
            double ex = hx * c + hy * s, ey = hx * s + hy * c;
            x0 = pad.X - ex; x1 = pad.X + ex; y0 = pad.Y - ey; y1 = pad.Y + ey;
        }

        static bool IsNpth(FPad p) { return !p.Plated && p.Hole > 0; }

        public static FpAnalysis Analyze(Part p)
        {
            FpAnalysis r = new FpAnalysis();
            List<FPad> pads = new List<FPad>();
            foreach (FPad pad in p.Pads)
            {
                if (IsNpth(pad)) { r.Npth++; continue; }
                if (pad.Hole > 0 || pad.Layer == "M") r.Th++; else r.Smd++;
                pads.Add(pad);
            }
            // copper extents
            r.PMinX = r.PMinY = double.MaxValue; r.PMaxX = r.PMaxY = double.MinValue;
            foreach (FPad pad in pads)
            {
                double x0, y0, x1, y1;
                PadBox(pad, out x0, out y0, out x1, out y1);
                r.PMinX = Math.Min(r.PMinX, x0); r.PMinY = Math.Min(r.PMinY, y0);
                r.PMaxX = Math.Max(r.PMaxX, x1); r.PMaxY = Math.Max(r.PMaxY, y1);
            }
            r.HasPads = pads.Count > 0;

            // body: 3D outline (EasyEDA) / fab layer (KiCad), else silkscreen
            string[] order = p.Source == "easyeda" ? new string[] { "CRT", "ASM", "TO" } : new string[] { "ASM", "CRT", "TO" };
            foreach (string layer in order)
            {
                List<PointD> pts = new List<PointD>();
                // a closed outline on the layer is the body; other items there (pin 1 marks) are not
                FGfx outline = null; double outlineArea = 0;
                foreach (FGfx g in p.Gfx)
                    if (g.Layer == layer && g.Kind == "REG" && g.Pts.Count >= 3)
                    {
                        double a = ShapeOrder.PolyArea(g.Pts);
                        if (a > outlineArea) { outlineArea = a; outline = g; }
                    }
                foreach (FGfx g in p.Gfx)
                {
                    if (g.Layer != layer) continue;
                    if (outline != null && g != outline) continue;
                    if (g.Kind == "ARC")
                    {
                        double sweep = Geo.Norm(g.A2 - g.A1); if (sweep == 0) sweep = 360;
                        for (int i = 0; i <= 24; i++)
                        {
                            double a = (g.A1 + sweep * i / 24) * Math.PI / 180;
                            pts.Add(new PointD(g.CX + g.R * Math.Cos(a), g.CY + g.R * Math.Sin(a)));
                        }
                    }
                    else pts.AddRange(g.Pts);
                }
                if (pts.Count < 2) continue;
                double x0, y0, x1, y1;
                Geo.BBox(pts, out x0, out y0, out x1, out y1);
                if (x1 - x0 < 1 || y1 - y0 < 1) continue;
                r.HasBody = true;
                r.BMinX = x0; r.BMinY = y0; r.BMaxX = x1; r.BMaxY = y1;
                r.BodyLabel = layer == "TO" ? "по шелкографии" : layer == "CRT" && p.Source == "easyeda" ? "контур корпуса" :
                              layer == "CRT" ? "по courtyard" : "по слою Fab";
                break;
            }

            r.FindPitch(pads);
            r.FindGroups(pads);
            r.MakeDims();
            r.CheckName(p.Footprint);
            return r;
        }

        // rows (same Y) and columns (same X) of pads
        static List<List<FPad>> Lines(List<FPad> pads, bool byY)
        {
            List<FPad> s = new List<FPad>(pads);
            s.Sort(delegate(FPad a, FPad b) { return (byY ? a.Y : a.X).CompareTo(byY ? b.Y : b.X); });
            List<List<FPad>> res = new List<List<FPad>>();
            List<FPad> cur = null;
            double last = double.NaN;
            foreach (FPad pad in s)
            {
                double v = byY ? pad.Y : pad.X;
                if (cur == null || Math.Abs(v - last) > Tol) { cur = new List<FPad>(); res.Add(cur); }
                cur.Add(pad);
                last = v;
            }
            List<List<FPad>> keep = new List<List<FPad>>();
            foreach (List<FPad> l in res)
            {
                if (l.Count < 2) continue;
                l.Sort(delegate(FPad a, FPad b) { return (byY ? a.X : a.Y).CompareTo(byY ? b.X : b.Y); });
                keep.Add(l);
            }
            return keep;
        }

        void FindPitch(List<FPad> pads)
        {
            List<List<FPad>> rows = Lines(pads, true), cols = Lines(pads, false);
            // most common gap between neighbouring pads of a row or column
            Dictionary<long, int> votes = new Dictionary<long, int>();
            int best = 0; long bestKey = 0;
            foreach (bool isRow in new bool[] { true, false })
                foreach (List<FPad> l in isRow ? rows : cols)
                    for (int i = 1; i < l.Count; i++)
                    {
                        double gap = isRow ? l[i].X - l[i - 1].X : l[i].Y - l[i - 1].Y;
                        if (gap < 1) continue;
                        long key = (long)Math.Round(gap * 2);    // half-mil buckets
                        int v;
                        votes.TryGetValue(key, out v);
                        votes[key] = ++v;
                        if (v > best || (v == best && key < bestKey)) { best = v; bestKey = key; }
                    }
            if (best > 0)
            {
                // representative pair: in the longest line that has this gap
                int bestLen = 0;
                foreach (bool isRow in new bool[] { true, false })
                    foreach (List<FPad> l in isRow ? rows : cols)
                        for (int i = 1; i < l.Count; i++)
                        {
                            double gap = isRow ? l[i].X - l[i - 1].X : l[i].Y - l[i - 1].Y;
                            if ((long)Math.Round(gap * 2) != bestKey || l.Count <= bestLen) continue;
                            bestLen = l.Count;
                            PitchA = l[i - 1]; PitchB = l[i]; PitchHorizontal = isRow;
                            Pitch = gap;
                            break;
                        }
                PitchIsRow = bestLen >= 3;
            }
            // only real pad rows (neighbours at the pitch) count for the row spacing:
            // in a DFN the opposite pads also share an X, but they are not a column of pads
            rows = RealLines(rows, true); cols = RealLines(cols, false);
            // outer rows with the same number of pads: row spacing
            if (rows.Count >= 2)
            {
                List<FPad> a = rows[0], b = rows[rows.Count - 1];
                if (a.Count == b.Count && a.Count >= 2)
                {
                    RowY1 = a[0].Y; RowY2 = b[0].Y; RowSpan = Math.Abs(RowY2 - RowY1);
                    RowX = Math.Min(a[0].X, b[0].X);
                }
            }
            if (cols.Count >= 2)
            {
                List<FPad> a = cols[0], b = cols[cols.Count - 1];
                if (a.Count == b.Count && a.Count >= 2)
                {
                    ColX1 = a[0].X; ColX2 = b[0].X; ColSpan = Math.Abs(ColX2 - ColX1);
                    ColY = Math.Max(a[a.Count - 1].Y, b[b.Count - 1].Y);
                }
            }
        }

        List<List<FPad>> RealLines(List<List<FPad>> lines, bool isRow)
        {
            List<List<FPad>> r = new List<List<FPad>>();
            if (Pitch <= 0) return r;
            foreach (List<FPad> l in lines)
            {
                bool ok = false;
                for (int i = 1; i < l.Count && !ok; i++)
                    ok = Math.Abs((isRow ? l[i].X - l[i - 1].X : l[i].Y - l[i - 1].Y) - Pitch) < 1;
                if (ok) r.Add(l);
            }
            return r;
        }

        void FindGroups(List<FPad> pads)
        {
            List<FPad> sorted = new List<FPad>(pads);
            // pad "1" first so that it represents its group
            sorted.Sort(delegate(FPad a, FPad b)
            {
                int ka = a.Name == "1" ? 0 : 1, kb = b.Name == "1" ? 0 : 1;
                return ka.CompareTo(kb);
            });
            foreach (FPad pad in sorted)
            {
                double w = pad.SX, h = pad.SY;
                double rot = Geo.Norm(pad.Rot);
                bool rot90 = Math.Abs(rot - 90) < 0.5 || Math.Abs(rot - 270) < 0.5;
                bool square = rot < 0.5 || Math.Abs(rot - 180) < 0.5 || rot90;
                if (rot90) { double t = w; w = h; h = t; }
                if (pad.Shape == 'P' && pad.Poly != null && pad.Poly.Count > 2)
                {
                    double x0, y0, x1, y1;
                    Geo.BBox(pad.Poly, out x0, out y0, out x1, out y1);
                    w = x1 - x0; h = y1 - y0; square = true;
                }
                PadGroup g = null;
                foreach (PadGroup e in Groups)
                    if (e.First.Shape == pad.Shape && Math.Abs(e.W - w) < Tol && Math.Abs(e.H - h) < Tol &&
                        Math.Abs(e.First.Hole - pad.Hole) < Tol && e.Rotated == !square) { g = e; break; }
                if (g == null) { g = new PadGroup { First = pad, W = w, H = h, Rotated = !square }; Groups.Add(g); }
                g.Count++;
            }
            Groups.Sort(delegate(PadGroup a, PadGroup b) { return b.Count.CompareTo(a.Count); });
        }

        void MakeDims()
        {
            if (HasPads)
            {
                Dims.Add(new DimLine { A = new PointD(PMinX, PMinY), B = new PointD(PMaxX, PMinY), Horizontal = true, Side = -1, Level = 3, Kind = 0 });
                Dims.Add(new DimLine { A = new PointD(PMaxX, PMinY), B = new PointD(PMaxX, PMaxY), Horizontal = false, Side = 1, Level = 3, Kind = 0 });
            }
            if (HasBody)
            {
                Dims.Add(new DimLine { A = new PointD(BMinX, BMaxY), B = new PointD(BMaxX, BMaxY), Horizontal = true, Side = 1, Level = 3, Kind = 1 });
                Dims.Add(new DimLine { A = new PointD(BMinX, BMinY), B = new PointD(BMinX, BMaxY), Horizontal = false, Side = -1, Level = 3, Kind = 1 });
            }
            if (Pitch > 0 && PitchA != null)
            {
                double cx = (PMinX + PMaxX) / 2, cy = (PMinY + PMaxY) / 2;
                DimLine d = new DimLine { A = new PointD(PitchA.X, PitchA.Y), B = new PointD(PitchB.X, PitchB.Y), Horizontal = PitchHorizontal, Level = 1, Kind = 2 };
                d.Side = PitchHorizontal ? (PitchA.Y >= cy ? 1 : -1) : (PitchA.X >= cx ? 1 : -1);
                d.Prefix = PitchIsRow ? "P " : "";
                Dims.Add(d);
            }
            if (RowSpan > 1)
            {
                double x = RowX;
                Dims.Add(new DimLine { A = new PointD(x, RowY1), B = new PointD(x, RowY2), Horizontal = false, Side = -1, Level = 2, Kind = 2, Prefix = "C " });
            }
            if (ColSpan > 1)
            {
                double y = ColY;
                Dims.Add(new DimLine { A = new PointD(ColX1, y), B = new PointD(ColX2, y), Horizontal = true, Side = 1, Level = 2, Kind = 2, Prefix = "C " });
            }
        }

        // compare with the sizes written in the footprint name (LCSC and KiCad conventions)
        void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            double mm = 1 / 0.0254;
            // LCSC: ..._L5.9-W5.2-P1.27-LS6.2-...
            Dictionary<string, double> v = new Dictionary<string, double>();
            foreach (Match m in Regex.Matches(name, "(?<=^|[-_])(LS|L|W|P|H)(\\d+\\.\\d+)(?=$|[-_])"))
                v[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            // KiCad: ..._3.9x4.9mm_P1.27mm
            Match km = Regex.Match(name, "(?<=^|[-_])(\\d+(?:\\.\\d+)?)x(\\d+(?:\\.\\d+)?)(?:x\\d+(?:\\.\\d+)?)?mm", RegexOptions.IgnoreCase);
            if (km.Success)
            {
                v["W"] = double.Parse(km.Groups[1].Value, CultureInfo.InvariantCulture);
                v["L"] = double.Parse(km.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            Match kp = Regex.Match(name, "(?<=^|[-_])P(\\d+(?:\\.\\d+)?)mm", RegexOptions.IgnoreCase);
            if (kp.Success) v["P"] = double.Parse(kp.Groups[1].Value, CultureInfo.InvariantCulture);
            if (v.Count == 0) return;

            double val;
            if (v.TryGetValue("P", out val))
            {
                if (Pitch > 0)
                {
                    double meas = Pitch / mm, k = Math.Round(meas / val);
                    bool ok = Math.Abs(meas - val) <= Math.Max(0.02, val * 0.02);
                    // two pads of a row with the middle pad missing (SOT-89, trimmers): a multiple of P
                    bool multiple = !ok && !PitchIsRow && k >= 2 && Math.Abs(meas - k * val) <= Math.Max(0.02, val * 0.02);
                    Name.Add(new CheckRow("Шаг P " + Fmt.Mm(val), "измерено " + Fmt.Mm(meas) + (multiple ? " (= " + k + " × P)" : ""), ok || multiple ? 1 : 2));
                }
                else Name.Add(new CheckRow("Шаг P " + Fmt.Mm(val), "ряд площадок не найден", 0));
            }
            double l, w;
            bool hasL = v.TryGetValue("L", out l), hasW = v.TryGetValue("W", out w);
            if ((hasL || hasW) && HasBody)
            {
                double bx = (BMaxX - BMinX) / mm, by = (BMaxY - BMinY) / mm;
                double big = Math.Max(bx, by), small = Math.Min(bx, by);
                if (hasL && hasW)
                {
                    double nb = Math.Max(l, w), ns = Math.Min(l, w);
                    int sev = Math.Max(Severity(big, nb), Severity(small, ns));
                    Name.Add(new CheckRow("Корпус " + Fmt.Mm(l) + " × " + Fmt.Mm(w), "по контуру " + Fmt.Mm(bx) + " × " + Fmt.Mm(by) + (sev == 1 ? " — близко" : sev == 2 ? " — отличается, сверьте с даташитом" : ""), sev == 0 ? 1 : 0));
                }
                else
                {
                    double nv = hasL ? l : w;
                    int sev = Math.Min(Severity(bx, nv), Severity(by, nv));
                    Name.Add(new CheckRow((hasL ? "Длина L " : "Ширина W ") + Fmt.Mm(nv), "по контуру " + Fmt.Mm(bx) + " × " + Fmt.Mm(by) + (sev == 2 ? " — отличается, сверьте с даташитом" : ""), sev == 0 ? 1 : 0));
                }
            }
            if (v.TryGetValue("LS", out val) && HasPads)
            {
                // lead span (tip to tip): the pads usually reach a bit beyond it
                double span = RowSpan > 1 && ColSpan <= 1 ? (PMaxY - PMinY) / mm : ColSpan > 1 && RowSpan <= 1 ? (PMaxX - PMinX) / mm
                              : Math.Max(PMaxX - PMinX, PMaxY - PMinY) / mm;
                int st = span + 0.05 < val ? 2 : 0;
                Name.Add(new CheckRow("Выводы LS " + Fmt.Mm(val), "площадки " + Fmt.Mm(span) + (st == 2 ? " — короче выводов" : ""), st));
            }
        }

        // 0: matches, 1: close (names round the sizes, outlines are drawn loosely), 2: differs
        static int Severity(double a, double b)
        {
            double d = Math.Abs(a - b);
            if (d <= Math.Max(0.15, b * 0.03)) return 0;
            if (d <= Math.Max(0.4, b * 0.08)) return 1;
            return 2;
        }

        // the body outline in the libraries is drawn loosely (courtyard, connector overhang), so a
        // body mismatch is shown as a note; only a pitch mismatch is a warning
    }

    static class Fmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Mm(double mm)
        {
            double r2 = Math.Round(mm, 2);
            string s = Math.Abs(mm - r2) > 0.0006 ? mm.ToString("0.000", Inv) : r2.ToString("0.00", Inv);
            return s.Replace("-", "−");
        }

        public static string Mil(double mil)
        {
            double r = Math.Round(mil, 1);
            return r.ToString("0.#", Inv).Replace("-", "−");
        }

        // value in mil -> text in the chosen unit
        public static string Len(double mil, bool mmUnit) { return mmUnit ? Mm(mil * 0.0254) : Mil(mil); }
    }

    class DimView : ThemedControl
    {
        Part part;
        FpAnalysis an;
        string message = "";
        bool mmUnit = true;
        public bool ShowDims = true, ShowSilk = true, ShowBody = true;

        // view
        double scale = 1, cx, cy;        // px per mil, view centre (mil)
        double ox, oy;                    // screen point of the view centre
        bool fitted;
        // mouse
        Point downAt, lastMouse;
        MouseButtons downBtn = MouseButtons.None;
        bool dragging;
        FPad hoverPad;
        PointD cursor;
        // ruler
        PointD rulerA, rulerB;
        bool rulerOpen;                   // A placed, B follows the mouse
        PointD snap;

        public DimView()
        {
            Cursor = Cursors.Cross;
            TabStop = true;
        }

        public bool Mm
        {
            get { return mmUnit; }
            set { mmUnit = value; Invalidate(); }
        }

        public Part Part { get { return part; } }

        public void SetPart(Part p)
        {
            part = p;
            message = "";
            an = p != null && p.Pads.Count > 0 ? FpAnalysis.Analyze(p) : null;
            fitted = false;
            hoverPad = null;
            ClearRuler();
            Invalidate();
        }

        public void SetMessage(string m)
        {
            part = null; an = null; message = m ?? "";
            ClearRuler();
            Invalidate();
        }

        public void Fit() { fitted = false; Invalidate(); }

        void ClearRuler() { rulerA = rulerB = null; rulerOpen = false; Invalidate(); }

        // ---------------------------------------------------------------- geometry

        bool WidePanel { get { return Width >= Ui.D(600); } }
        int PanelW { get { return WidePanel ? Ui.D(250) : 0; } }
        Rectangle Canvas { get { return new Rectangle(0, 0, Math.Max(10, Width - PanelW), Height); } }

        void DoFit()
        {
            if (part == null) return;
            List<PointD> pts = new List<PointD>();
            if (an != null && an.HasPads) { pts.Add(new PointD(an.PMinX, an.PMinY)); pts.Add(new PointD(an.PMaxX, an.PMaxY)); }
            if (an != null && an.HasBody) { pts.Add(new PointD(an.BMinX, an.BMinY)); pts.Add(new PointD(an.BMaxX, an.BMaxY)); }
            foreach (FGfx g in part.Gfx)
            {
                if (g.Layer != "TO") continue;
                if (g.Kind == "ARC") { pts.Add(new PointD(g.CX - g.R, g.CY - g.R)); pts.Add(new PointD(g.CX + g.R, g.CY + g.R)); }
                else pts.AddRange(g.Pts);
            }
            if (pts.Count == 0) return;
            double x0, y0, x1, y1;
            Geo.BBox(pts, out x0, out y0, out x1, out y1);
            Rectangle c = Canvas;
            double mt = Ui.Df(20), mb = Ui.Df(20), mr = Ui.Df(20), ml = Ui.Df(20);
            if (ShowDims && an != null)
            {
                mt = SideMargin(0) + (WidePanel ? 0 : Ui.Df(14));   // the compact summary line
                mb = SideMargin(1); mr = SideMargin(2); ml = SideMargin(3);
            }
            mb += Ui.Df(16);                                         // cursor coordinates
            double bw = Math.Max(x1 - x0, 1), bh = Math.Max(y1 - y0, 1);
            scale = Math.Max(1e-4, Math.Min((c.Width - ml - mr) / bw, (c.Height - mt - mb) / bh));
            cx = (x0 + x1) / 2; cy = (y0 + y1) / 2;
            ox = (ml + c.Width - mr) / 2; oy = (mt + c.Height - mb) / 2;
            fitted = true;
        }

        // distance between stacked dimension lines, smaller in a small view
        float Step
        {
            get
            {
                Rectangle c = Canvas;
                return Math.Max(Ui.Df(14), Math.Min(Ui.Df(24), Math.Min(c.Width, c.Height) / 14f));
            }
        }

        PointF S(PointD p) { return S(p.X, p.Y); }
        PointF S(double x, double y)
        {
            return new PointF((float)(ox + (x - cx) * scale), (float)(oy - (y - cy) * scale));
        }
        PointD W(Point p)
        {
            return new PointD(cx + (p.X - ox) / scale, cy - (p.Y - oy) / scale);
        }

        // ---- placement of the dimension lines: per side (0 top, 1 bottom, 2 right, 3 left)

        static int SideOf(DimLine d) { return d.Horizontal ? (d.Side > 0 ? 0 : 1) : (d.Side > 0 ? 2 : 3); }

        float Near { get { return Math.Min(Ui.Df(12), Step * 0.6f); } }

        // distance of a level 2/3 dimension line from the footprint extent
        float Offset(DimLine d)
        {
            int side = SideOf(d);
            bool hasL1 = false;
            List<int> levels = new List<int>();
            foreach (DimLine e in an.Dims)
            {
                if (SideOf(e) != side) continue;
                if (e.Level == 1) hasL1 = true;
                else if (!levels.Contains(e.Level)) levels.Add(e.Level);
            }
            levels.Sort();
            int rank = levels.IndexOf(d.Level);
            return (hasL1 ? Near + Ui.Df(16) : Ui.Df(16)) + Step * Math.Max(0, rank);
        }

        float SideMargin(int side)
        {
            float m = 0;
            bool any = false;
            foreach (DimLine d in an.Dims)
            {
                if (SideOf(d) != side) continue;
                any = true;
                m = Math.Max(m, d.Level == 1 ? Near + Ui.Df(4) : Offset(d));
            }
            return any ? m + Ui.Df(14) : Ui.Df(16);
        }

        // ---------------------------------------------------------------- mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            downAt = lastMouse = e.Location;
            downBtn = e.Button;
            dragging = false;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (part == null) { base.OnMouseMove(e); return; }
            if (downBtn != MouseButtons.None && !dragging && (Math.Abs(e.X - downAt.X) > 3 || Math.Abs(e.Y - downAt.Y) > 3)) dragging = true;
            if (dragging)
            {
                cx -= (e.X - lastMouse.X) / scale;
                cy += (e.Y - lastMouse.Y) / scale;
                Cursor = Cursors.SizeAll;
            }
            lastMouse = e.Location;
            cursor = W(e.Location);
            snap = Snap(e.Location);
            FPad h = e.X < Canvas.Width ? PadAt(cursor) : null;
            if (h != hoverPad) hoverPad = h;
            if (rulerOpen) rulerB = snap ?? cursor;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (part != null && !dragging && e.X < Canvas.Width)
            {
                if (e.Button == MouseButtons.Left)
                {
                    PointD p = Snap(e.Location) ?? W(e.Location);
                    if (!rulerOpen) { rulerA = p; rulerB = p; rulerOpen = true; }
                    else { rulerB = p; rulerOpen = false; }
                }
                else if (e.Button == MouseButtons.Right) ClearRuler();
            }
            downBtn = MouseButtons.None;
            dragging = false;
            Cursor = Cursors.Cross;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverPad = null; cursor = null; snap = null;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ClearRuler(); Fit(); }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (part == null || !fitted) return;
            PointD before = W(e.Location);
            double k = e.Delta > 0 ? 1.25 : 1 / 1.25;
            scale = Math.Max(1e-4, Math.Min(400, scale * k));
            PointD after = W(e.Location);
            cx += before.X - after.X; cy += before.Y - after.Y;
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Escape || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) ClearRuler();
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e) { fitted = false; base.OnResize(e); }

        FPad PadAt(PointD w)
        {
            if (part == null || w == null) return null;
            FPad best = null; double bestArea = double.MaxValue;
            foreach (FPad pad in part.Pads)
            {
                if (!Inside(pad, w)) continue;
                double a = pad.SX * pad.SY;
                if (a < bestArea) { bestArea = a; best = pad; }
            }
            return best;
        }

        static bool Inside(FPad pad, PointD w)
        {
            if (pad.Shape == 'P' && pad.Poly != null && pad.Poly.Count > 2)
            {
                bool inside = false;
                List<PointD> l = pad.Poly;
                for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                    if ((l[i].Y > w.Y) != (l[j].Y > w.Y) && w.X < (l[j].X - l[i].X) * (w.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) inside = !inside;
                return inside;
            }
            double a = -pad.Rot * Math.PI / 180;
            double dx = w.X - pad.X, dy = w.Y - pad.Y;
            double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
            double hx = pad.SX / 2, hy = pad.SY / 2;
            if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01) hx = hy = pad.Hole / 2;
            if (pad.Shape == 'C') { double r = Math.Max(hx, hy); return lx * lx + ly * ly <= r * r; }
            return Math.Abs(lx) <= hx && Math.Abs(ly) <= hy;
        }

        // pad centres, edge midpoints and corners
        IEnumerable<PointD> SnapPoints()
        {
            foreach (FPad pad in part.Pads)
            {
                yield return new PointD(pad.X, pad.Y);
                double hx = pad.SX / 2, hy = pad.SY / 2;
                if (pad.Shape == 'P' && pad.Poly != null) { foreach (PointD q in pad.Poly) yield return q; continue; }
                if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01) hx = hy = pad.Hole / 2;
                double a = pad.Rot * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
                double[,] loc = pad.Shape == 'C'
                    ? new double[,] { { hx, 0 }, { -hx, 0 }, { 0, hx }, { 0, -hx } }
                    : new double[,] { { hx, 0 }, { -hx, 0 }, { 0, hy }, { 0, -hy }, { hx, hy }, { -hx, hy }, { hx, -hy }, { -hx, -hy } };
                for (int i = 0; i < loc.GetLength(0); i++)
                    yield return new PointD(pad.X + loc[i, 0] * c - loc[i, 1] * s, pad.Y + loc[i, 0] * s + loc[i, 1] * c);
            }
            yield return new PointD(0, 0);
        }

        PointD Snap(Point m)
        {
            if (part == null || !fitted) return null;
            PointD best = null; double bd = Ui.D(9);
            foreach (PointD q in SnapPoints())
            {
                PointF s = S(q);
                double d = Math.Sqrt((s.X - m.X) * (s.X - m.X) + (s.Y - m.Y) * (s.Y - m.Y));
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }

        // ---------------------------------------------------------------- paint

        static readonly Color DimPads = Color.FromArgb(0x38, 0xBD, 0xF8);    // cyan
        static readonly Color DimBody = Color.FromArgb(0xA7, 0x8B, 0xFA);    // violet, drawn dashed
        static readonly Color DimPitch = Color.FromArgb(0xFB, 0xBF, 0x24);   // amber
        static readonly Color RulerC = Color.FromArgb(0xF4, 0x72, 0xB6);
        static readonly Color OkC = Color.FromArgb(0x4A, 0xDE, 0x80);
        static readonly Color WarnC = Color.FromArgb(0xFB, 0xBF, 0x24);

        protected override void OnPaint(PaintEventArgs e) { RenderTo(e.Graphics); }

        // test hook: pretend the mouse is at a screen point (hover + snap)
        public void SimulateMouse(Point m)
        {
            if (!fitted) DoFit();
            cursor = W(m); snap = Snap(m); hoverPad = PadAt(cursor);
        }

        public void SimulateRuler(PointD a, PointD b) { rulerA = a; rulerB = b; rulerOpen = false; }

        public FpAnalysis Analysis { get { return an; } }

        public void RenderTo(Graphics g)
        {
            Theme t = Theme.Current;
            PreviewPalette pal = t.Preview;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF all = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath clip = Ui.Round(all, Ui.Df(8)))
            {
                using (SolidBrush b = new SolidBrush(pal.FpBg)) g.FillPath(b, clip);
                Region old = g.Clip;
                g.SetClip(clip);
                if (part != null && an != null)
                {
                    if (!fitted) DoFit();
                    if (fitted)
                    {
                        g.SetClip(Canvas, CombineMode.Intersect);
                        DrawFootprint(g, pal);
                        if (ShowDims) DrawDims(g);
                        DrawRuler(g);
                        DrawHover(g);
                        DrawCursor(g);
                        g.Clip = old;
                        g.SetClip(clip);
                    }
                    if (WidePanel) DrawPanel(g, t);
                    else DrawCompactSummary(g);
                }
                else
                {
                    using (Font f = Ui.Font(9.5f))
                        Ui.Text(g, message != "" ? message : "Выберите деталь в списке", f, Color.FromArgb(150, 170, 175, 182), ClientRectangle,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
                g.Clip = old;
                using (Pen p = new Pen(t.Border)) g.DrawPath(p, clip);
            }
        }

        void DrawFootprint(Graphics g, PreviewPalette pal)
        {
            float k = (float)scale;
            // body outline
            if (ShowBody)
                using (Pen bp = new Pen(Color.FromArgb(150, 160, 170, 190), 1f))
                {
                    bp.DashStyle = DashStyle.Dash;
                    foreach (FGfx gf in part.Gfx)
                    {
                        if (gf.Layer != "ASM" && gf.Layer != "CRT") continue;
                        DrawGfx(g, gf, bp, null);
                    }
                }
            if (ShowSilk)
                using (Brush sb = new SolidBrush(pal.FpSilk))
                    foreach (FGfx gf in part.Gfx)
                    {
                        if (gf.Layer != "TO") continue;
                        using (Pen pen = new Pen(pal.FpSilk, (float)Math.Max(1.0, gf.W * scale)))
                        {
                            pen.StartCap = pen.EndCap = LineCap.Round;
                            DrawGfx(g, gf, pen, sb);
                        }
                    }
            using (Brush top = new SolidBrush(pal.FpTop))
            using (Brush bottom = new SolidBrush(pal.FpBottom))
            using (Brush multi = new SolidBrush(pal.FpMulti))
            using (Brush hole = new SolidBrush(pal.FpHole))
                foreach (FPad pad in part.Pads)
                {
                    Brush b = pad.Layer == "M" ? multi : pad.Layer == "B" ? bottom : top;
                    using (GraphicsPath gp = PadPath(pad))
                    {
                        if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01)
                        {
                            using (Pen hp = new Pen(pal.FpNpth, 1.2f)) g.DrawPath(hp, gp);
                            continue;
                        }
                        g.FillPath(b, gp);
                    }
                    if (pad.Hole > 0)
                    {
                        PointF c = S(pad.X, pad.Y);
                        float hr = (float)(pad.Hole * scale);
                        GraphicsState st = g.Save();
                        g.TranslateTransform(c.X, c.Y);
                        if (pad.SlotLen > 0)
                        {
                            g.RotateTransform((float)(-pad.SlotRot));
                            using (GraphicsPath gp = Obround((float)(pad.SlotLen * scale), hr)) g.FillPath(hole, gp);
                        }
                        else g.FillEllipse(hole, -hr / 2, -hr / 2, hr, hr);
                        g.Restore(st);
                    }
                }
            // pad numbers
            foreach (FPad pad in part.Pads)
            {
                if (pad.Name == "") continue;
                double x0, y0, x1, y1;
                FpAnalysis.PadBox(pad, out x0, out y0, out x1, out y1);
                float w = (float)((x1 - x0) * scale), h = (float)((y1 - y0) * scale);
                float px = Math.Min(Math.Min(w, h) * 0.55f, Ui.Df(13));
                if (px < Ui.Df(6.5f)) continue;
                PointF c = S(pad.X, pad.Y);
                using (Font f = new Font(Ui.UiFamily, px, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    Size sz = Ui.Measure(g, pad.Name, f);
                    if (sz.Width > w * 0.95f) continue;
                    Ui.Text(g, pad.Name, f, Color.FromArgb(230, 255, 255, 255), new Rectangle((int)(c.X - sz.Width / 2f) - 1, (int)(c.Y - sz.Height / 2f), sz.Width + 2, sz.Height),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
            }
            // origin
            PointF o = S(0, 0);
            using (Pen op = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
            {
                float r = Ui.Df(5);
                g.DrawLine(op, o.X - r, o.Y, o.X + r, o.Y);
                g.DrawLine(op, o.X, o.Y - r, o.X, o.Y + r);
            }
        }

        void DrawGfx(Graphics g, FGfx gf, Pen pen, Brush fill)
        {
            if (gf.Kind == "TRK" && gf.Pts.Count >= 2) g.DrawLine(pen, S(gf.Pts[0]), S(gf.Pts[1]));
            else if (gf.Kind == "ARC")
            {
                PointF c = S(gf.CX, gf.CY);
                float r = (float)(gf.R * scale);
                double sweep = Geo.Norm(gf.A2 - gf.A1); if (sweep == 0) sweep = 360;
                if (r > 0.5f) g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, (float)(-gf.A1), (float)(-sweep));
            }
            else if (gf.Kind == "REG" && gf.Pts.Count >= 3)
            {
                PointF[] pts = new PointF[gf.Pts.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = S(gf.Pts[i]);
                if (fill != null) g.FillPolygon(fill, pts); else g.DrawPolygon(pen, pts);
            }
        }

        GraphicsPath PadPath(FPad pad)
        {
            GraphicsPath gp;
            if (pad.Shape == 'P' && pad.Poly != null && pad.Poly.Count > 2)
            {
                gp = new GraphicsPath();
                PointF[] pts = new PointF[pad.Poly.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = S(pad.Poly[i]);
                gp.AddPolygon(pts);
                return gp;
            }
            float sx = (float)(pad.SX * scale), sy = (float)(pad.SY * scale);
            if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01) sx = sy = (float)(pad.Hole * scale);
            if (pad.Shape == 'C') { gp = new GraphicsPath(); float d = Math.Max(sx, sy); gp.AddEllipse(-d / 2, -d / 2, d, d); }
            else if (pad.Shape == 'R') { gp = new GraphicsPath(); gp.AddRectangle(new RectangleF(-sx / 2, -sy / 2, sx, sy)); }
            else gp = Obround(sx, sy);
            PointF c = S(pad.X, pad.Y);
            using (Matrix m = new Matrix())
            {
                m.Translate(c.X, c.Y);
                m.Rotate((float)(-pad.Rot));
                gp.Transform(m);
            }
            return gp;
        }

        static GraphicsPath Obround(float sx, float sy)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = Math.Min(sx, sy);
            if (d <= 0) { gp.AddRectangle(new RectangleF(0, 0, 1, 1)); return gp; }
            if (sx >= sy)
            {
                gp.AddArc(-sx / 2, -sy / 2, d, d, 90, 180);
                gp.AddArc(sx / 2 - d, -sy / 2, d, d, 270, 180);
            }
            else
            {
                gp.AddArc(-sx / 2, -sy / 2, d, d, 180, 180);
                gp.AddArc(-sx / 2, sy / 2 - d, d, d, 0, 180);
            }
            gp.CloseFigure();
            return gp;
        }

        // screen box of everything drawn, used to place the dimension bands
        RectangleF Extent()
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            if (an.HasPads) { x0 = an.PMinX; y0 = an.PMinY; x1 = an.PMaxX; y1 = an.PMaxY; }
            if (an.HasBody) { x0 = Math.Min(x0, an.BMinX); y0 = Math.Min(y0, an.BMinY); x1 = Math.Max(x1, an.BMaxX); y1 = Math.Max(y1, an.BMaxY); }
            PointF a = S(x0, y1), b = S(x1, y0);
            return RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
        }

        void DrawDims(Graphics g)
        {
            RectangleF ext = Extent();
            float near = Near;
            foreach (DimLine d in an.Dims)
            {
                Color c = d.Kind == 0 ? DimPads : d.Kind == 1 ? DimBody : DimPitch;
                bool dash = d.Kind == 1;            // body dimensions dashed, to match the body outline
                PointF a = S(d.A), b = S(d.B);
                string label = d.Prefix + Fmt.Len(d.Value, mmUnit);
                if (d.Horizontal)
                {
                    float y;
                    if (d.Level == 1)
                    {
                        // just outside the pads of the row
                        double x0, y0, x1, y1;
                        FpAnalysis.PadBox(an.PitchA, out x0, out y0, out x1, out y1);
                        y = d.Side > 0 ? S(0, y1).Y - near : S(0, y0).Y + near;
                    }
                    else y = d.Side > 0 ? ext.Top - Offset(d) : ext.Bottom + Offset(d);
                    DimH(g, c, a, b, y, label, dash);
                }
                else
                {
                    float x;
                    if (d.Level == 1)
                    {
                        double x0, y0, x1, y1;
                        FpAnalysis.PadBox(an.PitchA, out x0, out y0, out x1, out y1);
                        x = d.Side > 0 ? S(x1, 0).X + near : S(x0, 0).X - near;
                    }
                    else x = d.Side > 0 ? ext.Right + Offset(d) : ext.Left - Offset(d);
                    DimV(g, c, a, b, x, label, dash);
                }
            }
            DrawLegend(g);
            // pad sizes: one label per group
            int shown = 0;
            foreach (PadGroup pg in an.Groups)
            {
                if (shown >= 4) break;
                shown++;
                string s = Fmt.Len(pg.W, mmUnit) + "×" + Fmt.Len(pg.H, mmUnit);
                if (pg.First.Hole > 0) s += " Ø" + Fmt.Len(pg.First.Hole, mmUnit);
                double x0, y0, x1, y1;
                FpAnalysis.PadBox(pg.First, out x0, out y0, out x1, out y1);
                PointF a = S(x0, y1), b = S(x1, y0);
                RectangleF pr = RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
                using (Font f = Ui.Font(7.5f))
                {
                    Size sz = Ui.Measure(g, s, f);
                    float w = sz.Width + Ui.Df(8), h = sz.Height + Ui.Df(3);
                    PointF pc = new PointF(pr.X + pr.Width / 2, pr.Y + pr.Height / 2);
                    RectangleF lr;
                    if (pr.Width > w + 4 && pr.Height > h * 3f) lr = new RectangleF(pc.X - w / 2, pc.Y + Ui.Df(9), w, h);
                    else
                    {
                        // outside, towards the centre of the footprint (the outer side holds the dimension lines)
                        PointF fc = new PointF(ext.X + ext.Width / 2, ext.Y + ext.Height / 2);
                        float dx = fc.X - pc.X, dy = fc.Y - pc.Y;
                        float len = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1) { dx = 0; dy = -1; len = 1; }
                        dx /= len; dy /= len;
                        float off = Math.Max(pr.Width, pr.Height) / 2 + Ui.Df(16);
                        PointF lc = new PointF(pc.X + dx * off, pc.Y + dy * off);
                        lr = new RectangleF(lc.X - w / 2, lc.Y - h / 2, w, h);
                        using (Pen lp = new Pen(Color.FromArgb(170, 255, 255, 255), 1f)) g.DrawLine(lp, pc, lc);
                    }
                    Pill(g, lr, Color.FromArgb(215, 20, 22, 26), Color.FromArgb(120, 255, 255, 255));
                    Ui.Text(g, s, f, Color.White, Rectangle.Round(lr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
            }
        }

        static void Pill(Graphics g, RectangleF r, Color fill, Color border)
        {
            Ui.FillRound(g, fill, r, r.Height / 2);
            Ui.DrawRound(g, border, r, r.Height / 2, 1f);
        }

        void Label(Graphics g, Color c, PointF at, string s) { Label(g, c, at, s, false); }

        void Label(Graphics g, Color c, PointF at, string s, bool vertical)
        {
            using (Font f = Ui.Font(8f, FontStyle.Bold))
            {
                if (vertical)
                {
                    // rotated along the dimension line, so that stacked dimensions do not overlap
                    GraphicsState st = g.Save();
                    g.TranslateTransform(at.X, at.Y);
                    g.RotateTransform(-90);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    SizeF z = g.MeasureString(s, f);
                    RectangleF rr = new RectangleF(-z.Width / 2 - Ui.Df(2), -z.Height / 2, z.Width + Ui.Df(4), z.Height);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(230, Theme.Current.Preview.FpBg))) g.FillRectangle(b, rr);
                    using (SolidBrush tb = new SolidBrush(c)) g.DrawString(s, f, tb, -z.Width / 2, -z.Height / 2 + Ui.Df(0.5f));
                    g.Restore(st);
                    return;
                }
                Size sz = Ui.Measure(g, s, f);
                RectangleF r = new RectangleF(at.X - sz.Width / 2f - Ui.Df(4), at.Y - sz.Height / 2f - Ui.Df(1), sz.Width + Ui.Df(8), sz.Height + Ui.Df(2));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, Theme.Current.Preview.FpBg))) g.FillRectangle(b, r);
                Ui.Text(g, s, f, c, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        void DimH(Graphics g, Color c, PointF a, PointF b, float y, string label, bool dash)
        {
            if (a.X > b.X) { PointF t = a; a = b; b = t; }
            using (Pen ext = new Pen(Color.FromArgb(110, c), 1f))
            using (Pen p = new Pen(c, dash ? 1.4f : 1.2f))
            {
                ext.DashStyle = DashStyle.Dot;
                if (dash) { p.DashStyle = DashStyle.Dash; p.DashPattern = new float[] { 4, 2.5f }; }
                g.DrawLine(ext, a.X, a.Y, a.X, y + (y > a.Y ? 4 : -4));
                g.DrawLine(ext, b.X, b.Y, b.X, y + (y > b.Y ? 4 : -4));
                g.DrawLine(p, a.X, y, b.X, y);
                Arrow(g, c, new PointF(a.X, y), 1, 0);
                Arrow(g, c, new PointF(b.X, y), -1, 0);
            }
            Label(g, c, new PointF((a.X + b.X) / 2, y), label);
        }

        void DimV(Graphics g, Color c, PointF a, PointF b, float x, string label, bool dash)
        {
            if (a.Y > b.Y) { PointF t = a; a = b; b = t; }
            using (Pen ext = new Pen(Color.FromArgb(110, c), 1f))
            using (Pen p = new Pen(c, dash ? 1.4f : 1.2f))
            {
                ext.DashStyle = DashStyle.Dot;
                if (dash) { p.DashStyle = DashStyle.Dash; p.DashPattern = new float[] { 4, 2.5f }; }
                g.DrawLine(ext, a.X, a.Y, x + (x > a.X ? 4 : -4), a.Y);
                g.DrawLine(ext, b.X, b.Y, x + (x > b.X ? 4 : -4), b.Y);
                g.DrawLine(p, x, a.Y, x, b.Y);
                Arrow(g, c, new PointF(x, a.Y), 0, 1);
                Arrow(g, c, new PointF(x, b.Y), 0, -1);
            }
            Label(g, c, new PointF(x, (a.Y + b.Y) / 2), label, true);
        }

        // small key in the corner: only the kinds actually drawn
        void DrawLegend(Graphics g)
        {
            List<KeyValuePair<string, Color>> items = new List<KeyValuePair<string, Color>>();
            bool pads = false, body = false, pitch = false;
            foreach (DimLine d in an.Dims) { if (d.Kind == 0) pads = true; else if (d.Kind == 1) body = true; else pitch = true; }
            if (pads) items.Add(new KeyValuePair<string, Color>("площадки", DimPads));
            if (pitch) items.Add(new KeyValuePair<string, Color>(an.PitchIsRow ? "шаг" : "между центрами", DimPitch));
            if (body) items.Add(new KeyValuePair<string, Color>("контур корпуса", DimBody));
            if (items.Count < 2) return;      // one kind is self-evident
            using (Font f = Ui.Font(8f, FontStyle.Bold))
            {
                int lh = Ui.Measure(g, "Ag", f).Height, sw = Ui.D(16), gap = Ui.D(6), pad = Ui.D(8);
                int w = 0;
                foreach (KeyValuePair<string, Color> it in items) w = Math.Max(w, sw + Ui.D(5) + Ui.Measure(g, it.Key, f).Width);
                int h = items.Count * lh + (items.Count - 1) * gap + 2 * pad;
                Rectangle c = Canvas;
                RectangleF box = new RectangleF(c.Left + Ui.D(10), c.Bottom - h - Ui.D(28), w + 2 * pad, h);
                Ui.FillRound(g, Color.FromArgb(220, 24, 26, 30), box, Ui.Df(7));
                Ui.DrawRound(g, Color.FromArgb(60, 255, 255, 255), box, Ui.Df(7), 1f);
                float y = box.Y + pad;
                foreach (KeyValuePair<string, Color> it in items)
                {
                    float lineY = y + lh / 2f;
                    using (Pen pen = new Pen(it.Value, 2f))
                    {
                        if (it.Key == "контур корпуса") { pen.DashStyle = DashStyle.Dash; pen.DashPattern = new float[] { 3, 2 }; }
                        g.DrawLine(pen, box.X + pad, lineY, box.X + pad + sw, lineY);
                    }
                    Ui.Text(g, it.Key, f, Color.FromArgb(230, 235, 238, 242), new Rectangle((int)(box.X + pad + sw + Ui.D(5)), (int)y, w, lh + 2),
                            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    y += lh + gap;
                }
            }
        }

        // arrow head at p pointing against (dx, dy)
        static void Arrow(Graphics g, Color c, PointF p, float dx, float dy)
        {
            float l = Ui.Df(6), w = Ui.Df(2.6f);
            PointF[] pts = {
                p,
                new PointF(p.X + dx * l - dy * w, p.Y + dy * l + dx * w),
                new PointF(p.X + dx * l + dy * w, p.Y + dy * l - dx * w)
            };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        void DrawRuler(Graphics g)
        {
            if (snap != null)
            {
                PointF s = S(snap);
                using (Pen p = new Pen(RulerC, 1.4f)) g.DrawEllipse(p, s.X - 5, s.Y - 5, 10, 10);
            }
            if (rulerA == null || rulerB == null) return;
            PointF a = S(rulerA), b = S(rulerB);
            using (Pen p = new Pen(RulerC, 1.6f))
            {
                g.DrawLine(p, a, b);
                g.FillEllipse(Brushes.White, a.X - 2.5f, a.Y - 2.5f, 5, 5);
                g.FillEllipse(Brushes.White, b.X - 2.5f, b.Y - 2.5f, 5, 5);
            }
            double dx = rulerB.X - rulerA.X, dy = rulerB.Y - rulerA.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-6) return;
            string s1 = Fmt.Len(d, mmUnit) + (mmUnit ? " мм" : " mil");
            string s2 = "Δx " + Fmt.Len(Math.Abs(dx), mmUnit) + "   Δy " + Fmt.Len(Math.Abs(dy), mmUnit);
            using (Font f1 = Ui.Font(9f, FontStyle.Bold))
            using (Font f2 = Ui.Font(7.5f))
            {
                Size z1 = Ui.Measure(g, s1, f1), z2 = Ui.Measure(g, s2, f2);
                float w = Math.Max(z1.Width, z2.Width) + Ui.Df(14), h = z1.Height + z2.Height + Ui.Df(8);
                PointF m = new PointF((a.X + b.X) / 2 + Ui.Df(10), (a.Y + b.Y) / 2 - h - Ui.Df(6));
                RectangleF r = new RectangleF(m.X, m.Y, w, h);
                Ui.FillRound(g, Color.FromArgb(235, 30, 20, 30), r, Ui.Df(6));
                Ui.DrawRound(g, RulerC, r, Ui.Df(6), 1f);
                Ui.Text(g, s1, f1, Color.White, new Rectangle((int)r.X + Ui.D(7), (int)r.Y + Ui.D(4), (int)w, z1.Height), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                Ui.Text(g, s2, f2, Color.FromArgb(220, 230, 230, 235), new Rectangle((int)r.X + Ui.D(7), (int)r.Y + Ui.D(4) + z1.Height, (int)w, z2.Height + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }

        void DrawHover(Graphics g)
        {
            if (hoverPad == null || dragging) return;
            FPad pad = hoverPad;
            using (GraphicsPath gp = PadPath(pad))
            using (Pen p = new Pen(Color.White, 1.6f)) g.DrawPath(p, gp);
            List<string> lines = new List<string>();
            string u = mmUnit ? " мм" : " mil";
            string shape = pad.Shape == 'C' ? "круглая" : pad.Shape == 'R' ? "прямоугольная" : pad.Shape == 'P' ? "полигон" : "овальная";
            double w = pad.SX, h = pad.SY;
            if (pad.Shape == 'P' && pad.Poly != null && pad.Poly.Count > 2)
            {
                double x0, y0, x1, y1;
                Geo.BBox(pad.Poly, out x0, out y0, out x1, out y1);
                w = x1 - x0; h = y1 - y0;
            }
            else
            {
                double rot = Geo.Norm(pad.Rot);
                if (Math.Abs(rot - 90) < 0.5 || Math.Abs(rot - 270) < 0.5) { double t = w; w = h; h = t; }
            }
            lines.Add(Fmt.Len(w, mmUnit) + " × " + Fmt.Len(h, mmUnit) + u + ", " + shape);
            if (pad.Hole > 0)
                lines.Add((pad.Plated ? "Отверстие Ø" : "Неметаллизированное Ø") + Fmt.Len(pad.Hole, mmUnit) +
                          (pad.SlotLen > 0 ? " × " + Fmt.Len(pad.SlotLen, mmUnit) : "") + u);
            double rr = Geo.Norm(pad.Rot);
            if (rr > 0.05 && Math.Abs(rr - 90) > 0.05 && Math.Abs(rr - 180) > 0.05 && Math.Abs(rr - 270) > 0.05)
                lines.Add("Поворот " + rr.ToString("0.#", CultureInfo.InvariantCulture) + "°");
            lines.Add("Центр  X " + Fmt.Len(pad.X, mmUnit) + "   Y " + Fmt.Len(pad.Y, mmUnit));
            lines.Add(pad.Layer == "M" || pad.Hole > 0 ? "Сквозная" : pad.Layer == "B" ? "Нижний слой" : "Верхний слой");
            string title = pad.Name != "" ? "Площадка " + pad.Name : "Площадка без номера";
            using (Font tf = Ui.Font(9f, FontStyle.Bold))
            using (Font f = Ui.Font(8.5f))
            {
                int lh = Ui.Measure(g, "Ag", f).Height + Ui.D(2);
                int w0 = Ui.Measure(g, title, tf).Width;
                foreach (string l in lines) w0 = Math.Max(w0, Ui.Measure(g, l, f).Width);
                int bw = w0 + Ui.D(20), bh = Ui.Measure(g, title, tf).Height + lh * lines.Count + Ui.D(14);
                PointF at = cursor != null ? S(cursor) : S(pad.X, pad.Y);
                float x = at.X + Ui.Df(16), y = at.Y + Ui.Df(16);
                Rectangle c = Canvas;
                if (x + bw > c.Right - 4) x = at.X - bw - Ui.Df(10);
                if (y + bh > c.Bottom - 4) y = at.Y - bh - Ui.Df(10);
                RectangleF r = new RectangleF(x, y, bw, bh);
                Ui.FillRound(g, Color.FromArgb(240, 32, 34, 39), r, Ui.Df(7));
                Ui.DrawRound(g, Color.FromArgb(90, 255, 255, 255), r, Ui.Df(7), 1f);
                int ty = (int)y + Ui.D(7);
                Ui.Text(g, title, tf, Color.White, new Rectangle((int)x + Ui.D(10), ty, bw, lh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                ty += Ui.Measure(g, title, tf).Height + Ui.D(3);
                foreach (string l in lines)
                {
                    Ui.Text(g, l, f, Color.FromArgb(225, 225, 228, 235), new Rectangle((int)x + Ui.D(10), ty, bw, lh), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    ty += lh;
                }
            }
        }

        void DrawCursor(Graphics g)
        {
            if (cursor == null) return;
            PointD p = snap ?? cursor;
            string s = "X " + Fmt.Len(p.X, mmUnit) + "   Y " + Fmt.Len(p.Y, mmUnit) + (mmUnit ? "  мм" : "  mil");
            using (Font f = Ui.Font(8f))
                Ui.Text(g, s, f, Color.FromArgb(180, 200, 205, 212), new Rectangle(Ui.D(10), Height - Ui.D(24), Canvas.Width - Ui.D(20), Ui.D(18)),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        List<CheckRow> SummaryRows()
        {
            List<CheckRow> rows = new List<CheckRow>();
            string padsText = an.Smd + an.Th + " шт." + (an.Smd > 0 && an.Th > 0 ? " (SMD " + an.Smd + ", TH " + an.Th + ")" : an.Th > 0 ? ", сквозные" : ", SMD") +
                              (an.Npth > 0 ? " + " + an.Npth + " отв." : "");
            rows.Add(new CheckRow("Площадки", padsText, 0));
            if (an.Pitch > 0) rows.Add(new CheckRow(an.PitchIsRow ? "Шаг P" : "Между центрами", Fmt.Len(an.Pitch, mmUnit), 0));
            if (an.RowSpan > 1) rows.Add(new CheckRow("Между рядами C", Fmt.Len(an.RowSpan, mmUnit), 0));
            if (an.ColSpan > 1) rows.Add(new CheckRow("Между столбцами C", Fmt.Len(an.ColSpan, mmUnit), 0));
            if (an.HasPads) rows.Add(new CheckRow("Габарит площадок", Fmt.Len(an.PMaxX - an.PMinX, mmUnit) + " × " + Fmt.Len(an.PMaxY - an.PMinY, mmUnit), 0));
            if (an.HasBody) rows.Add(new CheckRow("Корпус (" + an.BodyLabel + ")", Fmt.Len(an.BMaxX - an.BMinX, mmUnit) + " × " + Fmt.Len(an.BMaxY - an.BMinY, mmUnit), 0));
            return rows;
        }

        void DrawCompactSummary(Graphics g)
        {
            List<string> parts = new List<string>();
            if (an.Pitch > 0) parts.Add((an.PitchIsRow ? "P " : "") + Fmt.Len(an.Pitch, mmUnit));
            if (an.HasPads) parts.Add("площадки " + Fmt.Len(an.PMaxX - an.PMinX, mmUnit) + "×" + Fmt.Len(an.PMaxY - an.PMinY, mmUnit));
            int warn = 0;
            foreach (CheckRow r in an.Name) if (r.State == 2) warn++;
            string s = string.Join("  ·  ", parts.ToArray()) + (mmUnit ? "  мм" : "  mil");
            using (Font f = Ui.Font(8f, FontStyle.Bold))
            {
                Ui.Text(g, s, f, Color.FromArgb(200, 210, 215, 222), new Rectangle(Ui.D(10), Ui.D(6), Width - Ui.D(20), Ui.D(18)),
                        TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                if (warn > 0)
                    Ui.Text(g, "⚠ размеры не совпадают с названием", f, WarnC, new Rectangle(Ui.D(10), Ui.D(24), Width - Ui.D(20), Ui.D(18)),
                            TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        void DrawPanel(Graphics g, Theme t)
        {
            int pw = PanelW, x0 = Width - pw;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, Ui.Mix(t.Preview.FpBg, Color.White, 0.05f)))) g.FillRectangle(b, x0, 0, pw, Height);
            using (Pen p = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawLine(p, x0, 0, x0, Height);
            Color head = Color.FromArgb(235, 236, 238, 242), dim = Color.FromArgb(165, 190, 195, 204);
            int x = x0 + Ui.D(14), w = pw - Ui.D(24), y = Ui.D(12);
            using (Font hf = Ui.Font(8f, FontStyle.Bold))
            using (Font kf = Ui.Font(8f))
            using (Font vf = Ui.Font(9f, FontStyle.Bold))
            {
                int kh = Ui.Measure(g, "Ag", kf).Height, vh = Ui.Measure(g, "Ag", vf).Height;
                Ui.Text(g, "РАЗМЕРЫ, " + (mmUnit ? "ММ" : "MIL"), hf, dim, new Rectangle(x, y, w, kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                y += kh + Ui.D(10);
                foreach (CheckRow r in SummaryRows())
                {
                    if (y > Height - vh * 2) break;
                    Ui.Text(g, r.Label, kf, dim, new Rectangle(x, y, w, kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    y += kh + Ui.D(1);
                    Ui.Text(g, r.Value, vf, head, new Rectangle(x, y, w, vh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    y += vh + Ui.D(8);
                }
                // pad sizes
                if (an.Groups.Count > 0 && y < Height - vh * 3)
                {
                    y += Ui.D(2);
                    Ui.Text(g, "РАЗМЕРЫ ПЛОЩАДОК", hf, dim, new Rectangle(x, y, w, kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    y += kh + Ui.D(6);
                    int n = 0;
                    foreach (PadGroup pg in an.Groups)
                    {
                        if (y > Height - vh * 2) break;
                        if (++n > 6) break;
                        string s = Fmt.Len(pg.W, mmUnit) + " × " + Fmt.Len(pg.H, mmUnit);
                        if (pg.First.Hole > 0) s += "  Ø" + Fmt.Len(pg.First.Hole, mmUnit);
                        string cnt = "× " + pg.Count + (pg.Count == 1 && pg.First.Name != "" ? "  (" + pg.First.Name + ")" : "");
                        Ui.Text(g, s, vf, head, new Rectangle(x, y, w, vh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                        int sw = Ui.Measure(g, s, vf).Width;
                        Ui.Text(g, cnt, kf, dim, new Rectangle(x + sw + Ui.D(8), y + (vh - kh) / 2, Math.Max(10, w - sw - Ui.D(8)), kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        y += vh + Ui.D(5);
                    }
                }
                // name check
                if (an.Name.Count > 0 && y < Height - vh * 3)
                {
                    y += Ui.D(6);
                    Ui.Text(g, "СВЕРКА С НАЗВАНИЕМ", hf, dim, new Rectangle(x, y, w, kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    y += kh + Ui.D(6);
                    foreach (CheckRow r in an.Name)
                    {
                        if (y > Height - kh * 2) break;
                        Color sc = r.State == 1 ? OkC : r.State == 2 ? WarnC : dim;
                        string mark = r.State == 1 ? "✓" : r.State == 2 ? "⚠" : "•";
                        Ui.Text(g, mark, vf, sc, new Rectangle(x, y, Ui.D(16), vh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                        Ui.Text(g, r.Label, vf, head, new Rectangle(x + Ui.D(16), y, w - Ui.D(16), vh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        y += vh + Ui.D(1);
                        Ui.Text(g, r.Value, kf, r.State == 2 ? WarnC : dim, new Rectangle(x + Ui.D(16), y, w - Ui.D(16), kh + 2), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        y += kh + Ui.D(7);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- toolbar

        // unit switch, overlay switches, fit, optional window button and a hint
        public static BgPanel MakeToolbar(DimView v, bool mm, Action<bool> unitChanged, Action openWindow, bool surface)
        {
            BgPanel bar = new BgPanel { Dock = DockStyle.Bottom, Height = Ui.D(44) };
            if (surface) bar.Surface();
            Segmented unit = new Segmented("мм", "mil") { Height = Ui.D(30) };
            unit.Width = unit.PreferredWidth;
            unit.Selected = mm ? 0 : 1;
            unit.SelectedChanged += delegate
            {
                v.Mm = unit.Selected == 0;
                if (unitChanged != null) unitChanged(v.Mm);
            };
            Toggle dims = new Toggle("Размеры").AutoWidth();
            dims.Checked = true;
            dims.CheckedChanged += delegate { v.ShowDims = dims.Checked; v.Fit(); };
            Toggle silk = new Toggle("Шелкография").AutoWidth();
            silk.Checked = true;
            silk.CheckedChanged += delegate { v.ShowSilk = silk.Checked; v.Invalidate(); };
            FlatButton fit = new FlatButton("Вписать", ButtonKind.Ghost, null) { Height = Ui.D(30) }.AutoWidth();
            fit.Click += delegate { v.Fit(); };
            FlatButton win = null;
            if (openWindow != null)
            {
                win = new FlatButton(Ui.IconFamily != null ? "" : "Окно", ButtonKind.Ghost, Ui.IconFamily != null ? "" : null) { Height = Ui.D(30) };
                win.Width = Ui.IconFamily != null ? Ui.D(34) : win.PreferredWidth;
                win.Click += delegate { openWindow(); };
            }
            TextLine hint = new TextLine("Клик — линейка (ПКМ — сброс) · колесо — масштаб · тянуть — сдвиг") { Dim = true, FontSize = 8.5f, Align = TextFormatFlags.Right };
            List<Control> ctl = new List<Control> { unit, dims, silk, fit };
            if (win != null) ctl.Add(win);
            bar.Controls.AddRange(ctl.ToArray());
            bar.Controls.Add(hint);
            bar.Resize += delegate
            {
                int x = surface ? 0 : Ui.D(12), cy = (surface ? Ui.D(8) : 0) + (bar.Height - (surface ? Ui.D(8) : 0)) / 2;
                foreach (Control c in ctl)
                {
                    c.Location = new Point(x, cy - c.Height / 2);
                    x = c.Right + (c is Toggle || c is Segmented ? Ui.D(12) : Ui.D(2));
                }
                hint.SetBounds(x + Ui.D(8), cy - Ui.D(11), Math.Max(0, bar.Width - x - Ui.D(12)), Ui.D(22));
                int need;
                using (Graphics g = bar.CreateGraphics())
                using (Font f = Ui.Font(hint.FontSize)) need = Ui.Measure(g, hint.Text, f).Width + Ui.D(4);
                hint.Visible = hint.Width >= need;
            };
            return bar;
        }
    }

    class DimForm : Form
    {
        public DimForm(Part p, bool mm, Action<bool> unitChanged)
        {
            Text = "Размеры футпринта — " + (p.Footprint != "" ? p.Footprint : p.Title);
            Font = Ui.Font(9f);
            Ui.ApplyIcon(this);
            BackColor = Theme.Current.Bg;
            ClientSize = new Size(Ui.D(1000), Ui.D(700));
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            DimView view = new DimView { Dock = DockStyle.Fill, Mm = mm };
            view.SetPart(p);
            Panel host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.D(10), Ui.D(10), Ui.D(10), 0), BackColor = Theme.Current.Bg };
            host.Controls.Add(view);
            BgPanel bar = DimView.MakeToolbar(view, mm, unitChanged, null, false);
            Controls.Add(host);
            Controls.Add(bar);
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !view.Focused) Close(); };
            Theme.Changed += OnTheme;
            FormClosed += delegate { Theme.Changed -= OnTheme; };
        }

        void OnTheme(object s, EventArgs e)
        {
            BackColor = Theme.Current.Bg;
            foreach (Control c in Controls) if (c is Panel && !(c is BgPanel)) c.BackColor = Theme.Current.Bg;
            Native.StyleTitleBar(this, Theme.Current);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(this, Theme.Current);
        }
    }
}
