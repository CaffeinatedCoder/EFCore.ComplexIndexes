# EFCore.ComplexIndexes.PostgreSQL

PostgreSQL index and constraint features for
[EFCore.ComplexIndexes](https://www.nuget.org/packages/EFCore.ComplexIndexes/), via
[Npgsql](https://www.npgsql.org/efcore/). The core package is included automatically.

Adds, on top of the core's complex-property, composite, unique, and filtered indexes:

- **Index methods** — GIN, GiST, BRIN, SP-GiST, Hash — plus operator classes, covering (`INCLUDE`)
  indexes, concurrent creation, and nulls-distinct control
- **`NULLS FIRST` / `NULLS LAST`** per-column null ordering
- **Expression (functional) indexes** — raw SQL *or* typed LINQ, on any entity, complex or not
- **JSON member indexes** — index members of `ToJson()` complex properties as `->>` extractions
- **Temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys** (PostgreSQL 18)
- **Exclusion (`EXCLUDE`) constraints** — filtered overlap protection, on every supported version

---

## Setup

Most features need nothing beyond installing the package. **Two** are rendered when migrations are
*applied* rather than at design time, because they have no slot on EF Core's native index operation,
and those need a one-time opt-in:

| Feature | Needs `UseNpgsqlComplexIndexes()` |
|---|---|
| Index methods, operator classes, `INCLUDE`, concurrent creation, nulls-distinct | no |
| Temporal constraints and temporal foreign keys | no *(since 5.0.2)* |
| Exclusion constraints | no |
| **Expression indexes** (raw SQL, typed LINQ, JSON member) | **yes** |
| **`DbOrder.NullsFirst` / `NullsLast`** | **yes** |

```csharp
services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(connectionString)
        .UseNpgsqlComplexIndexes());
```

> Forgetting this does not produce a silently wrong index: affected indexes carry a sentinel column
> named `__requires_UseNpgsqlComplexIndexes__`, so the stock generator fails loudly with that name
> in the error message.

Building your own internal service provider? Register the generator directly instead:

```csharp
var provider = new ServiceCollection()
    .AddEntityFrameworkNpgsql()
    .AddNpgsqlComplexIndexes()
    .BuildServiceProvider();
```

## Usage

### Index methods and options

```csharp
builder.ComplexProperty(x => x.Payload, c =>
    c.Property(x => x.Json)
     .HasComplexIndex(idx => idx.UseGin().HasOperators("jsonb_path_ops"))
);
```

`UseGin()`, `UseGist()`, `UseBrin()`, `UseHash()`, `UseSpGist()`, `HasOperators(...)`,
`IncludeProperties(...)`, `IsCreatedConcurrently()`, `AreNullsDistinct(...)`.

### Null ordering

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, Reviewed = DbOrder.NullsLast(DbOrder.Desc(x.ReviewedAt)) });
// CREATE INDEX ... (name, reviewed_at DESC NULLS LAST);
```

### Expression indexes

Raw SQL is emitted verbatim — no property-to-column resolution, no automatic quoting:

```csharp
builder.HasExpressionIndex("lower(email)", isUnique: true, filter: "deleted_at IS NULL");

builder.HasExpressionIndex(idx => idx
    .Expression("country")
    .Expression("lower(email)").Descending()
    .UseGin()
    .HasName("ix_person_country_email_ci"));
```

Or pass a lambda and let property paths resolve against the finalized model, so `HasColumnName`,
complex-property columns, and `ToJson()` members are honored automatically:

```csharp
builder.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true);
// CREATE UNIQUE INDEX ... ON people ((lower("email")));
```

The translated subset is deliberately small — `ToLower`/`ToUpper`, `Trim` variants, `Substring`,
`Replace`, `string.Length`, concatenation, `??`, constants — and anything else throws
`NotSupportedException` **at declaration time**, pointing at the raw-SQL overload.

### JSON member indexes

When a complex property is mapped with `ToJson()`, its members have no table columns — yet the same
index declarations keep working, resolving to extraction expressions instead:

```csharp
builder.ComplexProperty(x => x.Name, c => c.ToJson("name"));
builder.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
// CREATE UNIQUE INDEX "ux_employer_short_name" ON employers (("name" ->> 'ShortName'));
```

Nested complex types become `->` segments and `HasJsonPropertyName` is honored.

### Temporal constraints — PostgreSQL 18

```csharp
builder.HasTemporalConstraint(keyColumns: b => b.RoomId, period: b => b.ValidPeriod);
// ALTER TABLE bookings ADD CONSTRAINT ... UNIQUE (room_id, valid_period WITHOUT OVERLAPS);
```

The period must be a range or multirange column (`daterange`, `tstzrange`, `NpgsqlRange<T>`, …);
anything else throws at `migrations add`. It stays a plain mapped column, deliberately **not** part
of an EF key — EF Core forbids non-comparable range types in keys. Temporal foreign keys are
available as `HasTemporalForeignKey`, and require a matching constraint on the principal.

### Exclusion constraints

An exclusion constraint generalizes uniqueness, and unlike `UNIQUE … WITHOUT OVERLAPS` it accepts a
**`WHERE` predicate** — so a *filtered* overlap guarantee can only be expressed this way:

```csharp
builder.HasExclusionConstraint(
    equalityColumns: x => new { x.GranteeId, x.RoleId },
    overlapsColumn:  x => x.Period,
    filter:          "revoked_at IS NULL",
    name:            "ex_role_grant_active_period");
```

Constraint identity is the ordered elements **plus** the filter, so the same columns under different
predicates give you two coexisting partial constraints (both must be named).

### `btree_gist`

Scalar equality elements under `gist` need the extension; the differ injects
`CREATE EXTENSION IF NOT EXISTS btree_gist` automatically. Use `modelBuilder.UseBtreeGist()` for
explicit control or `SuppressTemporalExtensionAutoInjection()` to opt out.

---

## Documentation

- [PostgreSQL — indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md)
  — index methods, expression and typed LINQ indexes, JSON member indexes, null ordering
- [PostgreSQL — temporal and exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md)
  — `WITHOUT OVERLAPS`, temporal foreign keys, `EXCLUDE`, `btree_gist`
- [Full documentation](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes)

## Changelog

[This package's changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/src/EFCore.ComplexIndexes.PostgreSQL/CHANGELOG.md),
or the [root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covering all three packages.

MIT licensed.
