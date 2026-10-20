using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// What an index key or an exclusion element is built from: a property path the kernel resolves to
/// a column (or, on PostgreSQL, a JSON extraction), or a SQL template whose <c>{Path}</c>
/// placeholders it resolves.
/// </summary>
/// <remarks>
/// A closed hierarchy: every switch over it in the kernel is exhaustive without a discard, and a
/// third kind would be a compile error in each of them (CS8509 under TreatWarningsAsErrors).
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Column), "column")]
[JsonDerivedType(typeof(Template), "template")]
public closed record KeyTarget
{
    /// <summary>A dotted property path, resolved to a column at diff time.</summary>
    public sealed record Column(string Path) : KeyTarget;

    /// <summary>SQL with <c>{Path}</c> placeholders, from a typed lambda or from <see cref="Sql.Raw{T}"/>.</summary>
    public sealed record Template(string Sql) : KeyTarget;

    /// <summary>The token a default name is built from (the 5.x rule: the path's segments, or the alphanumerics of the SQL).</summary>
    [JsonIgnore]
    public string NameToken => this switch
    {
        Column c   => c.Path.Replace('.', '_'),
        Template t => new string(t.Sql.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } s ? s : "expr",
    };
}

/// <summary>A partial-index or constraint predicate in its stored form: SQL with <c>{Path}</c> placeholders.</summary>
public sealed record Predicate(string Template)
{
    /// <summary>Translates a typed predicate. The entity type may be an interface, so one predicate serves a convention over many entities.</summary>
    public static Predicate Of<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate)
        => LambdaTranslator.Predicate(predicate);

    /// <summary>Conjoins, the way 5.x <c>AddComplexIndexFilter</c> did: idempotent when <paramref name="other"/> is already the whole predicate or its last conjunct.</summary>
    public Predicate And(Predicate other)
        => Template == other.Template || Template.EndsWith($" AND ({other.Template})", StringComparison.Ordinal)
            ? this
            : new Predicate($"({Template}) AND ({other.Template})");
}

/// <summary>The one escape hatch into SQL: a call you write, never a string you pass by accident.</summary>
public static class Sql
{
    /// <summary>
    /// Verbatim SQL inside a typed lambda, with <c>{Path}</c> placeholders resolved like a filter's.
    /// <typeparamref name="T"/> gives the fragment a CLR type, so typed options still apply to it.
    /// </summary>
    public static T Raw<T>(string sql) =>
        throw new InvalidOperationException(
            $"Sql.Raw(\"{sql}\") is a marker for SchemaKit declarations; it is translated, never executed.");
}

/// <summary>
/// An immutable list with value equality, so a declaration record compares structurally. Built
/// from a collection expression.
/// </summary>
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
[JsonConverter(typeof(ValueListJsonConverterFactory))]
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly ImmutableArray<T> _items;

    internal ValueList(ImmutableArray<T> items) => _items = items;

    public static ValueList<T> Empty { get; } = new([]);

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}

public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new([.. items]);

    /// <summary>
    /// The one structural rule the type system leaves to run time: C# has no non-empty collection
    /// type a collection expression could target, so an empty key list is refused here.
    /// </summary>
    internal static ValueList<T> RequireNonEmpty<T>(ValueList<T> items, string what) =>
        items.Count > 0 ? items : throw new ArgumentException($"{what} needs at least one element.");
}

internal sealed class ValueListJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(ValueListJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class ValueListJsonConverter<T> : JsonConverter<ValueList<T>>
    {
        public override ValueList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new([.. JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []]);

        public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
