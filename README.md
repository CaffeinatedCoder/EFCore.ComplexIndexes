<p align="center">
  <img width="300" height="300" align="center" alt="efcore-complexindexes-logo" src="https://github.com/user-attachments/assets/9b51234a-90e4-44af-91a3-443d159f6d1d" />
</p>

[![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/)

## Index support for complex type properties in EF Core migrations — the missing piece for value object-driven architectures.
EF Core 8.0 introduced complex properties, but migration tooling doesn't automatically generate indexes for these nested value objects. This NuGet package bridges that gap with a clean, fluent API for defining single-column, composite, unique, and filtered indexes directly on complex type properties — and, on PostgreSQL, **expression (functional) indexes**.

### Why it matters:
- **Value Object Indexing**: Seamlessly add database indexes to properties buried inside complex types (e.g., `Person.EmailAddress.Value`)
- **DDD-Friendly**: Supports the Domain-Driven Design pattern of encapsulating logic in value objects without sacrificing database performance
- **Migration-Aware**: Automatically generates proper `CREATE INDEX` and `DROP INDEX` operations during EF Core migrations
- **Flexible Filtering**: Supports SQL `WHERE` clauses for filtered indexes (e.g., soft deletes)
- **Composite Indexes**: Define multi-column indexes spanning both scalar and nested properties with a single, intuitive expression — with per-column `ASC`/`DESC` ordering via `DbOrder.Asc`/`DbOrder.Desc`
- **Expression Indexes** *(PostgreSQL)*: Index arbitrary SQL expressions such as `lower(email)` or `to_tsvector('english', body)` — including on plain, non-complex entities

| Package | NuGet | Description |
|---|---|---|
| **EFCore.ComplexIndexes** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/) | Core library — single-column, composite, unique, and filtered indexes on complex type properties. Works with any EF Core relational provider. |
| **EFCore.ComplexIndexes.PostgreSQL** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.PostgreSQL.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes.PostgreSQL/) | PostgreSQL extensions via [Npgsql](https://www.npgsql.org/efcore/) — adds GIN, GiST, BRIN, SP-GiST, and Hash index methods, operator classes, covering indexes (`INCLUDE`), concurrent creation, nulls-distinct control, and **expression (functional) indexes**. |

> **Which package do I need?**
> Install only the **core** package if you use SQL Server, SQLite, or any provider where the default B-tree index type is sufficient.
> Add the **PostgreSQL** package when you need PostgreSQL-specific index types or expression indexes — it includes the core automatically.

---

## Getting started

### Complex-property indexes (core)

The complex-property, composite, and provider-method index features are wired up automatically through EF Core's design-time tooling. Just install the package, configure your indexes in `OnModelCreating`, and run `dotnet ef migrations add` — **zero additional ceremony**.

### Expression indexes (PostgreSQL) — one-time setup

Expression indexes are the **one exception**: rendering `CREATE INDEX … ((expr))` requires a custom migrations SQL generator that runs when migrations are *applied*. EF Core does not auto-wire runtime services, so you must opt in **once** when configuring your `DbContext`:

```csharp
services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(connectionString)
        .UseNpgsqlComplexIndexes());   // ← required for HasExpressionIndex(...)
```

> ⚠️ **`UseNpgsqlComplexIndexes()` is a prerequisite for `HasExpressionIndex`.**
> Without it, applying a migration that contains an expression index will fail (the stock generator can't render the expression). All other features — complex-property indexes, composite indexes, and the GIN/GiST/etc. methods — do **not** require this call; they flow through Npgsql's own SQL generator.

---

## Usage

### Single-column index on a complex property

```csharp
builder.ComplexProperty(x => x.EmailAddress, c =>
    c.Property(x => x.Value)
     .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);
```

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

### PostgreSQL index methods on a complex property

Use the builder-callback overload to reach the PostgreSQL-specific options (GIN, GiST, BRIN, SP-GiST, Hash, operator classes, `INCLUDE`, concurrent creation, nulls-distinct):

```csharp
builder.ComplexProperty(x => x.Payload, c =>
    c.Property(x => x.Json)
     .HasComplexIndex(idx => idx
         .UseGin()
         .HasOperators("jsonb_path_ops"))
);
```

### Expression (functional) indexes — PostgreSQL

> Requires `UseNpgsqlComplexIndexes()` (see [Getting started](#expression-indexes-postgresql--one-time-setup)).
> Available as an extension on `EntityTypeBuilder<TEntity>`, so it works on any entity — complex or not.

**Each string is emitted verbatim** — there is no property-to-column resolution and no automatic quoting. Write the final SQL exactly as it should appear inside the index, referencing real column names.

**Single expression:**

```csharp
// CREATE INDEX "IX_person_lowerlastname" ON person ((lower(last_name)));
builder.HasExpressionIndex("lower(last_name)");
```

**With unique / filter / explicit name:**

```csharp
builder.HasExpressionIndex(
    "lower(email)",
    isUnique:  true,
    filter:    "deleted_at IS NULL",
    indexName: "ix_person_email_ci");
```

**Multiple ordered parts + provider options (builder callback):**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("country")            // a plain column, written as raw SQL
    .Expression("lower(email)")       // a SQL expression
    .IsUnique()
    .HasFilter("deleted_at IS NULL")
    .HasName("ix_person_country_email_ci"));
// CREATE UNIQUE INDEX "ix_person_country_email_ci"
//   ON person ((country), (lower(email)))
//   WHERE deleted_at IS NULL;
```

**Full-text / JSONB with a GIN index:**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("to_tsvector('english', body)")
    .UseGin());
// CREATE INDEX ... ON articles USING gin ((to_tsvector('english', body)));
```

**Covering expression index (`INCLUDE`):**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("lower(email)")
    .IsUnique()
    .IncludeProperties("display_name"));
```

#### Quoting tip

Strings are passed through untouched, so identifiers that need PostgreSQL quoting (e.g. PascalCase columns) must include the quotes yourself. C# raw string literals keep this readable:

```csharp
// CREATE INDEX ... ON "People" ((lower("Email")));
builder.HasExpressionIndex(""" lower("Email") """.Trim());
```

> **Roadmap:** the expression API is built on an `IIndexExpression` seam. A future LINQ add-on will let you write `HasExpressionIndex(x => x.Email.ToLower())` and have it translated to SQL — flowing through the exact same pipeline.

---

The package integrates seamlessly with EF Core's design-time tooling. Apart from the one-time `UseNpgsqlComplexIndexes()` call for expression indexes, there is no additional ceremony — just configure and migrate.
