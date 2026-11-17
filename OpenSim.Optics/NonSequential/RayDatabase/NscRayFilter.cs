using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.RayDatabase;

/// <summary>
/// What the local-coordinate flags of a filter need (X_XGT, X_LGT, X_AXYG and the like): each
/// object's placement in the scene frame (1-based object number) and the lens unit the
/// coordinate thresholds are given in.
/// </summary>
public sealed record NscRayFilterFrame(IReadOnlyList<RigidTransform> Placements, LensUnit Unit)
{
    /// <summary>The frame of <paramref name="scene"/>.</summary>
    public static NscRayFilterFrame For(NscScene scene, LensUnit unit) => new(scene.GlobalPlacements(), unit);

    internal RigidTransform Placement(int objectNumber, string flag)
    {
        if (objectNumber < 1 || objectNumber > Placements.Count)
            throw new ArgumentException($"Filter flag {flag}: object {objectNumber} is not in the scene (1 to {Placements.Count}).");
        return Placements[objectNumber - 1];
    }
}

/// <summary>
/// OpticStudio's named filters (help, "Named Filters"): each line "filtername; filterstring",
/// for example "MyFilter; H5 &amp; H6 &amp; M9"; the name may then be used "instead of the explicit
/// string wherever filter strings are used". Names are matched without regard to case; a name
/// may not look like a filter flag (H5, Z, X_HIT, SEQ, O0, ...). A named filter may use other
/// named filters; a cycle is refused.
/// </summary>
public sealed class NscNamedFilters
{
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, string Filter)> _list = new();

    public static NscNamedFilters Empty { get; } = new();

    public IReadOnlyList<(string Name, string Filter)> Filters => _list;

    /// <summary>The named filters of <paramref name="lines"/> ("name; filter"; blank lines skipped).
    /// Throws <see cref="ArgumentException"/> naming the line for a malformed one.</summary>
    public static NscNamedFilters Parse(IEnumerable<string>? lines)
    {
        var r = new NscNamedFilters();
        if (lines is null) return r;
        int k = 0;
        foreach (var raw in lines)
        {
            k++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            int semi = raw.IndexOf(';');
            if (semi < 0)
                throw new ArgumentException($"Named filter line {k} (\"{raw}\"): the syntax is \"filtername; filterstring\" (help, \"Named Filters\").");
            string name = raw[..semi].Trim(), filter = raw[(semi + 1)..].Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new ArgumentException($"Named filter line {k}: the name \"{name}\" must be letters, digits and underscores, starting with a letter or underscore.");
            if (LooksLikeFlag(name))
                throw new ArgumentException($"Named filter line {k}: the name \"{name}\" reads as a filter flag; choose another name.");
            if (filter.Length == 0)
                throw new ArgumentException($"Named filter line {k} (\"{name}\"): the filter string is empty.");
            if (!r._byName.TryAdd(name, filter))
                throw new ArgumentException($"Named filter line {k}: \"{name}\" is defined twice.");
            r._list.Add((name, filter));
        }
        return r;
    }

    internal bool TryGet(string name, out string filter) => _byName.TryGetValue(name, out filter!);

    internal static bool LooksLikeFlag(string name) =>
        Regex.IsMatch(name, @"^([BDEFGHJLMORSTW]\d+|Z|X_.*|SEQ|_\d*)$", RegexOptions.IgnoreCase);
}

/// <summary>
/// A filter string (plan item OP-83; help, "The Filter String", "Filter String Examples";
/// docs/Optics-Conventions.md §75): flags combined with &amp; (and), | (or), ^ (exclusive or), !
/// (not) and parentheses, optionally led by {#n} or {#n1-n2} to keep only the first n, or the
/// n1-th to n2-th, ray trees that pass. Each flag is evaluated on one <b>branch</b> of a ray tree
/// (the segments from the source to one terminating segment, the viewer's "Expand into
/// Branches"); a tree passes when one of its branches passes, and a segment passes when it lies on
/// a passing branch. A filter and its <see cref="Not"/> therefore split the branches of every
/// tree into two disjoint sets that cover them all.
/// <para>
/// The help does not state the precedence of &amp;, | and ^, so a filter that mixes them without
/// parentheses is refused ("H1 &amp; H2 | H3" must be written "(H1 &amp; H2) | H3"). Built flags:
/// B, D, E, F, G, H, L, M, O, R, S, T, W, Z, the path filters ~ and $, and the X_ flags listed in
/// §75. Refused by name: J (its detector-only zeroing is not built), _n and {@n} (Path Analysis,
/// OP-84), SEQ[a:b] (Sequence Selector, OP-84), X_DIFF and diffraction orders in X_SEQ (OP-82),
/// X_EXT and X_ORD (birefringence). X_WAVESHIFT is built (OP-81).
/// </para>
/// </summary>
public sealed class NscRayFilter
{
    private readonly Node? _root;

    /// <summary>The filter as given.</summary>
    public string Text { get; }

    /// <summary>The filter with every named filter written out in parentheses.</summary>
    public string Expanded { get; }

    /// <summary>{#n1-n2}: the 1-based range of passing trees kept; null for all.</summary>
    public (long From, long To)? TreeRange { get; }

    /// <summary>True when a flag needs the scene's placements (<see cref="NscRayFilterFrame"/>).</summary>
    public bool NeedsFrame { get; }

    private NscRayFilter(string text, string expanded, Node? root, (long, long)? range, bool needsFrame)
    {
        Text = text; Expanded = expanded; _root = root; TreeRange = range; NeedsFrame = needsFrame;
    }

    /// <summary>The filter <paramref name="text"/>, named filters from <paramref name="named"/>;
    /// null for a blank string ("If blank, all rays are drawn"). Throws
    /// <see cref="ArgumentException"/> naming the position and the problem.</summary>
    public static NscRayFilter? Parse(string? text, NscNamedFilters? named = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = new Parser(text, named ?? NscNamedFilters.Empty);
        return p.ParseFilter();
    }

    /// <summary>The complement: passes exactly the branches <paramref name="filter"/> does not.
    /// Refused for a filter with a {#n} tree range, whose complement is not a filter.</summary>
    public static NscRayFilter Not(NscRayFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.TreeRange is not null)
            throw new ArgumentException($"Filter \"{filter.Text}\": a filter with a {{#n}} ray range has no complement filter.");
        return new NscRayFilter("!(" + filter.Text + ")", "!(" + filter.Expanded + ")", new NotNode(filter._root!), null, filter.NeedsFrame);
    }

    // ================================================================== evaluation

    /// <summary>The branches of <paramref name="segments"/>: for each terminating segment (one
    /// with no child), the segment numbers from 0 to it, in tree order.</summary>
    public static IReadOnlyList<int[]> Branches(IReadOnlyList<NscSegment> segments)
    {
        var hasChild = new bool[segments.Count];
        for (int i = 1; i < segments.Count; i++)
            if (segments[i].Parent >= 0) hasChild[segments[i].Parent] = true;
        var branches = new List<int[]>();
        var path = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (hasChild[i]) continue;
            path.Clear();
            for (int k = i; k >= 0; k = segments[k].Parent)
            {
                path.Add(k);
                if (path.Count > segments.Count)
                    throw new InvalidDataException($"Segment {i}'s parent chain does not reach segment 0.");
            }
            path.Reverse();
            branches.Add(path.ToArray());
        }
        return branches;
    }

    /// <summary>Whether one branch passes (the {#n} range is not applied here).</summary>
    public bool BranchPasses(NscRecordedRay ray, int[] branch, NscRayDatabaseRun run, NscRayFilterFrame? frame = null)
    {
        if (_root is null) return true;
        if (NeedsFrame && frame is null)
            throw new ArgumentException($"Filter \"{Text}\": its local-coordinate flags need the scene's object placements.");
        return _root.Eval(new Ctx(ray, branch, run, frame));
    }

    /// <summary>Whether any branch of the tree passes (the {#n} range is not applied here).</summary>
    public bool TreePasses(NscRecordedRay ray, NscRayDatabaseRun run, NscRayFilterFrame? frame = null)
    {
        if (_root is null) return true;
        foreach (var b in Branches(ray.Segments))
            if (BranchPasses(ray, b, run, frame)) return true;
        return false;
    }

    /// <summary>The segments of each ray that lie on a passing branch, in database order, with the
    /// {#n} range applied to the passing trees in that order; rays with no passing branch are
    /// left out. A null filter passes everything.</summary>
    public static IEnumerable<(NscRecordedRay Ray, bool[] Segments)> Select(NscRayDatabase database, NscRayFilter? filter,
        NscRayFilterFrame? frame = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        long passed = 0;
        foreach (var ray in database.Rays)
        {
            var keep = new bool[ray.Segments.Count];
            bool any = false;
            if (filter?._root is null)
            {
                Array.Fill(keep, true);
                any = true;
            }
            else
            {
                var run = database.Runs[ray.Run];
                foreach (var b in Branches(ray.Segments))
                    if (filter.BranchPasses(ray, b, run, frame))
                    {
                        any = true;
                        foreach (int s in b) keep[s] = true;
                    }
            }
            if (!any) continue;
            passed++;
            if (filter?.TreeRange is { } range)
            {
                if (passed < range.From) continue;
                if (passed > range.To) yield break;
            }
            yield return (ray, keep);
        }
    }

    // ================================================================== the tree

    private sealed class Ctx
    {
        public readonly NscRecordedRay Ray;
        public readonly IReadOnlyList<NscSegment> S;
        public readonly int[] B;
        public readonly NscRayDatabaseRun Run;
        public readonly NscRayFilterFrame? Frame;

        public Ctx(NscRecordedRay ray, int[] branch, NscRayDatabaseRun run, NscRayFilterFrame? frame)
        {
            Ray = ray; S = ray.Segments; B = branch; Run = run; Frame = frame;
        }

        /// <summary>The branch's segments after the source segment, with the one before each.</summary>
        public IEnumerable<(NscSegment Seg, NscSegment Prev, bool Last)> Steps()
        {
            for (int i = 1; i < B.Length; i++) yield return (S[B[i]], S[B[i - 1]], i == B.Length - 1);
        }
    }

    private abstract class Node
    {
        public abstract bool Eval(Ctx c);
    }

    private sealed class NotNode(Node inner) : Node
    {
        public override bool Eval(Ctx c) => !inner.Eval(c);
    }

    private sealed class BinaryNode(char op, List<Node> items) : Node
    {
        public override bool Eval(Ctx c)
        {
            switch (op)
            {
                case '&':
                    foreach (var n in items) if (!n.Eval(c)) return false;
                    return true;
                case '|':
                    foreach (var n in items) if (n.Eval(c)) return true;
                    return false;
                default:   // '^': true when an odd number are true (pairwise, left to right)
                    bool v = items[0].Eval(c);
                    for (int i = 1; i < items.Count; i++) v ^= items[i].Eval(c);
                    return v;
            }
        }
    }

    private sealed class FlagNode(Func<Ctx, bool> test) : Node
    {
        public override bool Eval(Ctx c) => test(c);
    }

    // ================================================================== parsing

    private sealed class Parser
    {
        private string _s;
        private int _i;
        private readonly string _text;
        private readonly NscNamedFilters _named;
        private readonly Stack<string> _expanding = new();
        private bool _needsFrame;
        private readonly StringBuilder _expanded = new();

        public Parser(string text, NscNamedFilters named)
        {
            _text = text; _s = text; _named = named;
        }

        private ArgumentException Fail(string message) =>
            new($"Filter \"{_text}\" at character {_i + 1}: {message}");

        private void Ws()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private bool At(char ch)
        {
            Ws();
            return _i < _s.Length && _s[_i] == ch;
        }

        public NscRayFilter ParseFilter()
        {
            (long, long)? range = null;
            Ws();
            if (At('{'))
            {
                _i++;
                Ws();
                if (At('@'))
                    throw Fail("{@n} (the first n rays per path per core) works only with Path Analysis data (PAF files), which is plan item OP-84; not built.");
                if (!At('#')) throw Fail("expected {#n} or {#n1-n2}.");
                _i++;
                long a = Integer("the ray count"), b = a;
                if (At('-'))
                {
                    _i++;
                    b = Integer("the last ray");
                    if (b < a) throw Fail($"in {{#{a}-{b}}} the last ray must not come before the first.");
                }
                else a = 1;
                if (a < 1) throw Fail("the rays of {#n1-n2} are counted from 1.");
                if (!At('}')) throw Fail("expected '}'.");
                _i++;
                range = (a, b);
            }
            Ws();
            Node? root = null;
            if (_i < _s.Length)
            {
                root = Expr();
                Ws();
                if (_i < _s.Length) throw Fail($"unexpected '{_s[_i]}' (a missing operator or an unmatched ')').");
            }
            string expanded = (range is { } r ? $"{{#{r.Item1}-{r.Item2}}}" : "") + _expanded;
            if (range is null && root is null) throw Fail("the filter is empty.");
            return new NscRayFilter(_text, expanded, root, range, _needsFrame);
        }

        private Node Expr()
        {
            var items = new List<Node> { Unary() };
            char? op = null;
            while (true)
            {
                Ws();
                if (_i >= _s.Length || _s[_i] is not ('&' or '|' or '^')) break;
                char o = _s[_i];
                if (op is char prev && prev != o)
                    throw Fail($"'{prev}' and '{o}' are mixed without parentheses; the help does not define their precedence, so group them with parentheses.");
                op = o;
                _i++;
                _expanded.Append(' ').Append(o).Append(' ');
                items.Add(Unary());
            }
            return items.Count == 1 ? items[0] : new BinaryNode(op!.Value, items);
        }

        private Node Unary()
        {
            Ws();
            if (At('!'))
            {
                _i++;
                _expanded.Append('!');
                return new NotNode(Unary());
            }
            return Primary();
        }

        private Node Primary()
        {
            Ws();
            if (_i >= _s.Length) throw Fail("a flag is missing at the end.");
            char ch = _s[_i];
            if (ch == '(')
            {
                _i++;
                _expanded.Append('(');
                var inner = Expr();
                if (!At(')')) throw Fail("expected ')'.");
                _i++;
                _expanded.Append(')');
                return inner;
            }
            if (ch is '~' or '$') return PathFilter(ch == '~' ? 3 : 2);
            if (ch == '_') throw Fail("filters _n are defined by Path Analysis (plan item OP-84); not built.");
            if (!(char.IsLetter(ch))) throw Fail($"'{ch}' cannot start a flag.");
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            string word = _s[start.._i];
            if (!NscNamedFilters.LooksLikeFlag(word) && _named.TryGet(word, out var definition))
                return Named(word, definition);
            if (word.StartsWith("X_", StringComparison.OrdinalIgnoreCase)) return Extended(word.ToUpperInvariant());
            string upper = word.ToUpperInvariant();
            if (upper == "SEQ") throw Fail("SEQ[a:b] selects sequences of the Sequence Selector (plan item OP-84); not built.");
            if (upper == "Z")
            {
                _expanded.Append('Z');
                return new FlagNode(c => c.B.Any(k => c.S[k].Events.HasFlag(NscEvents.Error)));
            }
            var m = Regex.Match(upper, @"^([A-Z])(\d+)$");
            if (!m.Success) throw Fail($"\"{word}\" is neither a filter flag nor a named filter.");
            char flag = m.Groups[1].Value[0];
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int? face = null;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                int fs = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i == fs) throw Fail($"a face number must follow \"{word}.\".");
                face = int.Parse(_s[fs.._i], CultureInfo.InvariantCulture);
            }
            string shown = $"{flag}{n}{(face is int f0 ? "." + f0 : "")}";
            _expanded.Append(shown);
            return SimpleFlag(flag, n, face, shown);
        }

        private Node Named(string name, string definition)
        {
            if (_expanding.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw Fail($"the named filter \"{name}\" refers to itself (through {string.Join(" → ", _expanding.Reverse())}).");
            _expanding.Push(name);
            var (saveS, saveI) = (_s, _i);
            _s = definition;
            _i = 0;
            _expanded.Append('(');
            Node inner;
            try
            {
                Ws();
                if (At('{')) throw Fail($"the named filter \"{name}\" starts with a {{#n}} range, which may only lead the whole filter.");
                inner = Expr();
                Ws();
                if (_i < _s.Length) throw Fail($"in the named filter \"{name}\": unexpected '{_s[_i]}'.");
            }
            finally
            {
                _s = saveS;
                _i = saveI;
                _expanding.Pop();
            }
            _expanded.Append(')');
            return inner;
        }

        private long Integer(string what)
        {
            Ws();
            int start = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i == start || !long.TryParse(_s[start.._i], NumberStyles.None, CultureInfo.InvariantCulture, out long v))
                throw Fail($"{what} must be a whole number.");
            return v;
        }

        // ---------------------------------------------------------------- single-letter flags

        private Node SimpleFlag(char flag, int n, int? face, string shown)
        {
            void NeedObject()
            {
                if (n < 1) throw Fail($"{shown}: the object number must be 1 or more (the help gives no meaning to 0 for {flag}).");
            }
            void NoFace()
            {
                if (face is not null) throw Fail($"{shown}: the {flag} flag takes no face number.");
            }
            bool Match(NscSegment s) => s.Hit == n && (face is not int f || s.Face == f);
            bool MatchAny(NscSegment s) => n == 0 ? s.Hit > 0 : Match(s);
            switch (flag)
            {
                case 'H':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg)));
                case 'M':
                    NeedObject();
                    return new FlagNode(c => !c.Steps().Any(t => Match(t.Seg)));
                case 'L':
                    NeedObject();
                    return new FlagNode(c => c.B.Length > 1 && Match(c.S[c.B[^1]]));
                case 'R':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg) && t.Seg.Events.HasFlag(NscEvents.Reflected)));
                case 'T':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg) && t.Seg.Events.HasFlag(NscEvents.Transmitted)));
                case 'S':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg) && t.Seg.Events.HasFlag(NscEvents.Scattered)));
                case 'D':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg) && t.Seg.Events.HasFlag(NscEvents.Diffracted)));
                case 'E':
                    NeedObject();
                    return new FlagNode(c => c.Steps().Any(t => t.Seg.Events.HasFlag(NscEvents.DiffractedFromParent) && Match(t.Prev)));
                case 'F':
                    return new FlagNode(c => c.Steps().Any(t => t.Seg.Events.HasFlag(NscEvents.ScatteredFromParent) && MatchAny(t.Prev)));
                case 'G':
                    return new FlagNode(c => c.Steps().Any(t => t.Seg.Events.HasFlag(NscEvents.Ghost) && MatchAny(t.Prev)));
                case 'B':
                    NoFace();
                    return new FlagNode(c => c.Steps().Any(t => t.Seg.Events.HasFlag(NscEvents.BulkScattered) && (n == 0 || t.Seg.Inside == n)));
                case 'O':
                    NoFace();
                    return new FlagNode(c => n == 0 || c.Ray.SourceObject == n);
                case 'W':
                    NoFace();
                    return new FlagNode(c => n == 0 || c.Run.WavelengthNumber == n);
                case 'J':
                    throw Fail($"{shown}: the J flag's zeroing of the segments before the ghost point is not built; use G{n}{(face is int f1 ? "." + f1 : "")} to select the same rays.");
                default:
                    throw Fail($"\"{shown}\": {flag} is not a filter flag (help, \"The Filter String\").");
            }
        }

        // ---------------------------------------------------------------- path filters

        private Node PathFilter(int digits)
        {
            char mark = _s[_i++];
            int start = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            string code = _s[start.._i];
            bool open = _i < _s.Length && _s[_i] == '#';
            if (open) _i++;
            if (code.Length == 0 || code.Length % digits != 0)
                throw Fail($"the path filter {mark}{code} needs {digits}-digit object numbers.");
            var objects = Enumerable.Range(0, code.Length / digits)
                .Select(k => int.Parse(code.Substring(k * digits, digits), CultureInfo.InvariantCulture)).ToArray();
            if (objects.Length > 50) throw Fail($"a path filter may list at most 50 objects; {mark}{code} lists {objects.Length}.");
            _expanded.Append(mark).Append(code).Append(open ? "#" : "");
            return new FlagNode(c =>
            {
                var path = new List<int> { c.Ray.SourceObject };
                foreach (var (seg, _, _) in c.Steps())
                    if (seg.Hit > 0 && path[^1] != seg.Hit) path.Add(seg.Hit);
                if (open ? path.Count < objects.Length : path.Count != objects.Length) return false;
                for (int k = 0; k < objects.Length; k++) if (path[k] != objects[k]) return false;
                return true;
            });
        }

        // ---------------------------------------------------------------- X_ flags

        private Node Extended(string name)
        {
            Ws();
            if (!At('(')) throw Fail($"{name} needs its arguments in parentheses.");
            _i++;
            int close = _s.IndexOf(')', _i);
            if (close < 0) throw Fail($"{name}: the ')' is missing.");
            string body = _s[_i..close];
            _i = close + 1;
            var args = body.Split(',').Select(a => a.Trim()).ToArray();
            _expanded.Append(name).Append('(').Append(string.Join(",", args)).Append(')');
            string shown = $"{name}({body})";

            void Count(int k)
            {
                if (args.Length != k || args.Any(a => a.Length == 0))
                    throw Fail($"{shown} needs {k} argument{(k == 1 ? "" : "s")} separated by commas (help, \"The Filter String\").");
            }
            (int N, int? F) Obj(string a, bool zeroAny = false)
            {
                var m = Regex.Match(a, @"^(\d+)(?:\.(\d+))?$");
                if (!m.Success) throw Fail($"{shown}: \"{a}\" is not an object number or object.face.");
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (n < 1 && !zeroAny) throw Fail($"{shown}: the object number must be 1 or more.");
                return (n, m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null);
            }
            double Num(string a)
            {
                if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                    throw Fail($"{shown}: \"{a}\" is not a number.");
                return v;
            }
            int Int(string a)
            {
                if (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
                    throw Fail($"{shown}: \"{a}\" must be a whole number, 0 or more.");
                return v;
            }
            static bool Match(NscSegment s, (int N, int? F) o) => s.Hit == o.N && (o.F is not int f || s.Face == f);
            static bool Range(int value, int n1, int n2) => n2 == 0 ? value >= n1 : value >= n1 && value <= n2;

            switch (name)
            {
                case "X_DIFF":
                    throw Fail($"{shown}: diffraction orders are not recorded until diffraction is built (plan item OP-82).");
                case "X_EXT":
                case "X_ORD":
                    throw Fail($"{shown}: ordinary and extraordinary rays need birefringent objects, which are not built.");
                case "X_WAVESHIFT":
                {
                    // OP-81: "Ray has wave shifted during a bulk scatter event from wavelength i to
                    // wavelength j": a B segment at wavelength i followed by its child at j.
                    Count(2);
                    int from = Int(args[0]), to = Int(args[1]);
                    static int Number(Ctx c, NscSegment g) => g.WavelengthNumber ?? c.Run.WavelengthNumber;
                    return new FlagNode(c => from != to && c.Steps().Any(t => t.Prev.Events.HasFlag(NscEvents.BulkScattered)
                        && Number(c, t.Prev) == from && Number(c, t.Seg) == to));
                }

                case "X_HIT":
                case "X_HITFACE2":
                {
                    (int N, int? F) o;
                    int b;
                    if (name == "X_HIT") { Count(2); o = Obj(args[0]); b = Int(args[1]); }
                    else { Count(3); o = (Obj(args[0]).N, Int(args[1])); b = Int(args[2]); }
                    return new FlagNode(c => c.Steps().Count(t => Match(t.Seg, o)) == b);
                }
                case "X_HITFACE":
                {
                    Count(2);
                    var o = (Obj(args[0]).N, (int?)Int(args[1]));
                    return new FlagNode(c => c.Steps().Any(t => Match(t.Seg, o)));
                }
                case "X_HITS":
                {
                    Count(2);
                    int n1 = Int(args[0]), n2 = Int(args[1]);
                    return new FlagNode(c => Range(c.Steps().Count(t => t.Seg.Hit > 0), n1, n2));
                }
                case "X_SEGMENTS":
                {
                    Count(2);
                    int n1 = Int(args[0]), n2 = Int(args[1]);
                    return new FlagNode(c => Range(c.B.Length - 1, n1, n2));
                }
                case "X_GHOST":
                {
                    Count(2);
                    var o = Obj(args[0], zeroAny: true);
                    int b = Int(args[1]);
                    return new FlagNode(c => GhostEvents(c, null) == b && (o.N == 0 || c.Steps().Any(t => Match(t.Seg, o))));
                }
                case "X_GHOSTN":
                {
                    Count(3);
                    var o = Obj(args[0]);
                    int n1 = Int(args[1]), n2 = Int(args[2]);
                    return new FlagNode(c =>
                    {
                        int k = GhostEvents(c, o);
                        return n2 == 0 ? k == n1 : k >= n1 && k <= n2;
                    });
                }
                case "X_TIR":
                {
                    Count(2);
                    var o = Obj(args[0]);
                    int v = Int(args[1]);
                    return new FlagNode(c => c.Steps().Count(t => Match(t.Seg, o) && t.Seg.Events.HasFlag(NscEvents.TotalInternalReflection)) == v);
                }
                case "X_SCATTER":
                {
                    Count(2);
                    var o = Obj(args[0], zeroAny: true);
                    int b = Int(args[1]);
                    return new FlagNode(c => c.Steps().Count(t => t.Seg.Events.HasFlag(NscEvents.ScatteredFromParent)) == b
                        && (o.N == 0 || c.Steps().Any(t => Match(t.Seg, o))));
                }
                case "X_SCATTERF":
                {
                    Count(2);
                    var o = Obj(args[0]);
                    int b = Int(args[1]);
                    if (b < 1) throw Fail($"{shown}: the hit count must be 1 or more.");
                    return new FlagNode(c =>
                    {
                        int hits = 0;
                        for (int i = 1; i < c.B.Length; i++)
                            if (Match(c.S[c.B[i]], o) && ++hits == b)
                                return i + 1 < c.B.Length && c.S[c.B[i + 1]].Events.HasFlag(NscEvents.ScatteredFromParent);
                        return false;
                    });
                }
                case "X_NDIF":
                {
                    Count(1);
                    int k = Int(args[0]);
                    return new FlagNode(c => c.Steps().Count(t => t.Seg.Events.HasFlag(NscEvents.Diffracted)) == k);
                }
                case "X_IAGT":
                case "X_IALT":
                case "X_IRGT":
                case "X_IRLT":
                {
                    Count(2);
                    var o = Obj(args[0]);
                    double v = Num(args[1]);
                    bool relative = name[3] == 'R', greater = name.EndsWith("GT", StringComparison.Ordinal);
                    return new FlagNode(c =>
                    {
                        double i0 = c.S[0].Intensity;
                        return c.Steps().Any(t => Match(t.Seg, o) && Compare(relative ? t.Seg.Intensity / i0 : t.Seg.Intensity, v, greater));
                    });
                }
                case "X_WAVERANGE":
                {
                    Count(3);
                    var o = Obj(args[0]);
                    double a = Num(args[1]), b = Num(args[2]);
                    return new FlagNode(c => c.Run.AirMicrometers >= a && c.Run.AirMicrometers <= b && c.Steps().Any(t => Match(t.Seg, o)));
                }
                case "X_SEQ":
                {
                    if (args.Length < 1 || args.Any(a => a.Length == 0)) throw Fail($"{shown} needs object or object.face entries separated by commas.");
                    if (body.Contains('/')) throw Fail($"{shown}: diffraction orders in X_SEQ are not recorded until diffraction is built (plan item OP-82).");
                    var seq = args.Select(a => Obj(a)).ToArray();
                    return new FlagNode(c =>
                    {
                        var steps = new List<(int N, int F)> { (c.Ray.SourceObject, 0) };
                        foreach (var (seg, _, _) in c.Steps()) if (seg.Hit > 0) steps.Add((seg.Hit, seg.Face));
                        if (steps.Count != seq.Length) return false;
                        for (int k = 0; k < seq.Length; k++)
                            if (steps[k].N != seq[k].N || (seq[k].F is int f && steps[k].F != f)) return false;
                        return true;
                    });
                }
            }

            // ---- local coordinates, direction cosines and angles at object n
            var local = Regex.Match(name, @"^X_([XYZLMN])(GT|LT)$");
            var angle = Regex.Match(name, @"^X_A(XY|XZ|YZ)(G|L)$");
            if (local.Success || angle.Success)
            {
                Count(2);
                var o = Obj(args[0]);
                double v = Num(args[1]);
                _needsFrame = true;
                bool greater = local.Success ? local.Groups[2].Value == "GT" : angle.Groups[2].Value == "G";
                Func<Vector3D, Vector3D, double> measure;
                if (local.Success)
                {
                    char axis = local.Groups[1].Value[0];
                    measure = axis switch
                    {
                        'X' => (p, _) => p.X,
                        'Y' => (p, _) => p.Y,
                        'Z' => (p, _) => p.Z,
                        'L' => (_, d) => d.X,
                        'M' => (_, d) => d.Y,
                        _ => (_, d) => d.Z,
                    };
                }
                else
                {
                    // The angle of the direction's projection on the plane from the reference axis
                    // (+y for x-y, +z for x-z and y-z), "without regard to the direction of
                    // propagation": atan2(|other|, |reference|), 0° to 90° (§75).
                    measure = angle.Groups[1].Value switch
                    {
                        "XY" => (_, d) => Math.Atan2(Math.Abs(d.X), Math.Abs(d.Y)) * 180 / Math.PI,
                        "XZ" => (_, d) => Math.Atan2(Math.Abs(d.X), Math.Abs(d.Z)) * 180 / Math.PI,
                        _ => (_, d) => Math.Atan2(Math.Abs(d.Y), Math.Abs(d.Z)) * 180 / Math.PI,
                    };
                }
                bool position = local.Success && local.Groups[1].Value[0] is 'X' or 'Y' or 'Z';
                return new FlagNode(c =>
                {
                    var t = c.Frame!.Placement(o.N, shown);
                    foreach (var (seg, _, _) in c.Steps())
                    {
                        if (!Match(seg, o)) continue;
                        var p = t.InverseApply(seg.Position);
                        var d = t.InverseApplyDirection(seg.Direction);
                        double value = measure(p, d);
                        if (position) value = OpticsConventions.FromMeters(value, c.Frame.Unit);
                        if (Compare(value, v, greater)) return true;
                    }
                    return false;
                });
            }
            throw Fail($"\"{name}\" is not an extended flag of the filter string (help, \"The Filter String\").");
        }

        private static bool Compare(double value, double threshold, bool greater) => greater ? value > threshold : value < threshold;

        /// <summary>Ghost events on the branch: segments marked G (whose parent's hit matches
        /// <paramref name="at"/>, when given) plus total internal reflections (at
        /// <paramref name="at"/>) that the branch continues from (the help: "rays which TIR from
        /// refractive surfaces are considered ghosts").</summary>
        private static int GhostEvents(Ctx c, (int N, int? F)? at)
        {
            bool M(NscSegment s) => at is not { } o || (s.Hit == o.N && (o.F is not int f || s.Face == f));
            int k = 0;
            foreach (var (seg, prev, last) in c.Steps())
            {
                if (seg.Events.HasFlag(NscEvents.Ghost) && M(prev)) k++;
                if (!last && seg.Events.HasFlag(NscEvents.TotalInternalReflection) && M(seg)) k++;
            }
            return k;
        }
    }
}
