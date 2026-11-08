namespace MonitorTray.Ryujin;

/// <summary>Decoded readings from the ROG RYUJIN II (firmware S750). Protocol: https://github.com/marzliak/asus-rog-protocol-notes</summary>
public sealed record RyujinPumpSensors(double WaterTempC, int PumpRpm, int EmbeddedFanRpm);
public sealed record RyujinFanReading(int[] Rpm, int DutyRaw)
{
    public int DutyPercent => DutyRaw * 100 / 255;
}

/// <summary>
/// 65-byte HID frame: b0 = 0xEC (Report ID), b1 = command, b2 = status (0 = ok), data from b3 on.
/// Only READ commands are here; writes stay out until there is a GO (see <see cref="IsReadCommand"/>).
/// </summary>
public static class RyujinProtocol
{
    public const int FrameLength = 65;
    public const byte ReportId = 0xEC;

    public const byte CmdFirmware = 0x82;
    public const byte CmdPumpSensors = 0x99;
    public const byte CmdFanRpm = 0xA0;
    public const byte CmdFanDuty = 0xA1;
    public const byte CmdDisplayStatus = 0xDC;
    public const byte CmdPumpConfig = 0x9A;

    /// <summary>Allow-list: only these commands may be sent through the read transport.</summary>
    public static bool IsReadCommand(byte cmd) =>
        cmd is CmdFirmware or CmdPumpSensors or CmdFanRpm or CmdFanDuty or CmdDisplayStatus or CmdPumpConfig;

    // ---- LCD (writes only TEXT and LAYOUT; validated live on 2026-11-02, docs §15-17)
    public const byte CmdLcdLayout = 0x52;
    public const byte CmdLcdText = 0x53;

    /// <summary>LCD write allow-list. No brightness (0x5C), media/flash (0x51, 0x70-0x73) or fans (0x1A, 0x21).</summary>
    public static bool IsDisplayCommand(byte cmd) => cmd is CmdLcdLayout or CmdLcdText;

    public const int LcdNameMax = 11, LcdValueOffset = 39, LcdValueMax = 12;

    /// <summary>0x53: b2 = slot (0..3), b3.. = ASCII name (up to 11), b39.. = UTF-8 value with the unit as one character.</summary>
    public static byte[] BuildText(int slot, string name, string value)
    {
        if (slot is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdLcdText; f[2] = (byte)slot;
        var n = System.Text.Encoding.ASCII.GetBytes(LcdText.Ascii(name, LcdNameMax));
        Array.Copy(n, 0, f, 3, n.Length);
        var v = LcdText.Utf8Cut(value, LcdValueMax);
        Array.Copy(v, 0, f, LcdValueOffset, v.Length);
        return f;
    }

    /// <summary>
    /// 0x52 in the dark theme, byte for byte as Armoury Crate sends it: `EC 52 02 n-1 g g 00 00 00` + FF in b9..b25.
    /// b3 = number of items - 1; b4 = b5 = sensor group of slot 0 (protocol numbering).
    /// </summary>
    public static byte[] BuildLayoutDark(int count, LcdGroup slot0Group)
    {
        if (count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count));
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdLcdLayout; f[2] = 2; f[3] = (byte)(count - 1);
        f[4] = f[5] = (byte)slot0Group;
        for (int i = 9; i <= 25; i++) f[i] = 0xFF;
        return f;
    }

    // ---- Fans (validated live on 2026-11-02, docs §18-19)
    public const byte CmdPumpSet = 0x1A;
    public const byte CmdRadiatorSet = 0x21;
    public static bool IsFanCommand(byte cmd) => cmd is CmdPumpSet or CmdRadiatorSet;

    /// <summary>0x1A: b3 = pump %, b4 = embedded fan % (0..100).</summary>
    public static byte[] BuildPump(int pumpPercent, int embeddedPercent)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdPumpSet;
        f[3] = (byte)Math.Clamp(pumpPercent, 0, 100); f[4] = (byte)Math.Clamp(embeddedPercent, 0, 100);
        return f;
    }

    /// <summary>0x21: b4 = radiator duty 0..255.</summary>
    public static byte[] BuildRadiator(byte duty)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdRadiatorSet; f[4] = duty;
        return f;
    }

    static int U16(byte[] f, int i) => f[i] | (f[i + 1] << 8);

    public static byte[] BuildRequest(byte cmd)
    {
        if (!IsReadCommand(cmd)) throw new ArgumentException($"command 0x{cmd:X2} is not a read command", nameof(cmd));
        var f = new byte[FrameLength];
        f[0] = ReportId;
        f[1] = cmd;
        return f;
    }

    /// <summary>The response echoes the command without bit 7 (0xA0 -> 0x20).</summary>
    public static bool IsResponseTo(byte[] frame, byte cmd) =>
        frame.Length >= FrameLength && frame[0] == ReportId && frame[1] == (cmd & 0x7F) && frame[2] == 0;

    public static string ParseFirmware(byte[] f)
    {
        int end = 3;
        while (end < f.Length && f[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(f, 3, end - 3);
    }

    public static RyujinPumpSensors ParsePumpSensors(byte[] f) =>
        new(f[3] + f[4] / 10.0, U16(f, 5), U16(f, 7));

    public static RyujinFanReading ParseFans(byte[] rpmFrame, byte[] dutyFrame) =>
        new([U16(rpmFrame, 5), U16(rpmFrame, 7), U16(rpmFrame, 9)], dutyFrame[4]);

    /// <summary>0x9A: b4 = pump %, b5 = embedded fan % (echo of the last 0x1A).</summary>
    public static (int PumpPercent, int EmbeddedPercent) ParsePumpConfig(byte[] f) => (f[4], f[5]);
}

/// <summary>Sensor group of slot 0 in 0x52 (PROTOCOL numbering; Frequency/Water Flow are swapped relative to the SDK).</summary>
public enum LcdGroup : byte { Voltage = 0, FanSpeed = 1, Temperature = 2, Frequency = 3, WaterFlow = 4 }

/// <summary>Text accepted by the LCD: ASCII name (no accents), UTF-8 value with a single unit character.</summary>
public static class LcdText
{
    public const string Celsius = "℃", GHz = "㎓", Volt = "↊", Rpm = "↌", Flow = "↍";

    public static string Ascii(string s, int max)
    {
        var d = (s ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
            if (sb.Length == max) break;
        }
        return sb.ToString();
    }

    public static byte[] Utf8Cut(string s, int maxBytes)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length <= maxBytes) return b;
        int n = maxBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // never cut a character in half
        return b[..n];
    }
}
