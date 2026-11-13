// Import of ready-made Altium libraries (.IntLib, .SchLib, .PcbLib — e.g. the Altium download
// of SnapEDA, Ultra Librarian or CSE). Components are copied into EasyLib.SchLib / EasyLib.PcbLib
// as they are, without conversion: symbol records, footprint primitives and embedded 3D models.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class AltiumSource
    {
        public string Name = "";
        public bool Pcb;
        public CfbEntry Root;
    }

    static class AltiumImport
    {
        // adds the libraries found in a file (.IntLib holds compressed SchLib/PcbLib streams)
        public static void Collect(List<AltiumSource> list, string name, byte[] data)
        {
            CfbEntry root;
            try { root = Cfb.Read(data); }
            catch { return; }
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".intlib")
            {
                foreach (CfbEntry st in root.Children)
                {
                    if (!st.IsStorage) continue;
                    bool pcb = string.Equals(st.Name, "PCBLib", StringComparison.OrdinalIgnoreCase);
                    if (!pcb && !string.Equals(st.Name, "SchLib", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (CfbEntry s in st.Children)
                    {
                        if (s.IsStorage || s.Data == null || s.Data.Length < 8) continue;
                        try
                        {
                            byte[] raw = s.Data[0] == 0x02 ? Inflate(s.Data, 1) : s.Data;
                            Add(list, name, Cfb.Read(raw));
                        }
                        catch { }
                    }
                }
            }
            else Add(list, name, root);
        }

        static void Add(List<AltiumSource> list, string name, CfbEntry root)
        {
            CfbEntry fh = root.Get("FileHeader");
            if (fh == null || fh.Data == null) return;
            string head = Bin.W1252.GetString(fh.Data, 0, Math.Min(fh.Data.Length, 300));
            bool sch = head.IndexOf("Schematic Library", StringComparison.OrdinalIgnoreCase) >= 0;
            bool pcb = !sch && head.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) >= 0 && root.GetPath("Library/Data") != null;
            if (!sch && !pcb) return;
            list.Add(new AltiumSource { Name = name, Pcb = pcb, Root = root });
        }

        // zlib stream: 2 header bytes, deflate data
        static byte[] Inflate(byte[] d, int offset)
        {
            using (MemoryStream ms = new MemoryStream(d, offset + 2, d.Length - offset - 2))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream())
            {
                ds.CopyTo(o);
                return o.ToArray();
            }
        }

        // merges the libraries into EasyLib; "done" gets one line per component
        public static void Apply(List<AltiumSource> sources, string schPath, string pcbPath, List<string> done)
        {
            if (sources.Count == 0) return;
            using (LibLock.Acquire(Path.GetDirectoryName(Path.GetFullPath(schPath))))
            {
                bool anySch = false, anyPcb = false;
                foreach (AltiumSource s in sources) { if (s.Pcb) anyPcb = true; else anySch = true; }
                CfbEntry sch = anySch ? AltiumLib.Load(schPath, false) : null;
                CfbEntry pcb = anyPcb ? AltiumLib.Load(pcbPath, true) : null;
                bool schChanged = false, pcbChanged = false;
                foreach (AltiumSource s in sources)
                {
                    if (!s.Pcb) continue;
                    foreach (string n in PcbLibWriter.FootprintNames(s.Root))
                    {
                        if (PcbLibWriter.HasFootprint(pcb, n)) { done.Add("футпринт «" + n + "» (уже был)"); continue; }
                        int models = PcbLibWriter.CopyFootprint(pcb, s.Root, n);
                        if (models < 0) continue;
                        pcbChanged = true;
                        done.Add("футпринт «" + n + "»" + (models > 0 ? " с 3D" : ""));
                    }
                }
                foreach (AltiumSource s in sources)
                {
                    if (s.Pcb) continue;
                    foreach (string n in SchLibWriter.SymbolNames(s.Root))
                    {
                        if (SchLibWriter.HasSymbol(sch, n)) { done.Insert(0, "«" + n + "» (уже был)"); continue; }
                        if (!SchLibWriter.CopySymbol(sch, s.Root, n)) continue;
                        schChanged = true;
                        done.Insert(0, "«" + n + "»");
                    }
                }
                // PcbLib first: a symbol should never point to a footprint that failed to save
                if (pcbChanged) AltiumLib.Save(pcb, pcbPath);
                if (schChanged) AltiumLib.Save(sch, schPath);
            }
        }

        public static void CopyTree(CfbEntry from, CfbEntry to)
        {
            foreach (CfbEntry c in from.Children)
            {
                if (c.IsStorage) CopyTree(c, to.Storage(c.Name));
                else to.SetStream(c.Name, (byte[])(c.Data ?? new byte[0]).Clone());
            }
        }
    }

    static partial class PcbLibWriter
    {
        static readonly CultureInfo InvC = CultureInfo.InvariantCulture;

        // copies a footprint with its embedded 3D models; returns the number of models it uses (-1: not found)
        public static int CopyFootprint(CfbEntry dst, CfbEntry src, string name)
        {
            CfbEntry st = FootprintStorage(src, name);
            if (st == null || st.Get("Data") == null) return -1;
            string key = Bin.SectionKey(name, dst);
            AltiumImport.CopyTree(st, dst.Storage(key));

            CfbEntry libData = dst.GetPath("Library/Data");
            int pos = 0;
            string header = Bin.ReadCStrBlock(libData.Data, ref pos);
            List<string> names = FootprintNames(dst);
            names.Add(name);
            List<byte[]> nd = new List<byte[]>();
            nd.Add(Bin.CStrBlock(header, 0));
            nd.Add(Bin.U32Bytes((uint)names.Count));
            foreach (string n in names) nd.Add(Bin.PascalBlock(n));
            libData.Data = Bin.Concat(nd);

            // table of contents: the source line if there is one
            string line = null;
            CfbEntry srcToc = src.GetPath("Library/ComponentParamsTOC");
            CfbEntry srcTocData = srcToc != null ? srcToc.Get("Data") : null;
            if (srcTocData != null && srcTocData.Data != null && srcTocData.Data.Length > 4)
            {
                int tp = 0;
                foreach (string l in Bin.ReadCStrBlock(srcTocData.Data, ref tp).Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match m = Regex.Match(l, "^Name=([^|]*)");
                    if (m.Success && string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) { line = l; break; }
                }
            }
            if (line == null) line = "Name=" + name + "|Pad Count=0|Height=0|Description=";
            CfbEntry tocSt = dst.GetPath("Library").Storage("ComponentParamsTOC");
            tocSt.SetStream("Header", Bin.U32Bytes(1));
            string toc = "";
            CfbEntry tocData = tocSt.Get("Data");
            if (tocData != null && tocData.Data.Length > 4) { int tp = 0; toc = Bin.ReadCStrBlock(tocData.Data, ref tp); }
            tocSt.SetStream("Data", Bin.CStrBlock(toc + line + "\r\n", 0));
            if (key != name) AddSectionKey(dst, name, key);

            // embedded models, by id
            List<string> ids = FootprintModelIds(src, name);
            if (ids.Count == 0) return 0;
            CfbEntry sm = src.GetPath("Library/Models");
            if (sm == null) return 0;
            List<ModelInfo> srcModels = Models(src);
            byte[] sd = sm.Get("Data") != null ? sm.Get("Data").Data : new byte[0];
            List<byte[]> blocks = new List<byte[]>();
            for (int p = 0; p + 4 <= sd.Length; )
            {
                int start = p;
                Bin.ReadCStrBlock(sd, ref p);
                byte[] b = new byte[p - start];
                Buffer.BlockCopy(sd, start, b, 0, b.Length);
                blocks.Add(b);
            }
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelInfo m in Models(dst)) have.Add(m.Id);
            CfbEntry dm = dst.GetPath("Library").Storage("Models");
            int used = 0;
            foreach (ModelInfo m in srcModels)
            {
                if (!ids.Contains(m.Id) || m.Index >= blocks.Count) continue;
                used++;
                if (!have.Add(m.Id)) continue;
                CfbEntry stream = sm.Get(m.Index.ToString(InvC));
                if (stream == null) continue;
                CfbEntry hdr = dm.Get("Header");
                uint n = hdr != null && hdr.Data.Length >= 4 ? Bin.U32(hdr.Data, 0) : 0;
                byte[] old = dm.Get("Data") != null ? dm.Get("Data").Data : new byte[0];
                dm.SetStream("Data", Bin.Concat(new List<byte[]> { old, blocks[m.Index] }));
                dm.SetStream(n.ToString(InvC), (byte[])stream.Data.Clone());
                dm.SetStream("Header", Bin.U32Bytes(n + 1));
            }
            return used;
        }
    }

    static partial class SchLibWriter
    {
        // font table of a library header: id -> (key without the id, value)
        static Dictionary<int, List<KeyValuePair<string, string>>> Fonts(List<KeyValuePair<string, string>> hp, out int count)
        {
            Dictionary<int, List<KeyValuePair<string, string>>> r = new Dictionary<int, List<KeyValuePair<string, string>>>();
            count = 0;
            int.TryParse(Bin.Get(hp, "FontIdCount") ?? "0", NumberStyles.Integer, Inv, out count);
            foreach (KeyValuePair<string, string> kv in hp)
            {
                Match m = Regex.Match(kv.Key, "^(Size|FontName|Italic|Bold|Underline|Rotation|Strikeout)(\\d+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int id = int.Parse(m.Groups[2].Value, Inv);
                List<KeyValuePair<string, string>> l;
                if (!r.TryGetValue(id, out l)) r[id] = l = new List<KeyValuePair<string, string>>();
                l.Add(new KeyValuePair<string, string>(m.Groups[1].Value, kv.Value));
            }
            return r;
        }

        static string FontSig(List<KeyValuePair<string, string>> f)
        {
            List<string> s = new List<string>();
            foreach (KeyValuePair<string, string> kv in f) s.Add(kv.Key.ToLowerInvariant() + "=" + kv.Value.ToLowerInvariant());
            s.Sort(StringComparer.Ordinal);
            return string.Join("|", s.ToArray());
        }

        // copies a symbol from another SchLib; text fonts are mapped to this library's font table
        public static bool CopySymbol(CfbEntry dst, CfbEntry src, string libRef)
        {
            CfbEntry st = src.Get(SymbolStorageKey(src, libRef));
            CfbEntry data = st != null && st.IsStorage ? st.Get("Data") : null;
            if (data == null || data.Data == null) return false;
            List<Comp> srcComps, comps;
            List<KeyValuePair<string, string>> shp = HeaderParams(src, out srcComps);
            List<KeyValuePair<string, string>> hp = HeaderParams(dst, out comps);
            Comp sc = null;
            foreach (Comp c in srcComps) if (string.Equals(c.LibRef, libRef, StringComparison.OrdinalIgnoreCase)) sc = c;
            if (sc == null) return false;

            // fonts
            int srcCount, dstCount;
            Dictionary<int, List<KeyValuePair<string, string>>> sf = Fonts(shp, out srcCount), df = Fonts(hp, out dstCount);
            Dictionary<string, int> bySig = new Dictionary<string, int>();
            foreach (KeyValuePair<int, List<KeyValuePair<string, string>>> kv in df) if (!bySig.ContainsKey(FontSig(kv.Value))) bySig[FontSig(kv.Value)] = kv.Key;
            Dictionary<int, int> map = new Dictionary<int, int>();
            List<KeyValuePair<string, string>> newFonts = new List<KeyValuePair<string, string>>();
            Func<int, int> mapFont = delegate(int id)
            {
                int to;
                if (map.TryGetValue(id, out to)) return to;
                List<KeyValuePair<string, string>> f;
                if (!sf.TryGetValue(id, out f)) { map[id] = to = Math.Max(1, Math.Min(id, Math.Max(1, dstCount))); return to; }
                string sig = FontSig(f);
                if (!bySig.TryGetValue(sig, out to))
                {
                    to = ++dstCount;
                    bySig[sig] = to;
                    foreach (KeyValuePair<string, string> kv in f) newFonts.Add(new KeyValuePair<string, string>(kv.Key + to.ToString(Inv), kv.Value));
                }
                map[id] = to;
                return to;
            };

            // records: rewrite FontID in the text records
            byte[] d = data.Data;
            List<byte[]> recs = new List<byte[]>();
            int pos = 0;
            while (pos + 4 <= d.Length)
            {
                uint h = BitConverter.ToUInt32(d, pos);
                int len = (int)(h & 0xFFFFFF), flags = (int)(h >> 24);
                if (pos + 4 + len > d.Length) return false;
                if (flags != 0)
                {
                    byte[] raw = new byte[4 + len];
                    Buffer.BlockCopy(d, pos, raw, 0, raw.Length);
                    recs.Add(raw);
                }
                else
                {
                    string t = Bin.W1252.GetString(d, pos + 4, len).TrimEnd('\0');
                    t = Regex.Replace(t, "(\\|(?:Text)?FontID=)(\\d+)", delegate(Match m)
                    {
                        return m.Groups[1].Value + mapFont(int.Parse(m.Groups[2].Value, Inv)).ToString(Inv);
                    }, RegexOptions.IgnoreCase);
                    recs.Add(Bin.CStrBlock(t, 0));
                }
                pos += 4 + len;
            }

            string key = Bin.SectionKey(libRef, dst);
            CfbEntry nst = dst.Storage(key);
            AltiumImport.CopyTree(st, nst);
            nst.SetStream("Data", Bin.Concat(recs));

            Comp nc = new Comp { LibRef = libRef, Descr = sc.Descr, PartCount = sc.PartCount };
            comps.Add(nc);
            List<KeyValuePair<string, string>> outp = new List<KeyValuePair<string, string>>();
            int weight = 1;
            foreach (KeyValuePair<string, string> kv in hp)
            {
                string k = kv.Key;
                if (string.Equals(k, "Weight", StringComparison.OrdinalIgnoreCase)) { int.TryParse(kv.Value, NumberStyles.Integer, Inv, out weight); continue; }
                if (string.Equals(k, "CompCount", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(k, "^(LibRef|CompDescr|PartCount)\\d+$", RegexOptions.IgnoreCase)) continue;
                if (string.Equals(k, "FontIdCount", StringComparison.OrdinalIgnoreCase)) { outp.Add(new KeyValuePair<string, string>(k, dstCount.ToString(Inv))); outp.AddRange(newFonts); newFonts = new List<KeyValuePair<string, string>>(); continue; }
                outp.Add(kv);
            }
            weight = Math.Max(1, weight) + recs.Count;
            outp.Insert(Math.Min(1, outp.Count), new KeyValuePair<string, string>("Weight", weight.ToString(Inv)));
            Add(outp, "CompCount", comps.Count.ToString(Inv));
            for (int i = 0; i < comps.Count; i++)
            {
                Add(outp, "LibRef" + i, comps[i].LibRef);
                if (!string.IsNullOrEmpty(comps[i].Descr)) Add(outp, "CompDescr" + i, comps[i].Descr);
                Add(outp, "PartCount" + i, comps[i].PartCount);
            }
            dst.SetStream("FileHeader", Bin.CStrBlock(Bin.JoinParams(outp, true), 0));

            if (key != libRef)
            {
                List<KeyValuePair<string, string>> sk = new List<KeyValuePair<string, string>>();
                CfbEntry ske = dst.Get("SectionKeys");
                if (ske != null && ske.Data.Length > 4) { int sp = 0; sk = Bin.ParseParams(Bin.ReadCStrBlock(ske.Data, ref sp)); }
                int kc = 0;
                int.TryParse(Bin.Get(sk, "KeyCount") ?? "0", NumberStyles.Integer, Inv, out kc);
                List<KeyValuePair<string, string>> nsk = new List<KeyValuePair<string, string>>();
                Add(nsk, "KeyCount", (kc + 1).ToString(Inv));
                for (int i = 0; i < kc; i++)
                {
                    Add(nsk, "LibRef" + i, Bin.Get(sk, "LibRef" + i) ?? "");
                    Add(nsk, "SectionKey" + i, Bin.Get(sk, "SectionKey" + i) ?? "");
                }
                Add(nsk, "LibRef" + kc, libRef);
                Add(nsk, "SectionKey" + kc, key);
                dst.SetStream("SectionKeys", Bin.CStrBlock(Bin.JoinParams(nsk, true), 0));
            }
            return true;
        }
    }
}
