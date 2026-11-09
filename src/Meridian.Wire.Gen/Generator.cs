namespace Meridian.Wire.Gen;

/// <summary>Entry point used by the tool and by the "generated code is up to date" test.</summary>
public static class Generator
{
    /// <summary>Parses <paramref name="schemaText"/> and returns the generated C# file contents.</summary>
    /// <exception cref="InvalidDataException">The schema is invalid; the message has the line number.</exception>
    public static string Generate(string schemaText)
    {
        try
        {
            return Emitter.Emit(SchemaParser.Parse(schemaText));
        }
        catch (SchemaException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }
}
