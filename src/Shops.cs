// Stock and price of a part in component shops: LCSC, ЧИП и ДИП, Промэлектроника.
// The Russian shops have no public API: their ordinary search page is read, the way a browser
// does it, once per shown part, and the best match by part number is taken.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLib
{
    class ShopOffer
    {
        public string Title = "", Maker = "", Url = "", Price = "", Term = "";
        public long Stock = -1, OnOrder = -1;     // -1: the shop does not say
    }

    class ShopResult
    {
        public int State;              // 0 loading, 1 found, 2 not sold there, 3 error
        public ShopOffer Offer;
        public int Similar;            // other parts the search returned
        public string Error = "", SearchUrl = "";
        public DateTime At = DateTime.Now;

        static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public string Text
        {
            get
            {
                if (State == 0) return "ищу…";
                if (State == 3) return Error;
                if (State == 2) return Similar > 0 ? "такого нет (похожих: " + Similar + ")" : "нет в каталоге";
                List<string> s = new List<string>();
                if (Offer.Stock > 0) s.Add(Offer.Stock.ToString("#,0", Ru) + " шт.");
                else if (Offer.Stock == 0) s.Add(Offer.OnOrder > 0 ? "под заказ " + Offer.OnOrder.ToString("#,0", Ru) + " шт." : "нет в наличии");
                if (Offer.Stock > 0 && Offer.OnOrder > 0) s.Add("ещё " + Offer.OnOrder.ToString("#,0", Ru) + " под заказ");
                if (Offer.Price != "") s.Add(Offer.Price);
                if (Offer.Term != "") s.Add(Offer.Term);
                if (s.Count == 0) s.Add("есть в каталоге");
                return string.Join("  ·  ", s.ToArray());
            }
        }

        public bool InStock { get { return State == 1 && Offer.Stock > 0; } }
    }

    static class Shops
    {
        public static readonly string[] Ids = { "lcsc", "chipdip", "promelec" };

        public static string Name(string id)
        {
            return id == "lcsc" ? "LCSC" : id == "chipdip" ? "ЧИП и ДИП" : id == "promelec" ? "Промэлектроника" : id;
        }

        public static string SearchUrl(string id, string query)
        {
            string q = Uri.EscapeDataString(query ?? "");
            return id == "chipdip" ? ChipDipBase.TrimEnd('/') + "/search?searchtext=" + q
                 : id == "promelec" ? PromelecBase.TrimEnd('/') + "/search/?query=" + q
                 : "https://www.lcsc.com/search?q=" + q;
        }

        // shops the user wants to see (settings)
        public static List<string> Enabled = new List<string>(Ids);

        public static List<string> ParseEnabled(string s)
        {
            List<string> r = new List<string>();
            foreach (string part in (s ?? "").Split(',')) { string id = part.Trim().ToLowerInvariant(); if (Array.IndexOf(Ids, id) >= 0 && !r.Contains(id)) r.Add(id); }
            return r;
        }

        static readonly string ChipDipBase = Environment.GetEnvironmentVariable("EASYLIB_CHIPDIP") ?? "https://www.chipdip.ru";
        static readonly string PromelecBase = Environment.GetEnvironmentVariable("EASYLIB_PROMELEC") ?? "https://www.promelec.ru";

        static readonly Dictionary<string, ShopResult> Memory = new Dictionary<string, ShopResult>();

        // lcsc: the LCSC number; other shops: the manufacturer part number
        public static ShopResult Lookup(string id, string mpn, string lcsc)
        {
            string key = id + "|" + (id == "lcsc" ? lcsc : mpn);
            lock (Memory)
            {
                ShopResult m;
                if (Memory.TryGetValue(key, out m) && DateTime.Now - m.At < TimeSpan.FromMinutes(15)) return m;
            }
            ShopResult r = new ShopResult();
            r.SearchUrl = SearchUrl(id, id == "lcsc" && lcsc != "" ? lcsc : mpn);
            try
            {
                if (id == "lcsc") LcscLookup(r, lcsc);
                else
                {
                    string html = Get(r.SearchUrl, id);
                    List<ShopOffer> offers = id == "chipdip" ? ParseChipDip(html, ChipDipBase) : ParsePromelec(html);
                    Pick(r, offers, mpn);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                int code = hr != null ? (int)hr.StatusCode : 0;
                if (code == 404) r.State = 2;      // the shop has no such page -> simply not in the catalogue
                else
                {
                    r.State = 3;
                    r.Error = code == 403 ? (id == "chipdip" ? "сайт не пускает (403): возможно, включён VPN" : "сайт не пускает (403)")
                            : code == 429 ? "слишком много запросов, позже"
                            : code > 0 ? "ошибка сайта " + code
                            : ex.Status == WebExceptionStatus.Timeout ? "сайт не отвечает" : "нет связи";
                }
            }
            catch (Exception)
            {
                r.State = 3;
                r.Error = "не удалось разобрать ответ";
            }
            r.At = DateTime.Now;
            // errors are retried sooner
            if (r.State != 3)
                lock (Memory)
                {
                    if (Memory.Count > 400) Memory.Clear();
                    Memory[key] = r;
                }
            return r;
        }

        static void LcscLookup(ShopResult r, string code)
        {
            if (string.IsNullOrEmpty(code)) { r.State = 2; return; }
            LcscInfo li = Lcsc.Product(code);
            if (li == null) { r.State = 2; return; }
            r.State = 1;
            ShopOffer o = new ShopOffer();
            o.Title = li.Mpn; o.Maker = li.Brand;
            o.Url = "https://www.lcsc.com/product-detail/" + code + ".html";
            long st;
            if (long.TryParse(li.Stock, out st)) o.Stock = st;
            if (li.Price != "") o.Price = "$" + li.Price + (li.MinQty != "" && li.MinQty != "1" ? " (от " + li.MinQty + " шт.)" : "");
            r.Offer = o;
        }

        // the offer whose part number is the query (ignoring punctuation and case); the one in stock wins
        public static void Pick(ShopResult r, List<ShopOffer> offers, string mpn)
        {
            string q = SearchRank.Norm(mpn);
            ShopOffer best = null;
            int bestScore = -1;
            foreach (ShopOffer o in offers)
            {
                string t = SearchRank.Norm(o.Title);
                if (t == "" || q == "") continue;
                // "AMS1117-3.3" is the part; "AMS1117-3.3 SOT-223" or a longer order code still is
                int score = t == q ? 3 : t.StartsWith(q) && t.Length - q.Length <= 6 ? 2 : q.StartsWith(t) && q.Length - t.Length <= 4 && t.Length >= 5 ? 1 : -1;
                if (score < 0) continue;
                score = score * 4 + (o.Stock > 0 ? 2 : 0) + (o.Price != "" ? 1 : 0);
                if (score > bestScore || (score == bestScore && best != null && o.Stock > best.Stock)) { best = o; bestScore = score; }
            }
            r.Offer = best;
            r.State = best != null ? 1 : 2;
            r.Similar = offers.Count - (best != null ? 1 : 0);
        }

        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

        // one cookie jar per shop for the whole session, and the homepage visited once to collect
        // the session cookies a WAF may demand before it answers a search (the common cause of 403)
        static readonly Dictionary<string, CookieContainer> Jars = new Dictionary<string, CookieContainer>();
        static readonly HashSet<string> Warmed = new HashSet<string>();

        static CookieContainer Jar(string id)
        {
            lock (Jars)
            {
                CookieContainer c;
                if (!Jars.TryGetValue(id, out c)) Jars[id] = c = new CookieContainer();
                return c;
            }
        }

        static void WarmUp(string id)
        {
            lock (Warmed) { if (!Warmed.Add(id)) return; }
            string home = id == "chipdip" ? ChipDipBase : id == "promelec" ? PromelecBase : null;
            if (home == null) return;
            try { Fetch(home, id, null); } catch { }     // only to pick up cookies
        }

        static string Get(string url, string id)
        {
            WarmUp(id);
            string text = Fetch(url, id, SearchUrl(id, ""));
            // kept for bug reports: when a shop changes its pages (or blocks us), this file shows what it sent
            try
            {
                string dir = Path.Combine(EasyEda.CacheDir, "shops");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, id + "-last.html"), "<!-- " + url + " -->\n" + text, Encoding.UTF8);
            }
            catch { }
            return text;
        }

        static string Fetch(string url, string id, string referer)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
            req.Headers["Accept-Language"] = "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7";
            req.Headers["Upgrade-Insecure-Requests"] = "1";
            req.Headers["sec-ch-ua"] = "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"";
            req.Headers["sec-ch-ua-mobile"] = "?0";
            req.Headers["sec-ch-ua-platform"] = "\"Windows\"";
            req.Headers["Sec-Fetch-Site"] = referer == null ? "none" : "same-origin";
            req.Headers["Sec-Fetch-Mode"] = "navigate";
            req.Headers["Sec-Fetch-User"] = "?1";
            req.Headers["Sec-Fetch-Dest"] = "document";
            if (referer != null) req.Referer = referer;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.CookieContainer = Jar(id);
            string text;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                st.CopyTo(ms);
                byte[] data = ms.ToArray();
                Encoding enc = Encoding.UTF8;
                try
                {
                    string cs = resp.CharacterSet;
                    Match m = Regex.Match(Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 2000)), "charset=[\"']?([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
                    if (m.Success) cs = m.Groups[1].Value;
                    if (!string.IsNullOrEmpty(cs)) enc = Encoding.GetEncoding(cs);
                }
                catch { }
                text = enc.GetString(data);
            }
            return text;
        }

        static string Plain(string html)
        {
            if (html == null) return "";
            string s = Regex.Replace(html, "<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s).Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');
            return Regex.Replace(s, "\\s+", " ").Trim();
        }

        static long Number(string s)
        {
            string d = Regex.Replace(s ?? "", "[^0-9]", "");
            long v;
            return d != "" && d.Length < 12 && long.TryParse(d, out v) ? v : -1;
        }

        // ---------------------------------------------------------------- Промэлектроника

        public static List<ShopOffer> ParsePromelec(string html)
        {
            List<ShopOffer> list = new List<ShopOffer>();
            string[] parts = Regex.Split(html, "class=\"table-list__item\\b");
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                Match t = Regex.Match(p, "class=\"product-preview__title[^\"]*\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
                if (!t.Success) continue;
                ShopOffer o = new ShopOffer();
                o.Title = Plain(t.Groups[1].Value);
                Match u = Regex.Match(p, "href=\"([^\"]*/product/[^\"]*)\"");
                if (u.Success) o.Url = u.Groups[1].Value.StartsWith("http") ? u.Groups[1].Value : PromelecBase.TrimEnd('/') + u.Groups[1].Value;
                Match mk = Regex.Match(p, "Производитель:\\s*</div>\\s*<div[^>]*>(.*?)</div>", RegexOptions.Singleline);
                if (mk.Success) o.Maker = Plain(mk.Groups[1].Value);
                Match st = Regex.Match(p, "Наличие:\\s*</div>\\s*<div[^>]*>(.*?)</div>", RegexOptions.Singleline);
                if (st.Success) o.Stock = Number(Plain(st.Groups[1].Value));
                Match od = Regex.Match(p, "Под заказ:\\s*</div>\\s*<div[^>]*>(.*?)</div>", RegexOptions.Singleline);
                if (od.Success) o.OnOrder = Number(Plain(od.Groups[1].Value));
                if (!st.Success && !od.Success) o.Stock = 0;       // "Сообщить о поступлении"
                Match pr = Regex.Match(p, "class=\"table-list__price[^\"]*\"[^>]*>(.*?)</span>\\s*</span>", RegexOptions.Singleline);
                if (!pr.Success) pr = Regex.Match(p, "class=\"table-list__price[^\"]*\"[^>]*>(.*?)</span>", RegexOptions.Singleline);
                if (pr.Success) o.Price = Price(Plain(pr.Groups[1].Value));
                list.Add(o);
            }
            return list;
        }

        // "от 9,59 ₽" / "14 руб." -> "от 9,59 ₽"
        static string Price(string s)
        {
            Match m = Regex.Match(s ?? "", "(от\\s*)?(\\d[\\d ]*(?:[.,]\\d+)?)\\s*(₽|руб|р\\.)?", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            string num = m.Groups[2].Value.Trim();
            if (Regex.Replace(num, "[^1-9]", "") == "") return "";
            return (m.Groups[1].Success ? "от " : "") + num + " ₽";
        }

        // ---------------------------------------------------------------- ЧИП и ДИП

        // The markup is read tolerantly: result rows (or a single product page), the first product
        // link in a row is the name, "N шт." the stock, a number before "руб" the price.
        public static List<ShopOffer> ParseChipDip(string html, string baseUrl)
        {
            List<ShopOffer> list = new List<ShopOffer>();
            string[] rows = Regex.Split(html, "<tr[^>]*class=\"[^\"]*with-hover[^\"]*\"", RegexOptions.IgnoreCase);
            if (rows.Length < 2) rows = Regex.Split(html, "<div[^>]*class=\"[^\"]*\\bitem\\b[^\"]*\"[^>]*id=\"item", RegexOptions.IgnoreCase);
            for (int i = 1; i < rows.Length; i++)
            {
                ShopOffer o = ChipDipRow(rows[i], baseUrl);
                if (o != null) list.Add(o);
            }
            if (list.Count == 0)
            {
                // an exact match can open the product page itself
                Match h1 = Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                Match canon = Regex.Match(html, "<link[^>]*rel=\"canonical\"[^>]*href=\"([^\"]*/product[^\"]*)\"", RegexOptions.IgnoreCase);
                if (h1.Success && canon.Success)
                {
                    ShopOffer o = new ShopOffer();
                    o.Title = FirstToken(Plain(h1.Groups[1].Value));
                    o.Url = canon.Groups[1].Value;
                    Fill(o, html);
                    list.Add(o);
                }
            }
            return list;
        }

        static ShopOffer ChipDipRow(string row, string baseUrl)
        {
            Match a = Regex.Match(row, "<a[^>]*href=\"(/product[^\"]*)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            // skip links that only wrap a picture
            while (a.Success && Plain(a.Groups[2].Value) == "") a = a.NextMatch();
            if (!a.Success) return null;
            ShopOffer o = new ShopOffer();
            Match b = Regex.Match(a.Groups[2].Value, "<b[^>]*>(.*?)</b>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            o.Title = b.Success ? Plain(b.Groups[1].Value) : FirstToken(Plain(a.Groups[2].Value));
            o.Url = baseUrl.TrimEnd('/') + a.Groups[1].Value;
            Fill(o, row);
            return o;
        }

        // "AMS1117-3.3, Стабилизатор напряжения…" -> "AMS1117-3.3"
        static string FirstToken(string s)
        {
            int i = s.IndexOf(", ", StringComparison.Ordinal);
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        static void Fill(ShopOffer o, string html)
        {
            string text = Plain(html);
            Match st = Regex.Match(text, "(\\d[\\d ]{0,9})\\s*шт\\b");
            // the availability element, when the class is still called so
            Match av = Regex.Match(html, "class=\"[^\"]*item__avail[^\"]*\"[^>]*>(.*?)</", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            string avText = av.Success ? Plain(av.Groups[1].Value) : "";
            if (avText != "")
            {
                Match n = Regex.Match(avText, "(\\d[\\d ]{0,9})\\s*шт");
                if (n.Success) o.Stock = Number(n.Groups[1].Value);
                else { o.Stock = 0; o.Term = avText.Length <= 40 ? avText.ToLowerInvariant() : ""; }
            }
            else if (st.Success) o.Stock = Number(st.Groups[1].Value);
            Match pr = Regex.Match(html, "itemprop=\"price\"[^>]*content=\"([0-9.,]+)\"", RegexOptions.IgnoreCase);
            if (pr.Success) o.Price = Price(pr.Groups[1].Value.Replace('.', ',') + " руб");
            else
            {
                Match p2 = Regex.Match(text, "(от\\s*)?(\\d[\\d ]{0,8}(?:[.,]\\d{1,2})?)\\s*(?:руб|₽)", RegexOptions.IgnoreCase);
                if (p2.Success) o.Price = Price(p2.Value);
            }
            if (o.Term == "")
            {
                Match term = Regex.Match(text, "(завтра|сегодня|послезавтра|\\d+\\s*[-–]\\s*\\d+\\s*(?:дн\\w*|недел\\w*)|\\d+\\s*(?:дн\\w*|недел\\w*)|(?:с|до|к)\\s+\\d{1,2}\\s+(?:января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря))", RegexOptions.IgnoreCase);
                if (term.Success) o.Term = term.Value.ToLowerInvariant();
            }
        }
    }
}
