using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit;

// The SQL generation engine's vocabulary. A contributor writes DDL as interpolated strings whose
// literal parts are its own code and whose holes accept only values that went through the dialect:
// identifiers, resolved fragments, validated operators, numbers. There is no hole for a string, so a
// name or a path cannot reach the DDL unquoted. EF Core's FromSql($"…") is the precedent: there the
// holes become parameters; here they become quoted identifiers and resolved SQL.

/// <summary>An identifier the dialect accepted: length checked on creation, delimited on output.</summary>
public sealed record Identifier
{
    internal Identifier(string name) => Name = name;

    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>A name rendered bare when the dialect allows it (operator classes: <c>jsonb_path_ops</c>), delimited otherwise.</summary>
public sealed record SqlName
{
    internal SqlName(string name) => Name = name;

    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>An operator token checked against the dialect's operator grammar (PostgreSQL: <c>+-*/&lt;&gt;=~!@#%^&amp;|`?</c>).</summary>
public sealed record SqlOperator
{
    internal SqlOperator(string token) => Token = token;

    public string Token { get; }
}

public sealed record TableRef(Identifier Name, Identifier? Schema);

/// <summary>
/// SQL the kernel produced: a resolved column, a resolved expression or predicate, or a fragment a
/// contributor composed through <see cref="SchemaSql.Fragment"/>. No public constructor and no
/// conversion from <see cref="string"/>.
/// </summary>
public sealed record SqlFragment
{
    internal SqlFragment(string text) => Text = text;

    internal string Text { get; }

    public override string ToString() => Text;
}

/// <summary>Collects one declaration's statements. The kernel turns them into <see cref="SqlOperation"/>s.</summary>
public sealed class SchemaSql
{
    private readonly List<SqlOperation> _operations = [];

    internal SchemaSql(ISchemaDialect dialect) => Dialect = dialect;

    internal ISchemaDialect Dialect { get; }

    public void Statement([InterpolatedStringHandlerArgument("")] SqlHandler sql, bool suppressTransaction = false)
        => _operations.Add(new SqlOperation { Sql = sql.ToStringAndClear() + Dialect.StatementTerminator, SuppressTransaction = suppressTransaction });

    public SqlFragment Fragment([InterpolatedStringHandlerArgument("")] SqlHandler sql) => new(sql.ToStringAndClear());

    public SqlFragment List(IEnumerable<SqlFragment> items) => new(string.Join(", ", items.Select(i => i.Text)));

    public SqlFragment List(IEnumerable<Identifier> items) => new(string.Join(", ", items.Select(i => Dialect.Delimit(i.Name))));

    /// <summary>A string literal, escaped by the dialect: for messages, never for names.</summary>
    public SqlFragment Literal(string value) => new(Dialect.StringLiteral(value));

    public static SqlFragment Empty { get; } = new("");

    internal IReadOnlyList<SqlOperation> Operations => _operations;
}

/// <summary>The interpolated-string handler behind <see cref="SchemaSql"/>: one <c>AppendFormatted</c> per safe kind, none for <see cref="string"/>.</summary>
[InterpolatedStringHandler]
public ref struct SqlHandler
{
    private readonly StringBuilder _text;
    private readonly ISchemaDialect _dialect;

    public SqlHandler(int literalLength, int formattedCount, SchemaSql sql)
    {
        _dialect = sql.Dialect;
        _text = new StringBuilder(literalLength + formattedCount * 16);
    }

    public void AppendLiteral(string literal) => _text.Append(literal);

    public void AppendFormatted(Identifier identifier) => _text.Append(_dialect.Delimit(identifier.Name));

    public void AppendFormatted(TableRef table) =>
        _text.Append(table.Schema is null ? _dialect.Delimit(table.Name.Name) : $"{_dialect.Delimit(table.Schema.Name)}.{_dialect.Delimit(table.Name.Name)}");

    public void AppendFormatted(SqlName name) => _text.Append(_dialect.DelimitIfNeeded(name.Name));

    public void AppendFormatted(SqlOperator op) => _text.Append(op.Token);

    public void AppendFormatted(SqlFragment fragment) => _text.Append(fragment.Text);

    public void AppendFormatted(int value) => _text.Append(value.ToString(CultureInfo.InvariantCulture));

    public void AppendFormatted(bool value) => _text.Append(value ? "true" : "false");

    internal string ToStringAndClear() => _text.ToString();
}

/// <summary>
/// The provider's spellings (ROADMAP 5.4). In SchemaKit it wraps the provider's own
/// <c>ISqlGenerationHelper</c> for delimiting and terminating, and adds what EF has no service for.
/// </summary>
public interface ISchemaDialect
{
    string Delimit(string identifier);

    string DelimitIfNeeded(string name);

    string StatementTerminator { get; }

    string StringLiteral(string value);

    /// <summary>Validates and wraps a name: throws, naming it and the limit, when the database would truncate or refuse it.</summary>
    Identifier Identifier(string name);

    SqlName Name(string name);

    SqlOperator Operator(string token);
}

public sealed partial class PostgresDialect : ISchemaDialect
{
    public string Delimit(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string DelimitIfNeeded(string name) => BareName().IsMatch(name) ? name : Delimit(name);

    public string StatementTerminator => ";";

    public string StringLiteral(string value) => $"'{value.Replace("'", "''")}'";

    public Identifier Identifier(string name) =>
        Encoding.UTF8.GetByteCount(name) is var bytes and > 63
            ? throw new InvalidOperationException($"'{name}' is {bytes} bytes long; PostgreSQL truncates identifiers to 63.")
            : new Identifier(name);

    public SqlName Name(string name) => new(Identifier(name).Name);

    public SqlOperator Operator(string token) =>
        OperatorToken().IsMatch(token) ? new SqlOperator(token) : throw new ArgumentException($"'{token}' is not a PostgreSQL operator.");

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex BareName();

    [GeneratedRegex(@"^[+\-*/<>=~!@#%^&|`?]{1,63}$")]
    private static partial Regex OperatorToken();
}
