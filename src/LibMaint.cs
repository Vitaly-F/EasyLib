// Library maintenance: what takes the space, deleting parts, removing 3D models, backups,
// and a lock file so that two EasyLib windows (or two colleagues on a shared folder)
// never write the same library at the same time.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace EasyLib
{
    class LibBusyException : IOException
    {
        public LibBusyException(string m) : base(m) { }
    }

    // exclusive lock on a library folder: EasyLib.lock, deleted when released
    class LibLock : IDisposable
    {
        FileStream fs;

        LibLock(FileStream f) { fs = f; }

        public static LibLock Acquire(string libDir) { return Acquire(libDir, 20000); }

        public static LibLock Acquire(string libDir, int timeoutMs)
        {
            Directory.CreateDirectory(libDir);
            string path = Path.Combine(libDir, "EasyLib.lock");
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                try
                {
                    // CreateNew is atomic on every file system (also network shares); the file goes away
                    // when the handle is closed, on Windows even if the process crashes
                    FileStream f = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    try
                    {
                        byte[] who = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + " " + Environment.UserName + " " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                        f.Write(who, 0, who.Length);
                        f.Flush();
                    }
                    catch { }
                    return new LibLock(f);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    bool exists = File.Exists(path);
                    // no lock file and still no access: a read-only folder; saving will report it clearly
                    if (!exists && ex is UnauthorizedAccessException) return new LibLock(null);
                    // a lock left behind by a crashed EasyLib (where the system does not remove it)
                    try { if (exists && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(3)) File.Delete(path); } catch { }
                    if (DateTime.UtcNow > until)
                        throw new LibBusyException("Библиотеку сейчас записывает другой EasyLib (возможно, у коллеги, если папка общая). Повторите через несколько секунд.");
                    Thread.Sleep(150);
                }
            }
        }

        public void Dispose()
        {
            if (fs != null) { try { fs.Dispose(); } catch { } fs = null; }
        }
    }

    class LibPart
    {
        public string Symbol = "", Footprint = "", Description = "";
        public bool FootprintMissing;          // the symbol links to a footprint that is not in the PcbLib
        public long SymbolBytes, FootprintBytes, ModelBytes;
        public int Models;
        public int SharedFootprint;            // other symbols using the same footprint
        public bool SharedModel;               // the 3D model is used by another footprint too
        public long Total { get { return SymbolBytes + FootprintBytes + ModelBytes; } }
    }

    class LibReport
    {
        public List<LibPart> Parts = new List<LibPart>();
        public long SchFileBytes, PcbFileBytes, ModelBytes, UnusedModelBytes;
        public int Symbols, Footprints, ModelCount, UnusedModels, FootprintsWithoutSymbol;
        public string Error = "";
    }

    static class LibMaint
    {
        public static string SchPath(string dir) { return Path.Combine(dir, "EasyLib.SchLib"); }
        public static string PcbPath(string dir) { return Path.Combine(dir, "EasyLib.PcbLib"); }

        public static LibReport Analyze(string dir)
        {
            LibReport r = new LibReport();
            string sch = SchPath(dir), pcb = PcbPath(dir);
            if (File.Exists(sch)) r.SchFileBytes = new FileInfo(sch).Length;
            if (File.Exists(pcb)) r.PcbFileBytes = new FileInfo(pcb).Length;
            CfbEntry s = null, p = null;
            try { if (File.Exists(sch)) s = AltiumLib.Load(sch, false); }
            catch (Exception ex) { r.Error = "EasyLib.SchLib не читается: " + ex.Message; }
            try { if (File.Exists(pcb)) p = AltiumLib.Load(pcb, true); }
            catch (Exception ex) { r.Error += (r.Error != "" ? " " : "") + "EasyLib.PcbLib не читается: " + ex.Message; }

            Dictionary<string, List<string>> fpModels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> fpBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> modelBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> modelUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> fps = new List<string>();
            if (p != null)
            {
                fps = PcbLibWriter.FootprintNames(p);
                foreach (PcbLibWriter.ModelInfo m in PcbLibWriter.Models(p))
                {
                    r.ModelCount++;
                    r.ModelBytes += m.Bytes;
                    if (m.Id == "") continue;
                    // a second copy of the same model is dead weight
                    if (modelBytes.ContainsKey(m.Id)) { r.UnusedModels++; r.UnusedModelBytes += m.Bytes; continue; }
                    modelBytes[m.Id] = m.Bytes; modelUse[m.Id] = 0;
                }
                foreach (string f in fps)
                {
                    List<string> ids = PcbLibWriter.FootprintModelIds(p, f);
                    fpModels[f] = ids;
                    fpBytes[f] = PcbLibWriter.FootprintBytes(p, f);
                    foreach (string id in ids) { int u; modelUse.TryGetValue(id, out u); modelUse[id] = u + 1; }
                }
                foreach (KeyValuePair<string, int> kv in modelUse)
                    if (kv.Value == 0) { r.UnusedModels++; r.UnusedModelBytes += modelBytes[kv.Key]; }
                r.Footprints = fps.Count;
            }

            Dictionary<string, int> fpUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (s != null)
            {
                foreach (string sym in SchLibWriter.SymbolNames(s))
                {
                    LibPart lp = new LibPart();
                    lp.Symbol = sym;
                    lp.Footprint = SchLibWriter.SymbolFootprint(s, sym) ?? "";
                    lp.Description = SchLibWriter.SymbolDescription(s, sym);
                    lp.SymbolBytes = SchLibWriter.SymbolBytes(s, sym);
                    r.Parts.Add(lp);
                    r.Symbols++;
                    if (lp.Footprint != "") { int u; fpUse.TryGetValue(lp.Footprint, out u); fpUse[lp.Footprint] = u + 1; }
                }
            }
            foreach (LibPart lp in r.Parts)
            {
                if (lp.Footprint == "") continue;
                List<string> ids;
                if (!fpModels.TryGetValue(lp.Footprint, out ids)) { lp.FootprintMissing = p != null; continue; }
                lp.FootprintBytes = fpBytes[lp.Footprint];
                foreach (string id in ids)
                {
                    long b;
                    if (modelBytes.TryGetValue(id, out b)) { lp.ModelBytes += b; lp.Models++; }
                    int u;
                    if (modelUse.TryGetValue(id, out u) && u > 1) lp.SharedModel = true;
                }
                lp.SharedFootprint = fpUse[lp.Footprint] - 1;
            }
            foreach (string f in fps)
            {
                if (fpUse.ContainsKey(f)) continue;
                LibPart lp = new LibPart();
                lp.Footprint = f;
                lp.FootprintBytes = fpBytes[f];
                foreach (string id in fpModels[f]) { long b; if (modelBytes.TryGetValue(id, out b)) { lp.ModelBytes += b; lp.Models++; } }
                r.Parts.Add(lp);
                r.FootprintsWithoutSymbol++;
            }
            r.Parts.Sort(delegate(LibPart a, LibPart b) { return b.Total.CompareTo(a.Total); });
            return r;
        }

        // copies both libraries to <dir>\Backups\<date time>\ and keeps the last 3 copies
        public static string Backup(string dir)
        {
            string root = Path.Combine(dir, "Backups");
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string target = Path.Combine(root, stamp);
            for (int i = 2; Directory.Exists(target); i++) target = Path.Combine(root, stamp + "_" + i);
            Directory.CreateDirectory(target);
            foreach (string f in new string[] { SchPath(dir), PcbPath(dir) })
                if (File.Exists(f)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            try
            {
                List<string> all = new List<string>(Directory.GetDirectories(root));
                all.Sort(StringComparer.Ordinal);
                for (int i = 0; i < all.Count - 3; i++) Directory.Delete(all[i], true);
            }
            catch { }
            return target;
        }

        public static List<string> Backups(string dir)
        {
            string root = Path.Combine(dir, "Backups");
            List<string> all = Directory.Exists(root) ? new List<string>(Directory.GetDirectories(root)) : new List<string>();
            all.Sort(StringComparer.Ordinal);
            all.Reverse();
            return all;
        }

        // the current libraries are backed up first, so a restore can be undone too
        public static void Restore(string dir, string backupFolder)
        {
            using (LibLock.Acquire(dir))
            {
                Dictionary<string, CfbEntry> load = new Dictionary<string, CfbEntry>();
                foreach (string name in new string[] { "EasyLib.SchLib", "EasyLib.PcbLib" })
                {
                    string src = Path.Combine(backupFolder, name);
                    if (File.Exists(src)) load[name] = Cfb.Read(File.ReadAllBytes(src));      // must be readable
                }
                if (load.Count == 0) throw new FileNotFoundException("в копии нет файлов библиотек");
                Backup(dir);
                foreach (KeyValuePair<string, CfbEntry> kv in load) AltiumLib.Save(kv.Value, Path.Combine(dir, kv.Key));
            }
        }

        public class Result
        {
            public int Symbols, Footprints, Stripped, Models;
            public long BytesBefore, BytesAfter;
            public string Backup = "";
        }

        // deletes the symbols, footprints that no remaining symbol uses (plus the given
        // footprint-only entries) and 3D models that nothing uses any more
        public static Result Delete(string dir, ICollection<string> symbols, ICollection<string> footprintsOnly)
        {
            return Change(dir, symbols, footprintsOnly, null);
        }

        // removes the 3D models from the given footprints
        public static Result Strip3D(string dir, ICollection<string> footprints)
        {
            return Change(dir, null, null, footprints);
        }

        public static Result PruneModels(string dir)
        {
            return Change(dir, null, null, null);
        }

        static Result Change(string dir, ICollection<string> symbols, ICollection<string> footprintsOnly, ICollection<string> strip)
        {
            Result res = new Result();
            string sch = SchPath(dir), pcb = PcbPath(dir);
            using (LibLock.Acquire(dir))
            {
                res.BytesBefore = (File.Exists(sch) ? new FileInfo(sch).Length : 0) + (File.Exists(pcb) ? new FileInfo(pcb).Length : 0);
                res.Backup = Backup(dir);
                CfbEntry s = File.Exists(sch) ? AltiumLib.Load(sch, false) : null;
                CfbEntry p = File.Exists(pcb) ? AltiumLib.Load(pcb, true) : null;
                HashSet<string> fpDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (s != null && symbols != null && symbols.Count > 0)
                {
                    HashSet<string> del = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
                    HashSet<string> stillUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string sym in SchLibWriter.SymbolNames(s))
                    {
                        string fp = SchLibWriter.SymbolFootprint(s, sym) ?? "";
                        if (fp == "") continue;
                        if (del.Contains(sym)) fpDelete.Add(fp); else stillUsed.Add(fp);
                    }
                    fpDelete.RemoveWhere(delegate(string f) { return stillUsed.Contains(f); });
                    res.Symbols = SchLibWriter.DeleteSymbols(s, symbols);
                    if (res.Symbols > 0) AltiumLib.Save(s, sch);
                }
                if (footprintsOnly != null) foreach (string f in footprintsOnly) fpDelete.Add(f);
                if (p != null)
                {
                    bool changed = false;
                    if (fpDelete.Count > 0) { res.Footprints = PcbLibWriter.DeleteFootprints(p, fpDelete); changed |= res.Footprints > 0; }
                    if (strip != null && strip.Count > 0) { res.Stripped = PcbLibWriter.StripBodies(p, strip); changed |= res.Stripped > 0; }
                    res.Models = PcbLibWriter.PruneModels(p);
                    changed |= res.Models > 0;
                    if (changed) AltiumLib.Save(p, pcb);
                }
                res.BytesAfter = (File.Exists(sch) ? new FileInfo(sch).Length : 0) + (File.Exists(pcb) ? new FileInfo(pcb).Length : 0);
            }
            return res;
        }

        public static string Size(long bytes)
        {
            CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
            if (bytes >= 1024L * 1024) return (bytes / 1048576.0).ToString(bytes >= 100L * 1048576 ? "0" : "0.0", ru) + " МБ";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0", ru) + " КБ";
            return bytes + " Б";
        }
    }
}
