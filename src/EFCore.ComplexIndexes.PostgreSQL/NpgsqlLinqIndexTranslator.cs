using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Translates a deliberately small subset of C# expressions into a PostgreSQL SQL template with
/// <c>{Property.Path}</c> placeholders (literal braces escaped as <c>{{</c>/<c>}}</c>). Property
/// paths stay symbolic so the differ can resolve them to columns — or JSON extractions — against
/// the finalized model at migration time.
/// </summary>
/// <remarks>
/// Supported: property paths (incl. complex/JSON members), <c>ToLower</c>/<c>ToUpper</c>,
/// <c>Trim</c>/<c>TrimStart</c>/<c>TrimEnd</c> (parameterless), <c>Substring</c>, <c>Replace</c>,
/// <c>string.Length</c>, string concatenation (<c>+</c>), null coalescing (<c>??</c>), and
/// constants (captured variables are evaluated and inlined as literals, numbers invariant-culture).
/// Anything else throws <see cref="NotSupportedException"/> at declaration time — fall back to the
/// raw-SQL <c>HasExpressionIndex(string)</c> overload for the rest.
/// </remarks>
internal static class NpgsqlLinqIndexTranslator
{
    // The characters Npgsql's query translation passes for a parameterless Trim/TrimStart/TrimEnd.
    private const string TrimCharacters = "E' \\t\\n\\r'";

    // ── The accepted constructs, as data ──
    // The dispatch below reads these tables, and TranslatorParitySweepTests enumerates them to
    // compare every construct with what Npgsql's query translation renders for the same lambda. A
    // construct added here is swept with no edit there; one added to the dispatch without a table
    // entry is not, so keep new constructs in the tables.

    /// <summary>Renders a call from its rendered target, its arguments and the operand visitor.</summary>
    internal delegate string CallRenderer(string target, IReadOnlyList<Expression> arguments, Func<Expression, string> visit);

    /// <summary>The string methods a typed expression or filter operand may call, by exact overload.</summary>
    internal static readonly IReadOnlyDictionary<MethodInfo, CallRenderer> StringMethods = new Dictionary<MethodInfo, CallRenderer>
    {
        [StringMethod(nameof(string.ToLower))] = (target, _, _) => $"lower({target})",
        [StringMethod(nameof(string.ToUpper))] = (target, _, _) => $"upper({target})",

        // Spelled as Npgsql's query translation spells them, character for character: an index
        // serves only a query whose expression matches it, and btrim(x) alone trims spaces, so a
        // unique index on it let 'Y\t' and 'Y' coexist where Trim() says equal.
        [StringMethod(nameof(string.Trim))]      = (target, _, _) => $"btrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimStart))] = (target, _, _) => $"ltrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimEnd))]   = (target, _, _) => $"rtrim({target}, {TrimCharacters})",

        [StringMethod(nameof(string.Replace), typeof(string), typeof(string))]
            = (target, arguments, visit) => $"replace({target}, {visit(arguments[0])}, {visit(arguments[1])})",

        // .NET substrings are 0-based, SQL's are 1-based. substring, not substr: they are different
        // functions to the planner, and Npgsql's queries call substring.
        [StringMethod(nameof(string.Substring), typeof(int))]
            = (target, arguments, visit) => $"substring({target}, {SubstringStart(arguments[0], visit)})",
        [StringMethod(nameof(string.Substring), typeof(int), typeof(int))]
            = (target, arguments, visit) => $"substring({target}, {SubstringStart(arguments[0], visit)}, {visit(arguments[1])})",
    };

    /// <summary>
    /// The members rendered from their target, keyed by declaring type (a generic one by its
    /// definition) and name.
    /// </summary>
    internal static readonly IReadOnlyDictionary<(Type DeclaringType, string Name), Func<string, string>> ValueMembers
        = new Dictionary<(Type, string), Func<string, string>>
        {
            [(typeof(string), nameof(string.Length))] = target => $"length({target})",

            // Nullable<T>.Value is the column itself, as in EF Core's queries; HasValue is its null
            // check (a filter writes it bare, and pushes a negation into IS NULL).
            [(typeof(Nullable<>), nameof(Nullable<int>.Value))]    = target => target,
            [(typeof(Nullable<>), nameof(Nullable<int>.HasValue))] = target => $"({target} IS NOT NULL)",
        };

    /// <summary>The operators an operand may use besides calls and members: concatenation of strings and <c>??</c>.</summary>
    internal static readonly IReadOnlySet<ExpressionType> ValueOperators
        = new HashSet<ExpressionType> { ExpressionType.Add, ExpressionType.Coalesce };

    /// <summary>The comparisons a filter may make. Equality carries EF Core's null compensation.</summary>
    internal static readonly IReadOnlyDictionary<ExpressionType, string> ComparisonOperators = new Dictionary<ExpressionType, string>
    {
        [ExpressionType.Equal]              = "=",
        [ExpressionType.NotEqual]           = "<>",
        [ExpressionType.LessThan]           = "<",
        [ExpressionType.LessThanOrEqual]    = "<=",
        [ExpressionType.GreaterThan]        = ">",
        [ExpressionType.GreaterThanOrEqual] = ">=",
    };

    /// <summary>The boolean structure a filter may have.</summary>
    internal static readonly IReadOnlySet<ExpressionType> LogicalOperators
        = new HashSet<ExpressionType> { ExpressionType.AndAlso, ExpressionType.OrElse, ExpressionType.Not };

    /// <summary>
    /// The types a captured value or constant may have: the ones with a portable SQL spelling. A
    /// DateTime, Guid or enum is IFormattable too, and came out as bare text that is not SQL.
    /// </summary>
    internal static readonly IReadOnlySet<Type> LiteralTypes = new HashSet<Type>
    {
        typeof(string), typeof(bool), typeof(char),
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte),
        typeof(uint), typeof(ulong), typeof(ushort), typeof(decimal), typeof(double), typeof(float),
    };

    private static MethodInfo StringMethod(string name, params Type[] parameters)
        => typeof(string).GetMethod(name, parameters)
        ?? throw new InvalidOperationException($"string.{name}({string.Join(", ", parameters.Select(p => p.Name))}) does not exist.");

    // A start known at the declaration, a literal or a captured variable, is folded as EF Core
    // folds it: substring("Code", 2), not substring("Code", (1 + 1)).
    private static string SubstringStart(Expression start, Func<Expression, string> visit)
        => !new ParameterFinder(parameter: null).Search(start) && Evaluate(start) is int startIndex
               ? (startIndex + 1).ToString(CultureInfo.InvariantCulture)
               : $"({visit(start)} + 1)";

    private static bool TryGetValueMember(MemberExpression member, out Func<string, string> render)
    {
        var declaringType = member.Member.DeclaringType!;
        if (declaringType.IsConstructedGenericType)
            declaringType = declaringType.GetGenericTypeDefinition();

        return ValueMembers.TryGetValue((declaringType, member.Member.Name), out render!);
    }

    public static string Translate(LambdaExpression lambda)
        => Visit(lambda.Body, lambda.Parameters[0]);

    /// <summary>
    /// Translates a boolean lambda into a filter template: comparisons (<c>==</c>/<c>!=</c> against
    /// null become <c>IS [NOT] NULL</c>), <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, boolean members,
    /// and on the operands everything <see cref="Translate"/> supports. Property paths stay
    /// <c>{Path}</c> placeholders for the differ to resolve.
    /// </summary>
    public static string TranslatePredicate(LambdaExpression lambda)
        => UnescapeLiteralBraces(VisitPredicate(lambda.Body, lambda.Parameters[0], negated: false));

    // Literals escape braces for templates, which unescape them when resolved. A predicate is a
    // filter, and the filter resolver has no `{{` escape ('{{1,2},{3,4}}' is an array literal), so
    // an escaped brace stayed doubled: `x.Code != "{draft}"` compared against '{{draft}}'. Inside a
    // literal a brace is never a placeholder to the filter resolver, so it can go back to one.
    private static string UnescapeLiteralBraces(string predicate)
    {
        var sql       = new System.Text.StringBuilder(predicate.Length);
        var inLiteral = false;

        for (var i = 0; i < predicate.Length; i++)
        {
            var ch = predicate[i];

            // A doubled quote inside a literal toggles twice and lands back inside it.
            if (ch == '\'')
                inLiteral = !inLiteral;

            sql.Append(ch);

            if (inLiteral && ch is '{' or '}' && i + 1 < predicate.Length && predicate[i + 1] == ch)
                i++;
        }

        return sql.ToString();
    }

    // C# compares with two-valued logic: `x.Kind != "a"` is true for a null Kind. SQL's `<>` is
    // NULL there, and a partial index or constraint filter that is NULL leaves the row out, so a
    // unique index "among rows that are not federated" silently stopped covering rows whose Kind
    // is null. Comparisons with an operand that can be null are therefore written the way EF Core
    // writes the same lambda in a query (`"Kind" <> 'a' OR "Kind" IS NULL`), which keeps the index
    // usable for that query too, and a negation over such a comparison is pushed down to it rather
    // than wrapped around a value that can be NULL. Operands that cannot be null keep the plain
    // forms, so their filters, and the indexes already built from them, do not change.
    private static string VisitPredicate(Expression node, ParameterExpression root, bool negated)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return VisitPredicate(convert.Operand, root, negated);

            case UnaryExpression { NodeType: ExpressionType.Not } not when LogicalOperators.Contains(not.NodeType):
                return ComparesNullable(not.Operand, root) || IsHasValue(not.Operand, root)
                           ? VisitPredicate(not.Operand, root, !negated)
                           : Negate($"NOT ({VisitPredicate(not.Operand, root, negated: false)})", negated);

            case BinaryExpression logical when LogicalOperators.Contains(logical.NodeType):
            {
                // De Morgan when a negation is being pushed through: NOT (a AND b) = NOT a OR NOT b.
                var and = logical.NodeType == ExpressionType.AndAlso ^ negated;
                return $"({VisitPredicate(logical.Left, root, negated)} {(and ? "AND" : "OR")} {VisitPredicate(logical.Right, root, negated)})";
            }

            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality
                when ComparisonOperators.ContainsKey(equality.NodeType):
            {
                var notEqual = equality.NodeType == ExpressionType.NotEqual ^ negated;

                // A captured variable that holds null is a null comparison too; inlined, it read `= NULL`.
                if (IsNullValue(equality.Right, root))
                    return $"{VisitOperand(equality.Left, root)} IS {(notEqual ? "NOT " : "")}NULL";

                if (IsNullValue(equality.Left, root))
                    return $"{VisitOperand(equality.Right, root)} IS {(notEqual ? "NOT " : "")}NULL";

                var (left, right) = VisitOperands(equality, root);
                var leftNullable  = CanBeNull(equality.Left, root);
                var rightNullable = CanBeNull(equality.Right, root);

                // EF Core's own null compensation for the same comparisons.
                return (notEqual, leftNullable, rightNullable) switch
                {
                    (false, true, true)  => $"({left} = {right} OR ({left} IS NULL AND {right} IS NULL))",
                    (false, _, _)        => $"{left} = {right}",
                    (true, true, true)   => $"(({left} <> {right} OR {left} IS NULL OR {right} IS NULL) AND ({left} IS NOT NULL OR {right} IS NOT NULL))",
                    (true, true, false)  => $"({left} <> {right} OR {left} IS NULL)",
                    (true, false, true)  => $"({left} <> {right} OR {right} IS NULL)",
                    (true, false, false) => $"{left} <> {right}",
                };
            }

            case BinaryExpression comparison when ComparisonOperators.ContainsKey(comparison.NodeType):
            {
                var (left, right) = VisitOperands(comparison, root);

                if (!negated)
                    return $"{left} {ComparisonOperators[comparison.NodeType]} {right}";

                // Negated: C#'s lifted comparison is false for a null, so its negation is true there,
                // which the complement alone would make NULL.
                var complement = comparison.NodeType switch
                {
                    ExpressionType.LessThan        => ExpressionType.GreaterThanOrEqual,
                    ExpressionType.LessThanOrEqual => ExpressionType.GreaterThan,
                    ExpressionType.GreaterThan     => ExpressionType.LessThanOrEqual,
                    _                              => ExpressionType.LessThan,
                };

                var nulls = string.Concat(
                    CanBeNull(comparison.Left, root) ? $" OR {left} IS NULL" : "",
                    CanBeNull(comparison.Right, root) ? $" OR {right} IS NULL" : "");

                return $"({left} {ComparisonOperators[complement]} {right}{nulls})";
            }

            case MemberExpression hasValue when IsHasValue(hasValue, root):
                return $"{Visit(hasValue.Expression!, root)} IS {(negated ? "" : "NOT ")}NULL";

            // A boolean column, or a bool? column's Value, stands on its own.
            case MemberExpression member when member.Type == typeof(bool) && DependsOnParameter(member, root):
                return Negate(Visit(member, root), negated);
        }

        // A captured flag: bake it in.
        if (node.Type == typeof(bool) && !DependsOnParameter(node, root))
            return Negate(Literal(Evaluate(node)), negated);

        throw new NotSupportedException(
            $"The expression '{node}' is not supported in a typed index filter. Supported: comparisons " +
            "(==, !=, <, <=, >, >=; == null and != null become IS NULL / IS NOT NULL), &&, ||, !, boolean " +
            "properties, and on the operands the same string operations and constants a typed expression " +
            "index accepts. Use the string filter overload with raw SQL for anything else.");
    }

    // Enum values never reach SQL: how one is stored depends on the property's value conversion,
    // which the translator cannot see, so `Status == Status.Active` would compare against 0 on a
    // text column and fail at apply time. Refuse it here, at the declaration.
    private static string VisitOperand(Expression node, ParameterExpression root)
    {
        var stripped = node;
        while (stripped is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            stripped = convert.Operand;

        if ((Nullable.GetUnderlyingType(stripped.Type) ?? stripped.Type).IsEnum)
            throw new NotSupportedException(
                $"The expression '{node}' compares an enum in a typed index filter. How an enum is stored depends " +
                "on the property's value conversion, which a filter cannot see; write the stored value with the " +
                "string filter overload instead.");

        return Visit(node, root);
    }

    // C# compares chars as ints: x.Grade == 'A' arrives as Convert(x.Grade) == 65, and the constant
    // rendered as 65 met a character column, which PostgreSQL refuses at apply. The constant goes
    // back to the char it was written as, as EF Core renders it.
    private static (string Left, string Right) VisitOperands(BinaryExpression comparison, ParameterExpression root)
    {
        var left  = VisitOperand(comparison.Left, root);
        var right = VisitOperand(comparison.Right, root);

        if (IsConvertedChar(comparison.Left) && CharCode(comparison.Right, root) is { } rightChar)
            right = Literal(rightChar);
        else if (IsConvertedChar(comparison.Right) && CharCode(comparison.Left, root) is { } leftChar)
            left = Literal(leftChar);

        return (left, right);
    }

    private static bool IsConvertedChar(Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert } convert
        && (Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type) == typeof(char);

    private static char? CharCode(Expression node, ParameterExpression root)
        => !DependsOnParameter(node, root) && !IsConvertedChar(node) && Evaluate(node) is int code ? (char)code : null;

    private static bool IsHasValue(Expression node, ParameterExpression root)
        => node is MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } target }
        && Nullable.GetUnderlyingType(target.Type) is not null
        && DependsOnParameter(target, root);

    private static string Negate(string predicate, bool negated)
        => negated ? $"NOT ({predicate})" : predicate;

    // A literal null or a captured value that evaluates to null.
    private static bool IsNullValue(Expression node, ParameterExpression root)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        return node is ConstantExpression { Value: null }
            || (!DependsOnParameter(node, root) && Evaluate(node) is null);
    }

    // Whether a comparison under `node` has an operand that can be null, i.e. whether wrapping it in
    // NOT (…) could leave NULL where C# has true. Null checks themselves never produce NULL.
    private static bool ComparesNullable(Expression node, ParameterExpression root) => node switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.Not } unary
            => ComparesNullable(unary.Operand, root),
        BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical
            => ComparesNullable(logical.Left, root) || ComparesNullable(logical.Right, root),
        BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality
            when IsNullValue(equality.Left, root) || IsNullValue(equality.Right, root)
            => false,
        BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan
                                    or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan
                                    or ExpressionType.GreaterThanOrEqual } comparison
            => CanBeNull(comparison.Left, root) || CanBeNull(comparison.Right, root),
        _ => false,
    };

    // Whether an operand can be NULL in SQL. A captured value is known; a property path is
    // nullable when any member on it is (a Nullable<T>, or a reference type not annotated as
    // non-nullable — oblivious code counts as nullable); anything computed from paths is nullable
    // when one of them is, since lower(NULL) and length(NULL) are NULL. A coalesce is nullable
    // only through its fallback, and a concatenation never is (its operands are coalesced).
    private static bool CanBeNull(Expression node, ParameterExpression root)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        if (!DependsOnParameter(node, root))
            return Evaluate(node) is null;

        if (node is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
            return CanBeNull(coalesce.Right, root);

        // A concatenation coalesces its nullable operands, so it is never null itself.
        if (IsConcatenation(node))
            return false;

        var finder = new NullableMemberFinder(root);
        finder.Visit(node);
        return finder.Found;
    }

    private sealed class NullableMemberFinder(ParameterExpression root) : ExpressionVisitor
    {
        private readonly NullabilityInfoContext _nullability = new();

        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            // A captured value inside the operand (`x.Name + suffix`) is known, not declared.
            if (node is not null && !DependsOnParameter(node, root))
            {
                if (Evaluate(node) is null)
                    Found = true;
                return node;
            }

            return base.Visit(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // HasValue is never null, whatever its target is.
            if (node.Member.Name == nameof(Nullable<int>.HasValue) && Nullable.GetUnderlyingType(node.Member.DeclaringType!) is not null)
                return node;

            // string.Length and Nullable<T>.Value are members too, but not of the entity: their
            // nullability is their target's, which the base visit reaches.
            var ofEntity = node.Member.DeclaringType != typeof(string)
                        && Nullable.GetUnderlyingType(node.Member.DeclaringType!) is null;

            if (ofEntity && node.Member switch
                {
                    PropertyInfo property => _nullability.Create(property).ReadState != NullabilityState.NotNull,
                    FieldInfo field       => _nullability.Create(field).ReadState != NullabilityState.NotNull,
                    _                     => !node.Type.IsValueType || Nullable.GetUnderlyingType(node.Type) is not null,
                })
                Found = true;

            return base.VisitMember(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            // A concatenation is never null: its nullable operands are coalesced.
            if (IsConcatenation(node))
                return node;

            // `a ?? b` is null only when b is.
            if (node.NodeType != ExpressionType.Coalesce)
                return base.VisitBinary(node);

            Visit(node.Right);
            return node;
        }
    }

    private static string Visit(Expression node, ParameterExpression root)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return Visit(convert.Operand, root);

            case MemberExpression { Expression: { } target } member
                when DependsOnParameter(target, root) && TryGetValueMember(member, out var render):
                return render(Visit(target, root));

            case MemberExpression member when TryGetPath(member, root, out var path):
                return "{" + path + "}";

            case MethodCallExpression call when DependsOnParameter(call, root):
                return VisitCall(call, root);

            case BinaryExpression { NodeType: ExpressionType.Add } concat
                when concat.Type == typeof(string) && ValueOperators.Contains(concat.NodeType):
                return $"({ConcatOperand(concat.Left, root)} || {ConcatOperand(concat.Right, root)})";

            case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce when ValueOperators.Contains(coalesce.NodeType):
                return $"coalesce({Visit(coalesce.Left, root)}, {Visit(coalesce.Right, root)})";

            case ConstantExpression constant:
                return Literal(constant.Value);
        }

        // A subtree that never touches the entity parameter is a value: bake it in as a literal.
        if (!DependsOnParameter(node, root))
            return Literal(Evaluate(node));

        throw NotSupported(node);
    }

    // C# concatenates null as an empty string; PostgreSQL's || returns NULL. An index on
    // "Code" || "Name" held NULL for every row without a Name, so it served no EF Core query (which
    // writes COALESCE("Name", '')) and, unique, checked none of those rows. An operand that can be
    // null is wrapped as EF Core wraps it; a non-string one is cast to text first, as COALESCE
    // needs one type.
    private static string ConcatOperand(Expression node, ParameterExpression root)
    {
        if (!CanBeNull(node, root))
            return Visit(node, root);

        if (!DependsOnParameter(node, root))
            return "''";

        var stripped = node;
        while (stripped is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            stripped = convert.Operand;

        var operand = Visit(stripped, root);
        return stripped.Type == typeof(string) ? $"coalesce({operand}, '')" : $"coalesce({operand}::text, '')";
    }

    private static bool IsConcatenation(Expression node)
        => node is BinaryExpression { NodeType: ExpressionType.Add } add && add.Type == typeof(string);

    private static string VisitCall(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Object is not null && StringMethods.TryGetValue(call.Method, out var render))
            return render(Visit(call.Object, root), call.Arguments, argument => Visit(argument, root));

        throw NotSupported(call);
    }

    private static bool TryGetPath(MemberExpression member, ParameterExpression root, out string path)
    {
        var segments = new Stack<string>();

        Expression? current = member;
        while (current is MemberExpression m)
        {
            segments.Push(m.Member.Name);
            current = m.Expression;
        }

        path = string.Join(".", segments);
        if (!ReferenceEquals(current, root))
            return false;

        // A member of a scalar type (CreatedAt.Year, RevokedAt.Value outside the table above) is no
        // property the model maps: it passed as the path {CreatedAt.Year} and failed at `migrations
        // add` with "Could not resolve property path". Refuse it here, at the declaration.
        for (Expression? node = member; node is MemberExpression m; node = m.Expression)
            if (m.Expression is { } owner && IsScalar(owner.Type))
                throw new NotSupportedException(
                    $"The expression '{member}' reads {m.Member.Name} of a {owner.Type.Name}, which is not a property the model " +
                    "maps and has no translation in a typed index expression or filter. Use the raw-SQL overload for it.");

        return true;
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type.IsArray || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    private static string Literal(object? value) => value switch
    {
        null       => "NULL",
        string s   => "'" + s.Replace("'", "''").Replace("{", "{{").Replace("}", "}}") + "'",
        bool b     => b ? "TRUE" : "FALSE",
        char c     => Literal(c.ToString()),
        // Invariant culture — a de-DE thread culture must never turn 1.5 into '1,5'.
        IFormattable number when LiteralTypes.Contains(value.GetType())
            => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
                 $"Cannot inline a value of type '{value.GetType().Name}' as a SQL literal in an index expression or filter. " +
                 "Only strings, booleans and numbers have a portable SQL spelling; write anything else in the raw-SQL overload.")
    };

    private static object? Evaluate(Expression node)
        => Expression.Lambda(node).Compile().DynamicInvoke();

    private static bool DependsOnParameter(Expression node, ParameterExpression root)
    {
        var finder = new ParameterFinder(root);
        finder.Visit(node);
        return finder.Found;
    }

    private static NotSupportedException NotSupported(Expression node)
        => new(
            $"The expression '{node}' is not supported in a typed index expression. Supported: " +
            "property paths, ToLower/ToUpper, Trim/TrimStart/TrimEnd, Substring, Replace, " +
            "string.Length, string concatenation (+), null coalescing (??), and constants. " +
            "Use the HasExpressionIndex(string) overload with raw SQL for anything else.");

    // Finds the given parameter, or with none given, any parameter.
    private sealed class ParameterFinder(ParameterExpression? parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public bool Search(Expression node)
        {
            Visit(node);
            return Found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (parameter is null || ReferenceEquals(node, parameter))
                Found = true;
            return base.VisitParameter(node);
        }
    }
}
