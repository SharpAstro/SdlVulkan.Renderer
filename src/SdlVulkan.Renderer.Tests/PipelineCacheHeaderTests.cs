using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Saved pipeline-cache data reaches the driver only when its header says this device made it
/// (<see cref="PipelineCacheHeader"/>): the driver is required to ignore data that is not its own, but a cache file
/// left by another GPU, an older driver or a torn write is not something to trust a driver with.
/// </summary>
public sealed class PipelineCacheHeaderTests
{
    private const uint Vendor = 0x5143; // Qualcomm
    private const uint Device = 0x36334330;
    private static readonly byte[] Uuid = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static byte[] Header(uint length = PipelineCacheHeader.Length, uint version = 1, uint vendor = Vendor,
        uint device = Device, byte[]? uuid = null, int payload = 64)
    {
        var data = new byte[PipelineCacheHeader.Length + payload];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), length);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), version);
        BitConverter.TryWriteBytes(data.AsSpan(8, 4), vendor);
        BitConverter.TryWriteBytes(data.AsSpan(12, 4), device);
        (uuid ?? Uuid).CopyTo(data, 16);
        return data;
    }

    [Fact]
    public void ThisDevicesDataMatches()
        => PipelineCacheHeader.Matches(Header(), Vendor, Device, Uuid).ShouldBeTrue();

    [Fact]
    public void AnotherVendorsDoesNot()
        => PipelineCacheHeader.Matches(Header(vendor: 0x10DE), Vendor, Device, Uuid).ShouldBeFalse();

    [Fact]
    public void AnotherDevicesDoesNot()
        => PipelineCacheHeader.Matches(Header(device: Device + 1), Vendor, Device, Uuid).ShouldBeFalse();

    [Fact]
    public void AnotherDriverBuildsDoesNot()
    {
        // The UUID is what changes with the driver build, whatever its version number says.
        var other = (byte[])Uuid.Clone();
        other[15] ^= 0xFF;
        PipelineCacheHeader.Matches(Header(uuid: other), Vendor, Device, Uuid).ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownHeaderVersionDoesNot()
        => PipelineCacheHeader.Matches(Header(version: 2), Vendor, Device, Uuid).ShouldBeFalse();

    [Fact]
    public void ATornFileDoesNot()
    {
        PipelineCacheHeader.Matches(Header().AsSpan(0, 20), Vendor, Device, Uuid).ShouldBeFalse();
        PipelineCacheHeader.Matches([], Vendor, Device, Uuid).ShouldBeFalse();
    }

    [Fact]
    public void AHeaderClaimingMoreThanTheFileHoldsDoesNot()
        => PipelineCacheHeader.Matches(Header(length: 4096, payload: 0), Vendor, Device, Uuid).ShouldBeFalse();

    [Fact]
    public void AHeaderShorterThanVersionOneDoesNot()
        => PipelineCacheHeader.Matches(Header(length: 16), Vendor, Device, Uuid).ShouldBeFalse();
}
