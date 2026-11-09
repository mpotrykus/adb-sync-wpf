using AdbSync.Core.Services.Logging;

namespace AdbSync.Core.Tests.Orchestration;

public class RunLogFilterTests
{
    private static readonly DateTime Base = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Local);

    private static string Line(double seconds, string message) =>
        $"[{Base.AddSeconds(seconds):yyyy-MM-dd HH:mm:ss.fff}] [INF] {message}";

    private static string Log(params string[] lines) => string.Join(Environment.NewLine, lines);

    [Fact]
    public void Filter_DeviceOnly_KeepsOnlyThatDevicesLines()
    {
        var log = Log(Line(0, "pulling from 'A'"), Line(1, "pulling from 'B'"), Line(2, "pulled from 'A'"));

        var result = RunLogFilter.Filter(log, "A", null, null);

        Assert.Equal(Log(Line(0, "pulling from 'A'"), Line(2, "pulled from 'A'")), result);
    }

    [Fact]
    public void Filter_TimeWindow_ExcludesOtherPhasesOfSameDevice()
    {
        var log = Log(Line(0, "pulling from 'A'"), Line(5, "merge with 'A' started"), Line(9, "pushing to 'A'"));
        var from = new DateTimeOffset(Base.AddSeconds(4));
        var to = new DateTimeOffset(Base.AddSeconds(6));

        var result = RunLogFilter.Filter(log, "A", from, to);

        Assert.Equal(Line(5, "merge with 'A' started"), result);
    }

    [Fact]
    public void Filter_ContinuationLinesFollowTheirEntry()
    {
        var log = Log(Line(0, "failed on 'A'"), "System.Exception: boom", "   at Foo()", Line(1, "other 'B'"));

        var result = RunLogFilter.Filter(log, "A", null, null);

        Assert.Equal(Log(Line(0, "failed on 'A'"), "System.Exception: boom", "   at Foo()"), result);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmpty() =>
        Assert.Equal("", RunLogFilter.Filter(Log(Line(0, "pulling from 'A'")), "Z", null, null));
}
