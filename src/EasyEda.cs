// LCSC / EasyEDA web API client with a local cache.
// Data source: JLCEDA/EasyEDA Official Library and the EasyEDA user library (https://easyeda.com).
//
// Notes on rate limiting: easyeda.com sits behind CloudFront and blocks an IP for a while when it
// sees many requests to /api/products/{C}/components. Parts are therefore fetched through
// /api/components/{uuid} (the endpoint the EasyEDA editor itself uses), requests are spaced out,
// and everything downloaded is cached on disk.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class SearchItem
    {
        public string Source = "lcsc";    // "lcsc" (official LCSC library) or "user" (EasyEDA user library)
        public string Uuid = "", Lcsc = "", Title = "", Mpn = "", Manufacturer = "", Package = "", Value = "", Class = "", Author = "";
        public string Stock = "", Price = "", MinQty = "";   // from the search list, when the server gives them
        public bool Direct;                                  // not found by search: load straight by the LCSC number
        public bool NoCad;                                   // LCSC sells it, but EasyEDA has no symbol / footprint
        public int Score;                                    // how well it matches the query (see SearchRank)

        public string Key { get { return Uuid != "" ? Uuid : Lcsc; } }
    }

    class SearchResult
    {
        public List<SearchItem> Items = new List<SearchItem>();
        public int TotalPages = 1;
    }

    class BlockedException : Exception
    {
        public BlockedException(string m) : base(m) { }
    }

    class NoCadException : Exception
    {
        public NoCadException(SearchItem it) : base(Text(it)) { }

        static string Text(SearchItem it)
        {
            string what = it.Mpn != "" && !it.Mpn.StartsWith(it.Lcsc) ? it.Mpn + (it.Manufacturer != "" ? " (" + it.Manufacturer + ")" : "") : it.Lcsc;
            long st;
            string stock = long.TryParse(it.Stock, out st) ? ", на складе " + st.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")) + " шт." : "";
            return "У LCSC деталь есть: " + what + stock + " Но символа и футпринта для неё в EasyEDA нет, поэтому поиск её и не находит. " +
                   "Нажмите SnapEDA, Ultra Librarian или CSE под списком: поиск откроется по партномеру" +
                   (it.Mpn != "" && !it.Mpn.StartsWith(it.Lcsc) ? " " + it.Mpn : "") + ".";
        }
    }

    // LCSC catalogue: stock, price and the EasyEDA component linked to a part number
    class LcscInfo
    {
        public string Code = "", Mpn = "", Brand = "", Package = "", Stock = "", Price = "", MinQty = "", Datasheet = "", EasyEdaUuid = "";
        public bool HasCad;
    }

    static class Lcsc
    {
        static readonly string Api = Environment.GetEnvironmentVariable("EASYLIB_LCSC") ?? "https://wmsc.lcsc.com";
        static readonly Dictionary<string, KeyValuePair<DateTime, LcscInfo>> Memory = new Dictionary<string, KeyValuePair<DateTime, LcscInfo>>();

        // null: LCSC does not know this number
        public static LcscInfo Product(string code)
        {
            code = (code ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(code, "^C\\d+$")) return null;
            lock (Memory)
            {
                KeyValuePair<DateTime, LcscInfo> m;
                if (Memory.TryGetValue(code, out m) && DateTime.UtcNow - m.Key < TimeSpan.FromMinutes(10)) return m.Value;
            }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Api.TrimEnd('/') + "/ftps/wm/product/detail?productCode=" + code);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
            req.Accept = "application/json, text/plain, */*";
            req.Referer = "https://www.lcsc.com/";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 15000;
            string text;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = sr.ReadToEnd();
            object j = JsonUtil.Parse(text);
            Dictionary<string, object> r = Get.D(j, "result");
            LcscInfo li = null;
            if (r != null && Get.S(r, "productCode") != "")
            {
                li = new LcscInfo();
                li.Code = Get.S(r, "productCode");
                li.Mpn = Converter.Clean(Get.S(r, "productModel"));
                li.Brand = Converter.Clean(Get.S(r, "brandNameEn"));
                li.Package = Converter.Clean(Get.S(r, "encapStandard"));
                if (li.Package == "-") li.Package = "";
                li.Stock = Get.S(r, "stockNumber");
                li.MinQty = Get.S(r, "minBuyNumber");
                object[] prices = Get.A(r, "productPriceList");
                if (prices.Length > 0)
                {
                    li.Price = Get.S(prices[0], "usdPrice");
                    if (li.Price == "") li.Price = Get.S(prices[0], "productPrice");
                }
                li.Datasheet = Get.S(r, "pdfUrl");
                Dictionary<string, object> eda = Get.D(r, "edaSvgInfo");
                if (eda != null)
                {
                    Match m = Regex.Match(Get.S(eda, "schSvg"), "/([0-9a-fA-F]{32})\\.svg");
                    if (m.Success) li.EasyEdaUuid = m.Groups[1].Value.ToLowerInvariant();
                    li.HasCad = Get.S(eda, "schSvg") != "" || Get.S(eda, "pcbSvg") != "";
                }
            }
            lock (Memory)
            {
                if (Memory.Count > 500) Memory.Clear();
                Memory[code] = new KeyValuePair<DateTime, LcscInfo>(DateTime.UtcNow, li);
            }
            return li;
        }
    }

    static class EasyEda
    {
        static readonly string Api = Env("EASYLIB_API", "https://easyeda.com");
        static readonly string Modules = Env("EASYLIB_MODULES", "https://modules.easyeda.com");
        const string LcscOwnerUid = "0819f05c4eef4c71ace90d822a990e87";
        const string Ver = "6.4.19.5";
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

        static readonly CookieContainer Cookies = new CookieContainer();
        static readonly object Gate = new object();
        static DateTime lastCall = DateTime.MinValue;
        static DateTime blockedUntil = DateTime.MinValue;

        public static string CacheDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyLib", "cache");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string Env(string name, string def)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? def : v.TrimEnd('/');
        }

        static EasyEda()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
        }

        // ---------------- HTTP ----------------

        static HttpWebRequest NewRequest(string url, bool api)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Referer = "https://easyeda.com/editor";
            req.Accept = api ? "application/json, text/javascript, */*; q=0.01" : "*/*";
            req.Headers["Accept-Language"] = "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7";
            if (api) req.Headers["X-Requested-With"] = "XMLHttpRequest";
            req.CookieContainer = Cookies;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            return req;
        }

        static byte[] ReadAll(Stream s)
        {
            MemoryStream ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        // keep a small gap between API calls so that browsing does not look like scraping
        static void Pace(int minGapMs)
        {
            lock (Gate)
            {
                double wait = minGapMs - (DateTime.UtcNow - lastCall).TotalMilliseconds;
                if (wait > 0) System.Threading.Thread.Sleep((int)wait);
                lastCall = DateTime.UtcNow;
            }
        }

        static Exception Translate(WebException ex)
        {
            HttpWebResponse r = ex.Response as HttpWebResponse;
            if (r != null && ((int)r.StatusCode == 403 || (int)r.StatusCode == 429))
            {
                blockedUntil = DateTime.UtcNow.AddMinutes(2);
                return new BlockedException("EasyEDA временно ограничил запросы с вашего IP (ответ " + (int)r.StatusCode +
                    "). Обычно это проходит само через несколько минут. Уже скачанные детали работают из кэша.");
            }
            return ex;
        }

        static byte[] GetBytes(string url, bool api)
        {
            if (api) Pace(350);
            try
            {
                HttpWebRequest req = NewRequest(url, api);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                    return ReadAll(s);
            }
            catch (WebException ex) { throw Translate(ex); }
        }

        static string PostForm(string url, string body)
        {
            Pace(250);
            try
            {
                HttpWebRequest req = NewRequest(url, true);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                req.Headers["Origin"] = "https://easyeda.com";
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                    return Encoding.UTF8.GetString(ReadAll(s));
            }
            catch (WebException ex) { throw Translate(ex); }
        }

        static object ParseJson(string text)
        {
            string t = text.TrimStart();
            if (!t.StartsWith("{") && !t.StartsWith("["))
            {
                if (t.IndexOf("Request blocked", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("cloudfront", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    blockedUntil = DateTime.UtcNow.AddMinutes(2);
                    throw new BlockedException("EasyEDA временно ограничил запросы с вашего IP. Обычно это проходит само через несколько минут.");
                }
                throw new Exception("Сервер EasyEDA ответил что-то непонятное вместо данных. Возможно, там идут работы: попробуйте позже.");
            }
            try { return JsonUtil.Parse(text); }
            catch (Exception ex) { throw new Exception("Ответ сервера EasyEDA оборвался или повреждён. Попробуйте ещё раз.", ex); }
        }

        // ---------------- search ----------------

        // source: "all", "lcsc" or "user"
        public static SearchResult Search(string query, int page, string source)
        {
            query = (query ?? "").Trim();
            SearchResult r = new SearchResult();
            Match cm = Regex.Match(query, "^[Cc](\\d+)$");
            if (cm.Success && source != "user")
            {
                string c = "C" + cm.Groups[1].Value;
                SearchResult s = SearchList(c, 1, "lcsc");
                foreach (SearchItem it in s.Items)
                    if (string.Equals(it.Lcsc, c, StringComparison.OrdinalIgnoreCase)) r.Items.Add(it);
                if (r.Items.Count > 0) return r;
                // The search index of EasyEDA does not know every LCSC number. Offer to load the part
                // straight by its number (the product endpoint knows all parts that have CAD data),
                // plus community parts that carry this LCSC number.
                SearchItem d = new SearchItem();
                d.Source = "lcsc"; d.Lcsc = c; d.Direct = true;
                d.Title = c; d.Mpn = c + " — загрузить по номеру";
                d.Uuid = CachedUuid(c);
                // the LCSC catalogue knows the part and the EasyEDA component it is linked to
                LcscInfo li = null;
                try { li = Lcsc.Product(c); } catch { }
                if (li != null)
                {
                    d.Mpn = li.Mpn != "" ? li.Mpn : d.Mpn;
                    d.Title = d.Mpn;
                    d.Manufacturer = li.Brand;
                    d.Package = li.Package;
                    d.Stock = li.Stock; d.Price = li.Price; d.MinQty = li.MinQty;
                    if (d.Uuid == "") d.Uuid = li.EasyEdaUuid;
                    d.NoCad = !li.HasCad && d.Uuid == "";
                    d.Class = d.NoCad ? "нет CAD" : "";
                }
                r.Items.Add(d);
                try
                {
                    foreach (SearchItem it in SearchList(c, 1, "user").Items)
                        if (string.Equals(it.Lcsc, c, StringComparison.OrdinalIgnoreCase)) r.Items.Add(it);
                }
                catch { }
                return r;
            }
            if (source == "lcsc" || source == "user") return SearchList(query, page, source);

            // both libraries: official parts first
            SearchResult a = null, b = null;
            Exception err = null;
            try { a = SearchList(query, page, "lcsc"); } catch (Exception ex) { err = ex; }
            try { b = SearchList(query, page, "user"); } catch (Exception ex) { if (err == null) err = ex; }
            if (a == null && b == null) throw err;
            if (a != null) r.Items.AddRange(a.Items);
            if (b != null) r.Items.AddRange(b.Items);
            r.TotalPages = Math.Max(a != null ? a.TotalPages : 1, b != null ? b.TotalPages : 1);
            return r;
        }

        static SearchResult SearchList(string query, int page, string source)
        {
            string uid = source == "user" ? "user" : LcscOwnerUid;
            string body = "type=3&doctype%5B%5D=2&uid=" + uid +
                          "&returnListStyle=classifyarr&wd=" + Uri.EscapeDataString(query) +
                          "&version=" + Ver + "&page=" + page.ToString(CultureInfo.InvariantCulture) + "&pageSize=50";
            object j = ParseJson(PostForm(Api + "/api/components/search", body));
            Dictionary<string, object> result = Get.D(j, "result");
            SearchResult r = new SearchResult();
            if (result == null) return r;
            int tp;
            r.TotalPages = int.TryParse(Get.S(result, "totalPage"), out tp) ? Math.Max(1, tp) : 1;
            string listName = source == "user" ? "user" : "lcsc";
            foreach (object it in Get.A(Get.D(result, "lists"), listName))
            {
                SearchItem s = Item(it, source == "user" ? "user" : "lcsc");
                if (s != null) r.Items.Add(s);
            }
            return r;
        }

        static SearchItem Item(object it, string source)
        {
            Dictionary<string, object> cp = Get.D(Get.D(Get.D(it, "dataStr"), "head"), "c_para") ?? new Dictionary<string, object>();
            SearchItem s = new SearchItem();
            s.Source = source;
            s.Uuid = Get.S(it, "uuid");
            Dictionary<string, object> lb = Get.D(it, "lcsc");
            s.Lcsc = Get.S(lb, "number");
            if (lb != null)
            {
                s.Stock = Get.S(lb, "stock"); s.Price = Get.S(lb, "price"); s.MinQty = Get.S(lb, "min");
            }
            if (s.Lcsc == "") s.Lcsc = Converter.SupplierPart(cp);
            if (s.Uuid == "" && s.Lcsc == "") return null;
            s.Title = Converter.Clean(Get.S(it, "title"));
            s.Mpn = Converter.Clean(Converter.FirstOf(cp, "Manufacturer Part", "Manufacturer Part Number", "BOM_Manufacturer Part", "MPN"));
            if (s.Mpn == "" || source == "user") s.Mpn = s.Title;
            s.Manufacturer = Converter.Clean(Converter.FirstOf(cp, "Manufacturer", "BOM_Manufacturer"));
            s.Package = Converter.Clean(Get.S(cp, "package"));
            s.Value = Converter.Clean(Get.S(cp, "Value"));
            Dictionary<string, object> owner = Get.D(it, "owner");
            s.Author = Converter.Clean(owner != null ? Get.S(owner, "username") : "");
            if (s.Author == "") s.Author = Converter.Clean(Get.S(cp, "Contributor"));
            if (source == "user") s.Class = "Сообщество";
            else
            {
                string cls = Converter.Clean(Get.S(cp, "JLCPCB Part Class"));
                s.Class = cls.StartsWith("Basic") ? "Basic" : cls.StartsWith("Extended") ? "Extended" : cls;
            }
            return s;
        }

        // ---------------- component ----------------

        static readonly Dictionary<string, Dictionary<string, object>> Memory = new Dictionary<string, Dictionary<string, object>>();

        public static Dictionary<string, object> Component(SearchItem it)
        {
            if (it.NoCad) throw new NoCadException(it);
            if (it.Uuid != "") return ComponentByUuid(it.Uuid, it.Lcsc);
            return Component(it.Lcsc, it.Direct);
        }

        public static Dictionary<string, object> Component(string lcsc) { return Component(lcsc, false); }

        // by LCSC number: find the component uuid through the search API, then load it
        static Dictionary<string, object> Component(string lcsc, bool searched)
        {
            lcsc = (lcsc ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(lcsc, "^C\\d+$")) throw new Exception("неверный номер LCSC: " + lcsc);
            Dictionary<string, object> cached = ReadCache(lcsc);
            if (cached != null) return cached;
            string uuidFile = Path.Combine(CacheDir, lcsc + ".uuid");
            string uuid = File.Exists(uuidFile) ? File.ReadAllText(uuidFile).Trim() : "";
            if (uuid == "" && !searched)
            {
                foreach (SearchItem s in SearchList(lcsc, 1, "lcsc").Items)
                    if (string.Equals(s.Lcsc, lcsc, StringComparison.OrdinalIgnoreCase)) { uuid = s.Uuid; break; }
            }
            if (uuid != "") return ComponentByUuid(uuid, lcsc);
            // last resort: the product endpoint
            string raw = Encoding.UTF8.GetString(GetBytes(Api + "/api/products/" + lcsc + "/components?version=" + Ver, true));
            return Store(lcsc, raw);
        }

        static Dictionary<string, object> ComponentByUuid(string uuid, string lcsc)
        {
            if (!Regex.IsMatch(uuid, "^[0-9a-fA-F]{16,40}$")) throw new Exception("неверный id детали");
            Dictionary<string, object> m;
            lock (Memory) if (Memory.TryGetValue(uuid, out m)) return m;
            Dictionary<string, object> cached = ReadCache(uuid);
            if (cached == null && lcsc != "") cached = ReadCache(lcsc);    // cache of EasyLib 2.0
            if (cached != null) { Remember(uuid, cached); return cached; }
            if (DateTime.UtcNow < blockedUntil)
                throw new BlockedException("EasyEDA временно ограничил запросы с вашего IP. Подождите пару минут: уже скачанные детали работают из кэша.");
            string raw = Encoding.UTF8.GetString(GetBytes(Api + "/api/components/" + uuid + "?version=" + Ver + "&uuid=" + uuid, true));
            Dictionary<string, object> res = Store(uuid, raw);
            if (res != null && lcsc != "") { try { File.WriteAllText(Path.Combine(CacheDir, lcsc + ".uuid"), uuid); } catch { } }
            if (res != null) Remember(uuid, res);
            return res;
        }

        static void Remember(string key, Dictionary<string, object> res)
        {
            lock (Memory)
            {
                if (Memory.Count > 300) Memory.Clear();
                Memory[key] = res;
            }
        }

        static string CachedUuid(string lcsc)
        {
            try
            {
                string f = Path.Combine(CacheDir, lcsc + ".uuid");
                if (File.Exists(f))
                {
                    string u = File.ReadAllText(f).Trim();
                    if (Regex.IsMatch(u, "^[0-9a-fA-F]{16,40}$")) return u;
                }
            }
            catch { }
            return "";
        }

        // raw server answer of a part, for bug reports
        public static string CacheFile(SearchItem it)
        {
            foreach (string k in new string[] { it.Uuid, it.Lcsc })
            {
                if (string.IsNullOrEmpty(k)) continue;
                string f = Path.Combine(CacheDir, k + ".json");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        static Dictionary<string, object> ReadCache(string key)
        {
            string file = Path.Combine(CacheDir, key + ".json");
            if (!File.Exists(file)) return null;
            try
            {
                object j = JsonUtil.Parse(File.ReadAllText(file, Encoding.UTF8));
                return Get.D(j, "result");
            }
            catch { return null; }
        }

        static Dictionary<string, object> Store(string key, string raw)
        {
            object j = ParseJson(raw);
            if (Get.S(j, "success") == "false" || Get.D(j, "result") == null) return null;
            try { File.WriteAllText(Path.Combine(CacheDir, key + ".json"), raw, new UTF8Encoding(false)); } catch { }
            return Get.D(j, "result");
        }

        // ---------------- 3D ----------------

        public static byte[] Step(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            string file = Path.Combine(CacheDir, uuid + ".step");
            if (File.Exists(file) && new FileInfo(file).Length > 100) return File.ReadAllBytes(file);
            byte[] data = GetBytes(Modules + "/qAxj6KHrDKw4blvCG8QJPs7Y/" + uuid, false);
            if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
            {
                using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress))
                    data = ReadAll(gz);
            }
            string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64));
            if (!head.Contains("ISO-10303-21")) return null;
            File.WriteAllBytes(file, data);
            return data;
        }

        public static string Obj(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            string file = Path.Combine(CacheDir, uuid + ".obj");
            if (File.Exists(file) && new FileInfo(file).Length > 50) return File.ReadAllText(file, Encoding.UTF8);
            string text = Encoding.UTF8.GetString(GetBytes(Modules + "/3dmodel/" + uuid, false));
            if (!text.Contains("\nv ") && !text.StartsWith("v ")) return null;
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return text;
        }
    }

    // Orders search results by how well they match the query; the EasyEDA search is fuzzy and
    // often returns parts that only share a word with the query.
    static class SearchRank
    {
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s.ToLowerInvariant(), "[^\\p{L}\\p{N}]", "");
        }

        public static int Score(SearchItem it, string query)
        {
            string q = Norm(query);
            if (q == "") return 0;
            string mpn = Norm(it.Mpn), title = Norm(it.Title), lcsc = Norm(it.Lcsc);
            if (it.Direct) return 100;
            if (q == mpn || q == title || q == lcsc) return 100;
            if ((mpn != "" && mpn.StartsWith(q)) || (title != "" && title.StartsWith(q))) return 80;
            if (mpn.Contains(q) || title.Contains(q)) return 60;
            string hay = mpn + "|" + title + "|" + lcsc + "|" + Norm(it.Value) + "|" + Norm(it.Package) + "|" + Norm(it.Manufacturer);
            string[] tokens = Regex.Split(query.Trim(), "[\\s,;]+");
            int n = 0, hit = 0;
            foreach (string t in tokens)
            {
                string nt = Norm(t);
                if (nt == "") continue;
                n++;
                if (hay.Contains(nt)) hit++;
            }
            if (n > 0 && hit == n) return 40;
            return n > 0 ? hit * 30 / n : 0;
        }

        // stable sort by score; exact: keep only the parts that contain the whole query
        public static List<SearchItem> Apply(List<SearchItem> items, string query, bool exact)
        {
            List<SearchItem> keep = new List<SearchItem>();
            HashSet<string> seen = new HashSet<string>();
            foreach (SearchItem it in items)
            {
                if (!seen.Add(it.Source + ":" + it.Key)) continue;
                it.Score = Score(it, query);
                if (exact && it.Score < 40) continue;
                keep.Add(it);
            }
            List<int> idx = new List<int>();
            for (int i = 0; i < keep.Count; i++) idx.Add(i);
            idx.Sort(delegate(int a, int b)
            {
                int c = keep[b].Score.CompareTo(keep[a].Score);
                return c != 0 ? c : a.CompareTo(b);
            });
            List<SearchItem> r = new List<SearchItem>();
            foreach (int i in idx) r.Add(keep[i]);
            return r;
        }
    }
}
