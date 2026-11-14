using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// Reads one page (data file) of the refractiveindex.info database into an
/// <see cref="OpticalMaterial"/> (OP-12). The database is CC0; its own Python scripts are GPL and
/// are not used: this reader is written from the files and from the formula definitions
/// (database/doc/Dispersion formulas.pdf, see <see cref="RefractiveIndexInfoFormulas"/>).
/// <para>
/// <b>What a page holds.</b> REFERENCES (text, the sources), COMMENTS (renamed NOTES in the
/// database's develop branch; both are read), DATA (a list of blocks), and optionally CONDITIONS
/// and PROPERTIES (SPECS in older files). A DATA block has a <c>type</c>: "formula 1" … "formula 9"
/// with <c>wavelength_range</c> (two numbers, µm) and <c>coefficients</c>; or "tabulated n",
/// "tabulated k", "tabulated nk" with a <c>data</c> block of rows "λ n", "λ k" or "λ n k" (λ in µm).
/// </para>
/// <para>
/// <b>How blocks combine.</b> A page gives n exactly once (a formula, "tabulated n" or
/// "tabulated nk") and k at most once ("tabulated k", or the k column of "tabulated nk"). A formula
/// becomes the material's coefficients with <see cref="FormulaNumbering.RefractiveIndexInfo"/>
/// and its range; "tabulated nk" becomes a <see cref="TabulatedIndex"/>; "tabulated n" a table
/// whose k is not data (<see cref="TabulatedIndex.HasExtinction"/> false); "tabulated k" an
/// <see cref="ExtinctionTable"/>. Anything else is refused with the page and line named:
/// "tabulated n2" (a nonlinear-index page from catalog-n2.yml), an unknown type or key in a DATA
/// block, a coefficient list that stops part-way through a term, rows that do not increase in
/// wavelength, n ≤ 0 or k &lt; 0 in a table, two sources of n or of k, or no n at all.
/// </para>
/// <para>
/// <b>Reference conditions.</b> The database does not state one convention for every page:
/// whether a page's wavelengths are in vacuum or in air, and whether n is absolute or relative to
/// air, follow the page's own source (its REFERENCES and CONDITIONS). The values are used as
/// written, as a catalog index (<see cref="OpticalMaterial.RelativeIndex"/>), with no conversion.
/// CONDITIONS (temperature in K, pressure in Pa, as the database writes them) are kept in
/// <see cref="MaterialSource.Conditions"/> for display; they set no reference temperature, and a
/// page carries no dn/dT data the thermal model (OP-13) can use.
/// </para>
/// </summary>
public static class RefractiveIndexInfoReader
{
    /// <summary>The database's name as a source line gives it.</summary>
    public const string DatabaseName = "refractiveindex.info database (CC0 1.0)";

    /// <summary>
    /// Reads the page <paramref name="text"/>. <paramref name="pagePath"/> is its path in the
    /// database (e.g. "main/SiO2/nk/Malitson.yml"), used in the source reference and in errors;
    /// <paramref name="name"/> and <paramref name="catalog"/> name the material; the titles are
    /// the database catalog's, when known. Throws <see cref="InvalidDataException"/> naming the
    /// page and line for anything it does not read.
    /// </summary>
    public static OpticalMaterial Read(string text, string pagePath, string name, string catalog = "",
                                       string bookTitle = "", string pageTitle = "")
    {
        if (YamlSubset.Parse(text, pagePath) is not YamlMapping root)
            throw new InvalidDataException($"{pagePath}: not a refractiveindex.info data page (the file is empty or not a mapping).");

        string references = Text(root, "REFERENCES", pagePath);
        string comments = Text(root, "COMMENTS", pagePath);
        if (comments.Length == 0) comments = Text(root, "NOTES", pagePath);
        if (root["DATA"] is not { } dataNode)
            throw new InvalidDataException($"{pagePath}: the page has no DATA block, so it is not a data page (an about.yml or catalog file is not read here).");
        if (dataNode is not YamlSequence { Items.Count: > 0 } data)
            throw new InvalidDataException($"{pagePath} line {dataNode.Line + 1}: DATA must be a non-empty list of blocks.");

        int? formula = null;
        double[] coefficients = Array.Empty<double>();
        (double Min, double Max)? formulaRange = null;
        TabulatedIndex? table = null;
        ExtinctionTable? extinction = null;
        string? nFrom = null, kFrom = null;

        foreach (var item in data.Items)
        {
            if (item is not YamlMapping block)
                throw new InvalidDataException($"{pagePath} line {item.Line + 1}: each DATA entry must be a block with a 'type'.");
            string Where(YamlNode? n) => $"{pagePath} line {(n ?? block).Line + 1}";
            if (block["type"] is not YamlScalar { Value: { Length: > 0 } type })
                throw new InvalidDataException($"{Where(block)}: a DATA block has no 'type'.");
            foreach (var key in block.Entries.Select(e => e.Key))
                if (key is not ("type" or "coefficients" or "wavelength_range" or "data"))
                    throw new InvalidDataException($"{Where(block[key])}: the DATA key '{key}' is not read; the block is refused rather than read in part.");

            void TakeN(string what)
            {
                if (nFrom is not null)
                    throw new InvalidDataException($"{Where(block)}: the page gives n twice ({nFrom}, then {what}).");
                nFrom = what;
            }
            void TakeK(string what)
            {
                if (kFrom is not null)
                    throw new InvalidDataException($"{Where(block)}: the page gives k twice ({kFrom}, then {what}).");
                kFrom = what;
            }

            if (type.StartsWith("formula ", StringComparison.Ordinal))
            {
                string number = type["formula ".Length..].Trim();
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int f) || !RefractiveIndexInfoFormulas.IsDefined(f))
                    throw new InvalidDataException($"{Where(block["type"])}: '{type}' is not one of the database's formulas 1–9 (database/doc/Dispersion formulas.pdf).");
                TakeN(type);
                var c = Numbers(block, "coefficients", pagePath);
                if (RefractiveIndexInfoFormulas.CheckCoefficientCount(f, c.Length) is { } problem)
                    throw new InvalidDataException($"{Where(block["coefficients"])}: {problem}");
                var range = Numbers(block, "wavelength_range", pagePath);
                if (range.Length != 2 || !(range[0] > 0) || !(range[1] > range[0]))
                    throw new InvalidDataException($"{Where(block["wavelength_range"])}: wavelength_range must be two wavelengths in µm, the first positive and smaller than the second.");
                formula = f;
                coefficients = c;
                formulaRange = (range[0], range[1]);
            }
            else if (type is "tabulated nk" or "tabulated n")
            {
                bool withK = type == "tabulated nk";
                TakeN(type);
                if (withK) TakeK(type);
                var rows = Rows(block, withK ? 3 : 2, pagePath);
                table = new TabulatedIndex
                {
                    Points = rows.Select(r => new TabulatedIndexPoint(r[0], r[1], withK ? r[2] : 0)).ToArray(),
                    HasExtinction = withK
                };
                if (table.Validate() is { Count: > 0 } problems)
                    throw new InvalidDataException($"{Where(block["data"])}: {string.Join(" ", problems)}");
            }
            else if (type == "tabulated k")
            {
                TakeK(type);
                var rows = Rows(block, 2, pagePath);
                extinction = new ExtinctionTable { Points = rows.Select(r => new ExtinctionPoint(r[0], r[1])).ToArray() };
                if (extinction.Validate() is { Count: > 0 } problems)
                    throw new InvalidDataException($"{Where(block["data"])}: {string.Join(" ", problems)}");
            }
            else if (type == "tabulated n2")
            {
                throw new InvalidDataException($"{Where(block["type"])}: 'tabulated n2' is the nonlinear index n2 (a page of catalog-n2.yml), not linear n and k; it is not read as a material.");
            }
            else
            {
                throw new InvalidDataException($"{Where(block["type"])}: the DATA type '{type}' is not read.");
            }
        }
        if (nFrom is null)
            throw new InvalidDataException($"{pagePath}: the page gives no refractive index (only {kFrom}); a material needs n.");

        var properties = Flatten(root["PROPERTIES"] ?? root["SPECS"]);
        PrintedNumber? Printed(string key) =>
            properties.FirstOrDefault(p => p.Key == key).Value is { } v && PrintedNumber.TryParse(v, out var number) ? number : null;

        // A tabulated "n, k" plus nothing else keeps k in the table; "tabulated n" with
        // "tabulated k" keeps both tables; a formula keeps its own range.
        return new OpticalMaterial
        {
            Name = name,
            Catalog = catalog,
            Comment = MaterialSource.PlainText(comments),
            Numbering = FormulaNumbering.RefractiveIndexInfo,
            FormulaNumber = formula,
            Coefficients = coefficients,
            MinMicrometers = formulaRange?.Min,
            MaxMicrometers = formulaRange?.Max,
            Tabulated = table,
            Extinction = extinction,
            Nd = Printed("nd"),
            Vd = Printed("Vd"),
            Source = new MaterialSource
            {
                Database = DatabaseName,
                PagePath = pagePath,
                BookTitle = bookTitle,
                PageTitle = pageTitle,
                References = references.Trim(),
                Comments = comments.Trim(),
                Conditions = Flatten(root["CONDITIONS"]),
                Properties = properties
            }
        };
    }

    /// <summary>Reads a page from a file; the material is named after the file, and the page
    /// path is the file's path.</summary>
    public static OpticalMaterial ReadFile(string path, string? name = null, string catalog = "") =>
        Read(File.ReadAllText(path), path, name ?? Path.GetFileNameWithoutExtension(path), catalog);

    private static string Text(YamlMapping root, string key, string pagePath) => root[key] switch
    {
        null => "",
        YamlScalar s => s.Value,
        var other => throw new InvalidDataException($"{pagePath} line {other.Line + 1}: {key} must be text.")
    };

    private static double[] Numbers(YamlMapping block, string key, string pagePath)
    {
        if (block[key] is not YamlScalar { Value: var text } node)
            throw new InvalidDataException($"{pagePath} line {block.Line + 1}: the block has no '{key}'.");
        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = new double[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                throw new InvalidDataException($"{pagePath} line {node.Line + 1}: '{fields[i]}' in {key} is not a number.");
        return values;
    }

    private static List<double[]> Rows(YamlMapping block, int columns, string pagePath)
    {
        if (block["data"] is not YamlScalar { Value: var text } node)
            throw new InvalidDataException($"{pagePath} line {block.Line + 1}: the tabulated block has no 'data'.");
        var rows = new List<double[]>();
        int lineNo = node.Line + 1;                          // the data block's first row is the line after "data: |"
        foreach (string raw in text.Split('\n'))
        {
            lineNo++;
            var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields.Length != columns)
                throw new InvalidDataException($"{pagePath} line {lineNo}: a row of {columns} numbers was expected, found {fields.Length} ('{raw.Trim()}').");
            var row = new double[columns];
            for (int i = 0; i < columns; i++)
                if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out row[i]) || !double.IsFinite(row[i]))
                    throw new InvalidDataException($"{pagePath} line {lineNo}: '{fields[i]}' is not a number.");
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A CONDITIONS or PROPERTIES block flattened: "key" or "parent.key" (a list item adds
    /// its 1-based position, "density.1.value") to the scalar text.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Flatten(YamlNode? node)
    {
        var list = new List<KeyValuePair<string, string>>();
        void Walk(YamlNode n, string prefix)
        {
            switch (n)
            {
                case YamlScalar s:
                    list.Add(new(prefix, s.Value.Trim()));
                    break;
                case YamlMapping m:
                    foreach (var e in m.Entries) Walk(e.Value, prefix.Length == 0 ? e.Key : $"{prefix}.{e.Key}");
                    break;
                case YamlSequence q:
                    for (int i = 0; i < q.Items.Count; i++)
                        Walk(q.Items[i], prefix.Length == 0 ? (i + 1).ToString(CultureInfo.InvariantCulture) : $"{prefix}.{i + 1}");
                    break;
            }
        }
        if (node is not null) Walk(node, "");
        return list;
    }
}
