# Changelog

All releases of the three packages, newest first. Each package also carries its own changelog,
covering only what changed for that package:
[core](src/EFCore.ComplexIndexes/CHANGELOG.md),
[PostgreSQL](src/EFCore.ComplexIndexes.PostgreSQL/CHANGELOG.md),
[SQL Server](src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md).

## 5.0.3

A packaging and documentation release. No behaviour changes to the differ or the generated SQL.

- **Changed:** the EF Core dependency now declares an exclusive upper bound — `[10.0.0, 11.0.0)` on `Microsoft.EntityFrameworkCore.Abstractions` for the core package, and on the provider package for each satellite. This package subclasses `MigrationsModelDiffer` and calls internals EF marks as changeable without notice in any release, so an open-ended `>= 10.0.0` let NuGet resolve a future major where the differ can break — surfacing as a confusing `dotnet ef` failure in your project rather than anywhere visible from here. **Nothing changes for existing consumers:** NuGet resolves the lowest version in a range, so restore still picks 10.0.0. Adopting EF Core 11 will need a release that lifts the ceiling deliberately, once the differ has been tested against it.
- **New:** the public API is now fully documented, so IntelliSense no longer comes up empty on the fluent API, the annotation keys, `CompositeIndexDefinition`, or `IndexPartDefinition`. The shipped `.xml` had 64 holes in it; `TreatWarningsAsErrors` now keeps it complete.
- **Tests:** a consumer smoke test runs on every PR and on release. It packs the packages, installs them into a throwaway project created outside this repository, and runs a real `dotnet ef migrations add` — then asserts on the scaffolded content, because the failure it guards against is a migration that succeeds while silently omitting every index. Nothing previously exercised the delivery chain end to end: NuGet restore, the packaged `.targets` injecting the design-time attribute, EF's host discovering it, and the right differ winning.

## 5.0.2

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

## 5.0.1

- **Changed:** exclusion-constraint `ADD CONSTRAINT` DDL is now preceded by `DROP CONSTRAINT IF EXISTS`, so adopting a pre-existing hand-written constraint of the same name applies cleanly instead of failing with `42P07`. The standalone drop path also uses `IF EXISTS`.
- **Fixed:** renaming a table no longer drops and recreates the exclusion and temporal constraints it carries (the same normalization complex indexes already had).
- **Changed:** a name-only change to an exclusion constraint, temporal constraint, or temporal foreign key — including the implicit one when a table rename changes a default-derived name — now emits `ALTER TABLE … RENAME CONSTRAINT` instead of dropping and rebuilding. Dependent temporal foreign keys survive such renames untouched.
- **Tests:** the differ is now exercised against *real* model snapshots — generated as C#, compiled in-memory, and rebuilt exactly as `dotnet ef migrations add` does — guarding the whole feature set against snapshot round-trip churn.

## 5.0.0

- **Fixed:** custom `DROP INDEX` operations are now ordered *before* the base migration operations. Previously, moving an index between a native `HasIndex` and a complex-index declaration scaffolded a migration that created the new index before dropping the same-named old one — colliding at apply time.
- **Fixed:** descending parts of expression indexes now render `DESC` (declarable via `ExpressionIndexBuilder.Descending()`).
- **Fixed:** integral provider-annotation values (e.g. fill factor) survive snapshot round-trips as `int` instead of degrading to `double`, which made generators drop them.
- **Changed:** property annotations are forwarded onto index operations through a provider **whitelist** instead of a blacklist. Column facets such as `Relational:ColumnName` no longer leak into scaffolded migrations, and the class of phantom drop/create churn caused by snapshot/code-model annotation asymmetries is closed for good.
- **Changed:** an indexed property that resolves to no column now throws at `migrations add` instead of silently dropping the index — unless it is a `ToJson()` member, which now resolves to a JSON expression index (PostgreSQL).
- **Changed:** two indexes over the same columns may now coexist when their filters differ (both must be named); re-declaring with the same filter still updates in place.
- **New:** entity-level `HasComplexIndex(x => x.Complex.Prop, …)` for single-column indexes, enabling multiple filtered indexes per column.
- **New:** `HasExclusionConstraint` — `EXCLUDE` constraints with `WHERE` predicates.
- **New:** typed LINQ expression indexes — `HasExpressionIndex(x => x.Email.ToLower())`.
- **New:** JSON member indexes for `ToJson()` complex properties.
- **New:** `NULLS FIRST`/`NULLS LAST` via `DbOrder.NullsFirst/NullsLast` and `ExpressionIndexBuilder.NullsFirst()/NullsLast()` (PostgreSQL).
- **New:** the **EFCore.ComplexIndexes.SqlServer** satellite — clustered, covering, online, fill-factor, and sort-in-tempdb options.
- **Changed:** `IncludeProperties(...)` entries are now resolved as property paths (complex members included) with verbatim column-name fallback — `IncludeProperties("Email.Value")` finds the real column.
- **Changed:** a name-only index change now emits `RenameIndexOperation` (PostgreSQL, SQL Server) instead of dropping and rebuilding the index; the core default remains drop + create for providers that cannot rename standalone.
- **Changed:** renaming a table no longer drops and recreates the complex indexes it carries.
- **Changed:** indexes requiring the custom PostgreSQL generator carry a loud sentinel column, so a missing `UseNpgsqlComplexIndexes()` fails at apply time with an actionable error instead of applying a silently wrong index.
