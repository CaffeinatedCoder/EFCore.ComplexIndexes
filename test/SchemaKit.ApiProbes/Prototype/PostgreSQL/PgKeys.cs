namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// A key before it is given to an index: typed by the CLR type of what it indexes, and not yet
/// tied to a method. Ordering, null placement and operator classes turn it into a method's key,
/// which is the step that decides where it may go.
/// </summary>
public sealed record PgKey<TValue>(KeyTarget Target, string? Collation = null)
{
    /// <summary>Descending order exists only on B-tree keys; the result converts to nothing else.</summary>
    public BTreeKey Desc() => ((BTreeKey)this) with { Descending = true };

    public BTreeKey Asc() => this;

    public BTreeKey NullsFirst() => ((BTreeKey)this) with { Nulls = NullOrder.First };

    public BTreeKey NullsLast() => ((BTreeKey)this) with { Nulls = NullOrder.Last };

    public BTreeKey Ops(BTreeOpClass opClass) => ((BTreeKey)this) with { OpClass = opClass };

    public GinKey Ops(GinOpClass opClass) => new(Target, Collation, opClass);

    public GistKey Ops(GistOpClass opClass) => new(Target, Collation, opClass);

    public SpGistKey Ops(SpGistOpClass opClass) => new(Target, Collation, opClass);

    public HashKey Ops(HashOpClass opClass) => new(Target, Collation, opClass);

    public BrinKey Ops(BrinOpClass opClass) => new(Target, Collation, opClass);

    public static implicit operator BTreeKey(PgKey<TValue> key) => new(key.Target, key.Collation);
    public static implicit operator GinKey(PgKey<TValue> key) => new(key.Target, key.Collation);
    public static implicit operator GistKey(PgKey<TValue> key) => new(key.Target, key.Collation);
    public static implicit operator SpGistKey(PgKey<TValue> key) => new(key.Target, key.Collation);
    public static implicit operator HashKey(PgKey<TValue> key) => new(key.Target, key.Collation);
    public static implicit operator BrinKey(PgKey<TValue> key) => new(key.Target, key.Collation);
}

public static class PgKeyExtensions
{
    // A collation exists only for a collatable value. PostgreSQL refuses `COLLATE` on an integer key
    // ("collations are not supported by type integer"); here the method is not there to call.
    extension(PgKey<string> key)
    {
        public PgKey<string> Collate(string collation) => key with { Collation = collation };
    }

    extension(PgKey<char> key)
    {
        public PgKey<char> Collate(string collation) => key with { Collation = collation };
    }
}

public sealed record BTreeKey(KeyTarget Target, string? Collation = null)
{
    public bool Descending { get; init; }

    public NullOrder? Nulls { get; init; }

    public BTreeOpClass? OpClass { get; init; }

    public BTreeKey Desc() => this with { Descending = true };

    public BTreeKey NullsFirst() => this with { Nulls = NullOrder.First };

    public BTreeKey NullsLast() => this with { Nulls = NullOrder.Last };

    public BTreeKey Ops(BTreeOpClass opClass) => this with { OpClass = opClass };
}

public sealed record GinKey(KeyTarget Target, string? Collation = null, GinOpClass? OpClass = null);

public sealed record GistKey(KeyTarget Target, string? Collation = null, GistOpClass? OpClass = null);

public sealed record SpGistKey(KeyTarget Target, string? Collation = null, SpGistOpClass? OpClass = null);

public sealed record HashKey(KeyTarget Target, string? Collation = null, HashOpClass? OpClass = null);

public sealed record BrinKey(KeyTarget Target, string? Collation = null, BrinOpClass? OpClass = null);
