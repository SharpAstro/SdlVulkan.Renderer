using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="ShutdownDrain"/> against a scripted loop, so no SDL and no wall clock is involved: the
/// loop runs a finite list of iterations, and the timeout runs on a <see cref="FakeTimeProvider"/>.
/// </summary>
public class ShutdownDrainTests
{
    /// <summary>One iteration per script entry; each polls <see cref="CheckNeedsRedraw"/> after its action.
    /// Running out of script without a stop fails the test rather than spinning.</summary>
    private sealed class ScriptedLoop(params Action<ScriptedLoop>[] script) : IShutdownDrainLoop
    {
        public Action? OnRender { get; set; } = () => { };
        public Func<bool>? CheckNeedsRedraw { get; set; }
        public Func<bool>? OnQuit { get; set; }
        public int Runs { get; private set; }
        public int Iterations { get; private set; }
        public int Stops { get; private set; }
        private bool _running;

        public void Stop() { Stops++; _running = false; }

        public void Run(CancellationToken ct)
        {
            Runs++;
            _running = true;
            foreach (var step in script)
            {
                if (!_running || ct.IsCancellationRequested)
                {
                    return;
                }
                Iterations++;
                step(this);
                CheckNeedsRedraw?.Invoke();
            }
            if (_running && !ct.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException("The scripted loop ran out of iterations without being stopped or cancelled.");
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void AlreadyCompletedDrainReturnsTrueWithoutRunningTheLoop()
    {
        var loop = new ScriptedLoop();

        ShutdownDrain.PumpUntilComplete(loop, Task.CompletedTask, new FakeTimeProvider(), null, null).ShouldBeTrue();

        loop.Runs.ShouldBe(0);
        loop.CheckNeedsRedraw.ShouldBeNull();
    }

    [Fact]
    public void DrainCompletingMidPumpStopsTheLoopFromCheckNeedsRedraw()
    {
        var tcs = new TaskCompletionSource();
        var loop = new ScriptedLoop(_ => { }, _ => { }, _ => tcs.SetResult(), _ => throw new Xunit.Sdk.XunitException("ran past the stop"));

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, new FakeTimeProvider(), null, null).ShouldBeTrue();

        loop.Stops.ShouldBe(1);
        loop.Iterations.ShouldBe(3);
    }

    [Fact]
    public void FaultedDrainReturnsTrueAndLogsAWarning()
    {
        var tcs = new TaskCompletionSource();
        var boom = new InvalidOperationException("boom");
        var loop = new ScriptedLoop(_ => tcs.SetException(boom));
        var logger = new CapturingLogger();

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, new FakeTimeProvider(), logger, null).ShouldBeTrue();

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldBe(boom);
    }

    [Fact]
    public void TimeoutOnAFakeClockReturnsFalseAndLogs()
    {
        var clock = new FakeTimeProvider();
        var tcs = new TaskCompletionSource();
        var loop = new ScriptedLoop(_ => { }, _ => clock.Advance(TimeSpan.FromSeconds(5)), _ => clock.Advance(TimeSpan.FromSeconds(6)));
        var logger = new CapturingLogger();

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, clock, logger, ShutdownDrain.DefaultTimeout).ShouldBeFalse();

        loop.Stops.ShouldBe(0);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("10");
    }

    [Fact]
    public void ClockShortOfTheTimeoutDoesNotCancel()
    {
        var clock = new FakeTimeProvider();
        var tcs = new TaskCompletionSource();
        var loop = new ScriptedLoop(
            _ => clock.Advance(TimeSpan.FromSeconds(9)),
            _ => tcs.SetResult());

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, clock, null, null).ShouldBeTrue();
        loop.Iterations.ShouldBe(2);
    }

    [Fact]
    public void SecondCloseRequestStopsTheLoopAndReturnsFalse()
    {
        var tcs = new TaskCompletionSource();
        bool? quitAnswer = null;
        var loop = new ScriptedLoop(l => quitAnswer = l.OnQuit!());
        var logger = new CapturingLogger();

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, new FakeTimeProvider(), logger, null).ShouldBeFalse();

        quitAnswer.ShouldBe(true);
        loop.Stops.ShouldBe(1);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("second close");
        entry.Message.ShouldNotContain("within");
    }

    [Fact]
    public void OnRenderIsCleared()
    {
        var tcs = new TaskCompletionSource();
        var loop = new ScriptedLoop(l => l.OnRender.ShouldBeNull(), _ => tcs.SetResult());
        loop.OnRender.ShouldNotBeNull();

        ShutdownDrain.PumpUntilComplete(loop, tcs.Task, new FakeTimeProvider(), null, null).ShouldBeTrue();

        loop.OnRender.ShouldBeNull();
    }
}
