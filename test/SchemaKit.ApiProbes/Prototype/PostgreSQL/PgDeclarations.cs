using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// A PostgreSQL index: one case per access method, each carrying exactly the options PostgreSQL
/// accepts for it. Values, not builders: compare them, put them in collections, change them with
/// <c>with</c>. No case has a property whose valid values depend on another property, so a
/// <c>with</c> expression cannot build an index PostgreSQL would refuse.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(BTree), "btree")]
[JsonDerivedType(typeof(Hash), "hash")]
[JsonDerivedType(typeof(Gist), "gist")]
[JsonDerivedType(typeof(SpGist), "spgist")]
[JsonDerivedType(typeof(Gin), "gin")]
[JsonDerivedType(typeof(Brin), "brin")]
public closed record PgIndex : IPostgresObject, ISchemaDeclaration<PgIndex>
{
    public static string OwnerId => "Pg.Index";

    [JsonIgnore] public string Owner => OwnerId;

    public string? Name { get; init; }

    /// <summary>Every access method takes a partial-index predicate.</summary>
    public Predicate? Where { get; init; }

    /// <summary>Operational, not part of the identity: changing it never rebuilds the index.</summary>
    public bool Concurrently { get; init; }

    /// <summary>Method, ordered key targets and predicate: the 5.x rule (direction and options ignored).</summary>
    [JsonIgnore]
    public string Identity => $"{Method}|{string.Join(",", Targets)}|{Where?.Template}";

    /// <summary>The <c>USING</c> clause; exhaustive over the cases, with no discard.</summary>
    [JsonIgnore]
    public string Method => this switch
    {
        BTree  => "btree",
        Hash   => "hash",
        Gist   => "gist",
        SpGist => "spgist",
        Gin    => "gin",
        Brin   => "brin",
    };

    [JsonIgnore]
    public IReadOnlyList<KeyTarget> Targets => this switch
    {
        BTree b  => [.. b.Keys.Select(k => k.Target)],
        Hash h   => [h.Key.Target],
        Gist g   => [.. g.Keys.Select(k => k.Target)],
        SpGist s => [s.Key.Target],
        Gin g    => [.. g.Keys.Select(k => k.Target)],
        Brin b   => [.. b.Keys.Select(k => k.Target)],
    };

    public PgIndex Named(string name) => this with { Name = name };

    /// <summary>ANDs a predicate on; what 5.x's <c>AddComplexIndexFilter</c> did, as a pure function.</summary>
    public PgIndex AndWhere(Predicate predicate) => this with { Where = Where is null ? predicate : Where.And(predicate) };

    public string ToAnnotationValue() => JsonSerializer.Serialize(this, PgJson.Options);

    public static PgIndex FromAnnotationValue(string json) => JsonSerializer.Deserialize<PgIndex>(json, PgJson.Options)!;

    /// <summary>B-tree, the only method with ordering, uniqueness and <c>NULLS NOT DISTINCT</c>.</summary>
    public sealed record BTree(ValueList<BTreeKey> Keys) : PgIndex
    {
        public ValueList<BTreeKey> Keys { get; init => field = ValueList.RequireNonEmpty(value, "A B-tree index"); } = ValueList.RequireNonEmpty(Keys, "A B-tree index");

        /// <summary>Null for a non-unique index. <c>NULLS NOT DISTINCT</c> lives inside, so it cannot exist without <c>UNIQUE</c>.</summary>
        public Uniqueness? Unique { get; init; }

        public ValueList<string> Include { get; init; } = ValueList<string>.Empty;

        public int? FillFactor { get; init; }

        public bool? DeduplicateItems { get; init; }
    }

    /// <summary>Hash: one key, no ordering, no uniqueness, no <c>INCLUDE</c>.</summary>
    public sealed record Hash(HashKey Key) : PgIndex
    {
        public int? FillFactor { get; init; }
    }

    /// <summary>GiST: several keys and <c>INCLUDE</c> (PostgreSQL 12+), no ordering, no uniqueness.</summary>
    public sealed record Gist(ValueList<GistKey> Keys) : PgIndex
    {
        public ValueList<GistKey> Keys { get; init => field = ValueList.RequireNonEmpty(value, "A GiST index"); } = ValueList.RequireNonEmpty(Keys, "A GiST index");

        public ValueList<string> Include { get; init; } = ValueList<string>.Empty;

        public int? FillFactor { get; init; }

        public GistBuffering? Buffering { get; init; }
    }

    /// <summary>SP-GiST: one key, <c>INCLUDE</c> (PostgreSQL 14+).</summary>
    public sealed record SpGist(SpGistKey Key) : PgIndex
    {
        public ValueList<string> Include { get; init; } = ValueList<string>.Empty;

        public int? FillFactor { get; init; }
    }

    /// <summary>GIN: several keys, no ordering, no uniqueness, no <c>INCLUDE</c>.</summary>
    public sealed record Gin(ValueList<GinKey> Keys) : PgIndex
    {
        public ValueList<GinKey> Keys { get; init => field = ValueList.RequireNonEmpty(value, "A GIN index"); } = ValueList.RequireNonEmpty(Keys, "A GIN index");

        public bool? FastUpdate { get; init; }

        public int? PendingListLimit { get; init; }
    }

    /// <summary>BRIN: several keys, no ordering, no uniqueness, no <c>INCLUDE</c>.</summary>
    public sealed record Brin(ValueList<BrinKey> Keys) : PgIndex
    {
        public ValueList<BrinKey> Keys { get; init => field = ValueList.RequireNonEmpty(value, "A BRIN index"); } = ValueList.RequireNonEmpty(Keys, "A BRIN index");

        public int? PagesPerRange { get; init; }

        public bool? Autosummarize { get; init; }
    }
}

/// <summary>A B-tree index's uniqueness; <see cref="NullsDistinct"/> false renders <c>NULLS NOT DISTINCT</c> (PostgreSQL 15+).</summary>
/// <remarks>
/// Always written: omitting a false default would read back as the constructor's <c>true</c>, a
/// <c>NULLS NOT DISTINCT</c> index silently becoming a plain unique one on the next diff.
/// </remarks>
public sealed record Uniqueness([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool NullsDistinct = true);

/// <summary>One element of an exclusion constraint: a key and the operator two rows must not both satisfy.</summary>
public sealed record ExclusionElement(KeyTarget Target, ExclusionOperator Operator);

/// <summary>
/// A PostgreSQL exclusion constraint. GiST takes several elements; SP-GiST one. GIN and BRIN
/// cannot back an exclusion constraint, so they are not cases.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Gist), "gist")]
[JsonDerivedType(typeof(SpGist), "spgist")]
public closed record PgExclusion : IPostgresObject, ISchemaDeclaration<PgExclusion>
{
    public static string OwnerId => "Pg.Exclusion";

    [JsonIgnore] public string Owner => OwnerId;

    public string? Name { get; init; }

    public Predicate? Where { get; init; }

    public Deferral Deferral { get; init; } = Deferral.NotDeferrable;

    /// <summary>Ordered elements (operators ignored) plus the predicate: the 5.x rule.</summary>
    [JsonIgnore]
    public string Identity => $"{Method}|{string.Join(",", ElementList.Select(e => e.Target))}|{Where?.Template}";

    [JsonIgnore]
    public string Method => this switch { Gist => "gist", SpGist => "spgist" };

    [JsonIgnore]
    public IReadOnlyList<ExclusionElement> ElementList => this switch
    {
        Gist g   => g.Elements,
        SpGist s => [s.Element],
    };

    public PgExclusion AndWhere(Predicate predicate) => this with { Where = Where is null ? predicate : Where.And(predicate) };

    public string ToAnnotationValue() => JsonSerializer.Serialize(this, PgJson.Options);

    public static PgExclusion FromAnnotationValue(string json) => JsonSerializer.Deserialize<PgExclusion>(json, PgJson.Options)!;

    public sealed record Gist(ValueList<ExclusionElement> Elements) : PgExclusion
    {
        /// <summary>
        /// The factories guarantee a non-equality element by type (<see cref="ExclusionElements{TEntity}"/>);
        /// this check covers what bypasses them, <c>with</c> and the annotation reader.
        /// </summary>
        public ValueList<ExclusionElement> Elements { get; init => field = RequireCompared(value); } = RequireCompared(Elements);

        private static ValueList<ExclusionElement> RequireCompared(ValueList<ExclusionElement> elements) =>
            elements.Any(e => e.Operator != ExclusionOperator.Equal)
                ? elements
                : throw new ArgumentException("An exclusion constraint of equality elements only is a unique constraint; declare one.");
    }

    public sealed record SpGist(ExclusionElement Element) : PgExclusion;
}

internal static class PgJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
