# EFCore.ComplexIndexes

> **On EF Core 11 or later?** This package supports EF Core 10 only. From 5.5.0 it throws when you
> register it on any other major version. Its successor for EF Core 11 is a new package,
> `CodoMetis.EFCore.SchemaKit`, tracked in
> [#37](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/37).

Indexes on **complex type properties** in EF Core migrations, for models built on value objects.

EF Core lets you map value objects as complex properties, but `dotnet ef migrations add` writes no
index on their columns. With this package you declare those indexes in `OnModelCreating`, and
`migrations add` writes the `CREATE INDEX` and `DROP INDEX` for you.

This is the **core package**, and it works with any EF Core relational provider. For a provider's own
index features, add its package. Each one brings this package with it:

| Package | Adds |
|---|---|
| [EFCore.ComplexIndexes.PostgreSQL](https://www.nuget.org/packages/EFCore.ComplexIndexes.PostgreSQL/) | GIN/GiST/BRIN/SP-GiST/Hash methods, operator classes, `INCLUDE`, `NULLS FIRST/LAST`, expression &amp; JSON indexes, temporal and exclusion constraints |
| [EFCore.ComplexIndexes.SqlServer](https://www.nuget.org/packages/EFCore.ComplexIndexes.SqlServer/) | Clustered, covering (`INCLUDE`), online builds, fill factor, sort-in-tempdb, data compression |

---

## Setup

None. The package registers itself with EF Core's design-time tools. Install it, declare your indexes
in `OnModelCreating`, and run `dotnet ef migrations add`.

## Usage

### Single-column index on a complex property

```csharp
builder.ComplexProperty(x => x.EmailAddress, c =>
    c.Property(x => x.Value)
     .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);
```

The package finds the real column names in your model. It follows both EF Core's naming conventions
(`Origin_Source`) and names you set with `HasColumnName`.

A filter can name properties instead of columns. `migrations add` turns `filter: "{DeletedAt} IS NULL"`
into the mapped column, quoted for your provider, and writes the result into the migration. The
lambda that picks the column reaches into complex properties. For a value object stored through a converter, it reaches one step
further: `x => x.Email.Value` is the converted column.

### Several indexes over one column

```csharp
builder.HasComplexIndex(x => x.EmailAddress.Value,
    isUnique: true, filter: "deleted_at IS NULL", indexName: "ux_person_email_active");
builder.HasComplexIndex(x => x.EmailAddress.Value,
    indexName: "ix_person_email_all");
```

A declaration on a property holds **one** index for that property. To give a column several indexes
with different filters, as in the usual soft-delete pattern, declare them on the entity. The lambda
can reach into complex properties. Each of these indexes needs its own explicit name, or the
declaration throws.

`migrations add` checks every index name, so the database doesn't reject or quietly shorten one:

- **Unique where the provider needs it:** across the database on SQLite, per schema on PostgreSQL,
  per table on SQL Server.
- **Within the provider's length limit:** 63 bytes on PostgreSQL, which would otherwise shorten the
  name without an error.

On SQLite, some migrations rebuild a table: altering or dropping a column, changing a key or
constraint, or renaming the table. A rebuild drops the table's complex indexes, and EF Core's SQL
generator runs it after everything else. Call `UseComplexIndexes()` after `UseSqlite()`, both where
`dotnet ef` builds the context and where the migration runs, and the indexes are created again after
the rebuild. Without the call, `dotnet ef migrations add` fails and says how to split the change into
several migrations
([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild)).

### Composite index across scalar and nested properties

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, x.EmailAddress.Value },
    isUnique: true);
```

### Per-column sort direction

```csharp
builder.HasComplexCompositeIndex(
    c => new { c.Created, Counter = DbOrder.Desc(c.Version.Counter) },
    indexName: "ix_commits_created_counter");
// CREATE INDEX ix_commits_created_counter ON ... (created, counter DESC);
```

**Every** relational provider writes the direction, because it maps to EF Core's own
`CreateIndexOperation.IsDescending`. A wrapped member is a method call, so C# needs you to name it in
the anonymous type.

You can declare `DbOrder.NullsFirst`/`NullsLast` here too, but only the PostgreSQL package writes
them. On SQL Server, and on providers without their own package such as SQLite, an index that uses
them makes `dotnet ef migrations add` fail.

---

### Reading declarations back

`GetComplexIndexes()` on an entity type, or on the model, returns every declaration: property-level,
entity-level, composite and expression indexes. Each one has its parts, `IsUnique`, `Filter` and the
`Name` you gave it, so you can check your own conventions in code. `FindComplexIndex(name)` finds one
by that name. This works on the mutable model inside `OnModelCreating` too, and `migrations add`
reads declarations through the same code.

```csharp
var unfiltered = modelBuilder.Model.GetEntityTypes()
    .SelectMany(e => e.GetComplexIndexes())
    .Where(ix => ix.IsUnique && ix.Filter is null);
```

You can also apply a convention instead of checking it. On the mutable model,
`AddComplexIndexFilter(predicate, where)` adds a predicate with `AND` to the filter of every
declaration you select, and repeating the call changes nothing. `AddComplexIndex(definition)` adds a declaration, and
follows the fluent API's rules for duplicates and names. Both change only what is declared when they
run, so call them after the configurations.

## Documentation

Full documentation, including every provider's features:
**https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes**

## Changelog

[This package's changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/src/EFCore.ComplexIndexes/CHANGELOG.md),
or the [root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covering all three packages.

MIT licensed.
