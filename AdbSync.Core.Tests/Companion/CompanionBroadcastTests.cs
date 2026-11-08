using AdbSync.Core.Services.Companion;

namespace AdbSync.Core.Tests.Companion;

public class CompanionBroadcastTests
{
    [Fact]
    public void BuildCommand_PhaseEvent()
    {
        var command = CompanionBroadcast.BuildCommand(new(CompanionEventKind.Phase, "Saves", "Pull", "S23"));

        Assert.Equal(
            "am broadcast -n com.adbsync.companion/.ProgressReceiver -a com.adbsync.companion.EVENT " +
            "--es event 'phase' --es job 'Saves' --es phase 'Pull' --es device 'S23'",
            command);
    }

    [Fact]
    public void BuildCommand_CompletedWithPush()
    {
        var command = CompanionBroadcast.BuildCommand(new(CompanionEventKind.Completed, "Saves", Pushed: true));

        Assert.EndsWith("--es event 'completed' --es job 'Saves' --ez pushed true", command);
    }

    [Fact]
    public void BuildCommand_QuotesShellMetacharacters()
    {
        var command = CompanionBroadcast.BuildCommand(new(CompanionEventKind.Phase, "It's $HOME; rm -rf /", "Pull"));

        Assert.Contains("--es job 'It'\\''s $HOME; rm -rf /'", command);
    }

    [Fact]
    public void BuildConfigCommand_IncludesUrlTokenAndPc()
    {
        var command = CompanionBroadcast.BuildConfigCommand("https://192.168.0.10:47615", "abc123", "ff00", "DESKTOP-1");

        Assert.EndsWith(
            "--es event 'config' --es url 'https://192.168.0.10:47615' --es token 'abc123' --es fp 'ff00' --es pc 'DESKTOP-1'",
            command);
    }

    [Fact]
    public void BuildCommand_FlattensAndTruncatesMessage()
    {
        var message = "line one\r\nline two " + new string('x', 400);
        var command = CompanionBroadcast.BuildCommand(new(CompanionEventKind.Failed, "Job", Message: message));

        var start = command.IndexOf("--es message '", StringComparison.Ordinal) + "--es message '".Length;
        var quoted = command[start..^1];
        Assert.StartsWith("line one line two ", quoted);
        Assert.Equal(300, quoted.Length);
        Assert.EndsWith("...", quoted);
    }
}
