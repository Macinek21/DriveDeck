using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriveDeck.Core
{
    /// <summary>
    /// A tiny, safe arithmetic formula language for converting OBD response bytes into values.
    /// Variables A..H are the data bytes after the PID/DID. Supported: numbers, + - * / %, parentheses,
    /// unary minus, and the functions min, max, abs, round, signed8, signed16, bit(x,n).
    /// There is no reflection, no eval, no loops and no I/O: a formula can only compute a number.
    /// Hard limits on length and nesting keep evaluation bounded.
    /// </summary>
    public sealed class Formula
    {
        public const int MaxLength = 200;
        public const int MaxDepth = 24;
        public const int VariableCount = 8;

        private readonly Node _root;
        public string Source { get; private set; }
        /// <summary>Highest variable index referenced (A=0). -1 when no variables are used.</summary>
        public int HighestVariable { get; private set; }

        private Formula(string source, Node root, int highest)
        {
            Source = source;
            _root = root;
            HighestVariable = highest;
        }

        public static bool TryParse(string source, out Formula formula, out string error)
        {
            formula = null;
            error = null;
            if (string.IsNullOrWhiteSpace(source)) { error = "Formula is empty."; return false; }
            if (source.Length > MaxLength) { error = "Formula is longer than " + MaxLength + " characters."; return false; }
            try
            {
                var p = new Parser(source);
                Node root = p.ParseExpression(0);
                p.ExpectEnd();
                formula = new Formula(source.Trim(), root, p.HighestVariable);
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static Formula Parse(string source)
        {
            Formula f; string err;
            if (!TryParse(source, out f, out err)) throw new FormatException(err);
            return f;
        }

        /// <summary>Evaluates with the given data bytes. Missing bytes evaluate as NaN, so the caller can reject the value.</summary>
        public double Evaluate(IList<byte> data)
        {
            double[] vars = new double[VariableCount];
            for (int i = 0; i < VariableCount; i++)
                vars[i] = (data != null && i < data.Count) ? data[i] : double.NaN;
            return _root.Eval(vars);
        }

        // ---------------- AST ----------------
        private abstract class Node { public abstract double Eval(double[] v); }
        private sealed class Num : Node { public double V; public override double Eval(double[] v) { return V; } }
        private sealed class Var : Node { public int I; public override double Eval(double[] v) { return v[I]; } }
        private sealed class Neg : Node { public Node X; public override double Eval(double[] v) { return -X.Eval(v); } }
        private sealed class Bin : Node
        {
            public char Op; public Node L, R;
            public override double Eval(double[] v)
            {
                double a = L.Eval(v), b = R.Eval(v);
                switch (Op)
                {
                    case '+': return a + b;
                    case '-': return a - b;
                    case '*': return a * b;
                    case '/': return b == 0 ? double.NaN : a / b;
                    case '%': return b == 0 ? double.NaN : a % b;
                    default: return double.NaN;
                }
            }
        }
        private sealed class Call : Node
        {
            public string Name; public Node[] Args;
            public override double Eval(double[] v)
            {
                double x = Args[0].Eval(v);
                switch (Name)
                {
                    case "abs": return Math.Abs(x);
                    case "round": return Math.Round(x, MidpointRounding.AwayFromZero);
                    case "signed8":
                        if (double.IsNaN(x)) return x;
                        { int b = ((int)x) & 0xFF; return b >= 0x80 ? b - 0x100 : b; }
                    case "signed16":
                        if (double.IsNaN(x)) return x;
                        { int w = ((int)x) & 0xFFFF; return w >= 0x8000 ? w - 0x10000 : w; }
                    case "min": return Math.Min(x, Args[1].Eval(v));
                    case "max": return Math.Max(x, Args[1].Eval(v));
                    case "bit":
                        {
                            double n = Args[1].Eval(v);
                            if (double.IsNaN(x) || double.IsNaN(n) || n < 0 || n > 31) return double.NaN;
                            return (((long)x) >> (int)n) & 1;
                        }
                    default: return double.NaN;
                }
            }
        }

        private static readonly Dictionary<string, int> FunctionArity = new Dictionary<string, int>
        {
            { "abs", 1 }, { "round", 1 }, { "signed8", 1 }, { "signed16", 1 },
            { "min", 2 }, { "max", 2 }, { "bit", 2 }
        };

        // ---------------- Parser (precedence climbing) ----------------
        private sealed class Parser
        {
            private readonly string _s;
            private int _pos;
            private int _depth;
            public int HighestVariable = -1;

            public Parser(string s) { _s = s; }

            private void SkipWs() { while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++; }
            private char Peek() { SkipWs(); return _pos < _s.Length ? _s[_pos] : '\0'; }

            public void ExpectEnd()
            {
                if (Peek() != '\0') throw new FormatException("Unexpected '" + _s[_pos] + "' at position " + (_pos + 1) + ".");
            }

            private static int Prec(char op)
            {
                switch (op) { case '+': case '-': return 1; case '*': case '/': case '%': return 2; default: return -1; }
            }

            public Node ParseExpression(int minPrec)
            {
                if (++_depth > MaxDepth) throw new FormatException("Formula is nested too deeply.");
                Node left = ParseUnary();
                while (true)
                {
                    char op = Peek();
                    int prec = Prec(op);
                    if (prec < 0 || prec < minPrec) break;
                    _pos++;
                    Node right = ParseExpression(prec + 1);
                    left = new Bin { Op = op, L = left, R = right };
                }
                _depth--;
                return left;
            }

            private Node ParseUnary()
            {
                char c = Peek();
                if (c == '-') { _pos++; if (++_depth > MaxDepth) throw new FormatException("Formula is nested too deeply."); var n = new Neg { X = ParseUnary() }; _depth--; return n; }
                if (c == '+') { _pos++; return ParseUnary(); }
                return ParsePrimary();
            }

            private Node ParsePrimary()
            {
                char c = Peek();
                if (c == '(')
                {
                    _pos++;
                    Node inner = ParseExpression(0);
                    if (Peek() != ')') throw new FormatException("Missing ')'.");
                    _pos++;
                    return inner;
                }
                if (char.IsDigit(c) || c == '.')
                {
                    int start = _pos;
                    while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
                    double val;
                    if (!double.TryParse(_s.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        throw new FormatException("Invalid number at position " + (start + 1) + ".");
                    return new Num { V = val };
                }
                if (char.IsLetter(c))
                {
                    int start = _pos;
                    while (_pos < _s.Length && char.IsLetterOrDigit(_s[_pos])) _pos++;
                    string ident = _s.Substring(start, _pos - start);
                    if (ident.Length == 1 && char.ToUpperInvariant(ident[0]) >= 'A' && char.ToUpperInvariant(ident[0]) < 'A' + VariableCount)
                    {
                        int idx = char.ToUpperInvariant(ident[0]) - 'A';
                        if (idx > HighestVariable) HighestVariable = idx;
                        return new Var { I = idx };
                    }
                    string name = ident.ToLowerInvariant();
                    int arity;
                    if (!FunctionArity.TryGetValue(name, out arity))
                        throw new FormatException("Unknown name '" + ident + "'. Use A-H for bytes or abs, round, min, max, signed8, signed16, bit.");
                    if (Peek() != '(') throw new FormatException("Expected '(' after " + ident + ".");
                    _pos++;
                    var args = new List<Node>();
                    args.Add(ParseExpression(0));
                    while (Peek() == ',') { _pos++; args.Add(ParseExpression(0)); }
                    if (Peek() != ')') throw new FormatException("Missing ')' after arguments of " + ident + ".");
                    _pos++;
                    if (args.Count != arity) throw new FormatException(ident + " takes " + arity + " argument(s).");
                    return new Call { Name = name, Args = args.ToArray() };
                }
                if (c == '\0') throw new FormatException("Formula ends unexpectedly.");
                throw new FormatException("Unexpected '" + c + "' at position " + (_pos + 1) + ".");
            }
        }
    }
}
