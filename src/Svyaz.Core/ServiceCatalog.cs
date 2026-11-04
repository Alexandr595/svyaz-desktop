namespace Svyaz.Core;

public sealed record ServicePreset(string Name, string ProbeUrl, string[] Domains);

// Editable starting points, not a complete or vendor-certified inventory.
public static class ServiceCatalog
{
    public static IReadOnlyList<ServicePreset> Presets { get; } =
    [
        new("ChatGPT", "https://chatgpt.com/", ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "challenges.cloudflare.com"]),
        new("Telegram", "https://web.telegram.org/", ["telegram.org", "t.me", "telegram.me", "telegram.dog", "telesco.pe", "tdesktop.com"]),
        new("Discord", "https://discord.com/", ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media"]),
        new("YouTube", "https://www.youtube.com/", ["youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com", "youtubei.googleapis.com", "youtube.googleapis.com", "ggpht.com"])
    ];
    public static string[] AllDomains => Presets.SelectMany(p => p.Domains).Distinct().ToArray();

    public static string[] ProbeUrls(IEnumerable<string> domains)
    {
        var targets = domains.ToArray();
        var known = Presets.SelectMany(p => p.Domains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urls = Presets.Where(p => targets.Any(t => ContainsHost(t, new Uri(p.ProbeUrl).Host)))
            .Select(p => p.ProbeUrl).ToList();
        // CDN apex domains often have no web server; probe the service's main page instead.
        urls.AddRange(targets.Where(t => !known.Contains(t) && !urls.Any(u => ContainsHost(t, new Uri(u).Host)))
            .Select(t => $"https://{t}/"));
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ContainsHost(string domain, string host) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
