# PostgreSQL — temporal and exclusion constraints

With the **EFCore.ComplexIndexes.PostgreSQL** package you can declare temporal `UNIQUE` constraints,
temporal foreign keys and exclusion constraints. They let the database check time periods: that
they don't overlap, or that one covers another. Exclusion constraints go further, as a general form
of uniqueness. For migrations, none of them needs `UseNpgsqlComplexIndexes()`: `migrations add`
writes their SQL into the migration itself.

For index methods, expression indexes and JSON member indexes, see
[PostgreSQL — indexes](postgresql-indexes.md).

## Temporal `UNIQUE` constraints (`WITHOUT OVERLAPS`) — requires PostgreSQL 18

> Migrations need no `UseNpgsqlComplexIndexes()`: `migrations add` writes the SQL into the migration
> itself. `HasTemporalConstraint` extends `EntityTypeBuilder<TEntity>`, so you can use it on any entity, with
> complex properties or without.

PostgreSQL 18 added `WITHOUT OVERLAPS` to unique constraints, for scheduling, booking and versioning.
A plain unique constraint checks *"is this exact value already there?"*. With `WITHOUT OVERLAPS`, the
database checks *"do two rows for the same key have overlapping time periods?"*.

```sql
ALTER TABLE bookings
  ADD CONSTRAINT ak_bookings_room_period
    UNIQUE (room_id, period WITHOUT OVERLAPS);
```

`HasTemporalConstraint` lets you declare this in EF Core. You pass the scalar key columns, which form
a group, such as a room, a resource or an employee. Within a group, periods must not overlap. You
also pass the period column, of a
[PostgreSQL range type](https://www.postgresql.org/docs/current/rangetypes.html) such as `daterange`,
`tstzrange` or `NpgsqlRange<T>`:

**One key column:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => b.RoomId,
    period:     b => b.ValidPeriod);
// ALTER TABLE "Bookings" ADD CONSTRAINT "AK_Bookings_RoomId_ValidPeriod"
//   UNIQUE ("RoomId", "ValidPeriod" WITHOUT OVERLAPS);
```

**Several key columns:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => new { b.Facility, b.RoomId },
    period:     b => b.ValidPeriod);
// UNIQUE ("Facility", "RoomId", "ValidPeriod" WITHOUT OVERLAPS)
```

**A name you choose:**

```csharp
builder.HasTemporalConstraint(
    keyColumns: b => b.RoomId,
    period:     b => b.ValidPeriod,
    name:       "uk_room_no_overlap");
```

### Which period columns work

`dotnet ef migrations add` checks the period property. It must map to a PostgreSQL range or
multirange store type, meaning any type ending in `range`, such as `daterange`, `tstzrange` or
`int4multirange`. A CLR type of `NpgsqlRange<T>`, or a multirange struct from `NpgsqlTypes`, works
too. Any other type, such as `string`, `int` or `DateOnly`, throws an `InvalidOperationException`
*before* any SQL is written:

```
The temporal constraint period property 'Start' on entity 'Booking' does not appear to be a range or multirange type. Found CLR type 'DateTime' (store type: 'timestamp with time zone'). Expected NpgsqlRange<T>, a PostgreSQL range/multirange column type, or a store type ending in 'range' (e.g., daterange, int4multirange).
```

The period stays a plain mapped column. On purpose, it is **not** part of an EF key: EF Core doesn't
allow range types in primary keys, since they can't be compared. Give the entity a surrogate or
scalar EF primary key for change tracking. The temporal constraint keeps the periods from overlapping
on its own.

### The `btree_gist` extension

A temporal constraint over scalar key columns needs PostgreSQL's `btree_gist` extension. When a
migration adds a temporal constraint, the package puts `CREATE EXTENSION IF NOT EXISTS btree_gist;` into
the migration for you. You can manage the extension yourself, or leave it out:

```csharp
// Explicit: declare the extension yourself (Npgsql's own differ handles it)
modelBuilder.UseBtreeGist();

// Opt out: e.g. if the extension is provisioned out-of-band by your DBA
modelBuilder.SuppressTemporalExtensionAutoInjection();
```

With `UseBtreeGist()` in the model, the package doesn't add its own statement, so the migration has
only one `CREATE EXTENSION`.

### Change, remove or rename a constraint

- **Declaring a temporal constraint again** on the same key and period replaces the earlier one.
- **Removing `HasTemporalConstraint`** from the model gives you a `DROP CONSTRAINT` in the next
  migration, unless the table itself is dropped.
- **Changing only the name** gives you `ALTER TABLE … RENAME CONSTRAINT`, instead of dropping and
  re-creating the constraint. That's the case when you pass a new `name:`, or rename the table, which
  changes the default name. Temporal foreign keys that depend on the constraint stay as they are.
- **Removing a constraint along with the table that references it** drops the temporal foreign key
  first, so the constraint can go.
- **Changing the type of a key column on both tables**, `int` to `long` for example, drops the
  temporal foreign key before the columns change and adds it back after. PostgreSQL can't keep a key
  whose two sides have different types for the moment between the two changes (42804).
  The same holds when the key's name changes in that migration, for example because you renamed the
  principal table and the default name follows it.

## Temporal foreign keys (`PERIOD`) — requires PostgreSQL 18

> Migrations need no `UseNpgsqlComplexIndexes()`: `migrations add` writes the `PERIOD` SQL into the
> migration itself.

`HasTemporalForeignKey` adds PostgreSQL 18's temporal foreign keys. The scalar key columns must be
equal, and the principal rows with matching keys must cover the dependent's whole period.

A typical subscription with add-ons looks like this:

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
    b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
        dependentKeyColumns: x => x.SubscriptionId,
        dependentPeriod:     x => x.ActiveDuring,
        principalKeyColumns: x => x.SubscriptionId,
        principalPeriod:     x => x.ValidDuring,
        name:                "fk_addons_subscriptions_temporal" 
    );
});
```

The migration contains:

```sql
ALTER TABLE subscription_addons
  ADD CONSTRAINT fk_addons_subscriptions_temporal
    FOREIGN KEY (subscription_id, PERIOD active_during)
    REFERENCES subscriptions (subscription_id, PERIOD valid_during);
```

For several key columns, use anonymous types on both sides:

```csharp
b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
    dependentKeyColumns: x => new { x.TenantId, x.SubscriptionId },
    dependentPeriod:     x => x.ActiveDuring,
    principalKeyColumns: x => new { x.TenantId, x.SubscriptionId },
    principalPeriod:     x => x.ValidDuring 
);
```

### What a temporal foreign key needs

- **PostgreSQL 18 or later.**
- **Range or multirange period columns:** `daterange`, `tstzrange`, `NpgsqlRange<T>` and so on.
- **A matching `HasTemporalConstraint` on the principal columns.** PostgreSQL requires the referenced
  columns to have a temporal `UNIQUE` or `PRIMARY KEY` constraint with `WITHOUT OVERLAPS`.
- **`NO ACTION` only.** The package writes `NO ACTION` referential actions, because PostgreSQL doesn't
  support `CASCADE`, `RESTRICT`, `SET NULL` or `SET DEFAULT` on a temporal foreign key.
- **No EF relationship.** You get a database constraint only. The package doesn't model the
  relationship as an EF navigation or relationship key.

That last point is on purpose. The period column stays a normal mapped property, not part of an EF
key. EF keys need values that change tracking can use, and Npgsql range values don't fit. PostgreSQL
enforces the temporal relationship in the database on its own.

## Exclusion constraints (`EXCLUDE`)

> Migrations need no `UseNpgsqlComplexIndexes()`: `migrations add` writes the SQL into the migration
> itself.

An exclusion constraint is a more general kind of uniqueness: no two rows may satisfy all of its
comparisons at once. Unlike `UNIQUE … WITHOUT OVERLAPS`, it takes a **`WHERE` filter**. PostgreSQL's
`ADD CONSTRAINT UNIQUE` and `PRIMARY KEY` have never allowed one. So to keep periods from overlapping
per key while ignoring revoked or soft-deleted rows, an `EXCLUDE` constraint is the **only** way. It
also works on every supported PostgreSQL version, not just 18 and later.

**Keys that must match, a period that must not overlap, and a filter:**

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

**Full control** over operators, expressions, the index method and deferral:

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

Selectors map complex-property members to their columns, as they do for complex indexes.

### Filter with property names

The filter takes the same `{Property.Path}` placeholders as a complex index filter.
`filter: "{RevokedAt} IS NULL"` becomes `WHERE ("revoked_at" IS NULL)`, whatever `HasColumnName` set.
JSON members become JSON reads, and a path that names no property makes `migrations add` fail.

You can also write the filter as a lambda. The overload with keys and a period takes a predicate in
place of the string, and the builder has `HasFilter(x => x.RevokedAt == null)`. The lambda can use
what [typed filters](postgresql-indexes.md#typed-filters) support.

### The `btree_gist` extension for scalar elements

A scalar element under `gist` needs the `btree_gist` extension, whatever its operator: `<>` as much
as `=`. A scalar type has no GiST operator class of its own. This holds however you spell the method,
`UseMethod("GiST")` included. The package adds `CREATE EXTENSION IF NOT EXISTS btree_gist` to the
migration for you:

- **for an element over a column** whose type has no GiST support of its own, meaning anything other
  than a range, multirange, geometric, full-text or extension type with that support;
- **for an expression element** compared with `=`, `<>` (or `!=`), `<`, `<=`, `>` or `>=`.

Temporal constraints share this, with the same `UseBtreeGist()` and
`SuppressTemporalExtensionAutoInjection()` switches.

### Two constraints on the same columns

Two declarations are the same constraint when they have the same elements in the same order **and**
the same filter. Operators don't count, so declaring a constraint again updates its operators. The
same elements with the same filter replace the earlier constraint. The same elements with a
*different* filter give you two constraints side by side, each over its own rows, which is the point
of the feature:

```csharp
b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                         filter: "revoked_at IS NULL",     name: "ex_grant_active");
b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                         filter: "revoked_at IS NOT NULL", name: "ex_grant_revoked");
```

Name both. The default name, `EX_{table}_{columns}`, comes from the elements alone, so the two would
get the same name in the database. When you remove a declaration, the next migration has a
`DROP CONSTRAINT` for it.

### Take over a constraint you wrote by hand

The migration runs `DROP CONSTRAINT IF EXISTS` before each `ADD CONSTRAINT`. So you can declare a
constraint that already exists in the database under the same name, for example from raw
`migrationBuilder.Sql(...)` in an earlier migration. The migration runs without an error on new and
existing databases alike, and you don't edit it by hand. Make sure the name you declare matches the
existing one.

> **Does a constraint show up again in every new migration?** `migrations add` compares your model
> with the *compiled* model snapshot, not with the `…ModelSnapshot.cs` file. Say a constraint is
> added again by every `dotnet ef migrations add`, although the snapshot file has its
> `CustomExclusion:Constraints` annotation. Then the compiled snapshot is out of date. Usually you
> ran `migrations add` with `--no-build`, or the migrations assembly (`MigrationsAssembly(...)`) was
> loaded from an old build. Rebuild the project that holds the snapshot, and run `migrations add`
> again.

### Reading constraints back

You can read exclusion constraints back from the model, mutable or finalized, as you can
[complex indexes](../README.md#reading-declarations-back). That lets you check a convention in your
application for constraints as well as indexes:

```csharp
var unfiltered = modelBuilder.Model.GetEntityTypes()
    .Where(IsWithdrawable)
    .SelectMany(e => e.GetExclusionConstraints())
    .Where(ex => ex.Filter is null);

var active = modelBuilder.Model.FindExclusionConstraint("ex_role_grant_active_period");
```

Each `ExclusionConstraintDeclaration` has:

- **The elements**, as property paths or expressions, each with its operator.
- **`Method`**, which is `gist` unless you set another.
- **`Filter` and the deferral settings.**
- **`Name`**, the name you gave it. It is null for a constraint with a default name, and
  `FindExclusionConstraint` doesn't find those.

`GetDeclaredExclusionConstraints()` leaves out inherited declarations, and lists each one on the
type that declares it. `migrations add` builds the constraint's SQL from the same reader.

### Amending constraints

On the mutable model, `AddExclusionConstraintFilter` adds a predicate to the filter of every
constraint you select, the way [`AddComplexIndexFilter`](../README.md#amending-declarations) does
for indexes:

```csharp
foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(IsWithdrawable))
    entityType.AddExclusionConstraintFilter("{RevokedAt} IS NULL");
```

- **An unfiltered constraint** gets the predicate as its filter.
- **A filtered constraint** gets `(existing) AND (predicate)`.
- **A constraint that already has the predicate** stays as it is.

Both methods also take a lambda, with the entity type given explicitly:
`AddComplexIndexFilter<TEntity>(x => x.RevokedAt == null)` and
`AddExclusionConstraintFilter<TEntity>(…)`.

The call changes only what is declared when it runs, so put it after the configurations.
