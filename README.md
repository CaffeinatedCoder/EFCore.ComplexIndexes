<p align="center">
  <img width="300" height="300" align="center" alt="efcore-complexindexes-logo" src="https://github.com/user-attachments/assets/9b51234a-90e4-44af-91a3-443d159f6d1d" />
</p>

[![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/)
[![.NET](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/actions/workflows/dotnet.yml/badge.svg)](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/actions/workflows/dotnet.yml)
[![Context7](https://img.shields.io/badge/Context7-Indexed-3B82F6)](https://context7.com/caffeinatedcoder/efcore.complexindexes)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com)

## Index support for complex type properties in EF Core migrations — the missing piece for value object-driven architectures.
EF Core 8.0 introduced complex properties, but migration tooling doesn't automatically generate indexes for these nested value objects. This NuGet package bridges that gap with a clean, fluent API for defining single-column, composite, unique, and filtered indexes directly on complex type properties — and, on PostgreSQL, **expression (functional) indexes**.

### Why it matters:
- **Value Object Indexing**: Seamlessly add database indexes to properties buried inside complex types (e.g., `Person.EmailAddress.Value`)
- **DDD-Friendly**: Supports the Domain-Driven Design pattern of encapsulating logic in value objects without sacrificing database performance
- **Migration-Aware**: Automatically generates proper `CREATE INDEX` and `DROP INDEX` operations during EF Core migrations
- **Flexible Filtering**: Supports SQL `WHERE` clauses for filtered indexes (e.g., soft deletes)
- **Composite Indexes**: Define multi-column indexes spanning both scalar and nested properties with a single, intuitive expression — with per-column `ASC`/`DESC` ordering via `DbOrder.Asc`/`DbOrder.Desc`
- **Expression Indexes** *(PostgreSQL)*: Index arbitrary SQL expressions such as `lower(email)` or `to_tsvector('english', body)` — including on plain, non-complex entities
- **Typed Expression Indexes** *(PostgreSQL)*: Write `HasExpressionIndex(x => x.Email.ToLower())` and let the package translate it — property paths resolve to real columns at migration time
- **JSON Member Indexes** *(PostgreSQL)*: Index members of complex properties mapped with `ToJson()` — the same `HasComplexIndex` declaration becomes a `(col ->> 'Member')` expression index automatically
- **Temporal Constraints** *(PostgreSQL 18)*: Declare `UNIQUE … WITHOUT OVERLAPS` constraints to guarantee no two rows occupy overlapping time periods — the database enforces scheduling integrity for you
- **Exclusion Constraints** *(PostgreSQL)*: Declare `EXCLUDE USING gist (… WITH =, … WITH &&) WHERE (…)` constraints — filtered overlap protection (e.g. ignore soft-deleted rows), on any supported PostgreSQL version
- **SQL Server Options** *(SQL Server)*: Clustered, covering (`INCLUDE`), online-built, fill-factor, and data-compression index options on complex-property indexes — rendered by the stock SQL Server generator, no runtime wiring

| Package | NuGet | Description |
|---|---|---|
| **EFCore.ComplexIndexes** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/) | Core library — single-column, composite, unique, and filtered indexes on complex type properties. Works with any EF Core relational provider. |
| **EFCore.ComplexIndexes.PostgreSQL** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.PostgreSQL.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes.PostgreSQL/) | PostgreSQL extensions via [Npgsql](https://www.npgsql.org/efcore/) — adds GIN, GiST, BRIN, SP-GiST, and Hash index methods, operator classes, covering indexes (`INCLUDE`), concurrent creation, nulls-distinct control, `NULLS FIRST/LAST`, **expression (functional) indexes** (raw SQL and **typed LINQ**), **JSON member indexes**, **temporal `UNIQUE` constraints (`WITHOUT OVERLAPS`)**, and **exclusion constraints (`EXCLUDE`)**. |
| **EFCore.ComplexIndexes.SqlServer** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.SqlServer.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes.SqlServer/) | SQL Server extensions — clustered/nonclustered control, covering indexes (`INCLUDE`), online index builds, fill factor, sort-in-tempdb, and data compression on complex-property indexes. Rendered by the stock SQL Server generator; no runtime wiring. |

> **Which package do I need?**
> Install only the **core** package if you use SQLite or any provider where the default B-tree index type is sufficient.
> Add the **PostgreSQL** package for PostgreSQL-specific index types, expression/JSON indexes, or temporal/exclusion constraints; add the **SQL Server** package for clustered/covering/online/fill-factor/compression options. Both include the core automatically.

---

## Getting started

### Install and go

Everything is wired up automatically through EF Core's design-time tooling. Install the package, configure your indexes in `OnModelCreating`, and run `dotnet ef migrations add` — **zero additional ceremony**.

### Runtime wiring — the two features that need it

Almost everything is rendered into the migration at design time and applies through your provider's
stock SQL generator. Two PostgreSQL features cannot be: they have no slot on EF Core's native index
operation, so they are rendered when migrations are *applied*, by a SQL generator you opt into
**once**.

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

> **Forgot the wiring?** You will not get a silently wrong index. Indexes that need the custom
> generator carry a sentinel entry `__requires_UseNpgsqlComplexIndexes__` in the scaffolded column
> list: the custom generator ignores it, and the stock generator fails **loudly** with that name in
> the error message.

> Using a custom Internal Service Provider? If your application builds its own `IServiceProvider` and passes it to `.UseInternalServiceProvider(...)`, EF Core prevents `.UseNpgsqlComplexIndexes()` from modifying services. Instead, register the generator directly on your `IServiceCollection`:

```csharp
var provider = new ServiceCollection()
.AddEntityFrameworkNpgsql()
.AddNpgsqlComplexIndexes() // ← Add this for expression indexes
.BuildServiceProvider();
```

---

## Core usage — any relational provider

### Single-column index on a complex property

```csharp
builder.ComplexProperty(x => x.EmailAddress, c =>
    c.Property(x => x.Value)
     .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);
```

A property-level declaration holds **one** index per property. To give the same column several
differently-filtered indexes (the classic soft-delete pattern), declare them at the **entity level**
— the selector reaches into complex properties, and each index needs its own explicit name:

```csharp
builder.HasComplexIndex(x => x.EmailAddress.Value,
    isUnique: true, filter: "deleted_at IS NULL", indexName: "ux_person_email_active");
builder.HasComplexIndex(x => x.EmailAddress.Value,
    indexName: "ix_person_email_all");
```

Index names must be unique per table, and the package enforces it rather than letting the database
reject the migration: reusing a name throws at the declaration, and two declarations that resolve to
the same name — including a property-level and an entity-level index over one column, which share a
default name — throw during `dotnet ef migrations add`.

### Composite index across scalar and nested properties

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, x.EmailAddress.Value },
    isUnique: true);
```

#### Per-column sort direction

Wrap any member in `DbOrder.Desc(...)` (or `DbOrder.Asc(...)`, the default) to control its sort order. Because a wrapped member is a method call, C# requires you to **name it** in the anonymous type:

```csharp
builder.HasComplexCompositeIndex(
    c => new { c.HybridDateTime.DateTime, Counter = DbOrder.Desc(c.HybridDateTime.Counter), c.Id },
    indexName: "IX_Commits_DateTime_Counter_Id");
// CREATE INDEX "IX_Commits_DateTime_Counter_Id" ON ... ("DateTime", "Counter" DESC, "Id");
```

Direction maps to EF Core's native `CreateIndexOperation.IsDescending`, so it is rendered by **every relational provider** (SQL Server, SQLite, PostgreSQL) — no extra wiring required. Re-declaring an index over the same columns updates its direction.

Markers of different kinds compose in any order; markers of the same kind do not — `DbOrder.Asc(DbOrder.Desc(x.A))` is a contradiction and throws. To control where nulls sort, see [null ordering](docs/postgresql-indexes.md#per-column-null-ordering) (PostgreSQL only).

---

## Documentation

Provider-specific features live in their own pages:

| Page | Covers |
|---|---|
| **[PostgreSQL — indexes](docs/postgresql-indexes.md)** | Index methods (GIN, GiST, BRIN, SP-GiST, Hash), operator classes, `INCLUDE`, expression (functional) indexes in raw SQL and typed LINQ, JSON member indexes, `NULLS FIRST/LAST` |
| **[PostgreSQL — temporal and exclusion constraints](docs/postgresql-constraints.md)** | `UNIQUE … WITHOUT OVERLAPS`, temporal foreign keys (`PERIOD`), `EXCLUDE` constraints with `WHERE` predicates, the `btree_gist` extension |
| **[SQL Server](docs/sqlserver.md)** | Clustered/nonclustered, covering (`INCLUDE`), online builds, fill factor, sort-in-tempdb, data compression — and the declarations SQL Server rejects outright |

Working on the package itself: [CONTRIBUTING.md](CONTRIBUTING.md) covers the setup and the quality
bar, and [CLAUDE.md](CLAUDE.md) is the architectural record — which seam a feature must use, why the
annotation flow is a whitelist, why operation ordering is load-bearing.

---

## Changelog

[CHANGELOG.md](CHANGELOG.md) covers all three packages. Each package also carries its own, so NuGet
shows package-specific history:
[core](src/EFCore.ComplexIndexes/CHANGELOG.md),
[PostgreSQL](src/EFCore.ComplexIndexes.PostgreSQL/CHANGELOG.md),
[SQL Server](src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md).

---

## Contributing and project practices

Bug reports and pull requests are welcome — [CONTRIBUTING.md](CONTRIBUTING.md) covers the setup and
the quality bar this package holds itself to. Security reports go privately through
[SECURITY.md](SECURITY.md).

A substantial portion of this codebase was written with AI assistance, under maintainer direction and
review. [CONTRIBUTING.md](CONTRIBUTING.md#ai-assisted-development) explains what that means in
practice, and how every change is verified before it ships.

---

The package integrates seamlessly with EF Core's design-time tooling. Apart from the one-time `UseNpgsqlComplexIndexes()` call required by expression indexes and `NULLS FIRST/LAST`, there is no additional ceremony — just configure and migrate.
