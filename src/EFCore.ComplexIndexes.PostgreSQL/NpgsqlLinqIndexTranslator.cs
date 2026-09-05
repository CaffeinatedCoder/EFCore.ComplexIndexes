using System.Globalization;
using System.Linq.Expressions;

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
    public static string Translate(LambdaExpression lambda)
        => Visit(lambda.Body, lambda.Parameters[0]);

    /// <summary>
    /// Translates a boolean lambda into a filter template: comparisons (<c>==</c>/<c>!=</c> against
    /// null become <c>IS [NOT] NULL</c>), <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, boolean members,
    /// and on the operands everything <see cref="Translate"/> supports. Property paths stay
    /// <c>{Path}</c> placeholders for the differ to resolve.
    /// </summary>
    public static string TranslatePredicate(LambdaExpression lambda)
        => VisitPredicate(lambda.Body, lambda.Parameters[0]);

    private static string VisitPredicate(Expression node, ParameterExpression root)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return VisitPredicate(convert.Operand, root);

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return $"NOT ({VisitPredicate(not.Operand, root)})";

            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return $"({VisitPredicate(and.Left, root)} AND {VisitPredicate(and.Right, root)})";

            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                return $"({VisitPredicate(or.Left, root)} OR {VisitPredicate(or.Right, root)})";

            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality:
            {
                var negated = equality.NodeType == ExpressionType.NotEqual;

                if (IsNullConstant(equality.Right))
                    return $"{VisitOperand(equality.Left, root)} IS {(negated ? "NOT " : "")}NULL";

                if (IsNullConstant(equality.Left))
                    return $"{VisitOperand(equality.Right, root)} IS {(negated ? "NOT " : "")}NULL";

                return $"{VisitOperand(equality.Left, root)} {(negated ? "<>" : "=")} {VisitOperand(equality.Right, root)}";
            }

            case BinaryExpression { NodeType: ExpressionType.LessThan } comparison:
                return $"{VisitOperand(comparison.Left, root)} < {VisitOperand(comparison.Right, root)}";

            case BinaryExpression { NodeType: ExpressionType.LessThanOrEqual } comparison:
                return $"{VisitOperand(comparison.Left, root)} <= {VisitOperand(comparison.Right, root)}";

            case BinaryExpression { NodeType: ExpressionType.GreaterThan } comparison:
                return $"{VisitOperand(comparison.Left, root)} > {VisitOperand(comparison.Right, root)}";

            case BinaryExpression { NodeType: ExpressionType.GreaterThanOrEqual } comparison:
                return $"{VisitOperand(comparison.Left, root)} >= {VisitOperand(comparison.Right, root)}";

            // A boolean column stands on its own.
            case MemberExpression member when member.Type == typeof(bool) && TryGetPath(member, root, out var path):
                return "{" + path + "}";
        }

        // A captured flag: bake it in.
        if (node.Type == typeof(bool) && !DependsOnParameter(node, root))
            return Literal(Evaluate(node));

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

    private static bool IsNullConstant(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        return node is ConstantExpression { Value: null };
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

                case nameof(string.Trim) when call.Arguments.Count == 0:
                    return $"btrim({target})";

                case nameof(string.TrimStart) when call.Arguments.Count == 0:
                    return $"ltrim({target})";

                case nameof(string.TrimEnd) when call.Arguments.Count == 0:
                    return $"rtrim({target})";

                case nameof(string.Replace) when call.Arguments.Count == 2 && call.Method.GetParameters()[0].ParameterType == typeof(string):
                    return $"replace({target}, {Visit(call.Arguments[0], root)}, {Visit(call.Arguments[1], root)})";

                case nameof(string.Substring) when call.Arguments.Count is 1 or 2:
                {
                    // .NET substrings are 0-based, SQL's substr is 1-based.
                    var start = call.Arguments[0] is ConstantExpression { Value: int startIndex }
                                    ? (startIndex + 1).ToString(CultureInfo.InvariantCulture)
                                    : $"({Visit(call.Arguments[0], root)} + 1)";

                    return call.Arguments.Count == 1
                               ? $"substr({target}, {start})"
                               : $"substr({target}, {start}, {Visit(call.Arguments[1], root)})";
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
