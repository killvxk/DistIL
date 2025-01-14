namespace DistIL.IR.Utils.Parser;

using System.Globalization;
using System.Text.RegularExpressions;

internal class Lexer
{
    private string _str;
    private int _pos;
    private int _startPos; //start position of the current token
    private int _peekedLeadPos; //leading (before start, considering whitespace) position of the peeked token
    private Token? _peeked;

    private bool _predByLF = false; //found '\n' before current token?
    private int _nextLevel = 0;
    private ArrayStack<int> _indents = new();

    private ParserContext _ctx;

    /// <summary> Get/set an opaque handle of the current cursor position. </summary>
    public CursorHandle Cursor {
        get => new() { Pos = LastPos(), Indent = _nextLevel };
        set {
            _peekedLeadPos = _startPos = _pos = value.Pos;
            _nextLevel = value.Indent;
            _peeked = null;

            while (NextIndent() != default) {
                //Reset indent levels, discard any tokens found after the cursor being set
            }
        }
    }

    public Lexer(ParserContext ctx) => (_str, _ctx) = (ctx.SourceCode, ctx);

    public bool Match(TokenType type) => Match(type, out _);
    public bool Match(TokenType type, out Token token)
    {
        token = Peek();
        if (token.Type == type) {
            _peeked = null;
            return true;
        }
        return false;
    }
    public bool MatchKeyword(string keyword)
    {
        var token = Peek();
        if (token.Type == TokenType.Identifier && token.StrValue == keyword) {
            _peeked = null;
            return true;
        }
        return false;
    }

    public bool IsNextOnNewLine()
    {
        return Peek().Type == TokenType.EOF || _predByLF;
    }
    public bool IsNext(TokenType type)
    {
        return Peek().Type == type;
    }

    public Token Expect(TokenType type)
    {
        var token = Next();
        if (token.Type != type) {
            ErrorUnexpected(token, type.ToString());
        }
        return token;
    }
    public string ExpectId(string? value = null)
    {
        var token = Next();
        if (token.Type == TokenType.Identifier && (value == null || token.StrValue == value)) {
            return token.StrValue;
        }
        ErrorUnexpected(token, value ?? "Identifier");
        return "<broken expectations>\r\n";
    }

    public int NextPos() => Peek().Position.Start;
    public int LastPos() => _peeked != null ? _peekedLeadPos : _pos;

    public Token Peek()
    {
        if (_peeked == null) {
            _peekedLeadPos = _pos;
            _peeked = Next();
        }
        return _peeked.Value;
    }
    public Token Next()
    {
        if (_peeked != null) {
            var token = _peeked.Value;
            _peeked = null;
            return token;
        }
        //Emit pending indent token
        var indentTok = NextIndent();
        if (indentTok != default) {
            return Tok(indentTok);
        }
        char ch;
        _predByLF = false;

        //Skip whitespace
        while (true) {
            if (_pos >= _str.Length) {
                return new Token(TokenType.EOF, _pos, _pos);
            }
            ch = _str[_pos++];
            if (ch == '\n') {
                indentTok = NextIndent(updateLevel: true);
                _predByLF = true;

                if (indentTok != default) {
                    return Tok(indentTok);
                }
            } else if (!char.IsWhiteSpace(ch) && !(ch == '/' && SkipComment())) {
                _pos--;
                break;
            }
        }

        _startPos = _pos;
        char ch2 = _pos + 1 < _str.Length ? _str[_pos + 1] : '\0';

        //Match token
        var sym = ch switch {
            '(' => TokenType.LParen,
            ')' => TokenType.RParen,
            '{' => TokenType.LBrace,
            '}' => TokenType.RBrace,
            '[' => TokenType.LBracket,
            ']' => TokenType.RBracket,
            '<' => TokenType.LAngle,
            '>' => TokenType.RAngle,

            '+' => TokenType.Plus,
            '*' => TokenType.Asterisk,
            '&' => TokenType.Ampersand,
            '=' => TokenType.Equal,
            ',' => TokenType.Comma,
            '.' => TokenType.Dot,
            ';' => TokenType.Semicolon,
            '?' => TokenType.QuestionMark,
            '!' => TokenType.ExlamationMark,
            '^' => TokenType.Caret,

            '-' => ch2 == '>' ? TokenType.Arrow : 
                   ch2 is not (>= '0' and <= '9') ? TokenType.Minus : 
                   default,
            ':' => ch2 == ':' ? TokenType.DoubleColon :
                   TokenType.Colon,
            _ => default
        };
        if (sym != default) {
            _pos += sym > TokenType._StartLen2 ? 2 : 1;
            return Tok(sym);
        }
        if (ch is (>= '0' and <= '9') or '-') {
            return Tok(TokenType.Literal, ParseNumber());
        }
        if (ch is '"') {
            return Tok(TokenType.Literal, ParseString());
        }
        if (IsIdentifierChar(ch)) {
            return Tok(TokenType.Identifier, ParseIdentifier());
        }
        throw _ctx.Fatal("Unknown character", (_startPos, _pos));

        Token Tok(TokenType type, object? value = null) => new(type, _startPos, _pos, value);
    }

    private TokenType NextIndent(bool updateLevel = false)
    {
        if (updateLevel) {
            _startPos = _pos;
            while (_pos < _str.Length && _str[_pos] == ' ') {
                _pos++;
            }
            _nextLevel = _pos - _startPos;
        }
        //Emit indent/dedents (based on https://stackoverflow.com/a/2742159)
        int currLevel = _indents.IsEmpty ? 0 : _indents.Top;
        if (_nextLevel > currLevel) {
            _indents.Push(_nextLevel);
            return TokenType.Indent;
        }
        if (_nextLevel < currLevel) {
            currLevel = _indents.IsEmpty ? 0 : _indents.Pop();
            if (currLevel < _nextLevel) {
                Error("Inconsistent indentation");
            }
            return TokenType.Dedent;
        }
        return default;
    }

    //[-] int [.fract] [E [+|-] exp] [UL|U|L|F|D]
    static readonly Regex s_NumberRegex = new(@"(-?\d+(\.\d+(?:E[+-]?\d+)?)?)(UL|U|L|F|D)?", RegexOptions.IgnoreCase);
    private Value ParseNumber()
    {
        var m = s_NumberRegex.Match(_str, _pos);
        if (!m.Success) {
            Error("Malformed number");
            _pos++; //skip at least one char to prevent infinite loop
            return ConstInt.CreateI(0);
        }
        _pos += m.Length;

        var literal = m.Groups[1].ValueSpan;
        var postfix = m.Groups[3].ValueSpan;
        bool F = postfix.EqualsIgnoreCase("F");
        bool D = postfix.EqualsIgnoreCase("D");

        if (m.Groups[2].Success || F || D) { //fraction or exponent
            double r = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);

            var type = F ? PrimType.Single : PrimType.Double;
            return ConstFloat.Create(type, r);
        } else {
            long r = long.Parse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture);

            bool U = postfix.ContainsIgnoreCase("U");
            bool L = postfix.ContainsIgnoreCase("L");
            var type = L
                ? (U ? PrimType.UInt64 : PrimType.Int64)
                : (U ? PrimType.UInt32 : PrimType.Int32);

            return ConstInt.Create(type, r);
        }
    }
    private Value ParseString()
    {
        var sb = new StringBuilder();
        _pos++; //skip initial quote
        while (_pos < _str.Length) {
            char ch = _str[_pos++];
            if (ch == '"') break;
            if (ch == '\\') {
                ch = _str[_pos++] switch {
                    'r' => '\r',
                    'n' => '\n',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\', // \
                    '\'' => '\'', // '
                    _ => Unk()
                };
                char Unk() { _ctx.Error("Unknown escaping sequence", (_pos - 2, _pos)); return '?'; }
            }
            sb.Append(ch);
        }
        if (_pos >= _str.Length) {
            Error("Unterminated string");
        }
        return ConstString.Create(sb.ToString());
    }
    private string ParseIdentifier()
    {
        for (; _pos < _str.Length; _pos++) {
            char ch = _str[_pos];
            bool valid =
                IsIdentifierChar(ch) ||
                //Allow a few special characters if they are surrounded by normal characters
                (ch is '.' or '-' && _pos + 1 < _str.Length && IsIdentifierChar(_str[_pos + 1]));
                
            if (!valid) break;
        }
        return _str[_startPos.._pos];
    }

    private static bool IsIdentifierChar(char ch)
    {
        return ch is
            (>= 'a' and <= 'z') or
            (>= 'A' and <= 'Z') or
            (>= '0' and <= '9') or
            '_' or '$' or '#' or '`' or '@';
    }

    private bool SkipComment()
    {
        var str = _str.AsSpan(_pos); //first character was already consumed
        int len;

        if (str[0] == '/') {
            len = str.IndexOf('\n');
            if (len < 0) len = str.Length;
        } else if (str[0] == '*') {
            len = str.IndexOf("*/") + 2;
            if (len == 1) {
                len = str.Length;
                Error("Unterminated multi-line comment");
            }
        } else {
            return false;
        }
        _pos += len;
        return true;
    }

    public void ErrorUnexpected(Token actual, string expected)
    {
        Error($"Expected '{expected}', got '{actual.Type}'");
    }
    public void Error(string msg, int start = -1)
    {
        _ctx.Error(msg, (start < 0 ? _startPos : start, _pos));
    }

    public struct CursorHandle
    {
        internal int Pos, Indent;
    }
}