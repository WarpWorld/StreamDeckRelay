using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace StreamDeckRelay;

/// <summary>
/// The actual relay plumbing. The Stream Deck MCP protocol is newline-delimited
/// JSON over a stream, so every proxied connection is a raw bidirectional byte
/// copy - the relay never parses traffic.
/// </summary>
internal static class RelayCore
{
    public const int DefaultPort = 18675;
    public const string PipeName = "elgato-mcp-streamdeck";

    public static event Action<string>? Log;
    /// <summary>Host mode: fires with the number of currently connected relay clients.</summary>
    public static event Action<int>? ActiveClientsChanged;
    /// <summary>Host mode: fires with the remote IP whenever a client connects.</summary>
    public static event Action<string>? ClientSeen;
    private static int _activeClients;
    private static void Info(string message) => Log?.Invoke(message);

    /// <summary>
    /// Quick probe: is the real Elgato pipe present on this machine?
    /// Used to auto-detect host vs client mode (before we create any pipe ourselves).
    /// </summary>
    public static async Task<bool> LocalStreamDeckPipeExistsAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(300);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- host

    /// <summary>Runs host mode: TCP listener forwarding each client into the local Elgato pipe.</summary>
    public static async Task RunHostAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Info($"Hosting on TCP port {port}.");
        ActiveClientsChanged?.Invoke(_activeClients = 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(ct);
                _ = HandleHostClientAsync(tcpClient, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleHostClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        var remote = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Info($"Client connected: {remote}");
        if (tcpClient.Client.RemoteEndPoint is IPEndPoint ep) ClientSeen?.Invoke(ep.Address.ToString());
        ActiveClientsChanged?.Invoke(Interlocked.Increment(ref _activeClients));
        try
        {
            using (tcpClient)
            {
                tcpClient.NoDelay = true;
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await using (pipe)
                {
                    await pipe.ConnectAsync(5000, ct);
                    await RelayAsync(tcpClient.GetStream(), pipe, ct);
                }
            }
            Info($"Client disconnected: {remote}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Info($"Connection {remote} ended: {ex.Message}");
        }
        finally
        {
            ActiveClientsChanged?.Invoke(Interlocked.Decrement(ref _activeClients));
        }
    }

    // -------------------------------------------------------------- client

    /// <summary>
    /// Runs client mode: creates the local Elgato-named pipe and tunnels each
    /// connection to the host PC. <paramref name="resolveHost"/> is called per
    /// connection so discovery can find/refresh the host address lazily.
    /// </summary>
    public static async Task RunClientAsync(
        Func<CancellationToken, Task<IPEndPoint?>> resolveHost,
        CancellationToken ct)
    {
        Info($@"Exposing local pipe \\.\pipe\{PipeName}.");
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // most likely the real Stream Deck app (or another relay) owns the pipe name
                await server.DisposeAsync();
                Info($"Cannot listen on pipe: {ex.Message}");
                await Task.Delay(3000, ct);
                continue;
            }

            _ = HandleLocalClientAsync(server, resolveHost, ct);
        }
    }

    private static async Task HandleLocalClientAsync(
        NamedPipeServerStream localClient,
        Func<CancellationToken, Task<IPEndPoint?>> resolveHost,
        CancellationToken ct)
    {
        try
        {
            await using (localClient)
            {
                var host = await resolveHost(ct);
                if (host is null)
                {
                    Info("Local app connected, but no host PC found yet.");
                    return; // closing the pipe signals failure to the app; it will retry
                }

                Info($"Local app connected; tunneling to {host}.");
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, ct);
                tcp.NoDelay = true;
                await RelayAsync(localClient, tcp.GetStream(), ct);
            }
            Info("Local app disconnected.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Info($"Tunnel ended: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- shared

    /// <summary>Copies bytes in both directions until either side closes.</summary>
    private static async Task RelayAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aToB = CopyUntilClosedAsync(a, b, linked.Token);
        var bToA = CopyUntilClosedAsync(b, a, linked.Token);
        await Task.WhenAny(aToB, bToA);
        // one side closed - tear down the other so pending reads unblock
        linked.Cancel();
        await Task.WhenAll(Swallow(aToB), Swallow(bToA));

        static async Task Swallow(Task task)
        {
            try { await task; }
            catch { /* teardown of the other direction is expected to throw */ }
        }
    }

    private static async Task CopyUntilClosedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            await destination.FlushAsync(ct);
        }
    }
}
