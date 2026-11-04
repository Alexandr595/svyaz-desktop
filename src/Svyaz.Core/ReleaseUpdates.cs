using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Svyaz.Core;

public sealed record ReleaseUpdate(Version Version, string Tag, bool Preview, Uri Installer, long Size, string Sha256)
{
    public string InstallerName => $"Svyaz-Setup-{Version}-x64.exe";
    public Uri Page => new($"https://github.com/Alexandr595/svyaz-desktop/releases/tag/{Tag}");
}

public sealed class ReleaseUpdateService : IDisposable
{
    public const string Repository = "Alexandr595/svyaz-desktop";
    public const long MaxInstallerBytes = 256L * 1024 * 1024;
    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repository}/releases?per_page=20");
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public ReleaseUpdateService(HttpClient? client = null)
    {
        ownsClient = client == null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ReleaseUpdate?> CheckAsync(Version installed, bool includePreview, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(Feed, false, deadline.Token).ConfigureAwait(false);
        var json = await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        return SelectUpdate(json, installed, includePreview);
    }

    public static ReleaseUpdate? SelectUpdate(byte[] json, Version installed, bool includePreview)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Некорректный список выпусков GitHub.");
        var current = new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build));
        ReleaseUpdate? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft")) continue;
            var preview = Flag(release, "prerelease");
            if (preview && !includePreview) continue;
            var tag = Text(release, "tag_name");
            if (!Regex.IsMatch(tag, @"^v?(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$", RegexOptions.CultureInvariant)
                || !Version.TryParse(tag.TrimStart('v'), out var version) || version <= current || (newest != null && version <= newest.Version)) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var name = $"Svyaz-Setup-{version}-x64.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != name || Text(asset, "state") != "uploaded") continue;
                var digest = Text(asset, "digest");
                if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
                    || !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size is <= 0 or > MaxInstallerBytes
                    || !Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url)) continue;
                var candidate = new ReleaseUpdate(version, tag, preview, url, size, digest[7..].ToLowerInvariant());
                if (!ValidRelease(candidate)) continue;
                newest = candidate;
                break;
            }
        }
        return newest;
    }

    public async Task<string> DownloadAsync(ReleaseUpdate release, string cacheDirectory, IProgress<int>? progress, CancellationToken token)
    {
        if (!ValidRelease(release)) throw new InvalidDataException("Недопустимый файл обновления.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await GetAsync(release.Installer, true, deadline.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("Размер обновления не совпадает с GitHub.");
        var folder = Path.Combine(Path.GetFullPath(cacheDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, release.InstallerName + ".part");
        var complete = Path.Combine(folder, release.InstallerName);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                progress?.Report(0);
                while (true)
                {
                    var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Size) throw new InvalidDataException("Файл обновления превышает ожидаемый размер.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    progress?.Report((int)(received * 100 / release.Size));
                }
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                throw new InvalidDataException("Проверка SHA-256 обновления не пройдена. Установка отменена.");
            File.Move(partial, complete);
            return complete;
        }
        catch
        {
            File.Delete(partial);
            Directory.Delete(folder);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri initial, bool download, CancellationToken token)
    {
        var url = initial;
        for (var hop = 0; hop < 6; hop++)
        {
            if (!TrustedUrl(url, download)) throw new InvalidDataException("Недопустимый адрес обновления.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Svyaz-Desktop-Updater/1.0");
            request.Headers.Accept.ParseAdd(download ? "application/octet-stream" : "application/vnd.github+json");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (next == null) throw new InvalidDataException("Не найден файл обновления.");
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Слишком много перенаправлений обновления.");
    }

    private static bool TrustedUrl(Uri uri, bool download) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (download
            ? (uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal))
                || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
            : uri.Host == "api.github.com" && uri.AbsolutePath == $"/repos/{Repository}/releases");

    private static bool ValidRelease(ReleaseUpdate release) => release.Size is > 0 and <= MaxInstallerBytes
        && Regex.IsMatch(release.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
        && release.Tag is not null && release.Installer.Scheme == "https" && release.Installer.IsDefaultPort
        && release.Installer.UserInfo.Length == 0 && release.Installer.Fragment.Length == 0 && release.Installer.Query.Length == 0
        && release.Installer.Host == "github.com"
        && release.Installer.AbsolutePath == $"/{Repository}/releases/download/{release.Tag}/{release.InstallerName}"
        && (release.Tag == $"v{release.Version}" || release.Tag == release.Version.ToString());

    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Ответ GitHub слишком большой.");
            output.Write(buffer, 0, count);
        }
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }
}

public static class UpdateInstallerCommand
{
    public static string Arguments(string installationDirectory)
    {
        if (!Path.IsPathFullyQualified(installationDirectory) || installationDirectory.Any(c => char.IsControl(c) || c == '"'))
            throw new ArgumentException("Некорректная папка обновления.");
        var full = Path.GetFullPath(installationDirectory);
        var path = full.TrimEnd(Path.DirectorySeparatorChar);
        if (path == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar)) throw new ArgumentException("Нельзя обновить корень диска.");
        // NSIS requires /D last, with no quotes even when the directory contains spaces.
        return "/UPDATE /D=" + path;
    }
}
