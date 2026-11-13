// Computes where a STEP model goes on the footprint so that it matches EasyEDA:
// the model (after its EasyEDA rotation) is centred on the EasyEDA 3D outline and its
// lowest point sits at the EasyEDA Z offset.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class ModelPlacement
    {
        public double RotX, RotY, RotZ;        // degrees (Altium MODEL.3D.ROTX/Y/Z)
        public double OriginX, OriginY;        // mil (Altium MODEL.2D.X/Y)
        public double Dz;                      // mil (Altium MODEL.3D.DZ)
        public double CenterX, CenterY;        // mil, centre of the projected body
        public double SizeX, SizeY;            // mil, projected body size
        public double BottomZ, TopZ;           // mil, body extent above the board
    }

    static class Step
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double MilPerMm = 1000.0 / 25.4;

        // vertices of a STEP file in mm
        public static List<double[]> Points(byte[] stepBytes)
        {
            string text = Encoding.ASCII.GetString(stepBytes);
            double scale = 1.0;  // to mm
            if (Regex.IsMatch(text, "CONVERSION_BASED_UNIT\\s*\\(\\s*'INCH'", RegexOptions.IgnoreCase)) scale = 25.4;
            else if (Regex.IsMatch(text, "SI_UNIT\\s*\\(\\s*\\$\\s*,\\s*\\.METRE\\.\\s*\\)")) scale = 1000.0;

            text = text.Replace("\r", "").Replace("\n", "");
            Dictionary<string, double[]> pts = new Dictionary<string, double[]>();
            foreach (Match m in Regex.Matches(text, "#(\\d+)\\s*=\\s*CARTESIAN_POINT\\s*\\(\\s*'[^']*'\\s*,\\s*\\(\\s*([^,\\)]+)\\s*,\\s*([^,\\)]+)\\s*(?:,\\s*([^,\\)]+))?\\s*\\)"))
            {
                double z = m.Groups[4].Success ? P(m.Groups[4].Value) : 0;
                pts[m.Groups[1].Value] = new double[] { P(m.Groups[2].Value) * scale, P(m.Groups[3].Value) * scale, z * scale };
            }
            List<double[]> verts = new List<double[]>();
            foreach (Match m in Regex.Matches(text, "VERTEX_POINT\\s*\\(\\s*'[^']*'\\s*,\\s*#(\\d+)\\s*\\)"))
            {
                double[] v;
                if (pts.TryGetValue(m.Groups[1].Value, out v)) verts.Add(v);
            }
            if (verts.Count == 0) verts.AddRange(pts.Values);
            return verts;
        }

        public static ModelPlacement Compute(Part p, byte[] stepBytes)
        {
            List<double[]> verts = Points(stepBytes);

            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            foreach (double[] v in verts)
            {
                double[] r = Rotate(v, p.ModelRX, p.ModelRY, p.ModelRZ);
                minx = Math.Min(minx, r[0]); maxx = Math.Max(maxx, r[0]);
                miny = Math.Min(miny, r[1]); maxy = Math.Max(maxy, r[1]);
                minz = Math.Min(minz, r[2]); maxz = Math.Max(maxz, r[2]);
            }
            if (verts.Count == 0) { minx = maxx = miny = maxy = minz = maxz = 0; }
            return Place(p, minx, maxx, miny, maxy, minz, maxz);
        }

        // Shared with the OBJ viewer: bbox (mm, already rotated) -> placement (mil)
        public static ModelPlacement Place(Part p, double minx, double maxx, double miny, double maxy, double minz, double maxz)
        {
            ModelPlacement mp = new ModelPlacement();
            mp.RotX = p.ModelRX; mp.RotY = p.ModelRY; mp.RotZ = p.ModelRZ;
            double cx = (minx + maxx) / 2 * MilPerMm, cy = (miny + maxy) / 2 * MilPerMm;
            mp.OriginX = p.ModelTX - cx;
            mp.OriginY = p.ModelTY - cy;
            mp.Dz = p.ModelTZ - minz * MilPerMm;
            mp.CenterX = p.ModelTX; mp.CenterY = p.ModelTY;
            mp.SizeX = (maxx - minx) * MilPerMm; mp.SizeY = (maxy - miny) * MilPerMm;
            mp.BottomZ = p.ModelTZ;
            mp.TopZ = p.ModelTZ + (maxz - minz) * MilPerMm;
            return mp;
        }

        static double P(string s) { return double.Parse(s.Trim(), NumberStyles.Float, Inv); }

        // rotate about X, then Y, then Z (degrees, right-handed) - same order Altium uses
        public static double[] Rotate(double[] v, double rx, double ry, double rz)
        {
            double x = v[0], y = v[1], z = v[2];
            double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;
            double y1 = y * Math.Cos(a) - z * Math.Sin(a), z1 = y * Math.Sin(a) + z * Math.Cos(a); y = y1; z = z1;
            double x2 = x * Math.Cos(b) + z * Math.Sin(b), z2 = -x * Math.Sin(b) + z * Math.Cos(b); x = x2; z = z2;
            double x3 = x * Math.Cos(c) - y * Math.Sin(c), y3 = x * Math.Sin(c) + y * Math.Cos(c); x = x3; y = y3;
            return new double[] { x, y, z };
        }
    }
}
