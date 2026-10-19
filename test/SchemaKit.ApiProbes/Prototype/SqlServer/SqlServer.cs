using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

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

public sealed class SqlServerIndexContributor : SchemaContributor<SqlServerIndex>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override IReadOnlyList<MigrationOperation> Create(SqlServerIndex index, SchemaTable table, ISchemaDialect dialect)
    {
        var keys = index.KeyList.Select(k => table.Render(k.Target) + (k.Descending ? " DESC" : ""));
        var name = index.Name ?? $"IX_{table.Name}_{string.Join("_", index.KeyList.Select(k => k.Target.NameToken))}";
        var (kind, tail) = index switch
        {
            SqlServerIndex.Clustered => ("CLUSTERED", ""),
            SqlServerIndex.Nonclustered n => ("NONCLUSTERED",
                (n.Include.Count > 0 ? $" INCLUDE ({string.Join(", ", n.Include.Select(p => table.Render(new KeyTarget.Column(p))))})" : "")
                + (n.Where is { } w ? $" WHERE {table.RenderPredicate(w)}" : "")),
        };
        var with = new[]
        {
            index.FillFactor is { } f ? $"FILLFACTOR = {f}" : null,
            index.Online ? "ONLINE = ON" : null,
            index.SortInTempDb ? "SORT_IN_TEMPDB = ON" : null,
            index.Compression is { } c ? $"DATA_COMPRESSION = {c.Sql}" : null,
        }.OfType<string>().ToList();
        var sql = $"CREATE {(index.Unique ? "UNIQUE " : "")}{kind} INDEX {dialect.Quote(name)} ON {dialect.Quote(table.Name)} ({string.Join(", ", keys)})"
                + tail + (with.Count > 0 ? $" WITH ({string.Join(", ", with)})" : "") + ";";
        return [new SqlOperation { Sql = sql }];
    }

    protected override IReadOnlyList<MigrationOperation> Drop(SqlServerIndex index, SchemaTable table, ISchemaDialect dialect)
        => [new SqlOperation { Sql = $"DROP INDEX {dialect.Quote(index.Name!)} ON {dialect.Quote(table.Name)};" }];
}
