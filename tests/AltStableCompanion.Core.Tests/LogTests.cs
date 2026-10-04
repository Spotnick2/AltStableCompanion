using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class LogTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 16, 58, 30);

    [Fact]
    public void Every_line_of_a_message_carries_the_time_and_the_rest_say_they_continue()
    {
        // What the enhancer writes after a cancelled picture: Codex's output, as it came.
        var text = Log.Format(T0, "enhance: Karuzo Memphisto: cancelled\r\ncodex\r\n\r\nI'm using the imagegen skill.  \nERROR blocked by policy");

        var nl = Environment.NewLine;
        Assert.Equal(
            "2026-10-04 16:58:30  enhance: Karuzo Memphisto: cancelled" + nl
            + "2026-10-04 16:58:30  | codex" + nl
            + "2026-10-04 16:58:30  | I'm using the imagegen skill." + nl
            + "2026-10-04 16:58:30  | ERROR blocked by policy" + nl, text);
    }

    [Fact]
    public void A_one_line_message_is_one_line()
    {
        Assert.Equal("2026-10-04 16:58:30  stopped" + Environment.NewLine, Log.Format(T0, "stopped"));
    }

    [Fact]
    public void The_codex_tail_starts_at_a_line()
    {
        var text = "first line, long enough to be cut\nsecond\nthird";
        Assert.Equal("second\nthird", CodexImageGen.Tail(text, 20));
        // Short enough: all of it.
        Assert.Equal(text, CodexImageGen.Tail(text, 200));
        // One line longer than the bound: cut, and it says so.
        Assert.Equal("…" + new string('x', 10), CodexImageGen.Tail(new string('x', 50), 10));
    }
}
