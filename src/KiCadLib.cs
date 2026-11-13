// Official KiCad libraries (https://gitlab.com/kicad/libraries, CC-BY-SA 4.0 with an exception
// for designs made with them). The symbol repository is downloaded once as a ZIP and indexed
// locally; footprints and STEP models are fetched one by one when a part is used.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    static class KiCadLib
    {
        public const string License = "KiCad Libraries (https://gitlab.com/kicad/libraries), CC-BY-SA 4.0 with design exception";
        static readonly string BaseUrl = Env("EASYLIB_KICAD", "https://gitlab.com/kicad/libraries");
        const string Branch = "master";

        class Entry
        {
            public string Lib, Name, Path, Footprint, Desc, Keywords, Ref, Extends;
            public string Hay;
        }

        static List<Entry> index;
        static readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>();
        static readonly object Gate = new object();

        static string Env(string name, string def)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? def : v.TrimEnd('/');
        }

        public static string Dir
        {
            get
            {
                string d = System.IO.Path.Combine(EasyEda.CacheDir, "kicad");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string ZipPath { get { return System.IO.Path.Combine(Dir, "kicad-symbols.zip"); } }
        static string IndexPath { get { return System.IO.Path.Combine(Dir, "index.tsv"); } }

        public static bool HasIndex { get { return File.Exists(IndexPath) && File.Exists(ZipPath); } }

        public static DateTime IndexDate { get { return File.Exists(IndexPath) ? File.GetLastWriteTime(IndexPath) : DateTime.MinValue; } }

        public static void Reset()
        {
            lock (Gate)
            {
                index = null;
                byKey.Clear();
                try { File.Delete(IndexPath); } catch { }
                try { File.Delete(ZipPath); } catch { }
            }
        }

        static HttpWebRequest Request(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "EasyLib/2.2 (KiCad library browser)";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 120000;
            return req;
        }

        static byte[] Get(string url)
        {
            HttpWebRequest req = Request(url);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            {
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // downloads the symbol library (once) and builds the search index
        public static void EnsureIndex(Action<string> progress)
        {
            lock (Gate)
            {
                if (index != null) return;
                if (!File.Exists(ZipPath))
                {
                    string url = BaseUrl + "/kicad-symbols/-/archive/" + Branch + "/kicad-symbols-" + Branch + ".zip";
                    string tmp = ZipPath + ".part";
                    HttpWebRequest req = Request(url);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buf = new byte[81920];
                        long total = 0, next = 0;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            total += n;
                            if (total >= next && progress != null)
                            {
                                progress("Скачиваю библиотеку KiCad: " + (total / 1048576) + " МБ…");
                                next = total + 1048576;
                            }
                        }
                    }
                    if (File.Exists(ZipPath)) File.Delete(ZipPath);
                    File.Move(tmp, ZipPath);
                    try { File.Delete(IndexPath); } catch { }
                }
                if (!File.Exists(IndexPath))
                {
                    if (progress != null) progress("Индексирую библиотеку KiCad…");
                    BuildIndex(progress);
                }
                LoadIndex();
            }
        }

        static string Esc(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        static void BuildIndex(Action<string> progress)
        {
            List<Entry> list = new List<Entry>();
            using (ZipArchive z = ZipFile.OpenRead(ZipPath))
            {
                int done = 0, total = z.Entries.Count;
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    done++;
                    if (!e.FullName.EndsWith(".kicad_sym", StringComparison.OrdinalIgnoreCase)) continue;
                    if (progress != null && done % 1000 == 0) progress("Индексирую библиотеку KiCad: " + (done * 100 / Math.Max(1, total)) + "%…");
                    string lib = LibOf(e.FullName);
                    string text;
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8)) text = r.ReadToEnd();
                    List<SNode> syms;
                    try { syms = KiCad.SymbolsOf(text); } catch { continue; }
                    foreach (SNode s in syms)
                    {
                        Dictionary<string, string> p = KiCad.Props(s);
                        string v;
                        Entry en = new Entry();
                        en.Lib = lib;
                        en.Name = s.S(1);
                        en.Path = e.FullName;
                        en.Footprint = p.TryGetValue("Footprint", out v) ? v : "";
                        en.Desc = p.TryGetValue("Description", out v) && v != "" ? v : p.TryGetValue("ki_description", out v) ? v : "";
                        en.Keywords = p.TryGetValue("ki_keywords", out v) ? v : "";
                        en.Ref = p.TryGetValue("Reference", out v) ? v : "";
                        SNode ext = s.Find("extends");
                        en.Extends = ext != null ? ext.S(1) : "";
                        list.Add(en);
                    }
                }
            }
            // derived symbols inherit footprint, description and reference from their parent
            Dictionary<string, Entry> map = new Dictionary<string, Entry>();
            foreach (Entry e in list) map[e.Lib + ":" + e.Name] = e;
            foreach (Entry e in list)
            {
                Entry cur = e;
                for (int i = 0; i < 6 && cur.Extends != ""; i++)
                {
                    Entry par;
                    if (!map.TryGetValue(e.Lib + ":" + cur.Extends, out par)) break;
                    if (e.Footprint == "") e.Footprint = par.Footprint;
                    if (e.Desc == "") e.Desc = par.Desc;
                    if (e.Keywords == "") e.Keywords = par.Keywords;
                    if (e.Ref == "") e.Ref = par.Ref;
                    cur = par;
                }
            }
            using (StreamWriter w = new StreamWriter(IndexPath, false, new UTF8Encoding(false)))
                foreach (Entry e in list)
                {
                    if (e.Ref.StartsWith("#")) continue;            // power symbols and flags
                    w.WriteLine(string.Join("\t", new string[] { Esc(e.Lib), Esc(e.Name), Esc(e.Path), Esc(e.Footprint), Esc(e.Desc), Esc(e.Keywords), Esc(e.Ref), Esc(e.Extends) }));
                }
        }

        // "kicad-symbols-master/MCU_ST_STM32F1.kicad_symdir/STM32F103C8Tx.kicad_sym" -> "MCU_ST_STM32F1"
        static string LibOf(string path)
        {
            string[] parts = path.Replace('\\', '/').Split('/');
            if (parts.Length >= 2 && parts[parts.Length - 2].EndsWith(".kicad_symdir", StringComparison.OrdinalIgnoreCase))
                return parts[parts.Length - 2].Substring(0, parts[parts.Length - 2].Length - ".kicad_symdir".Length);
            string f = parts[parts.Length - 1];
            return f.Substring(0, f.Length - ".kicad_sym".Length);
        }

        static void LoadIndex()
        {
            List<Entry> list = new List<Entry>();
            byKey.Clear();
            foreach (string line in File.ReadAllLines(IndexPath, Encoding.UTF8))
            {
                string[] t = line.Split('\t');
                if (t.Length < 8) continue;
                Entry e = new Entry { Lib = t[0], Name = t[1], Path = t[2], Footprint = t[3], Desc = t[4], Keywords = t[5], Ref = t[6], Extends = t[7] };
                e.Hay = (e.Name + " " + e.Lib + " " + e.Desc + " " + e.Keywords + " " + e.Footprint).ToLowerInvariant();
                list.Add(e);
                byKey[Key(e)] = e;
            }
            index = list;
        }

        static string Key(Entry e) { return "kicad:" + e.Lib + ":" + e.Name; }

        public static int Count { get { return index != null ? index.Count : 0; } }

        public static SearchResult Search(string query, int page)
        {
            SearchResult r = new SearchResult();
            if (index == null) return r;
            string q = (query ?? "").Trim().ToLowerInvariant();
            string[] tokens = q.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return r;
            List<KeyValuePair<int, Entry>> hits = new List<KeyValuePair<int, Entry>>();
            foreach (Entry e in index)
            {
                bool ok = true;
                foreach (string t in tokens) if (e.Hay.IndexOf(t, StringComparison.Ordinal) < 0) { ok = false; break; }
                if (!ok) continue;
                string n = e.Name.ToLowerInvariant();
                int score = n == q ? 0 : n.StartsWith(tokens[0]) ? 1 : n.Contains(tokens[0]) ? 2 : 3;
                if (e.Footprint == "") score += 4;          // symbols without a footprint go last
                hits.Add(new KeyValuePair<int, Entry>(score, e));
            }
            hits.Sort(delegate(KeyValuePair<int, Entry> a, KeyValuePair<int, Entry> b)
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase);
            });
            const int size = 50;
            r.TotalPages = Math.Max(1, (hits.Count + size - 1) / size);
            for (int i = (page - 1) * size; i < Math.Min(hits.Count, page * size); i++)
            {
                Entry e = hits[i].Value;
                SearchItem it = new SearchItem();
                it.Source = "kicad";
                it.Uuid = Key(e);
                it.Title = e.Name;
                it.Mpn = e.Name;
                it.Manufacturer = e.Lib;
                it.Package = KiCad.FootprintName(e.Footprint);
                it.Class = "KiCad";
                r.Items.Add(it);
            }
            return r;
        }

        // symbol (+ footprint) of a search result
        public static Part Load(SearchItem it)
        {
            Entry e;
            lock (Gate)
            {
                if (index == null) EnsureIndex(null);
                if (!byKey.TryGetValue(it.Uuid, out e)) throw new Exception("символ не найден в индексе KiCad");
            }
            Dictionary<string, SNode> syms = new Dictionary<string, SNode>();
            string dir = e.Path.Contains("/") ? e.Path.Substring(0, e.Path.LastIndexOf('/') + 1) : "";
            using (ZipArchive z = ZipFile.OpenRead(ZipPath))
            {
                Action<string> read = delegate(string path)
                {
                    ZipArchiveEntry ze = z.GetEntry(path);
                    if (ze == null) return;
                    using (StreamReader r = new StreamReader(ze.Open(), Encoding.UTF8))
                        foreach (SNode s in KiCad.SymbolsOf(r.ReadToEnd())) if (!syms.ContainsKey(s.S(1))) syms[s.S(1)] = s;
                };
                read(e.Path);
                // "extends" chain: in .kicad_symdir libraries every symbol is its own file
                string want = e.Extends;
                for (int i = 0; i < 6 && want != "" && !syms.ContainsKey(want); i++)
                {
                    read(dir + want + ".kicad_sym");
                    SNode par;
                    want = syms.TryGetValue(want, out par) && par.Find("extends") != null ? par.Find("extends").S(1) : "";
                }
            }
            SNode sym;
            if (!syms.TryGetValue(e.Name, out sym)) throw new Exception("символ не прочитан: " + e.Name);
            Func<string, SNode> lookup = delegate(string n) { SNode s; return syms.TryGetValue(n, out s) ? s : null; };

            Part p = new Part();
            p.Source = "kicad";
            p.Official = false;
            KiCad.ConvertSymbol(p, sym, lookup);
            p.Params["Source"] = License;
            p.Info["Official"] = "kicad";
            p.Info["Class"] = "KiCad";
            p.Info["Lib"] = e.Lib;
            p.Info["URL"] = BaseUrl + "/kicad-symbols/-/blob/" + Branch + "/" + StripRoot(e.Path);
            if (p.Desc == "") { p.Desc = e.Desc; p.Info["Description"] = e.Desc; }

            string fp = e.Footprint;
            p.Footprint = "";
            string text = null;
            if (fp != "" && KiCad.FootprintLib(fp) != "")
            {
                try { text = Footprint(KiCad.FootprintLib(fp), KiCad.FootprintName(fp)); }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    if (r == null || r.StatusCode != HttpStatusCode.NotFound) throw;
                    text = null;          // e.g. third-party "PCM_" libraries that are not in the official repository
                }
            }
            if (text != null)
            {
                KiModel model;
                KiCad.ConvertFootprint(p, text, out model);
                if (model != null && KiCad.OfficialModelPath(model.Path) != "") p.KiModel = model;
            }
            else p.Info["NoFootprint"] = "yes";
            KiCad.FinishInfo(p);
            return p;
        }

        static string StripRoot(string path)
        {
            int i = path.IndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        static string Seg(string s) { return Uri.EscapeDataString(s); }

        public static string Footprint(string lib, string name)
        {
            string dir = System.IO.Path.Combine(Dir, "footprints", Safe(lib));
            string file = System.IO.Path.Combine(dir, Safe(name) + ".kicad_mod");
            if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);
            byte[] data = Get(BaseUrl + "/kicad-footprints/-/raw/" + Branch + "/" + Seg(lib + ".pretty") + "/" + Seg(name + ".kicad_mod"));
            string text = new UTF8Encoding(false).GetString(data);
            if (!text.TrimStart().StartsWith("(")) throw new Exception("футпринт KiCad не скачался: " + lib + ":" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return text;
        }

        public static byte[] Step(KiModel m)
        {
            string rel = KiCad.OfficialModelPath(m.Path);
            if (rel == "") return null;
            string file = System.IO.Path.Combine(Dir, "3d", Safe(rel.Replace('/', '_')));
            if (File.Exists(file) && new FileInfo(file).Length > 100) return File.ReadAllBytes(file);
            string[] parts = rel.Split('/');
            byte[] data;
            try { data = Get(BaseUrl + "/kicad-packages3D/-/raw/" + Branch + "/" + Seg(parts[0]) + "/" + Seg(parts[1])); }
            catch (WebException) { return null; }
            string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 200));
            if (!head.Contains("ISO-10303-21")) return null;           // missing model or a Git LFS pointer
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            File.WriteAllBytes(file, data);
            return data;
        }

        static string Safe(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s) sb.Append(Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
