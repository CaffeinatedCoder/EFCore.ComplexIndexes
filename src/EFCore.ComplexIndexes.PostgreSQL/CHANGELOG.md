# EFCore.ComplexIndexes.PostgreSQL — changelog

Changes to the PostgreSQL satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

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
