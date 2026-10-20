# EFCore.ComplexIndexes.PostgreSQL

> **On EF Core 11 or later?** This package supports EF Core 10 only. From 5.5.0 it throws when you
> register it on any other major version. Its successor for EF Core 11 is a new package,
> `CodoMetis.EFCore.SchemaKit`, tracked in
> [#37](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/37).

PostgreSQL index and constraint features for
[EFCore.ComplexIndexes](https://www.nuget.org/packages/EFCore.ComplexIndexes/), through
[Npgsql](https://www.npgsql.org/efcore/). This package brings the core package with it.

On top of the core package's complex-property, composite, unique and filtered indexes, you get:

- **Index methods.** GIN, GiST, BRIN, SP-GiST and Hash, with operator classes, covering (`INCLUDE`)
  indexes, concurrent creation, nulls-distinct control, per-column collation and storage parameters
  (`WITH (fillfactor=70)`).
- **`NULLS FIRST` / `NULLS LAST`** for each column.
- **Expression indexes**, in raw SQL *or* as a typed LINQ lambda, on any entity, with complex
  properties or without.
- **JSON indexes.** Index members of `ToJson()` complex properties with the same expressions
  Npgsql's queries use. Or index the whole document, or a complex collection, with a GIN index on the
  `jsonb` column.
- **Temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys** (PostgreSQL 18).
- **Exclusion (`EXCLUDE`) constraints**, a more general form of uniqueness that also takes a `WHERE`
  filter, on every supported version.

---

## Setup

For migrations, most features need nothing beyond installing the package. **Two** need one call in
your context's configuration: expression indexes and null ordering. EF Core's index operation has no
place for them, so this package's SQL generator writes their SQL when the migration runs. The same
call also covers `EnsureCreated()`, as the last row shows.

| Feature | Needs `UseNpgsqlComplexIndexes()` |
|---|---|
| Index methods, operator classes, `INCLUDE`, concurrent creation, nulls-distinct | no |
| Temporal constraints and temporal foreign keys | no *(since 5.0.2)* |
| Exclusion constraints | no |
| **Expression indexes** (raw SQL, typed LINQ, JSON member) | **yes** |
| **`DbOrder.NullsFirst` / `NullsLast`** | **yes** |
| **`EnsureCreated()` / `GenerateCreateScript()` including the declarations** | **yes** *(since 5.1.0)* |

```csharp
services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(connectionString)
        .UseNpgsqlComplexIndexes());
```

> If you forget the call, the migration fails rather than creating an index that is quietly wrong.
> These indexes carry a placeholder column named `__requires_UseNpgsqlComplexIndexes__`. Npgsql's own
> generator writes it into the `CREATE INDEX`, and the migration fails when it runs, with that name in
> the error message.

Since 5.1.0 the same call also covers `Database.EnsureCreated()` and `GenerateCreateScript()`, so
they create your complex indexes and constraints. The pending-model-changes check in `Migrate()`
then also notices a complex index that no migration has created yet. These use your context's own
services, which the automatic design-time registration never reaches. Without the call,
`EnsureCreated()` creates the tables and none of the indexes, with no error.

Building your own internal service provider? Register the package on your service collection
instead:

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

The builder has `UseGin()`, `UseGist()`, `UseBrin()`, `UseHash()`, `UseSpGist()`,
`HasOperators(...)`, `IncludeProperties(...)`, `IsCreatedConcurrently()` and `AreNullsDistinct(...)`.

### Null ordering

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, Reviewed = DbOrder.NullsLast(DbOrder.Desc(x.ReviewedAt)) });
// CREATE INDEX ... (name, reviewed_at DESC NULLS LAST);
```

### Expression indexes

The package writes raw SQL exactly as you give it. It doesn't map properties to columns and doesn't
add quotes:

```csharp
builder.HasExpressionIndex("lower(email)", isUnique: true, filter: "deleted_at IS NULL");

builder.HasExpressionIndex(idx => idx
    .Expression("country")
    .Expression("lower(email)").Descending()
    .UseGin()
    .HasName("ix_person_country_email_ci"));
```

Or pass a lambda. The package maps its property paths to columns in your finalized model, so it
follows `HasColumnName`, complex-property columns and `ToJson()` members:

```csharp
builder.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true);
// CREATE UNIQUE INDEX ... ON people ((lower("email")));
```

The lambda can use a small set of operations on purpose: `ToLower`/`ToUpper`, the `Trim` methods
without arguments,
`Substring`, `Replace`, `string.Length`, concatenation, `??` and constants. Anything else throws
`NotSupportedException` **where you declare the index**, and the message points you to the raw-SQL
overload. A value object stored through a converter maps through its member: `x.Email.Value` is the
`email` column.

### JSON member indexes

The members of a complex property mapped with `ToJson()` have no table columns. You declare their
indexes the same way, and the package indexes an expression that reads the member:

```csharp
builder.ComplexProperty(x => x.Name, c => c.ToJson("name"));
builder.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
// CREATE UNIQUE INDEX "ux_employer_short_name" ON employers (("name" ->> 'ShortName'));
```

Each member is read the way Npgsql's queries read it, so those queries use the index. A nested member
is read with `#>> '{Address,City}'`, a typed member is cast to its store type
(`CAST("profile" ->> 'Rank' AS integer)`), and `HasJsonPropertyName` is followed.

Date and time members stay text, because PostgreSQL can't index their cast. A unique index on one
still keeps the values unique. A non-unique index that starts with one makes `migrations add` fail.
If you upgrade from 5.3 or earlier, the first migration re-creates the affected indexes once, under
their existing names.

### Temporal constraints — PostgreSQL 18

```csharp
builder.HasTemporalConstraint(keyColumns: b => b.RoomId, period: b => b.ValidPeriod);
// ALTER TABLE bookings ADD CONSTRAINT ... UNIQUE (room_id, valid_period WITHOUT OVERLAPS);
```

The period must be a range or multirange column (`daterange`, `tstzrange`, `NpgsqlRange<T>`, …).
Any other type makes `migrations add` throw. The period stays a plain mapped column, and on purpose
it is **not** part of an EF key: EF Core doesn't allow range types in keys, since they can't be
compared. For temporal foreign keys, use `HasTemporalForeignKey`. It needs a matching temporal
constraint on the principal.

### Exclusion constraints

An exclusion constraint is a more general kind of uniqueness. Unlike `UNIQUE … WITHOUT OVERLAPS`, it
takes a **`WHERE` filter**, so it is the only way to keep periods from overlapping for *some* rows:

```csharp
builder.HasExclusionConstraint(
    equalityColumns: x => new { x.GranteeId, x.RoleId },
    overlapsColumn:  x => x.Period,
    filter:          "revoked_at IS NULL",
    name:            "ex_role_grant_active_period");
```

Two declarations are the same constraint when they have the same elements in the same order **and**
the same filter. So the same columns with different filters give you two constraints side by side,
each over its own rows. Name both.

Filters, here and on indexes, can name properties as `{Property.Path}` placeholders. `migrations add`
turns them into columns or JSON reads. Every `filter:` also has a typed form: `x => x.RevokedAt == null`,
or `HasFilter(x => …)` on the builders (`HasFilter<TEntity>` on the non-generic index builders). The
typed form supports a small set: null checks, comparisons, `&&`/`||`/`!`, and the string functions
above. Where you declare it, it throws on an enum comparison, and on a value with no portable SQL
spelling, such as a `DateTime` or a `Guid`.

You can read declared constraints back with `GetExclusionConstraints()` on an entity type or the
model, or `FindExclusionConstraint(name)`. You get the elements, method, filter, deferrability and
name, from the mutable model in `OnModelCreating` as well as the finalized one. You can amend them
there too: `AddExclusionConstraintFilter(predicate, where)` adds a predicate with `AND` to the filter
of every constraint you select, and repeating the call changes nothing.

### `btree_gist`

A scalar element in a `gist` constraint needs the `btree_gist` extension, whatever its operator. The
package adds `CREATE EXTENSION IF NOT EXISTS btree_gist` to the migration for you. To manage it
yourself, call `modelBuilder.UseBtreeGist()`. To leave it out, call
`SuppressTemporalExtensionAutoInjection()`.

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
