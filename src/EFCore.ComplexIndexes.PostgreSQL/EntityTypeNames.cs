using System.Text;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Converts a CLR <see cref="Type.FullName"/> into the name EF Core gives the entity type of that
/// type: <c>Ns.Room`1[[System.Int32, …]]</c> to <c>Ns.Room&lt;int&gt;</c>, by EF Core's own rule
/// (<c>SharedTypeExtensions.DisplayName(fullName: true)</c>, EF Core 10): keywords for the built-in
/// types, <c>T?</c> for <c>Nullable&lt;T&gt;</c>, each generic segment of a nested type with its own
/// arguments, array ranks as written.
/// </summary>
/// <remarks>
/// Temporal foreign keys store their principal as the CLR FullName. A snapshot's property-bag entity
/// types have no CLR type to compare it with, only EF Core's name, so a generic principal did not
/// resolve there and every diff from the snapshot threw (#197). Stored names cannot change, since
/// snapshots hold them, so the reader converts.
/// </remarks>
internal static class EntityTypeNames
{
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = "bool",   ["System.Byte"]   = "byte",   ["System.Char"]   = "char",
        ["System.Decimal"] = "decimal", ["System.Double"] = "double", ["System.Single"] = "float",
        ["System.Int32"]   = "int",    ["System.Int64"]  = "long",   ["System.Object"] = "object",
        ["System.SByte"]   = "sbyte",  ["System.Int16"]  = "short",  ["System.String"] = "string",
        ["System.UInt32"]  = "uint",   ["System.UInt64"] = "ulong",  ["System.UInt16"] = "ushort",
        ["System.Void"]    = "void"
    };

    /// <summary>The entity-type name for a CLR FullName, or the input unchanged when it is not one.</summary>
    public static string FromClrFullName(string fullName)
    {
        try
        {
            var position = 0;
            var name     = ReadType(fullName, ref position);
            return position == fullName.Length ? name : fullName;
        }
        catch (ArgumentOutOfRangeException)
        {
            return fullName;
        }
    }

    // One type: its name, its assembly-qualified generic arguments ("[[A, asm],[B, asm]]") and its
    // array ranks ("[]", "[,]").
    private static string ReadType(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && text[position] is not ('[' or ',' or ']'))
            position++;

        var name      = text[start..position];
        var arguments = new List<string>();

        if (position + 1 < text.Length && text[position] == '[' && text[position + 1] == '[')
        {
            position++;
            while (true)
            {
                position++;                                   // the argument's '['
                arguments.Add(ReadType(text, ref position));
                position = text.IndexOf(']', position) + 1;   // past its assembly name
                if (text[position] == ']')
                {
                    position++;
                    break;
                }

                position++;                                   // the ',' between arguments
            }
        }

        var ranks = new StringBuilder();
        while (position + 1 < text.Length && text[position] == '[' && text[position + 1] is ']' or ',')
        {
            var close = text.IndexOf(']', position);
            ranks.Append(text, position, close - position + 1);
            position = close + 1;
        }

        return Display(name, arguments) + ranks;
    }

    private static string Display(string name, List<string> arguments)
    {
        if (arguments.Count == 0)
            return BuiltIn.GetValueOrDefault(name, name);

        if (name == "System.Nullable`1")
            return arguments[0] + "?";

        var display = new StringBuilder();
        var offset  = 0;
        foreach (var segment in name.Split('+'))
        {
            if (display.Length > 0)
                display.Append('+');

            var tick = segment.IndexOf('`');
            if (tick < 0)
            {
                display.Append(segment);
                continue;
            }

            var arity = int.Parse(segment[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            display.Append(segment, 0, tick)
                   .Append('<')
                   .AppendJoin(", ", arguments.Skip(offset).Take(arity))
                   .Append('>');
            offset += arity;
        }

        return display.ToString();
    }
}
