using System.Globalization;
using System.Linq.Expressions;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// A cut-down stand-in for 5.x's <c>NpgsqlLinqIndexTranslator</c>, which moves to the kernel
/// (ROADMAP 5.4). It translates at declaration and throws for anything outside the subset: lambda
/// bodies are the part of a declaration the type system cannot see into.
/// </summary>
internal static class LambdaTranslator
{
    public static KeyTarget Key(LambdaExpression lambda)
    {
        var body = Unwrap(lambda.Body);
        return TryPath(body, lambda.Parameters[0]) is { } path
            ? new KeyTarget.Column(path)
            : new KeyTarget.Template(Scalar(body, lambda.Parameters[0]));
    }

    public static string Path(LambdaExpression lambda) =>
        TryPath(Unwrap(lambda.Body), lambda.Parameters[0])
        ?? throw new NotSupportedException($"'{lambda.Body}' is not a property path rooted at '{lambda.Parameters[0].Name}'.");

    public static Predicate Predicate(LambdaExpression lambda) => new(Boolean(Unwrap(lambda.Body), lambda.Parameters[0]));

    private static string Boolean(Expression e, ParameterExpression root) => e switch
    {
        UnaryExpression { NodeType: ExpressionType.Not } u => $"NOT ({Boolean(Unwrap(u.Operand), root)})",
        BinaryExpression { NodeType: ExpressionType.AndAlso } b => $"({Boolean(Unwrap(b.Left), root)} AND {Boolean(Unwrap(b.Right), root)})",
        BinaryExpression { NodeType: ExpressionType.OrElse } b => $"({Boolean(Unwrap(b.Left), root)} OR {Boolean(Unwrap(b.Right), root)})",
        BinaryExpression b when IsNull(b.Right) && b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
            => $"{Scalar(Unwrap(b.Left), root)} IS {(b.NodeType == ExpressionType.Equal ? "" : "NOT ")}NULL",
        BinaryExpression b when Comparison(b.NodeType) is { } op
            => $"{Scalar(Unwrap(b.Left), root)} {op} {Scalar(Unwrap(b.Right), root)}",
        _ when e.Type == typeof(bool) => Scalar(e, root),
        _ => throw new NotSupportedException($"'{e}' is not supported in a typed predicate."),
    };

    private static string Scalar(Expression e, ParameterExpression root)
    {
        if (TryPath(e, root) is { } path) return "{" + path + "}";
        return e switch
        {
            MethodCallExpression { Method.Name: nameof(Sql.Raw), Method.DeclaringType: var t } call when t == typeof(Sql)
                => (string)((ConstantExpression)call.Arguments[0]).Value!,
            MethodCallExpression { Method.Name: "ToLower", Object: { } o, Arguments.Count: 0 } => $"lower({Scalar(Unwrap(o), root)})",
            MethodCallExpression { Method.Name: "ToUpper", Object: { } o, Arguments.Count: 0 } => $"upper({Scalar(Unwrap(o), root)})",
            ConstantExpression c => Literal(c.Value),
            MemberExpression m when !References(m, root) => Literal(Expression.Lambda(m).Compile().DynamicInvoke()),
            _ => throw new NotSupportedException($"'{e}' is not supported in a typed SchemaKit expression."),
        };
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''").Replace("{", "{{").Replace("}", "}}") + "'",
        bool b => b ? "TRUE" : "FALSE",
        Enum => throw new NotSupportedException("An enum's stored form depends on its value conversion; write the stored value with Sql.Raw."),
        int or long or short or decimal or double or float => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException($"A '{value.GetType().Name}' has no portable SQL literal."),
    };

    private static string? TryPath(Expression e, ParameterExpression root)
    {
        var segments = new Stack<string>();
        while (e is MemberExpression m)
        {
            segments.Push(m.Member.Name);
            e = Unwrap(m.Expression!);
        }
        return e == root && segments.Count > 0 ? string.Join('.', segments) : null;
    }

    private static bool References(Expression e, ParameterExpression root)
    {
        while (e is MemberExpression { Expression: { } inner }) e = Unwrap(inner);
        return e == root;
    }

    private static Expression Unwrap(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u ? Unwrap(u.Operand) : e;

    private static bool IsNull(Expression e) => Unwrap(e) is ConstantExpression { Value: null };

    private static string? Comparison(ExpressionType type) => type switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "<>",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        _ => null,
    };
}
