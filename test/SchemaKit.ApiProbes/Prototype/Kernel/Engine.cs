using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit;

// The differ's pipeline, one type per stage:
//
//   declaration (ISchemaObject: paths, typed options)        what the model says
//     → descriptor (ISchemaDescriptor: identifiers, SQL)      Resolve, through SchemaResolver
//     → statements (SchemaSql)                                Create / Drop / Rename
//     → MigrationOperation (SqlOperation)                     the kernel, never a contributor
//
// A contributor's Create receives only its descriptor, which holds resolved identifiers and
// fragments, so it cannot render a path the resolver did not see; and SchemaSql accepts only
// dialect-checked values, so it cannot render a name the dialect did not see.

/// <summary>Where a contributor's operations go relative to EF's own (ROADMAP 4.1, 5.3).</summary>
public closed record SchemaPlacement
{
    private sealed record BeforeTablesCase : SchemaPlacement;
    private sealed record AfterTablesCase(int Order) : SchemaPlacement;

    /// <summary>Created before EF's <c>CREATE TABLE</c>, dropped after its <c>DROP TABLE</c> (domains, ROADMAP 7.3).</summary>
    public static SchemaPlacement BeforeTables { get; } = new BeforeTablesCase();

    /// <summary>Dropped before EF's operations and created after them, ordered among contributors by <paramref name="order"/>.</summary>
    public static SchemaPlacement AfterTables(int order) => new AfterTablesCase(order);

    internal int Rank => this switch
    {
        BeforeTablesCase  => int.MinValue,
        AfterTablesCase a => a.Order,
    };
}

/// <summary>
/// A resolved declaration: what the differ compares. Records with value equality, so "equal" is the
/// diff identity and "equal but for the name" is a rename.
/// </summary>
public interface ISchemaDescriptor
{
    TableRef Table { get; }

    Identifier Name { get; }

    ISchemaDescriptor WithName(Identifier name);
}

/// <summary>
/// The namespaces a name occupies, which differ by database: on PostgreSQL an index name is unique
/// per schema, a constraint name per table, and an exclusion constraint claims both.
/// </summary>
public closed record NameClaim
{
    /// <summary>A relation (index, table) name, unique per schema: PostgreSQL's index rule.</summary>
    public sealed record Relation(Identifier? Schema, Identifier Name) : NameClaim;

    /// <summary>An index name unique per table: SQL Server's rule.</summary>
    public sealed record TableIndex(TableRef Table, Identifier Name) : NameClaim;

    public sealed record Constraint(TableRef Table, Identifier Name) : NameClaim;

    public sealed record Trigger(TableRef Table, Identifier Name) : NameClaim;
}

/// <summary>
/// A descriptor field that changes how DDL runs, not what it builds (<c>CONCURRENTLY</c>): carried to
/// rendering, never compared, so changing it never rebuilds the object.
/// </summary>
public readonly record struct Operational<T>(T Value)
{
    public bool Equals(Operational<T> other) => true;

    public override int GetHashCode() => 0;
}

/// <summary>What the type-mapping engine tells a contributor about a key: the provider's store type, from its own type mapping.</summary>
public sealed record StoreType(string Name, bool IsCollatable);

/// <summary>
/// Turns a declaration's paths and predicates into SQL for one table. The kernel implements it over
/// EF's relational model and the provider's type mapping source; a test implements it over a function.
/// </summary>
public abstract class SchemaResolver
{
    public abstract TableRef Table { get; }

    public abstract ISchemaDialect Dialect { get; }

    /// <summary>For composing a descriptor's fragments.</summary>
    public SchemaSql Sql => field ??= new SchemaSql(Dialect);

    /// <summary>A column, delimited; or an expression, resolved and parenthesised by the caller.</summary>
    public abstract SqlFragment Target(KeyTarget target);

    public abstract SqlFragment Predicate(Predicate predicate);

    public abstract StoreType StoreType(KeyTarget target);

    /// <summary>The declared name, or the 5.x default <c>{prefix}_{table}_{tokens}</c>, validated by the dialect.</summary>
    public Identifier Name(string? declared, string prefix, IEnumerable<KeyTarget> parts) =>
        Dialect.Identifier(declared ?? $"{prefix}_{Table.Name.Name}_{string.Join("_", parts.Select(p => p.NameToken))}");
}

/// <summary>
/// The open seam on the differ side (#56). The kernel holds a list of these and dispatches by
/// <see cref="ISchemaObject.Owner"/>; it never sees a contributor's declaration types.
/// </summary>
public interface ISchemaContributor
{
    string Owner { get; }

    SchemaPlacement Placement { get; }

    ISchemaDescriptor Resolve(ISchemaObject declaration, SchemaResolver resolver);

    void Create(ISchemaDescriptor descriptor, SchemaSql sql);

    void Drop(ISchemaDescriptor descriptor, SchemaSql sql);

    /// <summary>A name-only change; false when the kind cannot be renamed and the kernel drops and re-creates it.</summary>
    bool Rename(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql);

    IEnumerable<NameClaim> Claims(ISchemaDescriptor descriptor);
}

/// <summary>
/// The base a contributor derives from: typed over its own closed union and its own descriptor, so
/// its switches are exhaustive and a new case is a compile error in every one of them.
/// </summary>
public abstract class SchemaContributor<TDeclaration, TDescriptor> : ISchemaContributor
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    where TDescriptor : class, ISchemaDescriptor
{
    public string Owner => TDeclaration.OwnerId;

    public abstract SchemaPlacement Placement { get; }

    protected abstract TDescriptor Resolve(TDeclaration declaration, SchemaResolver resolver);

    protected abstract void Create(TDescriptor descriptor, SchemaSql sql);

    protected abstract void Drop(TDescriptor descriptor, SchemaSql sql);

    protected virtual bool Rename(TDescriptor from, TDescriptor to, SchemaSql sql) => false;

    protected abstract IEnumerable<NameClaim> Claims(TDescriptor descriptor);

    ISchemaDescriptor ISchemaContributor.Resolve(ISchemaObject declaration, SchemaResolver resolver) => Resolve(Own<TDeclaration>(declaration), resolver);

    void ISchemaContributor.Create(ISchemaDescriptor descriptor, SchemaSql sql) => Create(Own<TDescriptor>(descriptor), sql);

    void ISchemaContributor.Drop(ISchemaDescriptor descriptor, SchemaSql sql) => Drop(Own<TDescriptor>(descriptor), sql);

    bool ISchemaContributor.Rename(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql) => Rename(Own<TDescriptor>(from), Own<TDescriptor>(to), sql);

    IEnumerable<NameClaim> ISchemaContributor.Claims(ISchemaDescriptor descriptor) => Claims(Own<TDescriptor>(descriptor));

    private T Own<T>(object value) where T : class =>
        value as T ?? throw new InvalidOperationException($"The kernel routed a '{value.GetType().Name}' to the {Owner} contributor.");
}

/// <summary>A finding of the validation engine, naming the declaration it is about.</summary>
public closed record SchemaDiagnostic
{
    public sealed record NameCollision(NameClaim Claim, string First, string Second) : SchemaDiagnostic;

    public string Message => this switch
    {
        NameCollision c => $"SK1001: {c.First} and {c.Second} both claim {Describe(c.Claim)}.",
    };

    private static string Describe(NameClaim claim) => claim switch
    {
        NameClaim.Relation r   => $"the relation name '{r.Name}'",
        NameClaim.TableIndex i => $"the index name '{i.Name}' on '{i.Table.Name}'",
        NameClaim.Constraint c => $"the constraint name '{c.Name}' on '{c.Table.Name}'",
        NameClaim.Trigger t    => $"the trigger name '{t.Name}' on '{t.Table.Name}'",
    };
}

public sealed class SchemaValidationException(IReadOnlyList<SchemaDiagnostic> diagnostics)
    : InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)))
{
    public IReadOnlyList<SchemaDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// The generic descriptor differ (ROADMAP 4.1) in miniature: resolve both sides, validate the
/// target, match by equality, turn "equal but for the name" into a rename, order by placement.
/// The real one also normalises source tables onto renamed ones and slots EF's own operations in
/// between the drops and the creates.
/// </summary>
public static class SchemaDiffer
{
    public sealed record Declared(ISchemaObject Declaration, SchemaResolver Resolver);

    public static IReadOnlyList<MigrationOperation> Diff(
        IReadOnlyList<ISchemaContributor> contributors, IEnumerable<Declared> source, IEnumerable<Declared> target, ISchemaDialect dialect)
    {
        var byOwner = contributors.ToDictionary(c => c.Owner);
        var from = Resolve(source);
        var to = Resolve(target);
        Validate(to);

        var drops = from.Where(f => !to.Any(t => t.Matches(f))).ToList();
        var creates = to.Where(t => !from.Any(f => f.Matches(t))).ToList();
        var renames = new List<SqlOperation>();

        foreach (var create in creates.ToList())
        {
            var renamed = drops.FirstOrDefault(d => d.Contributor == create.Contributor && d.Descriptor.WithName(create.Descriptor.Name).Equals(create.Descriptor));
            var sql = new SchemaSql(dialect);
            if (renamed is not null && create.Contributor.Rename(renamed.Descriptor, create.Descriptor, sql))
            {
                drops.Remove(renamed);
                creates.Remove(create);
                renames.AddRange(sql.Operations);
            }
        }

        return
        [
            .. drops.OrderByDescending(d => d.Contributor.Placement.Rank).SelectMany(d => Render(d, dialect, (c, x, s) => c.Drop(x, s))),
            // EF's own operations go here.
            .. renames,
            .. creates.OrderBy(c => c.Contributor.Placement.Rank).SelectMany(c => Render(c, dialect, (k, x, s) => k.Create(x, s))),
        ];

        List<Resolved> Resolve(IEnumerable<Declared> side) =>
            [.. side.Select(d => byOwner[d.Declaration.Owner] is var c ? new Resolved(c, c.Resolve(d.Declaration, d.Resolver), d.Declaration) : null!)];
    }

    private sealed record Resolved(ISchemaContributor Contributor, ISchemaDescriptor Descriptor, ISchemaObject Declaration)
    {
        public bool Matches(Resolved other) => Contributor == other.Contributor && Descriptor.Equals(other.Descriptor);
    }

    private static IEnumerable<SqlOperation> Render(Resolved resolved, ISchemaDialect dialect, Action<ISchemaContributor, ISchemaDescriptor, SchemaSql> render)
    {
        var sql = new SchemaSql(dialect);
        render(resolved.Contributor, resolved.Descriptor, sql);
        return sql.Operations;
    }

    private static void Validate(IReadOnlyList<Resolved> target)
    {
        var diagnostics = target
            .SelectMany(r => r.Contributor.Claims(r.Descriptor).Select(claim => (claim, r)))
            .GroupBy(x => x.claim)
            .Where(g => g.Count() > 1)
            .Select(g => (SchemaDiagnostic)new SchemaDiagnostic.NameCollision(g.Key, Describe(g.First().r), Describe(g.Skip(1).First().r)))
            .ToList();
        if (diagnostics.Count > 0) throw new SchemaValidationException(diagnostics);

        static string Describe(Resolved r) => $"{r.Declaration.Owner} '{r.Descriptor.Name}' on '{r.Descriptor.Table.Name}'";
    }
}
