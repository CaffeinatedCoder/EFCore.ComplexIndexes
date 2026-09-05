# EFCore.ComplexIndexes.PostgreSQL — changelog

Changes to the PostgreSQL satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

## 5.2.0

- **Changed:** an index, exclusion constraint, temporal constraint or temporal foreign key whose
  name exceeds 63 bytes is rejected at `migrations add`. PostgreSQL would truncate it with a NOTICE
  and apply the migration cleanly, leaving the object under a name that no declaration and no
  constraint-violation error ever reports. Measured in UTF-8 bytes, as PostgreSQL does. Default
  temporal foreign key names, built from two table names, are the first to hit it: give the
  declaration a `name` — a name-only change renames the constraint in place.

## 5.1.0

- **Changed:** `UseNpgsqlComplexIndexes()` / `AddNpgsqlComplexIndexes()` also register the PostgreSQL
  differ at runtime, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes,
  expression indexes, and exclusion and temporal constraints, and `Migrate()`'s pending-model-changes
  check sees a declaration that was never scaffolded. Previously `EnsureCreated()` created the tables
  and silently none of them.
- **New:** whole-document JSON indexes. A `HasComplexIndex` selector ending at a `ToJson()` complex
  property or a complex collection indexes the `jsonb` container column — the idiomatic
  `USING gin (payload jsonb_path_ops)` — through the stock generator, no runtime wiring. Previously
  the path failed to resolve, and complex collections could not be indexed at all. A complex property
  nested inside the document resolves to a `->` extraction (an expression index).
- **New:** `HasStorageParameter(name, value)` — PostgreSQL storage parameters (`WITH (fillfactor=70)`)
  on complex and expression indexes, one call per parameter. Forwarded under the per-parameter
  `Npgsql:StorageParameter:` prefix, which the whitelist and the unknown-key rejection now both accept.
- **New:** `UseCollation(params string[])` — per-column index collations, positional (`UseCollation("C", "")`
  collates only the first column). Stored under Npgsql's model key and mapped to `Relational:Collation`
  on the operation, where Npgsql's generator reads it; a column's own collation is never copied onto
  the index.
- **Fixed:** SQL Server index options (`IsClustered`, `HasFillFactor`, …) on a complex index diffed by
  this satellite are rejected at `migrations add` — property-level and entity-level alike — instead of
  reaching Npgsql's generator, which ignored them.
- **Changed:** an exclusion constraint, temporal constraint or temporal foreign key declared on an
  entity type mapped to no table — typically the abstract base of a TPC hierarchy — fails at
  `migrations add` instead of producing nothing.

## 5.0.3

- **Changed:** the `Npgsql.EntityFrameworkCore.PostgreSQL` dependency is now `[10.0.0, 11.0.0)`. This
  differ extends Npgsql's own diff and generator internals, which carry no cross-major compatibility
  promise. Nothing changes if you are on Npgsql 10: NuGet resolves the lowest version in a range.
- **New:** the public API is fully documented, including the differ and the custom SQL generator.
- **Tests:** the consumer smoke test scaffolds a real migration from this package as installed from a
  NuGet feed, which is what verifies that the packaged `.targets` still registers the Npgsql differ.

## 5.0.2

- **Fixed:** temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys are rendered
  at design time and **no longer need `UseNpgsqlComplexIndexes()`**. Without that wiring the stock
  Npgsql generator emitted a plain `UNIQUE (key, period)` — valid DDL that applied cleanly and
  silently dropped the entire non-overlap guarantee. Migrations scaffolded before this change keep
  working.
- **Fixed:** exclusion-constraint identity now includes the filter. Two `EXCLUDE` constraints over
  the same columns with different predicates coexist instead of the second silently replacing the
  first — the filtered-overlap case the API exists for.
- **Fixed:** duplicate exclusion-constraint names are rejected. Because every `ADD CONSTRAINT` is
  preceded by `DROP CONSTRAINT IF EXISTS`, a reused name did not fail — the migration applied and
  the second constraint quietly replaced the first.
- **Fixed:** the design-time differ is scoped to the Npgsql provider, so a solution that also
  references another satellite can no longer hand a PostgreSQL model to the wrong differ.
- **Fixed:** `Npgsql:IndexSortOrder`/`IndexNullSortOrder` are no longer forwarded, and setting
  either now throws with a pointer to `DbOrder`. They duplicated what `DbOrder.Asc`/`Desc`/
  `NullsFirst`/`NullsLast` already express per column, so an index could carry two conflicting
  descriptions of its sort order with the annotation's half silently losing.
- **Fixed:** validation no longer inspects index operations this package did not create, so a plain
  native `HasIndex` carrying provider options is left alone.

## 5.0.1

- **Changed:** exclusion-constraint `ADD CONSTRAINT` DDL is preceded by `DROP CONSTRAINT IF EXISTS`,
  so adopting a pre-existing hand-written constraint of the same name applies cleanly instead of
  failing with `42P07`.
- **Fixed:** renaming a table no longer drops and recreates the exclusion and temporal constraints
  it carries.
- **Changed:** a name-only change to an exclusion constraint, temporal constraint, or temporal
  foreign key emits `ALTER TABLE … RENAME CONSTRAINT` instead of rebuilding. Dependent temporal
  foreign keys survive untouched.

## 5.0.0

- **New:** `HasExclusionConstraint` — `EXCLUDE` constraints with `WHERE` predicates.
- **New:** typed LINQ expression indexes — `HasExpressionIndex(x => x.Email.ToLower())`.
- **New:** JSON member indexes for `ToJson()` complex properties.
- **New:** `NULLS FIRST`/`NULLS LAST` via `DbOrder.NullsFirst/NullsLast` and
  `ExpressionIndexBuilder.NullsFirst()/NullsLast()`.
- **Fixed:** descending parts of expression indexes render `DESC`.
- **Changed:** `IncludeProperties(...)` entries resolve as property paths (complex members included)
  with verbatim column-name fallback.
- **Changed:** indexes requiring the custom generator carry a loud sentinel column, so a missing
  `UseNpgsqlComplexIndexes()` fails at apply time with an actionable error instead of applying a
  silently wrong index.
