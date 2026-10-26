using System.Text.RegularExpressions;

// Rules for the Outputs fields and the COM port helpers.
static class InputRules
{
    // Host field rule. Empty is allowed here; the card shows "set a host".
    // - Starts like an IPv4 address (digits-only first part): must be a
    //   complete one, so "192.168.2.411" and "192.168.2.255eeee" are rejected.
    // - Contains ':': must be an IPv6 address.
    // - Otherwise a host name: dot-separated parts of letters, digits and
    //   inner hyphens (RFC 1123). Whether it resolves shows up on connecting.
    internal static bool ValidHost(string text)
    {
        var host = text.Trim();
        if (host.Length == 0)
            return true;
        if (host.Contains(':'))
            return System.Net.IPAddress.TryParse(host, out var v6)
                && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var parts = host.Split('.');
        if (parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit))
            return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit))
                && System.Net.IPAddress.TryParse(host, out _);
        return parts.All(p => p.Length is > 0 and <= 63
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && p[0] != '-' && p[^1] != '-');
    }

    internal static bool ValidPort(string text) => int.TryParse(text.Trim(), out var p) && p is >= 1 and <= 65535;
    internal static bool ValidBaud(string text) => int.TryParse(text.Trim(), out var b) && b >= 1;

    // Dropdown entries like "COM9 — Silicon Labs CP210x USB to UART Bridge":
    // friendly names come from WMI (Win32_PnPEntity), matched to the raw port
    // list; ports without a PnP entry stay plain. "COM2" before "COM10".
    // Only enumerates; never opens a port.
    internal static string[] AvailableComPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p.Length).ThenBy(p => p)
            .ToList();

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var device in searcher.Get())
            {
                if (device["Name"] is not string name) continue;   // e.g. "… Bridge (COM9)"
                var match = Regex.Match(name, @"\((COM\d+)\)");
                if (match.Success)
                    names[match.Groups[1].Value] = name[..match.Index].Trim();
            }
        }
        catch { /* WMI unavailable: plain port names still work */ }

        return ports
            .Select(p => names.TryGetValue(p, out var n) && n.Length > 0 ? $"{p} — {n}" : p)
            .ToArray();
    }

    // The combo shows "COM9 — <description>", the config stores only "COM9".
    internal static string ExtractComPort(string text)
    {
        var match = Regex.Match(text.Trim(), @"^COM\d+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : text.Trim().ToUpperInvariant();
    }
}
