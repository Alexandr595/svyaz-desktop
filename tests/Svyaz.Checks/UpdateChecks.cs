using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Svyaz.Core;

static class UpdateChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified update fixture");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static ReleaseUpdate Release => new(new Version(0, 4, 2), "v0.4.2", true,
        new Uri("https://github.com/Alexandr595/svyaz-desktop/releases/download/v0.4.2/Svyaz-Setup-0.4.2-x64.exe"), Payload.Length, Hash);

    public static (string Name, Func<Task> Test)[] Cases() =>
    [
        ("Updates compare numeric versions, normalize assembly revision, and honor preview channel", () =>
        {
            var feed = Feed(Entry("0.4.2", true), Entry("0.10.0"), Entry("0.9.0"), Entry("1.0.0", draft: true));
            Assert(ReleaseUpdateService.SelectUpdate(feed, new Version(0, 4, 1, 0), true)?.Version == new Version(0, 10, 0));
            Assert(ReleaseUpdateService.SelectUpdate(Feed(Entry("0.4.2", true)), new Version(0, 4, 1), false) == null);
            Assert(ReleaseUpdateService.SelectUpdate(Feed(Entry("0.4.1")), new Version(0, 4, 1, 0), true) == null);
            return Task.CompletedTask;
        }),
        ("Updates reject foreign assets, missing digests, drafts, and invalid sizes", () =>
        {
            foreach (var entry in new[] {
                Entry("0.4.2", url: "https://evil.example/update.exe"),
                Entry("0.4.2", digest: ""), Entry("0.4.2", digest: "sha256:0000"),
                Entry("0.4.2", size: ReleaseUpdateService.MaxInstallerBytes + 1), Entry("0.4.2", size: 0),
                Entry("0.4.2", draft: true), Entry("0.4.2", state: "new") })
                Assert(ReleaseUpdateService.SelectUpdate(Feed(entry), new Version(0, 4, 1), true) == null);
            return Task.CompletedTask;
        }),
        ("Updates fetch the public repository feed without credentials", async () =>
        {
            using var http = new HttpClient(new Handler((request, _) =>
            {
                Assert(request.RequestUri?.Host == "api.github.com" && request.RequestUri.AbsolutePath == "/repos/Alexandr595/svyaz-desktop/releases");
                Assert(request.Headers.Authorization == null && request.Headers.UserAgent.Count > 0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Feed(Entry("0.4.2"))) });
            }));
            using var service = new ReleaseUpdateService(http);
            Assert((await service.CheckAsync(new Version(0, 4, 1), true, CancellationToken.None))?.Version == Release.Version);
        }),
        ("Update download verifies SHA-256 before exposing an executable", async () =>
        {
            using var http = Respond(Payload);
            using var service = new ReleaseUpdateService(http);
            var directory = TemporaryDirectory();
            try
            {
                var path = await service.DownloadAsync(Release, directory, null, CancellationToken.None);
                Assert(File.Exists(path) && Path.GetFileName(path) == Release.InstallerName);
                Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(Payload));
                Assert(!Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories).Any());
            }
            finally { Directory.Delete(directory, true); }
        }),
        ("Update hash mismatch and truncated download leave no executable or partial file", async () =>
        {
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes(new string('x', Payload.Length)), Payload[..^1], Payload.Concat(new byte[] { 1 }).ToArray() })
            {
                using var http = Respond(bytes, Payload.Length);
                using var service = new ReleaseUpdateService(http);
                var directory = TemporaryDirectory();
                try
                {
                    await Reject(() => service.DownloadAsync(Release, directory, null, CancellationToken.None));
                    Assert(!Directory.EnumerateFileSystemEntries(directory).Any());
                }
                finally { Directory.Delete(directory, true); }
            }
        }),
        ("Update redirect cannot leave GitHub HTTPS asset hosts", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new Handler((_, _) =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("http://evil.example/payload.exe");
                return Task.FromResult(response);
            }));
            using var service = new ReleaseUpdateService(http);
            var directory = TemporaryDirectory();
            try { await Reject(() => service.DownloadAsync(Release, directory, null, CancellationToken.None)); Assert(calls == 1); }
            finally { Directory.Delete(directory, true); }
        }),
        ("Update download follows official CDN redirects and still verifies bytes", async () =>
        {
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.RequestUri?.Host == "github.com")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/fixture");
                    return Task.FromResult(response);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            }));
            using var service = new ReleaseUpdateService(http);
            var directory = TemporaryDirectory();
            try { Assert(File.Exists(await service.DownloadAsync(Release, directory, null, CancellationToken.None))); }
            finally { Directory.Delete(directory, true); }
        }),
        ("Cancelling an update does not produce an executable", async () =>
        {
            using var cancel = new CancellationTokenSource();
            using var http = new HttpClient(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(1000, token); return new(HttpStatusCode.OK); }));
            using var service = new ReleaseUpdateService(http);
            var directory = TemporaryDirectory();
            try
            {
                try { await service.DownloadAsync(Release, directory, null, cancel.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Assert(!Directory.EnumerateFileSystemEntries(directory).Any()); }
            }
            finally { Directory.Delete(directory, true); }
        }),
        ("Update handoff preserves a spaced path and keeps NSIS /D last", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "Связь Desktop");
            Assert(UpdateInstallerCommand.Arguments(path) == "/UPDATE /D=" + path);
            foreach (var invalid in new[] { Path.GetPathRoot(path)!, path + "\"", path + "\n", "relative" })
            {
                try { _ = UpdateInstallerCommand.Arguments(invalid); throw new Exception("Invalid path accepted"); }
                catch (ArgumentException) { }
            }
            return Task.CompletedTask;
        }),
        ("Cancelling a partial update removes downloaded bytes", async () =>
        {
            using var cancel = new CancellationTokenSource();
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new CancellingStream(Payload, cancel)) })));
            using var service = new ReleaseUpdateService(http);
            var directory = TemporaryDirectory();
            try
            {
                try { await service.DownloadAsync(Release, directory, null, cancel.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Assert(!Directory.EnumerateFileSystemEntries(directory).Any()); }
            }
            finally { Directory.Delete(directory, true); }
        })
    ];

    private static object Entry(string version, bool preview = false, bool draft = false, string? url = null, string? digest = null, long? size = null, string state = "uploaded") => new
    {
        tag_name = "v" + version, prerelease = preview, draft,
        assets = new[] { new { name = $"Svyaz-Setup-{version}-x64.exe", state, size = size ?? Payload.Length, digest = digest ?? "sha256:" + Hash,
            browser_download_url = url ?? $"https://github.com/Alexandr595/svyaz-desktop/releases/download/v{version}/Svyaz-Setup-{version}-x64.exe" } }
    };
    private static byte[] Feed(params object[] releases) => JsonSerializer.SerializeToUtf8Bytes(releases);
    private static HttpClient Respond(byte[] bytes, long? claimedSize = null) => new(new Handler((_, _) =>
    {
        var content = new ByteArrayContent(bytes);
        if (claimedSize != null) content.Headers.ContentLength = claimedSize;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }));
    private static string TemporaryDirectory() { var path = Path.Combine(Path.GetTempPath(), "svyaz-update-check-" + Guid.NewGuid()); Directory.CreateDirectory(path); return path; }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe update accepted"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position > 0) cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(8, buffer.Length)], token);
        }
    }
}
