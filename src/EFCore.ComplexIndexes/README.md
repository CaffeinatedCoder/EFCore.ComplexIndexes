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

## Changelog

### 5.0.3

- **Changed:** the `Microsoft.EntityFrameworkCore.Abstractions` dependency is now `[10.0.0, 11.0.0)`.
  This package subclasses `MigrationsModelDiffer` and calls internals EF marks as changeable without
  notice in any release, so an open-ended floor let NuGet resolve a future major where the differ can
  break — in your `dotnet ef` run, not anywhere visible from here. Nothing changes if you are on EF
  Core 10: NuGet resolves the lowest version in a range, so restore still picks 10.0.0.
- **New:** the public API is fully documented. The shipped `.xml` had 64 gaps, so IntelliSense came up
  empty on parts of the fluent API, the annotation keys, `CompositeIndexDefinition` and
  `IndexPartDefinition`.
- **Tests:** a consumer smoke test packs the packages, installs them into a throwaway project outside
  the repository, and runs a real `dotnet ef migrations add`, asserting on the scaffolded content —
  the delivery chain (restore, `.targets` injection, design-time discovery, differ selection) was
  previously only ever verified in pieces.

### 5.0.2

- **Fixed:** the design-time migration differ is now selected deterministically when a provider
  satellite is installed. This package's design-time attribute rides along transitively next to the
  satellite's, and EF Core resolves last-registration-wins, so NuGet's restore order decided which
  differ ran — and this one winning silently drops every provider-specific feature.
- **Fixed:** duplicate index names are rejected instead of producing a migration that fails at apply
  time. Reusing an explicit name throws at the declaration; collisions between default names —
  including a property-level and an entity-level index over the same column — throw during
  `dotnet ef migrations add`.
- **Fixed:** selectors that read a captured variable or static member instead of the lambda
  parameter (`x => captured.Name`) throw at the declaration, naming the offending selector.
  Previously they produced an unmatchable property path that failed much later with an opaque
  resolution error.
- **Fixed:** `DbOrder.Asc` now marks a column ascending, and combining it with `DbOrder.Desc` (or
  `NullsFirst` with `NullsLast`) throws rather than silently picking one. Repeating a marker is fine.
- **Fixed:** provider validation runs through a scoped extension point instead of sweeping the
  finished operation list, so satellites can no longer reject index operations this package did not
  create.
- **Fixed:** array-valued provider annotations (operator classes, `INCLUDE` lists) compare by
  content rather than by reference in `CompositeIndexDefinition`.

### 5.0.1

- **Tests:** the differ is now exercised against *real* model snapshots — generated as C#, compiled
  in-memory, and rebuilt exactly as `dotnet ef migrations add` does — guarding the whole feature set
  against snapshot round-trip churn.

### 5.0.0

- **Fixed:** custom `DROP INDEX` operations are ordered *before* the base migration operations.
  Moving an index between a native `HasIndex` and a complex-index declaration previously scaffolded
  a migration that created the new index before dropping the same-named old one.
- **Fixed:** integral provider-annotation values (e.g. fill factor) survive snapshot round-trips as
  `int` instead of degrading to `double`, which made generators drop them.
- **Changed:** property annotations reach index operations through a provider **whitelist** instead
  of a blacklist. Column facets such as `Relational:ColumnName` no longer leak into scaffolded
  migrations, closing a class of phantom drop/create churn.
- **Changed:** an indexed property that resolves to no column throws at `migrations add` instead of
  silently dropping the index.
- **Changed:** two indexes over the same columns may coexist when their filters differ (both must be
  named); re-declaring with the same filter updates in place.
- **Changed:** a name-only index change emits `RenameIndexOperation` on providers that can rename
  standalone; renaming a table no longer drops and recreates the complex indexes it carries.
- **New:** entity-level `HasComplexIndex(x => x.Complex.Prop, …)` for single-column indexes.
- **New:** per-column `ASC`/`DESC` via `DbOrder.Asc`/`DbOrder.Desc`.

---

Full documentation, including every provider-specific feature:
**https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes**

MIT licensed.
