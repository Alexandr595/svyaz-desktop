using System.Text.Json;

namespace Svyaz.Core;

public sealed class SessionJournal(string directory)
{
    private readonly object gate = new();
    public void Append(string text)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                File.Move(path, Path.Combine(directory, "session.previous.log"), true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + "  " + text + Environment.NewLine);
        }
    }

    public void Status(SessionState state, string message, bool engineRunning, bool proxyRunning, int proxyPort,
        IReadOnlyList<ProbeResult> results)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "status.json");
            var snapshot = new
            {
                checkedAt = DateTimeOffset.Now, pid = Environment.ProcessId, state = state.ToString(), message,
                engineRunning, proxyRunning, proxyPort,
                results = results.Select(r => new
                {
                    url = new Uri(r.Url).GetLeftPart(UriPartial.Path), r.IsCanary, r.Healthy,
                    r.StatusCode, r.ElapsedMs, r.Detail, r.ViaExternalProxy
                })
            };
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }
}

public sealed class InstanceLease : IDisposable
{
    private readonly FileStream handle;
    private InstanceLease(FileStream handle) => this.handle = handle;
    public static InstanceLease? TryAcquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try { return new InstanceLease(new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)); }
        catch (IOException) { return null; }
    }
    public void Dispose() => handle.Dispose();
}
