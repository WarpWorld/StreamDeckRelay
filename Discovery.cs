using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamDeckRelay;

/// <summary>
/// LAN discovery via UDP broadcast. The host answers probes on a fixed UDP
/// port with the TCP port it relays on; clients broadcast probes and take the
/// first answer.
/// </summary>
internal static class Discovery
{
    public const int UdpPort = 18676;
    private const string ProbeMessage = "STREAMDECKRELAY_DISCOVER_V1";
    private const string ReplyPrefix = "STREAMDECKRELAY_HOST_V1 ";

    /// <summary>Host mode: fires with the client's address whenever a discovery probe is answered.</summary>
    public static event Action<IPAddress>? ProbeAnswered;

    /// <summary>Host side: answer discovery probes until cancelled.</summary>
    public static async Task RunResponderAsync(int tcpPort, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
        udp.EnableBroadcast = true;
        var reply = Encoding.UTF8.GetBytes(ReplyPrefix + tcpPort);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct);
            }
            catch (SocketException)
            {
                continue; // e.g. ICMP port-unreachable from a previous send
            }

            if (Encoding.UTF8.GetString(received.Buffer) == ProbeMessage)
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, ct);
                ProbeAnswered?.Invoke(received.RemoteEndPoint.Address);
            }
        }
    }

    /// <summary>
    /// Client side: broadcast a probe and wait briefly for a host to answer.
    /// Returns the host's relay endpoint, or null if nothing answered.
    /// </summary>
    public static async Task<IPEndPoint?> FindHostAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var probe = Encoding.UTF8.GetBytes(ProbeMessage);

        // broadcast on every interface's subnet plus the global broadcast address
        foreach (var target in GetBroadcastAddresses())
        {
            try
            {
                await udp.SendAsync(probe, new IPEndPoint(target, UdpPort), ct);
            }
            catch (SocketException)
            {
                // some interfaces refuse broadcast; ignore
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var received = await udp.ReceiveAsync(timeoutCts.Token);
                var text = Encoding.UTF8.GetString(received.Buffer);
                if (text.StartsWith(ReplyPrefix, StringComparison.Ordinal) &&
                    int.TryParse(text.AsSpan(ReplyPrefix.Length), out var tcpPort))
                {
                    return new IPEndPoint(received.RemoteEndPoint.Address, tcpPort);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout - no host answered
        }
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        yield return IPAddress.Broadcast;

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }
}
