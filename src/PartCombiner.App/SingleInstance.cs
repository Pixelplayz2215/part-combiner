using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PartCombiner;

/// <summary>
/// Keeps one Part Combiner window per user session. A second launch (dropping parts onto the exe, "Open with",
/// the Explorer menu, which starts one process per selected file) hands its paths to the running window and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = BuildId();
    private static readonly string MutexName = @"Local\PartCombiner-" + Id;
    private static readonly string PipeName = "PartCombiner-" + Id;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Becomes the primary instance, or returns null if another one is already running.</summary>
    public static SingleInstance? TryClaim()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
            return new SingleInstance(mutex);

        try
        {
            if (mutex.WaitOne(0))
                return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex); // the previous owner crashed; the mutex is ours now
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>Sends paths to the running instance. False if it couldn't be reached (then run normally).</summary>
    public static bool TrySend(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Several copies can start at once; the first may still be setting up its pipe.
            client.Connect(TimeSpan.FromSeconds(5));
            _ = AllowSetForegroundWindow(ASFW_ANY); // so the running window may come to the front
            var full = paths.Select(p => { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }).ToArray();
            client.Write(JsonSerializer.SerializeToUtf8Bytes(full));
            client.Flush();
            client.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Starts accepting paths from later launches. <paramref name="received"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> received) => _ = Task.Run(() => ListenAsync(received, _cts.Token));

    private static async Task ListenAsync(Action<string[]> received, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // Read on the side so the next launch can connect right away.
            _ = Task.Run(async () =>
            {
                await using (server)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        using var buffer = new MemoryStream();
                        await server.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
                        var paths = JsonSerializer.Deserialize<string[]>(buffer.ToArray()) ?? [];
                        received(paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray());
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
                }
            }, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { } // not owned by this thread
        _mutex.Dispose();
    }

    private static string BuildId()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { user = Environment.UserName; }
        // Pipe names are machine-wide, so include the session for fast user switching / RDP.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var raw = $"{user}-{self.SessionId}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
