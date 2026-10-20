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

    public static string Translate(LambdaExpression lambda)
        => Visit(lambda.Body, lambda.Parameters[0]);

    /// <summary>
    /// Translates a boolean lambda into a filter template: comparisons (<c>==</c>/<c>!=</c> against
    /// null become <c>IS [NOT] NULL</c>), <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, boolean members,
    /// and on the operands everything <see cref="Translate"/> supports. Property paths stay
    /// <c>{Path}</c> placeholders for the differ to resolve.
    /// </summary>
    public static string TranslatePredicate(LambdaExpression lambda)
        => VisitPredicate(lambda.Body, lambda.Parameters[0], negated: false);

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

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return ComparesNullable(not.Operand, root)
                           ? VisitPredicate(not.Operand, root, !negated)
                           : Negate($"NOT ({VisitPredicate(not.Operand, root, negated: false)})", negated);

            case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
            {
                // De Morgan when a negation is being pushed through: NOT (a AND b) = NOT a OR NOT b.
                var and = logical.NodeType == ExpressionType.AndAlso ^ negated;
                return $"({VisitPredicate(logical.Left, root, negated)} {(and ? "AND" : "OR")} {VisitPredicate(logical.Right, root, negated)})";
            }

            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality:
            {
                var notEqual = equality.NodeType == ExpressionType.NotEqual ^ negated;

                // A captured variable that holds null is a null comparison too; inlined, it read `= NULL`.
                if (IsNullValue(equality.Right, root))
                    return $"{VisitOperand(equality.Left, root)} IS {(notEqual ? "NOT " : "")}NULL";

                if (IsNullValue(equality.Left, root))
                    return $"{VisitOperand(equality.Right, root)} IS {(notEqual ? "NOT " : "")}NULL";

                var left          = VisitOperand(equality.Left, root);
                var right         = VisitOperand(equality.Right, root);
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

            case BinaryExpression { NodeType: ExpressionType.LessThan or ExpressionType.LessThanOrEqual
                                            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual } comparison:
            {
                var left  = VisitOperand(comparison.Left, root);
                var right = VisitOperand(comparison.Right, root);

                if (!negated)
                    return $"{left} {ComparisonOperator(comparison.NodeType)} {right}";

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

                return $"({left} {ComparisonOperator(complement)} {right}{nulls})";
            }

            // A boolean column stands on its own.
            case MemberExpression member when member.Type == typeof(bool) && TryGetPath(member, root, out var path):
                return Negate("{" + path + "}", negated);
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

    private static string Negate(string predicate, bool negated)
        => negated ? $"NOT ({predicate})" : predicate;

    private static string ComparisonOperator(ExpressionType type) => type switch
    {
        ExpressionType.LessThan        => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan     => ">",
        _                              => ">=",
    };

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
    // when one of them is, since lower(NULL), NULL || 'x' and length(NULL) are all NULL. A
    // coalesce is nullable only through its fallback.
    private static bool CanBeNull(Expression node, ParameterExpression root)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        if (!DependsOnParameter(node, root))
            return Evaluate(node) is null;

        if (node is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
            return CanBeNull(coalesce.Right, root);

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

            case MemberExpression { Member.Name: nameof(string.Length), Expression: { } target } member
                when member.Member.DeclaringType == typeof(string) && DependsOnParameter(target, root):
                return $"length({Visit(target, root)})";

            case MemberExpression member when TryGetPath(member, root, out var path):
                return "{" + path + "}";

            case MethodCallExpression call when DependsOnParameter(call, root):
                return VisitCall(call, root);

            case BinaryExpression { NodeType: ExpressionType.Add } concat when concat.Type == typeof(string):
                return $"({Visit(concat.Left, root)} || {Visit(concat.Right, root)})";

            case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce:
                return $"coalesce({Visit(coalesce.Left, root)}, {Visit(coalesce.Right, root)})";

            case ConstantExpression constant:
                return Literal(constant.Value);
        }

        // A subtree that never touches the entity parameter is a value: bake it in as a literal.
        if (!DependsOnParameter(node, root))
            return Literal(Evaluate(node));

        throw NotSupported(node);
    }

    private static string VisitCall(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Object is not null && call.Method.DeclaringType == typeof(string))
        {
            var target = Visit(call.Object, root);

            switch (call.Method.Name)
            {
                case nameof(string.ToLower) when call.Arguments.Count == 0:
                    return $"lower({target})";

                case nameof(string.ToUpper) when call.Arguments.Count == 0:
                    return $"upper({target})";

                // Spelled as Npgsql's query translation spells them, character for character: an
                // index serves only a query whose expression matches it, and btrim(x) alone trims
                // spaces, so a unique index on it let 'Y\t' and 'Y' coexist where Trim() says equal.
                case nameof(string.Trim) when call.Arguments.Count == 0:
                    return $"btrim({target}, {TrimCharacters})";

                case nameof(string.TrimStart) when call.Arguments.Count == 0:
                    return $"ltrim({target}, {TrimCharacters})";

                case nameof(string.TrimEnd) when call.Arguments.Count == 0:
                    return $"rtrim({target}, {TrimCharacters})";

                case nameof(string.Replace) when call.Arguments.Count == 2 && call.Method.GetParameters()[0].ParameterType == typeof(string):
                    return $"replace({target}, {Visit(call.Arguments[0], root)}, {Visit(call.Arguments[1], root)})";

                case nameof(string.Substring) when call.Arguments.Count is 1 or 2:
                {
                    // .NET substrings are 0-based, SQL's are 1-based. substring, not substr: they are
                    // different functions to the planner, and Npgsql's queries call substring.
                    var start = call.Arguments[0] is ConstantExpression { Value: int startIndex }
                                    ? (startIndex + 1).ToString(CultureInfo.InvariantCulture)
                                    : $"({Visit(call.Arguments[0], root)} + 1)";

                    return call.Arguments.Count == 1
                               ? $"substring({target}, {start})"
                               : $"substring({target}, {start}, {Visit(call.Arguments[1], root)})";
                }
            }
        }

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
        return ReferenceEquals(current, root);
    }

    private static string Literal(object? value) => value switch
    {
        null       => "NULL",
        string s   => "'" + s.Replace("'", "''").Replace("{", "{{").Replace("}", "}}") + "'",
        bool b     => b ? "TRUE" : "FALSE",
        char c     => Literal(c.ToString()),
        // Invariant culture — a de-DE thread culture must never turn 1.5 into '1,5'. Only numbers:
        // a DateTime, Guid or enum is IFormattable too, and came out as bare text that is not SQL.
        int or long or short or byte or sbyte or uint or ulong or ushort or decimal or double or float
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
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

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, parameter))
                Found = true;
            return base.VisitParameter(node);
        }
    }
}
