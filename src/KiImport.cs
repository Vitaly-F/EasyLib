// Import of parts downloaded in KiCad format (SnapEDA, Ultra Librarian, Component Search Engine /
// SamacSys, Mouser/DigiKey via those services, or any KiCad library files).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class ImportResult
    {
        public List<Part> Parts = new List<Part>();
        public List<string> Notes = new List<string>();
        public string Vendor = "";
        public List<AltiumSource> Altium = new List<AltiumSource>();   // ready-made Altium libraries: copied as they are
    }

    static class KiImport
    {
        const long MaxFile = 60L * 1024 * 1024;
        const int MaxEntries = 4000;

        class Files
        {
            public List<KeyValuePair<string, string>> Sym = new List<KeyValuePair<string, string>>();   // file, text
            public List<KeyValuePair<string, string>> Lib = new List<KeyValuePair<string, string>>();
            public Dictionary<string, string> Dcm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string, string>> Mod = new List<KeyValuePair<string, string>>();
            public Dictionary<string, byte[]> Step = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public StringBuilder Hints = new StringBuilder();
            public List<AltiumSource> Altium = new List<AltiumSource>();
        }

        static string Ext(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (string e in new string[] { ".kicad_sym", ".kicad_mod", ".lib", ".dcm", ".step", ".stp", ".zip", ".txt", ".htm", ".html", ".md", ".intlib", ".schlib", ".pcblib" })
                if (n.EndsWith(e)) return e;
            return "";
        }

        static string Base(string path)
        {
            string s = path.Replace('\\', '/');
            int i = s.LastIndexOf('/');
            if (i >= 0) s = s.Substring(i + 1);
            int d = s.LastIndexOf('.');
            return d > 0 ? s.Substring(0, d) : s;
        }

        static void Collect(Files f, string name, byte[] data, int depth)
        {
            string ext = Ext(name);
            switch (ext)
            {
                case ".kicad_sym": f.Sym.Add(new KeyValuePair<string, string>(name, Text(data))); break;
                case ".lib":
                    {
                        string t = Text(data);
                        if (t.StartsWith("EESchema-LIBRARY", StringComparison.Ordinal)) f.Lib.Add(new KeyValuePair<string, string>(name, t));
                        break;
                    }
                case ".dcm": f.Dcm[Base(name)] = Text(data); break;
                case ".kicad_mod": f.Mod.Add(new KeyValuePair<string, string>(name, Text(data))); break;
                case ".step":
                case ".stp":
                    if (!f.Step.ContainsKey(Base(name))) f.Step[Base(name)] = data;
                    break;
                case ".intlib":
                case ".schlib":
                case ".pcblib":
                    AltiumImport.Collect(f.Altium, Base(name) + ext, data);
                    break;
                case ".zip":
                    if (depth < 2) ReadZip(f, new MemoryStream(data), depth + 1);
                    break;
                case ".txt":
                case ".htm":
                case ".html":
                case ".md":
                    if (data.Length < 200000 && f.Hints.Length < 400000) f.Hints.Append(Text(data)).Append('\n');
                    break;
            }
            f.Hints.Append(name).Append('\n');
        }

        static string Text(byte[] d)
        {
            return new UTF8Encoding(false).GetString(d).TrimStart('﻿');
        }

        static void ReadZip(Files f, Stream s, int depth)
        {
            using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
            {
                int n = 0;
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    if (++n > MaxEntries) break;
                    if (e.Length > MaxFile || e.FullName.EndsWith("/")) continue;
                    if (Ext(e.FullName) == "") continue;
                    using (Stream es = e.Open())
                    {
                        MemoryStream ms = new MemoryStream();
                        es.CopyTo(ms);
                        Collect(f, e.FullName, ms.ToArray(), depth);
                    }
                }
            }
        }

        // true if a ZIP contains KiCad symbols or footprints (quick check for the Downloads watcher)
        public static bool LooksLikeKiCad(string zipPath)
        {
            try
            {
                using (FileStream fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    int n = 0;
                    foreach (ZipArchiveEntry e in z.Entries)
                    {
                        if (++n > MaxEntries) break;
                        string x = Ext(e.FullName);
                        if (x == ".kicad_sym" || x == ".kicad_mod" || x == ".lib" || x == ".intlib" || x == ".schlib" || x == ".pcblib") return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static ImportResult FromPaths(IEnumerable<string> paths)
        {
            Files f = new Files();
            List<string> names = new List<string>();
            foreach (string path in paths)
            {
                names.Add(Path.GetFileName(path.TrimEnd('\\', '/')));
                if (Directory.Exists(path))
                {
                    int n = 0;
                    foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        if (++n > MaxEntries) break;
                        FileInfo fi = new FileInfo(file);
                        if (fi.Length > MaxFile || Ext(file) == "") continue;
                        Collect(f, file, File.ReadAllBytes(file), 0);
                    }
                }
                else if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > MaxFile * 4) throw new Exception("файл слишком большой: " + Path.GetFileName(path));
                    byte[] data;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        MemoryStream ms = new MemoryStream();
                        fs.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    Collect(f, path, data, 0);
                }
            }
            return Build(f, string.Join(", ", names.ToArray()));
        }

        static string DetectVendor(Files f)
        {
            string h = f.Hints.ToString().ToLowerInvariant();
            foreach (KeyValuePair<string, string> kv in f.Sym) h += kv.Value.Length > 4000 ? kv.Value.Substring(0, 4000).ToLowerInvariant() : kv.Value.ToLowerInvariant();
            if (h.Contains("snapeda") || h.Contains("snapmagic")) return "SnapEDA";
            if (h.Contains("ultra librarian") || h.Contains("ultralibrarian")) return "Ultra Librarian";
            if (h.Contains("samacsys") || h.Contains("componentsearchengine") || h.Contains("component search engine")) return "Component Search Engine";
            return "";
        }

        static string ModName(string text, string file)
        {
            Match m = Regex.Match(text, "\\((?:footprint|module)\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s\\)]+)");
            if (m.Success) return m.Groups[1].Value.Trim('"');
            return Base(file);
        }

        static ImportResult Build(Files f, string sourceName)
        {
            ImportResult res = new ImportResult();
            res.Vendor = DetectVendor(f);
            res.Altium = f.Altium;
            string source = (res.Vendor != "" ? res.Vendor + ", " : "") + "imported from " + sourceName;

            // footprints by name
            Dictionary<string, string> mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> modByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in f.Mod)
            {
                string n = ModName(kv.Value, kv.Key);
                if (!mods.ContainsKey(n)) mods[n] = kv.Value;
                modByFile[Base(kv.Key)] = kv.Value;
            }
            HashSet<string> usedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // symbols: KiCad 6+ first, legacy .lib only when there is nothing newer
            List<Part> parts = new List<Part>();
            Dictionary<string, SNode> all = new Dictionary<string, SNode>(StringComparer.Ordinal);
            List<SNode> order = new List<SNode>();
            foreach (KeyValuePair<string, string> kv in f.Sym)
            {
                try
                {
                    foreach (SNode s in KiCad.SymbolsOf(kv.Value))
                    {
                        if (!all.ContainsKey(s.S(1))) { all[s.S(1)] = s; order.Add(s); }
                    }
                }
                catch (Exception ex) { res.Notes.Add(Path.GetFileName(kv.Key) + ": " + ex.Message); }
            }
            Func<string, SNode> lookup = delegate(string n) { SNode r; return all.TryGetValue(n, out r) ? r : null; };
            foreach (SNode s in order)
            {
                Dictionary<string, string> props = KiCad.MergedProps(s, lookup);
                string reference;
                props.TryGetValue("Reference", out reference);
                if ((reference ?? "").StartsWith("#")) continue;             // power flags and the like
                Part p = NewPart(source, res.Vendor);
                KiCad.ConvertSymbol(p, s, lookup);
                parts.Add(p);
            }
            if (parts.Count == 0)
            {
                foreach (KeyValuePair<string, string> kv in f.Lib)
                {
                    Dictionary<string, string[]> dcm = null;
                    string dt;
                    if (f.Dcm.TryGetValue(Base(kv.Key), out dt)) dcm = KiCad.ParseDcm(dt);
                    else if (f.Dcm.Count == 1) foreach (string v in f.Dcm.Values) dcm = KiCad.ParseDcm(v);
                    foreach (KiCad.LegacySym ls in KiCad.ParseLegacyLib(kv.Value))
                    {
                        if (ls.Ref.StartsWith("#")) continue;
                        List<string> names = new List<string> { ls.Name };
                        names.AddRange(ls.Aliases);
                        foreach (string n in names)
                        {
                            string[] d = null;
                            if (dcm != null) dcm.TryGetValue(n, out d);
                            Part p = NewPart(source, res.Vendor);
                            KiCad.ConvertLegacy(p, ls, n, d);
                            parts.Add(p);
                        }
                    }
                }
            }

            // attach footprints and 3D models
            foreach (Part p in parts)
            {
                string text = null;
                if (p.Footprint != "")
                {
                    if (!mods.TryGetValue(p.Footprint, out text)) modByFile.TryGetValue(p.Footprint, out text);
                }
                if (text == null && f.Mod.Count == 1) text = f.Mod[0].Value;
                if (text == null && parts.Count == 1 && f.Mod.Count > 1) text = f.Mod[0].Value;
                if (text != null)
                {
                    p.Footprint = "";
                    AttachFootprint(p, text, f, res);
                    usedMods.Add(p.Footprint);
                }
                else if (p.Footprint != "") res.Notes.Add(p.Title + ": футпринт «" + p.Footprint + "» не найден в архиве, добавится только символ");
                KiCad.FinishInfo(p);
                res.Parts.Add(p);
            }
            // footprints nobody uses -> footprint only
            foreach (KeyValuePair<string, string> kv in mods)
            {
                if (usedMods.Contains(kv.Key)) continue;
                Part p = NewPart(source, res.Vendor);
                p.NoSymbol = true;
                AttachFootprint(p, kv.Value, f, res);
                p.Title = p.Footprint;
                p.Desc = p.FootprintDesc;
                p.Info["Description"] = p.Desc;
                p.Info["MPN"] = p.Footprint;
                KiCad.FinishInfo(p);
                res.Parts.Add(p);
            }
            if (res.Parts.Count == 0 && res.Altium.Count == 0) res.Notes.Add("в файле нет символов и футпринтов KiCad (.kicad_sym, .lib, .kicad_mod) или библиотек Altium (.IntLib, .SchLib, .PcbLib).");
            return res;
        }

        static Part NewPart(string source, string vendor)
        {
            Part p = new Part();
            p.Source = "import";
            p.Official = false;
            p.Params["Source"] = source;
            p.Info["Class"] = vendor != "" ? vendor : "Импорт";
            p.Info["Official"] = "import";
            p.Info["Vendor"] = vendor;
            return p;
        }

        static void AttachFootprint(Part p, string text, Files f, ImportResult res)
        {
            KiModel model;
            try { KiCad.ConvertFootprint(p, text, out model); }
            catch (Exception ex) { res.Notes.Add("футпринт не прочитан: " + ex.Message); return; }
            byte[] step = null;
            string stepBase = null;
            if (model != null)
            {
                string b = KiCad.ModelBaseName(model.Path);
                if (b != "" && f.Step.TryGetValue(b, out step)) stepBase = b;
            }
            if (step == null && f.Step.Count == 1) foreach (KeyValuePair<string, byte[]> kv in f.Step) { step = kv.Value; stepBase = kv.Key; }
            if (step == null && f.Step.ContainsKey(p.Footprint)) { step = f.Step[p.Footprint]; stepBase = p.Footprint; }
            if (step != null)
            {
                try
                {
                    p.StepData = step;
                    p.StepName = stepBase + ".step";
                    p.StepPlacement = KiCad.Place(step, model ?? new KiModel());
                    p.Info["Model3D"] = "yes";
                }
                catch (Exception ex) { p.StepData = null; res.Notes.Add("3D-модель не прочитана: " + ex.Message); }
            }
        }
    }

    // adds a part of any origin to the libraries (downloads the 3D model when needed)
    static class PartAdder
    {
        public static LibResult Add(Part p, string schPath, string pcbPath, bool with3D, out string note)
        {
            note = "";
            byte[] step = null;
            ModelPlacement mp = null;
            string stepName = null;
            if (with3D)
            {
                try
                {
                    if (p.StepData != null)
                    {
                        step = p.StepData; mp = p.StepPlacement; stepName = p.StepName;
                    }
                    else if (p.Source == "kicad" && p.KiModel != null)
                    {
                        step = KiCadLib.Step(p.KiModel);
                        if (step != null) { mp = KiCad.Place(step, p.KiModel); stepName = KiCad.ModelBaseName(p.KiModel.Path) + ".step"; }
                        else note = " (STEP-модель в библиотеке KiCad не найдена)";
                    }
                    else if (p.ModelUuid != "")
                    {
                        step = EasyEda.Step(p.ModelUuid);
                        if (step != null)
                        {
                            mp = Step.Compute(p, step);
                            stepName = (p.ModelTitle != "" ? p.ModelTitle : (p.Lcsc != "" ? p.Lcsc : "model")) + ".step";
                        }
                        else note = " (у детали нет STEP-модели)";
                    }
                }
                catch (Exception ex) { note = " (3D-модель не скачалась: " + ex.Message + ")"; step = null; mp = null; }
            }
            if (step == null || mp == null) { step = null; mp = null; stepName = null; }
            return AltiumLib.Add(p, schPath, pcbPath, step, stepName, mp);
        }
    }
}
