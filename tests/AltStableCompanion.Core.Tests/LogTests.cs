using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class LogTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 16, 58, 30);
    private static readonly string Nl = Environment.NewLine;

    [Fact]
    public void Every_line_of_a_message_carries_the_time_and_the_rest_say_they_continue()
    {
        // What the enhancer writes after a cancelled picture: Codex's output, as it came.
        var text = Log.Format(T0, "enhance: Karuzo Memphisto: cancelled\r\ncodex\r\n\r\nI'm using the imagegen skill.  \nERROR blocked by policy");

        Assert.Equal(
            "2026-10-04 16:58:30  enhance: Karuzo Memphisto: cancelled" + Nl
            + "2026-10-04 16:58:30  | codex" + Nl
            + "2026-10-04 16:58:30  | I'm using the imagegen skill." + Nl
            + "2026-10-04 16:58:30  | ERROR blocked by policy" + Nl, text);
    }

    [Fact]
    public void A_one_line_message_is_one_line()
    {
        Assert.Equal("2026-10-04 16:58:30  stopped" + Nl, Log.Format(T0, "stopped"));
    }

    [Fact]
    public void A_lone_carriage_return_is_a_line_break_too()
    {
        // A progress line written over itself: an editor shows what follows the CR as a line.
        Assert.Equal("2026-10-04 16:58:30  downloading 10%" + Nl + "2026-10-04 16:58:30  | downloading 100%" + Nl,
            Log.Format(T0, "downloading 10%\rdownloading 100%"));
    }

    [Fact]
    public void A_message_that_starts_with_a_break_starts_with_its_first_words()
    {
        Assert.Equal("2026-10-04 16:58:30  enhance: cancelled" + Nl + "2026-10-04 16:58:30  | codex" + Nl,
            Log.Format(T0, "\n\nenhance: cancelled\ncodex"));
        Assert.Equal("2026-10-04 16:58:30  " + Nl, Log.Format(T0, ""));
    }

    [Fact]
    public void The_codex_tail_starts_at_a_line_when_that_costs_little()
    {
        // Short enough: all of it.
        Assert.Equal("a\nb", CodexImageGen.Tail("a\nb", 200));
        // The window starts at a line already: nothing is dropped.
        Assert.Equal("bbbb\ncc", CodexImageGen.Tail("aaaa\nbbbb\ncc", 7));
        // Half a word at the top: dropped, up to the line after it.
        Assert.Equal(new string('b', 9), CodexImageGen.Tail(new string('a', 7) + "\n" + new string('b', 9), 10));
        // One line longer than the bound: cut, and it says so.
        Assert.Equal("…" + new string('x', 10), CodexImageGen.Tail(new string('x', 50), 10));
    }

    [Fact]
    public void A_long_error_line_followed_by_a_short_one_keeps_the_error()
    {
        var text = "start\n" + new string('x', 30) + "\nTokens used: 12";
        Assert.Equal("…" + new string('x', 14) + "\nTokens used: 12", CodexImageGen.Tail(text, 30));
    }
}
