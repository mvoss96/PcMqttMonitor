using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// Sends each snapshot as a single JSON datagram to a fixed host:port.
// Fire-and-forget: UDP has no acknowledgement and a send to an address
// nobody owns still succeeds. For a target in the local subnet a background
// ARP lookup checks whether any device owns the address; the result only
// changes the card text, sending goes on either way.
sealed class UdpSink : IMetricsSink
{
    readonly UdpClient _client = new();
    readonly UdpConfig _config;
    readonly Action<string> _log;
    readonly SinkStatus _status;
    readonly CancellationTokenSource _stop = new();
    readonly Task _deviceCheck;

    volatile bool _sendOk;
    volatile int _device = Unknown;   // ARP result for the target
    const int Unknown = 0, Present = 1, Missing = 2;

    public string Name => "UDP";

    public UdpSink(UdpConfig config, Action<string> log, Action<string, StatusKind?, string> report)
    {
        _config = config.Clone();
        _log = log;
        _status = new SinkStatus(Name, log, report);
        _deviceCheck = Task.Run(DeviceCheckLoopAsync);
    }

    // Never throws: a failed send is reported on the card and logged once.
    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        try
        {
            await _client.SendAsync(MetricsJson.SerializeToUtf8Bytes(metrics), _config.Host, _config.Port, ct);
            if (_status.Failing) _log($"UDP sending to {_config.Host}:{_config.Port} again.");
            _sendOk = true;
            ShowState();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _sendOk = false;
            _status.Fail(ex, _config.Host, "send");
        }
    }

    // Card text after a successful send: red while ARP found no device.
    void ShowState()
    {
        if (!_sendOk) return;   // the send failure stays on the card
        if (_device == Missing)
            _status.Error(string.Format(L.T.OutNoDevice, _config.Host));
        else
            _status.Ok(string.Format(L.T.OutSendingTo, _config.Host, _config.Port));
    }

    async Task DeviceCheckLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int result = CheckDevice();
                if (result != _device)
                {
                    if (result == Missing) _log($"UDP: no device answers for {_config.Host} on the local network.");
                    else if (_device == Missing) _log($"UDP: device at {_config.Host} found.");
                    _device = result;
                    ShowState();
                }
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Present/Missing for an IPv4 target in a local subnet, Unknown otherwise
    // (other subnets, broadcast addresses, names that do not resolve).
    int CheckDevice()
    {
        try
        {
            var target = Dns.GetHostAddresses(_config.Host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (target == null || !InLocalSubnet(target)) return Unknown;
            var mac = new byte[6];
            int len = mac.Length;
            uint dest = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
            return SendARP(dest, 0, mac, ref len) == 0 ? Present : Missing;
        }
        catch { return Unknown; }
    }

    // True for a host address in the subnet of an active IPv4 interface;
    // false for the subnet's network and broadcast addresses.
    static bool InLocalSubnet(IPAddress target)
    {
        uint t = ToUInt(target);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                uint mask = ToUInt(u.IPv4Mask);
                if (mask == 0 || (t & mask) != (ToUInt(u.Address) & mask)) continue;
                return (t & ~mask) != 0 && (t | mask) != uint.MaxValue;
            }
        }
        return false;
    }

    static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    // Asks the local network who owns the address; 0 when a device answered.
    [DllImport("iphlpapi.dll")]
    static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int macLen);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _deviceCheck; } catch { }
        _stop.Dispose();
        _client.Dispose();
        _status.Clear();
    }
}
