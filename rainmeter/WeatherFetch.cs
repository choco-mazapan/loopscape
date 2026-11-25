// WeatherFetch - prints the current weather as key=value lines for the Rainmeter widget.
// Location: the city typed in the widget Settings (looked up once with Open-Meteo's geocoder), or, if chosen there,
// the Windows location service (Windows asks for permission; the city name then comes from BigDataCloud).
// There is no IP-address lookup. Weather: Open-Meteo (free, no API key). All requests are HTTPS.
// Weather is fetched at most once an hour (cached in weathercache.txt next to the exe); a Windows location at most
// once a day (lastlocation.txt), a typed city only when it changes (citylocation.txt). "force" skips the caches;
// "nowcast" (the Rain widget) keeps the cache for 15 minutes only, so the rain for the next hours stays current.
// Usage: WeatherFetch.exe [fahrenheit|celsius] [force|nowcast|""] [city|windows] ["City name"]
// Build: csc /target:exe /r:System.Device.dll /r:System.Windows.Forms.dll /win32icon:Loopscape.ico /out:Loopscape\@Resources\WeatherFetch.exe WeatherFetch.cs
using System;
using System.Device.Location;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// Windows' location settings list apps by this name
[assembly: System.Reflection.AssemblyTitle("Loopscape")]
[assembly: System.Reflection.AssemblyProduct("Loopscape")]
[assembly: System.Reflection.AssemblyDescription("Weather for the Loopscape widgets")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

static class WeatherFetch
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string Get(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Loopscape-widget";
        req.Timeout = 10000;
        using (var resp = req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }
    static double Num(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
        return m.Success ? double.Parse(m.Groups[1].Value, Inv) : double.NaN;
    }
    static string Str(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    // Values of a JSON array, e.g. "time":["2026-11-23","2026-11-24"] -> {"2026-11-23", ...} (quotes kept off)
    static string[] Arr(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success) return new string[0];
        string[] parts = m.Groups[1].Value.Split(',');
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim().Trim('"');
        return parts;
    }
    static string Round(string v)
    {
        double d;
        return double.TryParse(v, NumberStyles.Float, Inv, out d) ? Math.Round(d).ToString("0", Inv) : "--";
    }

    // Location + weather are fetched at most once per CacheMinutes; every run in between (timer, skin refresh, unit
    // change) prints the cached result. Values are cached in Celsius and converted when printed.
    const int CacheMinutes = 60, NowcastMinutes = 15;
    static string CachePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weathercache.txt"); } }

    [STAThread] // the Windows location API needs a single-threaded apartment
    static int Main(string[] args)
    {
        // Rainmeter reads our stdout; keep it plain ASCII-safe UTF-8
        Console.OutputEncoding = new UTF8Encoding(false);
        bool f = args.Length > 0 && !args[0].ToLowerInvariant().StartsWith("c");
        // After the unit: "force" ("Refresh weather now" in the widget's menu), the mode (city / windows) and the city,
        // in any order; empty arguments are skipped
        bool force = false, nowcast = false;
        string mode = null, cityQuery = "";
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].Trim(), low = a.ToLowerInvariant();
            if (low == "force") force = true;
            else if (low == "nowcast") nowcast = true;
            else if (mode == null && (low == "city" || low == "windows")) mode = low;
            else if (a.Length > 0 && cityQuery.Length == 0) cityQuery = CleanCity(a);
        }
        if (mode == null) mode = "city";
        if (mode == "city" && cityQuery.Length == 0) { Console.WriteLine("error=Set your city in Settings"); return 1; }
        // the cache belongs to one location setting; changing it fetches again
        string where = "where=" + mode + "|" + cityQuery.ToLowerInvariant();

        string[] cached = null; TimeSpan age = TimeSpan.MaxValue;
        try
        {
            if (File.Exists(CachePath))
            {
                string[] all = File.ReadAllLines(CachePath);
                // (a cache from before the rain series existed is fetched again)
                if (all.Length > 2 && all[0].StartsWith("fetched=") && all[1] == where && all.Any(l => l.StartsWith("rain=")))
                {
                    age = DateTime.UtcNow - new DateTime(long.Parse(all[0].Substring(8), Inv), DateTimeKind.Utc);
                    cached = new string[all.Length - 2];
                    Array.Copy(all, 2, cached, 0, cached.Length);
                }
            }
        }
        catch { cached = null; }

        string[] lines = cached;
        if (force || cached == null || age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(nowcast ? NowcastMinutes : CacheMinutes))
        {
            string error;
            string[] fresh = Fetch(force, mode, cityQuery, out error);
            if (fresh != null)
            {
                lines = fresh;
                try { File.WriteAllLines(CachePath, new[] { "fetched=" + DateTime.UtcNow.Ticks.ToString(Inv), where }.Concat(fresh)); } catch { }
            }
            else if (cached == null || age >= TimeSpan.FromHours(6)) { Console.WriteLine("error=" + error); return 1; }
            // otherwise: fetch failed, keep showing the cached weather (it is retried on the next run after the hour)
        }
        Print(lines, f);
        return 0;
    }

    static string Temp(string celsius, bool f, string format)
    {
        double d;
        if (!double.TryParse(celsius, NumberStyles.Float, Inv, out d)) return "--";
        return (f ? d * 9 / 5 + 32 : d).ToString(format, Inv);
    }

    // Cached lines (Celsius) -> widget output in the chosen unit; isday follows the clock between fetches
    static void Print(string[] lines, bool f)
    {
        string rise = "", set = "";
        foreach (string l in lines)
        {
            if (l.StartsWith("sunrise=")) rise = l.Substring(8);
            if (l.StartsWith("sunset=")) set = l.Substring(7);
        }
        string now = DateTime.Now.ToString("HH:mm", Inv);
        foreach (string l in lines)
        {
            if (l.StartsWith("temp=")) Console.WriteLine("temp=" + Temp(l.Substring(5), f, "0.#"));
            else if (l.StartsWith("isday=") && rise.Length == 5 && set.Length == 5)
                Console.WriteLine("isday=" + (string.CompareOrdinal(now, rise) >= 0 && string.CompareOrdinal(now, set) < 0 ? "1" : "0"));
            else if (Regex.IsMatch(l, "^fc\\d="))
            {
                string[] p = l.Split('|');   // fcN=Day|code|max|min
                Console.WriteLine(p.Length == 4 ? p[0] + "|" + p[1] + "|" + Temp(p[2], f, "0") + "|" + Temp(p[3], f, "0") : l);
            }
            else Console.WriteLine(l);
        }
    }

    // Letters, digits, spaces and , . ' - only (it arrives on the command line from Settings.inc)
    static string CleanCity(string s)
    {
        var b = new StringBuilder();
        foreach (char ch in s ?? "") if (char.IsLetterOrDigit(ch) || " ,.'-".IndexOf(ch) >= 0) b.Append(ch);
        return Regex.Replace(b.ToString(), "\\s+", " ").Trim();
    }

    // Typed city -> coordinates with Open-Meteo's geocoder. "Paris, France" searches "Paris" and prefers a result
    // whose country / region matches the rest. Saved in citylocation.txt (query, lat, lon, name) until the city changes.
    static bool Geocode(string query, bool force, out double lat, out double lon, out string name, out string error)
    {
        lat = lon = double.NaN; name = ""; error = null;
        string cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "citylocation.txt");
        try
        {
            if (!force && File.Exists(cache))
            {
                string[] p = File.ReadAllLines(cache);
                if (p.Length >= 4 && p[0] == query.ToLowerInvariant())
                {
                    lat = double.Parse(p[1], Inv); lon = double.Parse(p[2], Inv); name = p[3];
                    return true;
                }
            }
        }
        catch { }
        int comma = query.IndexOf(',');
        string head = (comma >= 0 ? query.Substring(0, comma) : query).Trim();
        string rest = comma >= 0 ? query.Substring(comma + 1).Trim().ToLowerInvariant() : "";
        string j;
        try { j = Get("https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(head)); }
        catch (Exception e) { error = "City lookup failed: " + e.Message.Replace('\n', ' '); return false; }
        var res = Regex.Match(j, "\"results\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
        string pick = null;
        if (res.Success)
            foreach (Match o in Regex.Matches(res.Groups[1].Value, "\\{[^{}]*\\}"))
            {
                if (pick == null) pick = o.Value;
                if (rest.Length == 0) break;
                string places = (Str(o.Value, "country") + "|" + Str(o.Value, "country_code") + "|" + Str(o.Value, "admin1")).ToLowerInvariant();
                if (places.Contains(rest)) { pick = o.Value; break; }
            }
        if (pick == null) { error = "City not found: " + query; return false; }
        lat = Num(pick, "latitude"); lon = Num(pick, "longitude"); name = Str(pick, "name");
        if (double.IsNaN(lat) || double.IsNaN(lon)) { error = "City not found: " + query; return false; }
        if (name.Length == 0) name = head;
        try { File.WriteAllLines(cache, new[] { query.ToLowerInvariant(), lat.ToString("R", Inv), lon.ToString("R", Inv), name }); } catch { }
        return true;
    }

    // Finds the location and fetches the weather (in Celsius). Returns the output lines, or null with error set.
    // Where we are is looked up at most once per LocationHours (or on "force"); weather fetches in between reuse the
    // saved coordinates and city, so they never touch Windows location. lastlocation.txt: lat,lon,ticks,source,city
    const int LocationHours = 24;

    static string[] Fetch(bool force, string mode, string cityQuery, out string error)
    {
        error = null;
        var outp = new System.Collections.Generic.List<string>();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        double lat = double.NaN, lon = double.NaN;
        string city = "", source = "", locNote = "";
        if (mode == "city")
        {
            if (!Geocode(cityQuery, force, out lat, out lon, out city, out error)) return null;
            source = "city";
        }
        else
        {
        string cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lastlocation.txt");
        double[] saved = null; string savedSource = "", savedCity = ""; TimeSpan savedAge = TimeSpan.MaxValue;
        try
        {
            if (File.Exists(cache))
            {
                string[] p = File.ReadAllText(cache).Trim().Split(new[] { ',' }, 5);
                if (p.Length >= 3)
                {
                    saved = new[] { double.Parse(p[0], Inv), double.Parse(p[1], Inv) };
                    savedAge = DateTime.UtcNow - new DateTime(long.Parse(p[2], Inv), DateTimeKind.Utc);
                    if (p.Length >= 4) savedSource = p[3];
                    if (p.Length >= 5) savedCity = p[4];
                }
            }
        }
        catch { saved = null; }

        bool relocate = force || saved == null || savedAge < TimeSpan.Zero || savedAge >= TimeSpan.FromHours(LocationHours)
                        || savedCity.Length == 0;
        if (!relocate)
        {
            lat = saved[0]; lon = saved[1]; city = savedCity; source = (savedSource.Length > 0 ? savedSource : "windows") + "-saved";
        }

        // 1) Windows location service (only works when Location is turned on in Settings)
        if (relocate) try
        {
            using (var w = new GeoCoordinateWatcher(GeoPositionAccuracy.Default))
            {
                w.Start(true);
                // A fix can take a few seconds (Wi-Fi scan); keep pumping messages so updates arrive
                var c = w.Position.Location;
                for (int i = 0; i < 150 && (c.IsUnknown || w.Status != GeoPositionStatus.Ready); i++)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                    c = w.Position.Location;
                    if (w.Status == GeoPositionStatus.Disabled) break;
                }
                bool started = w.Status == GeoPositionStatus.Ready;
                if (started && !c.IsUnknown) { lat = c.Latitude; lon = c.Longitude; source = "windows"; }
                else locNote = "windows location unavailable (" + w.Permission + ", " + w.Status + ")";
            }
        }
        catch (Exception e) { locNote = "windows location error: " + e.Message; }

        // Windows had no fix this time: an older saved position (even past LocationHours) is better than nothing
        if (relocate && double.IsNaN(lat) && saved != null && savedAge < TimeSpan.FromDays(7)
            && (savedSource.Length == 0 || savedSource.StartsWith("windows")))
        {
            lat = saved[0]; lon = saved[1]; city = savedCity; source = "windows-saved";
        }

        if (double.IsNaN(lat)) { error = "Windows location is off - set a city in Settings"; return null; }

        // Windows location gives only coordinates: look up the city name (BigDataCloud, free, no key)
        if (city.Length == 0)
        {
            try
            {
                string g = Get("https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=" + lat.ToString("0.###", Inv) +
                               "&longitude=" + lon.ToString("0.###", Inv) + "&localityLanguage=en");
                city = Str(g, "city");
                if (city.Length == 0) city = Str(g, "locality");
            }
            catch { }
        }

        // Save where we are with a fresh timestamp, even if Windows had no fix and an older position was reused:
        // the next location lookup is then LocationHours away, not on the next hourly weather fetch
        if (relocate)
        {
            try
            {
                File.WriteAllText(cache, lat.ToString("R", Inv) + "," + lon.ToString("R", Inv) + "," + DateTime.UtcNow.Ticks.ToString(Inv) +
                                         "," + source.Replace("-saved", "") + "," + city.Replace('\r', ' ').Replace('\n', ' '));
            }
            catch { }
        }
        }   // windows mode

        try
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.###", Inv) +
                         "&longitude=" + lon.ToString("0.###", Inv) +
                         "&current=temperature_2m,weather_code,is_day" +
                         "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset&forecast_days=4" +
                         "&minutely_15=precipitation&forecast_minutely_15=24" +
                         "&temperature_unit=celsius&timezone=auto";
            string wj = Get(url);
            var cur = Regex.Match(wj, "\"current\"\\s*:\\s*\\{([^}]*)\\}");
            if (!cur.Success) { error = "bad weather response"; return null; }
            string c = cur.Groups[1].Value;
            outp.Add("temp=" + Num(c, "temperature_2m").ToString("0.##", Inv));
            outp.Add("code=" + Num(c, "weather_code").ToString("0", Inv));
            outp.Add("isday=" + Num(c, "is_day").ToString("0", Inv));
            outp.Add("city=" + city);
            outp.Add("source=" + source);
            if (locNote.Length > 0) outp.Add("locnote=" + locNote);
            // when the weather was fetched (not when it was last printed from the cache)
            outp.Add("time=" + DateTime.Now.ToString("HH:mm", Inv));

            // Next 3 days (index 0 is today): fcN=Day|code|max|min
            var daily = Regex.Match(wj, "\"daily\"\\s*:\\s*\\{([^}]*)\\}");
            string sunrise = "", sunset = "", sunrise2 = "";
            if (daily.Success)
            {
                string d = daily.Groups[1].Value;
                // "2026-11-22T06:58" (local time at the location) -> "06:58"
                string[] rise = Arr(d, "sunrise"), set = Arr(d, "sunset");
                Func<string[], int, string> hm = (a, i) => i < a.Length && a[i].Length >= 16 ? a[i].Substring(11, 5) : "";
                sunrise = hm(rise, 0); sunset = hm(set, 0); sunrise2 = hm(rise, 1);
                if (sunrise.Length > 0) outp.Add("sunrise=" + sunrise);
                if (sunset.Length > 0) outp.Add("sunset=" + sunset);
                if (sunrise2.Length > 0) outp.Add("sunrise2=" + sunrise2);
                string[] days = Arr(d, "time"), codes = Arr(d, "weather_code"), hi = Arr(d, "temperature_2m_max"), lo = Arr(d, "temperature_2m_min");
                for (int i = 1; i <= 3 && i < days.Length && i < codes.Length && i < hi.Length && i < lo.Length; i++)
                {
                    DateTime dt;
                    string label = DateTime.TryParseExact(days[i].Trim('"'), "yyyy-MM-dd", Inv, DateTimeStyles.None, out dt) ? dt.ToString("ddd", Inv) : "Day " + i;
                    // raw Celsius here; Print rounds after converting
                    outp.Add("fc" + i + "=" + label + "|" + codes[i] + "|" + hi[i] + "|" + lo[i]);
                }
            }
            // Rain for the next 6 hours in 15-minute steps (mm in each step): rain=2026-11-24T14:15|0,0,0.1,...
            // (local time at the location; the Rain widget lines it up with the clock)
            var m15 = Regex.Match(wj, "\"minutely_15\"\\s*:\\s*\\{([^}]*)\\}");
            if (m15.Success)
            {
                string[] t = Arr(m15.Groups[1].Value, "time"), p = Arr(m15.Groups[1].Value, "precipitation");
                if (t.Length > 0 && p.Length > 0)
                    outp.Add("rain=" + t[0] + "|" + string.Join(",", p.Select(v => v == "null" ? "0" : v)));
            }
            else outp.Add("rain=");
            // For Loopscape (night by sunset, wallpaper that matches the weather): weathernow.txt next to this exe
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weathernow.txt"),
                    "date=" + DateTime.Now.ToString("yyyy-MM-dd", Inv) + "\r\nfetched=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv) +
                    "\r\ncode=" + Num(c, "weather_code").ToString("0", Inv) + "\r\nisday=" + Num(c, "is_day").ToString("0", Inv) +
                    "\r\nsunrise=" + sunrise + "\r\nsunset=" + sunset + "\r\nsunrise2=" + sunrise2 + "\r\n");
            }
            catch { }
            return outp.ToArray();
        }
        catch (Exception e) { error = e.Message.Replace('\n', ' '); return null; }
    }
}
