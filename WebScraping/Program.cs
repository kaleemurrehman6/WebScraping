using HtmlAgilityPack;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

// ================== Config ==================
var apiKey = "";

// (Query, Region label, Serper country code, Mode)
// Mode "places" = Google Maps businesses (seedha company ki official website milti hai)
// Mode "search" = normal Google search (Remote ke liye, kam accurate)
var searches = new (string Query, string Region, string Gl, string Mode)[]
{
    // Karachi
    ("software house in Karachi", "Karachi", "pk", "places"),
    ("IT company in Karachi", "Karachi", "pk", "places"),
    ("web development company in Karachi", "Karachi", "pk", "places"),

    // Gulf
    ("software company in Dubai", "Gulf-UAE", "ae", "places"),
    ("software company in Abu Dhabi", "Gulf-UAE", "ae", "places"),
    ("software company in Riyadh", "Gulf-KSA", "sa", "places"),
    ("software company in Doha", "Gulf-Qatar", "qa", "places"),

    // Remote (Maps mein nahi hoti, isliye search)
    ("remote-first software company .NET developers", "Remote", "us", "search"),
    ("fully remote software company C# backend", "Remote", "us", "search"),
};

const int PlacesPages = 2;          // har places query ke 2 pages (~40 businesses)

const int ResultsPerSearch = 10;
const int MaxPagesPerCompany = 4;   // homepage + 3 relevant pages
const int DelayMs = 1500;           // server par load na dalo

// Aggregator/directory sites jo company ki apni site nahi hoti
var blockedRoots = new HashSet<string>
{
    "linkedin.com","indeed.com","glassdoor.com","facebook.com","twitter.com","x.com","instagram.com",
    "youtube.com","clutch.co","goodfirms.co","rozee.pk","mustakbil.com","wikipedia.org","reddit.com",
    "quora.com","medium.com","bayt.com","naukrigulf.com","gulftalent.com","crunchbase.com","zoominfo.com",
    "ambitionbox.com","designrush.com","sortlist.com","themanifest.com","techbehemoths.com","topdevelopers.co",
    "yellowpages.com","google.com","pinterest.com","github.com","tiktok.com","apple.com","amazon.com",
    "naukri.com","monster.com","ziprecruiter.com","remote.co","weworkremotely.com","remotive.com","builtin.com"
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MyJobFinder/1.0)");

var robotsCache = new Dictionary<string, string?>();

// ================== 1. Companies dhoondo ==================
var companies = new Dictionary<string, Company>(); // key = root domain

foreach (var (query, region, gl, mode) in searches)
{
    try
    {
        Console.WriteLine($"Searching [{region}/{mode}]: {query}");
        var hits = mode == "places"
            ? await SearchPlacesAsync(http, apiKey, query, gl, PlacesPages)
            : await SearchSerperAsync(http, apiKey, query, ResultsPerSearch, gl);

        foreach (var hit in hits)
        {
            // Normal search mein blogs, "Top 10" lists aur job pages hatao
            if (mode == "search" && IsListicle(hit.Title + " " + hit.Link)) continue;

            if (!Uri.TryCreate(hit.Link, UriKind.Absolute, out var uri)) continue;
            var root = RootDomain(uri.Host);
            if (blockedRoots.Contains(root) || companies.ContainsKey(root)) continue;

            companies[root] = new Company(
                Region: region,
                Name: CleanName(hit.Title, root),
                BaseUrl: $"{uri.Scheme}://{uri.Host}",
                Root: root);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Search failed: {ex.Message}");
    }
    await Task.Delay(800);
}

Console.WriteLine($"\n{companies.Count} unique companies mili. Ab un ki sites check ho rahi hain...\n");

// ================== 2. Har company ki site se emails nikalo ==================
var rows = new List<Row>();
var seen = new HashSet<string>(); // company+email duplicate se bachne ke liye
var count = 0;

foreach (var c in companies.Values)
{
    count++;
    Console.WriteLine($"[{count}/{companies.Count}] {c.Name} ({c.BaseUrl})");

    var emails = new Dictionary<string, string>(); // email -> page jahan mila
    string careersPage = "";

    try
    {
        // Homepage
        var home = await TryFetchAsync(http, c.BaseUrl + "/", robotsCache);
        if (home != null)
        {
            CollectEmails(home.Value.Html, home.Value.Doc, c.Root, c.BaseUrl + "/", emails);

            // Relevant pages (careers/contact/about) dhoondo
            var candidates = FindCandidatePages(home.Value.Doc, new Uri(c.BaseUrl + "/"), c.Root);

            // Agar links na milein to common paths try karo
            if (candidates.Count == 0)
                candidates = new List<string> { c.BaseUrl + "/careers", c.BaseUrl + "/contact-us", c.BaseUrl + "/contact" };

            careersPage = candidates.FirstOrDefault(IsCareerUrl) ?? "";

            foreach (var page in candidates.Take(MaxPagesPerCompany - 1))
            {
                await Task.Delay(DelayMs);
                var res = await TryFetchAsync(http, page, robotsCache);
                if (res != null)
                    CollectEmails(res.Value.Html, res.Value.Doc, c.Root, page, emails);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   Error: {ex.Message}");
    }

    if (emails.Count == 0)
    {
        // Email nahi mili, lekin careers page ka link save kar lo (manual apply ke liye)
        rows.Add(new Row(c.Region, c.Name, c.BaseUrl, careersPage, "", "", ""));
        Console.WriteLine("   Email nahi mili.");
    }
    else
    {
        foreach (var (email, foundOn) in emails.OrderBy(e => TypeRank(e.Key)))
        {
            if (!seen.Add(c.Root + "|" + email)) continue;
            rows.Add(new Row(c.Region, c.Name, c.BaseUrl, careersPage, email, EmailType(email), foundOn));
            Console.WriteLine($"   {email}  [{EmailType(email)}]");
        }
    }

    await Task.Delay(DelayMs);
}

// ================== 3. CSV save ==================
// HR/Careers wali emails upar aayen
var sorted = rows
    .OrderBy(r => r.Region)
    .ThenBy(r => r.Email == "" ? 2 : (r.EmailType == "HR/Careers" ? 0 : 1))
    .ThenBy(r => r.Company)
    .ToList();

var path = Directory.GetCurrentDirectory();

path = Path.GetFullPath(
    Path.Combine(path, @"..\..\..\..")
);

SaveCsv(sorted, Path.Combine(path, "output.csv"));

var withEmail = sorted.Count(r => r.Email != "");
Console.WriteLine($"\nDone. {companies.Count} companies, {withEmail} emails mili.");
Console.WriteLine($"File: {path}");


// =====================================================
//                      Functions
// =====================================================

static async Task<List<SearchHit>> SearchSerperAsync(HttpClient http, string apiKey, string query, int max, string gl)
{
    var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { q = query, num = max, gl }),
            Encoding.UTF8, "application/json")
    };
    req.Headers.Add("X-API-KEY", apiKey);

    var res = await http.SendAsync(req);
    res.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    var list = new List<SearchHit>();

    if (doc.RootElement.TryGetProperty("organic", out var organic))
    {
        foreach (var item in organic.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var link = item.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "";
            if (link != "") list.Add(new SearchHit(title, link));
        }
    }
    return list;
}

// Google Maps businesses: naam + official website
static async Task<List<SearchHit>> SearchPlacesAsync(HttpClient http, string apiKey, string query, string gl, int pages)
{
    var list = new List<SearchHit>();

    for (var page = 1; page <= pages; page++)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/places")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { q = query, gl, page }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-API-KEY", apiKey);

        var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("places", out var places)) break;

        var n = 0;
        foreach (var p in places.EnumerateArray())
        {
            n++;
            var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var website = p.TryGetProperty("website", out var w) ? w.GetString() ?? "" : "";
            if (website != "") list.Add(new SearchHit(title, website));
        }
        if (n == 0) break;

        await Task.Delay(500);
    }
    return list;
}

// Blog/list/job-board type results pehchano (sirf "search" mode mein use hota hai)
static bool IsListicle(string text)
{
    var t = text.ToLowerInvariant();
    var words = new[] { "top ", "best ", "list of", "jobs", "vacanc", "hiring", "salary", "salaries",
                        "directory", "ranking", "companies in", "review", "how to" };
    return words.Any(t.Contains);
}

// Page fetch + robots.txt check
static async Task<(string Html, HtmlDocument Doc)?> TryFetchAsync(
    HttpClient http, string url, Dictionary<string, string?> robotsCache)
{
    try
    {
        if (!await IsAllowedAsync(http, url, robotsCache)) return null;

        var html = await http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return (html, doc);
    }
    catch
    {
        return null; // 404, timeout, block wagairah, agli page par chalo
    }
}

static async Task<bool> IsAllowedAsync(HttpClient http, string url, Dictionary<string, string?> cache)
{
    var uri = new Uri(url);
    var key = uri.Scheme + "://" + uri.Host;

    if (!cache.TryGetValue(key, out var robots))
    {
        try { robots = await http.GetStringAsync(key + "/robots.txt"); }
        catch { robots = null; }
        cache[key] = robots;
    }
    if (robots == null) return true;

    var applies = false;
    foreach (var raw in robots.Split('\n'))
    {
        var line = raw.Split('#')[0].Trim();
        if (line.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            applies = line[11..].Trim() == "*";
        else if (applies && line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
        {
            var p = line[9..].Trim();
            if (p.Length > 0 && uri.AbsolutePath.StartsWith(p)) return false;
        }
    }
    return true;
}

// Homepage ke links mein se careers/contact/about wali pages chuno
static List<string> FindCandidatePages(HtmlDocument doc, Uri baseUri, string root)
{
    var careerWords = new[] { "career", "jobs", "join", "hiring", "work-with", "work-at", "opportunit", "vacanc" };
    var otherWords = new[] { "contact", "about", "team" };

    var careers = new List<string>();
    var others = new List<string>();

    foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
    {
        var href = a.GetAttributeValue("href", "");
        if (href.StartsWith("mailto:") || href.StartsWith("tel:") || href.StartsWith("#")) continue;
        if (!Uri.TryCreate(baseUri, href, out var abs)) continue;
        if (abs.Scheme != "http" && abs.Scheme != "https") continue;
        if (RootDomain(abs.Host) != root) continue;

        var key = (abs.AbsolutePath + " " + a.InnerText).ToLowerInvariant();
        var clean = abs.GetLeftPart(UriPartial.Path);

        if (careerWords.Any(key.Contains)) { if (!careers.Contains(clean)) careers.Add(clean); }
        else if (otherWords.Any(key.Contains)) { if (!others.Contains(clean)) others.Add(clean); }
    }

    // Careers pages pehle, phir contact/about
    return careers.Take(2).Concat(others.Take(2)).Distinct().ToList();
}

static bool IsCareerUrl(string url)
{
    var u = url.ToLowerInvariant();
    return new[] { "career", "jobs", "join", "hiring", "work-with", "vacanc", "opportunit" }.Any(u.Contains);
}

static MatchCollection FindEmails(string text) =>
    Regex.Matches(text, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}");

static void CollectEmails(string html, HtmlDocument doc, string root, string pageUrl, Dictionary<string, string> found)
{
    var candidates = new HashSet<string>();

    // 1. Raw HTML mein regex (mailto: bhi isi mein aa jata hai)
    foreach (Match m in FindEmails(System.Net.WebUtility.HtmlDecode(html)))
        candidates.Add(m.Value);

    // 2. Cloudflare "email protection" (data-cfemail) decode karo
    foreach (var n in doc.DocumentNode.SelectNodes("//*[@data-cfemail]") ?? new HtmlNodeCollection(null))
    {
        var decoded = DecodeCfEmail(n.GetAttributeValue("data-cfemail", ""));
        if (decoded != "") candidates.Add(decoded);
    }

    foreach (var raw in candidates)
    {
        var email = raw.Trim().Trim('.', ',', ';').ToLowerInvariant();
        if (!IsUsableEmail(email, root)) continue;
        found.TryAdd(email, pageUrl);
    }
}

static bool IsUsableEmail(string email, string root)
{
    var at = email.LastIndexOf('@');
    if (at < 1) return false;

    var domain = email[(at + 1)..];
    var local = email[..at];

    // Images/files jaise "logo@2x.png" ko hatao
    var badExt = new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".css", ".js", ".ico", ".woff" };
    if (badExt.Any(domain.EndsWith)) return false;

    // Placeholder/junk
    var junk = new[] { "example", "yourname", "your-email", "youremail", "sentry", "wixpress", "domain.com", "email@", "name@", "user@", "test@" };
    if (junk.Any(email.Contains)) return false;
    if (local.Length > 40) return false;

    // Sirf company ke apne domain ki emails (gmail/third-party nahi)
    return RootDomain(domain) == root;
}

static string DecodeCfEmail(string hex)
{
    if (hex.Length < 4 || hex.Length % 2 != 0) return "";
    try
    {
        var key = Convert.ToInt32(hex[..2], 16);
        var sb = new StringBuilder();
        for (var i = 2; i < hex.Length; i += 2)
            sb.Append((char)(Convert.ToInt32(hex.Substring(i, 2), 16) ^ key));
        return sb.ToString();
    }
    catch { return ""; }
}

static string EmailType(string email)
{
    var local = email.Split('@')[0];
    var hr = new[] { "career", "job", "hr", "recruit", "talent", "hiring", "people", "apply", "join", "resume", "cv" };
    if (hr.Any(local.Contains)) return "HR/Careers";
    return "General";
}

static int TypeRank(string email) => EmailType(email) == "HR/Careers" ? 0 : 1;

// www.abc.com.pk -> abc.com.pk ; sub.abc.com -> abc.com
static string RootDomain(string host)
{
    host = host.ToLowerInvariant();
    if (host.StartsWith("www.")) host = host[4..];

    var labels = host.Split('.');
    if (labels.Length <= 2) return host;

    var secondLevel = new[] { "com", "co", "org", "net", "gov", "edu" };
    var takeLast = (labels[^1].Length == 2 && secondLevel.Contains(labels[^2])) ? 3 : 2;
    return string.Join(".", labels.TakeLast(takeLast));
}

// Search result title se company ka naam nikalo
static string CleanName(string title, string root)
{
    var name = Regex.Split(title, @"\s[-|–—:]\s|\s\|\s")[0].Trim();
    if (name.Length < 2 || name.Length > 60)
    {
        var label = root.Split('.')[0];
        name = char.ToUpper(label[0]) + label[1..];
    }
    return name;
}

static void SaveCsv(List<Row> rows, string path)
{
    static string Esc(string s) =>
        "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    var sb = new StringBuilder();
    sb.AppendLine("Region,Company,Website,CareersPage,Email,EmailType,FoundOn");

    foreach (var r in rows)
        sb.AppendLine(string.Join(",",
            Esc(r.Region), Esc(r.Company), Esc(r.Website),
            Esc(r.CareersPage), Esc(r.Email), Esc(r.EmailType), Esc(r.FoundOn)));

    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
}

// =============== Types ===============
record SearchHit(string Title, string Link);
record Company(string Region, string Name, string BaseUrl, string Root);
record Row(string Region, string Company, string Website, string CareersPage, string Email, string EmailType, string FoundOn);