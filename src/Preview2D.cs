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
    static class Preview
    {

        public static Bitmap Symbol(Part p, int W, int H) { return Symbol(p, W, H, Theme.MakeLight().Preview); }
        public static Bitmap Footprint(Part p, int W, int H) { return Footprint(p, W, H, Theme.MakeLight().Preview); }

        public static Bitmap Symbol(Part p, int W, int H, PreviewPalette pal)
        {
            List<PointD> all = new List<PointD>();
            foreach (SShape s in p.Shapes)
            {
                if (s.Unit != 1) continue;
                if (s.Kind == "ELL" || s.Kind == "ARC")
                {
                    all.Add(new PointD(s.Pts[0].X - s.R, s.Pts[0].Y - Math.Max(s.R, s.R2)));
                    all.Add(new PointD(s.Pts[0].X + s.R, s.Pts[0].Y + Math.Max(s.R, s.R2)));
                }
                else all.AddRange(s.Pts);
            }
            foreach (SPin pin in p.Pins)
            {
                if (pin.Unit != 1) continue;
                all.Add(new PointD(pin.X, pin.Y));
                all.Add(Hot(pin));
            }
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(pal.SymBg);
                if (all.Count > 0)
                {
                    Xf xf = new Xf(all, W, H, Math.Max(14, Math.Min(W, H) / 12));
                    float lw = Math.Max(1.4f, Math.Min(2.6f, (float)(10 * xf.S)));
                    using (Pen pen = new Pen(pal.SymLine, lw))
                    using (Brush bodyB = new SolidBrush(pal.SymBody))
                    using (Brush lineB = new SolidBrush(pal.SymFill))
                    using (Brush textB = new SolidBrush(pal.SymText))
                    {
                        foreach (SShape s in ShapeOrder.Sorted(p.Shapes))
                        {
                            if (s.Unit != 1) continue;
                            if (s.Kind == "RECT")
                            {
                                RectangleF r = xf.Rect(s.Pts[0], s.Pts[1]);
                                if (s.Fill > 0) g.FillRectangle(s.Fill == 1 ? bodyB : lineB, r);
                                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                            }
                            else if (s.Kind == "LINE" || s.Kind == "POLY")
                            {
                                PointF[] pts = xf.Pts(s.Pts);
                                if (s.Kind == "POLY" && s.Fill > 0) g.FillPolygon(s.Fill == 1 ? bodyB : lineB, pts);
                                if (s.Kind == "POLY") g.DrawPolygon(pen, pts); else g.DrawLines(pen, pts);
                            }
                            else if (s.Kind == "ELL")
                            {
                                PointF c = xf.P(s.Pts[0]);
                                float rx = (float)(s.R * xf.S), ry = (float)(s.R2 * xf.S);
                                if (s.Fill > 0) g.FillEllipse(s.Fill == 1 ? bodyB : lineB, c.X - rx, c.Y - ry, 2 * rx, 2 * ry);
                                g.DrawEllipse(pen, c.X - rx, c.Y - ry, 2 * rx, 2 * ry);
                            }
                            else if (s.Kind == "ARC")
                            {
                                PointF c = xf.P(s.Pts[0]);
                                float r = (float)(s.R * xf.S);
                                double sweep = Geo.Norm(s.A2 - s.A1); if (sweep == 0) sweep = 360;
                                if (r > 0.5f) g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, (float)(-s.A1), (float)(-sweep));
                            }
                            else if (s.Kind == "TEXT" && !string.IsNullOrEmpty(s.Text) && xf.S * 100 > 6)
                            {
                                PointF c = xf.P(s.Pts[0]);
                                float px = (float)Math.Max(6.0, Math.Min(16.0, (s.Size > 0 ? s.Size : 7) * 10 * xf.S));
                                using (Font tf = new Font(Ui.UiFamily, px, FontStyle.Regular, GraphicsUnit.Pixel))
                                {
                                    GraphicsState st = g.Save();
                                    g.TranslateTransform(c.X, c.Y);
                                    if (Math.Abs(s.Rot) > 0.1) g.RotateTransform((float)(-s.Rot));
                                    SizeF sz = g.MeasureString(s.Text, tf);
                                    g.DrawString(s.Text, tf, textB, 0, -sz.Height);
                                    g.Restore(st);
                                }
                            }
                        }
                    }
                    using (Pen pinPen = new Pen(pal.Pin, Math.Max(1.4f, Math.Min(2.6f, (float)(10 * xf.S)))))
                    using (Font f = new Font(Ui.UiFamily, Math.Max(7f, Math.Min(13f, (float)(65 * xf.S))), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Brush nb = new SolidBrush(pal.PinText))
                    {
                        foreach (SPin pin in p.Pins)
                        {
                            if (pin.Unit != 1 || pin.Hidden) continue;
                            PointF a = xf.P(new PointD(pin.X, pin.Y)), b = xf.P(Hot(pin));
                            g.DrawLine(pinPen, a, b);
                            if (xf.S * 100 > 6 && pin.Num != "")
                            {
                                PointF m = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                                SizeF sz = g.MeasureString(pin.Num, f);
                                g.DrawString(pin.Num, f, nb, m.X - sz.Width / 2, m.Y - sz.Height);
                            }
                            if (xf.S * 100 > 8 && pin.NameVis && pin.Name != "" && (pin.Orient == 0 || pin.Orient == 2))
                            {
                                SizeF sz = g.MeasureString(pin.Name, f);
                                float nx = pin.Orient == 0 ? a.X - sz.Width - 2 : a.X + 2;
                                g.DrawString(pin.Name, f, nb, nx, a.Y - sz.Height / 2);
                            }
                        }
                    }
                }
            }
            return bmp;
        }

        static PointD Hot(SPin pin)
        {
            switch (pin.Orient)
            {
                case 0: return new PointD(pin.X + pin.Len, pin.Y);
                case 1: return new PointD(pin.X, pin.Y + pin.Len);
                case 2: return new PointD(pin.X - pin.Len, pin.Y);
                default: return new PointD(pin.X, pin.Y - pin.Len);
            }
        }

        public static Bitmap Footprint(Part p, int W, int H, PreviewPalette pal)
        {
            List<PointD> all = new List<PointD>();
            foreach (FPad pad in p.Pads)
            {
                double r = Math.Max(pad.SX, pad.SY) / 2;
                all.Add(new PointD(pad.X - r, pad.Y - r)); all.Add(new PointD(pad.X + r, pad.Y + r));
                if (pad.Poly != null) all.AddRange(pad.Poly);
            }
            foreach (FGfx gf in p.Gfx)
            {
                if (gf.Layer != "TO" && gf.Layer != "T") continue;
                if (gf.Kind == "ARC") { all.Add(new PointD(gf.CX - gf.R, gf.CY - gf.R)); all.Add(new PointD(gf.CX + gf.R, gf.CY + gf.R)); }
                else all.AddRange(gf.Pts);
            }
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(pal.FpBg);
                if (all.Count > 0)
                {
                    Xf xf = new Xf(all, W, H, Math.Max(12, Math.Min(W, H) / 10));
                    using (Brush top = new SolidBrush(pal.FpTop))
                    using (Brush bottom = new SolidBrush(pal.FpBottom))
                    using (Brush multi = new SolidBrush(pal.FpMulti))
                    using (Brush hole = new SolidBrush(pal.FpHole))
                    using (Pen silk = new Pen(pal.FpSilk, 1.4f))
                    using (Brush silkB = new SolidBrush(pal.FpSilk))
                    {
                        foreach (FGfx gf in p.Gfx)
                        {
                            if (gf.Layer != "TO") continue;
                            float w = (float)Math.Max(1.0, gf.W * xf.S);
                            using (Pen pen = new Pen(silk.Color, w))
                            {
                                pen.StartCap = pen.EndCap = LineCap.Round;
                                if (gf.Kind == "TRK") g.DrawLine(pen, xf.P(gf.Pts[0]), xf.P(gf.Pts[1]));
                                else if (gf.Kind == "ARC")
                                {
                                    PointF c = xf.P(new PointD(gf.CX, gf.CY));
                                    float r = (float)(gf.R * xf.S);
                                    double sweep = Geo.Norm(gf.A2 - gf.A1); if (sweep == 0) sweep = 360;
                                    if (r > 0.5f) g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, (float)(-gf.A1), (float)(-sweep));
                                }
                                else if (gf.Kind == "REG" && gf.Pts.Count >= 3) g.FillPolygon(silkB, xf.Pts(gf.Pts));
                            }
                        }
                        foreach (FPad pad in p.Pads)
                        {
                            Brush b = pad.Layer == "M" ? multi : pad.Layer == "B" ? bottom : top;
                            if (!pad.Plated && pad.Hole > 0 && pad.SX <= pad.Hole + 0.01)
                            {
                                PointF c = xf.P(new PointD(pad.X, pad.Y));
                                float r = (float)(pad.Hole / 2 * xf.S);
                                using (Pen hp = new Pen(pal.FpNpth, 1.2f)) g.DrawEllipse(hp, c.X - r, c.Y - r, 2 * r, 2 * r);
                                continue;
                            }
                            if (pad.Shape == 'P' && pad.Poly != null) { g.FillPolygon(b, xf.Pts(pad.Poly)); continue; }
                            GraphicsState st = g.Save();
                            PointF pc = xf.P(new PointD(pad.X, pad.Y));
                            g.TranslateTransform(pc.X, pc.Y);
                            g.RotateTransform((float)(-pad.Rot));
                            float sx = (float)(pad.SX * xf.S), sy = (float)(pad.SY * xf.S);
                            if (pad.Shape == 'R') g.FillRectangle(b, -sx / 2, -sy / 2, sx, sy);
                            else if (pad.Shape == 'C') g.FillEllipse(b, -sx / 2, -sy / 2, sx, sy);
                            else
                            {
                                using (GraphicsPath gp = Obround(sx, sy)) g.FillPath(b, gp);
                            }
                            if (pad.Hole > 0)
                            {
                                float hr = (float)(pad.Hole * xf.S);
                                if (pad.SlotLen > 0)
                                {
                                    float sl = (float)(pad.SlotLen * xf.S);
                                    g.RotateTransform((float)(-(pad.SlotRot - pad.Rot)));
                                    using (GraphicsPath gp = Obround(sl, hr)) g.FillPath(hole, gp);
                                }
                                else g.FillEllipse(hole, -hr / 2, -hr / 2, hr, hr);
                            }
                            g.Restore(st);
                        }
                    }
                }
            }
            return bmp;
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

        class Xf
        {
            public double S, Ox, Oy;
            int w, h;
            public Xf(List<PointD> pts, int w, int h, int margin)
            {
                this.w = w; this.h = h;
                double minx, miny, maxx, maxy;
                Geo.BBox(pts, out minx, out miny, out maxx, out maxy);
                double bw = Math.Max(maxx - minx, 1), bh = Math.Max(maxy - miny, 1);
                S = Math.Min((w - 2 * margin) / bw, (h - 2 * margin) / bh);
                Ox = (minx + maxx) / 2; Oy = (miny + maxy) / 2;
            }
            public PointF P(PointD p)
            {
                return new PointF((float)(w / 2.0 + (p.X - Ox) * S), (float)(h / 2.0 - (p.Y - Oy) * S));
            }
            public PointF[] Pts(List<PointD> l)
            {
                PointF[] r = new PointF[l.Count];
                for (int i = 0; i < l.Count; i++) r[i] = P(l[i]);
                return r;
            }
            public RectangleF Rect(PointD a, PointD b)
            {
                PointF pa = P(a), pb = P(b);
                return new RectangleF(Math.Min(pa.X, pb.X), Math.Min(pa.Y, pb.Y), Math.Abs(pa.X - pb.X), Math.Abs(pa.Y - pb.Y));
            }
        }
    }
}
