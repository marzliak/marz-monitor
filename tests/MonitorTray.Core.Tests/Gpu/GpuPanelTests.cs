namespace MonitorTray.Core.Tests.Gpu;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MonitorTray.Gpu;
using Xunit;

public sealed class GpuPanelTests
{
    private sealed class Api : IGpuApi
    {
        internal GpuIdentity Identity = new("GPU-fixture", "NVIDIA Test", 2);
        internal uint? Temp = 42;
        internal GpuTelemetry Telemetry = new() { MinFanPercent = 30, MaxFanPercent = 100, SlowdownC = 95, ClockEventReasons = 0 };
        internal FanReading[] Fans = { new(0, 30, 30, 0), new(1, 31, 30, 0) };
        internal readonly List<(int Fan,uint? Speed)> Writes = new();
        internal bool FailRead, FailLower, FailAuto, FailHigh;
        internal GpuFanController? Controller;
        internal bool? OwnershipAtFirstWrite;
        public IReadOnlyList<GpuIdentity> Discover() => new[] { Identity };
        public GpuReading Read(string uuid) => FailRead ? throw new InvalidOperationException("read failed")
            : new(Identity,DateTimeOffset.UtcNow,Temp,Fans.ToArray()) { Telemetry = Telemetry };
        public void SetSpeed(string uuid,int fan,uint percent)
        {
            OwnershipAtFirstWrite ??= Controller?.IsHoldingManual;
            Writes.Add((fan,percent));
            if (FailLower && percent < 100 && fan==1 || FailHigh && percent==100 && fan==0) throw new InvalidOperationException("write failed");
            Fans[fan] = Fans[fan] with { TargetPercent=percent, Policy=1 };
        }
        public void RestoreAutomatic(string uuid,int fan)
        {
            Writes.Add((fan,null));
            if (FailAuto && fan==0) throw new InvalidOperationException("auto failed");
            Fans[fan] = Fans[fan] with { Policy=0 };
        }
        public void Dispose() { }
        internal GpuFanController New() => Controller = new GpuFanController(this,Identity);
    }
    [Theory] [InlineData(30u)] [InlineData(50u)] [InlineData(75u)] [InlineData(100u)]
    public void AcceptedPresetWritesAndVerifiesEveryFan(uint target)
    {
        var a=new Api();var c=a.New();c.SetSpeed(target);
        Assert.Equal(target,c.RequestedPercent);Assert.True(c.IsHoldingManual);Assert.True(a.OwnershipAtFirstWrite);
        Assert.All(a.Fans,f=>Assert.Equal(target,f.TargetPercent));
        c.RestoreAutomatic();Assert.False(c.IsHoldingManual);Assert.Null(c.RequestedPercent);
    }
    [Theory] [InlineData(0u)] [InlineData(10u)] [InlineData(15u)] [InlineData(25u)] [InlineData(101u)]
    public void OutOfRangeRejectedBeforeAnyWrite(uint target)
    {
        var a=new Api();var c=a.New();Assert.ThrowsAny<Exception>(()=>c.SetSpeed(target));Assert.Empty(a.Writes);Assert.False(c.IsHoldingManual);
    }
    [Fact] public void MissingRangeBlocksReductionButNotEmergencyFull()
    {
        var a=new Api{Telemetry=new()};var c=a.New();Assert.Throws<InvalidOperationException>(()=>c.SetSpeed(50));Assert.Empty(a.Writes);c.SetFullSpeed();Assert.Equal(100u,c.RequestedPercent);
    }
    [Fact] public void UnknownTemperatureRefusesReduction()
    {
        var a=new Api{Temp=null};var c=a.New();Assert.Throws<InvalidOperationException>(()=>c.SetSpeed(50));Assert.Empty(a.Writes);
    }
    [Fact] public void UnreadablePolicyRefusesReduction()
    {
        var a=new Api();a.Fans[0]=a.Fans[0] with {Policy=null};var c=a.New();Assert.Throws<InvalidOperationException>(()=>c.SetSpeed(50));Assert.Empty(a.Writes);
    }
    [Fact] public void LowPresetsRequireDriverRangeAndColdCore()
    {
        var a=new Api{Telemetry=new(){MinFanPercent=0,MaxFanPercent=100,ClockEventReasons=0},Temp=49};var c=a.New();c.SetSpeed(10);Assert.Equal(10u,c.RequestedPercent);
        a.Temp=50;c.Maintain();Assert.Equal(100u,c.RequestedPercent);Assert.NotNull(c.SafetyNotice);
    }
    [Theory] [InlineData(80u)] [InlineData(95u)]
    public void HotCorePromotesFullAndNeverSilentlyReturns(uint hot)
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.Temp=hot;c.Maintain();Assert.Equal(100u,c.RequestedPercent);
        a.Temp=42;c.Maintain();Assert.Equal(100u,c.RequestedPercent);Assert.NotNull(c.SafetyNotice);
    }
    [Fact] public void DriverLowerThermalLimitIsRespected()
    {
        var a=new Api{Telemetry=new(){MinFanPercent=30,MaxFanPercent=100,SlowdownC=70},Temp=68};var c=a.New();Assert.Throws<InvalidOperationException>(()=>c.SetSpeed(50));Assert.Empty(a.Writes);
    }
    [Fact] public void VramThermalAndDriverEventPromoteFull()
    {
        foreach(bool eventOnly in new[]{true,false})
        {
            var a=new Api();var c=a.New();c.SetSpeed(75);
            a.Telemetry=a.Telemetry with {MemoryTemperatureC=eventOnly?null:95,ClockEventReasons=eventOnly?32UL:0};
            c.Maintain();Assert.Equal(100u,c.RequestedPercent);Assert.NotNull(c.SafetyNotice);
        }
    }
    [Fact] public void ReadFailureStillSendsEveryFullTarget()
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.FailRead=true;a.Writes.Clear();
        Assert.Throws<AggregateException>(c.Maintain);Assert.Equal(100u,c.RequestedPercent);Assert.Equal(new[]{(0,(uint?)100),(1,(uint?)100)},a.Writes);
    }
    [Fact] public void PartialReductionFailsHighWithoutAutoRollback()
    {
        var a=new Api{FailLower=true};var c=a.New();Assert.Throws<AggregateException>(()=>c.SetSpeed(50));
        Assert.Equal(100u,c.RequestedPercent);Assert.True(c.IsHoldingManual);Assert.All(a.Fans,f=>Assert.Equal(100u,f.TargetPercent));Assert.DoesNotContain(a.Writes,w=>w.Speed==null);
    }
    [Fact] public void PartialFailHighKeepsGoodSiblingAtFull()
    {
        var a=new Api{FailLower=true,FailHigh=true};var c=a.New();Assert.Throws<AggregateException>(()=>c.SetSpeed(50));Assert.Equal(100u,a.Fans[1].TargetPercent);Assert.True(c.IsHoldingManual);
    }
    [Fact] public void FailedAutomaticNeverResumesOldLowTarget()
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.FailAuto=true;Assert.Throws<AggregateException>(c.RestoreAutomatic);
        Assert.Equal(100u,c.RequestedPercent);c.Maintain();Assert.All(a.Fans,f=>Assert.Equal(100u,f.TargetPercent));
    }
    [Fact] public void FailedAutomaticImmediatelyAttemptsFullBeforeReturningError()
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.Writes.Clear();a.FailAuto=true;
        Assert.Throws<AggregateException>(c.RestoreAutomatic);
        Assert.Equal(new[]{(0,(uint?)null),(1,(uint?)null),(0,(uint?)100),(1,(uint?)100)},a.Writes);
        Assert.All(a.Fans,f=>Assert.Equal(100u,f.TargetPercent));Assert.Contains("confirmados",c.SafetyNotice!);
    }
    [Fact] public void FailedAutomaticAndFailHighNeverClaimsConfirmedProtection()
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.FailAuto=a.FailHigh=true;
        Assert.Throws<AggregateException>(c.RestoreAutomatic);Assert.Contains("nao confirmados",c.SafetyNotice!);
        Assert.True(c.IsHoldingManual);Assert.Equal(100u,a.Fans[1].TargetPercent);
    }
    [Theory] [InlineData(null)] [InlineData(8UL)]
    public void MissingEventsOrHardwareSlowdownRefusesReduction(ulong? bits)
    {
        var a=new Api();a.Telemetry=a.Telemetry with {ClockEventReasons=bits};var c=a.New();
        Assert.Throws<InvalidOperationException>(()=>c.SetSpeed(50));Assert.Empty(a.Writes);
    }
    [Fact] public void UnknownDriverEventsDuringManualPromoteFull()
    {
        var a=new Api();var c=a.New();c.SetSpeed(50);a.Telemetry=a.Telemetry with {ClockEventReasons=null};
        c.Maintain();Assert.Equal(100u,c.RequestedPercent);Assert.NotNull(c.SafetyNotice);
    }
    [Fact] public void DriverDropsPresetItIsReasserted()
    {
        var a=new Api();var c=a.New();c.SetSpeed(75);a.Fans[0]=new(0,0,0,0);c.Maintain();Assert.Equal(75u,a.Fans[0].TargetPercent);
    }
    [Fact] public void NvmlWindowsAbiSizesAndOffsetsAreExact()
    {
        Assert.Equal(8,Marshal.SizeOf<NvmlGpuApi.Utilization>());Assert.Equal(24,Marshal.SizeOf<NvmlGpuApi.MemoryInfo>());
        Assert.Equal(12,Marshal.SizeOf<NvmlGpuApi.FanSpeedInfo>());Assert.Equal(40,Marshal.SizeOf<NvmlGpuApi.FieldValue>());
        Assert.Equal(32,Marshal.OffsetOf<NvmlGpuApi.FieldValue>(nameof(NvmlGpuApi.FieldValue.Value)).ToInt32());
        Assert.Equal(24,Marshal.OffsetOf<NvmlGpuApi.FieldValue>(nameof(NvmlGpuApi.FieldValue.ValueType)).ToInt32());
    }
    [Theory] [InlineData(15)] [InlineData(17)] [InlineData(9)] [InlineData(16)] [InlineData(18)] [InlineData(999)]
    public void DeviceLossOrBlockedOperatingSystemAreNeverOptional(int code) => Assert.False(NvmlGpuApi.IsOptionalMetricError(code));
    [Theory] [InlineData(3)] [InlineData(4)] [InlineData(13)] [InlineData(21)] [InlineData(25)]
    public void MissingOptionalTelemetryRemainsUnknown(int code) => Assert.True(NvmlGpuApi.IsOptionalMetricError(code));
    [Fact] public void MemoryFieldRequiresSuccessTypeTimestampAndPlausibleValue()
    {
        var now=DateTimeOffset.UtcNow;var field=new NvmlGpuApi.FieldValue{FieldId=82,Timestamp=now.ToUnixTimeMilliseconds()*1000,ValueType=5,Return=0,Value=new(){Int=70}};
        Assert.Equal(70u,NvmlGpuApi.DecodeMemoryTemperature(field,now));
        field.Return=3;Assert.Null(NvmlGpuApi.DecodeMemoryTemperature(field,now));field.Return=0;
        field.ValueType=99;Assert.Null(NvmlGpuApi.DecodeMemoryTemperature(field,now));field.ValueType=5;
        field.Timestamp-=10000000;Assert.Null(NvmlGpuApi.DecodeMemoryTemperature(field,now));
    }
    [Fact] public void MissingClockEventsAreNotReportedAsNoEvents()
    {
        Assert.Contains("N/D",new GpuTelemetry().ClockEvents);Assert.Contains("Nenhum",new GpuTelemetry{ClockEventReasons=0}.ClockEvents);
        Assert.Contains("termico",new GpuTelemetry{ClockEventReasons=32}.ClockEvents);
    }
}
