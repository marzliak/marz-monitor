namespace MonitorTray.Core.Normalize;

public enum TrafficScope { Ignore, Local, Internet }

/// <summary>
/// Classifies an IP address as local network or internet. Local: private (10/8, 172.16/12, 192.168/16), link-local,
/// CGNAT/Tailscale (100.64/10), multicast/broadcast, IPv6 ULA (fc00::/7), link-local (fe80::/10) and multicast.
/// Loopback and "unspecified" are ignored (traffic inside the PC itself).
/// </summary>
public static class NetScope
{
    public static TrafficScope Classify(ReadOnlySpan<byte> a)
    {
        if (a.Length == 16)
        {
            bool mapped = a[..10].IndexOfAnyExcept((byte)0) < 0 && a[10] == 0xFF && a[11] == 0xFF;
            if (mapped) return Classify(a[12..]);
            if (a.IndexOfAnyExcept((byte)0) < 0) return TrafficScope.Ignore;                          // ::
            if (a[..15].IndexOfAnyExcept((byte)0) < 0 && a[15] == 1) return TrafficScope.Ignore;      // ::1
            if ((a[0] & 0xFE) == 0xFC) return TrafficScope.Local;                                     // fc00::/7
            if (a[0] == 0xFE && (a[1] & 0xC0) == 0x80) return TrafficScope.Local;                     // fe80::/10
            if (a[0] == 0xFF) return TrafficScope.Local;                                              // multicast
            return TrafficScope.Internet;
        }
        if (a.Length != 4) return TrafficScope.Ignore;
        byte b0 = a[0], b1 = a[1];
        if (b0 == 127 || (b0 == 0 && b1 == 0 && a[2] == 0 && a[3] == 0)) return TrafficScope.Ignore;
        if (b0 == 10 || (b0 == 172 && (b1 & 0xF0) == 16) || (b0 == 192 && b1 == 168) || (b0 == 169 && b1 == 254)
            || (b0 == 100 && (b1 & 0xC0) == 64) || b0 >= 224) return TrafficScope.Local;
        return TrafficScope.Internet;
    }

    /// <summary>A packet is "internet" if either end is public; ignored if both are loopback.</summary>
    public static TrafficScope Of(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var p = Classify(x); var q = Classify(y);
        if (p == TrafficScope.Internet || q == TrafficScope.Internet) return TrafficScope.Internet;
        if (p == TrafficScope.Local || q == TrafficScope.Local) return TrafficScope.Local;
        return TrafficScope.Ignore;
    }
}
