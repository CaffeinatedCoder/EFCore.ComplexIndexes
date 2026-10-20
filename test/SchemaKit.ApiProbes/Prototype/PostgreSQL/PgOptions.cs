using System.Text.Json.Serialization;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

// Options with a fixed set of values are closed hierarchies of singletons, never enums: an enum is
// an int, `(Deferral)7` would compile, and a switch over an enum always needs a discard.

/// <summary>An exclusion constraint's deferrability. Three states, so "initially deferred but not deferrable" has no spelling.</summary>
[JsonConverter(typeof(SingletonJsonConverter<Deferral>))]
public closed record Deferral : ISingleton<Deferral>
{
    private sealed record NotDeferrableCase : Deferral;
    private sealed record InitiallyImmediateCase : Deferral;
    private sealed record InitiallyDeferredCase : Deferral;

    public static Deferral NotDeferrable { get; } = new NotDeferrableCase();
    public static Deferral InitiallyImmediate { get; } = new InitiallyImmediateCase();
    public static Deferral InitiallyDeferred { get; } = new InitiallyDeferredCase();

    static IReadOnlyList<Deferral> ISingleton<Deferral>.All => [NotDeferrable, InitiallyImmediate, InitiallyDeferred];
}

/// <summary>GiST's <c>buffering</c> storage parameter.</summary>
[JsonConverter(typeof(SingletonJsonConverter<GistBuffering>))]
public closed record GistBuffering : ISingleton<GistBuffering>
{
    private sealed record OnCase : GistBuffering;
    private sealed record OffCase : GistBuffering;
    private sealed record AutoCase : GistBuffering;

    public static GistBuffering On { get; } = new OnCase();
    public static GistBuffering Off { get; } = new OffCase();
    public static GistBuffering Auto { get; } = new AutoCase();

    static IReadOnlyList<GistBuffering> ISingleton<GistBuffering>.All => [On, Off, Auto];
}

/// <summary>Where nulls sort in a B-tree key. Only <see cref="BTreeKey"/> has one.</summary>
[JsonConverter(typeof(SingletonJsonConverter<NullOrder>))]
public closed record NullOrder : ISingleton<NullOrder>
{
    private sealed record FirstCase : NullOrder;
    private sealed record LastCase : NullOrder;

    public static NullOrder First { get; } = new FirstCase();
    public static NullOrder Last { get; } = new LastCase();

    static IReadOnlyList<NullOrder> ISingleton<NullOrder>.All => [First, Last];
}

/// <summary>An exclusion operator. Each typed factory on <see cref="PgSchema{TEntity}"/> picks the one its operand type allows.</summary>
public sealed record ExclusionOperator(string Sql)
{
    public static ExclusionOperator Equal { get; } = new("=");
    public static ExclusionOperator Overlaps { get; } = new("&&");
    public static ExclusionOperator Adjacent { get; } = new("-|-");
    public static ExclusionOperator NotEqual { get; } = new("<>");
}

// Operator classes are typed by the index method: a GIN operator class cannot reach a B-tree key.
// The constructor takes any name, for operator classes extensions bring (pg_trgm's gin_trgm_ops).

public sealed record BTreeOpClass(string Name)
{
    public static BTreeOpClass TextPatternOps { get; } = new("text_pattern_ops");
    public static BTreeOpClass VarcharPatternOps { get; } = new("varchar_pattern_ops");
}

public sealed record GinOpClass(string Name)
{
    public static GinOpClass JsonbOps { get; } = new("jsonb_ops");
    public static GinOpClass JsonbPathOps { get; } = new("jsonb_path_ops");
    public static GinOpClass ArrayOps { get; } = new("array_ops");
    public static GinOpClass TsVectorOps { get; } = new("tsvector_ops");
}

public sealed record GistOpClass(string Name)
{
    public static GistOpClass RangeOps { get; } = new("range_ops");
    public static GistOpClass MultirangeOps { get; } = new("multirange_ops");
    public static GistOpClass InetOps { get; } = new("inet_ops");
}

public sealed record SpGistOpClass(string Name)
{
    public static SpGistOpClass RangeOps { get; } = new("range_ops");
    public static SpGistOpClass TextOps { get; } = new("text_ops");
}

public sealed record HashOpClass(string Name);

public sealed record BrinOpClass(string Name);

/// <summary>A fixed set of values, so the JSON form is a name and the switch needs no discard.</summary>
public interface ISingleton<TSelf> where TSelf : class, ISingleton<TSelf>
{
    static abstract IReadOnlyList<TSelf> All { get; }
}

internal sealed class SingletonJsonConverter<T> : JsonConverter<T> where T : class, ISingleton<T>
{
    public override T Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var name = reader.GetString();
        return T.All.Single(v => Name(v) == name);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, T value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(Name(value));

    private static string Name(T value) => value.GetType().Name.Replace("Case", "");
}
