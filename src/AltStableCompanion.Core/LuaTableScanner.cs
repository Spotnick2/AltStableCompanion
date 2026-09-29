using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>A SavedVariables file that does not parse - normally one WoW is still writing.</summary>
public sealed class SavedVariablesFormatException(string message) : Exception(message);

/// <summary>
/// Reads ONE top-level value out of a SavedVariables file, as plain data.
///
/// WoW writes SavedVariables in a small, fixed subset of Lua: tables of <c>["key"] = value</c>
/// and <c>[n] = value</c> fields (or bare positional values), strings, numbers, booleans and
/// nil, with <c>-- [1]</c> comments after array items. This parses exactly that. It never
/// executes the file - a tampered file is data that fails to parse, not code that runs.
///
/// Tables come back as <see cref="Dictionary{TKey, TValue}"/> keyed by string (string keys)
/// or long (numeric keys and positions); scalars as string, double, bool or null.
///
/// A VALUE it does not know (nan, -nan(ind), a hex number, whatever a later client writes) reads
/// as null and a KEY it does not know drops its field; the rest of the table is still read.
/// One odd number must not hide every capture of an account. Only a file that ENDS inside a
/// table is an error: that one is being written, and is worth reading again.
/// </summary>
public static class LuaTableScanner
{
    /// <summary>
    /// The value assigned to <paramref name="global"/> at the start of a line, or null when the
    /// file has no such assignment or assigns nil. <paramref name="found"/> says which: the
    /// contract treats both as "no captures", but a reader must not go looking elsewhere once
    /// the assignment exists.
    /// </summary>
    public static object? ReadGlobal(string text, string global, out bool found)
    {
        var m = Regex.Match(text, "^" + Regex.Escape(global) + @"\s*=", RegexOptions.Multiline);
        found = m.Success;
        if (!m.Success) return null;
        var p = new Parser(text, m.Index + m.Length);
        return p.ParseValue();
    }

    private sealed class Parser(string text, int pos)
    {
        private int _pos = pos;

        public object? ParseValue()
        {
            SkipTrivia();
            if (_pos >= text.Length) throw Eof();
            var c = text[_pos];
            if (c == '{') return ParseTable();
            if (c == '"' || c == '\'') return ParseString();
            if (c == '-' || c == '.' || char.IsDigit(c)) return ParseNumber();
            var from = _pos;
            if (!(char.IsLetter(c) || c == '_')) return SkipUnknown(from);
            return ParseWord() switch
            {
                "nil" => null,
                "true" => true,
                "false" => false,
                "inf" => double.PositiveInfinity,
                "nan" => double.NaN,
                _ => SkipUnknown(from),
            };
        }

        // Not a value this reader knows: step over it, to the separator or bracket that ends
        // it, and read it as nil. Running out of file on the way is still a file cut short; and
        // a value that is no characters at all is a stray bracket, which nothing can step over.
        private object? SkipUnknown(int from)
        {
            while (_pos < text.Length && text[_pos] is not (',' or ';' or '}' or ']' or '\n')) _pos++;
            if (_pos >= text.Length) throw Eof();
            if (_pos == from) throw new SavedVariablesFormatException($"unexpected '{text[_pos]}' at {_pos}");
            return null;
        }

        private Dictionary<object, object?> ParseTable()
        {
            _pos++;                                             // {
            var table = new Dictionary<object, object?>();
            long position = 1;
            while (true)
            {
                SkipTrivia();
                if (_pos >= text.Length) throw Eof();
                if (text[_pos] == '}') { _pos++; return table; }

                object key;
                if (text[_pos] == '[')
                {
                    _pos++;
                    var k = ParseValue();
                    SkipTrivia();
                    Expect(']');
                    SkipTrivia();
                    Expect('=');
                    switch (k)
                    {
                        case string s:
                            key = s;
                            break;
                        case double d when d == Math.Floor(d) && Math.Abs(d) < long.MaxValue:
                            key = (long)d;
                            break;
                        default:
                            // [1.5], [true]: nothing here is looked up by such a key.
                            ParseValue();
                            SkipSeparator();
                            continue;
                    }
                }
                else
                {
                    // A bare identifier key (name = value) or a positional value.
                    var save = _pos;
                    if (char.IsLetter(text[_pos]) || text[_pos] == '_')
                    {
                        var word = ParseWord();
                        SkipTrivia();
                        if (_pos < text.Length && text[_pos] == '=' && !(word is "true" or "false" or "nil"))
                        {
                            _pos++;
                            key = word;
                            table[key] = ParseValue();
                            SkipSeparator();
                            continue;
                        }
                        _pos = save;
                    }
                    key = position++;
                    table[key] = ParseValue();
                    SkipSeparator();
                    continue;
                }
                table[key] = ParseValue();
                SkipSeparator();
            }
        }

        private void SkipSeparator()
        {
            SkipTrivia();
            if (_pos < text.Length && (text[_pos] == ',' || text[_pos] == ';')) _pos++;
        }

        // A Lua string is BYTES, and a \ddd escape is one of them: "Zo\195\171" is "Zoe" with a
        // diaeresis, in UTF-8. So the string is collected as bytes and decoded once, at the end.
        private string ParseString()
        {
            var quote = text[_pos++];
            var bytes = new List<byte>();
            var plain = _pos;                                   // start of text not yet copied
            void Flush(int end)
            {
                if (end > plain) bytes.AddRange(Encoding.UTF8.GetBytes(text[plain..end]));
            }

            while (true)
            {
                if (_pos >= text.Length) throw Eof();
                var c = text[_pos];
                if (c == quote)
                {
                    Flush(_pos++);
                    return Encoding.UTF8.GetString([.. bytes]);
                }
                if (c != '\\') { _pos++; continue; }

                Flush(_pos++);
                if (_pos >= text.Length) throw Eof();
                var e = text[_pos++];
                if (e is >= '0' and <= '9')
                {
                    var start = _pos - 1;
                    while (_pos < text.Length && _pos - start < 3 && text[_pos] is >= '0' and <= '9') _pos++;
                    bytes.Add((byte)int.Parse(text[start.._pos], CultureInfo.InvariantCulture));
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(e switch
                    {
                        'n' => "\n",
                        't' => "\t",
                        'r' => "\r",
                        'a' => "\a",
                        'b' => "\b",
                        'f' => "\f",
                        'v' => "\v",
                        _ => e.ToString(),                      // \" \' \\ and a line break
                    }));
                }
                plain = _pos;
            }
        }

        private object? ParseNumber()
        {
            var start = _pos;
            if (text[_pos] == '-') _pos++;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '.' or '+' or '-' or '#'))
            {
                // Stop a trailing minus from eating the start of a "--" comment.
                if (text[_pos] == '-' && !(text[_pos - 1] is 'e' or 'E')) break;
                _pos++;
            }
            var s = text[start.._pos];
            // -nan(ind), 1.#QNAN(...): the token goes on past what a number can hold.
            if (_pos < text.Length && text[_pos] == '(') return SkipUnknown(start);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            var (sign, digits) = s.StartsWith('-') ? (-1.0, s[1..]) : (1.0, s);
            if (digits is "inf" or "1.#INF") return sign * double.PositiveInfinity;
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            {
                return sign * hex;
            }
            return SkipUnknown(start);
        }

        private string ParseWord()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            return text[start.._pos];
        }

        private void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c)) { _pos++; continue; }
                if (c == '-' && _pos + 1 < text.Length && text[_pos + 1] == '-')
                {
                    while (_pos < text.Length && text[_pos] != '\n') _pos++;
                    continue;
                }
                break;
            }
        }

        private void Expect(char c)
        {
            if (_pos >= text.Length) throw Eof();
            if (text[_pos] != c) throw new SavedVariablesFormatException($"expected '{c}' at {_pos}");
            _pos++;
        }

        private static SavedVariablesFormatException Eof() =>
            new("the file ends mid-table - WoW may still be writing it");
    }
}
