namespace MonitorTray.Core.Tests.Gpu;
using MonitorTray.Gpu;
using Xunit;

public class NvApiThermalTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090", 1, 9)]
    [InlineData("NVIDIA GeForce RTX 3080 Ti", 1, 9)]
    [InlineData("NVIDIA GeForce RTX 4090", 1, 7)]
    [InlineData(null, 1, 9)]
    public void Channel_map_follows_generation(string? name, int hotspot, int memory)
    {
        var map = NvApiThermal.ChannelMap(name);
        Assert.Equal(hotspot, map.Hotspot);
        Assert.Equal(memory, map.Memory);
    }

    [Fact]
    public void Rtx50_has_no_separate_hotspot() => Assert.Equal((null, 2), NvApiThermal.ChannelMap("NVIDIA GeForce RTX 5090"));

    [Fact]
    public void Decodes_fixed_point_from_active_channel() => Assert.Equal(58u, NvApiThermal.Decode(14848, 0x3FF, 9)); // 58.0 C

    [Theory]
    [InlineData(14848, 0x1FF, 9)]   // channel not in mask
    [InlineData(0, 0x3FF, 9)]       // absent value
    [InlineData(-256, 0x3FF, 9)]    // negative
    [InlineData(40000, 0x3FF, 9)]   // > 150 C
    [InlineData(14848, 0x3FF, 32)]  // out of range channel
    public void Rejects_absent_or_implausible_values(int raw, uint mask, int channel) => Assert.Null(NvApiThermal.Decode(raw, mask, channel));
}
