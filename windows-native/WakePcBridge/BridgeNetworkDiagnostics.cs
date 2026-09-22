using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WakePcBridge;

public sealed record BridgeNetworkAddress(
    string InterfaceName,
    string Address,
    int PrefixLength,
    IReadOnlyList<string> Gateways,
    bool DhcpEnabled,
    string MacAddress,
    bool IsWired);

public static class BridgeNetworkDiagnostics
{
    public static IReadOnlyList<BridgeNetworkAddress> GetActiveIpv4()
    {
        var addresses = new List<BridgeNetworkAddress>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                string[] gateways = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(4)
                    .ToArray();
                bool dhcpEnabled = properties.GetIPv4Properties()?.IsDhcpEnabled ?? false;
                string macAddress = FormatMacAddress(adapter.GetPhysicalAddress());
                bool isWired = IsPhysicalWiredAdapter(adapter);
                foreach (UnicastIPAddressInformation unicast in
                         properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        System.Net.IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    addresses.Add(new BridgeNetworkAddress(
                        adapter.Name,
                        unicast.Address.ToString(),
                        unicast.PrefixLength,
                        gateways,
                        dhcpEnabled,
                        macAddress,
                        isWired));
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return addresses
            .OrderByDescending(item => item.IsWired)
            .ThenByDescending(item => item.Gateways.Count > 0)
            .ThenBy(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address, StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 6
            ? string.Join(":", bytes.Select(value => value.ToString("X2")))
            : string.Empty;
    }

    private static bool IsPhysicalWiredAdapter(NetworkInterface adapter)
    {
        bool ethernetType = adapter.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet;
        if (!ethernetType)
        {
            return false;
        }

        string identity = $"{adapter.Name} {adapter.Description}";
        string[] virtualMarkers =
        [
            "virtual",
            "vethernet",
            "hyper-v",
            "vmware",
            "virtualbox",
            "docker",
            "wsl",
            "vpn",
            "tap-windows",
            "kernel debug",
        ];
        return !virtualMarkers.Any(marker =>
            identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatForDisplay(
        IReadOnlyList<BridgeNetworkAddress> addresses,
        int port)
    {
        if (addresses.Count == 0)
        {
            return "No active non-loopback IPv4 address was found.\n\n" +
                "Check the Windows network connection, then try again.";
        }

        var output = new StringBuilder(
            "Use the appropriate PC address in Decky → Wake PC → Settings.\n\n");
        for (int index = 0; index < addresses.Count; index++)
        {
            BridgeNetworkAddress address = addresses[index];
            if (index > 0)
            {
                output.AppendLine();
            }

            output.AppendLine(address.InterfaceName);
            output.AppendLine($"PC address: {address.Address}/{address.PrefixLength}");
            output.AppendLine(
                "Default gateway: " +
                (address.Gateways.Count == 0
                    ? "None"
                    : string.Join(", ", address.Gateways)));
            output.AppendLine($"Sleep Bridge target: {address.Address}:{port}");
            output.AppendLine($"DHCP: {(address.DhcpEnabled ? "Enabled" : "Disabled")}");
        }

        output.Append(
            "\nIf the router was reset, update the IP address in Decky and " +
            "recreate the router's DHCP reservation.");
        return output.ToString();
    }
}
