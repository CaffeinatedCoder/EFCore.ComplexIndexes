using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodoMetis.EFCore.SchemaKit.SqlServer;

/// <summary>A SQL Server key: ascending or descending, nothing else. There is no <c>NULLS FIRST/LAST</c> in T-SQL.</summary>
public sealed record SqlServerKey(KeyTarget Target)
{
    public bool Descending { get; init; }

    public SqlServerKey Desc() => this with { Descending = true };

    public SqlServerKey Asc() => this with { Descending = false };
}

/// <summary><c>DATA_COMPRESSION</c>.</summary>
public closed record Compression
{
    private sealed record NoneCase : Compression;
    private sealed record RowCase : Compression;
    private sealed record PageCase : Compression;

    public static Compression None { get; } = new NoneCase();
    public static Compression Row { get; } = new RowCase();
    public static Compression Page { get; } = new PageCase();

    public string Sql => this switch { NoneCase => "NONE", RowCase => "ROW", PageCase => "PAGE" };
}

/// <summary>
/// A SQL Server index. Clustered and nonclustered are cases, because SQL Server allows
/// <c>INCLUDE</c> and a filter only on a nonclustered one.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Clustered), "clustered")]
[JsonDerivedType(typeof(Nonclustered), "nonclustered")]
public closed record SqlServerIndex : ISqlServerObject, ISchemaDeclaration<SqlServerIndex>
{
    public static string OwnerId => "SqlServer.Index";

    [JsonIgnore] public string Owner => OwnerId;

    public string? Name { get; init; }

    public bool Unique { get; init; }

    public int? FillFactor { get; init; }

    public bool Online { get; init; }

    public bool SortInTempDb { get; init; }

    [JsonIgnore] public Compression? Compression { get; init; }

    [JsonIgnore]
    public ValueList<SqlServerKey> KeyList => this switch
    {
        Clustered c    => c.Keys,
        Nonclustered n => n.Keys,
    };

    [JsonIgnore]
    public string Identity => this switch
    {
        Clustered c    => $"clustered|{string.Join(",", c.Keys.Select(k => k.Target))}",
        Nonclustered n => $"nonclustered|{string.Join(",", n.Keys.Select(k => k.Target))}|{n.Where?.Template}",
    };

    public string ToAnnotationValue() => JsonSerializer.Serialize(this, Options);

    public static SqlServerIndex FromAnnotationValue(string json) => JsonSerializer.Deserialize<SqlServerIndex>(json, Options)!;

    public sealed record Clustered(ValueList<SqlServerKey> Keys) : SqlServerIndex;

    public sealed record Nonclustered(ValueList<SqlServerKey> Keys) : SqlServerIndex
    {
        public Predicate? Where { get; init; }

        public ValueList<string> Include { get; init; } = ValueList<string>.Empty;
    }

    private static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>The typed factory for SQL Server declarations of <typeparamref name="TEntity"/>.</summary>
public readonly struct SqlServerSchema<TEntity> where TEntity : class
{
    /// <summary>A column key. SQL Server has no expression indexes, so a lambda that is not a property path is refused here, at declaration.</summary>
    public SqlServerKey Key<TValue>(Expression<Func<TEntity, TValue>> key) => new(new KeyTarget.Column(LambdaTranslator.Path(key)));

    public SqlServerIndex.Clustered Clustered<TValue>(
        Expression<Func<TEntity, TValue>> key,
        bool unique = false,
        int? fillFactor = null,
        bool online = false,
        bool sortInTempDb = false,
        Compression? compression = null,
        string? name = null)
        => Clustered([Key(key)], unique, fillFactor, online, sortInTempDb, compression, name);

    public SqlServerIndex.Clustered Clustered(
        ValueList<SqlServerKey> keys,
        bool unique = false,
        int? fillFactor = null,
        bool online = false,
        bool sortInTempDb = false,
        Compression? compression = null,
        string? name = null)
        => new(keys)
        {
            Unique = unique, FillFactor = fillFactor, Online = online, SortInTempDb = sortInTempDb,
            Compression = compression, Name = name,
        };

    public SqlServerIndex.Nonclustered Nonclustered<TValue>(
        Expression<Func<TEntity, TValue>> key,
        bool unique = false,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool online = false,
        bool sortInTempDb = false,
        Compression? compression = null,
        string? name = null)
        => Nonclustered([Key(key)], unique, where, include, fillFactor, online, sortInTempDb, compression, name);

    public SqlServerIndex.Nonclustered Nonclustered(
        ValueList<SqlServerKey> keys,
        bool unique = false,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool online = false,
        bool sortInTempDb = false,
        Compression? compression = null,
        string? name = null)
        => new(keys)
        {
            Unique = unique,
            Where = where is null ? null : LambdaTranslator.Predicate(where),
            Include = include is null ? ValueList<string>.Empty : [.. include.Select(LambdaTranslator.Path)],
            FillFactor = fillFactor, Online = online, SortInTempDb = sortInTempDb, Compression = compression, Name = name,
        };
}

public static class SqlServerDeclarationExtensions
{
    extension<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        public EntityTypeBuilder<TEntity> DeclareSqlServer(Func<SqlServerSchema<TEntity>, IEnumerable<ISqlServerObject>> declare)
        {
            builder.Metadata.Declare(declare(default));
            return builder;
        }
    }
}

public static class SqlServerSchema
{
    public static SqlServerSchema<TEntity> For<TEntity>() where TEntity : class => default;
}

/// <summary>A resolved <see cref="SqlServerIndex"/>.</summary>
public sealed record SqlServerIndexDescriptor(
    TableRef Table,
    Identifier Name,
    bool Unique,
    SqlFragment Kind,
    ValueList<SqlFragment> Keys,
    ValueList<SqlFragment> Include,
    SqlFragment? Where,
    ValueList<SqlFragment> With) : ISchemaDescriptor
{
    public ISchemaDescriptor WithName(Identifier name) => this with { Name = name };
}

public sealed class SqlServerIndexContributor : SchemaContributor<SqlServerIndex, SqlServerIndexDescriptor>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override SqlServerIndexDescriptor Resolve(SqlServerIndex index, SchemaResolver r)
    {
        var sql = r.Sql;
        var (kind, include, where) = index switch
        {
            SqlServerIndex.Clustered => (sql.Fragment($"CLUSTERED"), ValueList<string>.Empty, (Predicate?)null),
            SqlServerIndex.Nonclustered n => (sql.Fragment($"NONCLUSTERED"), n.Include, n.Where),
        };
        IEnumerable<SqlFragment?> with =
        [
            index.FillFactor is { } f ? sql.Fragment($"FILLFACTOR = {f}") : null,
            index.Online ? sql.Fragment($"ONLINE = ON") : null,
            index.SortInTempDb ? sql.Fragment($"SORT_IN_TEMPDB = ON") : null,
            index.Compression is null ? null
                : index.Compression == Compression.Page ? sql.Fragment($"DATA_COMPRESSION = PAGE")
                : index.Compression == Compression.Row ? sql.Fragment($"DATA_COMPRESSION = ROW")
                : sql.Fragment($"DATA_COMPRESSION = NONE"),
        ];
        return new SqlServerIndexDescriptor(
            r.Table,
            r.Name(index.Name, "IX", index.KeyList.Select(k => k.Target)),
            index.Unique,
            kind,
            [.. index.KeyList.Select(k => k.Descending ? sql.Fragment($"{r.Target(k.Target)} DESC") : r.Target(k.Target))],
            [.. include.Select(p => r.Target(new KeyTarget.Column(p)))],
            where is null ? null : r.Predicate(where),
            [.. with.OfType<SqlFragment>()]);
    }

    protected override void Create(SqlServerIndexDescriptor d, SchemaSql sql)
    {
        var unique = d.Unique ? sql.Fragment($"UNIQUE ") : SchemaSql.Empty;
        var include = d.Include.Count > 0 ? sql.Fragment($" INCLUDE ({sql.List(d.Include)})") : SchemaSql.Empty;
        var where = d.Where is { } w ? sql.Fragment($" WHERE {w}") : SchemaSql.Empty;
        var with = d.With.Count > 0 ? sql.Fragment($" WITH ({sql.List(d.With)})") : SchemaSql.Empty;
        sql.Statement($"CREATE {unique}{d.Kind} INDEX {d.Name} ON {d.Table} ({sql.List(d.Keys)}){include}{where}{with}");
    }

    protected override void Drop(SqlServerIndexDescriptor d, SchemaSql sql) => sql.Statement($"DROP INDEX {d.Name} ON {d.Table}");

    protected override IEnumerable<NameClaim> Claims(SqlServerIndexDescriptor d) => [new NameClaim.TableIndex(d.Table, d.Name)];
}

public sealed class SqlServerDialect : ISchemaDialect
{
    public string Delimit(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public string DelimitIfNeeded(string name) => Delimit(name);

    public string StatementTerminator => ";";

    public string StringLiteral(string value) => $"N'{value.Replace("'", "''")}'";

    public Identifier Identifier(string name) =>
        name.Length > 128 ? throw new InvalidOperationException($"'{name}' is {name.Length} characters long; SQL Server allows 128.") : new Identifier(name);

    public SqlName Name(string name) => new(Identifier(name).Name);

    public SqlOperator Operator(string token) => throw new NotSupportedException("SQL Server declarations have no operators.");
}
