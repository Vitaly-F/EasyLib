// Minimal Compound File Binary (OLE2 structured storage) reader/writer.
// Altium .SchLib/.PcbLib files are compound files. Writer emits version 3 (512-byte sectors)
// with a valid red-black directory tree, mini stream and DIFAT for large files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EasyLib
{
    class CfbEntry
    {
        public string Name;
        public bool IsStorage;
        public byte[] Data = new byte[0];
        public byte[] Clsid = new byte[16];
        public List<CfbEntry> Children = new List<CfbEntry>();

        public CfbEntry(string name, bool isStorage)
        {
            Name = name;
            IsStorage = isStorage;
        }

        public CfbEntry Get(string name)
        {
            foreach (CfbEntry c in Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public CfbEntry GetPath(string path)
        {
            CfbEntry cur = this;
            foreach (string part in path.Split('/'))
            {
                if (cur == null) return null;
                cur = cur.Get(part);
            }
            return cur;
        }

        public CfbEntry Storage(string name)
        {
            CfbEntry e = Get(name);
            if (e == null)
            {
                e = new CfbEntry(name, true);
                Children.Add(e);
            }
            return e;
        }

        public CfbEntry SetStream(string name, byte[] data)
        {
            CfbEntry e = Get(name);
            if (e == null)
            {
                e = new CfbEntry(name, false);
                Children.Add(e);
            }
            e.Data = data ?? new byte[0];
            return e;
        }

        public void Remove(string name)
        {
            CfbEntry e = Get(name);
            if (e != null) Children.Remove(e);
        }
    }

    static class Cfb
    {
        const uint FREESECT = 0xFFFFFFFF, ENDOFCHAIN = 0xFFFFFFFE, FATSECT = 0xFFFFFFFD, DIFSECT = 0xFFFFFFFC, NOSTREAM = 0xFFFFFFFF;
        static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        // ------------------------------------------------------------------ read

        public static CfbEntry Read(byte[] f)
        {
            for (int i = 0; i < 8; i++)
                if (f.Length < 512 || f[i] != Signature[i]) throw new InvalidDataException("not a compound file");
            int major = U16(f, 0x1A);
            int sectorShift = U16(f, 0x1E);
            int miniShift = U16(f, 0x20);
            int sectorSize = 1 << sectorShift;
            int miniSize = 1 << miniShift;
            uint firstDir = U32(f, 0x30);
            uint cutoff = U32(f, 0x38);
            uint firstMiniFat = U32(f, 0x3C);
            uint firstDifat = U32(f, 0x44);
            uint numDifat = U32(f, 0x48);

            Func<uint, int> off = delegate(uint sid) { return (int)((sid + 1) << sectorShift); };

            // FAT sector list from header DIFAT + DIFAT chain
            List<uint> fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++)
            {
                uint s = U32(f, 0x4C + i * 4);
                if (s != FREESECT) fatSectors.Add(s);
            }
            uint d = firstDifat;
            int guard = 0;
            while (d != ENDOFCHAIN && d != FREESECT && guard++ < numDifat + 1)
            {
                int o = off(d);
                int per = sectorSize / 4 - 1;
                for (int i = 0; i < per; i++)
                {
                    uint s = U32(f, o + i * 4);
                    if (s != FREESECT) fatSectors.Add(s);
                }
                d = U32(f, o + per * 4);
            }
            int perSector = sectorSize / 4;
            uint[] fat = new uint[fatSectors.Count * perSector];
            for (int i = 0; i < fatSectors.Count; i++)
            {
                int o = off(fatSectors[i]);
                for (int j = 0; j < perSector; j++)
                    fat[i * perSector + j] = o + j * 4 + 4 <= f.Length ? U32(f, o + j * 4) : FREESECT;
            }

            byte[] dir = ReadChain(f, fat, firstDir, sectorSize, sectorShift, -1);
            int nEntries = dir.Length / 128;
            if (nEntries == 0) throw new InvalidDataException("empty directory");

            // entries
            string[] names = new string[nEntries];
            int[] types = new int[nEntries];
            uint[] left = new uint[nEntries], right = new uint[nEntries], child = new uint[nEntries], start = new uint[nEntries];
            long[] size = new long[nEntries];
            byte[][] clsid = new byte[nEntries][];
            for (int i = 0; i < nEntries; i++)
            {
                int o = i * 128;
                int nameLen = U16(dir, o + 64);
                names[i] = nameLen >= 2 ? Encoding.Unicode.GetString(dir, o, Math.Min(nameLen, 64) - 2) : "";
                types[i] = dir[o + 66];
                left[i] = U32(dir, o + 68);
                right[i] = U32(dir, o + 72);
                child[i] = U32(dir, o + 76);
                clsid[i] = new byte[16];
                Array.Copy(dir, o + 80, clsid[i], 0, 16);
                start[i] = U32(dir, o + 116);
                size[i] = U32(dir, o + 120);
                if (major >= 4) size[i] |= ((long)U32(dir, o + 124)) << 32;
            }

            // mini stream + mini FAT
            byte[] miniStream = ReadChain(f, fat, start[0], sectorSize, sectorShift, size[0]);
            byte[] miniFatBytes = firstMiniFat == ENDOFCHAIN || firstMiniFat == FREESECT ? new byte[0] : ReadChain(f, fat, firstMiniFat, sectorSize, sectorShift, -1);
            uint[] miniFat = new uint[miniFatBytes.Length / 4];
            for (int i = 0; i < miniFat.Length; i++) miniFat[i] = U32(miniFatBytes, i * 4);

            Func<int, byte[]> streamData = delegate(int i)
            {
                if (size[i] == 0) return new byte[0];
                if (size[i] < cutoff)
                {
                    byte[] buf = new byte[size[i]];
                    uint s = start[i];
                    int pos = 0, g = 0;
                    while (pos < buf.Length && s != ENDOFCHAIN && s < miniFat.Length && g++ < miniFat.Length + 1)
                    {
                        int n = Math.Min(miniSize, buf.Length - pos);
                        Array.Copy(miniStream, (int)s * miniSize, buf, pos, n);
                        pos += n;
                        s = miniFat[s];
                    }
                    return buf;
                }
                return ReadChain(f, fat, start[i], sectorSize, sectorShift, size[i]);
            };

            CfbEntry root = new CfbEntry(names[0], true);
            root.Clsid = clsid[0];
            Build(root, child[0], names, types, left, right, child, clsid, streamData, 0);
            return root;
        }

        static void Build(CfbEntry parent, uint did, string[] names, int[] types, uint[] left, uint[] right, uint[] child,
                          byte[][] clsid, Func<int, byte[]> data, int depth)
        {
            if (depth > 64) return;
            // in-order traversal of the sibling tree
            Stack<uint> stack = new Stack<uint>();
            uint cur = did;
            HashSet<uint> seen = new HashSet<uint>();
            while ((cur != NOSTREAM && cur < names.Length) || stack.Count > 0)
            {
                while (cur != NOSTREAM && cur < names.Length && !seen.Contains(cur))
                {
                    stack.Push(cur);
                    cur = left[cur];
                }
                if (stack.Count == 0) break;
                uint n = stack.Pop();
                if (seen.Contains(n)) { cur = NOSTREAM; continue; }
                seen.Add(n);
                int t = types[n];
                if (t == 1 || t == 2)
                {
                    CfbEntry e = new CfbEntry(names[n], t == 1);
                    e.Clsid = clsid[n];
                    if (t == 2) e.Data = data((int)n);
                    else Build(e, child[n], names, types, left, right, child, clsid, data, depth + 1);
                    parent.Children.Add(e);
                }
                cur = right[n];
            }
        }

        static byte[] ReadChain(byte[] f, uint[] fat, uint startSector, int sectorSize, int shift, long size)
        {
            MemoryStream ms = new MemoryStream();
            uint s = startSector;
            int guard = 0;
            while (s != ENDOFCHAIN && s != FREESECT && s < fat.Length && guard++ <= fat.Length)
            {
                int o = (int)((s + 1) << shift);
                int n = Math.Min(sectorSize, f.Length - o);
                if (n <= 0) break;
                ms.Write(f, o, n);
                s = fat[s];
            }
            byte[] r = ms.ToArray();
            if (size >= 0 && r.Length > size)
            {
                byte[] t = new byte[size];
                Array.Copy(r, t, size);
                r = t;
            }
            return r;
        }

        // ------------------------------------------------------------------ write

        class DirNode
        {
            public CfbEntry Entry;
            public int Type;            // 1 storage, 2 stream, 5 root
            public uint Left = NOSTREAM, Right = NOSTREAM, Child = NOSTREAM;
            public byte Color = 1;      // 0 red, 1 black
            public uint Start = ENDOFCHAIN;
            public uint Size;
        }

        public static int CompareNames(string a, string b)
        {
            if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
            for (int i = 0; i < a.Length; i++)
            {
                char x = char.ToUpperInvariant(a[i]), y = char.ToUpperInvariant(b[i]);
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        public static byte[] Write(CfbEntry root)
        {
            const int SS = 512, MS = 64, CUTOFF = 4096;
            List<DirNode> nodes = new List<DirNode>();
            DirNode rootNode = new DirNode { Entry = root, Type = 5, Color = 1 };
            nodes.Add(rootNode);
            AddChildren(rootNode, nodes);

            // mini stream
            MemoryStream mini = new MemoryStream();
            List<uint> miniFat = new List<uint>();
            List<DirNode> bigStreams = new List<DirNode>();
            foreach (DirNode n in nodes)
            {
                if (n.Type != 2) continue;
                byte[] data = n.Entry.Data;
                n.Size = (uint)data.Length;
                if (data.Length == 0) { n.Start = ENDOFCHAIN; continue; }
                if (data.Length < CUTOFF)
                {
                    int count = (data.Length + MS - 1) / MS;
                    n.Start = (uint)miniFat.Count;
                    for (int i = 0; i < count; i++)
                        miniFat.Add(i == count - 1 ? ENDOFCHAIN : (uint)(miniFat.Count + 1));
                    mini.Write(data, 0, data.Length);
                    int pad = count * MS - data.Length;
                    if (pad > 0) mini.Write(new byte[pad], 0, pad);
                }
                else bigStreams.Add(n);
            }
            byte[] miniBytes = mini.ToArray();

            // sector layout: [mini stream][big streams][directory][mini FAT][FAT][DIFAT]
            List<byte[]> sectors = new List<byte[]>();
            List<uint> fat = new List<uint>();
            Func<byte[], uint> addChain = delegate(byte[] data)
            {
                if (data.Length == 0) return ENDOFCHAIN;
                int count = (data.Length + SS - 1) / SS;
                uint first = (uint)sectors.Count;
                for (int i = 0; i < count; i++)
                {
                    byte[] s = new byte[SS];
                    Array.Copy(data, i * SS, s, 0, Math.Min(SS, data.Length - i * SS));
                    sectors.Add(s);
                    fat.Add(i == count - 1 ? ENDOFCHAIN : (uint)(first + i + 1));
                }
                return first;
            };

            rootNode.Start = addChain(miniBytes);
            rootNode.Size = (uint)miniBytes.Length;
            foreach (DirNode n in bigStreams) n.Start = addChain(n.Entry.Data);

            // directory
            int dirCount = nodes.Count;
            int dirPerSector = SS / 128;
            int dirSectors = (dirCount + dirPerSector - 1) / dirPerSector;
            byte[] dir = new byte[dirSectors * SS];
            for (int i = 0; i < dirSectors * dirPerSector; i++)
            {
                int o = i * 128;
                if (i < dirCount) WriteDirEntry(dir, o, nodes[i]);
                else
                {
                    W32(dir, o + 68, NOSTREAM); W32(dir, o + 72, NOSTREAM); W32(dir, o + 76, NOSTREAM);
                }
            }
            uint firstDir = addChain(dir);

            // mini FAT
            uint firstMiniFat = ENDOFCHAIN;
            int miniFatSectors = 0;
            if (miniFat.Count > 0)
            {
                int perS = SS / 4;
                miniFatSectors = (miniFat.Count + perS - 1) / perS;
                byte[] mf = new byte[miniFatSectors * SS];
                for (int i = 0; i < miniFatSectors * perS; i++)
                    W32(mf, i * 4, i < miniFat.Count ? miniFat[i] : FREESECT);
                firstMiniFat = addChain(mf);
            }

            // FAT + DIFAT sizing
            int dataSectors = sectors.Count;
            int numFat = 1, numDifat = 0;
            while (true)
            {
                numDifat = numFat > 109 ? (numFat - 109 + 126) / 127 : 0;
                int total = dataSectors + numFat + numDifat;
                if (numFat * 128 >= total) break;
                numFat++;
            }
            uint fatStart = (uint)dataSectors;
            uint difatStart = (uint)(dataSectors + numFat);
            for (int i = 0; i < numFat; i++) fat.Add(FATSECT);
            for (int i = 0; i < numDifat; i++) fat.Add(DIFSECT);
            byte[] fatBytes = new byte[numFat * SS];
            for (int i = 0; i < numFat * 128; i++) W32(fatBytes, i * 4, i < fat.Count ? fat[i] : FREESECT);
            for (int i = 0; i < numFat; i++)
            {
                byte[] s = new byte[SS];
                Array.Copy(fatBytes, i * SS, s, 0, SS);
                sectors.Add(s);
            }
            // DIFAT sectors
            for (int k = 0; k < numDifat; k++)
            {
                byte[] s = new byte[SS];
                for (int j = 0; j < 127; j++)
                {
                    int fi = 109 + k * 127 + j;
                    W32(s, j * 4, fi < numFat ? (uint)(fatStart + fi) : FREESECT);
                }
                W32(s, 127 * 4, k == numDifat - 1 ? ENDOFCHAIN : (uint)(difatStart + k + 1));
                sectors.Add(s);
            }

            // header
            byte[] h = new byte[SS];
            Array.Copy(Signature, h, 8);
            W16(h, 0x18, 0x003E);
            W16(h, 0x1A, 0x0003);
            W16(h, 0x1C, 0xFFFE);
            W16(h, 0x1E, 9);
            W16(h, 0x20, 6);
            W32(h, 0x28, 0);
            W32(h, 0x2C, (uint)numFat);
            W32(h, 0x30, firstDir);
            W32(h, 0x34, 0);
            W32(h, 0x38, CUTOFF);
            W32(h, 0x3C, firstMiniFat);
            W32(h, 0x40, (uint)miniFatSectors);
            W32(h, 0x44, numDifat > 0 ? difatStart : ENDOFCHAIN);
            W32(h, 0x48, (uint)numDifat);
            for (int i = 0; i < 109; i++)
                W32(h, 0x4C + i * 4, i < numFat ? (uint)(fatStart + i) : FREESECT);

            MemoryStream outMs = new MemoryStream();
            outMs.Write(h, 0, SS);
            foreach (byte[] s in sectors) outMs.Write(s, 0, SS);
            return outMs.ToArray();
        }

        static void AddChildren(DirNode parent, List<DirNode> nodes)
        {
            List<CfbEntry> kids = new List<CfbEntry>(parent.Entry.Children);
            if (kids.Count == 0) return;
            kids.Sort(delegate(CfbEntry a, CfbEntry b) { return CompareNames(a.Name, b.Name); });
            List<DirNode> kidNodes = new List<DirNode>();
            foreach (CfbEntry k in kids)
            {
                DirNode n = new DirNode { Entry = k, Type = k.IsStorage ? 1 : 2 };
                nodes.Add(n);
                kidNodes.Add(n);
            }
            // balanced red-black tree from the sorted list
            int maxDepth = 0;
            uint rootIdx = BuildTree(kidNodes, nodes, 0, kidNodes.Count - 1, 0, ref maxDepth);
            bool perfect = kidNodes.Count == (1 << (maxDepth + 1)) - 1;
            if (!perfect) ColorDepth(kidNodes, nodes, rootIdx, 0, maxDepth);
            parent.Child = rootIdx;
            foreach (DirNode n in kidNodes)
                if (n.Type == 1) AddChildren(n, nodes);
        }

        static uint BuildTree(List<DirNode> sorted, List<DirNode> all, int lo, int hi, int depth, ref int maxDepth)
        {
            if (lo > hi) return NOSTREAM;
            int mid = (lo + hi) / 2;
            DirNode n = sorted[mid];
            n.Color = 1;
            if (depth > maxDepth) maxDepth = depth;
            n.Left = BuildTree(sorted, all, lo, mid - 1, depth + 1, ref maxDepth);
            n.Right = BuildTree(sorted, all, mid + 1, hi, depth + 1, ref maxDepth);
            return (uint)all.IndexOf(n);
        }

        static void ColorDepth(List<DirNode> sorted, List<DirNode> all, uint idx, int depth, int target)
        {
            if (idx == NOSTREAM) return;
            DirNode n = all[(int)idx];
            if (depth == target) n.Color = 0;
            ColorDepth(sorted, all, n.Left, depth + 1, target);
            ColorDepth(sorted, all, n.Right, depth + 1, target);
        }

        static void WriteDirEntry(byte[] dir, int o, DirNode n)
        {
            string name = n.Type == 5 ? "Root Entry" : n.Entry.Name;
            if (name.Length > 31) name = name.Substring(0, 31);
            byte[] nb = Encoding.Unicode.GetBytes(name);
            Array.Copy(nb, 0, dir, o, nb.Length);
            W16(dir, o + 64, nb.Length + 2);
            dir[o + 66] = (byte)n.Type;
            dir[o + 67] = n.Color;
            W32(dir, o + 68, n.Left);
            W32(dir, o + 72, n.Right);
            W32(dir, o + 76, n.Child);
            Array.Copy(n.Entry.Clsid ?? new byte[16], 0, dir, o + 80, 16);
            if (n.Type == 1)
            {
                W32(dir, o + 116, 0);
                W32(dir, o + 120, 0);
            }
            else
            {
                W32(dir, o + 116, n.Start);
                W32(dir, o + 120, n.Size);
            }
        }

        static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        static void W16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void W32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    }
}
