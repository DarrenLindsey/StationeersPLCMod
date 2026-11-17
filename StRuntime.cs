using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace StationeersPlc
{
    // ---------------------------------------------------------------------------------------------
    // A small IEC 61131-3 Structured Text interpreter that reads and writes the same four tables
    // an external PLC sees. Supported: PROGRAM/VAR, BOOL/INT/UINT/DINT/REAL/TIME, AT %IX %QX %IW %QW,
    // IF/ELSIF/ELSE, WHILE, := , AND OR XOR NOT, + - * / MOD, comparisons, TON timers,
    // ABS MIN MAX LIMIT. Everything is evaluated as double; BOOL and integer types are normalised on assignment.
    // ---------------------------------------------------------------------------------------------

    internal sealed class StVar
    {
        public string Name;
        public string Type = "BOOL";
        public double Value;
        public bool Bound;
        public bool IsOutput;
        public Table Table;
        public int Address;
        public bool IsTimer;
        public double Elapsed, PresetMs;
        public bool Q;
        public bool IsBool => Type == "BOOL";
    }

    internal sealed class StProgram
    {
        public readonly List<StVar> Vars = new List<StVar>();
        public readonly List<StStmt> Body = new List<StStmt>();
        public StVar Find(string n)
        {
            foreach (var v in Vars) if (string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }
    }

    internal sealed class StCtx { public double DtMs; public int Loops; }

    internal abstract class StExpr { public abstract double Eval(StCtx c); }
    internal abstract class StStmt { public abstract void Exec(StCtx c); }

    internal sealed class StNum : StExpr
    {
        public double V;
        public override double Eval(StCtx c) => V;
    }

    internal sealed class StRef : StExpr
    {
        public StVar Var;
        public string Member; // timer member Q or ET
        public override double Eval(StCtx c)
        {
            if (Var.IsTimer)
            {
                if (Member == "Q") return Var.Q ? 1 : 0;
                if (Member == "ET") return Var.Elapsed;
            }
            return Var.Value;
        }
    }

    internal sealed class StBin : StExpr
    {
        public string Op; public StExpr A, B;
        public override double Eval(StCtx c)
        {
            double a = A.Eval(c), b = B.Eval(c);
            switch (Op)
            {
                case "OR": return (a != 0 || b != 0) ? 1 : 0;
                case "XOR": return ((a != 0) != (b != 0)) ? 1 : 0;
                case "AND": return (a != 0 && b != 0) ? 1 : 0;
                case "=": return a == b ? 1 : 0;
                case "<>": return a != b ? 1 : 0;
                case "<": return a < b ? 1 : 0;
                case ">": return a > b ? 1 : 0;
                case "<=": return a <= b ? 1 : 0;
                case ">=": return a >= b ? 1 : 0;
                case "+": return a + b;
                case "-": return a - b;
                case "*": return a * b;
                case "/": return b == 0 ? 0 : a / b;
                case "MOD": return b == 0 ? 0 : a % b;
            }
            return 0;
        }
    }

    internal sealed class StUn : StExpr
    {
        public bool Not; public StExpr A;
        public override double Eval(StCtx c)
        {
            double a = A.Eval(c);
            return Not ? (a == 0 ? 1 : 0) : -a;
        }
    }

    internal sealed class StFn : StExpr
    {
        public string Name; public List<StExpr> Args = new List<StExpr>();
        public override double Eval(StCtx c)
        {
            switch (Name)
            {
                case "ABS": return Math.Abs(Args[0].Eval(c));
                case "MIN": return Math.Min(Args[0].Eval(c), Args[1].Eval(c));
                case "MAX": return Math.Max(Args[0].Eval(c), Args[1].Eval(c));
                case "LIMIT": return Math.Max(Args[0].Eval(c), Math.Min(Args[1].Eval(c), Args[2].Eval(c)));
            }
            return 0;
        }
    }

    internal sealed class StAssign : StStmt
    {
        public StVar Target; public StExpr Value;
        public override void Exec(StCtx c)
        {
            double v = Value.Eval(c);
            switch (Target.Type)
            {
                case "BOOL": v = v != 0 ? 1 : 0; break;
                case "INT": case "DINT": case "UINT": case "WORD": v = Math.Truncate(v); break;
            }
            Target.Value = v;
        }
    }

    internal sealed class StIf : StStmt
    {
        public List<StExpr> Conds = new List<StExpr>();
        public List<List<StStmt>> Blocks = new List<List<StStmt>>();
        public List<StStmt> Else;
        public override void Exec(StCtx c)
        {
            for (int i = 0; i < Conds.Count; i++)
                if (Conds[i].Eval(c) != 0) { foreach (var s in Blocks[i]) s.Exec(c); return; }
            if (Else != null) foreach (var s in Else) s.Exec(c);
        }
    }

    internal sealed class StWhile : StStmt
    {
        public StExpr Cond; public List<StStmt> Body = new List<StStmt>();
        public override void Exec(StCtx c)
        {
            int n = 0;
            while (Cond.Eval(c) != 0)
            {
                if (++n > 1000 || ++c.Loops > 20000) throw new InvalidOperationException("WHILE loop ran too long (scan overrun)");
                foreach (var s in Body) s.Exec(c);
            }
        }
    }

    internal sealed class StTon : StStmt
    {
        public StVar Timer; public StExpr In, Pt;
        public override void Exec(StCtx c)
        {
            bool inp = In != null && In.Eval(c) != 0;
            double pt = Pt != null ? Pt.Eval(c) : Timer.PresetMs;
            Timer.PresetMs = pt;
            if (inp) { Timer.Elapsed = Math.Min(Timer.Elapsed + c.DtMs, pt); Timer.Q = Timer.Elapsed >= pt; }
            else { Timer.Elapsed = 0; Timer.Q = false; }
        }
    }

    internal sealed class StParseException : Exception
    {
        public StParseException(string m, int line) : base("line " + line + ": " + m) { }
    }

    internal static class StCompiler
    {
        private struct Tok { public string Text; public string Kind; public int Line; } // Kind: id num addr sym time

        // Safety limits. The interpreter walks the program tree with recursion, and a stack overflow kills the whole game,
        // so a program that is absurdly long or deeply nested is refused with a normal compile error instead.
        private const int MaxTokens = 5000;   // a typical student program is well under 1000
        private const int MaxDepth = 64;      // nested IF / WHILE / brackets / NOT chains

        public static StProgram Compile(string src, out string error)
        {
            try
            {
                error = null;
                var toks = Lex(src);
                if (toks.Count > MaxTokens) throw new StParseException("program is too long (limit " + MaxTokens + " tokens)", 1);
                return new Parser(toks).ParseProgram();
            }
            catch (StParseException e) { error = e.Message; return null; }
        }

        private static List<Tok> Lex(string s)
        {
            var t = new List<Tok>();
            int i = 0, line = 1;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') { line++; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int e = s.IndexOf("*)", i + 2, StringComparison.Ordinal);
                    if (e < 0) throw new StParseException("comment not closed", line);
                    for (int k = i; k < e; k++) if (s[k] == '\n') line++;
                    i = e + 2; continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '%')
                {
                    int st = i++;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i++;
                    t.Add(new Tok { Text = s.Substring(st, i - st).ToUpperInvariant(), Kind = "addr", Line = line });
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int st = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    string w = s.Substring(st, i - st);
                    if ((w.Equals("T", StringComparison.OrdinalIgnoreCase) || w.Equals("TIME", StringComparison.OrdinalIgnoreCase)) && i < s.Length && s[i] == '#')
                    {
                        i++;
                        int ts = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i++;
                        t.Add(new Tok { Text = s.Substring(ts, i - ts), Kind = "time", Line = line });
                        continue;
                    }
                    t.Add(new Tok { Text = w, Kind = "id", Line = line });
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int st = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))) i++;
                    t.Add(new Tok { Text = s.Substring(st, i - st), Kind = "num", Line = line });
                    continue;
                }
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two == ":=" || two == "<>" || two == "<=" || two == ">=") { t.Add(new Tok { Text = two, Kind = "sym", Line = line }); i += 2; continue; }
                if ("+-*/()<>=;:,.".IndexOf(c) >= 0) { t.Add(new Tok { Text = c.ToString(), Kind = "sym", Line = line }); i++; continue; }
                throw new StParseException("unexpected character '" + c + "'", line);
            }
            return t;
        }

        private sealed class Parser
        {
            private readonly List<Tok> _t; private int _p; private StProgram _prog;
            private int _depth; // current nesting level, see MaxDepth
            public Parser(List<Tok> t) { _t = t; }

            /// <summary>Call on entry to every recursive parse step; pair with _depth-- in a finally block.</summary>
            private void Enter() { if (++_depth > MaxDepth) throw Err("program is nested too deeply (limit " + MaxDepth + " levels)"); }

            private Tok Cur => _p < _t.Count ? _t[_p] : new Tok { Text = "<end>", Kind = "end", Line = _t.Count > 0 ? _t[_t.Count - 1].Line : 1 };
            private bool IsKw(string k) => Cur.Kind == "id" && Cur.Text.Equals(k, StringComparison.OrdinalIgnoreCase);
            private bool IsSym(string s) => Cur.Kind == "sym" && Cur.Text == s;
            private StParseException Err(string m) => new StParseException(m, Cur.Line);
            private void ExpectKw(string k) { if (!IsKw(k)) throw Err("expected " + k + " but found '" + Cur.Text + "'"); _p++; }
            private void ExpectSym(string s) { if (!IsSym(s)) throw Err("expected '" + s + "' but found '" + Cur.Text + "'"); _p++; }
            private string ExpectId() { if (Cur.Kind != "id") throw Err("expected a name but found '" + Cur.Text + "'"); return _t[_p++].Text; }

            public StProgram ParseProgram()
            {
                _prog = new StProgram();
                ExpectKw("PROGRAM"); ExpectId();
                while (IsKw("VAR")) ParseVars();
                ParseBlock(_prog.Body, "END_PROGRAM");
                ExpectKw("END_PROGRAM");
                return _prog;
            }

            private void ParseVars()
            {
                ExpectKw("VAR");
                while (!IsKw("END_VAR"))
                {
                    var names = new List<string> { ExpectId() };
                    string addrText = null;
                    if (IsKw("AT"))
                    {
                        _p++;
                        if (Cur.Kind != "addr") throw Err("expected an address like %IW0 after AT");
                        addrText = _t[_p++].Text;
                    }
                    else while (IsSym(",")) { _p++; names.Add(ExpectId()); }
                    ExpectSym(":");
                    string type = ExpectId().ToUpperInvariant();
                    double init = 0;
                    if (IsSym(":=")) { _p++; init = ConstValue(); }
                    ExpectSym(";");

                    foreach (var n in names)
                    {
                        if (_prog.Find(n) != null) throw Err("variable '" + n + "' is declared twice");
                        var v = new StVar { Name = n, Type = type == "TON" ? "TON" : type, Value = init };
                        if (type == "TON") v.IsTimer = true;
                        else if (type != "BOOL" && type != "INT" && type != "UINT" && type != "DINT" && type != "WORD" && type != "REAL" && type != "TIME")
                            throw Err("type " + type + " is not supported (use BOOL INT UINT DINT REAL TIME TON)");
                        if (addrText != null) BindAddress(v, addrText);
                        _prog.Vars.Add(v);
                    }
                }
                ExpectKw("END_VAR");
            }

            private double ConstValue()
            {
                bool neg = false;
                if (IsSym("-")) { neg = true; _p++; }
                double v;
                if (Cur.Kind == "num") v = double.Parse(_t[_p++].Text, CultureInfo.InvariantCulture);
                else if (Cur.Kind == "time") v = ParseTime(_t[_p++].Text);
                else if (IsKw("TRUE")) { _p++; v = 1; }
                else if (IsKw("FALSE")) { _p++; v = 0; }
                else throw Err("expected a constant value");
                return neg ? -v : v;
            }

            private double ParseTime(string s)
            {
                // 5s, 500ms, 1m30s, 2h
                double total = 0; int i = 0; string lower = s.ToLowerInvariant();
                while (i < lower.Length)
                {
                    int st = i;
                    while (i < lower.Length && (char.IsDigit(lower[i]) || lower[i] == '.')) i++;
                    if (st == i) throw Err("bad time literal T#" + s);
                    double n = double.Parse(lower.Substring(st, i - st), CultureInfo.InvariantCulture);
                    int us = i;
                    while (i < lower.Length && char.IsLetter(lower[i])) i++;
                    string unit = lower.Substring(us, i - us);
                    switch (unit)
                    {
                        case "ms": total += n; break;
                        case "s": total += n * 1000; break;
                        case "m": total += n * 60000; break;
                        case "h": total += n * 3600000; break;
                        default: throw Err("bad time unit '" + unit + "' in T#" + s);
                    }
                }
                return total;
            }

            private void BindAddress(StVar v, string a)
            {
                // %IX0.3 = discrete input bit 3 (byte*8+bit), %IX5 = bit 5, %QX = coil, %IW n = input register, %QW n = holding register
                if (a.Length < 4 || a[0] != '%') throw Err("bad address " + a);
                char dir = a[1], size = a[2];
                string num = a.Substring(3);
                int addr;
                int dot = num.IndexOf('.');
                if (size == 'X' && dot >= 0)
                {
                    int by, bit;
                    if (!int.TryParse(num.Substring(0, dot), out by) || !int.TryParse(num.Substring(dot + 1), out bit) || bit < 0 || bit > 7) throw Err("bad bit address " + a);
                    addr = by * 8 + bit;
                }
                else if (!int.TryParse(num, out addr)) throw Err("bad address " + a);
                if (addr < 0 || addr >= PointStore.Size) throw Err("address out of range " + a);

                if (dir == 'I' && size == 'X') { v.Table = Table.DiscreteInput; v.Type = "BOOL"; }
                else if (dir == 'Q' && size == 'X') { v.Table = Table.Coil; v.IsOutput = true; v.Type = "BOOL"; }
                else if (dir == 'I' && size == 'W') v.Table = Table.InputRegister;
                else if (dir == 'Q' && size == 'W') { v.Table = Table.HoldingRegister; v.IsOutput = true; }
                else throw Err("address must be %IX %QX %IW or %QW: " + a);
                v.Bound = true; v.Address = addr;
            }

            private void ParseBlock(List<StStmt> into, params string[] enders)
            {
                while (true)
                {
                    foreach (var e in enders) if (IsKw(e)) return;
                    if (Cur.Kind == "end") throw Err("missing " + enders[0]);
                    into.Add(ParseStmt());
                }
            }

            private StStmt ParseStmt() { Enter(); try { return ParseStmtCore(); } finally { _depth--; } }

            private StStmt ParseStmtCore()
            {
                if (IsKw("IF"))
                {
                    _p++;
                    var s = new StIf();
                    s.Conds.Add(ParseExpr()); ExpectKw("THEN");
                    var b = new List<StStmt>(); ParseBlock(b, "ELSIF", "ELSE", "END_IF"); s.Blocks.Add(b);
                    while (IsKw("ELSIF"))
                    {
                        _p++; s.Conds.Add(ParseExpr()); ExpectKw("THEN");
                        var b2 = new List<StStmt>(); ParseBlock(b2, "ELSIF", "ELSE", "END_IF"); s.Blocks.Add(b2);
                    }
                    if (IsKw("ELSE")) { _p++; s.Else = new List<StStmt>(); ParseBlock(s.Else, "END_IF"); }
                    ExpectKw("END_IF"); ExpectSym(";");
                    return s;
                }
                if (IsKw("WHILE"))
                {
                    _p++;
                    var w = new StWhile { Cond = ParseExpr() };
                    ExpectKw("DO"); ParseBlock(w.Body, "END_WHILE"); ExpectKw("END_WHILE"); ExpectSym(";");
                    return w;
                }

                string name = ExpectId();
                var v = _prog.Find(name);
                if (v == null) throw Err("unknown variable '" + name + "'");
                if (v.IsTimer)
                {
                    var t = new StTon { Timer = v };
                    ExpectSym("(");
                    while (!IsSym(")"))
                    {
                        string arg = ExpectId().ToUpperInvariant();
                        ExpectSym(":=");
                        var e = ParseExpr();
                        if (arg == "IN") t.In = e; else if (arg == "PT") t.Pt = e; else throw Err("TON has inputs IN and PT, not " + arg);
                        if (IsSym(",")) _p++;
                    }
                    ExpectSym(")"); ExpectSym(";");
                    return t;
                }
                if (v.IsOutput == false && v.Bound) throw Err("'" + name + "' is an input (" + (v.Table == Table.DiscreteInput ? "%IX" : "%IW") + ") and cannot be assigned");
                ExpectSym(":=");
                var a = new StAssign { Target = v, Value = ParseExpr() };
                ExpectSym(";");
                return a;
            }

            private StExpr ParseExpr() { Enter(); try { return ParseOr(); } finally { _depth--; } }
            private StExpr Bin(string op, StExpr a, StExpr b) => new StBin { Op = op, A = a, B = b };

            private StExpr ParseOr()
            {
                var a = ParseXor();
                while (IsKw("OR")) { _p++; a = Bin("OR", a, ParseXor()); }
                return a;
            }
            private StExpr ParseXor()
            {
                var a = ParseAnd();
                while (IsKw("XOR")) { _p++; a = Bin("XOR", a, ParseAnd()); }
                return a;
            }
            private StExpr ParseAnd()
            {
                var a = ParseEq();
                while (IsKw("AND")) { _p++; a = Bin("AND", a, ParseEq()); }
                return a;
            }
            private StExpr ParseEq()
            {
                var a = ParseRel();
                while (IsSym("=") || IsSym("<>")) { string op = _t[_p++].Text; a = Bin(op, a, ParseRel()); }
                return a;
            }
            private StExpr ParseRel()
            {
                var a = ParseAdd();
                while (IsSym("<") || IsSym(">") || IsSym("<=") || IsSym(">=")) { string op = _t[_p++].Text; a = Bin(op, a, ParseAdd()); }
                return a;
            }
            private StExpr ParseAdd()
            {
                var a = ParseMul();
                while (IsSym("+") || IsSym("-")) { string op = _t[_p++].Text; a = Bin(op, a, ParseMul()); }
                return a;
            }
            private StExpr ParseMul()
            {
                var a = ParseUnary();
                while (IsSym("*") || IsSym("/") || IsKw("MOD")) { string op = _t[_p++].Text.ToUpperInvariant(); a = Bin(op, a, ParseUnary()); }
                return a;
            }
            private StExpr ParseUnary() { Enter(); try { return ParseUnaryCore(); } finally { _depth--; } }

            private StExpr ParseUnaryCore()
            {
                if (IsKw("NOT")) { _p++; return new StUn { Not = true, A = ParseUnary() }; }
                if (IsSym("-")) { _p++; return new StUn { Not = false, A = ParseUnary() }; }
                return ParsePrimary();
            }
            private StExpr ParsePrimary()
            {
                if (Cur.Kind == "num") return new StNum { V = double.Parse(_t[_p++].Text, CultureInfo.InvariantCulture) };
                if (Cur.Kind == "time") return new StNum { V = ParseTime(_t[_p++].Text) };
                if (IsSym("(")) { _p++; var e = ParseExpr(); ExpectSym(")"); return e; }
                if (IsKw("TRUE")) { _p++; return new StNum { V = 1 }; }
                if (IsKw("FALSE")) { _p++; return new StNum { V = 0 }; }
                if (Cur.Kind == "id")
                {
                    string name = _t[_p++].Text;
                    string up = name.ToUpperInvariant();
                    if (IsSym("("))
                    {
                        if (up != "ABS" && up != "MIN" && up != "MAX" && up != "LIMIT") throw Err("unknown function " + name);
                        _p++;
                        var f = new StFn { Name = up };
                        while (!IsSym(")")) { f.Args.Add(ParseExpr()); if (IsSym(",")) _p++; else break; }
                        ExpectSym(")");
                        int need = up == "ABS" ? 1 : up == "LIMIT" ? 3 : 2;
                        if (f.Args.Count != need) throw Err(up + " needs " + need + " arguments");
                        return f;
                    }
                    var v = _prog.Find(name);
                    if (v == null) throw Err("unknown variable '" + name + "'");
                    string member = null;
                    if (IsSym("."))
                    {
                        _p++; member = ExpectId().ToUpperInvariant();
                        if (!v.IsTimer || (member != "Q" && member != "ET")) throw Err("only timers have .Q and .ET");
                    }
                    else if (v.IsTimer) throw Err("use " + name + ".Q or " + name + ".ET");
                    return new StRef { Var = v, Member = member };
                }
                throw Err("unexpected '" + Cur.Text + "'");
            }
        }
    }

    /// <summary>Runs the active program every scan on the Unity main thread. STOP is the default at startup.</summary>
    /// <summary>
    /// Runtime for the in-game PLC. Threading: the web thread may call Submit, RequestMode, Force, Release and Check
    /// (these only queue work or compile in isolation); everything else runs on the Unity main thread inside Tick.
    /// One scan = read bound inputs, apply forces, run the program body, write bound outputs. The scan period is
    /// ScanSeconds; timers advance by the real elapsed time of the previous scan.
    /// </summary>
    internal static class StRuntime
    {
        public const string SampleProgram =
@"(* Structured Text sample. Edit, press Compile, then Run.
   Addresses are the Address column of the I/O list:
   %IX n = discrete input n    %QX n = coil n    (also %IX0.3 = bit 3)
   %IW n = input register n    %QW n = holding register n
   Find real addresses in the table on the right, then replace the examples. *)
PROGRAM Main
VAR
    // Pressure AT %IW0 : INT;
    // Pump    AT %QX0 : BOOL;
    Counter : INT := 0;
    Delay   : TON;
END_VAR

Delay(IN := TRUE, PT := T#2s);
IF Delay.Q THEN
    Counter := Counter + 1;
    Delay(IN := FALSE, PT := T#2s);
END_IF;
END_PROGRAM
";

        public static bool Enabled = true;
        public static bool BlockExternalWrites = true;
        public static float ScanSeconds = 0.1f;
        public static string ProgramPath;

        public static volatile bool Running;
        public static volatile string CompileError = "";
        public static volatile string Fault = "";
        public static long ScanCount;
        public static double LastScanMs;
        public static string Source = SampleProgram;

        private static StProgram _prog;
        private static readonly object Gate = new object();
        private static string _pendingSource;
        private static int _pendingMode; // 0 none, 1 run, 2 stop
        private static float _next, _last;
        private static bool _saveNeeded;

        /// <summary>Called from any thread (web page). The main thread applies it on the next tick.</summary>
        public static void Submit(string src)
        {
            lock (Gate) { _pendingSource = src; }
        }

        public static void RequestMode(bool run) { _pendingMode = run ? 1 : 2; }

        // Forced values: lets the player (or a student) set an input from the web page without touching the game.
        // Applies to unbound variables and to bound inputs (%IX / %IW). Outputs cannot be forced.
        private static readonly ConcurrentQueue<KeyValuePair<string, double?>> ForceQueue = new ConcurrentQueue<KeyValuePair<string, double?>>();
        private static readonly Dictionary<string, double> Forces = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public static void Force(string name, double value) { ForceQueue.Enqueue(new KeyValuePair<string, double?>(name, value)); }
        public static void Release(string name) { ForceQueue.Enqueue(new KeyValuePair<string, double?>(name, null)); }

        private static void DrainForces()
        {
            KeyValuePair<string, double?> f;
            while (ForceQueue.TryDequeue(out f))
            {
                if (f.Value == null) { Forces.Remove(f.Key); continue; }
                var v = _prog != null ? _prog.Find(f.Key) : null;
                if (v == null || v.IsTimer || (v.Bound && v.IsOutput)) continue;
                Forces[f.Key] = f.Value.Value;
            }
        }

        public static void Load(string path)
        {
            ProgramPath = path;
            try
            {
                if (File.Exists(path)) Source = File.ReadAllText(path);
                else { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, Source, new UTF8Encoding(false)); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not read the ST program file: " + e.Message); }
            Install(Source);
        }

        private static void Install(string src)
        {
            string err;
            var p = StCompiler.Compile(src, out err);
            lock (Gate) { Source = src; }
            if (p == null) { CompileError = err; return; }
            CompileError = "";
            _prog = p;
            Running = false;
            Fault = "";
        }

        /// <summary>Any thread. Compiles without installing; used by the editor for live feedback.</summary>
        public static string Check(string src)
        {
            string err;
            var p = StCompiler.Compile(src ?? "", out err);
            string warn = p != null ? (Validate(p) ?? "") : "";
            return "{\"ok\":" + (p != null ? "true" : "false") + ",\"error\":\"" + Web.Esc(err) + "\",\"warning\":\"" + Web.Esc(warn) + "\"}";
        }

        private static string Validate(StProgram p)
        {
            foreach (var v in p.Vars)
            {
                if (!v.Bound) continue;
                if (v.Table == Table.Coil && !PointStore.CoilAssigned[v.Address]) return $"%QX{v.Address} (variable {v.Name}) is not an assigned coil. Check the I/O list.";
                if (v.Table == Table.HoldingRegister && !PointStore.HrAssigned[v.Address]) return $"%QW{v.Address} (variable {v.Name}) is not an assigned holding register. Check the I/O list.";
            }
            return null;
        }

        public static void Tick(float now)
        {
            string src = null;
            lock (Gate) { if (_pendingSource != null) { src = _pendingSource; _pendingSource = null; } }
            if (src != null)
            {
                Install(src);
                _saveNeeded = true;
            }
            if (_saveNeeded)
            {
                _saveNeeded = false;
                try { File.WriteAllText(ProgramPath, Source, new UTF8Encoding(false)); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not save the ST program: " + e.Message); }
            }

            DrainForces();
            int mode = _pendingMode; _pendingMode = 0;
            if (mode == 2) { Running = false; Plugin.Log.LogInfo("PLC program: STOP"); }
            if (mode == 1)
            {
                if (_prog == null) Fault = "Cannot run: the program does not compile. " + CompileError;
                else
                {
                    string why = Validate(_prog);
                    if (why != null) Fault = "Cannot run: " + why;
                    else
                    {
                        Fault = "";
                        foreach (var v in _prog.Vars)
                        {
                            v.Elapsed = 0; v.Q = false;
                            if (v.Bound && v.IsOutput) v.Value = ReadPoint(v); // start from the real device state
                        }
                        _last = now; _next = now;
                        Running = true;
                        Plugin.Log.LogInfo("PLC program: RUN");
                    }
                }
            }

            if (!Running || _prog == null || now < _next) return;
            _next = now + Mathf_Max(0.02f, ScanSeconds);
            double dt = Math.Max(0, (now - _last) * 1000.0);
            _last = now;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Scan(dt); }
            catch (Exception e)
            {
                Running = false;
                Fault = e.Message;
                Plugin.Log.LogWarning("PLC program FAULT: " + e.Message);
            }
            LastScanMs = sw.Elapsed.TotalMilliseconds;
            ScanCount++;
        }

        // Tiny local stand-in for Mathf.Max so this file has no UnityEngine dependency.
        private static float Mathf_Max(float a, float b) => a > b ? a : b;

        private static double ReadPoint(StVar v)
        {
            switch (v.Table)
            {
                case Table.DiscreteInput: return PointStore.Di[v.Address] ? 1 : 0;
                case Table.Coil: return PointStore.Coil[v.Address] ? 1 : 0;
                case Table.InputRegister: return PointStore.Ir[v.Address];
                default: return PointStore.Hr[v.Address];
            }
        }

        /// <summary>One PLC scan. Outputs are written to PointStore only when they change, and each change is queued for the game.</summary>
        private static void Scan(double dtMs)
        {
            foreach (var v in _prog.Vars)
                if (v.Bound && !v.IsOutput) v.Value = ReadPoint(v);
            foreach (var v in _prog.Vars)
            {
                double fv;
                if (!v.IsTimer && !(v.Bound && v.IsOutput) && Forces.TryGetValue(v.Name, out fv))
                    v.Value = v.IsBool ? (fv != 0 ? 1 : 0) : fv;
            }

            var ctx = new StCtx { DtMs = dtMs };
            foreach (var s in _prog.Body) s.Exec(ctx);

            foreach (var v in _prog.Vars)
            {
                if (!v.Bound || !v.IsOutput) continue;
                if (v.Table == Table.Coil)
                {
                    bool on = v.Value != 0;
                    if (PointStore.Coil[v.Address] != on)
                    {
                        PointStore.Coil[v.Address] = on;
                        Plugin.PendingWrites.Enqueue(new WriteRequest { Table = Table.Coil, Address = v.Address, Value = on ? 1 : 0 });
                    }
                }
                else
                {
                    double r = Math.Round(v.Value);
                    short raw = r > short.MaxValue ? short.MaxValue : r < short.MinValue ? short.MinValue : (short)r;
                    if (PointStore.Hr[v.Address] != raw)
                    {
                        PointStore.Hr[v.Address] = raw;
                        Plugin.PendingWrites.Enqueue(new WriteRequest { Table = Table.HoldingRegister, Address = v.Address, Value = raw });
                    }
                }
            }
        }

        /// <summary>JSON state for the web page. Built on the main thread.</summary>
        public static string StateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"running\":").Append(Running ? "true" : "false");
            sb.Append(",\"scans\":").Append(ScanCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"scanMs\":").Append(LastScanMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"blockExternal\":").Append(BlockExternalWrites ? "true" : "false");
            sb.Append(",\"prefix\":\"").Append(Web.Esc(GameAdapter.NamePrefix)).Append('"');
            sb.Append(",\"compileError\":\"").Append(Web.Esc(CompileError)).Append('"');
            sb.Append(",\"fault\":\"").Append(Web.Esc(Fault)).Append('"');
            sb.Append(",\"vars\":[");
            var p = _prog;
            if (p != null)
            {
                bool first = true;
                foreach (var v in p.Vars)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    string val = v.IsTimer ? (v.Q ? "Q=1 ET=" : "Q=0 ET=") + ((long)v.Elapsed).ToString(CultureInfo.InvariantCulture) + "ms"
                                           : v.Value.ToString("0.###", CultureInfo.InvariantCulture);
                    string at = v.Bound ? (v.IsOutput ? "%Q" : "%I") + (v.Table == Table.Coil || v.Table == Table.DiscreteInput ? "X" : "W") + v.Address : "";
                    sb.Append("{\"n\":\"").Append(Web.Esc(v.Name)).Append("\",\"t\":\"").Append(v.Type).Append("\",\"at\":\"").Append(at)
                      .Append("\",\"v\":\"").Append(Web.Esc(val)).Append(Forces.ContainsKey(v.Name) ? "\",\"f\":1}" : "\"}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
