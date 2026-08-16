# EFCore.ComplexIndexes

Index support for **complex type properties** in EF Core migrations — the missing piece for value
object-driven architectures.

EF Core models complex properties (value objects) but its migration tooling does not generate
indexes for their nested columns. This package hooks into EF Core's design-time pipeline and emits
the `CREATE INDEX` / `DROP INDEX` operations for you.

This is the **core, provider-agnostic package**. It works with any EF Core relational provider.
For provider-specific index features, add a satellite package — each one includes this package
automatically:

| Package | Adds |
|---|---|
| [EFCore.ComplexIndexes.PostgreSQL](https://www.nuget.org/packages/EFCore.ComplexIndexes.PostgreSQL/) | GIN/GiST/BRIN/SP-GiST/Hash methods, operator classes, `INCLUDE`, `NULLS FIRST/LAST`, expression &amp; JSON indexes, temporal and exclusion constraints |
| [EFCore.ComplexIndexes.SqlServer](https://www.nuget.org/packages/EFCore.ComplexIndexes.SqlServer/) | Clustered, covering (`INCLUDE`), online builds, fill factor, sort-in-tempdb, data compression |

---

## Setup

None. The migration differ is registered through EF Core's design-time tooling automatically —
install the package, declare your indexes in `OnModelCreating`, and run `dotnet ef migrations add`.

## Usage

### Single-column index on a complex property

```csharp
builder.ComplexProperty(x => x.EmailAddress, c =>
    c.Property(x => x.Value)
     .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);
```

Column names are resolved against the real model, so both convention-based names (`Origin_Source`)
and explicit `HasColumnName` overrides are honored.

### Several indexes over one column

A property-level declaration holds **one** index per property. To give a column several
differently-filtered indexes (the classic soft-delete pattern), declare them at the entity level —
the selector reaches into complex properties, and each index needs its own explicit name:

```csharp
builder.HasComplexIndex(x => x.EmailAddress.Value,
    isUnique: true, filter: "deleted_at IS NULL", indexName: "ux_person_email_active");
builder.HasComplexIndex(x => x.EmailAddress.Value,
    indexName: "ix_person_email_all");
```

Index names must be unique per table, and the package enforces it rather than letting the database
reject the migration.

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

Direction maps onto EF Core's native `CreateIndexOperation.IsDescending`, so **every** relational
provider renders it. Because a wrapped member is a method call, C# requires you to name it in the
anonymous type.

`DbOrder.NullsFirst`/`NullsLast` are declared here too, but rendering them is provider-specific —
see the PostgreSQL package.

---

## Documentation

Full documentation, including every provider-specific feature:
**https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes**

## Changelog

[This package's changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/src/EFCore.ComplexIndexes/CHANGELOG.md),
or the [root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covering all three packages.

MIT licensed.
