using System.Globalization;
using System.Text.RegularExpressions;

namespace Meridian.Wire.Gen;

/// <summary>A field type: a primitive, a struct reference, or a bounded list/string/bytes.</summary>
internal sealed record FieldType(string Kind, string? Element = null, int Max = 0);

internal sealed record Field(string Name, FieldType Type, int Line);

internal sealed record Definition(
    string Name,
    bool IsMessage,
    ushort Id,
    string Direction,
    string Delivery,
    IReadOnlyList<Field> Fields,
    int Line);

internal sealed record Schema(IReadOnlyList<Definition> Definitions, ulong Hash);

/// <summary>Thrown with a line number when the schema file is invalid.</summary>
internal sealed class SchemaException(string message) : Exception(message);

/// <summary>Parses the small line-oriented schema language described in messages.wire.</summary>
internal static partial class SchemaParser
{
    public static readonly IReadOnlySet<string> Primitives = new HashSet<string>(StringComparer.Ordinal)
    {
        "u8", "u16", "u32", "u64", "i16", "i32", "i64", "bool", "varu32", "vari32",
    };

    private static readonly Dictionary<string, string> Directions = new(StringComparer.Ordinal)
    {
        ["server_bound"] = "ServerBound",
        ["client_bound"] = "ClientBound",
        ["internal"] = "Internal",
        ["handshake"] = "Handshake",
    };

    private static readonly Dictionary<string, string> Deliveries = new(StringComparer.Ordinal)
    {
        ["unreliable_sequenced"] = "UnreliableSequenced",
        ["reliable_ordered"] = "ReliableOrdered",
        ["reliable_unordered"] = "ReliableUnordered",
    };

    public static Schema Parse(string text)
    {
        var defs = new List<Definition>();
        string? name = null;
        var isMessage = false;
        ushort id = 0;
        string direction = "", delivery = "";
        var fields = new List<Field>();
        var startLine = 0;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (name is null)
            {
                startLine = lineNo;
                fields = [];
                if (parts is ["struct", var structName])
                {
                    name = RequireIdentifier(structName, lineNo, pascal: true);
                    isMessage = false;
                }
                else if (parts is ["message", var idText, var msgName, var dir, var del])
                {
                    if (!ushort.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    {
                        throw new SchemaException($"line {lineNo}: message id '{idText}' is not a u16.");
                    }
                    name = RequireIdentifier(msgName, lineNo, pascal: true);
                    direction = Directions.GetValueOrDefault(dir) ?? throw new SchemaException($"line {lineNo}: unknown direction '{dir}'.");
                    delivery = Deliveries.GetValueOrDefault(del) ?? throw new SchemaException($"line {lineNo}: unknown delivery '{del}'.");
                    isMessage = true;
                }
                else
                {
                    throw new SchemaException($"line {lineNo}: expected 'struct <Name>' or 'message <id> <Name> <direction> <delivery>'.");
                }
                continue;
            }

            if (parts is ["end"])
            {
                defs.Add(new Definition(name, isMessage, id, direction, delivery, fields, startLine));
                name = null;
                continue;
            }

            if (parts is not [var typeText, var fieldName])
            {
                throw new SchemaException($"line {lineNo}: expected '<type> <name>' or 'end'.");
            }
            var fname = RequireIdentifier(fieldName, lineNo, pascal: false);
            if (fields.Any(f => f.Name == fname))
            {
                throw new SchemaException($"line {lineNo}: duplicate field '{fname}' in {name}.");
            }
            fields.Add(new Field(fname, ParseType(typeText, lineNo), lineNo));
        }

        if (name is not null)
        {
            throw new SchemaException($"line {startLine}: '{name}' is missing 'end'.");
        }

        Validate(defs);
        return new Schema(defs, ComputeHash(defs));
    }

    private static FieldType ParseType(string text, int line)
    {
        if (Primitives.Contains(text))
        {
            return new FieldType(text);
        }
        var bounded = BoundedType().Match(text);
        if (bounded.Success)
        {
            var max = int.Parse(bounded.Groups["max"].Value, CultureInfo.InvariantCulture);
            if (max <= 0)
            {
                throw new SchemaException($"line {line}: limit must be positive.");
            }
            var kind = bounded.Groups["kind"].Value;
            var element = bounded.Groups["elem"].Success ? bounded.Groups["elem"].Value : null;
            if (kind == "list" && element is null)
            {
                throw new SchemaException($"line {line}: list needs an element type, e.g. list<u32>(16).");
            }
            if (kind != "list" && element is not null)
            {
                throw new SchemaException($"line {line}: only list takes an element type.");
            }
            return new FieldType(kind, element, max);
        }
        if (Identifier().IsMatch(text) && char.IsUpper(text[0]))
        {
            return new FieldType("struct", text);
        }
        throw new SchemaException($"line {line}: unknown type '{text}'.");
    }

    private static void Validate(List<Definition> defs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<ushort>();
        foreach (var d in defs)
        {
            if (!names.Add(d.Name))
            {
                throw new SchemaException($"line {d.Line}: '{d.Name}' is defined twice.");
            }
            if (d.IsMessage && !ids.Add(d.Id))
            {
                throw new SchemaException($"line {d.Line}: message id {d.Id} is used twice.");
            }
        }

        var structs = defs.Where(d => !d.IsMessage).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            foreach (var f in d.Fields)
            {
                var referenced = f.Type.Kind == "struct" ? f.Type.Element
                    : f.Type.Kind == "list" && !Primitives.Contains(f.Type.Element!) ? f.Type.Element
                    : null;
                if (referenced is null)
                {
                    continue;
                }
                if (!structs.Contains(referenced))
                {
                    throw new SchemaException($"line {f.Line}: '{referenced}' is not a struct.");
                }
                if (!defined.Contains(referenced))
                {
                    // Also rules out recursive structs, which would make sizes unbounded.
                    throw new SchemaException($"line {f.Line}: struct '{referenced}' must be defined before it is used.");
                }
            }
            defined.Add(d.Name);
        }
    }

    /// <summary>FNV-1a over a canonical rendering, so comments and spacing never change the hash.</summary>
    private static ulong ComputeHash(List<Definition> defs)
    {
        var canonical = string.Join('\n', defs.Select(d =>
            $"{(d.IsMessage ? "message" : "struct")} {d.Id} {d.Name} {d.Direction} {d.Delivery}:" +
            string.Join(',', d.Fields.Select(f => $"{f.Type.Kind}<{f.Type.Element}>({f.Type.Max}) {f.Name}"))));
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(canonical))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }

    private static string RequireIdentifier(string text, int line, bool pascal)
    {
        if (!Identifier().IsMatch(text) || (pascal ? !char.IsUpper(text[0]) : !char.IsLower(text[0])))
        {
            var style = pascal ? "PascalCase" : "camelCase";
            throw new SchemaException($"line {line}: '{text}' must be a {style} identifier.");
        }
        return text;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(?<kind>string|bytes|list)(<(?<elem>[A-Za-z][A-Za-z0-9]*)>)?\((?<max>[0-9]+)\)$")]
    private static partial Regex BoundedType();
}
