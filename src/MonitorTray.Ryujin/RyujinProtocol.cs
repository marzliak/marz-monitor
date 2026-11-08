namespace MonitorTray.Ryujin;

/// <summary>Leituras decodificadas do ROG RYUJIN II (firmware S750). Protocol: https://github.com/marzliak/asus-rog-protocol-notes.</summary>
public sealed record RyujinPumpSensors(double WaterTempC, int PumpRpm, int EmbeddedFanRpm);
public sealed record RyujinFanReading(int[] Rpm, int DutyRaw)
{
    public int DutyPercent => DutyRaw * 100 / 255;
}

/// <summary>
/// Quadro HID de 65 bytes: b0 = 0xEC (Report ID), b1 = comando, b2 = status (0 = ok), dados a partir de b3.
/// Só os comandos de LEITURA estão aqui; escrita fica fora até haver GO (ver <see cref="IsReadCommand"/>).
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

    /// <summary>Allow-list: só estes comandos podem ser enviados pelo transporte de leitura.</summary>
    public static bool IsReadCommand(byte cmd) =>
        cmd is CmdFirmware or CmdPumpSensors or CmdFanRpm or CmdFanDuty or CmdDisplayStatus or CmdPumpConfig;

    // ---- LCD (escrita só de TEXTO e LAYOUT; validado ao vivo em 2026-11-02, docs §15-17)
    public const byte CmdLcdLayout = 0x52;
    public const byte CmdLcdText = 0x53;

    /// <summary>Allow-list de escrita do LCD. Nada de brilho (0x5C), mídia/flash (0x51, 0x70-0x73) ou fans (0x1A, 0x21).</summary>
    public static bool IsDisplayCommand(byte cmd) => cmd is CmdLcdLayout or CmdLcdText;

    public const int LcdNameMax = 11, LcdValueOffset = 39, LcdValueMax = 12;

    /// <summary>0x53: b2 = slot (0..3), b3.. = nome ASCII (até 11), b39.. = valor UTF-8 com a unidade num caractere.</summary>
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
    /// 0x52 no tema escuro, byte a byte como o Armoury Crate envia: `EC 52 02 n-1 g g 00 00 00` + FF em b9..b25.
    /// b3 = quantidade de informações - 1; b4 = b5 = grupo do sensor do slot 0 (numeração do protocolo).
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

    // ---- Fans (validado ao vivo em 2026-11-02, docs §18-19)
    public const byte CmdPumpSet = 0x1A;
    public const byte CmdRadiatorSet = 0x21;
    public static bool IsFanCommand(byte cmd) => cmd is CmdPumpSet or CmdRadiatorSet;

    /// <summary>0x1A: b3 = % da bomba, b4 = % do fan embutido (0..100).</summary>
    public static byte[] BuildPump(int pumpPercent, int embeddedPercent)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdPumpSet;
        f[3] = (byte)Math.Clamp(pumpPercent, 0, 100); f[4] = (byte)Math.Clamp(embeddedPercent, 0, 100);
        return f;
    }

    /// <summary>0x21: b4 = duty do radiador 0..255.</summary>
    public static byte[] BuildRadiator(byte duty)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId; f[1] = CmdRadiatorSet; f[4] = duty;
        return f;
    }

    static int U16(byte[] f, int i) => f[i] | (f[i + 1] << 8);

    public static byte[] BuildRequest(byte cmd)
    {
        if (!IsReadCommand(cmd)) throw new ArgumentException($"comando 0x{cmd:X2} não é de leitura", nameof(cmd));
        var f = new byte[FrameLength];
        f[0] = ReportId;
        f[1] = cmd;
        return f;
    }

    /// <summary>A resposta ecoa o comando sem o bit 7 (0xA0 -> 0x20).</summary>
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

    /// <summary>0x9A: b4 = % da bomba, b5 = % do fan embutido (eco do último 0x1A).</summary>
    public static (int PumpPercent, int EmbeddedPercent) ParsePumpConfig(byte[] f) => (f[4], f[5]);
}

/// <summary>Grupo do sensor do slot 0 no 0x52 (numeração do PROTOCOLO; Frequency/Water Flow são invertidos em relação ao SDK).</summary>
public enum LcdGroup : byte { Voltage = 0, FanSpeed = 1, Temperature = 2, Frequency = 3, WaterFlow = 4 }

/// <summary>Texto aceito pelo LCD: nome em ASCII (sem acento), valor em UTF-8 com um caractere de unidade.</summary>
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
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // não corta um caractere no meio
        return b[..n];
    }
}
