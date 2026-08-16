# PostgreSQL — temporal and exclusion constraints

Provided by the **EFCore.ComplexIndexes.PostgreSQL** package. None of these features need runtime
wiring: the DDL is rendered at design time into the migration itself.

For index methods, expression indexes and JSON member indexes, see
[PostgreSQL — indexes](postgresql-indexes.md).

## Temporal `UNIQUE` constraints (`WITHOUT OVERLAPS`) — requires PostgreSQL 18

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

### How the period column is validated

The migration differ validates the period property at migration-generation time (`dotnet ef migrations add`). It must be mapped to a PostgreSQL range or multirange store type (anything ending in `range` — e.g. `daterange`, `tstzrange`, `int4multirange`) or have a CLR type of `NpgsqlRange<T>` / a multirange struct from `NpgsqlTypes`. Using an incompatible type such as `string`, `int`, or `DateOnly` throws an `InvalidOperationException` *before* any SQL is generated:

```
The temporal constraint period property 'Start' on entity 'Booking' does not appear to be a range or multirange type. Found CLR type 'DateTime' (store type: 'timestamp with time zone'). Expected NpgsqlRange<T>, a PostgreSQL range/multirange column type, or a store type ending in 'range' (e.g., daterange, int4multirange).
```

The period column stays a plain mapped column — it is deliberately **not** part of an EF key, because EF Core forbids non-comparable range types in primary keys. Use a surrogate or scalar EF primary key for change tracking; the temporal constraint handles the non-overlap guarantee independently.

### `btree_gist` extension

Temporal constraints over scalar key columns require the `btree_gist` PostgreSQL extension. The differ injects `CREATE EXTENSION IF NOT EXISTS btree_gist;` automatically when a temporal constraint is first added. You can take explicit control or opt out:

```csharp
// Explicit: declare the extension yourself (Npgsql's own differ handles it)
modelBuilder.UseBtreeGist();

// Opt out: e.g. if the extension is provisioned out-of-band by your DBA
modelBuilder.SuppressTemporalExtensionAutoInjection();
```

When `UseBtreeGist()` is present, automatic injection backs off to avoid a duplicate `CREATE EXTENSION` statement.

### Idempotency and renames

Re-declaring a temporal constraint on the same key + period replaces the previous one. Removing `HasTemporalConstraint` from the model causes the differ to emit a `DROP CONSTRAINT` in the next migration (unless the table itself is being dropped).

A change that only affects the **name** — whether you pass a new `name:` or rename the table, which
changes the default-derived name — emits `ALTER TABLE … RENAME CONSTRAINT` rather than dropping and
rebuilding the constraint, so dependent temporal foreign keys survive untouched.

## Temporal foreign keys (`PERIOD`) — requires PostgreSQL 18

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

### Restrictions and validation

- PostgreSQL 18+ only.
- Period columns must be PostgreSQL range or multirange columns (`daterange`, `tstzrange`, `NpgsqlRange<T>`, etc.).
- The referenced principal columns must have a matching `HasTemporalConstraint` in the model. PostgreSQL requires a referenced temporal `UNIQUE`/`PRIMARY KEY` constraint with `WITHOUT OVERLAPS`.
- Temporal foreign keys emit `NO ACTION` referential actions. PostgreSQL does not support temporal FK `CASCADE`, `RESTRICT`, `SET NULL`, or `SET DEFAULT` actions.
- This API emits standalone database constraints; it does not try to model the temporal relationship as an EF navigation/relationship key.

The standalone design is intentional. The period column remains a normal mapped property, not an EF key member. EF keys require key values suitable for change tracking, while Npgsql range values are not suitable EF key members; PostgreSQL enforces the temporal relationship independently at the database level.

## Exclusion constraints (`EXCLUDE`)

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
