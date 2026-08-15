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

Markers of different kinds compose in any order; markers of the same kind do not — `DbOrder.Asc(DbOrder.Desc(x.A))` is a contradiction and throws. To control where nulls sort, see [null ordering](#per-column-null-ordering) (PostgreSQL only).

---

## PostgreSQL

### Per-column null ordering

`DbOrder.NullsFirst(...)` / `DbOrder.NullsLast(...)` control where nulls sort; the markers compose with `Desc`:

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, Reviewed = DbOrder.NullsLast(DbOrder.Desc(x.ReviewedAt)) });
// CREATE INDEX ... ON ... (name, reviewed_at DESC NULLS LAST);
```

Null ordering has no slot on EF's native index operation, so these indexes render through the package's PostgreSQL SQL generator — they require the one-time [`UseNpgsqlComplexIndexes()`](#runtime-wiring--the-two-features-that-need-it) wiring, and the SQL Server differ rejects the markers (SQL Server has no `NULLS FIRST/LAST` syntax).

### Index methods on a complex property

Use the builder-callback overload to reach the PostgreSQL-specific options (GIN, GiST, BRIN, SP-GiST, Hash, operator classes, `INCLUDE`, concurrent creation, nulls-distinct):

```csharp
builder.ComplexProperty(x => x.Payload, c =>
    c.Property(x => x.Json)
     .HasComplexIndex(idx => idx
         .UseGin()
         .HasOperators("jsonb_path_ops"))
);
```

### Expression (functional) indexes

> Requires [`UseNpgsqlComplexIndexes()`](#runtime-wiring--the-two-features-that-need-it).
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

**Descending parts:** call `.Descending()` after any part to sort it descending:

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("created_at").Descending()
    .Expression("lower(email)"));
// CREATE INDEX ... ON person ((created_at) DESC, (lower(email)));
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

### Typed (LINQ) expression indexes

> Requires [`UseNpgsqlComplexIndexes()`](#runtime-wiring--the-two-features-that-need-it), like all expression indexes.

Instead of raw SQL, pass a lambda — property paths stay symbolic and are resolved against the
finalized model at `migrations add` time, so `HasColumnName`, complex-property columns, and even
`ToJson()` members are honored automatically:

```csharp
builder.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true);
// CREATE UNIQUE INDEX ... ON people ((lower("email")));

builder.HasExpressionIndex(x => (x.Nickname ?? x.FirstName) + " " + x.LastName);
// CREATE INDEX ... ON people (((coalesce("nickname", "first_name") || ' ') || "last_name"));
```

The supported subset is deliberately small and fails loudly: `ToLower`/`ToUpper`, `Trim`/`TrimStart`/`TrimEnd`, `Substring` (1-based conversion handled), `Replace`, `string.Length`, string concatenation (`+`), null coalescing (`??`), and constants (captured variables are evaluated and inlined invariant-culture). Anything else throws `NotSupportedException` **at declaration time** with a pointer to the raw-SQL overload.

### JSON member indexes

> Requires [`UseNpgsqlComplexIndexes()`](#runtime-wiring--the-two-features-that-need-it) — JSON member indexes are expression indexes under the hood.

When a complex property is mapped to JSON with `ToJson()`, its members have no table columns — yet
the **same index declarations keep working**: the differ resolves them to `->>`
extraction expressions instead. Moving a value object between scalar columns and a JSON document
does not force you to rewrite its indexes:

```csharp
builder.ComplexProperty(x => x.Name, c => c.ToJson("name"));

// Entity level …
builder.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
// … or property level, inside the complex property:
//   c.Property(x => x.ShortName).HasComplexIndex(isUnique: true);

// ALTER: CREATE UNIQUE INDEX "ux_employer_short_name" ON employers (("name" ->> 'ShortName'));
```

Nested complex types become `->` segments (`("profile" -> 'Address' ->> 'City')`), and
`HasJsonPropertyName` is honored. Members are extracted as **text**; for typed comparisons or
ordering semantics use `HasExpressionIndex` with an explicit cast.

### Temporal `UNIQUE` constraints (`WITHOUT OVERLAPS`) — requires PostgreSQL 18

> No runtime wiring required — the DDL is rendered at design time into the migration itself.
> Available as an extension on `EntityTypeBuilder<TEntity>`, so it works on any entity — complex or not.

PostgreSQL 18 introduced `WITHOUT OVERLAPS` for unique constraints — a long-requested feature for scheduling, booking, and versioning scenarios. Instead of only checking *"is this exact value already present?"*, the database enforces *"no two rows for the same key have overlapping time periods"*.

```sql
ALTER TABLE bookings
  ADD CONSTRAINT ak_bookings_room_period
    UNIQUE (room_id, period WITHOUT OVERLAPS);
```

`HasTemporalConstraint` exposes this as a first-class EF Core API. You supply scalar key columns (the "group" — e.g. a room, a resource, an employee) and a period column (a [PostgreSQL range type](https://www.postgresql.org/docs/current/rangetypes.html) such as `daterange`, `tstzrange`, or `NpgsqlRange<T>`):

**Single key column:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => b.RoomId,
    period:     b => b.ValidPeriod);
// ALTER TABLE "Bookings" ADD CONSTRAINT "AK_Bookings__RoomId_ValidPeriod"
//   UNIQUE ("RoomId", "ValidPeriod" WITHOUT OVERLAPS);
```

**Composite key columns:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => new { b.Facility, b.RoomId },
    period:     b => b.ValidPeriod);
// UNIQUE ("Facility", "RoomId", "ValidPeriod" WITHOUT OVERLAPS)
```

**Explicit constraint name:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => b.RoomId,
    period:     b => b.ValidPeriod,
    name:       "uk_room_no_overlap");
```

#### How the period column is validated

The migration differ validates the period property at migration-generation time (`dotnet ef migrations add`). It must be mapped to a PostgreSQL range or multirange store type (anything ending in `range` — e.g. `daterange`, `tstzrange`, `int4multirange`) or have a CLR type of `NpgsqlRange<T>` / a multirange struct from `NpgsqlTypes`. Using an incompatible type such as `string`, `int`, or `DateOnly` throws an `InvalidOperationException` *before* any SQL is generated:

```
The temporal constraint period property 'Start' on entity 'Booking' does not appear to be a range or multirange type. Found CLR type 'DateTime' (store type: 'timestamp with time zone'). Expected NpgsqlRange<T>, a PostgreSQL range/multirange column type, or a store type ending in 'range' (e.g., daterange, int4multirange).
```

The period column stays a plain mapped column — it is deliberately **not** part of an EF key, because EF Core forbids non-comparable range types in primary keys. Use a surrogate or scalar EF primary key for change tracking; the temporal constraint handles the non-overlap guarantee independently.

#### `btree_gist` extension

Temporal constraints over scalar key columns require the `btree_gist` PostgreSQL extension. The differ injects `CREATE EXTENSION IF NOT EXISTS btree_gist;` automatically when a temporal constraint is first added. You can take explicit control or opt out:

```csharp
// Explicit: declare the extension yourself (Npgsql's own differ handles it)
modelBuilder.UseBtreeGist();

// Opt out: e.g. if the extension is provisioned out-of-band by your DBA
modelBuilder.SuppressTemporalExtensionAutoInjection();
```

When `UseBtreeGist()` is present, automatic injection backs off to avoid a duplicate `CREATE EXTENSION` statement.

#### Idempotency and renames

Re-declaring a temporal constraint on the same key + period replaces the previous one. Removing `HasTemporalConstraint` from the model causes the differ to emit a `DROP CONSTRAINT` in the next migration (unless the table itself is being dropped).

A change that only affects the **name** — whether you pass a new `name:` or rename the table, which
changes the default-derived name — emits `ALTER TABLE … RENAME CONSTRAINT` rather than dropping and
rebuilding the constraint, so dependent temporal foreign keys survive untouched.

### Temporal foreign keys (`PERIOD`) — requires PostgreSQL 18

> No runtime wiring required — the `PERIOD` DDL is rendered at design time into the migration itself.

`HasTemporalForeignKey` adds PostgreSQL 18 temporal referential integrity. The scalar key columns are matched by equality, and the dependent period must be fully covered by matching principal periods.

A typical subscription/add-on model looks like this:

```csharp
modelBuilder.Entity<Subscription>(b =>
{
    // Principal side: PostgreSQL requires the referenced columns to have
    // a temporal UNIQUE/PRIMARY KEY constraint with WITHOUT OVERLAPS.
    b.HasTemporalConstraint(
        keyColumns: x => x.SubscriptionId,
        period:     x => x.ValidDuring);
});

modelBuilder.Entity<SubscriptionAddOn>(b =>
{
    b.HasTemporalForeignKey<Subscription>(
        dependentKeyColumns: x => x.SubscriptionId,
        dependentPeriod:     x => x.ActiveDuring,
        principalKeyColumns: x => x.SubscriptionId,
        principalPeriod:     x => x.ValidDuring,
        name:                "fk_addons_subscriptions_temporal" 
    );
});
```

Generated SQL:

```sql
ALTER TABLE subscription_addons
  ADD CONSTRAINT fk_addons_subscriptions_temporal
    FOREIGN KEY (subscription_id, PERIOD active_during)
    REFERENCES subscriptions (subscription_id, PERIOD valid_during);
```

Composite keys use anonymous types on both sides:

```csharp
b.HasTemporalForeignKey<Subscription>(
    dependentKeyColumns: x => new { x.TenantId, x.SubscriptionId },
    dependentPeriod:     x => x.ActiveDuring,
    principalKeyColumns: x => new { x.TenantId, x.SubscriptionId },
    principalPeriod:     x => x.ValidDuring 
);
```

#### Restrictions and validation

- PostgreSQL 18+ only.
- Period columns must be PostgreSQL range or multirange columns (`daterange`, `tstzrange`, `NpgsqlRange<T>`, etc.).
- The referenced principal columns must have a matching `HasTemporalConstraint` in the model. PostgreSQL requires a referenced temporal `UNIQUE`/`PRIMARY KEY` constraint with `WITHOUT OVERLAPS`.
- Temporal foreign keys emit `NO ACTION` referential actions. PostgreSQL does not support temporal FK `CASCADE`, `RESTRICT`, `SET NULL`, or `SET DEFAULT` actions.
- This API emits standalone database constraints; it does not try to model the temporal relationship as an EF navigation/relationship key.

The standalone design is intentional. The period column remains a normal mapped property, not an EF key member. EF keys require key values suitable for change tracking, while Npgsql range values are not suitable EF key members; PostgreSQL enforces the temporal relationship independently at the database level.

### Exclusion constraints (`EXCLUDE`)

> No runtime wiring required — the DDL is rendered at design time into the migration itself.

An exclusion constraint generalizes uniqueness: no two rows may satisfy all the per-element
comparisons at once. Its killer feature over `UNIQUE … WITHOUT OVERLAPS`: it accepts a **`WHERE`
predicate**. PostgreSQL's `ADD CONSTRAINT UNIQUE`/`PRIMARY KEY` grammar has never allowed one, so a
*filtered* overlap guarantee — "no overlapping periods per key, but ignore revoked/soft-deleted
rows" — can **only** be expressed as an EXCLUDE constraint. It also works on every supported
PostgreSQL version, not just 18+.

**The scheduling shape** (equality keys + overlap column + predicate):

```csharp
builder.HasExclusionConstraint(
    equalityColumns: x => new { x.GranteeId, x.RoleId },
    overlapsColumn:  x => x.Period,
    filter:          "revoked_at IS NULL",
    name:            "ex_role_grant_active_period");
// ALTER TABLE role_grants ADD CONSTRAINT "ex_role_grant_active_period"
//   EXCLUDE USING gist (grantee_id WITH =, role_id WITH =, period WITH &&)
//   WHERE (revoked_at IS NULL);
```

**Full control** (arbitrary operators, expressions, method, deferrability):

```csharp
builder.HasExclusionConstraint(ex => ex
    .WithEquality(x => x.Slot.Resource)      // complex-property members resolve to columns
    .WithOverlaps(x => x.Slot.Period)
    .WithExpression("lower(code)", "=")      // verbatim SQL element
    .UseMethod("gist")                        // the default
    .HasFilter("deleted_at IS NULL")
    .HasName("ex_booking_slot")
    .IsDeferrable(initiallyDeferred: true));
```

Selectors resolve complex-property members to their mapped columns, exactly like complex indexes.
Scalar equality elements under `gist` need the `btree_gist` extension — the differ injects
`CREATE EXTENSION IF NOT EXISTS btree_gist` automatically, shared with temporal constraints and
governed by the same `UseBtreeGist()` / `SuppressTemporalExtensionAutoInjection()` switches.
Constraint identity is the ordered elements **plus the filter** (operators are ignored, so
re-declaring updates them). Re-declaring the same elements with the same filter replaces the
constraint; the same elements with a *different* filter give you two coexisting partial
constraints — which is the point of the feature:

```csharp
b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                         filter: "revoked_at IS NULL",     name: "ex_grant_active");
b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                         filter: "revoked_at IS NOT NULL", name: "ex_grant_revoked");
```

Coexisting constraints must both be named: the default `EX_{table}_{columns}` name is derived from
the elements alone, so the two would collide in the database. Removing a declaration emits a
`DROP CONSTRAINT` in the next migration.

**Adopting hand-written constraints:** the generated `ADD CONSTRAINT` is preceded by
`DROP CONSTRAINT IF EXISTS`, so declaring a constraint that already exists in the database under
the same name — e.g. raw `migrationBuilder.Sql(...)` DDL from an earlier migration — applies
cleanly on both fresh and existing databases. No hand-editing of the scaffolded migration needed;
just make sure the declared name matches the existing one.

> **If a constraint re-appears in every scaffolded migration:** the differ compares the model
> against the *compiled* model snapshot, not the `…ModelSnapshot.cs` file. A constraint that is
> re-emitted on every `dotnet ef migrations add` even though the snapshot file contains its
> `CustomExclusion:Constraints` annotation means the compiled snapshot is stale — typically
> scaffolding with `--no-build`, or a migrations assembly (`MigrationsAssembly(...)`) resolved from
> an out-of-date build output. Rebuild the project that hosts the snapshot and re-scaffold.

---

## SQL Server

### Index options

The **EFCore.ComplexIndexes.SqlServer** package brings the SQL Server option set to complex-property
indexes. Like the PostgreSQL GIN/GiST options, everything flows as native provider annotations that
SQL Server's own migrations SQL generator renders — **no runtime wiring at all**:

```csharp
builder.ComplexProperty(x => x.Email, c =>
    c.Property(x => x.Value).HasColumnName("email"));

builder.HasComplexIndex(x => x.Email.Value, ix => ix
    .IsUnique()
    .HasName("ux_person_email")
    .IncludeProperties("name")   // covering index
    .IsCreatedOnline()           // ONLINE = ON
    .HasFillFactor(80));
// CREATE UNIQUE INDEX [ux_person_email] ON [person] ([email])
//   INCLUDE ([name]) WITH (FILLFACTOR = 80, ONLINE = ON);
```

`IsClustered()`, `SortInTempDb()`, and `UseDataCompression(DataCompressionType.Page)` are also available. Filtered indexes (`filter:`) and
`DbOrder.Desc` work out of the box, since both ride on EF's native operation. Two deliberate
rejections with clear errors at `migrations add`: expression parts (SQL Server has no
expression-index DDL — model a persisted computed column and index that) and
`DbOrder.NullsFirst/NullsLast` (no such T-SQL syntax).

---

## What changed in 5.0.3

A packaging and documentation release. No behaviour changes to the differ or the generated SQL.

- **Changed:** the EF Core dependency now declares an exclusive upper bound — `[10.0.0, 11.0.0)` on `Microsoft.EntityFrameworkCore.Abstractions` for the core package, and on the provider package for each satellite. This package subclasses `MigrationsModelDiffer` and calls internals EF marks as changeable without notice in any release, so an open-ended `>= 10.0.0` let NuGet resolve a future major where the differ can break — surfacing as a confusing `dotnet ef` failure in your project rather than anywhere visible from here. **Nothing changes for existing consumers:** NuGet resolves the lowest version in a range, so restore still picks 10.0.0. Adopting EF Core 11 will need a release that lifts the ceiling deliberately, once the differ has been tested against it.
- **New:** the public API is now fully documented, so IntelliSense no longer comes up empty on the fluent API, the annotation keys, `CompositeIndexDefinition`, or `IndexPartDefinition`. The shipped `.xml` had 64 holes in it; `TreatWarningsAsErrors` now keeps it complete.
- **Tests:** a consumer smoke test runs on every PR and on release. It packs the packages, installs them into a throwaway project created outside this repository, and runs a real `dotnet ef migrations add` — then asserts on the scaffolded content, because the failure it guards against is a migration that succeeds while silently omitting every index. Nothing previously exercised the delivery chain end to end: NuGet restore, the packaged `.targets` injecting the design-time attribute, EF's host discovering it, and the right differ winning.

## What changed in 5.0.2

A review of the 5.0.1 tree turned up eleven issues. The first three produced migrations that
scaffolded *and applied* cleanly while being silently wrong; the rest turn late, obscure, or silent
failures into errors raised at the declaration or during `dotnet ef migrations add`.

- **Fixed:** the design-time differ is now selected deterministically. A satellite package's `DesignTimeServicesReferenceAttribute` is scoped to its provider (`ForProvider`), and the core registration backs off when a satellite is present — previously, because the core package's attribute rides along transitively and EF resolves last-registration-wins, NuGet's restore order decided which differ ran. A solution referencing two satellites could hand one provider's model to the other provider's differ, silently dropping its index options.
- **Fixed:** temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys are now rendered at design time, like exclusion constraints, and no longer need `UseNpgsqlComplexIndexes()`. Previously a consumer without that wiring got a plain `UNIQUE (key, period)` — valid DDL that applied cleanly and silently dropped the entire non-overlap guarantee. Migrations scaffolded before this change keep working: the SQL generator still renders the old stamped operations.
- **Fixed:** exclusion-constraint identity now includes the filter, so two `EXCLUDE` constraints over the same columns with different predicates coexist (both must be named) instead of the second silently replacing the first — the filtered-overlap case the API exists for. Re-declaring with the same filter still updates in place.
- **Fixed:** duplicate index and exclusion-constraint names are now rejected instead of producing a migration that fails at apply time (42P07) — or, for exclusion constraints, one that applies silently and leaves only the last constraint standing. Reusing an explicit name throws at the declaration; collisions between default names, or between a property-level and an entity-level declaration, throw during `migrations add`.
- **Fixed:** `CompositeIndexDefinition` equality compares array-valued provider annotations (operator classes, INCLUDE lists) by content instead of by reference.
- **Fixed:** index, temporal-constraint, and exclusion-constraint selectors that read a captured variable or static member instead of the lambda parameter (`x => captured.Name`) now throw at the declaration, naming the offending selector — previously they produced an unmatchable property path that failed much later with an opaque resolution error.
- **Fixed:** provider validation no longer inspects index operations this package did not create. The satellites previously swept every `CreateIndexOperation` in the migration, so a plain native `HasIndex` carrying a provider option outside the satellite's whitelist would have failed the entire `migrations add` — harmless with today's providers, but it tied your migrations to the exact index-option set each satellite knows about.
- **Fixed:** `DbOrder.Asc` now marks a column ascending, and combining it with `DbOrder.Desc` (or `NullsFirst` with `NullsLast`) throws instead of silently picking one. Repeating the same marker is still fine.
- **Fixed:** `Npgsql:IndexSortOrder`/`IndexNullSortOrder` are no longer forwarded onto complex indexes, and setting either now throws with a pointer to `DbOrder`. They duplicated what `DbOrder.Asc`/`Desc`/`NullsFirst`/`NullsLast` already express per column, giving one index two sources of truth for its sort options — with the annotation's half silently losing whenever the index rendered through this package's generator.
- **Fixed:** clustered-index combinations SQL Server rejects are now caught at `migrations add` rather than at apply time: a clustered index with `INCLUDE` columns, a clustered filtered index, two clustered complex indexes on one table, and — the common one — a clustered complex index on a table whose primary key already holds the clustered slot, which is the SQL Server default.
- **New:** `UseDataCompression(DataCompressionType)` on SQL Server complex indexes — the annotation was already forwarded but had no way to set it.

## What changed in 5.0.1

- **Changed:** exclusion-constraint `ADD CONSTRAINT` DDL is now preceded by `DROP CONSTRAINT IF EXISTS`, so adopting a pre-existing hand-written constraint of the same name applies cleanly instead of failing with `42P07`. The standalone drop path also uses `IF EXISTS`.
- **Fixed:** renaming a table no longer drops and recreates the exclusion and temporal constraints it carries (the same normalization complex indexes already had).
- **Changed:** a name-only change to an exclusion constraint, temporal constraint, or temporal foreign key — including the implicit one when a table rename changes a default-derived name — now emits `ALTER TABLE … RENAME CONSTRAINT` instead of dropping and rebuilding. Dependent temporal foreign keys survive such renames untouched.
- **Tests:** the differ is now exercised against *real* model snapshots — generated as C#, compiled in-memory, and rebuilt exactly as `dotnet ef migrations add` does — guarding the whole feature set against snapshot round-trip churn.

## What changed in 5.0.0

- **Fixed:** custom `DROP INDEX` operations are now ordered *before* the base migration operations. Previously, moving an index between a native `HasIndex` and a complex-index declaration scaffolded a migration that created the new index before dropping the same-named old one — colliding at apply time.
- **Fixed:** descending parts of expression indexes now render `DESC` (declarable via `ExpressionIndexBuilder.Descending()`).
- **Fixed:** integral provider-annotation values (e.g. fill factor) survive snapshot round-trips as `int` instead of degrading to `double`, which made generators drop them.
- **Changed:** property annotations are forwarded onto index operations through a provider **whitelist** instead of a blacklist. Column facets such as `Relational:ColumnName` no longer leak into scaffolded migrations, and the class of phantom drop/create churn caused by snapshot/code-model annotation asymmetries is closed for good.
- **Changed:** an indexed property that resolves to no column now throws at `migrations add` instead of silently dropping the index — unless it is a `ToJson()` member, which now resolves to a JSON expression index (PostgreSQL).
- **Changed:** two indexes over the same columns may now coexist when their filters differ (both must be named); re-declaring with the same filter still updates in place.
- **New:** entity-level `HasComplexIndex(x => x.Complex.Prop, …)` for single-column indexes, enabling multiple filtered indexes per column.
- **New:** `HasExclusionConstraint` — `EXCLUDE` constraints with `WHERE` predicates (see above).
- **New:** typed LINQ expression indexes — `HasExpressionIndex(x => x.Email.ToLower())`.
- **New:** JSON member indexes for `ToJson()` complex properties.
- **New:** `NULLS FIRST`/`NULLS LAST` via `DbOrder.NullsFirst/NullsLast` and `ExpressionIndexBuilder.NullsFirst()/NullsLast()` (PostgreSQL).
- **New:** the **EFCore.ComplexIndexes.SqlServer** satellite — clustered, covering, online, fill-factor, and sort-in-tempdb options.
- **Changed:** `IncludeProperties(...)` entries are now resolved as property paths (complex members included) with verbatim column-name fallback — `IncludeProperties("Email.Value")` finds the real column.
- **Changed:** a name-only index change now emits `RenameIndexOperation` (PostgreSQL, SQL Server) instead of dropping and rebuilding the index; the core default remains drop + create for providers that cannot rename standalone.
- **Changed:** renaming a table no longer drops and recreates the complex indexes it carries.
- **Changed:** indexes requiring the custom PostgreSQL generator carry a loud sentinel column, so a missing `UseNpgsqlComplexIndexes()` fails at apply time with an actionable error instead of applying a silently wrong index.

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
