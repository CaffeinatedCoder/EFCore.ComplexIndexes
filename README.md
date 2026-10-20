<p align="center">
  <img width="300" height="300" align="center" alt="efcore-complexindexes-logo" src="https://github.com/user-attachments/assets/9b51234a-90e4-44af-91a3-443d159f6d1d" />
</p>

[![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/)
[![.NET](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/actions/workflows/dotnet.yml/badge.svg)](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/actions/workflows/dotnet.yml)
[![Context7](https://img.shields.io/badge/Context7-Indexed-3B82F6)](https://context7.com/caffeinatedcoder/efcore.complexindexes)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com)

> **On EF Core 11 or later?** This package supports EF Core 10 only. From 5.5.0 it throws when you
> register it on any other major version. Its successor for EF Core 11 is a new package,
> `CodoMetis.EFCore.SchemaKit`, tracked in
> [#37](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/37).

# EFCore.ComplexIndexes

EF Core lets you map value objects as complex properties, but `dotnet ef migrations add` writes no
index on their columns. With this package you declare those indexes in `OnModelCreating`, and
`migrations add` writes the `CREATE INDEX` and `DROP INDEX` for you.

## What you can do

- **Index the columns of a value object.** Single-column, composite, unique and filtered indexes on
  properties inside complex types, such as `Person.EmailAddress.Value`. You keep your logic in value
  objects and still get the indexes your queries need.
- **Filter an index.** Add a SQL `WHERE` clause, for example to leave out soft-deleted rows.
- **Mix scalar and nested properties** in one composite index, and sort each column `ASC` or `DESC`
  with `DbOrder.Asc`/`DbOrder.Desc`.
- **Index an expression** *(PostgreSQL)*, such as `lower(email)` or `to_tsvector('english', body)`.
  This works on any entity, with complex properties or without.
- **Write the expression as a lambda** *(PostgreSQL)*: `HasExpressionIndex(x => x.Email.ToLower())`.
  The package translates it, and the property paths become the real columns when you run
  `migrations add`.
- **Index members of a JSON document** *(PostgreSQL)*. For a complex property mapped with
  `ToJson()`, the same `HasComplexIndex` call creates an expression index. It reads the member the way
  Npgsql's queries do (`col ->> 'Member'`, `col #>> '{A,B}'`, cast to its type), so those queries use
  the index.
- **Keep periods from overlapping** *(PostgreSQL 18)*. A `UNIQUE … WITHOUT OVERLAPS` constraint makes
  the database reject two rows for the same key whose time periods overlap.
- **Declare exclusion constraints** *(PostgreSQL)*. An
  `EXCLUDE USING gist (… WITH =, … WITH &&) WHERE (…)` constraint is a more general form of
  uniqueness. Unlike `WITHOUT OVERLAPS` it takes a filter, so it can keep periods from overlapping
  while ignoring some rows, such as soft-deleted ones. It works on every supported PostgreSQL
  version.
- **Set SQL Server index options** *(SQL Server)*: clustered, covering (`INCLUDE`), online builds,
  fill factor and data compression, on indexes over complex properties. EF Core's own SQL Server
  generator writes them, so they need no call in your context's configuration.

| Package | NuGet | Description |
|---|---|---|
| **EFCore.ComplexIndexes** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/) | The core package: single-column, composite, unique and filtered indexes on complex type properties. Works with any EF Core relational provider. |
| **EFCore.ComplexIndexes.PostgreSQL** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.PostgreSQL.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes.PostgreSQL/) | PostgreSQL features, through [Npgsql](https://www.npgsql.org/efcore/): the GIN, GiST, BRIN, SP-GiST and Hash index methods, operator classes, covering indexes (`INCLUDE`), concurrent creation, nulls-distinct control, per-column collation, storage parameters, `NULLS FIRST/LAST`, **expression indexes** (raw SQL and **typed LINQ**), **JSON member indexes**, **temporal `UNIQUE` constraints (`WITHOUT OVERLAPS`)** and **exclusion constraints (`EXCLUDE`)**. |
| **EFCore.ComplexIndexes.SqlServer** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.SqlServer.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes.SqlServer/) | SQL Server index options on indexes over complex properties: clustered or nonclustered, covering indexes (`INCLUDE`), online builds, fill factor, sort in tempdb and data compression. EF Core's own SQL Server generator writes them, so they need no call in your context's configuration. |

### Which package do I need?

- **SQLite, or any provider where a plain B-tree index is enough:** the core package only.
- **PostgreSQL index methods, expression or JSON indexes, temporal or exclusion constraints:** add
  the PostgreSQL package.
- **SQL Server clustered, covering, online, fill-factor or compression options:** add the SQL Server
  package.

The PostgreSQL and SQL Server packages each bring the core package with them.

---

## Getting started

### Install and go

Install the package, declare your indexes in `OnModelCreating`, and run `dotnet ef migrations add`.
The package registers itself with EF Core's design-time tools, so `migrations add` picks it up with
no setup.

### Runtime wiring — the two features that need it

Two PostgreSQL features need one call in your context's configuration: expression indexes and
`NULLS FIRST`/`NULLS LAST`. EF Core's index operation has no place for them, so the package's own SQL
generator writes their SQL when the migration runs. You add that generator once, with
`UseNpgsqlComplexIndexes()`. Almost everything else goes into the migration as written by
`migrations add`, and your provider's own SQL generator runs it. The other exception is a table
rebuild on SQLite, [described below](#table-rebuilds-on-sqlite).

| Feature | Needs `UseNpgsqlComplexIndexes()` |
|---|---|
| Complex-property, composite, and filtered indexes | no |
| `DbOrder.Asc`/`Desc` sort direction | no |
| PostgreSQL index methods (GIN, GiST, BRIN, …), operator classes, `INCLUDE`, concurrent creation, nulls-distinct | no |
| Temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys | no *(since 5.0.2)* |
| Exclusion (`EXCLUDE`) constraints | no |
| SQL Server index options | no |
| **Expression indexes** — `HasExpressionIndex`, including typed LINQ and JSON member indexes | **yes** |
| **`DbOrder.NullsFirst`/`NullsLast` null ordering** | **yes** |

```csharp
services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(connectionString)
        .UseNpgsqlComplexIndexes());   // ← expression indexes and NULLS ordering
```

> **Forgot the call?** The migration fails, rather than creating an index that is quietly wrong.
> `migrations add` puts a placeholder column named `__requires_UseNpgsqlComplexIndexes__` into the
> column list of these indexes. The package's generator skips it. Npgsql's own generator writes it
> into the `CREATE INDEX`, and the migration fails when it runs, with that name in the error
> message.

> **Building your own internal service provider?** If you pass your own `IServiceProvider` to
> `.UseInternalServiceProvider(...)`, EF Core doesn't let `.UseNpgsqlComplexIndexes()` change its
> services. Register the generator on your `IServiceCollection` instead:

```csharp
var provider = new ServiceCollection()
.AddEntityFrameworkNpgsql()
.AddNpgsqlComplexIndexes() // ← Add this for expression indexes
.BuildServiceProvider();
```

### `EnsureCreated`, `GenerateCreateScript`, and the pending-changes check

If you use `Database.EnsureCreated()`, `Database.GenerateCreateScript()` or the pending-model-changes
check that `Migrate()` runs, register the package once next to your provider:

| Provider | Call |
|---|---|
| PostgreSQL | `UseNpgsqlComplexIndexes()`, the same call as above. Since 5.1.0 it covers this too. |
| SQL Server | `UseSqlServerComplexIndexes()` |
| SQLite | `UseComplexIndexes()` from the core package, after `UseSqlite()`. It also wraps SQLite's migrations SQL generator, so a table rebuild creates the table's complex indexes again. |
| Any other provider | `UseComplexIndexes()` from the core package |

`migrations add` finds the package on its own. These three don't: they use your context's own
services, and the automatic registration never reaches them. Without the call they use EF Core's
own migration code, which knows nothing about your complex indexes. `EnsureCreated()` then creates
the tables and none of the indexes, with no error. `Migrate()` doesn't warn about a complex index that
no migration has created yet.

With the PostgreSQL or SQL Server package installed, call only that package's method. The core
package's `UseComplexIndexes()` would give `EnsureCreated()` a schema without that package's
features, such as exclusion constraints. If you build your own internal service provider, each call
has a counterpart: `AddComplexIndexes()`, `AddNpgsqlComplexIndexes()` and
`AddSqlServerComplexIndexes()`.

---

## Core usage — any relational provider

### Single-column index on a complex property

```csharp
builder.ComplexProperty(x => x.EmailAddress, c =>
    c.Property(x => x.Value)
     .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);
```

This creates a unique index on the column that `EmailAddress.Value` maps to, over the rows where
`deleted_at` is null. The non-generic builder has the same overloads, so you can configure a property
by name: `c.Property("Value").HasComplexIndex()`.

#### Name properties in a filter

Write `{Property.Path}` in a filter to name a property instead of a column. `migrations add` replaces
it with the mapped column. That follows `HasColumnName`, complex members and, on PostgreSQL,
`ToJson()` members, so the filter can't drift away from the column mapping:

```csharp
c.Property(x => x.Value).HasComplexIndex(isUnique: true, filter: "{DeletedAt} IS NULL");
// WHERE "deleted_at" IS NULL   (PostgreSQL)   WHERE [deleted_at] IS NULL   (SQL Server)
```

The migration contains the resolved text, so this needs no call in your context's configuration.

Good to know:

- **What counts as a placeholder.** Only a pair of braces around a dotted property path, outside a
  single-quoted string. `'{urgent}'` and `'{"a": 1}'` stay as you wrote them.
- **A placeholder that names no property** makes `migrations add` fail.

#### Several indexes on one column

```csharp
builder.HasComplexIndex(x => x.EmailAddress.Value,
    isUnique: true, filter: "deleted_at IS NULL", indexName: "ux_person_email_active");
builder.HasComplexIndex(x => x.EmailAddress.Value,
    indexName: "ix_person_email_all");
```

A declaration on a property holds **one** index for that property. To give a column several indexes
with different filters, as in the usual soft-delete pattern, declare them on the **entity**. The
lambda can reach into complex properties. Each of these indexes needs its own explicit name, or the
declaration throws.

#### Value objects stored through a converter

The lambda can also reach through a value converter. Say a value object is stored in one column, with
`HasConversion(e => e.Value, v => new(v))`. Then `x => x.Email.Value` is that column, as long as the
type of `Value` is the converter's provider type. `x => x.CreatedAt.Year` doesn't resolve to a
column, and `migrations add` fails on it.

#### Index names

The package checks index names before the database sees them, so the database doesn't reject the
migration later.

- **Where a name must be unique** depends on the database. On SQLite it is the whole database, on
  PostgreSQL the schema, and on SQL Server the table. On PostgreSQL, tables, views and sequences
  share that namespace, including the sequence PostgreSQL creates for an identity or serial column.
- **An explicit name used twice on one entity** throws at the declaration.
- **Two declarations that end up with one name** make `migrations add` fail. One example is a
  property-level and an entity-level index on one column, which get the same default name. Another
  is a complex index and an EF Core index on two SQLite tables.
- **A name longer than the provider's limit** makes `migrations add` fail too. PostgreSQL would cut
  it to 63 bytes with a NOTICE, and the migration would run without an error. The index would then
  have a name that no declaration and no constraint-violation error ever shows. Default names are
  checked as well. The package doesn't shorten them, except for the copies of a declaration on a
  TPC type (see
  [Upgrading](docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- **Upper and lower case** count as the database counts them. SQLite and SQL Server ignore case, so
  `ix_code` and `IX_CODE` are one name there. PostgreSQL keeps names as written.

#### Table rebuilds on SQLite

On SQLite, some changes rebuild the table: altering or dropping a column, or changing a key, foreign
key, unique or check constraint. Renaming a table counts too, because EF Core renames its primary key
with it. After a rebuild, EF Core creates only its own indexes again. It runs the rebuild after every
other step of the migration, so no step of the migration can create a complex index after it. Only a
SQL generator wrapped around EF Core's can.

Call `UseComplexIndexes()` after `UseSqlite()` on the context that `dotnet ef` uses. The migration
then drops each complex index on the table and creates it again after the rebuild. The call must also
be there when you apply the migration, or the first create fails before the rebuild.

Without the call, `migrations add` fails and names the indexes. The error also offers another way.
Remove the complex index declarations on the table, make the change, then declare them again, with a
migration for each step. See
[Upgrading](docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild).

### Composite index across scalar and nested properties

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, x.EmailAddress.Value },
    isUnique: true);
```

This creates one unique index over `Name` and `EmailAddress.Value`.

#### Per-column sort direction

Wrap a member in `DbOrder.Desc(...)` to sort that column in descending order:

```csharp
builder.HasComplexCompositeIndex(
    c => new { c.HybridDateTime.DateTime, Counter = DbOrder.Desc(c.HybridDateTime.Counter), c.Id },
    indexName: "IX_Commits_DateTime_Counter_Id");
// CREATE INDEX "IX_Commits_DateTime_Counter_Id" ON ... ("DateTime", "Counter" DESC, "Id");
```

`DbOrder.Asc(...)` is the default. A wrapped member is a method call, so C# needs you to **name it**
in the anonymous type, as with `Counter =` above.

Every relational provider writes the direction (SQL Server, SQLite, PostgreSQL), because it maps to
EF Core's own `CreateIndexOperation.IsDescending`. It needs no call in your context's configuration. Declaring
an index over the same columns again updates its direction.

Good to know:

- **Markers of the same kind don't nest.** `DbOrder.Asc(DbOrder.Desc(x.A))` contradicts itself and
  throws. Markers of different kinds combine in any order.
- **Where nulls sort** is a PostgreSQL feature: see
  [null ordering](docs/postgresql-indexes.md#per-column-null-ordering).

### Reading declarations back

You can read every index you declared back from the model. That lets you check your own conventions
in code, instead of trusting each configuration to follow them. This works on the finalized `context.Model`,
and on the mutable model inside `OnModelCreating`:

```csharp
// "Every unique index on a withdrawable aggregate is filtered to live rows."
var unfiltered = modelBuilder.Model.GetEntityTypes()
    .Where(IsWithdrawable)
    .SelectMany(e => e.GetComplexIndexes())
    .Where(ix => ix.IsUnique && ix.Filter is null)
    .ToList();

var byName = modelBuilder.Model.FindComplexIndex("ux_person_email_active");
```

`GetComplexIndexes()` returns every kind of declaration as a `ComplexIndexDeclaration`:
property-level, entity-level, composite and expression indexes. Each one has:

- **`Parts`**: property paths, or expressions for an expression index.
- **`IsUnique` and `Filter`.**
- **`Name`**, the name you gave it. It is null when the package builds a default name from the
  resolved column names, and `FindComplexIndex` doesn't find those.
- **The provider options**, for entity-level declarations.

You get what you declared. The package works out column names only when it builds migration
operations: for `migrations add`, and for `EnsureCreated()` and the other calls above.
`GetDeclaredComplexIndexes()` leaves out inherited declarations, and lists each one on the type that
declares it. `migrations add` reads the model through the same code, so what you check is what the
migration creates. PostgreSQL exclusion constraints have the same methods: see
[reading constraints back](docs/postgresql-constraints.md#reading-constraints-back).

### Amending declarations

You can also apply a convention instead of checking it. On the mutable model,
`AddComplexIndexFilter` adds a predicate to the filter of every declaration you select, property-level
and entity-level alike:

```csharp
// At the end of OnModelCreating, after the configurations have been applied:
foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(IsWithdrawable))
    entityType.AddComplexIndexFilter("{RevokedAt} IS NULL", ix => ix.IsUnique);
```

- **An unfiltered index** gets the predicate as its filter.
- **A filtered index** gets `(existing) AND (predicate)`.
- **An index that already has it**, as its whole filter or as the `AND (predicate)` this call added,
  stays as it is. So you can repeat the call safely.

The call changes only what is declared when it runs. That's why it goes after the configurations,
and why the read-back check above is still useful for anything declared later.

`AddComplexIndex` adds an entity-level declaration, and follows the fluent API's rules for duplicates
and names. Exclusion constraints have `AddExclusionConstraintFilter`: see
[amending constraints](docs/postgresql-constraints.md#amending-constraints).

---

## Documentation

Each provider has its own pages:

| Page | Covers |
|---|---|
| **[PostgreSQL — indexes](docs/postgresql-indexes.md)** | Index methods (GIN, GiST, BRIN, SP-GiST, Hash), operator classes, `INCLUDE`, expression indexes in raw SQL and typed LINQ, typed filters, JSON member indexes, `NULLS FIRST/LAST` |
| **[PostgreSQL — temporal and exclusion constraints](docs/postgresql-constraints.md)** | `UNIQUE … WITHOUT OVERLAPS`, temporal foreign keys (`PERIOD`), `EXCLUDE` constraints with a `WHERE` filter, reading and amending them, the `btree_gist` extension |
| **[SQL Server](docs/sqlserver.md)** | Clustered or nonclustered, covering (`INCLUDE`), online builds, fill factor, sort in tempdb, data compression, and the declarations SQL Server can't express |
| **[Upgrading](docs/upgrading.md)** | What the first `migrations add` after an upgrade does, and which models fail now where they passed before |

To work on the package itself, start with [CONTRIBUTING.md](CONTRIBUTING.md) for the setup and the
quality bar. [CLAUDE.md](CLAUDE.md) records the architecture: which hook point a feature must use,
why annotations on a property reach a migration only from an allow-list, and why the order of the
migration's steps matters.

---

## Changelog

[CHANGELOG.md](CHANGELOG.md) covers all three packages. Each package also has its own, so NuGet shows
the history of that package:
[core](src/EFCore.ComplexIndexes/CHANGELOG.md),
[PostgreSQL](src/EFCore.ComplexIndexes.PostgreSQL/CHANGELOG.md),
[SQL Server](src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md).

Before you upgrade, read [docs/upgrading.md](docs/upgrading.md). It says what the first
`dotnet ef migrations add` after the upgrade writes, and which models fail now where they passed
before.

---

## Contributing and project practices

Bug reports and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers the setup and
the quality bar this package holds itself to. Please report security issues privately, as
[SECURITY.md](SECURITY.md) describes.

A large part of this codebase was written with AI assistance, directed and reviewed by the
maintainer. [CONTRIBUTING.md](CONTRIBUTING.md#ai-assisted-development) explains what that means in
practice, and how each change is checked before it ships.
