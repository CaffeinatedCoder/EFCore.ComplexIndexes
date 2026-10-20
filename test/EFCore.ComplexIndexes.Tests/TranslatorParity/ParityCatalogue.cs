using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;

namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

internal enum ParityKind { Filter, Expression }

/// <summary>
/// One row of the sweep: a lambda in C# source, the captured variables it closes over, and the
/// constructs it exercises (for the coverage check).
/// </summary>
internal sealed record ParityCase(ParityKind Kind, string Lambda, string Captures, IReadOnlySet<string> Constructs)
{
    /// <summary>The row's identity in the waiver file: kind, lambda and captures, as written.</summary>
    public string Id => $"{(Kind == ParityKind.Filter ? "filter" : "expression")}: {Lambda}{(Captures.Length == 0 ? "" : "  with " + Captures)}";
}

/// <summary>
/// Builds the sweep's catalogue: every construct <see cref="NpgsqlLinqIndexTranslator"/> accepts, read
/// from its own tables, applied to every operand shape <see cref="ParityRow"/> has, plus the
/// constructs EF Core translates that the translator does not claim, which must be refused at the
/// declaration. Rows are C# source text; the compiler drops the ones that are not C#
/// (<c>x.Bool &lt; true</c>) and the rest are compiled into the exact trees a consumer's lambda has.
/// </summary>
internal static class ParityCatalogue
{
    private sealed record Operand(string Path, Type Type, bool Nullable, string Shape);

    private sealed record Value(string Text, Type Type, bool Nullable, string Captures, IReadOnlySet<string> Constructs, bool Identity);

    // C# spellings of the translator's operators. A new operator in its tables fails here, loudly,
    // until it has a spelling, rather than going unswept.
    private static readonly Dictionary<ExpressionType, string> CSharpOperators = new()
    {
        [ExpressionType.Equal]              = "==",
        [ExpressionType.NotEqual]           = "!=",
        [ExpressionType.LessThan]           = "<",
        [ExpressionType.LessThanOrEqual]    = "<=",
        [ExpressionType.GreaterThan]        = ">",
        [ExpressionType.GreaterThanOrEqual] = ">=",
        [ExpressionType.AndAlso]            = "&&",
        [ExpressionType.OrElse]             = "||",
        [ExpressionType.Not]                = "!",
        [ExpressionType.Add]                = "+",
        [ExpressionType.Coalesce]           = "??",
    };

    // A sample of each type, in C#. Every literal type the translator accepts needs one; Guid and
    // DateTime are here so the sweep shows they are refused at the declaration. The string has a
    // brace and a quote, which templates escape and filters must not (#70).
    private static readonly Dictionary<Type, string> Samples = new()
    {
        [typeof(string)]   = "\"{it's}\"",
        [typeof(bool)]     = "true",
        [typeof(char)]     = "'A'",
        [typeof(int)]      = "5",
        [typeof(long)]     = "5L",
        [typeof(short)]    = "(short)5",
        [typeof(byte)]     = "(byte)5",
        [typeof(sbyte)]    = "(sbyte)5",
        [typeof(uint)]     = "5u",
        [typeof(ulong)]    = "5ul",
        [typeof(ushort)]   = "(ushort)5",
        [typeof(decimal)]  = "5.5m",
        [typeof(double)]   = "5.5",
        [typeof(float)]    = "5.5f",
        [typeof(Guid)]     = "new Guid(\"00000000-0000-0000-0000-000000000005\")",
        [typeof(DateTime)] = "new DateTime(2026, 1, 1)",
    };

    // Constructs EF Core translates that the translator does not claim. Each must be refused at the
    // declaration, or render as EF renders it; failing only at `migrations add` is the bug.
    private static readonly (string Name, Func<Type, bool> AppliesTo, string Template, Func<Type, Type> Result)[] NearMisses =
    [
        ("HasValue",           IsNullableValue,                     "{0}.HasValue",               _ => typeof(bool)),
        ("Value",              IsNullableValue,                     "{0}.Value",                  Underlying),
        ("GetValueOrDefault",  IsNullableValue,                     "{0}.GetValueOrDefault()",    Underlying),
        ("StartsWith",         t => t == typeof(string),            "{0}.StartsWith(\"a\")",      _ => typeof(bool)),
        ("EndsWith",           t => t == typeof(string),            "{0}.EndsWith(\"a\")",        _ => typeof(bool)),
        ("Contains",           t => t == typeof(string),            "{0}.Contains(\"a\")",        _ => typeof(bool)),
        ("IndexOf",            t => t == typeof(string),            "{0}.IndexOf(\"a\")",         _ => typeof(int)),
        ("PadLeft",            t => t == typeof(string),            "{0}.PadLeft(4)",             _ => typeof(string)),
        ("Trim(char)",         t => t == typeof(string),            "{0}.Trim('x')",              _ => typeof(string)),
        ("Replace(char,char)", t => t == typeof(string),            "{0}.Replace('a', 'b')",      _ => typeof(string)),
        ("ToLowerInvariant",   t => t == typeof(string),            "{0}.ToLowerInvariant()",     _ => typeof(string)),
        ("IsNullOrEmpty",      t => t == typeof(string),            "string.IsNullOrEmpty({0})",  _ => typeof(bool)),
        ("ToString",           t => t != typeof(string),            "{0}.ToString()",             _ => typeof(string)),
        ("arithmetic",         IsNumeric,                           "{0} + 1",                    Promoted),
        ("negation",           IsNumeric,                           "-{0}",                       Promoted),
        ("Math.Abs",           t => t == typeof(int) || t == typeof(decimal), "Math.Abs({0})",    Underlying),
        ("conditional",        t => t == typeof(bool),              "({0} ? \"y\" : \"n\")",      _ => typeof(string)),
        ("DateTime.Year",      t => t == typeof(DateTime),          "{0}.Year",                   _ => typeof(int)),
    ];

    public static IReadOnlyList<ParityCase> Build()
    {
        var operands = Operands().ToList();
        var cases    = new List<ParityCase>();

        // Every value construct over every operand it applies to, as an expression index.
        var values = operands.SelectMany(Values).ToList();
        foreach (var value in values)
            cases.Add(new ParityCase(ParityKind.Expression, "x => " + value.Text, value.Captures, value.Constructs));

        // Every literal type the translator inlines, as a captured value in a concatenation, and a
        // captured null, which C# concatenates as an empty string.
        foreach (var type in NpgsqlLinqIndexTranslator.LiteralTypes)
            cases.Add(new ParityCase(ParityKind.Expression, "x => x.Str + c", $"{Name(type)} c = {Sample(type)};",
                                     Set($"literal:{type.Name}", $"operator:{ExpressionType.Add}")));
        cases.Add(new ParityCase(ParityKind.Expression, "x => x.Str + c", "string? c = null;", Set($"operator:{ExpressionType.Add}")));

        // Every comparison over every operand, against each kind of right-hand side.
        var atoms = new List<ParityCase>();
        foreach (var value in values)
            atoms.AddRange(Comparisons(value, operands));
        cases.AddRange(atoms);

        // Negation over each comparison with a constant, which is where null compensation moves.
        foreach (var atom in atoms.Where(a => a.Captures.Length == 0 && !a.Lambda.Contains(" null")))
            cases.Add(Negate(atom));

        // Boolean operands and boolean constructs as predicates on their own.
        foreach (var value in values.Where(v => v.Type == typeof(bool)))
        {
            cases.Add(new ParityCase(ParityKind.Filter, "x => " + value.Text, value.Captures, value.Constructs));
            cases.Add(new ParityCase(ParityKind.Filter, $"x => !{Parenthesize(value.Text)}", value.Captures,
                                     Union(value.Constructs, Set($"operator:{ExpressionType.Not}"))));
        }

        // Boolean structure over a few atoms with and without nullable operands.
        string[] logicalAtoms =
        [
            "x.Int == 5", "x.NInt == 5", "x.NStr != \"a\"", "x.Bool", "x.NBool == true", "x.Doc.NInt > 5", "x.Str.Trim() == \"a\"",
        ];

        foreach (var left in logicalAtoms)
        foreach (var right in logicalAtoms.Where(r => r != left))
        foreach (var op in NpgsqlLinqIndexTranslator.LogicalOperators.Where(o => o != ExpressionType.Not))
        {
            var lambda = $"{left} {CSharp(op)} {right}";
            cases.Add(new ParityCase(ParityKind.Filter, "x => " + lambda, "", Set($"operator:{op}")));
            cases.Add(new ParityCase(ParityKind.Filter, $"x => !({lambda})", "", Set($"operator:{op}", $"operator:{ExpressionType.Not}")));
        }

        return cases.DistinctBy(c => c.Id).ToList();
    }

    /// <summary>Every construct the translator's tables accept, by the key the cases record.</summary>
    public static IEnumerable<string> AcceptedConstructs()
        => NpgsqlLinqIndexTranslator.StringMethods.Keys.Select(MethodKey)
                                    .Concat(NpgsqlLinqIndexTranslator.ValueMembers.Keys.Select(k => MemberKey(k.DeclaringType, k.Name)))
                                    .Concat(NpgsqlLinqIndexTranslator.ValueOperators.Select(o => $"operator:{o}"))
                                    .Concat(NpgsqlLinqIndexTranslator.ComparisonOperators.Keys.Select(o => $"operator:{o}"))
                                    .Concat(NpgsqlLinqIndexTranslator.LogicalOperators.Select(o => $"operator:{o}"))
                                    .Concat(NpgsqlLinqIndexTranslator.LiteralTypes.Select(t => $"literal:{t.Name}"));

    // ── Operands ──

    private static IEnumerable<Operand> Operands()
    {
        var nullability = new NullabilityInfoContext();

        foreach (var property in typeof(ParityRow).GetProperties().Where(p => IsScalar(p.PropertyType) && p.Name != nameof(ParityRow.Id)))
            yield return new Operand($"x.{property.Name}", property.PropertyType, IsNullable(property), IsNullable(property) ? "nullable" : "column");

        foreach (var property in typeof(ParityDoc).GetProperties())
            yield return new Operand($"x.Doc.{property.Name}", property.PropertyType, IsNullable(property), IsNullable(property) ? "json nullable" : "json");

        yield return new Operand("x.Email.Value", typeof(string), false, "converter");

        bool IsNullable(PropertyInfo property)
            => nullability.Create(property).ReadState != NullabilityState.NotNull;
    }

    // The value constructs over one operand: the operand itself, every table entry that applies to
    // its type, and the near misses. Str2 and NStr2 exist as right-hand sides only.
    private static IEnumerable<Value> Values(Operand operand)
    {
        var identity = new Value(operand.Path, operand.Type, operand.Nullable, "", Set($"shape:{operand.Shape}"), Identity: true);
        yield return identity;

        if (operand.Path.EndsWith('2'))
            yield break;

        foreach (var (method, _) in NpgsqlLinqIndexTranslator.StringMethods)
        {
            if (method.DeclaringType != operand.Type)
                continue;

            var arguments = method.GetParameters().Select((p, i) => ArgumentSample(p.ParameterType, i)).ToList();
            yield return Call(arguments, "");

            // A captured argument is inlined as a literal; a computed start becomes (start + 1).
            if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(int))
                yield return Call(["c", .. arguments.Skip(1)], "int c = 1;");

            Value Call(IEnumerable<string> args, string captures)
                => new($"{operand.Path}.{method.Name}({string.Join(", ", args)})", method.ReturnType, operand.Nullable, captures,
                       Set($"shape:{operand.Shape}", MethodKey(method)), Identity: false);
        }

        foreach (var (declaringType, name) in NpgsqlLinqIndexTranslator.ValueMembers.Keys)
        {
            var closed = declaringType.IsGenericTypeDefinition && operand.Type.IsConstructedGenericType
                      && operand.Type.GetGenericTypeDefinition() == declaringType
                             ? operand.Type
                             : declaringType;
            if (closed != operand.Type)
                continue;

            var member = closed.GetProperty(name)!;
            yield return new Value($"{operand.Path}.{name}", member.PropertyType, operand.Nullable && !IsNullableValue(operand.Type), "",
                                   Set($"shape:{operand.Shape}", MemberKey(declaringType, name)), Identity: false);
        }

        foreach (var op in NpgsqlLinqIndexTranslator.ValueOperators)
        {
            switch (op)
            {
                case ExpressionType.Add when operand.Type == typeof(string):
                    foreach (var (text, nullable) in new[]
                             {
                                 ($"{operand.Path} + \"x\"", operand.Nullable),
                                 ($"\"x\" + {operand.Path}", operand.Nullable),
                                 ($"{operand.Path} + x.Str2", operand.Nullable),
                                 ($"{operand.Path} + x.NStr2", true),
                                 ($"{operand.Path} + x.Int", operand.Nullable),
                                 ($"{operand.Path} + x.NInt", true),
                                 ($"{operand.Path} + x.Chr", operand.Nullable),
                             })
                        yield return new Value(text, typeof(string), nullable, "", Set($"shape:{operand.Shape}", $"operator:{op}"), Identity: false);
                    break;

                case ExpressionType.Coalesce when operand.Nullable:
                    var underlying = Nullable.GetUnderlyingType(operand.Type) ?? operand.Type;
                    yield return new Value($"({operand.Path} ?? {Sample(underlying)})", underlying, false, "",
                                           Set($"shape:{operand.Shape}", $"operator:{op}"), Identity: false);
                    if (underlying == typeof(string))
                        yield return new Value($"({operand.Path} ?? x.NStr2)", typeof(string), true, "",
                                               Set($"shape:{operand.Shape}", $"operator:{op}"), Identity: false);
                    break;
            }
        }

        foreach (var (name, _, template, result) in NearMisses.Where(n => n.AppliesTo(operand.Type)))
            yield return new Value(string.Format(CultureInfo.InvariantCulture, template, operand.Path), result(operand.Type), false, "",
                                   Set($"shape:{operand.Shape}", $"near-miss:{name}"), Identity: false);
    }

    // ── Comparisons ──

    private static IEnumerable<ParityCase> Comparisons(Value value, IReadOnlyList<Operand> operands)
    {
        var type = Underlying(value.Type);

        foreach (var op in NpgsqlLinqIndexTranslator.ComparisonOperators.Keys)
        {
            var constructs = Union(value.Constructs, Set($"operator:{op}"));
            var symbol     = CSharp(op);

            if (Samples.TryGetValue(type, out var sample))
            {
                yield return Case($"{value.Text} {symbol} {sample}", value.Captures);

                if (!value.Identity)
                    continue;

                yield return Case($"{sample} {symbol} {value.Text}", "");
                yield return Case($"{value.Text} {symbol} c", $"{Name(type)} c = {sample};", $"literal:{type.Name}");
            }
            else if (!value.Identity)
                continue;

            if (value.Nullable && op is ExpressionType.Equal or ExpressionType.NotEqual)
            {
                yield return Case($"{value.Text} {symbol} null", "");
                yield return Case($"{value.Text} {symbol} c", $"{Name(type)}? c = null;");
            }

            // Against another column of the same type, nullable and not.
            foreach (var other in operands.Where(o => o.Path != value.Text && !o.Path.StartsWith("x.Doc.", StringComparison.Ordinal)
                                                   && o.Shape is "column" or "nullable"
                                                   && (Nullable.GetUnderlyingType(o.Type) ?? o.Type) == type))
                yield return Case($"{value.Text} {symbol} {other.Path}", "");

            ParityCase Case(string lambda, string captures, params string[] extra)
                => new(ParityKind.Filter, "x => " + lambda, captures, Union(constructs, Set(extra)));
        }
    }

    private static ParityCase Negate(ParityCase atom)
        => atom with { Lambda = $"x => !({atom.Lambda["x => ".Length..]})", Constructs = Union(atom.Constructs, Set($"operator:{ExpressionType.Not}")) };

    // ── Helpers ──

    private static string ArgumentSample(Type type, int position) => type switch
    {
        _ when type == typeof(int)    => position == 0 ? "1" : "3",
        _ when type == typeof(string) => position == 0 ? "\"-\"" : "\"\"",
        _ => throw new InvalidOperationException($"No argument sample for {type.Name}; add one so the method is swept."),
    };

    private static string Sample(Type type)
        => Samples.TryGetValue(type, out var sample)
               ? sample
               : throw new InvalidOperationException($"No sample for {type.Name}; add one so the literal type is swept.");

    private static string CSharp(ExpressionType op)
        => CSharpOperators.TryGetValue(op, out var symbol)
               ? symbol
               : throw new InvalidOperationException($"No C# spelling for ExpressionType.{op}; add one so the operator is swept.");

    private static string Name(Type type) => type.FullName!;

    private static string Parenthesize(string text) => text.Contains(' ') ? $"({text})" : text;

    public static string MethodKey(MethodInfo method)
        => $"method:{method.DeclaringType!.Name}.{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})";

    public static string MemberKey(Type declaringType, string name) => $"member:{declaringType.Name}.{name}";

    private static bool IsScalar(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type) is var t && (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)
                                                                   || t == typeof(Guid) || t == typeof(DateTime));

    private static bool IsNullableValue(Type type) => Nullable.GetUnderlyingType(type) is not null;

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    // C# arithmetic on a short or byte is int arithmetic.
    private static Type Promoted(Type type)
        => Underlying(type) is var t && (t == typeof(short) || t == typeof(byte)) ? typeof(int) : t;

    private static bool IsNumeric(Type type)
        => Underlying(type) is var t
        && (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(decimal) || t == typeof(double));

    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items);

    private static IReadOnlySet<string> Union(IReadOnlySet<string> left, IReadOnlySet<string> right) => new HashSet<string>(left.Concat(right));
}
