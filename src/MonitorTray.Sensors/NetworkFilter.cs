using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace MonitorTray.Sensors;

/// <summary>
/// Adaptadores físicos/lógicos reais. O Windows expõe, para cada placa, interfaces de filtro (WFP, QoS)
/// e WAN Miniports com os mesmos contadores; somá-las conta o mesmo tráfego várias vezes.
/// Regra genérica do Windows, não depende de modelo de hardware.
/// </summary>
public static partial class NetworkFilter
{
    [GeneratedRegex(@"-(WFP|QoS|Npcap|VirtualBox NDIS)\b.*-\d{4}$|LightWeight Filter|Packet Scheduler", RegexOptions.IgnoreCase)]
    private static partial Regex FilterName();
    [GeneratedRegex(@"^WAN Miniport|Kernel Debug|Pseudo-Interface|Teredo|6to4|IP-HTTPS", RegexOptions.IgnoreCase)]
    private static partial Regex Virtual();

    public static bool IsFilterOrVirtual(string name, string description) =>
        FilterName().IsMatch(name) || FilterName().IsMatch(description) || Virtual().IsMatch(description);

    public static IEnumerable<NetworkInterface> Real(bool upOnly)
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch (NetworkInformationException) { yield break; }
        var macs = new HashSet<string>();
        foreach (var ni in all)
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.OperationalStatus == OperationalStatus.NotPresent) continue;
            if (upOnly && ni.OperationalStatus != OperationalStatus.Up) continue;
            if (IsFilterOrVirtual(ni.Name, ni.Description)) continue;
            // Mesmo MAC = mesma placa vista por outra camada.
            string mac;
            try { mac = ni.GetPhysicalAddress().ToString(); } catch (Exception) { mac = ""; }
            if (mac.Length > 0 && mac.Trim('0').Length > 0 && !macs.Add(mac)) continue;
            yield return ni;
        }
    }

    public static HashSet<string> RealNames() => Real(false).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
}
