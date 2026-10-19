# Delete guard: design

Design for ROADMAP.md D1 (section 4.2), written 2026-10-19 against `main` at 5.5.0 (fd28ded).
**Proposed**, for the maintainer's review. Nothing here is implemented.

The consumer case: an application had to hand-write `CREATE TRIGGER` in a migration to stop rows
in a final state from being deleted. Nothing in the model records that trigger, nothing diffs it,
`EnsureCreated()` never creates it, and the next developer who renames the column it reads finds
out at apply time.

The design has three constraints that pull against each other:

1. It can ship on `EFCore.ComplexIndexes` 5.x, the EF Core 10 line, if an EF Core 10 application
   needs it before it moves to EF Core 11.
2. It moves to `CodoMetis.EFCore.SchemaKit` (#37, API designed in #61) with **no migration**:
   the DDL, the annotation and the snapshot text a 5.x consumer already has are what SchemaKit
   reads and renders (#59).
3. In SchemaKit the same mechanism carries the roadmap's follow-ups (update guards, append-only
   tables, immutable columns, guards on SQL Server and SQLite) as additions, not rewrites.

Every PostgreSQL behaviour this document relies on was checked on PostgreSQL 14 and 18 on
2026-10-19 (section 10 lists the probes); "verified" below means that.

- [1. Summary](#1-summary)
- [2. ROADMAP 4.2, critically](#2-roadmap-42-critically)
- [3. Architecture](#3-architecture)
- [4. The error the application sees](#4-the-error-the-application-sees)
- [5. EnsureCreated and the other runtime paths](#5-ensurecreated-and-the-other-runtime-paths)
- [6. What is permanent](#6-what-is-permanent)
- [7. Transfer to SchemaKit](#7-transfer-to-schemakit)
- [8. Extensibility in SchemaKit](#8-extensibility-in-schemakit)
- [9. Effort, and whether 4.1 comes first](#9-effort-and-whether-41-comes-first)
- [10. Release placement](#10-release-placement)
- [11. Verified behaviour](#11-verified-behaviour)
- [12. Open questions for the maintainer](#12-open-questions-for-the-maintainer)

## 1. Summary

```csharp
builder.Entity<Invoice>()
    .HasDeleteGuard(x => x.FinalizedAt != null,
        name: "RC_Invoices_Finalized",
        message: "Finalized invoices cannot be deleted.");
```

```sql
DO $codometis$ BEGIN
    CREATE FUNCTION "billing"."codometis_row_constraint"() RETURNS trigger LANGUAGE plpgsql AS $body$
    BEGIN
        RAISE EXCEPTION USING
            ERRCODE    = 'check_violation',
            MESSAGE    = COALESCE(TG_ARGV[0], format('%s on table %I violates row constraint %I', TG_OP, TG_TABLE_NAME, TG_NAME)),
            CONSTRAINT = TG_NAME,
            TABLE      = TG_TABLE_NAME,
            SCHEMA     = TG_TABLE_SCHEMA;
    END $body$;
EXCEPTION WHEN duplicate_function THEN NULL;
END $codometis$;

DROP TRIGGER IF EXISTS "RC_Invoices_Finalized" ON "billing"."Invoices";
CREATE TRIGGER "RC_Invoices_Finalized" BEFORE DELETE ON "billing"."Invoices"
    FOR EACH ROW WHEN (OLD."FinalizedAt" IS NOT NULL)
    EXECUTE FUNCTION "billing"."codometis_row_constraint"('Finalized invoices cannot be deleted.');
```

- **Recommendation: build it, in a 5.6.0, not in 5.5.0.** 5.5.0 has to be on NuGet before EF Core
  11 ships in November; the guard is about the size of 5.2.0's typed-filter work.
- **4.1, the generic descriptor differ, is not a prerequisite on 5.x.** The guard is the fifth copy
  of a 120-line algorithm on a line that takes no further kinds; SchemaKit builds the generic
  differ anyway (#61, section 7).
- **ROADMAP 4.2 needs six corrections** before it is implemented (section 2): the rename it plans
  reports the old name, `CREATE OR REPLACE` breaks under a second migration role, a column type
  change under a guard fails at apply time, and the typed predicate lets rows with a null column
  through a guard their C# predicate covers (fixed for typed filters by #68, which guards then
  inherit). All four are verified.
- **Permanent from the first migration a consumer applies:** the function's name, signature and
  body, the trigger shape, the annotation key and JSON, the predicate rendering, the default name
  rule, and the SQLSTATE contract (section 6).

## 2. ROADMAP 4.2, critically

What 4.2 gets right and this design keeps: design-time `SqlOperation`s, so the statement cannot
degrade when `UseNpgsqlComplexIndexes()` is forgotten; a definition that diffs (event, predicate,
name, message) rather than a trigger body; one shared function; `ERRCODE = 'check_violation'` with
`CONSTRAINT`, `TABLE` and `SCHEMA` set, so Npgsql surfaces 23514 with `ConstraintName`; `DROP
TRIGGER IF EXISTS` before every `CREATE TRIGGER`, so adopting the consumer's hand-written trigger
applies cleanly; recording the trigger in EF's model (`HasTrigger`); and the read model as the
differ's reader.

What it gets wrong or leaves out, each with what this design does instead:

| # | 4.2 says | What happens | This design |
|---|---|---|---|
| 1 | A name-only change is `ALTER TRIGGER "old" ON t RENAME TO "new"`; the name is passed to the function as `TG_ARGV[0]` | The trigger is renamed, but its arguments are not: the guard keeps raising with `ConstraintName` = the **old** name. The application's error mapping, which dispatches on that name, silently stops matching. Verified: after the rename, `pg_get_triggerdef` still shows `('RC_Invoices_CreatedAt', …)` | The function reads the name from `TG_NAME`, which follows a rename. The only argument is the message. Rename stays a catalog update |
| 2 | `CREATE OR REPLACE FUNCTION` in every migration that adds a guard, "the way `btree_gist` is handled" | Fails with 42501 "must be owner of function" when the migration runs under a role other than the one that created the function (a deploy role per environment, an ownership hand-over). Verified. And `REPLACE` would let any later package version silently change the behaviour of every guard already in the database | Create if missing: `CREATE FUNCTION` inside a `DO` block that swallows `duplicate_function` (42723). The body is frozen; a different body would get a different name (`codometis_row_constraint_v2`), never a replace |
| 3 | Nothing about base operations on the columns a predicate reads | `ALTER COLUMN … TYPE` on a column used in a trigger's `WHEN` fails: "cannot alter type of a column used in a trigger definition" (verified). EF emits exactly that for a length or precision change. The migration fails at apply time, which is loud, but on every such change, and the consumer's only fix is hand-editing the migration | A guard whose resolved predicate reads a column that the base operations alter is dropped before them and created after them, even though its descriptor did not change (the temporal FK's forced rebuild, applied to guards) |
| 4 | The typed overload runs `TranslatePredicate`, "exactly as typed filters do" | Typed filters rendered SQL three-valued logic: `x.Status != "Draft"` became `"Status" <> 'Draft'`, which is NULL for a null status. In a trigger `WHEN`, NULL means *does not fire*: the row is deleted, although the C# predicate the developer wrote is true for it. Verified. For a guard that is the whole failure: the protection silently has a hole | The same defect in typed index filters is #68, fixed for 5.5.0 in PR #69: `TranslatePredicate` now writes comparisons with a nullable operand as EF Core does (`"Status" <> 'Draft' OR "Status" IS NULL`) and pushes negations down to them. Guards reuse it unchanged, so this needs no guard-specific code once #69 is merged; a guard test pins it |
| 5 | "Identifier length (63 bytes)" | PostgreSQL truncates a longer trigger name with only a NOTICE (verified). The next migration's `DROP TRIGGER IF EXISTS "<full name>"` finds nothing and the old trigger stays next to the new one | Kept, and moved to the top of the validations: a name over 63 UTF-8 bytes fails `migrations add`, through the existing `ThrowIfIdentifierTooLong` |
| 6 | Name uniqueness "per table through the generic differ" | Trigger names are unique per table in PostgreSQL, but the application sees the guard as a *constraint*: a guard and a CHECK constraint may share a name (verified), and then the application cannot tell which one fired | The guard's name is claimed in the table's **constraint** namespace too, in `ValidateNamesAcrossKinds`: it may not equal a check, unique, exclusion, temporal or foreign key constraint name on the same table, nor another guard |

Smaller points:

- **4.2 makes the message mandatory in the typed overload's example and optional nowhere.** Here it is
  optional; the function's default text names the operation, the table and the guard, in the shape
  of PostgreSQL's own check-violation message. A message change is drop-and-create, since trigger
  arguments cannot be altered.
- **Raw predicates.** In a trigger `WHEN` a bare column reference is an error; every column needs
  `OLD.`. The raw overload therefore takes the placeholder form only for columns
  (`"{FinalizedAt} IS NOT NULL"`), and a bare column fails at apply time ("missing FROM-clause
  entry"), loud. The declaration throws when a raw predicate contains no placeholder at all, since
  such a predicate is either constant or wrong.
- **Bypasses to document, not to fix:** `TRUNCATE` (row triggers do not fire; verified),
  `session_replication_role = replica` (verified; it is how `pg_restore` and logical replication
  run), `ALTER TABLE … DISABLE TRIGGER` by the table owner. A guard is protection against the
  application and its developers, not against the table owner.
- **Cascades** fire the guard (verified): a guarded child row blocks the `ON DELETE CASCADE` from its
  parent, and the error carries the child's table and guard name. 4.2 calls that intended; the
  documentation has to say it prominently, because it surprises.
- **Idempotent scripts.** `migrations script --idempotent` wraps each statement in
  `DO $EF$ … $EF$`. The function DDL therefore uses named dollar tags (`$codometis$`, `$body$`),
  never bare `$$`, so the nesting is always well-formed.
- **TPT and entity splitting.** A predicate must read only columns of the table the guard is on.
  A TPT derived type whose predicate reads a base-table column throws at `migrations add` with the
  table each column lives in. TPC abstract bases go through `ThrowIfDeclaredOnUnmappedType`, as
  every other kind does.

## 3. Architecture

### 3.1 Declaration (5.x)

PostgreSQL package, `NpgsqlDeleteGuardExtensions`, on `EntityTypeBuilder<TEntity>`:

```csharp
// Conditional: the name is required, because the application dispatches on it.
public EntityTypeBuilder<TEntity> HasDeleteGuard(
    Expression<Func<TEntity, bool>> when, string name, string? message = null);

// Raw predicate with {Property.Path} placeholders, the filter form.
public EntityTypeBuilder<TEntity> HasDeleteGuard(string when, string name, string? message = null);

// Unconditional: the name defaults to RC_{table}.
public EntityTypeBuilder<TEntity> HasDeleteGuard(string? name = null, string? message = null);
```

The method name follows SchemaKit's `pg.DeleteGuard` (#61, section 13) rather than 4.2's
`HasDeleteConstraint`; the API name does not reach the database, so this is the maintainer's call
(section 12), and nothing below depends on it.

Each call also records the trigger in EF's own model, the same as
`ToTable(t => t.HasTrigger(name))`. Verified: the snapshot generator emits
`b.ToTable("Invoices", null, t => t.HasTrigger("RC_Invoices_Finalized"))` and the snapshot round trip
diffs to nothing. Npgsql's update pipeline does not change its SQL for a declared trigger; SQL
Server's does (no `OUTPUT` without `INTO`), which is why SchemaKit's SQL Server guard needs it and
why recording it now keeps that port a rendering change.

### 3.2 Storage and identity

One JSON list per entity type, under **`CustomRowConstraint:Constraints`**, the shape every 5.x kind
uses (`CustomExclusion:Constraints`):

```json
[{"event":"delete","when":"{FinalizedAt} IS NOT NULL","name":"RC_Invoices_Finalized","message":"Finalized invoices cannot be deleted."}]
```

- `event`: `"delete"` only in 5.x. The field exists so update, truncate and immutable-column guards
  are new values, not a new format.
- `when`: the placeholder template, stored exactly as the translator produced it, or absent for an
  unconditional guard.
- `name`: always written, also for the defaulted `RC_{table}` form, so the stored name never depends
  on a rule a later version could change.
- `message`: absent when not given.
- Property order is fixed (`event`, `when`, `name`, `message`); absent means null, and every reader
  default equals "absent". (SchemaKit learned that rule from a `nullsDistinct: false` that read back
  as `true`.)

**Store identity** (what a re-declaration replaces): `(event, when)`. Two guards with different
predicates on one entity coexist; a second explicit use of a name on the entity throws at the
declaration, as for indexes and exclusion constraints.

**Diff identity** (what the differ compares): `(table, schema, event, resolved when, name, message,
rendering version)`. Equal but for the name → rename; anything else → drop and create.

### 3.3 Read model and mutable API

`RowConstraintDeclaration` (event, `When` as declared, `Name`, `Message`), with
`GetDeclaredRowConstraints()` on the entity type and `FindRowConstraint(name)` on the model, and
`AddDeleteGuard` on `IMutableEntityType`. The differ builds its descriptors from these readers, the
rule every kind follows since 5.2.0.

### 3.4 Differ integration

A fifth `Apply…` step in `NpgsqlComplexIndexMigrationsModelDiffer.GetDifferences`, after
`ApplyExclusionConstraints`, written as a copy of that method (section 9 explains why a copy):

1. Build source and target descriptors from the read model; resolve `when` through `ResolveFilter`
   with a new row-qualifier argument (`OLD.` for delete guards), which prefixes each resolved column
   and each JSON extraction's container column. Record the set of columns the resolved predicate
   reads.
2. Validate the target only: unmapped declaring type, identifier length (63 bytes), duplicate
   resolved name per table, predicate columns in another table (TPT), unresolvable placeholder.
3. Normalise source descriptors onto renamed tables (`BuildRenamedTables`).
4. Match. Equal but for the name → `ALTER TRIGGER "old" ON <new table> RENAME TO "new"`, placed after
   the base operations. Otherwise drop (before the base operations, `DROP TRIGGER IF EXISTS … ON
   <old table>`, skipped when the base operations drop the table) and create (after them).
5. **Forced rebuild:** an unchanged guard whose predicate reads a column that an
   `AlterColumnOperation` in the base operations touches is dropped before and created after
   (finding 3). A `RenameColumnOperation` needs nothing: the resolved predicate changes, so the
   descriptor does.
6. Emit the function DDL once per schema, before the first `CREATE TRIGGER` in that schema, in the
   same `SqlOperation` as that trigger. A table without a configured schema gets an unqualified
   function, resolved on `search_path` when the trigger is created; the trigger binds the
   function's OID, so a later `search_path` does not matter.
7. `ValidateNamesAcrossKinds` gains the guard: name claimed per table in the constraint namespace
   (finding 6). Guards own no index, so they claim nothing in the schema's relation namespace.

Placement relative to the other kinds: guard drops go first of all the package's drops (a guard
may read a column an exclusion constraint's rebuild also touches; dropping a trigger never
depends on anything), guard creates go last. That is the order `SchemaPlacement` would give a kind
with the highest ordinal in SchemaKit, so the transfer keeps the operation order too.

### 3.5 Design time, not runtime

The guard is a `SqlOperation` built at `migrations add`, exactly like exclusion and temporal
constraints. No `IMigrationsSqlGenerator` involvement, so a migration a 5.x consumer scaffolds
applies identically whether or not `UseNpgsqlComplexIndexes()` is wired, today and after the
swap to SchemaKit, whose generator renders nothing new (#61, section 7).

## 4. The error the application sees

| Path | Exception | Fields |
|---|---|---|
| `SaveChanges` / `SaveChangesAsync` | `DbUpdateException`, `InnerException` is `PostgresException` | `SqlState` = `"23514"` (`PostgresErrorCodes.CheckViolation`), `ConstraintName` = the guard's name (`TG_NAME`), `TableName`, `SchemaName`, `MessageText` = the message |
| `ExecuteDelete` / `ExecuteDeleteAsync` | `PostgresException` directly | same |
| Cascade from a parent | either of the above, raised for the parent's delete | the **child's** table and guard name |
| psql, raw SQL | `ERROR: 23514: <message>` | `SCHEMA NAME`, `TABLE NAME`, `CONSTRAINT NAME` (verified) |

The application dispatches on `(SqlState, ConstraintName)`, which is the same code path it already
has for CHECK constraints; that is why 23514 and not a custom SQLSTATE. The documentation shows
the one helper an application needs:

```csharp
catch (DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "RC_Invoices_Finalized" } })
```

A provider-neutral mapper (`TryGetRowConstraintViolation`, ROADMAP 6.4) belongs to SchemaKit, where
there is a second provider to be neutral about.

## 5. EnsureCreated and the other runtime paths

`EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`'s pending-changes check run the context's
own differ. With `UseNpgsqlComplexIndexes()` they see guards like every other kind; without it,
`EnsureCreated()` creates the tables and **no guard**, silently. That is the existing contract for
every feature of this package (README's wiring table), but a guard is the feature where it hurts
most: the consumer case is a test database that should refuse a delete and does not.

5.x keeps the contract and documents it in the wiring table ("needs runtime wiring for
`EnsureCreated`: yes"). It does not add a startup check: a runtime differ check would have to run
for every context, and the only thing it could report is "a declaration exists and no wiring is
present", which is #57's ownership check in SchemaKit, where it belongs. Recommended for the docs:
an integration-test assertion the consumer can copy, reading `pg_trigger` for the declared names.

## 6. What is permanent

Everything in this table lands in a consumer's database or snapshot with the first migration, and
SchemaKit must render or read it unchanged forever:

| Identifier or format | Value | Why permanent |
|---|---|---|
| Function name | `codometis_row_constraint` | Every trigger binds it; owner-branded, not product-branded (ROADMAP 5.1) |
| Function schema | the guarded table's schema, or unqualified | Triggers reference it by OID; a second schema gets its own copy |
| Signature | `() RETURNS trigger LANGUAGE plpgsql` | `CREATE FUNCTION` with another signature is another function |
| Body | `ERRCODE 'check_violation'`, `MESSAGE = COALESCE(TG_ARGV[0], <default>)`, `CONSTRAINT = TG_NAME`, `TABLE`, `SCHEMA` | Never replaced (finding 2); a change means a new function name |
| Default message | `format('%s on table %I violates row constraint %I', TG_OP, TG_TABLE_NAME, TG_NAME)` | Part of the body. `TG_OP` makes it correct for `UPDATE` and `TRUNCATE` guards too |
| Trigger arguments | `(message)` or none | Changing them is drop-and-create of every guard |
| Trigger shape | `BEFORE DELETE … FOR EACH ROW [WHEN (…)] EXECUTE FUNCTION …` | SchemaKit's diff compares resolved descriptors; another shape is a rebuild |
| Trigger name | the declared name; default `RC_{table}` for the unconditional form | Snapshot and database both hold it; the application dispatches on it |
| Predicate rendering | placeholders → `OLD."col"`, JSON members as the 5.4.0 rendering with `OLD.` on the container, C# null semantics as `TranslatePredicate` writes them since #68 (section 2, finding 4) | Resolved on both sides at diff time: a rendering change would never reach existing databases. Carried as a guard rendering version, so a future change is a deliberate rebuild (the `RenderingVersion` rule) |
| Annotation key | `CustomRowConstraint:Constraints` on the entity type | In every snapshot |
| JSON | `event`, `when`, `name`, `message`; fixed order; absent = null; `event` values `delete` (later `update`, `truncate`, `immutable`) | Read by SchemaKit's v1 reader forever |
| EF trigger metadata | `HasTrigger(<name>)` on the table | In the snapshot; SQL Server's update pipeline depends on it |
| Error contract | SQLSTATE 23514, `ConstraintName` = guard name | Applications are written against it |

Not permanent: the C# API names (SchemaKit has a new API by decision, #61), the read model's type
names, and the internal descriptor shape.

## 7. Transfer to SchemaKit

The move adds no migration when SchemaKit, for a model that declares the same guards:

1. **Reads** `CustomRowConstraint:Constraints` through its v1 reader into the guard declaration, the
   way #61 section 9 reads `CustomExclusion:Constraints`. One row in that table:
   `CustomRowConstraint:Constraints` → one guard declaration per element, `event` picks the case.
2. **Resolves** both sides to the same descriptor. The differ compares resolved declarations, not
   annotations, so the snapshot's v1 form and the model's v2 form resolve alike and produce no
   operations, which is #61's argument for every kind.
3. **Renders** a new guard with the identical DDL of section 6, so a guard added after the move
   looks like one added before it.
4. Keeps the `HasTrigger` registration under the same name.

#59 gets one test row: a 5.x model and snapshot with a conditional, an unconditional and a
JSON-member guard, swapped to SchemaKit, `migrations add` → no operations, on PostgreSQL 16 and 18.

**Why the v1 blob and not SchemaKit's v2 format from the start.** 5.x could write
`CodoMetis:Pg.RowConstraint:<hash>` directly, so SchemaKit would need no v1 reader for guards. That
fixes the v2 key format, the owner string and the hashed identity now, while #61 is a draft under
review, and a later change to any of them would make the 5.x form a legacy format anyway. The v1 blob
is what every other 5.x kind writes, and SchemaKit already promises to read 5.x forever: one more row
costs less than freezing an unreviewed format. If #61 is merged before the guard is built, revisit.

## 8. Extensibility in SchemaKit

### 8.1 One union, one function

Every follow-up the roadmap lists is a trigger that raises when a condition on the affected row (or
statement) holds. The raise-only function covers all of them unchanged, because it takes the name
from `TG_NAME`, the operation from `TG_OP`, and the condition lives in the trigger's `WHEN`:

| Follow-up | Trigger | Verified |
|---|---|---|
| Delete guard | `BEFORE DELETE FOR EACH ROW WHEN (p(OLD))` | yes |
| Update guard | `BEFORE UPDATE FOR EACH ROW WHEN (p(OLD))` | shape as delete |
| Immutable columns | `BEFORE UPDATE FOR EACH ROW WHEN (OLD.a IS DISTINCT FROM NEW.a OR …)` | yes |
| Truncate guard | `BEFORE TRUNCATE FOR EACH STATEMENT` | yes |
| Append-only | delete guard + update guard + truncate guard, three triggers, three names | yes, per part |
| Transition guard | `BEFORE UPDATE … WHEN (p(OLD, NEW))` | needs the two-root translator |

So in SchemaKit the guard kind is one closed union and one contributor:

```csharp
public closed record RowGuard : ISchemaObject, ISchemaDeclaration<RowGuard>
{
    public sealed record OnDelete(Predicate? When)                 : RowGuard;   // event "delete"
    public sealed record OnUpdate(Predicate? When)                 : RowGuard;   // event "update"
    public sealed record OnTruncate()                              : RowGuard;   // event "truncate"
    public sealed record Immutable(IReadOnlyList<string> Paths)    : RowGuard;   // event "immutable"
    // Name, Message, Identity, ToAnnotationValue, FromAnnotationValue …
}
```

- **Additions, not rewrites.** A new case is a new `event` value in the same JSON, a new arm in the
  contributor's own switches (a compile error until written, P9 of #61), and nothing else: the
  function, the storage, the differ step, the name registry claims and the error contract are shared.
  Append-only is a factory that returns three values, not a case.
- **Closed inside, open at the seam (#56).** `RowGuard` is the contributor's closed union; the kernel
  sees only `ISchemaObject` and routes by owner. A third-party package that needs a guard of its own
  semantics (a CodoMetis.Hierarchies path check, say) does not add a case. It writes its own
  contributor and may reuse the published function by name, which is why the function's contract is
  part of section 6.
- **Two-root predicates.** Immutable columns and transition guards need `OLD` and `NEW`: the
  translator's row qualifier becomes a per-parameter qualifier (`(old, @new) => …`), which the
  kernel translator in #61 can take as one more `Predicate` form.

### 8.2 Providers

The declaration is provider-neutral (event, predicate, name, message); the rendering is not:

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Trigger | `BEFORE … FOR EACH ROW WHEN` | `AFTER DELETE`, set-based over `deleted`, `THROW 50001` | `BEFORE DELETE … WHEN … BEGIN SELECT RAISE(ABORT, …); END` |
| Name in the error | `ConstraintName` | in the message text | in the message text |
| Truncate | `BEFORE TRUNCATE` | not expressible (`TRUNCATE` fires no trigger) | not applicable |
| `HasTrigger` needed | no | yes (no `OUTPUT` without `INTO`) | yes (no `RETURNING`) |
| Cascades | fire the guard | fire `AFTER` triggers | fire triggers |

That shape suggests: `RowGuard` lives in the **kernel** as the stored and diffed value; each
provider package contributes the renderer through its dialect; the **factories** stay per provider
(`pg.DeleteGuard`, `ss.DeleteGuard`, `sqlite.DeleteGuard`) and offer only what that provider can
render, so `ss.TruncateGuard` does not exist and a SQL Server append-only declaration cannot
compile with a truncate part (#61's P4 at the factory level). One stored format means a model
that runs PostgreSQL in production and SQLite in tests declares a guard once and gets it in both,
which is the reason ROADMAP 6.3 gives for the SQLite package.

Whether the owner is kernel-level (`RowConstraint`) or PostgreSQL-scoped (`Pg.RowConstraint`, as
#61's section 9 lists it) is the one alignment question left with #61. 5.x does not depend on the
answer: its annotation is the v1 blob either way.

## 9. Effort, and whether 4.1 comes first

Measured against what exists: exclusion constraints are 549 lines of declaration, storage and read
model plus about 280 lines of differ, and 5.2.0 (typed filters) was 1,258 source and 1,121 test
lines.

| Part | Files | Size |
|---|---|---|
| Declaration, definition, JSON storage, read model, mutable API | `NpgsqlDeleteGuardExtensions.cs`, `RowConstraintDefinition.cs`, `RowConstraintDeclaration.cs`, `NpgsqlRowConstraintModelExtensions.cs`, `NpgsqlRowConstraintMutableExtensions.cs`, `NpgsqlRowConstraintAnnotations.cs` | ~450 lines |
| Differ step, forced rebuild, function DDL, name claims | `NpgsqlComplexIndexMigrationsModelDiffer.cs` | ~300 lines |
| Row qualifier in `ResolveFilter` | `CustomMigrationsModelDiffer.cs` (core) | ~30 lines; makes it a core minor too |
| C# null semantics for guard predicates | none: inherited from `TranslatePredicate` after #68 | a test |
| Tests | differ (create, drop, rename, message change, no churn, forced rebuild, ordering), snapshot round trip with a compiled snapshot, read model, every validation with `verify-the-guard`, translator null semantics, integration on 16 and 18 (23514 and `ConstraintName` via SaveChanges and ExecuteDelete, cascade, rename keeps the name, ALTER COLUMN TYPE, idempotent script, second role), consumer smoke test assertion | ~1,100 lines, ~60 tests |
| Docs | `docs/postgresql-guards.md`, README feature bullet and wiring-table row, root and package changelogs, CLAUDE.md paragraph, `migration-safety-review` checklist item | ~250 lines |

About the size of 5.2.0, so one minor's work, with a review.

**Is 4.1 a prerequisite?** Not on 5.x. 4.1 replaces four copies of the descriptor diff with one
generic differ; the guard would be the fifth copy. On a line that takes fixes only after the guard,
there is no sixth kind for the generic differ to pay off on, and the refactor's risk lands on
existing kinds that LTS consumers rely on: every regression it could cause is in exclusion,
temporal or index code that works today. SchemaKit builds the generic differ as its kernel (#61,
section 7), where every kind is new code anyway. The copy costs about 120 lines and is the same
algorithm `ApplyExclusionConstraints` already proves; `OperationOrderingTests` gets one
characterisation test that declares all five kinds on one model, so the copy's placement is pinned.

## 10. Release placement

**Recommendation: 5.6.0, not 5.5.0.**

- 5.5.0 is the EF Core major guard (#55), and it has to be on NuGet before EF Core 11's GA in
  November, or EF Core 11 users install a package that runs on an EF Core it never saw. The delete
  guard is a minor's worth of work plus review; adding it to 5.5.0 delays the one release with a
  date.
- 5.6.0 ships only if an EF Core 10 application needs the guard before it moves to EF Core 11. The
  consumer case is real today, so the default is to build it; if that application moves to EF Core
  11 in the SchemaKit 11.0.0 timeframe instead, the guard goes straight to SchemaKit and 5.6.0 never
  exists. That is the maintainer's call (section 12).

**What the 5.x rule has to say to allow it.** Today the rule (#36, CONTRIBUTING.md) is "after 5.5.0,
`release/5.x` takes fixes only", and CONTRIBUTING.md's general rule is that additions reach `main`
first so the LTS line never carries something the current line lacks. A 5.6.0 guard breaks both, so
the rule needs one exception, stated narrowly:

> After 5.5.0, `release/5.x` takes fixes only, with one exception: a feature whose DDL, database
> objects and annotation format are fixed in a design document SchemaKit has accepted, so that
> SchemaKit renders and reads them unchanged. It ships as a 5.x minor, its SchemaKit counterpart
> gets a #59 test row before SchemaKit 11.0.0, and it lands on `release/5.x` directly, since `main`
> no longer has the 5.x API.

The delete guard is the only candidate today. The rule keeps 5.x from becoming a second feature line
while letting one consumer-driven feature through without stranding the consumer on SchemaKit's
timeline.

Tracking: an epic `5.6.0` with milestone `5.6.0` under the Roadmap (#35), holding this design's
issue and the implementation issue. Proposed, not created.

## 11. Verified behaviour

On `postgres:14` and `postgres:18`, 2026-10-19, with the DDL of section 1 (function in schema
`billing`, table with a cascading foreign key):

- Deleting a guarded row: `ERROR 23514`, `SCHEMA NAME billing`, `TABLE NAME Invoices`, `CONSTRAINT
  NAME RC_Invoices_Finalized`, the message from the argument. Unguarded rows delete.
- Cascade from the parent into a guarded child: the same error, raised inside the RI trigger's
  `DELETE`.
- `TRUNCATE … CASCADE` deletes guarded rows; `session_replication_role = replica` lets a guarded
  delete through.
- `BEFORE TRUNCATE FOR EACH STATEMENT` and `BEFORE UPDATE … WHEN (OLD.c IS DISTINCT FROM NEW.c)`
  through the same function raise with their own names.
- `ALTER TRIGGER … RENAME` keeps the trigger's arguments: a name passed as an argument goes stale.
- `CREATE OR REPLACE FUNCTION` by a second role: `42501 must be owner of function`.
- A trigger name of 75 characters is truncated to 63 with a NOTICE.
- A CHECK constraint may take a guard's name on the same table.
- `WHEN (OLD."Status" <> 'Draft')` lets a row with a null status be deleted.
- `ALTER COLUMN … TYPE` on a column in a trigger's `WHEN`: "cannot alter type of a column used in
  a trigger definition". `RENAME COLUMN`, `RENAME TO` and `SET SCHEMA` on the table are followed
  by the trigger; `DROP COLUMN` is refused while the trigger reads it.

And in EF Core 10 (the repository's `SnapshotRoundTripTests` harness): `HasTrigger` round-trips
through a compiled snapshot as `t.HasTrigger("…")`, together with a `CustomRowConstraint:Constraints`
annotation, with no operations.

## 12. Open questions for the maintainer

1. **Build it for 5.x at all?** Yes if the consuming application stays on EF Core 10 past
   SchemaKit 11.0.0; otherwise straight to SchemaKit.
2. **The 5.x rule's exception** in section 10: agreed wording, and the `5.6.0` epic?
3. **API name:** `HasDeleteGuard` (aligned with SchemaKit's `pg.DeleteGuard`) or 4.2's
   `HasDeleteConstraint` (aligned with how the error surfaces)?
4. **Owner in SchemaKit:** kernel `RowConstraint` with per-provider factories (section 8.2), or
   `Pg.RowConstraint`? To settle with #61.
5. **Typed index filters had finding 4 too.** That is #68, fixed for 5.5.0 by PR #69 with EF Core's
   own null compensation (so the index stays usable for EF's query of the same lambda). The guard
   design assumes it is merged.
