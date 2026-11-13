// Intermediate part model (units: mil, Altium orientation: Y up)
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
    class PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    // ---------------- intermediate model ----------------

    class SPin
    {
        public int Unit = 1;
        public string Num = "", Name = "";
        public int Elec;            // 0 passive,1 input,2 output,3 io,4 power,5 open collector,6 hi-Z,7 open emitter
        public double X, Y;         // body end (Altium pin location), mil
        public int Orient;          // 0,1,2,3 = 0,90,180,270 deg (direction body -> hot end)
        public double Len;          // mil
        public bool NameVis = true, NumVis = true, Dot, Clock, Hidden;
    }

    class SShape
    {
        public string Kind;         // RECT, LINE, POLY, ELL, ARC, TEXT
        public int Unit = 1;
        public List<PointD> Pts = new List<PointD>();
        public int Fill;            // 0 none, 1 body colour, 2 line colour
        public double R, R2, A1, A2, Rot, Size;
        public string Text = "";
    }

    static class ShapeOrder
    {
        // Altium (and the preview) draw symbol graphics in record order, so a filled body drawn
        // after the lines inside it hides them. Order: filled shapes from the largest to the
        // smallest, then outlines, lines and arcs, then texts. Pins are written after all of them.
        public static List<SShape> Sorted(List<SShape> shapes)
        {
            List<KeyValuePair<double, SShape>> filled = new List<KeyValuePair<double, SShape>>();
            List<SShape> lines = new List<SShape>(), texts = new List<SShape>();
            foreach (SShape s in shapes)
            {
                if (s.Kind == "TEXT") texts.Add(s);
                else if (s.Fill != 0 && (s.Kind == "RECT" || s.Kind == "POLY" || s.Kind == "ELL")) filled.Add(new KeyValuePair<double, SShape>(Area(s), s));
                else lines.Add(s);
            }
            // stable sort by area, largest first
            List<int> idx = new List<int>();
            for (int i = 0; i < filled.Count; i++) idx.Add(i);
            idx.Sort(delegate(int a, int b)
            {
                int c = filled[b].Key.CompareTo(filled[a].Key);
                return c != 0 ? c : a.CompareTo(b);
            });
            List<SShape> r = new List<SShape>();
            foreach (int i in idx) r.Add(filled[i].Value);
            r.AddRange(lines);
            r.AddRange(texts);
            return r;
        }

        public static double Area(SShape s)
        {
            if (s.Kind == "ELL") return Math.PI * Math.Abs(s.R * s.R2);
            if (s.Kind == "RECT" && s.Pts.Count >= 2) return Math.Abs((s.Pts[1].X - s.Pts[0].X) * (s.Pts[1].Y - s.Pts[0].Y));
            return PolyArea(s.Pts);
        }

        public static double PolyArea(List<PointD> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                PointD p = pts[i], q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return Math.Abs(a) / 2;
        }
    }

    class FPad
    {
        public string Name = "";
        public double X, Y, SX, SY, Rot;    // mil, deg
        public char Shape = 'R';            // R rect, C round, O oval/obround, P polygon
        public string Layer = "T";          // T, B, M
        public double Hole, SlotLen, SlotRot;
        public bool Plated = true;
        public List<PointD> Poly;           // absolute polygon (mil) for custom pads
    }

    class FGfx
    {
        public string Kind;                 // TRK, ARC, REG
        public string Layer;
        public double W;
        public List<PointD> Pts = new List<PointD>();
        public double CX, CY, R, A1, A2;
    }

    class Part
    {
        public string Title = "", Prefix = "U?", Desc = "", Footprint = "", Lcsc = "", Uuid = "", Author = "";
        public bool Official = true;
        public Dictionary<string, string> Params = new Dictionary<string, string>();
        public int PartCount = 1;
        public List<SPin> Pins = new List<SPin>();
        public List<SShape> Shapes = new List<SShape>();
        public List<FPad> Pads = new List<FPad>();
        public List<FGfx> Gfx = new List<FGfx>();
        // 3D
        public string ModelUuid = "", ModelTitle = "";
        public double ModelRX, ModelRY, ModelRZ;
        public double ModelTX, ModelTY, ModelTZ;    // target outline centre and bottom Z (mil)
        public bool HasOutline;
        public Dictionary<string, string> Info = new Dictionary<string, string>();
        public Dictionary<string, ShopResult> Shops = new Dictionary<string, ShopResult>();   // stock and price, filled while the part is shown
        // parts that do not come from EasyEDA
        public string Source = "easyeda";           // easyeda, kicad, import
        public KiModel KiModel;                     // 3D model reference of a KiCad footprint
        public byte[] StepData;                     // STEP already at hand (imported ZIP)
        public string StepName;
        public ModelPlacement StepPlacement;
        public bool NoSymbol;                       // footprint only
        public string FootprintDesc = "";
    }

}
