// Writes native Altium Designer libraries (.SchLib / .PcbLib) by appending components to
// existing files (or creating new ones). Record layouts follow files saved by Altium itself;
// binary primitives are built from Altium-saved templates (Templates.cs) with the geometry
// fields overwritten at their known offsets.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class LibResult
    {
        public string LibRef = "", Footprint = "";
        public bool SymbolExisted, FootprintExisted, ModelAdded, NoFootprint;
        public int Repaired;
    }

    static class Bin
    {
        public static readonly Encoding W1252 = Encoding.GetEncoding(1252);
        static readonly Random Rng = new Random();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // names in Altium libraries are ANSI: transliterate Cyrillic (otherwise every letter becomes '?'
        // and two different Russian names can collide), keep what Windows-1252 has
        static readonly string[] Translit = {
            "а","a","б","b","в","v","г","g","д","d","е","e","ё","e","ж","zh","з","z","и","i","й","y","к","k","л","l","м","m",
            "н","n","о","o","п","p","р","r","с","s","т","t","у","u","ф","f","х","kh","ц","ts","ч","ch","ш","sh","щ","shch",
            "ъ","","ы","y","ь","","э","e","ю","yu","я","ya","і","i","ї","yi","є","ye","ґ","g","µ","u","μ","u" };

        public static string Ansi(string s)
        {
            if (s == null) return "";
            s = s.Replace("Ω", "Ohm").Replace("Ω", "Ohm");
            if (!Is1252(s))
            {
                StringBuilder sb = new StringBuilder(s.Length + 8);
                foreach (char ch in s)
                {
                    string low = char.ToLowerInvariant(ch).ToString(), rep = null;
                    for (int i = 0; i < Translit.Length; i += 2) if (Translit[i] == low) { rep = Translit[i + 1]; break; }
                    if (rep == null) sb.Append(ch);
                    else if (char.IsUpper(ch) && rep.Length > 0) sb.Append(char.ToUpperInvariant(rep[0])).Append(rep.Substring(1));
                    else sb.Append(rep);
                }
                s = sb.ToString();
            }
            return W1252.GetString(W1252.GetBytes(s));
        }

        public static bool Is1252(string s)
        {
            return W1252.GetString(W1252.GetBytes(s ?? "")) == (s ?? "");
        }

        // [u32 len | flags<<24][text + NUL]
        public static byte[] CStrBlock(string text, byte flags)
        {
            byte[] t = W1252.GetBytes(text ?? "");
            byte[] r = new byte[4 + t.Length + 1];
            WriteU32(r, 0, (uint)((flags << 24) | (t.Length + 1)));
            Array.Copy(t, 0, r, 4, t.Length);
            return r;
        }

        public static byte[] CStrBlockRaw(byte[] t, byte flags)
        {
            byte[] r = new byte[4 + t.Length + 1];
            WriteU32(r, 0, (uint)((flags << 24) | (t.Length + 1)));
            Array.Copy(t, 0, r, 4, t.Length);
            return r;
        }

        // [u32 len][byte n][n bytes]
        public static byte[] PascalBlock(string s)
        {
            byte[] t = W1252.GetBytes(s ?? "");
            int n = Math.Min(255, t.Length);
            byte[] r = new byte[4 + 1 + n];
            WriteU32(r, 0, (uint)(1 + n));
            r[4] = (byte)n;
            Array.Copy(t, 0, r, 5, n);
            return r;
        }

        public static byte[] Block(byte[] data)
        {
            byte[] r = new byte[4 + data.Length];
            WriteU32(r, 0, (uint)data.Length);
            Array.Copy(data, 0, r, 4, data.Length);
            return r;
        }

        public static string ReadCStrBlock(byte[] d, ref int pos)
        {
            uint len = U32(d, pos) & 0xFFFFFF;
            pos += 4;
            int n = (int)len;
            int end = pos + n;
            int tl = n;
            while (tl > 0 && d[pos + tl - 1] == 0) tl--;
            string s = W1252.GetString(d, pos, tl);
            pos = end;
            return s;
        }

        public static string ReadPascalBlock(byte[] d, ref int pos)
        {
            uint len = U32(d, pos) & 0xFFFFFF;
            pos += 4;
            int n = d[pos];
            string s = W1252.GetString(d, pos + 1, Math.Min(n, (int)len - 1));
            pos += (int)len;
            return s;
        }

        public static List<KeyValuePair<string, string>> ParseParams(string text)
        {
            List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
            foreach (string part in text.Split('|'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 0) r.Add(new KeyValuePair<string, string>(part, ""));
                else r.Add(new KeyValuePair<string, string>(part.Substring(0, eq), part.Substring(eq + 1)));
            }
            return r;
        }

        public static string JoinParams(List<KeyValuePair<string, string>> p, bool leadingBar)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < p.Count; i++)
            {
                if (leadingBar || i > 0) sb.Append('|');
                sb.Append(p[i].Key).Append('=').Append(p[i].Value);
            }
            return sb.ToString();
        }

        public static string Get(List<KeyValuePair<string, string>> p, string key)
        {
            foreach (KeyValuePair<string, string> kv in p)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public static string UniqueId()
        {
            char[] c = new char[8];
            lock (Rng) for (int i = 0; i < 8; i++) c[i] = (char)('A' + Rng.Next(26));
            return new string(c);
        }

        public static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        public static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        public static void WriteI32(byte[] b, int o, int v) { WriteU32(b, o, unchecked((uint)v)); }
        public static void WriteI16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        public static void WriteDouble(byte[] b, int o, double v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 8); }
        public static byte[] U32Bytes(uint v) { byte[] b = new byte[4]; WriteU32(b, 0, v); return b; }

        public static byte[] Concat(List<byte[]> parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            byte[] r = new byte[n];
            int o = 0;
            foreach (byte[] p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        public static byte[] FromB64(string s) { return Convert.FromBase64String(s); }

        public static byte[] Inflate(byte[] zlib)
        {
            using (MemoryStream src = new MemoryStream(zlib, 2, zlib.Length - 2))
            using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream dst = new MemoryStream())
            {
                ds.CopyTo(dst);
                return dst.ToArray();
            }
        }

        public static byte[] ZlibCompress(byte[] data)
        {
            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0xDA);
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                ds.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        public static string Mil(double mil)
        {
            return Math.Round(mil, 4).ToString("0.####", Inv) + "mil";
        }

        public static string F3(double v) { return v.ToString("0.000", Inv); }

        // Characters that are known to survive in Altium storage names. Altium derives the storage
        // name of a part from its name (e.g. it turns '*' and '/' into '_') unless SectionKeys maps it,
        // so anything outside this set is replaced and the mapping is always written.
        public static bool IsSafeKeyChar(char ch)
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) return true;
            return " .-_()[],%~\u00B1".IndexOf(ch) >= 0;
        }

        public static bool IsSafeKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 31) return false;
            foreach (char ch in key) if (!IsSafeKeyChar(ch)) return false;
            return true;
        }

        // Altium storage names: max 31 chars, safe characters only, unique among siblings
        public static string SectionKey(string name, CfbEntry parent)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in name ?? "")
                sb.Append(IsSafeKeyChar(ch) ? ch : '_');
            string key = sb.Length == 0 ? "_" : sb.ToString();
            if (key.Length > 31) key = key.Substring(0, 31);
            string baseKey = key;
            int n = 1;
            while (parent.Get(key) != null)
            {
                string suffix = "~" + n++;
                key = (baseKey.Length + suffix.Length > 31 ? baseKey.Substring(0, 31 - suffix.Length) : baseKey) + suffix;
            }
            return key;
        }
    }

    // ======================================================================
    // PCB library
    // ======================================================================

    static partial class PcbLibWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double Raw = 10000.0;     // internal units per mil

        static int R(double mil) { return (int)Math.Round(mil * Raw); }

        public static int LayerCode(string l)
        {
            switch (l)
            {
                case "T": return 1;
                case "B": return 32;
                case "M": return 74;
                case "TO": return 33;
                case "BO": return 34;
                case "TP": return 35;
                case "BP": return 36;
                case "TM": return 37;
                case "BM": return 38;
                case "ASM": return 56 + 13;
                case "CRT": return 56 + 15;
                default: return 33;
            }
        }

        static string LayerName(int layer)
        {
            switch (layer)
            {
                case 1: return "TOP";
                case 32: return "BOTTOM";
                case 33: return "TOPOVERLAY";
                case 34: return "BOTTOMOVERLAY";
                case 35: return "TOPPASTE";
                case 36: return "BOTTOMPASTE";
                case 37: return "TOPSOLDER";
                case 38: return "BOTTOMSOLDER";
                case 74: return "MULTILAYER";
            }
            if (layer >= 57 && layer <= 72) return "MECHANICAL" + (layer - 56);
            return "TOPOVERLAY";
        }

        static uint V7(int layer)
        {
            if (layer == 32) return 0x0100FFFFu;
            if (layer >= 1 && layer <= 31) return 0x01000000u + (uint)layer;
            if (layer >= 57 && layer <= 72) return 0x01020000u + (uint)(layer - 56);
            switch (layer)
            {
                case 33: return 0x01030006u;
                case 34: return 0x01030007u;
                case 35: return 0x01030008u;
                case 36: return 0x01030009u;
                case 37: return 0x0103000Au;
                case 38: return 0x0103000Bu;
                default: return 0x0103000Fu;
            }
        }

        public static CfbEntry NewLibrary(string path)
        {
            CfbEntry root = new CfbEntry("Root Entry", true);

            // FileHeader: two string blocks around the 5.01 version double
            List<byte[]> fh = new List<byte[]>();
            string ver = "PCB 6.0 Binary Library File";
            fh.Add(Bin.U32Bytes((uint)ver.Length));
            fh.Add(new byte[] { (byte)ver.Length });
            fh.Add(Bin.W1252.GetBytes(ver));
            fh.Add(BitConverter.GetBytes(5.01d));
            string uid = Bin.UniqueId();
            fh.Add(Bin.U32Bytes(8));
            fh.Add(new byte[] { 8 });
            fh.Add(Bin.W1252.GetBytes(uid));
            root.SetStream("FileHeader", Bin.Concat(fh));

            CfbEntry fvi = root.Storage("FileVersionInfo");
            fvi.SetStream("Header", Bin.U32Bytes(1));
            fvi.SetStream("Data", Bin.Inflate(Bin.FromB64(Templates.PcbFileVersionInfo)));

            CfbEntry lib = root.Storage("Library");
            lib.SetStream("Header", Bin.U32Bytes(1));
            string header = Bin.W1252.GetString(Bin.Inflate(Bin.FromB64(Templates.PcbLibDataHeader)));
            header = Regex.Replace(header, "\\|FILENAME=[^|]*", "|FILENAME=" + Bin.Ansi(path).Replace("|", "_"));
            header = Regex.Replace(header, "\\|DATE=[^|]*", "|DATE=" + DateTime.Now.ToString("dd/MM/yyyy", Inv));
            header = Regex.Replace(header, "\\|TIME=[^|]*", "|TIME=" + DateTime.Now.ToString("HH:mm:ss", Inv));
            List<byte[]> ld = new List<byte[]>();
            ld.Add(Bin.CStrBlock(header, 0));
            ld.Add(Bin.U32Bytes(0));
            lib.SetStream("Data", Bin.Concat(ld));
            lib.SetStream("EmbeddedFonts", Bin.FromB64(Templates.PcbEmbeddedFonts));

            CfbEntry lkm = lib.Storage("LayerKindMapping");
            lkm.SetStream("Header", Bin.U32Bytes(1));
            lkm.SetStream("Data", Bin.FromB64(Templates.PcbLayerKindMapping));

            CfbEntry models = lib.Storage("Models");
            models.SetStream("Header", Bin.U32Bytes(0));
            models.SetStream("Data", new byte[0]);

            CfbEntry noEmbed = lib.Storage("ModelsNoEmbed");
            noEmbed.SetStream("Header", Bin.U32Bytes(0));
            noEmbed.SetStream("Data", new byte[0]);

            CfbEntry pvl = lib.Storage("PadViaLibrary");
            pvl.SetStream("Header", Bin.U32Bytes(0));
            string pv = "|PADVIALIBRARY.LIBRARYID={" + Guid.NewGuid().ToString("D").ToUpperInvariant() +
                        "}|PADVIALIBRARY.LIBRARYNAME=<Local>|PADVIALIBRARY.DISPLAYUNITS=1";
            pvl.SetStream("Data", Bin.CStrBlock(pv, 0));

            CfbEntry tex = lib.Storage("Textures");
            tex.SetStream("Header", Bin.U32Bytes(0));
            tex.SetStream("Data", new byte[0]);
            return root;
        }

        public static List<string> FootprintNames(CfbEntry root)
        {
            List<string> names = new List<string>();
            CfbEntry data = root.GetPath("Library/Data");
            if (data == null) return names;
            byte[] d = data.Data;
            int pos = 0;
            Bin.ReadCStrBlock(d, ref pos);
            if (pos + 4 > d.Length) return names;
            uint n = Bin.U32(d, pos);
            pos += 4;
            for (int i = 0; i < n && pos < d.Length; i++) names.Add(Bin.ReadPascalBlock(d, ref pos));
            return names;
        }

        static bool Contains(List<string> list, string s)
        {
            foreach (string x in list) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool HasFootprint(CfbEntry root, string name)
        {
            return Contains(FootprintNames(root), name);
        }

        static CfbEntry FootprintStorage(CfbEntry root, string name)
        {
            CfbEntry sk = root.Get("SectionKeys");
            if (sk != null && !sk.IsStorage && sk.Data != null && sk.Data.Length >= 4)
            {
                byte[] d = sk.Data;
                uint n = Bin.U32(d, 0);
                int pos = 4;
                for (int i = 0; i < n && pos < d.Length; i++)
                {
                    string a = Bin.ReadPascalBlock(d, ref pos);
                    string b = Bin.ReadPascalBlock(d, ref pos);
                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) { CfbEntry e = root.Get(b); if (e != null) return e; }
                }
            }
            string key = name.Length > 31 ? name.Substring(0, 31) : name;
            CfbEntry st = root.Get(key);
            if (st == null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (char ch in key) sb.Append(ch == '/' || ch == '\\' || ch == ':' || ch == '!' || ch < 32 ? '_' : ch);
                st = root.Get(sb.ToString());
            }
            return st != null && st.IsStorage ? st : null;
        }

        // designators of the pads of an existing footprint; null if the footprint can't be parsed
        public static HashSet<string> PadDesignators(CfbEntry root, string name)
        {
            try
            {
                CfbEntry st = FootprintStorage(root, name);
                if (st == null) return null;
                CfbEntry data = st.Get("Data");
                if (data == null || data.Data == null) return null;
                byte[] d = data.Data;
                int pos = 0;
                Bin.ReadPascalBlock(d, ref pos);
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (pos < d.Length)
                {
                    int type = d[pos++];
                    int blocks;
                    switch (type)
                    {
                        case 2: blocks = 6; break;
                        case 5: blocks = 2; break;
                        case 1: case 3: case 4: case 6: case 11: case 12: blocks = 1; break;
                        default: return null;
                    }
                    for (int b = 0; b < blocks; b++)
                    {
                        if (pos + 4 > d.Length) return null;
                        int len = (int)(Bin.U32(d, pos) & 0xFFFFFF);
                        if (type == 2 && b == 0 && len > 0)
                        {
                            int n = d[pos + 4];
                            string des = Bin.W1252.GetString(d, pos + 5, Math.Min(n, len - 1));
                            if (des != "") set.Add(des);
                        }
                        pos += 4 + len;
                    }
                }
                return pos == d.Length ? set : null;
            }
            catch { return null; }
        }

        public static HashSet<string> PadDesignators(Part p)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FPad pad in p.Pads) if (pad.Name != "") set.Add(Bin.Ansi(pad.Name));
            return set;
        }

        public static void AddFootprint(CfbEntry root, Part p, string name, string description, byte[] step, string stepName, ModelPlacement mp)
        {
            // ---- primitives ----
            List<byte[]> prims = new List<byte[]>();
            int count = 0, padCount = 0;
            foreach (FGfx g in p.Gfx)
                if (g.Kind == "ARC") { prims.Add(new byte[] { 1 }); prims.Add(Bin.Block(Arc(g))); count++; }
            foreach (FPad pad in p.Pads)
            {
                prims.Add(new byte[] { 2 });
                prims.Add(Pad(pad));
                count++;
                if (pad.Name != "") padCount++;
            }
            foreach (FGfx g in p.Gfx)
                if (g.Kind == "TRK") { prims.Add(new byte[] { 4 }); prims.Add(Bin.Block(Track(g))); count++; }
            foreach (FGfx g in p.Gfx)
                if (g.Kind == "REG") { prims.Add(new byte[] { 11 }); prims.Add(Bin.Block(Region(LayerCode(g.Layer), g.Pts))); count++; }
            foreach (FPad pad in p.Pads)
            {
                if (pad.Shape != 'P' || pad.Poly == null) continue;
                bool bottom = pad.Layer == "B";
                foreach (string l in new string[] { bottom ? "B" : "T", bottom ? "BM" : "TM", bottom ? "BP" : "TP" })
                {
                    prims.Add(new byte[] { 11 });
                    prims.Add(Bin.Block(Region(LayerCode(l), pad.Poly)));
                    count++;
                }
            }

            string modelId = null;
            uint checksum = 0;
            double height = 0;
            if (step != null && mp != null)
            {
                checksum = Checksum(step);
                modelId = AddModel(root, step, stepName, checksum, mp);
                prims.Add(new byte[] { 12 });
                prims.Add(Bin.Block(Body(modelId, checksum, stepName, mp)));
                count++;
                height = Math.Max(0, mp.TopZ);
            }

            List<byte[]> data = new List<byte[]>();
            data.Add(Bin.PascalBlock(name));
            data.AddRange(prims);

            string key = Bin.SectionKey(name, root);
            CfbEntry st = root.Storage(key);
            st.SetStream("Header", Bin.U32Bytes((uint)count));
            st.SetStream("Parameters", Bin.CStrBlock("|PATTERN=" + Bin.Ansi(name) + "|HEIGHT=" + Bin.Mil(height) +
                                                     "|DESCRIPTION=" + Bin.Ansi(description).Replace("|", "/") + "|ITEMGUID=|REVISIONGUID=", 0));
            st.SetStream("WideStrings", Bin.CStrBlock("", 0));
            st.SetStream("Data", Bin.Concat(data));

            // ---- library index ----
            CfbEntry libData = root.GetPath("Library/Data");
            byte[] d = libData.Data;
            int pos = 0;
            string header = Bin.ReadCStrBlock(d, ref pos);
            List<string> names = FootprintNames(root);
            names.Add(name);
            List<byte[]> nd = new List<byte[]>();
            nd.Add(Bin.CStrBlock(header, 0));
            nd.Add(Bin.U32Bytes((uint)names.Count));
            foreach (string n in names) nd.Add(Bin.PascalBlock(n));
            libData.Data = Bin.Concat(nd);

            // ComponentParamsTOC
            CfbEntry tocSt = root.GetPath("Library").Storage("ComponentParamsTOC");
            tocSt.SetStream("Header", Bin.U32Bytes(1));
            string toc = "";
            CfbEntry tocData = tocSt.Get("Data");
            if (tocData != null && tocData.Data.Length > 4)
            {
                int tp = 0;
                toc = Bin.ReadCStrBlock(tocData.Data, ref tp);
            }
            toc += "Name=" + Bin.Ansi(name) + "|Pad Count=" + padCount.ToString(Inv) + "|Height=" +
                   Math.Round(height, 4).ToString("0.####", Inv) + "|Description=" + Bin.Ansi(description).Replace("|", "/") + "\r\n";
            tocSt.SetStream("Data", Bin.CStrBlock(toc, 0));

            // SectionKeys for names longer than a storage name
            if (key != name) AddSectionKey(root, name, key);
        }

        static List<KeyValuePair<string, string>> ReadSectionKeys(CfbEntry root)
        {
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            CfbEntry sk = root.Get("SectionKeys");
            if (sk != null && !sk.IsStorage && sk.Data != null && sk.Data.Length >= 4)
            {
                byte[] d = sk.Data;
                uint n = Bin.U32(d, 0);
                int pos = 4;
                for (int i = 0; i < n && pos < d.Length; i++)
                {
                    string a = Bin.ReadPascalBlock(d, ref pos);
                    string b = Bin.ReadPascalBlock(d, ref pos);
                    pairs.Add(new KeyValuePair<string, string>(a, b));
                }
            }
            return pairs;
        }

        static void WriteSectionKeys(CfbEntry root, List<KeyValuePair<string, string>> pairs)
        {
            List<byte[]> o = new List<byte[]>();
            o.Add(Bin.U32Bytes((uint)pairs.Count));
            foreach (KeyValuePair<string, string> kv in pairs) { o.Add(Bin.PascalBlock(kv.Key)); o.Add(Bin.PascalBlock(kv.Value)); }
            root.SetStream("SectionKeys", Bin.Concat(o));
        }

        // EasyLib 2.0/2.1 kept characters such as '*' in storage names without a SectionKeys entry;
        // Altium then looks for a different name and refuses the whole library. Rename such storages.
        public static int RepairStorageNames(CfbEntry root)
        {
            List<KeyValuePair<string, string>> pairs = ReadSectionKeys(root);
            int fixedCount = 0;
            foreach (string name in FootprintNames(root))
            {
                int idx = -1;
                for (int i = 0; i < pairs.Count; i++) if (string.Equals(pairs[i].Key, name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                string cur = idx >= 0 ? pairs[idx].Value : (name.Length > 31 ? name.Substring(0, 31) : name);
                if (Bin.IsSafeKey(cur)) continue;
                CfbEntry st = root.Get(cur);
                if (st == null || !st.IsStorage) continue;
                string nk = Bin.SectionKey(name, root);
                st.Name = nk;
                KeyValuePair<string, string> kv = new KeyValuePair<string, string>(name, nk);
                if (idx >= 0) pairs[idx] = kv; else pairs.Add(kv);
                fixedCount++;
            }
            if (fixedCount > 0) WriteSectionKeys(root, pairs);
            return fixedCount;
        }

        static void AddSectionKey(CfbEntry root, string name, string key)
        {
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            CfbEntry sk = root.Get("SectionKeys");
            if (sk != null && sk.Data.Length >= 4)
            {
                byte[] d = sk.Data;
                uint n = Bin.U32(d, 0);
                int pos = 4;
                for (int i = 0; i < n && pos < d.Length; i++)
                {
                    string a = Bin.ReadPascalBlock(d, ref pos);
                    string b = Bin.ReadPascalBlock(d, ref pos);
                    pairs.Add(new KeyValuePair<string, string>(a, b));
                }
            }
            pairs.Add(new KeyValuePair<string, string>(name, key));
            List<byte[]> o = new List<byte[]>();
            o.Add(Bin.U32Bytes((uint)pairs.Count));
            foreach (KeyValuePair<string, string> kv in pairs) { o.Add(Bin.PascalBlock(kv.Key)); o.Add(Bin.PascalBlock(kv.Value)); }
            root.SetStream("SectionKeys", Bin.Concat(o));
        }

        public static CfbEntry FootprintStorageOf(CfbEntry root, string name) { return FootprintStorage(root, name); }

        // primitives of a footprint: (type, bytes of the whole record incl. the type byte); null if unknown records
        static List<KeyValuePair<int, byte[]>> Primitives(byte[] d, out byte[] nameBlock)
        {
            nameBlock = null;
            int pos = 0;
            Bin.ReadPascalBlock(d, ref pos);
            nameBlock = new byte[pos];
            Buffer.BlockCopy(d, 0, nameBlock, 0, pos);
            List<KeyValuePair<int, byte[]>> r = new List<KeyValuePair<int, byte[]>>();
            while (pos < d.Length)
            {
                int start = pos, type = d[pos++];
                int blocks;
                switch (type)
                {
                    case 2: blocks = 6; break;
                    case 5: blocks = 2; break;
                    case 1: case 3: case 4: case 6: case 11: case 12: blocks = 1; break;
                    default: return null;
                }
                for (int b = 0; b < blocks; b++)
                {
                    if (pos + 4 > d.Length) return null;
                    pos += 4 + (int)(Bin.U32(d, pos) & 0xFFFFFF);
                    if (pos > d.Length) return null;
                }
                byte[] rec = new byte[pos - start];
                Buffer.BlockCopy(d, start, rec, 0, rec.Length);
                r.Add(new KeyValuePair<int, byte[]>(type, rec));
            }
            return r;
        }

        // ids of the embedded 3D models a footprint uses
        public static List<string> FootprintModelIds(CfbEntry root, string name)
        {
            List<string> ids = new List<string>();
            CfbEntry st = FootprintStorage(root, name);
            CfbEntry data = st != null ? st.Get("Data") : null;
            if (data == null || data.Data == null) return ids;
            byte[] nb;
            List<KeyValuePair<int, byte[]>> prims = Primitives(data.Data, out nb);
            if (prims == null)
            {
                // unknown records: still find the ids in the raw bytes
                foreach (Match m in Regex.Matches(Bin.W1252.GetString(data.Data), "MODELID=(\\{[0-9A-Fa-f-]+\\})")) if (!ids.Contains(m.Groups[1].Value.ToUpperInvariant())) ids.Add(m.Groups[1].Value.ToUpperInvariant());
                return ids;
            }
            foreach (KeyValuePair<int, byte[]> p in prims)
            {
                if (p.Key != 12) continue;
                Match m = Regex.Match(Bin.W1252.GetString(p.Value), "MODELID=(\\{[0-9A-Fa-f-]+\\})");
                if (m.Success && !ids.Contains(m.Groups[1].Value.ToUpperInvariant())) ids.Add(m.Groups[1].Value.ToUpperInvariant());
            }
            return ids;
        }

        public static long FootprintBytes(CfbEntry root, string name)
        {
            CfbEntry st = FootprintStorage(root, name);
            long n = 0;
            if (st != null) foreach (CfbEntry c in st.Children) if (!c.IsStorage && c.Data != null) n += c.Data.Length;
            return n;
        }

        public class ModelInfo
        {
            public int Index;
            public string Id = "", Name = "";
            public long Bytes;
        }

        public static List<ModelInfo> Models(CfbEntry root)
        {
            List<ModelInfo> list = new List<ModelInfo>();
            CfbEntry models = root.GetPath("Library/Models");
            if (models == null) return list;
            CfbEntry data = models.Get("Data");
            byte[] d = data != null && data.Data != null ? data.Data : new byte[0];
            int pos = 0, i = 0;
            while (pos + 4 <= d.Length)
            {
                List<KeyValuePair<string, string>> kv = Bin.ParseParams(Bin.ReadCStrBlock(d, ref pos));
                ModelInfo m = new ModelInfo();
                m.Index = i;
                m.Id = (Bin.Get(kv, "ID") ?? "").ToUpperInvariant();
                m.Name = Bin.Get(kv, "NAME") ?? "";
                CfbEntry s = models.Get(i.ToString(Inv));
                m.Bytes = s != null && s.Data != null ? s.Data.Length : 0;
                list.Add(m);
                i++;
            }
            return list;
        }

        // removes the given footprints from the library index, TOC, section keys and storage
        public static int DeleteFootprints(CfbEntry root, ICollection<string> names)
        {
            HashSet<string> del = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            List<string> all = FootprintNames(root);
            int removed = 0;
            foreach (string n in all)
            {
                if (!del.Contains(n)) continue;
                CfbEntry st = FootprintStorage(root, n);
                if (st != null) root.Remove(st.Name);
                removed++;
            }
            if (removed == 0) return 0;
            CfbEntry libData = root.GetPath("Library/Data");
            byte[] d = libData.Data;
            int pos = 0;
            string header = Bin.ReadCStrBlock(d, ref pos);
            List<byte[]> nd = new List<byte[]>();
            nd.Add(Bin.CStrBlock(header, 0));
            List<string> keep = new List<string>();
            foreach (string n in all) if (!del.Contains(n)) keep.Add(n);
            nd.Add(Bin.U32Bytes((uint)keep.Count));
            foreach (string n in keep) nd.Add(Bin.PascalBlock(n));
            libData.Data = Bin.Concat(nd);

            CfbEntry tocSt = root.GetPath("Library/ComponentParamsTOC");
            CfbEntry tocData = tocSt != null ? tocSt.Get("Data") : null;
            if (tocData != null && tocData.Data != null && tocData.Data.Length > 4)
            {
                int tp = 0;
                string toc = Bin.ReadCStrBlock(tocData.Data, ref tp);
                StringBuilder sb = new StringBuilder();
                foreach (string line in toc.Split(new string[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line == "") continue;
                    Match m = Regex.Match(line, "^Name=([^|]*)");
                    if (m.Success && del.Contains(m.Groups[1].Value)) continue;
                    sb.Append(line).Append("\r\n");
                }
                tocSt.SetStream("Data", Bin.CStrBlock(sb.ToString(), 0));
            }
            List<KeyValuePair<string, string>> pairs = ReadSectionKeys(root);
            int before = pairs.Count;
            pairs.RemoveAll(delegate(KeyValuePair<string, string> kv) { return del.Contains(kv.Key); });
            if (pairs.Count != before) WriteSectionKeys(root, pairs);
            return removed;
        }

        // removes the 3D bodies from footprints; returns the number of footprints changed
        public static int StripBodies(CfbEntry root, ICollection<string> names)
        {
            int changed = 0;
            foreach (string n in names)
            {
                CfbEntry st = FootprintStorage(root, n);
                CfbEntry data = st != null ? st.Get("Data") : null;
                if (data == null || data.Data == null) continue;
                byte[] nb;
                List<KeyValuePair<int, byte[]>> prims = Primitives(data.Data, out nb);
                if (prims == null) continue;
                List<byte[]> o = new List<byte[]>();
                o.Add(nb);
                int kept = 0, dropped = 0;
                foreach (KeyValuePair<int, byte[]> p in prims)
                {
                    if (p.Key == 12) { dropped++; continue; }
                    o.Add(p.Value);
                    kept++;
                }
                if (dropped == 0) continue;
                st.SetStream("Data", Bin.Concat(o));
                st.SetStream("Header", Bin.U32Bytes((uint)kept));
                changed++;
            }
            return changed;
        }

        // embedded models no footprint refers to are removed and the rest renumbered
        public static int PruneModels(CfbEntry root)
        {
            CfbEntry models = root.GetPath("Library/Models");
            if (models == null) return 0;
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in FootprintNames(root)) foreach (string id in FootprintModelIds(root, n)) used.Add(id);
            List<ModelInfo> list = Models(root);
            CfbEntry data = models.Get("Data");
            byte[] d = data != null && data.Data != null ? data.Data : new byte[0];
            List<byte[]> blocks = new List<byte[]>();
            int pos = 0;
            while (pos + 4 <= d.Length)
            {
                int start = pos;
                Bin.ReadCStrBlock(d, ref pos);
                byte[] b = new byte[pos - start];
                Buffer.BlockCopy(d, start, b, 0, b.Length);
                blocks.Add(b);
            }
            if (blocks.Count != list.Count) return 0;
            List<byte[]> keepBlocks = new List<byte[]>(), keepStreams = new List<byte[]>();
            int removed = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                CfbEntry s = models.Get(i.ToString(Inv));
                // unused, or a second copy of the same model (bodies find models by id)
                if (list[i].Id != "" && (!used.Contains(list[i].Id) || !seen.Add(list[i].Id))) { removed++; continue; }
                keepBlocks.Add(blocks[i]);
                keepStreams.Add(s != null ? s.Data : new byte[0]);
            }
            if (removed == 0) return 0;
            for (int i = 0; i < list.Count; i++) models.Remove(i.ToString(Inv));
            for (int i = 0; i < keepStreams.Count; i++) models.SetStream(i.ToString(Inv), keepStreams[i]);
            models.SetStream("Data", Bin.Concat(keepBlocks));
            models.SetStream("Header", Bin.U32Bytes((uint)keepStreams.Count));
            return removed;
        }

        public static uint Checksum(byte[] step)
        {
            uint c = 0;
            for (int i = 0; i < step.Length; i++) c = unchecked(c + (uint)step[i] * (i == 0 ? 1u : (uint)i));
            return c;
        }

        static string AddModel(CfbEntry root, byte[] step, string stepName, uint checksum, ModelPlacement mp)
        {
            CfbEntry models = root.GetPath("Library").Storage("Models");
            CfbEntry hdr = models.Get("Header");
            uint n = hdr != null && hdr.Data.Length >= 4 ? Bin.U32(hdr.Data, 0) : 0;
            byte[] old = models.Get("Data") != null ? models.Get("Data").Data : new byte[0];

            // reuse an identical model already stored in this library
            int pos = 0;
            while (pos + 4 <= old.Length)
            {
                List<KeyValuePair<string, string>> kv = Bin.ParseParams(Bin.ReadCStrBlock(old, ref pos));
                if (Bin.Get(kv, "CHECKSUM") == unchecked((int)checksum).ToString(Inv) && Bin.Get(kv, "NAME") == Bin.Ansi(stepName))
                    return Bin.Get(kv, "ID");
            }

            string id = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
            string text = "EMBED=TRUE|MODELSOURCE=Undefined|ID=" + id +
                          "|ROTX=" + Bin.F3(Norm(mp.RotX)) + "|ROTY=" + Bin.F3(Norm(mp.RotY)) + "|ROTZ=" + Bin.F3(Norm(mp.RotZ)) +
                          "|DZ=0|CHECKSUM=" + unchecked((int)checksum).ToString(Inv) + "|NAME=" + Bin.Ansi(stepName);
            List<byte[]> nd = new List<byte[]>();
            nd.Add(old);
            nd.Add(Bin.CStrBlock(text, 0));
            models.SetStream("Data", Bin.Concat(nd));
            models.SetStream(n.ToString(Inv), Bin.ZlibCompress(step));
            models.SetStream("Header", Bin.U32Bytes(n + 1));
            return id;
        }

        static double Norm(double a)
        {
            a = a % 360.0;
            if (a < 0) a += 360.0;
            if (a >= 359.9995) a = 0;
            return a;
        }

        // ---------------- primitives ----------------

        static void Header(byte[] b, int layer)
        {
            b[0] = (byte)layer;
        }

        static byte[] Track(FGfx g)
        {
            byte[] b = Bin.FromB64(Templates.Track);
            int layer = LayerCode(g.Layer);
            Header(b, layer);
            Bin.WriteI32(b, 13, R(g.Pts[0].X));
            Bin.WriteI32(b, 17, R(g.Pts[0].Y));
            Bin.WriteI32(b, 21, R(g.Pts[1].X));
            Bin.WriteI32(b, 25, R(g.Pts[1].Y));
            Bin.WriteI32(b, 29, R(Math.Max(g.W, 1)));
            Bin.WriteU32(b, 41, V7(layer));
            return b;
        }

        static byte[] Arc(FGfx g)
        {
            byte[] b = Bin.FromB64(Templates.Arc);
            int layer = LayerCode(g.Layer);
            Header(b, layer);
            Bin.WriteI32(b, 13, R(g.CX));
            Bin.WriteI32(b, 17, R(g.CY));
            Bin.WriteI32(b, 21, R(g.R));
            double a1 = g.A1, a2 = g.A2;
            if (a2 >= 359.999 && a1 == 0) a2 = 360;
            Bin.WriteDouble(b, 25, a1);
            Bin.WriteDouble(b, 33, a2);
            Bin.WriteI32(b, 41, R(Math.Max(g.W, 1)));
            Bin.WriteU32(b, 52, V7(layer));
            return b;
        }

        static byte[] Region(int layer, List<PointD> pts)
        {
            List<byte[]> o = new List<byte[]>();
            byte[] h = new byte[18];
            h[0] = (byte)layer;
            h[1] = 0x0C; h[2] = 0x00;
            for (int i = 3; i < 13; i++) h[i] = 0xFF;
            o.Add(h);
            o.Add(Bin.CStrBlock("V7_LAYER=" + LayerName(layer) + "|NAME= |KIND=0|SUBPOLYINDEX=-1|UNIONINDEX=0|ARCRESOLUTION=0.5mil|ISSHAPEBASED=FALSE|CAVITYHEIGHT=0mil", 0));
            List<PointD> v = new List<PointD>(pts);
            if (v.Count > 1 && Math.Abs(v[0].X - v[v.Count - 1].X) < 1e-6 && Math.Abs(v[0].Y - v[v.Count - 1].Y) < 1e-6) v.RemoveAt(v.Count - 1);
            byte[] g = new byte[4 + v.Count * 16];
            Bin.WriteU32(g, 0, (uint)v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                Bin.WriteDouble(g, 4 + i * 16, Math.Round(v[i].X * Raw));
                Bin.WriteDouble(g, 12 + i * 16, Math.Round(v[i].Y * Raw));
            }
            o.Add(g);
            return Bin.Concat(o);
        }

        static byte[] Body(string modelId, uint checksum, string stepName, ModelPlacement mp)
        {
            List<byte[]> o = new List<byte[]>();
            byte[] h = new byte[18];
            h[0] = 57;   // Mechanical 1
            h[1] = 0x0C; h[2] = 0x00;
            for (int i = 3; i < 13; i++) h[i] = 0xFF;
            o.Add(h);
            double standoff = Math.Max(0, mp.BottomZ);
            double overall = Math.Max(mp.TopZ, standoff);
            string t = "V7_LAYER=MECHANICAL1|NAME= |KIND=0|SUBPOLYINDEX=-1|UNIONINDEX=0|ARCRESOLUTION=0.5mil|ISSHAPEBASED=FALSE|CAVITYHEIGHT=0mil" +
                       "|STANDOFFHEIGHT=" + Bin.Mil(standoff) + "|OVERALLHEIGHT=" + Bin.Mil(overall) +
                       "|BODYPROJECTION=0|ARCRESOLUTION=0.5mil|BODYCOLOR3D=8421504|BODYOPACITY3D=1.000|IDENTIFIER=|TEXTURE=" +
                       "|TEXTURECENTERX=0mil|TEXTURECENTERY=0mil|TEXTURESIZEX=0mil|TEXTURESIZEY=0mil|TEXTUREROTATION= 0.00000000000000E+0000" +
                       "|MODELID=" + modelId + "|MODEL.CHECKSUM=" + checksum.ToString(Inv) + "|MODEL.EMBED=TRUE|MODEL.NAME=" + Bin.Ansi(stepName) +
                       "|MODEL.2D.X=" + Bin.Mil(mp.OriginX) + "|MODEL.2D.Y=" + Bin.Mil(mp.OriginY) + "|MODEL.2D.ROTATION=0.000" +
                       "|MODEL.3D.ROTX=" + Bin.F3(Norm(mp.RotX)) + "|MODEL.3D.ROTY=" + Bin.F3(Norm(mp.RotY)) + "|MODEL.3D.ROTZ=" + Bin.F3(Norm(mp.RotZ)) +
                       "|MODEL.3D.DZ=" + Bin.Mil(mp.Dz) + "|MODEL.MODELTYPE=1|MODEL.MODELSOURCE=Undefined";
            o.Add(Bin.CStrBlock(t, 0));
            double hx = Math.Max(mp.SizeX, 1) / 2, hy = Math.Max(mp.SizeY, 1) / 2;
            double[,] c = { { mp.CenterX + hx, mp.CenterY + hy }, { mp.CenterX - hx, mp.CenterY + hy }, { mp.CenterX - hx, mp.CenterY - hy }, { mp.CenterX + hx, mp.CenterY - hy } };
            byte[] g = new byte[4 + 4 * 16];
            Bin.WriteU32(g, 0, 4);
            for (int i = 0; i < 4; i++)
            {
                Bin.WriteDouble(g, 4 + i * 16, Math.Round(c[i, 0] * Raw));
                Bin.WriteDouble(g, 12 + i * 16, Math.Round(c[i, 1] * Raw));
            }
            o.Add(g);
            return Bin.Concat(o);
        }

        static byte ShapeCode(char s)
        {
            return (byte)(s == 'C' || s == 'O' ? 1 : 2);   // 1 round (obround when X != Y), 2 rectangular
        }

        static byte[] Pad(FPad pad)
        {
            bool th = pad.Layer == "M" || pad.Hole > 0;
            bool slot = th && pad.SlotLen > pad.Hole + 0.01;
            int layer = th ? 74 : (pad.Layer == "B" ? 32 : 1);
            byte[] main = Bin.FromB64(slot ? Templates.PadSlotMain : th ? Templates.PadThMain : Templates.PadSmdMain);
            byte shape = ShapeCode(pad.Shape);
            int sx = R(pad.SX), sy = R(pad.SY);
            main[0] = (byte)layer;
            Bin.WriteI32(main, 13, R(pad.X));
            Bin.WriteI32(main, 17, R(pad.Y));
            for (int i = 0; i < 3; i++)
            {
                Bin.WriteI32(main, 21 + i * 8, sx);
                Bin.WriteI32(main, 25 + i * 8, sy);
                main[49 + i] = shape;
            }
            Bin.WriteI32(main, 45, th ? R(pad.Hole) : 0);
            Bin.WriteDouble(main, 52, Norm(pad.Rot));
            main[60] = (byte)(th && pad.Plated ? 1 : 0);
            Bin.WriteU32(main, 114, V7(layer));
            Array.Copy(Guid.NewGuid().ToByteArray(), 0, main, 126, 16);
            Array.Copy(Guid.NewGuid().ToByteArray(), 0, main, 142, 16);

            byte[] ss = new byte[0];
            if (slot)
            {
                ss = Bin.FromB64(Templates.PadSlotSizeShape);
                for (int i = 0; i < 29; i++)
                {
                    Bin.WriteI32(ss, i * 4, sx);
                    Bin.WriteI32(ss, 116 + i * 4, sy);
                    ss[232 + i] = shape;
                }
                ss[262] = 2;                                   // slot hole
                Bin.WriteI32(ss, 263, R(pad.SlotLen));         // slot length
                Bin.WriteDouble(ss, 267, Norm(pad.SlotRot - pad.Rot));   // relative to the pad
                ss[640] = shape;                               // full-stack entry
                Bin.WriteI32(ss, 641, sx);
                Bin.WriteI32(ss, 645, sy);
            }

            List<byte[]> o = new List<byte[]>();
            o.Add(Bin.PascalBlock(pad.Name));
            o.Add(Bin.PascalBlock(""));
            o.Add(Bin.PascalBlock("|&|0"));
            o.Add(Bin.Block(new byte[] { 0 }));
            o.Add(Bin.Block(main));
            o.Add(Bin.Block(ss));
            return Bin.Concat(o);
        }
    }

    // ======================================================================
    // Schematic library
    // ======================================================================

    static partial class SchLibWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int Body = 11599871, Line = 128, Text = 8388608, LineFill = 128;

        public static CfbEntry NewLibrary()
        {
            CfbEntry root = new CfbEntry("Root Entry", true);
            string prefix = Regex.Replace(Templates.SchHeaderPrefix, "\\|UniqueID=[A-Z]+", "|UniqueID=" + Bin.UniqueId());
            prefix = Regex.Replace(prefix, "\\|Weight=\\d+", "|Weight=1");
            root.SetStream("FileHeader", Bin.CStrBlock(prefix + "|CompCount=0", 0));
            root.SetStream("Storage", Bin.FromB64(Templates.SchStorage));
            return root;
        }

        class Comp
        {
            public string LibRef, Descr, PartCount;
        }

        static List<KeyValuePair<string, string>> HeaderParams(CfbEntry root, out List<Comp> comps)
        {
            int pos = 0;
            List<KeyValuePair<string, string>> p = Bin.ParseParams(Bin.ReadCStrBlock(root.Get("FileHeader").Data, ref pos));
            comps = new List<Comp>();
            string cc = Bin.Get(p, "CompCount");
            int n = 0;
            int.TryParse(cc ?? "0", NumberStyles.Integer, Inv, out n);
            for (int i = 0; i < n; i++)
            {
                Comp c = new Comp();
                c.LibRef = Bin.Get(p, "LibRef" + i) ?? "";
                c.Descr = Bin.Get(p, "CompDescr" + i);
                c.PartCount = Bin.Get(p, "PartCount" + i) ?? "2";
                comps.Add(c);
            }
            return p;
        }

        public static List<string> SymbolNames(CfbEntry root)
        {
            List<Comp> comps;
            HeaderParams(root, out comps);
            List<string> r = new List<string>();
            foreach (Comp c in comps) r.Add(c.LibRef);
            return r;
        }

        public static bool HasSymbol(CfbEntry root, string libRef)
        {
            foreach (string s in SymbolNames(root))
                if (string.Equals(s, libRef, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // storage holding a symbol (SectionKeys map long or unusual names to storage names)
        public static string SymbolStorageKey(CfbEntry root, string libRef)
        {
            CfbEntry ske = root.Get("SectionKeys");
            if (ske != null && !ske.IsStorage && ske.Data != null && ske.Data.Length > 4)
            {
                int sp = 0;
                List<KeyValuePair<string, string>> raw = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp));
                int kc = 0;
                int.TryParse(Bin.Get(raw, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                for (int i = 0; i < kc; i++)
                    if (string.Equals(Bin.Get(raw, "LibRef" + i), libRef, StringComparison.OrdinalIgnoreCase)) return Bin.Get(raw, "SectionKey" + i);
            }
            return libRef.Length > 31 ? libRef.Substring(0, 31) : libRef;
        }

        public static string SymbolDescription(CfbEntry root, string libRef)
        {
            List<Comp> comps;
            HeaderParams(root, out comps);
            foreach (Comp c in comps) if (string.Equals(c.LibRef, libRef, StringComparison.OrdinalIgnoreCase)) return c.Descr ?? "";
            return "";
        }

        public static long SymbolBytes(CfbEntry root, string libRef)
        {
            CfbEntry st = root.Get(SymbolStorageKey(root, libRef));
            CfbEntry d = st != null ? st.Get("Data") : null;
            return d != null && d.Data != null ? d.Data.Length : 0;
        }

        // removes symbols with their storages, header entries and section keys; returns the number removed
        public static int DeleteSymbols(CfbEntry root, ICollection<string> libRefs)
        {
            HashSet<string> del = new HashSet<string>(libRefs, StringComparer.OrdinalIgnoreCase);
            List<Comp> comps;
            List<KeyValuePair<string, string>> hp = HeaderParams(root, out comps);
            int removed = 0, removedRecords = 0;
            List<Comp> keep = new List<Comp>();
            foreach (Comp c in comps)
            {
                if (!del.Contains(c.LibRef)) { keep.Add(c); continue; }
                string key = SymbolStorageKey(root, c.LibRef);
                CfbEntry st = root.Get(key);
                if (st != null && st.IsStorage)
                {
                    CfbEntry d = st.Get("Data");
                    if (d != null && d.Data != null)
                        for (int pos = 0; pos + 4 <= d.Data.Length; removedRecords++) pos += 4 + (int)(BitConverter.ToUInt32(d.Data, pos) & 0xFFFFFF);
                    root.Remove(key);
                }
                removed++;
            }
            if (removed == 0) return 0;
            List<KeyValuePair<string, string>> outp = new List<KeyValuePair<string, string>>();
            int weight = 1;
            foreach (KeyValuePair<string, string> kv in hp)
            {
                string k = kv.Key;
                if (string.Equals(k, "Weight", StringComparison.OrdinalIgnoreCase)) { int.TryParse(kv.Value, NumberStyles.Integer, Inv, out weight); continue; }
                if (string.Equals(k, "CompCount", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(k, "^(LibRef|CompDescr|PartCount)\\d+$", RegexOptions.IgnoreCase)) continue;
                outp.Add(kv);
            }
            weight = Math.Max(1, weight - removedRecords);
            outp.Insert(Math.Min(1, outp.Count), new KeyValuePair<string, string>("Weight", weight.ToString(Inv)));
            Add(outp, "CompCount", keep.Count.ToString(Inv));
            for (int i = 0; i < keep.Count; i++)
            {
                Add(outp, "LibRef" + i, keep[i].LibRef);
                if (!string.IsNullOrEmpty(keep[i].Descr)) Add(outp, "CompDescr" + i, keep[i].Descr);
                Add(outp, "PartCount" + i, keep[i].PartCount);
            }
            root.SetStream("FileHeader", Bin.CStrBlock(Bin.JoinParams(outp, true), 0));

            CfbEntry ske = root.Get("SectionKeys");
            if (ske != null && !ske.IsStorage && ske.Data != null && ske.Data.Length > 4)
            {
                int sp = 0;
                List<KeyValuePair<string, string>> raw = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp));
                int kc = 0;
                int.TryParse(Bin.Get(raw, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < kc; i++)
                {
                    string a = Bin.Get(raw, "LibRef" + i), b = Bin.Get(raw, "SectionKey" + i);
                    if (a != null && b != null && !del.Contains(a)) pairs.Add(new KeyValuePair<string, string>(a, b));
                }
                List<KeyValuePair<string, string>> nsk = new List<KeyValuePair<string, string>>();
                Add(nsk, "KeyCount", pairs.Count.ToString(Inv));
                for (int i = 0; i < pairs.Count; i++) { Add(nsk, "LibRef" + i, pairs[i].Key); Add(nsk, "SectionKey" + i, pairs[i].Value); }
                root.SetStream("SectionKeys", Bin.CStrBlock(Bin.JoinParams(nsk, true), 0));
            }
            return removed;
        }

        // footprint linked to an existing symbol ("" if none, null if it can't be read)
        public static string SymbolFootprint(CfbEntry root, string libRef)
        {
            try
            {
                string key = null;
                CfbEntry ske = root.Get("SectionKeys");
                if (ske != null && !ske.IsStorage && ske.Data != null && ske.Data.Length > 4)
                {
                    int sp = 0;
                    List<KeyValuePair<string, string>> raw = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp));
                    int kc = 0;
                    int.TryParse(Bin.Get(raw, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                    for (int i = 0; i < kc && key == null; i++)
                        if (string.Equals(Bin.Get(raw, "LibRef" + i), libRef, StringComparison.OrdinalIgnoreCase)) key = Bin.Get(raw, "SectionKey" + i);
                }
                if (key == null) key = libRef.Length > 31 ? libRef.Substring(0, 31) : libRef;
                CfbEntry st = root.Get(key);
                CfbEntry data = st != null ? st.Get("Data") : null;
                if (data == null || data.Data == null) return null;
                byte[] d = data.Data;
                int pos = 0;
                while (pos + 4 <= d.Length)
                {
                    uint h = BitConverter.ToUInt32(d, pos);
                    int len = (int)(h & 0xFFFFFF), flags = (int)(h >> 24);
                    if (pos + 4 + len > d.Length) break;
                    if (flags != 1)
                    {
                        string t = Bin.W1252.GetString(d, pos + 4, len);
                        if (t.IndexOf("|RECORD=45|", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Match m = Regex.Match(t, "\\|(?:%UTF8%)?ModelName=([^|\\x00]*)", RegexOptions.IgnoreCase);
                            if (m.Success) return m.Groups[1].Value;
                        }
                    }
                    pos += 4 + len;
                }
                return "";
            }
            catch { return null; }
        }

        // ---- coordinate helpers (DXP unit = 10 mil, _Frac = 1/100000 DXP unit) ----

        static void Coord(List<KeyValuePair<string, string>> p, string key, double mil)
        {
            long raw = (long)Math.Round(mil * 10000.0);
            long dxp = raw / 100000;
            long frac = raw % 100000;
            if (dxp != 0) p.Add(new KeyValuePair<string, string>(key, dxp.ToString(Inv)));
            if (frac != 0) p.Add(new KeyValuePair<string, string>(key + "_Frac", frac.ToString(Inv)));
        }

        static void Add(List<KeyValuePair<string, string>> p, string k, string v)
        {
            p.Add(new KeyValuePair<string, string>(k, v));
        }

        static void AddText(List<KeyValuePair<string, string>> p, string k, string v)
        {
            v = (v ?? "").Replace("|", "/");
            if (k != "Text") { Add(p, k, Bin.Ansi(v)); return; }
            if (Bin.Is1252(v)) Add(p, k, v);
            else Add(p, "%UTF8%" + k, Bin.W1252.GetString(Encoding.UTF8.GetBytes(v)));
        }

        static byte[] Rec(List<KeyValuePair<string, string>> p)
        {
            return Bin.CStrBlock(Bin.JoinParams(p, true), 0);
        }

        static List<KeyValuePair<string, string>> NewRec(int record)
        {
            List<KeyValuePair<string, string>> p = new List<KeyValuePair<string, string>>();
            Add(p, "RECORD", record.ToString(Inv));
            return p;
        }

        static void Owner(List<KeyValuePair<string, string>> p, int unit)
        {
            Add(p, "IsNotAccesible", "T");
            Add(p, "OwnerPartId", Math.Max(1, unit).ToString(Inv));
        }

        static byte[] PinRecord(SPin pin)
        {
            List<byte> b = new List<byte>();
            b.AddRange(BitConverter.GetBytes(2));
            b.Add(0);
            b.AddRange(BitConverter.GetBytes((short)Math.Max(1, pin.Unit)));
            b.Add(0);
            b.Add((byte)(pin.Clock ? 3 : 0));      // inner edge: clock
            b.Add((byte)(pin.Dot ? 1 : 0));        // outer edge: dot (inverted)
            b.Add(0);                              // inside
            b.Add(0);                              // outside
            b.Add(0);                              // description ""
            b.Add(1);                              // formal type
            int elec;
            switch (pin.Elec)
            {
                case 1: elec = 0; break;           // input
                case 2: elec = 2; break;           // output
                case 3: elec = 1; break;           // I/O
                case 4: elec = 7; break;           // power
                case 5: elec = 3; break;           // open collector
                case 6: elec = 5; break;           // hi-Z (tri-state)
                case 7: elec = 6; break;           // open emitter
                default: elec = 4; break;          // passive
            }
            b.Add((byte)elec);
            int cong = pin.Orient & 3;
            if (pin.Hidden) cong |= 0x04;
            if (pin.NameVis) cong |= 0x08;
            if (pin.NumVis) cong |= 0x10;
            cong |= 0x20;
            b.Add((byte)cong);
            b.AddRange(BitConverter.GetBytes((short)Math.Round(pin.Len / 10.0)));
            b.AddRange(BitConverter.GetBytes((short)Math.Round(pin.X / 10.0)));
            b.AddRange(BitConverter.GetBytes((short)Math.Round(pin.Y / 10.0)));
            b.AddRange(BitConverter.GetBytes(0));  // colour
            Pascal(b, pin.Name);
            Pascal(b, pin.Num);
            Pascal(b, "");
            Pascal(b, "|&|");
            Pascal(b, "");
            byte[] arr = b.ToArray();
            byte[] r = new byte[4 + arr.Length];
            Bin.WriteU32(r, 0, (uint)((1 << 24) | arr.Length));
            Array.Copy(arr, 0, r, 4, arr.Length);
            return r;
        }

        static void Pascal(List<byte> b, string s)
        {
            byte[] t = Bin.W1252.GetBytes(Bin.Ansi(s ?? ""));
            int n = Math.Min(255, t.Length);
            b.Add((byte)n);
            for (int i = 0; i < n; i++) b.Add(t[i]);
        }

        public static void AddSymbol(CfbEntry root, Part p, string libRef, string footprint)
        {
            List<byte[]> recs = new List<byte[]>();

            // body bounds (for designator / comment placement)
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (SShape s in p.Shapes)
                foreach (PointD q in s.Pts)
                {
                    minx = Math.Min(minx, q.X); maxx = Math.Max(maxx, q.X);
                    miny = Math.Min(miny, q.Y); maxy = Math.Max(maxy, q.Y);
                }
            if (minx > maxx) { minx = miny = -100; maxx = maxy = 100; }

            int pinCount = 0;
            foreach (SPin pin in p.Pins) pinCount++;

            List<KeyValuePair<string, string>> c = NewRec(1);
            AddText(c, "LibReference", libRef);
            if (!string.IsNullOrEmpty(p.Desc)) AddText(c, "ComponentDescription", p.Desc);
            Add(c, "PartCount", (p.PartCount + 1).ToString(Inv));
            Add(c, "DisplayModeCount", "1");
            Add(c, "IndexInSheet", "-1");
            Add(c, "OwnerPartId", "-1");
            Add(c, "CurrentPartId", "1");
            Add(c, "LibraryPath", "*");
            Add(c, "SourceLibraryName", "*");
            Add(c, "SheetPartFileName", "*");
            Add(c, "TargetFileName", "*");
            Add(c, "UniqueID", Bin.UniqueId());
            Add(c, "AreaColor", Body.ToString(Inv));
            Add(c, "Color", Line.ToString(Inv));
            Add(c, "PartIDLocked", "T");
            if (pinCount > 0) Add(c, "AllPinCount", pinCount.ToString(Inv));
            recs.Add(Rec(c));

            // graphics first, pins after them: Altium draws records in order, so a filled body
            // written after the pins hides the pin names on a placed component. Within the graphics
            // filled shapes go first (largest first), otherwise a filled body hides what is drawn inside it.
            foreach (SShape s in ShapeOrder.Sorted(p.Shapes))
            {
                List<KeyValuePair<string, string>> r;
                switch (s.Kind)
                {
                    case "RECT":
                        r = NewRec(14);
                        Owner(r, s.Unit);
                        Coord(r, "Location.X", Math.Min(s.Pts[0].X, s.Pts[1].X));
                        Coord(r, "Location.Y", Math.Min(s.Pts[0].Y, s.Pts[1].Y));
                        Coord(r, "Corner.X", Math.Max(s.Pts[0].X, s.Pts[1].X));
                        Coord(r, "Corner.Y", Math.Max(s.Pts[0].Y, s.Pts[1].Y));
                        Add(r, "Color", Line.ToString(Inv));
                        Add(r, "AreaColor", (s.Fill == 2 ? LineFill : Body).ToString(Inv));
                        if (s.Fill != 0) Add(r, "IsSolid", "T");
                        break;
                    case "LINE":
                        r = NewRec(6);
                        Owner(r, s.Unit);
                        Add(r, "LineWidth", "1");
                        Add(r, "Color", Line.ToString(Inv));
                        Add(r, "LocationCount", s.Pts.Count.ToString(Inv));
                        for (int i = 0; i < s.Pts.Count; i++)
                        {
                            Coord(r, "X" + (i + 1), s.Pts[i].X);
                            Coord(r, "Y" + (i + 1), s.Pts[i].Y);
                        }
                        break;
                    case "POLY":
                        r = NewRec(7);
                        Owner(r, s.Unit);
                        Add(r, "LineWidth", "1");
                        Add(r, "Color", Line.ToString(Inv));
                        Add(r, "AreaColor", (s.Fill == 1 ? Body : LineFill).ToString(Inv));
                        if (s.Fill != 0) Add(r, "IsSolid", "T");
                        Add(r, "LocationCount", s.Pts.Count.ToString(Inv));
                        for (int i = 0; i < s.Pts.Count; i++)
                        {
                            Coord(r, "X" + (i + 1), s.Pts[i].X);
                            Coord(r, "Y" + (i + 1), s.Pts[i].Y);
                        }
                        break;
                    case "ELL":
                        r = NewRec(8);
                        Owner(r, s.Unit);
                        Coord(r, "Location.X", s.Pts[0].X);
                        Coord(r, "Location.Y", s.Pts[0].Y);
                        Coord(r, "Radius", s.R);
                        Coord(r, "SecondaryRadius", s.R2);
                        Add(r, "LineWidth", "1");
                        Add(r, "Color", Line.ToString(Inv));
                        Add(r, "AreaColor", (s.Fill == 1 ? Body : LineFill).ToString(Inv));
                        if (s.Fill != 0) Add(r, "IsSolid", "T");
                        break;
                    case "ARC":
                        r = NewRec(12);
                        Owner(r, s.Unit);
                        Coord(r, "Location.X", s.Pts[0].X);
                        Coord(r, "Location.Y", s.Pts[0].Y);
                        Coord(r, "Radius", s.R);
                        Add(r, "LineWidth", "1");
                        if (Math.Abs(s.A1) > 1e-6) Add(r, "StartAngle", Bin.F3(s.A1));
                        double a2 = s.A2;
                        if (a2 <= s.A1 + 1e-6 && Math.Abs(a2) < 1e-6) a2 = 360;
                        Add(r, "EndAngle", Bin.F3(a2));
                        Add(r, "Color", Line.ToString(Inv));
                        break;
                    case "TEXT":
                        r = NewRec(4);
                        Owner(r, s.Unit);
                        Coord(r, "Location.X", s.Pts[0].X);
                        Coord(r, "Location.Y", s.Pts[0].Y);
                        int o = ((int)Math.Round(Norm(s.Rot) / 90.0)) % 4;
                        if (o != 0) Add(r, "Orientation", o.ToString(Inv));
                        Add(r, "Color", Line.ToString(Inv));
                        Add(r, "FontID", "1");
                        AddText(r, "Text", s.Text);
                        break;
                    default:
                        continue;
                }
                Add(r, "UniqueID", Bin.UniqueId());
                recs.Add(Rec(r));
            }

            foreach (SPin pin in p.Pins) recs.Add(PinRecord(pin));

            // Designator and Comment
            List<KeyValuePair<string, string>> d = NewRec(34);
            Add(d, "IndexInSheet", "-1");
            Add(d, "OwnerPartId", "-1");
            Coord(d, "Location.X", minx);
            Coord(d, "Location.Y", maxy + 20);
            Add(d, "Color", Text.ToString(Inv));
            Add(d, "FontID", "1");
            AddText(d, "Text", p.Prefix);
            Add(d, "Name", "Designator");
            Add(d, "ReadOnlyState", "1");
            Add(d, "UniqueID", Bin.UniqueId());
            recs.Add(Rec(d));

            string value;
            bool hasValue = p.Params.TryGetValue("Value", out value) && !string.IsNullOrEmpty(value);
            List<KeyValuePair<string, string>> cm = NewRec(41);
            Add(cm, "IndexInSheet", "-1");
            Add(cm, "OwnerPartId", "-1");
            Coord(cm, "Location.X", minx);
            Coord(cm, "Location.Y", miny - 30);
            Add(cm, "Color", Text.ToString(Inv));
            Add(cm, "FontID", "1");
            AddText(cm, "Text", hasValue ? "=Value" : libRef);
            Add(cm, "Name", "Comment");
            Add(cm, "UniqueID", Bin.UniqueId());
            recs.Add(Rec(cm));

            foreach (KeyValuePair<string, string> kv in p.Params)
            {
                List<KeyValuePair<string, string>> pr = NewRec(41);
                Add(pr, "IndexInSheet", "-1");
                Add(pr, "OwnerPartId", "-1");
                Coord(pr, "Location.X", minx);
                Coord(pr, "Location.Y", miny - 60);
                Add(pr, "Color", Text.ToString(Inv));
                Add(pr, "FontID", "1");
                Add(pr, "IsHidden", "T");
                AddText(pr, "Text", kv.Value);
                AddText(pr, "Name", kv.Key);
                Add(pr, "UniqueID", Bin.UniqueId());
                recs.Add(Rec(pr));
            }

            // footprint link (records 44-48, as Altium writes them)
            int idx44 = recs.Count;
            recs.Add(Bin.CStrBlock("|RECORD=44", 0));
            if (!string.IsNullOrEmpty(footprint))
            {
                int idx45 = recs.Count;
                List<KeyValuePair<string, string>> im = NewRec(45);
                Add(im, "OwnerIndex", idx44.ToString(Inv));
                Add(im, "IndexInSheet", "-1");
                Add(im, "Description", "Footprint");
                Add(im, "UseComponentLibrary", "T");
                AddText(im, "ModelName", footprint);
                Add(im, "ModelType", "PCBLIB");
                Add(im, "DatafileCount", "1");
                AddText(im, "ModelDatafileEntity0", footprint);
                Add(im, "ModelDatafileKind0", "PCBLib");
                Add(im, "IsCurrent", "T");
                Add(im, "UniqueID", Bin.UniqueId());
                recs.Add(Rec(im));
                recs.Add(Bin.CStrBlock("|RECORD=46|OwnerIndex=" + idx45.ToString(Inv), 0));
                recs.Add(Bin.CStrBlock("|RECORD=48|OwnerIndex=" + idx45.ToString(Inv), 0));
            }

            string key = Bin.SectionKey(libRef, root);
            CfbEntry st = root.Storage(key);
            st.SetStream("Data", Bin.Concat(recs));

            // ---- FileHeader ----
            List<Comp> comps;
            List<KeyValuePair<string, string>> hp = HeaderParams(root, out comps);
            Comp nc = new Comp();
            nc.LibRef = libRef;
            nc.Descr = p.Desc;
            nc.PartCount = (p.PartCount + 1).ToString(Inv);
            comps.Add(nc);
            List<KeyValuePair<string, string>> outp = new List<KeyValuePair<string, string>>();
            int weight = 1;
            foreach (KeyValuePair<string, string> kv in hp)
            {
                string k = kv.Key;
                if (string.Equals(k, "Weight", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(kv.Value, NumberStyles.Integer, Inv, out weight);
                    continue;
                }
                if (string.Equals(k, "CompCount", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(k, "^(LibRef|CompDescr|PartCount)\\d+$", RegexOptions.IgnoreCase)) continue;
                outp.Add(kv);
            }
            weight = Math.Max(1, weight) + recs.Count;
            outp.Insert(Math.Min(1, outp.Count), new KeyValuePair<string, string>("Weight", weight.ToString(Inv)));
            Add(outp, "CompCount", comps.Count.ToString(Inv));
            for (int i = 0; i < comps.Count; i++)
            {
                Add(outp, "LibRef" + i, Bin.Ansi(comps[i].LibRef));
                if (!string.IsNullOrEmpty(comps[i].Descr)) Add(outp, "CompDescr" + i, Bin.Ansi(comps[i].Descr).Replace("|", "/"));
                Add(outp, "PartCount" + i, comps[i].PartCount);
            }
            root.SetStream("FileHeader", Bin.CStrBlock(Bin.JoinParams(outp, true), 0));

            if (key != libRef)
            {
                List<KeyValuePair<string, string>> sk = new List<KeyValuePair<string, string>>();
                CfbEntry ske = root.Get("SectionKeys");
                if (ske != null && ske.Data.Length > 4)
                {
                    int sp = 0;
                    sk = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp));
                }
                int kc = 0;
                int.TryParse(Bin.Get(sk, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                List<KeyValuePair<string, string>> nsk = new List<KeyValuePair<string, string>>();
                Add(nsk, "KeyCount", (kc + 1).ToString(Inv));
                for (int i = 0; i < kc; i++)
                {
                    Add(nsk, "LibRef" + i, Bin.Get(sk, "LibRef" + i) ?? "");
                    Add(nsk, "SectionKey" + i, Bin.Get(sk, "SectionKey" + i) ?? "");
                }
                Add(nsk, "LibRef" + kc, Bin.Ansi(libRef));
                Add(nsk, "SectionKey" + kc, key);
                root.SetStream("SectionKeys", Bin.CStrBlock(Bin.JoinParams(nsk, true), 0));
            }
        }

        // see PcbLibWriter.RepairStorageNames: rename storages whose names Altium would not find
        public static int RepairStorageNames(CfbEntry root)
        {
            CfbEntry fh = root.Get("FileHeader");
            if (fh == null || fh.IsStorage || fh.Data == null || fh.Data.Length < 5) return 0;
            int p0 = 0;
            List<KeyValuePair<string, string>> hp = Bin.ParseParams(Bin.ReadCStrBlock(fh.Data, ref p0));
            List<string> refs = new List<string>();
            foreach (KeyValuePair<string, string> kv in hp)
                if (Regex.IsMatch(kv.Key, "^LibRef\\d+$", RegexOptions.IgnoreCase)) refs.Add(kv.Value);

            List<KeyValuePair<string, string>> sk = new List<KeyValuePair<string, string>>();
            CfbEntry ske = root.Get("SectionKeys");
            if (ske != null && !ske.IsStorage && ske.Data != null && ske.Data.Length > 4)
            {
                int sp = 0;
                List<KeyValuePair<string, string>> raw = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp));
                int kc = 0;
                int.TryParse(Bin.Get(raw, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                for (int i = 0; i < kc; i++)
                {
                    string a = Bin.Get(raw, "LibRef" + i), b = Bin.Get(raw, "SectionKey" + i);
                    if (a != null && b != null) sk.Add(new KeyValuePair<string, string>(a, b));
                }
            }

            int fixedCount = 0;
            foreach (string libRef in refs)
            {
                int idx = -1;
                for (int i = 0; i < sk.Count; i++) if (string.Equals(sk[i].Key, libRef, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                string cur = idx >= 0 ? sk[idx].Value : (libRef.Length > 31 ? libRef.Substring(0, 31) : libRef);
                if (Bin.IsSafeKey(cur)) continue;
                CfbEntry st = root.Get(cur);
                if (st == null || !st.IsStorage) continue;
                string nk = Bin.SectionKey(libRef, root);
                st.Name = nk;
                KeyValuePair<string, string> kv = new KeyValuePair<string, string>(libRef, nk);
                if (idx >= 0) sk[idx] = kv; else sk.Add(kv);
                fixedCount++;
            }
            if (fixedCount > 0)
            {
                List<KeyValuePair<string, string>> nsk = new List<KeyValuePair<string, string>>();
                Add(nsk, "KeyCount", sk.Count.ToString(Inv));
                for (int i = 0; i < sk.Count; i++)
                {
                    Add(nsk, "LibRef" + i, sk[i].Key);
                    Add(nsk, "SectionKey" + i, sk[i].Value);
                }
                root.SetStream("SectionKeys", Bin.CStrBlock(Bin.JoinParams(nsk, true), 0));
            }
            return fixedCount;
        }

        // Libraries written by EasyLib 2.0 had the pins before the body graphics, which hides the
        // pin names under a filled rectangle once the part is placed; versions up to 2.2 kept the
        // EasyEDA order of the graphics, so a filled body could hide the lines drawn inside it
        // (switch contacts, transistor arrows). Reorder: filled shapes (largest first), other
        // graphics, texts, then pins.
        public static int RepairPinOrder(CfbEntry root)
        {
            int fixedCount = 0;
            foreach (CfbEntry st in root.Children)
            {
                if (!st.IsStorage) continue;
                CfbEntry data = st.Get("Data");
                if (data == null || data.IsStorage || data.Data == null) continue;
                byte[] d = data.Data;
                List<byte[]> recs = new List<byte[]>();
                List<bool> isPin = new List<bool>();
                List<string> text = new List<string>();
                int pos = 0;
                bool ok = true;
                while (pos + 4 <= d.Length)
                {
                    uint h = BitConverter.ToUInt32(d, pos);
                    int len = (int)(h & 0xFFFFFF), flags = (int)(h >> 24);
                    if (pos + 4 + len > d.Length) { ok = false; break; }
                    byte[] rec = new byte[4 + len];
                    Buffer.BlockCopy(d, pos, rec, 0, rec.Length);
                    recs.Add(rec);
                    isPin.Add(flags == 1);
                    text.Add(flags == 1 ? "" : Bin.W1252.GetString(d, pos + 4, len));
                    pos += 4 + len;
                }
                if (!ok || pos != d.Length || recs.Count < 3 || isPin[0]) continue;
                // range of body records: after the component record, up to the designator / first parameter
                int end = recs.Count;
                for (int i = 1; i < recs.Count; i++)
                    if (!isPin[i] && (Regex.IsMatch(text[i], "\\|RECORD=(34|41|44)\\|", RegexOptions.IgnoreCase) || text[i].StartsWith("|RECORD=44"))) { end = i; break; }
                bool refs = false;
                List<int> filled = new List<int>(), other = new List<int>(), texts = new List<int>(), pins = new List<int>();
                Dictionary<int, double> area = new Dictionary<int, double>();
                for (int i = 1; i < end; i++)
                {
                    if (isPin[i]) { pins.Add(i); continue; }
                    string t = text[i];
                    if (t.IndexOf("OwnerIndex=", StringComparison.OrdinalIgnoreCase) >= 0) refs = true;
                    string rec = RecParam(t, "RECORD");
                    bool solid = string.Equals(RecParam(t, "IsSolid"), "T", StringComparison.OrdinalIgnoreCase);
                    if (rec == "4") texts.Add(i);
                    else if (solid && (rec == "14" || rec == "10" || rec == "7" || rec == "8" || rec == "11"))
                    {
                        filled.Add(i);
                        area[i] = RecArea(t, rec);
                    }
                    else other.Add(i);
                }
                if (refs) continue;
                List<int> sortedFilled = new List<int>(filled);
                sortedFilled.Sort(delegate(int a, int b)
                {
                    int c = area[b].CompareTo(area[a]);
                    return c != 0 ? c : a.CompareTo(b);
                });
                List<int> order = new List<int>();
                order.Add(0);
                order.AddRange(sortedFilled); order.AddRange(other); order.AddRange(texts); order.AddRange(pins);
                for (int i = end; i < recs.Count; i++) order.Add(i);
                bool changed = false;
                for (int i = 0; i < order.Count; i++) if (order[i] != i) { changed = true; break; }
                if (!changed) continue;
                List<byte[]> outRecs = new List<byte[]>();
                foreach (int i in order) outRecs.Add(recs[i]);
                st.SetStream("Data", Bin.Concat(outRecs));
                fixedCount++;
            }
            return fixedCount;
        }

        static string RecParam(string t, string key)
        {
            Match m = Regex.Match(t, "\\|" + Regex.Escape(key) + "=([^|]*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim().TrimEnd('\0') : null;
        }

        static double RecNum(string t, string key)
        {
            double v = 0, f = 0;
            string s = RecParam(t, key), sf = RecParam(t, key + "_Frac");
            if (s != null) double.TryParse(s, NumberStyles.Float, Inv, out v);
            if (sf != null) double.TryParse(sf, NumberStyles.Float, Inv, out f);
            return v + f / 100000.0;
        }

        static double RecArea(string t, string rec)
        {
            if (rec == "14" || rec == "10")
                return Math.Abs((RecNum(t, "Corner.X") - RecNum(t, "Location.X")) * (RecNum(t, "Corner.Y") - RecNum(t, "Location.Y")));
            if (rec == "8")
            {
                double r = RecNum(t, "Radius"), r2 = RecNum(t, "SecondaryRadius");
                return Math.PI * r * (r2 > 0 ? r2 : r);
            }
            if (rec == "11") { double r = RecNum(t, "Radius"); return Math.PI * r * r; }
            int n = (int)RecNum(t, "LocationCount");
            List<PointD> pts = new List<PointD>();
            for (int i = 1; i <= n; i++) pts.Add(new PointD(RecNum(t, "X" + i), RecNum(t, "Y" + i)));
            return ShapeOrder.PolyArea(pts);
        }

        static double Norm(double a)
        {
            a = a % 360.0;
            if (a < 0) a += 360.0;
            return a;
        }
    }

    // ======================================================================
    // high level: add a part to the pair of libraries
    // ======================================================================

    static class AltiumLib
    {
        public static string SafeName(string s)
        {
            s = Bin.Ansi(s ?? "").Replace("|", "/").Trim();
            if (s.Length > 250) s = s.Substring(0, 250);
            return s;
        }

        public static CfbEntry Load(string path, bool pcb)
        {
            if (File.Exists(path))
            {
                byte[] data = ReadShared(path);
                if (data.Length > 0)
                {
                    CfbEntry root;
                    try { root = Cfb.Read(data); }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException("Файл " + Path.GetFileName(path) + " повреждён или это не библиотека Altium (" + ex.Message +
                            "). Восстановите копию: кнопка «Библиотека» → «Восстановить копию», или переименуйте " + Path.GetFileName(path) + ".bak.", ex);
                    }
                    CfbEntry fh = root.Get("FileHeader");
                    string head = fh != null && fh.Data != null ? W1252Head(fh.Data) : "";
                    bool ok = pcb ? head.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) >= 0
                                  : head.IndexOf("Schematic Library", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!ok)
                        throw new InvalidDataException("Файл " + Path.GetFileName(path) + " — не " + (pcb ? "PCB-библиотека" : "библиотека схемных символов") +
                                                       " Altium. Проверьте, что файлы не перепутаны.");
                    return root;
                }
            }
            return pcb ? PcbLibWriter.NewLibrary(path) : SchLibWriter.NewLibrary();
        }

        static string W1252Head(byte[] d) { return Bin.W1252.GetString(d, 0, Math.Min(d.Length, 300)); }

        // read even if another program (Altium) has the file open
        static byte[] ReadShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        MemoryStream ms = new MemoryStream();
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                    System.Threading.Thread.Sleep(300);
                }
            }
        }

        public static void Save(CfbEntry root, string path)
        {
            byte[] data = Cfb.Write(root);
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(path))
            {
                string tmp0 = path + ".tmp";
                File.WriteAllBytes(tmp0, data);
                File.Move(tmp0, path);
                return;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("файл " + Path.GetFileName(path) + " только для чтения (снимите этот флаг в свойствах файла)");
            string bak = path + ".bak";
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                // 1) atomic replace (works when nobody holds the file open)
                try
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Replace(tmp, path, bak);
                    return;
                }
                catch (Exception ex) { if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw; last = ex; }
                // 2) the file is open elsewhere (e.g. Altium has the library loaded): back up and overwrite in place
                try
                {
                    File.Copy(path, bak, true);
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Write(data, 0, data.Length);
                        fs.SetLength(data.Length);
                    }
                    try { File.Delete(tmp); } catch { }
                    return;
                }
                catch (Exception ex) { if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw; last = ex; }
                System.Threading.Thread.Sleep(400);
            }
            try { File.Delete(tmp); } catch { }
            throw new IOException("файл занят другой программой: " + Path.GetFileName(path), last);
        }

        // fixes symbols written by EasyLib 2.0 (pin names hidden under the body); returns the number fixed
        public static int RepairSchLib(string schPath)
        {
            if (!File.Exists(schPath)) return 0;
            using (LibLock.Acquire(Path.GetDirectoryName(Path.GetFullPath(schPath))))
            {
                CfbEntry sch = Load(schPath, false);
                int n = SchLibWriter.RepairStorageNames(sch) + SchLibWriter.RepairPinOrder(sch);
                if (n > 0) Save(sch, schPath);
                return n;
            }
        }

        public static int RepairPcbLib(string pcbPath)
        {
            if (!File.Exists(pcbPath)) return 0;
            using (LibLock.Acquire(Path.GetDirectoryName(Path.GetFullPath(pcbPath))))
            {
                CfbEntry pcb = Load(pcbPath, true);
                int n = PcbLibWriter.RepairStorageNames(pcb);
                if (n > 0) Save(pcb, pcbPath);
                return n;
            }
        }

        public static LibResult Add(Part p, string schPath, string pcbPath, byte[] step, string stepName, ModelPlacement mp)
        {
            using (LibLock.Acquire(Path.GetDirectoryName(Path.GetFullPath(schPath))))
                return AddLocked(p, schPath, pcbPath, step, stepName, mp);
        }

        static void CheckWritable(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("файл " + Path.GetFileName(path) + " только для чтения (снимите этот флаг в свойствах файла)");
        }

        static LibResult AddLocked(Part p, string schPath, string pcbPath, byte[] step, string stepName, ModelPlacement mp)
        {
            // fail before anything is written: both files must be writable and readable
            CheckWritable(schPath);
            CheckWritable(pcbPath);
            CfbEntry schPre = p.NoSymbol ? null : Load(schPath, false);
            LibResult r = new LibResult();
            r.LibRef = SafeName(p.Title == "" ? (p.Lcsc != "" ? p.Lcsc : "Part") : p.Title);
            // user parts often share names ("10k", "STM32F103C8T6"); keep them apart from the official ones
            if (!p.Official && p.Author != "") r.LibRef = SafeName(r.LibRef + " [" + p.Author + "]");
            r.Footprint = SafeName(p.Footprint == "" ? (p.Lcsc != "" ? p.Lcsc : r.LibRef) : p.Footprint);

            if (p.Pads.Count == 0)
            {
                // symbol only (some user parts have no footprint)
                r.Footprint = "";
                r.NoFootprint = true;
                CfbEntry sch0 = schPre;
                r.Repaired = SchLibWriter.RepairStorageNames(sch0) + SchLibWriter.RepairPinOrder(sch0);
                if (SchLibWriter.HasSymbol(sch0, r.LibRef)) r.SymbolExisted = true;
                else SchLibWriter.AddSymbol(sch0, p, r.LibRef, "");
                if (!r.SymbolExisted || r.Repaired > 0) Save(sch0, schPath);
                return r;
            }

            CfbEntry pcb = Load(pcbPath, true);
            int pcbFixed = PcbLibWriter.RepairStorageNames(pcb);
            if (PcbLibWriter.HasFootprint(pcb, r.Footprint))
            {
                // same name but different pads (e.g. a community part reusing a common name): keep both
                HashSet<string> have = PcbLibWriter.PadDesignators(pcb, r.Footprint);
                if (have != null && !have.SetEquals(PcbLibWriter.PadDesignators(p)))
                {
                    string baseName = r.Footprint + " (" + (p.Lcsc != "" ? p.Lcsc : "v2") + ")";
                    r.Footprint = baseName;
                    for (int i = 2; PcbLibWriter.HasFootprint(pcb, r.Footprint); i++) r.Footprint = baseName + "-" + i;
                }
            }
            if (PcbLibWriter.HasFootprint(pcb, r.Footprint))
            {
                r.FootprintExisted = true;
                if (pcbFixed > 0) Save(pcb, pcbPath);
            }
            else
            {
                PcbLibWriter.AddFootprint(pcb, p, r.Footprint, p.FootprintDesc != "" ? p.FootprintDesc : p.Desc, step, stepName, mp);
                r.ModelAdded = step != null && mp != null;
                Save(pcb, pcbPath);
            }
            if (p.NoSymbol)
            {
                r.LibRef = "";
                r.Repaired = pcbFixed;
                return r;
            }

            CfbEntry sch = schPre;
            r.Repaired = SchLibWriter.RepairStorageNames(sch) + SchLibWriter.RepairPinOrder(sch);
            bool schFixed = r.Repaired > 0;
            r.Repaired += pcbFixed;
            // same name from another source with another footprint: keep both, mark the new one
            if (SchLibWriter.HasSymbol(sch, r.LibRef))
            {
                string have = SchLibWriter.SymbolFootprint(sch, r.LibRef);
                if (have != null && !string.Equals(Bin.Ansi(have), Bin.Ansi(r.Footprint), StringComparison.OrdinalIgnoreCase))
                {
                    string vendor;
                    p.Info.TryGetValue("Vendor", out vendor);
                    string mark = p.Source == "kicad" ? " [KiCad]"
                                : p.Source == "import" ? " [" + (string.IsNullOrEmpty(vendor) ? "import" : vendor) + "]"
                                : p.Lcsc != "" ? " (" + p.Lcsc + ")" : " (2)";
                    string baseRef = SafeName(r.LibRef + mark);
                    r.LibRef = baseRef;
                    for (int i = 2; SchLibWriter.HasSymbol(sch, r.LibRef); i++)
                    {
                        string again = SchLibWriter.SymbolFootprint(sch, r.LibRef);
                        if (again != null && string.Equals(Bin.Ansi(again), Bin.Ansi(r.Footprint), StringComparison.OrdinalIgnoreCase)) break;
                        r.LibRef = baseRef + " " + i;
                    }
                }
            }
            if (SchLibWriter.HasSymbol(sch, r.LibRef))
                r.SymbolExisted = true;
            else
                SchLibWriter.AddSymbol(sch, p, r.LibRef, r.Footprint);
            if (!r.SymbolExisted || schFixed) Save(sch, schPath);
            return r;
        }
    }
}
