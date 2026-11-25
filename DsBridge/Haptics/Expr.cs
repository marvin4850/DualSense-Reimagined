using System.Globalization;

namespace DsBridge.Haptics;

/// <summary>Tiny expression language for rule parameters ("=0.06+0.64*sqrt(clamp(eff/800,0,1))").
/// Values: double, string, null (missing). Operators: + - * / %, unary - !, &lt; &lt;= &gt; &gt;= == !=, &amp;&amp; ||, ?:.
/// Functions: sqrt abs min max clamp pow lin(x,x0,x1,y0,y1) has(x) floor round exp log variant(slot) (forge bank selection).
/// A missing variable is null; arithmetic with null gives NaN; has(x) tests presence.</summary>
public static class Expr
{
    public delegate object? Lookup(string name);

    /// <summary>Compiled expression (parsed once, evaluated per event).</summary>
    public sealed class Compiled
    {
        internal readonly Node Root; public readonly string Source;
        internal Compiled(Node root, string src) { Root = root; Source = src; }
        public object? Eval(Lookup vars) => Root.Eval(vars);
        public double EvalNumber(Lookup vars) => ToNum(Root.Eval(vars));
    }

    public static Compiled Compile(string src)
    {
        var p = new Parser(src);
        var n = p.ParseTernary();
        p.Expect(TokKind.End);
        return new Compiled(n, src);
    }

    public static double ToNum(object? v) => v switch
    {
        null => double.NaN,
        double d => d,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        _ => double.NaN
    };
    static bool Truthy(object? v) => v switch { null => false, double d => !double.IsNaN(d) && d != 0, string s => s.Length > 0, _ => false };

    // ---------------- AST ----------------
    internal abstract class Node { public abstract object? Eval(Lookup v); }
    sealed class Num : Node { public double V; public override object? Eval(Lookup v) => V; }
    sealed class Str : Node { public string V = ""; public override object? Eval(Lookup v) => V; }
    sealed class Var : Node { public string Name = ""; public override object? Eval(Lookup v) => v(Name); }
    sealed class Unary : Node
    {
        public char Op; public Node A = null!;
        public override object? Eval(Lookup v) => Op == '-' ? -ToNum(A.Eval(v)) : (Truthy(A.Eval(v)) ? 0.0 : 1.0);
    }
    sealed class Bin : Node
    {
        public string Op = ""; public Node A = null!, B = null!;
        public override object? Eval(Lookup v)
        {
            if (Op == "&&") return Truthy(A.Eval(v)) && Truthy(B.Eval(v)) ? 1.0 : 0.0;
            if (Op == "||") return Truthy(A.Eval(v)) || Truthy(B.Eval(v)) ? 1.0 : 0.0;
            var a = A.Eval(v); var b = B.Eval(v);
            if (Op is "==" or "!=")
            {
                bool eq = (a is string || b is string) ? string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal)
                                                       : ToNum(a) == ToNum(b);
                return (Op == "==") == eq ? 1.0 : 0.0;
            }
            double x = ToNum(a), y = ToNum(b);
            return Op switch
            {
                "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, "%" => x % y,
                "<" => x < y ? 1.0 : 0.0, "<=" => x <= y ? 1.0 : 0.0, ">" => x > y ? 1.0 : 0.0, ">=" => x >= y ? 1.0 : 0.0,
                _ => double.NaN
            };
        }
    }
    sealed class Cond : Node
    {
        public Node C = null!, T = null!, F = null!;
        public override object? Eval(Lookup v) => Truthy(C.Eval(v)) ? T.Eval(v) : F.Eval(v);
    }
    sealed class Call : Node
    {
        public string Fn = ""; public List<Node> Args = new();
        public override object? Eval(Lookup v)
        {
            if (Fn == "variant") return SampleBank.Variant(Args[0].Eval(v)?.ToString() ?? "");   // forge bank selection of a slot
            if (Fn == "has") return Args.Count == 1 && Args[0].Eval(v) != null ? 1.0 : 0.0;
            if (Fn == "contains") { var a0 = Args[0].Eval(v)?.ToString(); var a1 = Args[1].Eval(v)?.ToString(); return a0 != null && a1 != null && a0.Contains(a1, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0; }
            var a = Args.Select(x => ToNum(x.Eval(v))).ToArray();
            return Fn switch
            {
                "sqrt" => Math.Sqrt(Math.Max(0, a[0])),
                "abs" => Math.Abs(a[0]),
                "min" => a.Min(), "max" => a.Max(),
                "clamp" => Math.Clamp(a[0], Math.Min(a[1], a[2]), Math.Max(a[1], a[2])),
                "pow" => Math.Pow(a[0], a[1]),
                "floor" => Math.Floor(a[0]), "round" => Math.Round(a[0]),
                "exp" => Math.Exp(a[0]), "log" => Math.Log(a[0]),
                "lin" => a[3] + (a[4] - a[3]) * Math.Clamp((a[0] - a[1]) / (a[2] - a[1]), 0, 1),   // lin(x, x0, x1, y0, y1), clamped
                _ => double.NaN
            };
        }
    }

    static readonly Dictionary<string, int> Arity = new()
    {
        ["sqrt"] = 1, ["abs"] = 1, ["clamp"] = 3, ["pow"] = 2, ["lin"] = 5, ["has"] = 1, ["contains"] = 2, ["variant"] = 1, ["floor"] = 1, ["round"] = 1, ["exp"] = 1, ["log"] = 1,
        ["min"] = -2, ["max"] = -2   // -2 = at least 2
    };

    // ---------------- parser ----------------
    enum TokKind { Num, Str, Id, Op, LParen, RParen, Comma, Q, Colon, End }
    record Tok(TokKind K, string S, double N = 0);

    sealed class Parser
    {
        readonly List<Tok> _t = new(); int _i; readonly string _src;
        public Parser(string s)
        {
            _src = s; int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int j = i; while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == 'e' || s[j] == 'E' || ((s[j] == '-' || s[j] == '+') && j > i && (s[j - 1] == 'e' || s[j - 1] == 'E')))) j++;
                    _t.Add(new Tok(TokKind.Num, s[i..j], double.Parse(s[i..j], CultureInfo.InvariantCulture))); i = j; continue;
                }
                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    int j = i; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.' || s[j] == '$')) j++;
                    var id = s[i..j]; if (id.StartsWith("$event.")) id = id[7..]; else if (id.StartsWith("$")) id = id[1..];
                    _t.Add(new Tok(TokKind.Id, id)); i = j; continue;
                }
                if (c == '\'')
                {
                    int j = s.IndexOf('\'', i + 1); if (j < 0) throw new FormatException($"unterminated string in '{s}'");
                    _t.Add(new Tok(TokKind.Str, s[(i + 1)..j])); i = j + 1; continue;
                }
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "<=" or ">=" or "==" or "!=" or "&&" or "||") { _t.Add(new Tok(TokKind.Op, two)); i += 2; continue; }
                switch (c)
                {
                    case '(': _t.Add(new Tok(TokKind.LParen, "(")); break;
                    case ')': _t.Add(new Tok(TokKind.RParen, ")")); break;
                    case ',': _t.Add(new Tok(TokKind.Comma, ",")); break;
                    case '?': _t.Add(new Tok(TokKind.Q, "?")); break;
                    case ':': _t.Add(new Tok(TokKind.Colon, ":")); break;
                    case '+': case '-': case '*': case '/': case '%': case '<': case '>': case '!': _t.Add(new Tok(TokKind.Op, c.ToString())); break;
                    default: throw new FormatException($"unexpected '{c}' in '{s}'");
                }
                i++;
            }
            _t.Add(new Tok(TokKind.End, ""));
        }
        Tok Peek => _t[_i];
        Tok Next() => _t[_i++];
        public void Expect(TokKind k) { if (Peek.K != k) throw new FormatException($"expected {k} at token {_i} ('{Peek.S}') in '{_src}'"); _i++; }

        public Node ParseTernary()
        {
            var c = ParseBin(0);
            if (Peek.K != TokKind.Q) return c;
            Next(); var t = ParseTernary(); Expect(TokKind.Colon); var f = ParseTernary();
            return new Cond { C = c, T = t, F = f };
        }
        static readonly string[][] Levels = { new[] { "||" }, new[] { "&&" }, new[] { "==", "!=" }, new[] { "<", "<=", ">", ">=" }, new[] { "+", "-" }, new[] { "*", "/", "%" } };
        Node ParseBin(int level)
        {
            if (level == Levels.Length) return ParseUnary();
            var left = ParseBin(level + 1);
            while (Peek.K == TokKind.Op && Levels[level].Contains(Peek.S))
            {
                var op = Next().S; var right = ParseBin(level + 1);
                left = new Bin { Op = op, A = left, B = right };
            }
            return left;
        }
        Node ParseUnary()
        {
            if (Peek.K == TokKind.Op && (Peek.S == "-" || Peek.S == "!")) { var op = Next().S[0]; return new Unary { Op = op, A = ParseUnary() }; }
            if (Peek.K == TokKind.Op && Peek.S == "+") { Next(); return ParseUnary(); }
            return ParsePrimary();
        }
        Node ParsePrimary()
        {
            var t = Next();
            switch (t.K)
            {
                case TokKind.Num: return new Num { V = t.N };
                case TokKind.Str: return new Str { V = t.S };
                case TokKind.LParen: { var e = ParseTernary(); Expect(TokKind.RParen); return e; }
                case TokKind.Id:
                    if (t.S == "null") return new Var { Name = "\u0000null" };
                    if (Peek.K == TokKind.LParen)
                    {
                        if (!Arity.TryGetValue(t.S, out int ar)) throw new FormatException($"unknown function '{t.S}' in '{_src}'");
                        Next(); var call = new Call { Fn = t.S };
                        if (Peek.K != TokKind.RParen) { call.Args.Add(ParseTernary()); while (Peek.K == TokKind.Comma) { Next(); call.Args.Add(ParseTernary()); } }
                        Expect(TokKind.RParen);
                        if (ar >= 0 ? call.Args.Count != ar : call.Args.Count < -ar) throw new FormatException($"'{t.S}' takes {(ar >= 0 ? ar.ToString() : "at least " + (-ar))} argument(s) in '{_src}'");
                        return call;
                    }
                    return new Var { Name = t.S };
                default: throw new FormatException($"unexpected '{t.S}' in '{_src}'");
            }
        }
    }
}
