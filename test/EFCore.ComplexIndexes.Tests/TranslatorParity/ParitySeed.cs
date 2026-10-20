namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

/// <summary>
/// The rows the live sweep runs both renderings against: every nullable operand both null and set,
/// strings with braces, quotes, tabs and edge spaces, values on each side of the samples. No char is
/// left at its default: PostgreSQL text and jsonb cannot hold U+0000.
/// </summary>
internal static class ParitySeed
{
    private static readonly DateTime Day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<ParityRow> Rows()
    {
        yield return new ParityRow
        {
            Id = 1, Str = "{it's}", Str2 = "x", Int = 5, Long = 5, Short = 5, Byte = 5, Dec = 5.5m, Dbl = 5.5, Flt = 5.5f,
            Bool = true, Chr = 'A', Guid = Guid.Parse("00000000-0000-0000-0000-000000000005"), Date = Day,
            NStr = "{it's}", NStr2 = "x", NInt = 5, NLong = 5, NShort = 5, NByte = 5, NDec = 5.5m, NDbl = 5.5, NFlt = 5.5f,
            NBool = true, NChr = 'A', NGuid = Guid.Empty, NDate = Day,
            Doc = new ParityDoc { Str = "{it's}", Int = 5, Long = 5, Short = 5, Dec = 5.5m, Dbl = 5.5, Bool = true, Chr = 'A', Date = Day, NStr = "a", NInt = 5, NBool = true },
            Email = new ParityEmail { Value = "{it's}" },
        };

        yield return new ParityRow
        {
            Id = 2, Str = "", Str2 = "", Int = 0, Long = 0, Short = 0, Byte = 0, Dec = 0, Dbl = 0, Flt = 0,
            Bool = false, Chr = 'a', Date = Day.AddDays(1),
            Doc = new ParityDoc { Str = "", Chr = 'a', Date = Day.AddDays(1) },
            Email = new ParityEmail { Value = "" },
        };

        yield return new ParityRow
        {
            Id = 3, Str = " a\t", Str2 = "-", Int = -1, Long = 6, Short = 4, Byte = 6, Dec = 6m, Dbl = 4.5, Flt = 6f,
            Bool = true, Chr = 'B', Date = Day.AddDays(-1),
            NStr = "a-b", NInt = 6, NLong = 4, NDec = 4.5m, NBool = false, NChr = 'B', NDate = Day.AddDays(1),
            Doc = new ParityDoc { Str = "x", Int = 6, Long = 4, Short = 6, Dec = 4.5m, Dbl = 6, Bool = false, Chr = 'B', Date = Day.AddDays(-1), NStr = "a-b", NInt = 4, NBool = false },
            Email = new ParityEmail { Value = "B" },
        };

        yield return new ParityRow
        {
            Id = 4, Str = "abcdef", Str2 = "{it's}", Int = 6, Long = 4, Short = 6, Byte = 4, Dec = 4.5m, Dbl = 6, Flt = 4.5f,
            Bool = false, Chr = 'Z', Date = Day,
            NStr = "", NStr2 = "", NInt = 4, NShort = 6, NByte = 4, NDbl = 6, NFlt = 4.5f, NBool = true, NChr = 'a',
            Doc = new ParityDoc { Str = "abcdef", Int = 4, Chr = 'Z', Date = Day, NStr = "{it's}", NBool = true },
            Email = new ParityEmail { Value = "abcdef" },
        };

        yield return new ParityRow
        {
            Id = 5, Str = "x", Str2 = "y", Int = 5, Chr = 'A', Date = Day,
            NStr2 = "y", NDbl = 5.5, NDec = 5.5m,
            Doc = new ParityDoc { Str = " {it's} ", Chr = ' ', Date = Day, NStr = "z", NInt = 6, NBool = false },
            Email = new ParityEmail { Value = "x" },
        };
    }
}
