namespace OpenSim.Geometry.Step.Part21;

/// <summary>
/// Recursive-descent parser for ISO 10303-21 exchange structures. Pass 1 of the STEP
/// importer: purely syntactic — it builds the instance table and captures the header,
/// attaching no entity semantics, so it is testable with tiny in-memory strings.
/// Complex (multi-type) instances <c>#5=(A(…) B(…) C(…));</c> are first-class.
/// </summary>
public sealed class Part21Parser
{
    private readonly StepTokenizer _tz;
    private StepToken _tok;

    private Part21Parser(string source)
    {
        _tz = new StepTokenizer(source);
        _tok = _tz.Next();
    }

    /// <summary>Parses STEP text into a <see cref="StepFile"/>.</summary>
    public static StepFile Parse(string source) => new Part21Parser(source).ParseFile();

    /// <summary>Reads and parses a STEP file from disk.</summary>
    public static StepFile ParseFile(string path) => Parse(File.ReadAllText(path));

    private StepFile ParseFile()
    {
        ExpectKeyword("ISO-10303-21");
        Expect(StepTokenKind.Semicolon);

        ExpectKeyword("HEADER");
        Expect(StepTokenKind.Semicolon);
        var header = ParseHeaderRecords();

        var instances = new SortedDictionary<int, StepInstance>();
        while (true)
        {
            if (_tok.Kind != StepTokenKind.Keyword)
                throw new StepParseException(_tok.Line, $"expected a section keyword, found {_tok}");

            if (_tok.Text == "END-ISO-10303-21")
            {
                Advance();
                Expect(StepTokenKind.Semicolon);
                break;
            }
            if (_tok.Text != "DATA")
                throw new StepParseException(_tok.Line,
                    $"unsupported section '{_tok.Text}' (only DATA sections are supported)");
            Advance();
            if (_tok.Kind == StepTokenKind.LParen) SkipBalancedParens(); // ed. 3 DATA(…) parameters
            Expect(StepTokenKind.Semicolon);
            ParseDataSection(instances);
        }

        string? schema = null, name = null, system = null;
        foreach (var rec in header)
        {
            switch (rec.Keyword)
            {
                case "FILE_SCHEMA":
                    // FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }'));
                    if (rec.Args.Count > 0 && rec.Args[0] is StepValue.ValueList { Items.Count: > 0 } schemas
                        && schemas.Items[0] is StepValue.Text s)
                        schema = s.Value.Split('{')[0].Trim();
                    break;
                case "FILE_NAME":
                    if (rec.Args.Count > 0 && rec.Args[0] is StepValue.Text n) name = n.Value;
                    if (rec.Args.Count > 5 && rec.Args[5] is StepValue.Text os) system = os.Value;
                    break;
            }
        }
        return new StepFile(schema, name, system, instances);
    }

    private List<StepRecord> ParseHeaderRecords()
    {
        var records = new List<StepRecord>();
        while (!(_tok.Kind == StepTokenKind.Keyword && _tok.Text == "ENDSEC"))
        {
            if (_tok.Kind != StepTokenKind.Keyword)
                throw new StepParseException(_tok.Line, $"expected a header entity, found {_tok}");
            string keyword = _tok.Text;
            Advance();
            records.Add(new StepRecord(keyword, ParseArgList()));
            Expect(StepTokenKind.Semicolon);
        }
        Advance(); // ENDSEC
        Expect(StepTokenKind.Semicolon);
        return records;
    }

    private void ParseDataSection(SortedDictionary<int, StepInstance> instances)
    {
        while (!(_tok.Kind == StepTokenKind.Keyword && _tok.Text == "ENDSEC"))
        {
            if (_tok.Kind != StepTokenKind.Hash)
                throw new StepParseException(_tok.Line, $"expected '#id =' or ENDSEC, found {_tok}");
            int line = _tok.Line;
            long id = _tok.Integer;
            if (id > int.MaxValue)
                throw new StepParseException(line, $"instance id #{id} exceeds the supported range");
            Advance();
            Expect(StepTokenKind.Equals);

            var records = new List<StepRecord>(1);
            if (_tok.Kind == StepTokenKind.Keyword)
            {
                records.Add(ParseRecord());
            }
            else if (_tok.Kind == StepTokenKind.LParen)
            {
                Advance();
                while (_tok.Kind == StepTokenKind.Keyword) records.Add(ParseRecord());
                if (records.Count == 0)
                    throw new StepParseException(_tok.Line, "empty complex instance");
                Expect(StepTokenKind.RParen);
            }
            else
            {
                throw new StepParseException(_tok.Line,
                    $"expected an entity record after '#{id} =', found {_tok}");
            }
            Expect(StepTokenKind.Semicolon);

            if (instances.TryGetValue((int)id, out var existing))
                throw new StepParseException(line,
                    $"duplicate instance #{id} (first defined at line {existing.Line})");
            instances.Add((int)id, new StepInstance((int)id, records, line));
        }
        Advance(); // ENDSEC
        Expect(StepTokenKind.Semicolon);
    }

    private StepRecord ParseRecord()
    {
        string keyword = _tok.Text;
        Advance();
        return new StepRecord(keyword, ParseArgList());
    }

    private IReadOnlyList<StepValue> ParseArgList()
    {
        Expect(StepTokenKind.LParen);
        var args = new List<StepValue>();
        if (_tok.Kind != StepTokenKind.RParen)
        {
            args.Add(ParseValue());
            while (_tok.Kind == StepTokenKind.Comma)
            {
                Advance();
                args.Add(ParseValue());
            }
        }
        Expect(StepTokenKind.RParen);
        return args;
    }

    private StepValue ParseValue()
    {
        switch (_tok.Kind)
        {
            case StepTokenKind.Dollar: Advance(); return StepValue.Null.Instance;
            case StepTokenKind.Star: Advance(); return StepValue.Derived.Instance;
            case StepTokenKind.Integer: { var v = new StepValue.Integer(_tok.Integer); Advance(); return v; }
            case StepTokenKind.Real: { var v = new StepValue.Real(_tok.Real); Advance(); return v; }
            case StepTokenKind.Text: { var v = new StepValue.Text(_tok.Text); Advance(); return v; }
            case StepTokenKind.Enumeration: { var v = new StepValue.Enumeration(_tok.Text); Advance(); return v; }
            case StepTokenKind.Hash:
            {
                if (_tok.Integer > int.MaxValue)
                    throw new StepParseException(_tok.Line, $"reference #{_tok.Integer} exceeds the supported range");
                var v = new StepValue.Reference((int)_tok.Integer);
                Advance();
                return v;
            }
            case StepTokenKind.LParen:
            {
                Advance();
                var items = new List<StepValue>();
                if (_tok.Kind != StepTokenKind.RParen)
                {
                    items.Add(ParseValue());
                    while (_tok.Kind == StepTokenKind.Comma)
                    {
                        Advance();
                        items.Add(ParseValue());
                    }
                }
                Expect(StepTokenKind.RParen);
                return new StepValue.ValueList(items);
            }
            case StepTokenKind.Keyword:
            {
                // Typed parameter, e.g. LENGTH_MEASURE(25.4).
                string keyword = _tok.Text;
                Advance();
                return new StepValue.Typed(keyword, ParseArgList());
            }
            default:
                throw new StepParseException(_tok.Line, $"expected a parameter value, found {_tok}");
        }
    }

    private void SkipBalancedParens()
    {
        int depth = 0;
        do
        {
            if (_tok.Kind == StepTokenKind.LParen) depth++;
            else if (_tok.Kind == StepTokenKind.RParen) depth--;
            else if (_tok.Kind == StepTokenKind.EndOfFile)
                throw new StepParseException(_tok.Line, "unbalanced parentheses");
            Advance();
        } while (depth > 0);
    }

    private void Advance() => _tok = _tz.Next();

    private void Expect(StepTokenKind kind)
    {
        if (_tok.Kind != kind)
            throw new StepParseException(_tok.Line,
                $"expected {new StepToken(kind, "", 0, 0, 0)}, found {_tok}");
        Advance();
    }

    private void ExpectKeyword(string keyword)
    {
        if (_tok.Kind != StepTokenKind.Keyword || _tok.Text != keyword)
            throw new StepParseException(_tok.Line, $"expected '{keyword}', found {_tok}");
        Advance();
    }
}
