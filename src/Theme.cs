// Colours, DPI scaling and small native helpers for the custom-drawn UI.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EasyLib
{
    class Theme
    {
        public string Name;
        public bool Dark;
        public Color Bg, Surface, Surface2, Surface3, Border, Text, TextDim, TextFaint;
        public Color Accent, AccentHover, AccentText, Selection, Hover, InputBg;
        public Color Basic, Extended, Community, Danger, Warning, Success;
        public Color View3DTop, View3DBottom;
        public PreviewPalette Preview;

        public static Theme Current = MakeDark();
        public static event EventHandler Changed;

        public static void Set(string name)
        {
            Current = name == "light" ? MakeLight() : MakeDark();
            Obj3D.BgTop = Current.View3DTop;
            Obj3D.BgBottom = Current.View3DBottom;
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Theme MakeDark()
        {
            Theme t = new Theme();
            t.Name = "dark"; t.Dark = true;
            t.Bg = C(0x1B1C1F); t.Surface = C(0x242529); t.Surface2 = C(0x2D2F34); t.Surface3 = C(0x373A40);
            t.Border = C(0x393C42); t.Text = C(0xE3E5E8); t.TextDim = C(0x9BA1AA); t.TextFaint = C(0x6B717B);
            t.Accent = C(0x3B82F6); t.AccentHover = C(0x5B97F8); t.AccentText = Color.White;
            t.Selection = C(0x23395D); t.Hover = C(0x2C2E33); t.InputBg = C(0x1F2024);
            t.Basic = C(0x4ADE80); t.Extended = C(0xFBBF24); t.Community = C(0xC084FC);
            t.Danger = C(0xF87171); t.Warning = C(0xFBBF24); t.Success = C(0x4ADE80);
            t.View3DTop = C(0x2A2D33); t.View3DBottom = C(0x17181B);
            PreviewPalette p = new PreviewPalette();
            p.SymBg = C(0x1F2024); p.SymBody = C(0x3A3424); p.SymLine = C(0xE5B54A); p.SymFill = C(0xE5B54A);
            p.Pin = C(0x7AA7FF); p.PinText = C(0xC9CDD4); p.SymText = C(0xC9CDD4);
            p.FpBg = C(0x141518); p.FpTop = C(0xE04545); p.FpBottom = C(0x3D7BE0); p.FpMulti = C(0xD4AA4A);
            p.FpSilk = C(0xF0E070); p.FpHole = C(0x141518); p.FpNpth = C(0x8A8F98);
            t.Preview = p;
            return t;
        }

        public static Theme MakeLight()
        {
            Theme t = new Theme();
            t.Name = "light"; t.Dark = false;
            t.Bg = C(0xF1F2F4); t.Surface = C(0xFFFFFF); t.Surface2 = C(0xF3F4F6); t.Surface3 = C(0xE6E8EB);
            t.Border = C(0xDCDFE4); t.Text = C(0x1F2328); t.TextDim = C(0x5F6670); t.TextFaint = C(0x9AA0A8);
            t.Accent = C(0x2563EB); t.AccentHover = C(0x3B74F0); t.AccentText = Color.White;
            t.Selection = C(0xDCE8FF); t.Hover = C(0xF1F4F9); t.InputBg = C(0xFFFFFF);
            t.Basic = C(0x15803D); t.Extended = C(0xB45309); t.Community = C(0x7C3AED);
            t.Danger = C(0xDC2626); t.Warning = C(0xB45309); t.Success = C(0x15803D);
            t.View3DTop = C(0xE9ECF1); t.View3DBottom = C(0xC9CED6);
            PreviewPalette p = new PreviewPalette();
            p.SymBg = C(0xFFFFFF); p.SymBody = C(0xFFFFB0); p.SymLine = C(0x800000); p.SymFill = C(0x800000);
            p.Pin = C(0x14148C); p.PinText = C(0x282828); p.SymText = C(0x282828);
            p.FpBg = C(0x181818); p.FpTop = C(0xDC2828); p.FpBottom = C(0x3D7BE0); p.FpMulti = C(0xC8AA3C);
            p.FpSilk = C(0xF0F050); p.FpHole = C(0x181818); p.FpNpth = C(0x8A8F98);
            t.Preview = p;
            return t;
        }

        public Color ClassColor(string cls)
        {
            if (cls == "Basic") return Basic;
            if (cls == "Extended") return Extended;
            if (cls == "Сообщество") return Community;
            if (cls == "KiCad") return Dark ? Color.FromArgb(45, 212, 191) : Color.FromArgb(15, 118, 110);
            if (cls == "SnapEDA" || cls == "Ultra Librarian" || cls == "Component Search Engine" || cls == "Импорт")
                return Dark ? Color.FromArgb(244, 114, 182) : Color.FromArgb(190, 24, 93);
            return TextDim;
        }
    }

    class PreviewPalette
    {
        public Color SymBg, SymBody, SymLine, SymFill, Pin, PinText, SymText;
        public Color FpBg, FpTop, FpBottom, FpMulti, FpSilk, FpHole, FpNpth;
    }

    static class Ui
    {
        public static float Scale = 1f;

        public static int D(int v) { return (int)Math.Round(v * Scale); }
        public static float Df(float v) { return v * Scale; }

        public static void InitScale()
        {
            string forced = Environment.GetEnvironmentVariable("EASYLIB_SCALE");
            float f;
            if (!string.IsNullOrEmpty(forced) && float.TryParse(forced, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0.5f)
            {
                Scale = f;
                return;
            }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch { Scale = 1f; }
        }

        static string uiFamily, iconFamily;
        static bool iconChecked;

        public static string UiFamily
        {
            get
            {
                if (uiFamily == null) uiFamily = HasFont("Segoe UI") ? "Segoe UI" : FontFamily.GenericSansSerif.Name;
                return uiFamily;
            }
        }

        // Windows 10/11 icon fonts (glyphs from the Segoe MDL2 / Fluent set); null on other systems
        public static string IconFamily
        {
            get
            {
                if (!iconChecked)
                {
                    iconChecked = true;
                    if (HasFont("Segoe Fluent Icons")) iconFamily = "Segoe Fluent Icons";
                    else if (HasFont("Segoe MDL2 Assets")) iconFamily = "Segoe MDL2 Assets";
                }
                return iconFamily;
            }
        }

        static bool HasFont(string name)
        {
            try
            {
                foreach (FontFamily f in FontFamily.Families)
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        // ---- embedded branding (src/Assets) ----
        static Icon appIcon;
        static Bitmap logoMark;

        static System.IO.Stream Asset(string name)
        {
            return typeof(Ui).Assembly.GetManifestResourceStream("EasyLib.Assets." + name);
        }

        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                {
                    try { using (System.IO.Stream s = Asset("EasyLib.ico")) if (s != null) appIcon = new Icon(s); } catch { }
                }
                return appIcon;
            }
        }

        public static Bitmap LogoMark
        {
            get
            {
                if (logoMark == null)
                {
                    try { using (System.IO.Stream s = Asset("logo-mark.png")) if (s != null) logoMark = new Bitmap(s); } catch { }
                }
                return logoMark;
            }
        }

        public static void ApplyIcon(Form f)
        {
            Icon ic = AppIcon;
            if (ic != null) f.Icon = ic;
        }

        public static Font Font(float pt) { return new Font(UiFamily, pt, FontStyle.Regular); }
        public static Font Font(float pt, FontStyle st) { return new Font(UiFamily, pt, st); }

        public static Font Icon(float pt)
        {
            string f = IconFamily;
            return f == null ? null : new Font(f, pt, FontStyle.Regular);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPrefix);
        }

        // background behind a control: the nearest opaque colour up the parent chain
        // (clearing with a transparent colour paints black on Windows)
        public static Color BackOf(Control parent)
        {
            for (Control c = parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return Theme.Current.Bg;
        }

        public static Size Measure(Graphics g, string s, Font f)
        {
            return TextRenderer.MeasureText(g, s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static void ForAll(Control root, Action<Control> a)
        {
            a(root);
            foreach (Control c in root.Controls) ForAll(c, a);
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        public static void EnableDpiAwareness()
        {
            if (!IsWindows) return;
            try { SetProcessDPIAware(); } catch { }
        }

        // dark title bar (Windows 10 2004+ / 11) and, on Windows 11, a caption coloured like the window
        public static void StyleTitleBar(Form f, Theme t)
        {
            if (!IsWindows || !f.IsHandleCreated) return;
            try
            {
                int dark = t.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref dark, 4);
                int cap = t.Bg.R | (t.Bg.G << 8) | (t.Bg.B << 16);
                DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);
                int txt = t.Text.R | (t.Text.G << 8) | (t.Text.B << 16);
                DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);
                int border = t.Border.R | (t.Border.G << 8) | (t.Border.B << 16);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        // blink the taskbar button until the window is activated
        public static void Flash(Form f)
        {
            if (!IsWindows || !f.IsHandleCreated) return;
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = f.Handle;
                fi.dwFlags = 3 | 12;            // caption + tray, until foreground
                fi.uCount = 5;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string DownloadsFolder()
        {
            if (IsWindows)
            {
                try
                {
                    IntPtr p;
                    if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                    {
                        string s = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
            }
            string home = Environment.GetEnvironmentVariable("EASYLIB_DOWNLOADS");
            if (!string.IsNullOrEmpty(home)) return home;
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static void DarkScrollbars(Control c, bool dark)
        {
            if (!IsWindows || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }
    }
}
