using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Logging;

namespace LamSims.Core.Tests;

public class LogPrimitivesTests
{
    [Fact]
    public void A_factory_sets_the_severity_it_names()
    {
        Assert.Equal(LogSeverity.Info, LogLine.Info("x").Severity);
        Assert.Equal(LogSeverity.Warning, LogLine.Warning("x").Severity);
        Assert.Equal(LogSeverity.Error, LogLine.Error("x").Severity);
    }

    /// <summary>
    /// The code is what lets the view group and filter a line, and it is optional because
    /// catalog and application lines belong to no pack.
    /// </summary>
    [Fact]
    public void A_line_carries_its_code_or_none()
    {
        Assert.Equal("EP01", LogLine.Info("x", "EP01").Code);
        Assert.Null(LogLine.Info("x").Code);
    }

    /// <summary>
    /// Every constructor defaults to this, so it is on the hot path of a segmented download with
    /// nothing attached. It must be free and it must never throw.
    /// </summary>
    [Fact]
    public async Task The_null_sink_is_a_shared_instance_that_swallows_from_every_thread()
    {
        Assert.Same(NullLogSink.Instance, NullLogSink.Instance);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++) NullLogSink.Instance.Write(LogLine.Info($"{i}-{n}"));
        })));
    }

    /// <summary>The double the Core tests assert through; a defect here fakes every later task.</summary>
    [Fact]
    public async Task The_recording_sink_keeps_every_line_written_concurrently()
    {
        var sink = new RecordingLogSink();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 100; n++) sink.Write(LogLine.Info($"{i}-{n}"));
        })));

        Assert.Equal(1600, sink.Lines.Count);
        Assert.Equal(1600, sink.Lines.Select(l => l.Text).Distinct().Count());
    }

    [Fact]
    public void The_recording_sink_matches_on_a_fragment()
    {
        var sink = new RecordingLogSink();
        sink.Write(LogLine.Info("Fetching https://host.example.invalid/EP01.zip", "EP01"));

        Assert.True(sink.Logged("Fetching https://host.example.invalid"));
        Assert.False(sink.Logged("Installing"));
    }
}
