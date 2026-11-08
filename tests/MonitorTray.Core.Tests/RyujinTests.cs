using MonitorTray.Ryujin;
using Xunit;

namespace MonitorTray.Core.Tests;

public class RyujinTests
{
    static byte[] Frame(string hex)
    {
        var b = new byte[RyujinProtocol.FrameLength];
        var s = Convert.FromHexString(hex.Replace(" ", ""));
        Array.Copy(s, b, s.Length);
        return b;
    }

    // Quadros capturados ao vivo do cooler (leituras, 2026-11-02).
    static readonly byte[] Fw = Frame("ec 02 00 41 55 52 4a 31 2d 53 37 35 30 2d 30 31 30 34");
    static readonly byte[] Pump = Frame("ec 19 00 27 01 50 0a 9e 07");
    static readonly byte[] Rpm = Frame("ec 20 00 00 00 ec 04 ec 04 ec 04");
    static readonly byte[] Duty = Frame("ec 21 00 00 a0");

    [Fact] public void FirmwareString() => Assert.Equal("AURJ1-S750-0104", RyujinProtocol.ParseFirmware(Fw));

    [Fact]
    public void PumpSensors()
    {
        var p = RyujinProtocol.ParsePumpSensors(Pump);
        Assert.Equal(39.1, p.WaterTempC, 3);
        Assert.Equal(2640, p.PumpRpm);
        Assert.Equal(1950, p.EmbeddedFanRpm);
    }

    [Fact]
    public void FansAndDuty()
    {
        var f = RyujinProtocol.ParseFans(Rpm, Duty);
        Assert.Equal(new[] { 1260, 1260, 1260 }, f.Rpm);
        Assert.Equal(62, f.DutyPercent);
    }

    [Fact]
    public void ResponseMatchesCommandWithoutBit7()
    {
        Assert.True(RyujinProtocol.IsResponseTo(Rpm, RyujinProtocol.CmdFanRpm));
        Assert.False(RyujinProtocol.IsResponseTo(Rpm, RyujinProtocol.CmdFanDuty));
    }

    [Theory]
    [InlineData(0x82)] [InlineData(0x99)] [InlineData(0xA0)] [InlineData(0xA1)] [InlineData(0xDC)]
    public void ReadCommandsAllowed(byte cmd) => Assert.Equal(cmd, RyujinProtocol.BuildRequest(cmd)[1]);

    [Theory]
    [InlineData(0x21)] [InlineData(0x1A)] [InlineData(0x53)] [InlineData(0x52)] [InlineData(0x5C)]
    [InlineData(0x70)] [InlineData(0x72)] [InlineData(0x73)] [InlineData(0x11)]
    public void WriteCommandsAreRejected(byte cmd) => Assert.Throws<ArgumentException>(() => RyujinProtocol.BuildRequest(cmd));

    // Valores escritos pelo Armoury Crate com CPU ~48 C (captura): Silent 48 %/0x7A, Standard 51 %/0x82, Turbo 60 %/0x99.
    [Theory]
    [InlineData(RyujinCurves.Preset.Silent, 48, 0x7A)]
    [InlineData(RyujinCurves.Preset.Standard, 51, 0x82)]
    [InlineData(RyujinCurves.Preset.Turbo, 60, 0x99)]
    public void PresetCurveMatchesArmouryCrate(RyujinCurves.Preset preset, int pct, int duty)
    {
        var p = RyujinCurves.Interpolate(RyujinCurves.CurveFor(RyujinCurves.Fan.Radiator, preset), 48);
        Assert.Equal(pct, (int)Math.Round(p));
        Assert.Equal(duty, RyujinCurves.RadiatorDuty(p));
    }

    [Fact]
    public void FullSpeedIs100()
    {
        Assert.Equal(100, RyujinCurves.Interpolate(RyujinCurves.CurveFor(RyujinCurves.Fan.Pump, RyujinCurves.Preset.FullSpeed), 30));
        Assert.Equal(0xFF, RyujinCurves.RadiatorDuty(100));
    }

    static readonly RpmPoint[] PumpTable = [new(0,840),new(10,870),new(20,900),new(30,1140),new(40,1470),new(50,1650),new(60,1890),new(70,2130),new(80,2310),new(90,2520),new(100,2800)];
    static readonly RpmPoint[] RadTable = [new(0,0),new(10,300),new(20,420),new(30,630),new(40,810),new(50,1020),new(60,1140),new(70,1320),new(80,1500),new(90,1680),new(100,1890)];

    // Fixed RPM capturado: bomba 1506 -> 42 %, 2106 -> 69 %; radiador 609 -> 29 % (0x21=73), 1212 -> 64 % (0x21=163).
    [Theory]
    [InlineData(1506, 42)] [InlineData(2106, 69)]
    public void PumpFixedRpmToPercent(int rpm, int pct) => Assert.Equal(pct, (int)Math.Round(RyujinCurves.RpmToPercent(PumpTable, rpm)));

    [Theory]
    [InlineData(609, 29, 73)] [InlineData(1212, 64, 163)]
    public void RadiatorFixedRpm(int rpm, int pct, int duty)
    {
        var p = RyujinCurves.RpmToPercent(RadTable, rpm);
        Assert.Equal(pct, (int)Math.Round(p));
        Assert.Equal(duty, RyujinCurves.RadiatorDuty(p));
    }

    [Fact] public void PercentToRpmRoundTrip() => Assert.Equal(1020, RyujinCurves.PercentToRpm(RadTable, 50), 3);
}

public class RyujinLcdTests
{
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // Quadros reais do Armoury Crate (captured from the vendor software, 2026-11-02): o Marz deve gerar exatamente os mesmos bytes.
    [Fact]
    public void TextFrameMatchesArmouryCrate()
    {
        var ac = "ec530343505520467265712e000000000000000000000000000000000000000000000000000000352e363030e38e93000000000000000000000000000000000000";
        Assert.Equal(ac, Hex(RyujinProtocol.BuildText(3, "CPU Freq.", "5.600" + LcdText.GHz)));
        var ac2 = "ec53004350552054656d702e00000000000000000000000000000000000000000000000000000035352e30e2848300000000000000000000000000000000000000";
        Assert.Equal(ac2, Hex(RyujinProtocol.BuildText(0, "CPU Temp.", "55.0" + LcdText.Celsius)));
    }

    [Fact]
    public void LayoutFrameMatchesArmouryCrate()
    {
        var ac = "ec5202030202000000ffffffffffffffffffffffffffffffffff000000000000000000000000000000000000000000000000000000000000000000000000000000";
        Assert.Equal(ac, Hex(RyujinProtocol.BuildLayoutDark(4, LcdGroup.Temperature)));
    }

    [Theory]
    [InlineData(LcdGroup.Voltage, 0)] [InlineData(LcdGroup.FanSpeed, 1)] [InlineData(LcdGroup.Temperature, 2)]
    [InlineData(LcdGroup.Frequency, 3)] [InlineData(LcdGroup.WaterFlow, 4)]
    public void LayoutGroupBytes(LcdGroup g, int b)
    {
        var f = RyujinProtocol.BuildLayoutDark(1, g);
        Assert.Equal(0, f[3]); Assert.Equal(b, f[4]); Assert.Equal(b, f[5]);
    }

    [Fact]
    public void NameIsAsciiAndCut()
    {
        var f = RyujinProtocol.BuildText(0, "Água da bomba longa", "1");
        Assert.Equal("Agua da bom", System.Text.Encoding.ASCII.GetString(f, 3, 11));
        Assert.Equal(0, f[14]);
    }

    [Fact]
    public void ValueNeverSplitsUtf8()
    {
        var b = LcdText.Utf8Cut("123456789.0" + LcdText.Celsius, 12);
        Assert.Equal("123456789.0", System.Text.Encoding.UTF8.GetString(b));
    }

    [Theory]
    [InlineData(LcdGroup.Temperature, 55.04, "55.0\u2103")]
    [InlineData(LcdGroup.Frequency, 5600.0, "5.600\u3393")]
    [InlineData(LcdGroup.Frequency, 5.6, "5.600\u3393")]
    [InlineData(LcdGroup.FanSpeed, 1263.6, "1264\u218C")]
    [InlineData(LcdGroup.Voltage, 1.4921, "1.492\u218A")]
    public void ValuesLikeArmouryCrate(LcdGroup g, double v, string expected) => Assert.Equal(expected, LcdComposer.Value(g, v));

    [Fact] public void MissingValueIsNA() => Assert.Equal("N/A\u2103", LcdComposer.Value(LcdGroup.Temperature, null));

    [Fact]
    public void OnlyTextAndLayoutAreDisplayCommands()
    {
        Assert.True(RyujinProtocol.IsDisplayCommand(0x52)); Assert.True(RyujinProtocol.IsDisplayCommand(0x53));
        foreach (var c in new byte[] { 0x5C, 0x51, 0x70, 0x72, 0x73, 0x1A, 0x21, 0x11 }) Assert.False(RyujinProtocol.IsDisplayCommand(c));
    }

    [Fact]
    public void ConfigDefaultsOffAndClamps()
    {
        var c = LcdConfig.Parse("{\"version\":1,\"enabled\":true,\"pages\":[{\"durationMs\":100,\"slots\":[{\"sensorId\":\"x\",\"name\":\"Temperatura da água\",\"group\":\"waterFlow\"},{},{},{},{}]}]}");
        Assert.True(c.Enabled);
        Assert.Equal(2000, c.Pages[0].DurationMs);
        Assert.Equal(4, c.Pages[0].Slots.Count);
        Assert.Equal("Temperatura", c.Pages[0].Slots[0].Name);
        Assert.Equal(LcdGroup.WaterFlow, c.Pages[0].Slots[0].Group);
        Assert.False(LcdConfig.Parse("lixo").Enabled);
        var round = LcdConfig.Parse(c.ToJson());
        Assert.Equal(c.Pages[0].Slots[0], round.Pages[0].Slots[0]);
    }

    [Fact] public void PumpConfigParse() => Assert.Equal((51, 60), RyujinProtocol.ParsePumpConfig(Convert.FromHexString("ec1a0000333c" + new string('0', 118))));
}
