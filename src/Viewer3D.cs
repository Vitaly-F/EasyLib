// 3D preview: parses the EasyEDA OBJ model, places it exactly like the Altium 3D body
// (same rotation / centring / Z rules as Step.cs) and renders it together with the
// footprint pads using a small software z-buffer renderer (no external libraries).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EasyLib
{
    class Scene
    {
        public List<double> V = new List<double>();   // x,y,z (mm)
        public List<int> T = new List<int>();         // vertex indices, 3 per triangle
        public List<int> C = new List<int>();         // ARGB per triangle
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;
        public bool HasModel;

        public int AddV(double x, double y, double z)
        {
            V.Add(x); V.Add(y); V.Add(z);
            MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
            MinZ = Math.Min(MinZ, z); MaxZ = Math.Max(MaxZ, z);
            return V.Count / 3 - 1;
        }

        public void AddPoly(List<double[]> pts, double z, int color)
        {
            if (pts.Count < 3) return;
            int b = AddV(pts[0][0], pts[0][1], z);
            for (int i = 1; i < pts.Count; i++) AddV(pts[i][0], pts[i][1], z);
            for (int i = 1; i + 1 < pts.Count; i++) { T.Add(b); T.Add(b + i); T.Add(b + i + 1); C.Add(color); }
        }
    }

    static class Obj3D
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double MmPerMil = 0.0254;

        public static Scene Build(Part p, string obj, bool board)
        {
            Scene s = new Scene();
            if (board) AddFootprint(s, p);
            if (string.IsNullOrEmpty(obj)) return s;

            // parse OBJ
            List<double[]> verts = new List<double[]>();
            List<int[]> faces = new List<int[]>();
            List<int> faceColor = new List<int>();
            Dictionary<string, int> mats = new Dictionary<string, int>();
            string curMat = null;
            int curColor = Color.FromArgb(180, 180, 180).ToArgb();
            foreach (string raw in obj.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 2) continue;
                string[] t = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "v":
                        if (t.Length >= 4) verts.Add(new double[] { D(t[1]), D(t[2]), D(t[3]) });
                        break;
                    case "newmtl":
                        curMat = t.Length > 1 ? t[1] : "";
                        break;
                    case "Kd":
                        if (curMat != null && t.Length >= 4)
                            mats[curMat] = Color.FromArgb(Clamp(D(t[1])), Clamp(D(t[2])), Clamp(D(t[3]))).ToArgb();
                        break;
                    case "usemtl":
                        if (t.Length > 1 && mats.ContainsKey(t[1])) curColor = mats[t[1]];
                        break;
                    case "f":
                        {
                            List<int> idx = new List<int>();
                            for (int i = 1; i < t.Length; i++)
                            {
                                string a = t[i].Split('/')[0];
                                int n;
                                if (int.TryParse(a, NumberStyles.Integer, Inv, out n))
                                    idx.Add(n > 0 ? n - 1 : verts.Count + n);
                            }
                            for (int i = 1; i + 1 < idx.Count; i++)
                            {
                                faces.Add(new int[] { idx[0], idx[i], idx[i + 1] });
                                faceColor.Add(curColor);
                            }
                            break;
                        }
                }
            }
            if (verts.Count == 0) return s;

            // rotate like Altium does, then centre on the EasyEDA outline and drop to the EasyEDA Z
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            List<double[]> rv = new List<double[]>(verts.Count);
            foreach (double[] v in verts)
            {
                double[] r = Step.Rotate(v, p.ModelRX, p.ModelRY, p.ModelRZ);
                rv.Add(r);
                minx = Math.Min(minx, r[0]); maxx = Math.Max(maxx, r[0]);
                miny = Math.Min(miny, r[1]); maxy = Math.Max(maxy, r[1]);
                minz = Math.Min(minz, r[2]); maxz = Math.Max(maxz, r[2]);
            }
            ModelPlacement mp = Step.Place(p, minx, maxx, miny, maxy, minz, maxz);
            double ox = mp.OriginX * MmPerMil, oy = mp.OriginY * MmPerMil, oz = mp.Dz * MmPerMil;
            int baseIdx = s.V.Count / 3;
            foreach (double[] r in rv) s.AddV(r[0] + ox, r[1] + oy, r[2] + oz);
            for (int i = 0; i < faces.Count; i++)
            {
                int[] f = faces[i];
                if (f[0] < 0 || f[1] < 0 || f[2] < 0 || f[0] >= rv.Count || f[1] >= rv.Count || f[2] >= rv.Count) continue;
                s.T.Add(baseIdx + f[0]); s.T.Add(baseIdx + f[1]); s.T.Add(baseIdx + f[2]);
                s.C.Add(faceColor[i]);
            }
            s.HasModel = true;
            return s;
        }

        static void AddFootprint(Scene s, Part p)
        {
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (FPad pad in p.Pads)
            {
                double r = Math.Max(pad.SX, pad.SY) / 2;
                minx = Math.Min(minx, pad.X - r); maxx = Math.Max(maxx, pad.X + r);
                miny = Math.Min(miny, pad.Y - r); maxy = Math.Max(maxy, pad.Y + r);
            }
            if (minx > maxx) return;
            double m = Math.Max(maxx - minx, maxy - miny) * 0.15 + 20;
            int board = Color.FromArgb(20, 70, 40).ToArgb();
            List<double[]> plate = new List<double[]>();
            plate.Add(new double[] { (minx - m) * MmPerMil, (miny - m) * MmPerMil });
            plate.Add(new double[] { (maxx + m) * MmPerMil, (miny - m) * MmPerMil });
            plate.Add(new double[] { (maxx + m) * MmPerMil, (maxy + m) * MmPerMil });
            plate.Add(new double[] { (minx - m) * MmPerMil, (maxy + m) * MmPerMil });
            s.AddPoly(plate, -0.002, board);

            int gold = Color.FromArgb(214, 170, 70).ToArgb();
            int hole = Color.FromArgb(10, 10, 10).ToArgb();
            foreach (FPad pad in p.Pads)
            {
                List<double[]> outline = new List<double[]>();
                if (pad.Shape == 'P' && pad.Poly != null)
                    foreach (PointD q in pad.Poly) outline.Add(new double[] { q.X * MmPerMil, q.Y * MmPerMil });
                else
                {
                    double hx = pad.SX / 2, hy = pad.SY / 2, a = pad.Rot * Math.PI / 180;
                    List<double[]> local = new List<double[]>();
                    if (pad.Shape == 'R') { local.Add(new double[] { -hx, -hy }); local.Add(new double[] { hx, -hy }); local.Add(new double[] { hx, hy }); local.Add(new double[] { -hx, hy }); }
                    else Stadium(local, hx, hy);
                    foreach (double[] q in local)
                        outline.Add(new double[] { (pad.X + q[0] * Math.Cos(a) - q[1] * Math.Sin(a)) * MmPerMil, (pad.Y + q[0] * Math.Sin(a) + q[1] * Math.Cos(a)) * MmPerMil });
                }
                if (!pad.Plated && pad.Hole > 0) { s.AddPoly(outline, 0.003, hole); continue; }
                s.AddPoly(outline, 0.003, gold);
                if (pad.Hole > 0)
                {
                    List<double[]> h = new List<double[]>();
                    List<double[]> local = new List<double[]>();
                    double hl = pad.SlotLen > pad.Hole ? pad.SlotLen / 2 : pad.Hole / 2, hw = pad.Hole / 2;
                    Stadium(local, hl, hw);
                    double a = pad.SlotRot * Math.PI / 180;
                    if (pad.SlotLen <= pad.Hole) a = 0;
                    foreach (double[] q in local)
                        h.Add(new double[] { (pad.X + q[0] * Math.Cos(a) - q[1] * Math.Sin(a)) * MmPerMil, (pad.Y + q[0] * Math.Sin(a) + q[1] * Math.Cos(a)) * MmPerMil });
                    s.AddPoly(h, 0.006, hole);
                }
            }
        }

        static void Stadium(List<double[]> outPts, double hx, double hy)
        {
            int n = 12;
            if (hx >= hy)
            {
                double c = hx - hy;
                for (int i = 0; i <= n; i++) { double a = -Math.PI / 2 + Math.PI * i / n; outPts.Add(new double[] { c + hy * Math.Cos(a), hy * Math.Sin(a) }); }
                for (int i = 0; i <= n; i++) { double a = Math.PI / 2 + Math.PI * i / n; outPts.Add(new double[] { -c + hy * Math.Cos(a), hy * Math.Sin(a) }); }
            }
            else
            {
                double c = hy - hx;
                for (int i = 0; i <= n; i++) { double a = Math.PI * i / n; outPts.Add(new double[] { hx * Math.Cos(a), c + hx * Math.Sin(a) }); }
                for (int i = 0; i <= n; i++) { double a = Math.PI + Math.PI * i / n; outPts.Add(new double[] { hx * Math.Cos(a), -c + hx * Math.Sin(a) }); }
            }
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : 0;
        }

        static int Clamp(double v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v * 255))); }

        // ---------------- software renderer ----------------

        public static Color BgTop = Color.FromArgb(42, 45, 51), BgBottom = Color.FromArgb(23, 24, 27);

        public static int[] Render(Scene s, int w, int h, double yawDeg, double pitchDeg, double zoom, double panX, double panY)
        {
            int[] px = new int[w * h];
            float[] zb = new float[w * h];
            Color c0 = BgTop, c1 = BgBottom;
            for (int y = 0; y < h; y++)
            {
                float k = y / (float)Math.Max(1, h - 1);
                int col = Color.FromArgb((int)(c0.R + (c1.R - c0.R) * k), (int)(c0.G + (c1.G - c0.G) * k), (int)(c0.B + (c1.B - c0.B) * k)).ToArgb();
                for (int x = 0; x < w; x++) { px[y * w + x] = col; zb[y * w + x] = float.MinValue; }
            }
            if (s.T.Count == 0) return px;

            double cx = (s.MinX + s.MaxX) / 2, cy = (s.MinY + s.MaxY) / 2, cz = (s.MinZ + s.MaxZ) / 2;
            double diag = Math.Sqrt(Sq(s.MaxX - s.MinX) + Sq(s.MaxY - s.MinY) + Sq(s.MaxZ - s.MinZ));
            double scale = zoom * Math.Min(w, h) / Math.Max(diag, 0.01) * 0.9;
            double cyw = Math.Cos(yawDeg * Math.PI / 180), syw = Math.Sin(yawDeg * Math.PI / 180);
            double cp = Math.Cos(pitchDeg * Math.PI / 180), sp = Math.Sin(pitchDeg * Math.PI / 180);

            int nv = s.V.Count / 3;
            double[] X = new double[nv], Y = new double[nv], Z = new double[nv];
            for (int i = 0; i < nv; i++)
            {
                double x = s.V[i * 3] - cx, y = s.V[i * 3 + 1] - cy, z = s.V[i * 3 + 2] - cz;
                double x1 = x * cyw - y * syw, y1 = x * syw + y * cyw;
                double y2 = y1 * cp - z * sp, z2 = y1 * sp + z * cp;
                X[i] = w / 2.0 + panX + x1 * scale;
                Y[i] = h / 2.0 + panY - y2 * scale;
                Z[i] = z2;
            }
            double lx = -0.35, ly = 0.45, lz = 1.0, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            lx /= ll; ly /= ll; lz /= ll;

            for (int t = 0; t < s.T.Count / 3; t++)
            {
                int a = s.T[t * 3], b = s.T[t * 3 + 1], c = s.T[t * 3 + 2];
                // normal in view space (screen y is flipped)
                double ux = X[b] - X[a], uy = -(Y[b] - Y[a]), uz = Z[b] - Z[a];
                double vx = X[c] - X[a], vy = -(Y[c] - Y[a]), vz = Z[c] - Z[a];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                double shade = 0.55;
                if (nl > 1e-12) shade = 0.3 + 0.7 * Math.Abs((nx * lx + ny * ly + nz * lz) / nl);
                Color col = Color.FromArgb(s.C[t]);
                int argb = Color.FromArgb((int)(col.R * shade), (int)(col.G * shade), (int)(col.B * shade)).ToArgb();
                Raster(px, zb, w, h, X[a], Y[a], Z[a], X[b], Y[b], Z[b], X[c], Y[c], Z[c], argb);
            }
            return px;
        }

        static double Sq(double v) { return v * v; }

        static void Raster(int[] px, float[] zb, int w, int h,
                           double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2, int color)
        {
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
            int maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
            int maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
            if (minX > maxX || minY > maxY) return;
            double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area) < 1e-9) return;
            double inv = 1.0 / area;
            for (int y = minY; y <= maxY; y++)
            {
                double py = y + 0.5;
                for (int x = minX; x <= maxX; x++)
                {
                    double pxx = x + 0.5;
                    double w0 = ((x1 - pxx) * (y2 - py) - (x2 - pxx) * (y1 - py)) * inv;
                    double w1 = ((x2 - pxx) * (y0 - py) - (x0 - pxx) * (y2 - py)) * inv;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;
                    float z = (float)(w0 * z0 + w1 * z1 + w2 * z2);
                    int i = y * w + x;
                    if (z > zb[i]) { zb[i] = z; px[i] = color; }
                }
            }
        }

        public static Bitmap ToBitmap(int[] px, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                Marshal.Copy(px, y * w, IntPtr.Add(bd.Scan0, y * bd.Stride), w);
            bmp.UnlockBits(bd);
            return bmp;
        }
    }

    // interactive control: drag = rotate, right drag = pan, wheel = zoom, double click = reset
    class Viewer3D : Control
    {
        Scene scene;
        double yaw = -30, pitch = -58, zoom = 1, panX, panY;
        Point last;
        MouseButtons dragButton = MouseButtons.None;
        Bitmap frame;
        Timer hqTimer;
        string message = "";

        public Viewer3D()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(40, 44, 52);
            hqTimer = new Timer { Interval = 180 };
            hqTimer.Tick += delegate { hqTimer.Stop(); Redraw(true); };
            Theme.Changed += OnTheme;
        }

        void OnTheme(object s, EventArgs e) { Redraw(true); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Theme.Changed -= OnTheme; hqTimer.Dispose(); if (frame != null) frame.Dispose(); }
            base.Dispose(disposing);
        }

        public Scene Scene
        {
            get { return scene; }
            set { scene = value; message = ""; Redraw(true); }
        }

        public string Message
        {
            get { return message; }
            set { message = value ?? ""; scene = null; Redraw(true); }
        }

        public void ResetView()
        {
            yaw = -30; pitch = -58; zoom = 1; panX = panY = 0;
            Redraw(true);
        }

        public void SetView(double yawDeg, double pitchDeg)
        {
            yaw = yawDeg; pitch = pitchDeg; panX = panY = 0;
            Redraw(true);
        }

        // fast frame while interacting, 2x supersampled frame when idle
        void Redraw(bool hq)
        {
            if (Width < 4 || Height < 4) return;
            if (frame != null) { frame.Dispose(); frame = null; }
            if (scene != null)
            {
                if (hq)
                {
                    int w2 = Width * 2, h2 = Height * 2;
                    using (Bitmap big = Obj3D.ToBitmap(Obj3D.Render(scene, w2, h2, yaw, pitch, zoom, panX * 2, panY * 2), w2, h2))
                    {
                        frame = new Bitmap(Width, Height);
                        using (Graphics g = Graphics.FromImage(frame))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(big, new Rectangle(0, 0, Width, Height));
                        }
                    }
                }
                else
                {
                    frame = Obj3D.ToBitmap(Obj3D.Render(scene, Width, Height, yaw, pitch, zoom, panX, panY), Width, Height);
                    hqTimer.Stop();
                    hqTimer.Start();
                }
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Redraw(false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (frame != null) { g.DrawImageUnscaled(frame, 0, 0); return; }
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                       new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Obj3D.BgTop, Obj3D.BgBottom, 90f))
                g.FillRectangle(b, ClientRectangle);
            if (message != "")
                using (Font f = Ui.Font(10f))
                    Ui.Text(g, message, f, Theme.Current.TextDim, ClientRectangle,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            last = e.Location;
            dragButton = e.Button;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragButton != MouseButtons.None) Redraw(true);
            dragButton = MouseButtons.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragButton == MouseButtons.None || scene == null) return;
            int dx = e.X - last.X, dy = e.Y - last.Y;
            last = e.Location;
            if (dragButton == MouseButtons.Left)
            {
                yaw += dx * 0.6;
                pitch = Math.Max(-180, Math.Min(0, pitch + dy * 0.6));
            }
            else { panX += dx; panY += dy; }
            Redraw(false);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (scene == null) return;
            zoom *= e.Delta > 0 ? 1.15 : 1 / 1.15;
            zoom = Math.Max(0.2, Math.Min(20, zoom));
            Redraw(false);
        }

        protected override void OnDoubleClick(EventArgs e) { ResetView(); }
    }
}
