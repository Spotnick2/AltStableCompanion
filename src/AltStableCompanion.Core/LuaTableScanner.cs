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
            var word = ParseWord();
            return word switch
            {
                "nil" => null,
                "true" => true,
                "false" => false,
                _ => throw new SavedVariablesFormatException($"unexpected '{word}' at {_pos}"),
            };
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
                    key = k switch
                    {
                        string s => s,
                        double d when d == Math.Floor(d) => (long)d,
                        _ => throw new SavedVariablesFormatException($"unsupported key at {_pos}"),
                    };
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

        private string ParseString()
        {
            var quote = text[_pos++];
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= text.Length) throw Eof();
                var c = text[_pos++];
                if (c == quote) return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_pos >= text.Length) throw Eof();
                var e = text[_pos++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\n': sb.Append('\n'); break;
                    default:
                        if (char.IsDigit(e))
                        {
                            var start = _pos - 1;
                            while (_pos < text.Length && _pos - start < 3 && char.IsDigit(text[_pos])) _pos++;
                            sb.Append((char)int.Parse(text[start.._pos], CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(e);
                        }
                        break;
                }
            }
        }

        private double ParseNumber()
        {
            var start = _pos;
            if (text[_pos] == '-') _pos++;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '.' or '+' or '-'))
            {
                // Stop a trailing minus from eating the start of a "--" comment.
                if (text[_pos] == '-' && !(text[_pos - 1] is 'e' or 'E')) break;
                _pos++;
            }
            var s = text[start.._pos];
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (s is "inf" or "1.#INF") return double.PositiveInfinity;
            throw new SavedVariablesFormatException($"bad number '{s}' at {start}");
        }

        private string ParseWord()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            if (_pos == start) throw new SavedVariablesFormatException($"unexpected '{text[_pos]}' at {_pos}");
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
