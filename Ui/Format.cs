using System.Text.RegularExpressions;

// Number and name formatting for the UI. Numbers follow the app language
// ("72,6 °C" in German); MQTT/JSON stays invariant and never uses this.
static class Format
{
    internal static string Num(float? v, string fmt = "0.#") =>
        v?.ToString(fmt, L.Culture) ?? "?";

    internal static string? Unit(float? v, string unit, string fmt = "0.#") =>
        v == null ? null : $"{v.Value.ToString(fmt, L.Culture)} {unit}";

    // "170 GB", or "1,0 TB" from 1024 GB on (the values are binary GB).
    internal static string Size(float gb) =>
        gb >= 1024 ? $"{(gb / 1024f).ToString("0.0", L.Culture)} TB" : $"{gb.ToString("0", L.Culture)} GB";

    internal static string Gb(float? mb) =>
        mb == null ? "?" : (mb.Value / 1024f).ToString("0.#", L.Culture);

    internal static string Speed(float? kbps) =>
        kbps == null ? "—"
        : kbps.Value >= 1024 ? $"{(kbps.Value / 1024f).ToString("0.##", L.Culture)} MB/s"
        : $"{kbps.Value.ToString("0.#", L.Culture)} KB/s";

    // "2d 8h 40m", "3h 12m" or "4m 10s"; used for uptime and idle time.
    internal static string Duration(int? sec)
    {
        if (sec == null) return "—";
        var t = TimeSpan.FromSeconds(sec.Value);
        return t.Days  > 0 ? $"{t.Days}d {t.Hours}h {t.Minutes}m"
             : t.Hours > 0 ? $"{t.Hours}h {t.Minutes}m"
             :               $"{t.Minutes}m {t.Seconds}s";
    }

    // Junk that never carries information: (R)/(TM), the CPUID brand-string
    // suffixes "6-Core Processor" / "CPU @ 3.70GHz", and iGPU tails like
    // "w/ Radeon 890M".
    static readonly Regex NameJunkRx =
        new(@"\((R|TM|C)\)|\s\d+-Core Processor|\sCPU\s*@\s*[\d.]+\s*GHz|\sw/.*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Redundant generation prefix: "12th Gen Intel Core i7-12700K".
    static readonly Regex NameGenRx = new(@"^\d+(st|nd|rd|th) Gen ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The brand word between vendor and model (GeForce/Radeon/Arc/Quadro,
    // Core [Ultra], Ryzen variants). Anything unknown stays untouched.
    static readonly Regex NameBrandRx =
        new(@"\b(GeForce|Radeon|Arc|Quadro|Core|Ryzen( AI)?( Threadripper)?( [3579])?) ", RegexOptions.Compiled);

    // Progressive shortening stages for CPU/GPU names; the dashboard shows the
    // first one that fits. Generation prefix, brand word and finally the vendor
    // are dropped in that order:
    //   "NVIDIA GeForce RTX 5070" → "NVIDIA RTX 5070" → "RTX 5070"
    //   "AMD Ryzen 7 9800X3D"     → "AMD 9800X3D"     → "9800X3D"
    internal static List<string> NameStages(string name)
    {
        string s = NameJunkRx.Replace(name, "").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        var stages = new List<string>();
        Add(stages, s);
        Add(stages, s = NameGenRx.Replace(s, ""));
        Add(stages, s = NameBrandRx.Replace(s, "", 1));
        foreach (var vendor in new[] { "NVIDIA ", "AMD ", "Intel " })
        {
            if (s.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) { Add(stages, s[vendor.Length..]); break; }
        }
        return stages;

        static void Add(List<string> l, string v)
        {
            if (v.Length > 0 && (l.Count == 0 || v != l[^1])) l.Add(v);
        }
    }
}
