using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The saved pipeline cache carries a checksum (<see cref="PipelineCacheFile"/>): the header check says this device
/// made the data, this says the data is what was written. A body damaged behind an intact header must not reach the
/// driver.
/// </summary>
public sealed class PipelineCacheFileTests
{
    private static byte[] Data()
    {
        var data = new byte[PipelineCacheHeader.Length + 300];
        new Random(7).NextBytes(data);
        return data;
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsRead()
    {
        var data = Data();

        PipelineCacheFile.TryUnwrap(PipelineCacheFile.Wrap(data), out var read).ShouldBeTrue();

        read.ToArray().ShouldBe(data);
    }

    [Fact]
    public void ABodyDamagedBehindAnIntactHeaderIsRefused()
    {
        var file = PipelineCacheFile.Wrap(Data());
        // Past the envelope and past the cache's own 32-byte header: the part the header check never reads.
        file[PipelineCacheFile.EnvelopeLength + PipelineCacheHeader.Length + 100] ^= 0x01;

        PipelineCacheFile.TryUnwrap(file, out _).ShouldBeFalse();
    }

    [Fact]
    public void ADamagedChecksumIsRefused()
    {
        var file = PipelineCacheFile.Wrap(Data());
        file[8] ^= 0x80;

        PipelineCacheFile.TryUnwrap(file, out _).ShouldBeFalse();
    }

    [Fact]
    public void ATruncatedFileIsRefused()
    {
        var file = PipelineCacheFile.Wrap(Data());

        PipelineCacheFile.TryUnwrap(file.AsSpan(0, file.Length - 1).ToArray(), out _).ShouldBeFalse();
        PipelineCacheFile.TryUnwrap(file.AsSpan(0, 10).ToArray(), out _).ShouldBeFalse();
        PipelineCacheFile.TryUnwrap([], out _).ShouldBeFalse();
    }

    [Fact]
    public void AFileFromBeforeTheChecksumIsRefused()
    {
        // 7.117 and 7.118 wrote the driver's data bare: it starts with the Vulkan header, not the magic number.
        PipelineCacheFile.TryUnwrap(Data(), out _).ShouldBeFalse();
    }

    [Fact]
    public void AnotherFormatVersionIsRefused()
    {
        var file = PipelineCacheFile.Wrap(Data());
        file[4] = 2;

        PipelineCacheFile.TryUnwrap(file, out _).ShouldBeFalse();
    }
}
