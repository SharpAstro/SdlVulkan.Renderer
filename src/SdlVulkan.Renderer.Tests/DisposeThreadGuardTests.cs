using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

public class DisposeThreadGuardTests
{
    [Fact]
    public void SameThread_IsFine() => DisposeThreadGuard.Check(5, 5, "SdlVulkanWindow").ShouldBeNull();

    [Fact]
    public void NoOwnerRecorded_IsFine() => DisposeThreadGuard.Check(0, 9, "VulkanContext").ShouldBeNull();

    [Fact]
    public void OtherThread_NamesBothThreadsAndTheFix()
    {
        var message = DisposeThreadGuard.Check(5, 9, "VulkanContext");
        message.ShouldNotBeNull();
        message.ShouldContain("VulkanContext");
        message.ShouldContain("thread 9");
        message.ShouldContain("thread 5");
        message.ShouldContain("ShutdownDrain.PumpUntilComplete");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Enforce_SameThread_NeitherThrowsNorLogs()
    {
        var logger = new CapturingLogger();
        DisposeThreadGuard.Enforce(5, 5, "VulkanContext", logger);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Enforce_OtherThread_ThrowsInDebugAndLogsInRelease()
    {
        var logger = new CapturingLogger();
#if DEBUG
        var ex = Should.Throw<InvalidOperationException>(() => DisposeThreadGuard.Enforce(5, 9, "VulkanContext", logger));
        ex.Message.ShouldContain("thread 5");
        logger.Entries.ShouldBeEmpty();
#else
        DisposeThreadGuard.Enforce(5, 9, "VulkanContext", logger);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("thread 9");
        entry.Message.ShouldContain("ShutdownDrain.PumpUntilComplete");
#endif
    }
}
