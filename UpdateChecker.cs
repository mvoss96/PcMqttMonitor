using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

// Checks GitHub for a newer release. Notify-only by design: the app runs
// elevated out of Program Files, so instead of self-updating we point the
// user at the releases page to run the installer themselves.
static class UpdateChecker
{
    public const string RepoOwner = "mvoss96";
    public const string RepoName  = "PcMqttMonitor";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";
    const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            // Assembly versions are 4-part (x.y.z.0) — normalize to 3 parts for comparison/display.
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    // Returns the latest release version if it is newer than the running one, otherwise null.
    // Throws on network/API errors — the caller logs and retries later.
    public static async Task<Version?> CheckAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(RepoName, CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var json = await http.GetStringAsync(ApiUrl, ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (tag.StartsWith('v')) tag = tag[1..];
        if (!Version.TryParse(tag, out var latest))
            return null;

        var latest3 = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        return latest3 > CurrentVersion ? latest3 : null;
    }
}
