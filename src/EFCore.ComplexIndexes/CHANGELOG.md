# EFCore.ComplexIndexes — changelog

Changes to the core package, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

## 5.3.0

- **Fixed:** a converter-member path (`x => x.Email.Value`) resolves against a model snapshot. The
  snapshot persists the property as its provider type on a property-bag type and drops the
  converter, so every diff whose source was the snapshot — the next `migrations add`,
  `has-pending-model-changes`, `Migrate()`'s pending-model-changes check — threw
  `Could not resolve property path 'Email.Value'`; only the first `migrations add` succeeded. On a
  property-bag type the persisted scalar is accepted for a path the configured model already
  validated; against a configured model the provider-type check is unchanged.

## 5.2.0

- **Changed:** a complex index name longer than the provider's identifier limit
  (`GetMaxIdentifierLength`, 63 on PostgreSQL, 128 on SQL Server) is rejected at `migrations add`.
  Default names are checked too — this package never truncates them, unlike EF Core's own — and a
  provider without a limit is left alone.

- **New:** `GetComplexIndexes()` / `GetDeclaredComplexIndexes()` on `IReadOnlyEntityType`,
  `GetComplexIndexes()` and `FindComplexIndex(name)` on `IReadOnlyModel` — the declarations read
  back as `ComplexIndexDeclaration`s (parts as property paths, `IsUnique`, `Filter`, explicit `Name`,
  provider options of entity-level declarations), from the mutable model in `OnModelCreating` too.
  The differ reads the model through the same code.

- **New:** filters resolve `{Property.Path}` placeholders to the mapped column at `migrations add`,
  quoted through the new `QuoteIdentifier` seam (ANSI by default); the resolved text is baked into
  the migration and compared against the snapshot. Only a dotted identifier path in braces outside
  a single-quoted literal is a placeholder; an unknown one throws. Template resolution moved into
  the core differ on the same seam.
- **New:** a property path may end at a member of a converter-mapped value object
  (`x => x.Email.Value`), which resolves to the converted column when the member's type is the
  converter's provider type.

- **New:** `AddComplexIndexFilter(predicate, where)` and `AddComplexIndex(definition)` on
  `IMutableEntityType` — install a filter on every selected declaration (AND-ed onto an existing
  one, idempotent) or add a declaration with the fluent API's identity rules, from `OnModelCreating`.

## 5.1.0

- **Fixed:** `HasDifferences` now reports changes to complex indexes (and, through the satellites'
  overrides, exclusion and temporal constraints). EF Core's base implementation bypasses
  `GetDifferences`, so `dotnet ef migrations has-pending-model-changes`, the pending-model-changes
  warning `Migrate()` raises, and the snapshot check in `migrations remove` all reported "no changes"
  when only a declaration from this package had changed.
- **New:** `UseComplexIndexes()` / `AddComplexIndexes()` register the differ at runtime, for providers
  without a satellite package. `EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`'s
  pending-model-changes check run the runtime differ, which the design-time wiring never reaches —
  without this, `EnsureCreated()` created the tables and silently none of the complex indexes.
- **Fixed:** a complex index named like a native `HasIndex` on the same table is rejected at
  `migrations add` instead of scaffolding two `CREATE INDEX` statements under one name that fail when
  applied. An index moving between a native and a complex declaration under one name still diffs.
- **New:** the property-level `HasComplexIndex` overloads also exist on the non-generic
  `ComplexTypePropertyBuilder` (`c.Property("Value").HasComplexIndex()`).
- **Changed:** a complex index declared on an entity type mapped to no table — typically the abstract
  base of a TPC hierarchy — fails at `migrations add` instead of producing nothing. View-mapped and
  query-mapped types are still skipped.

## 5.0.3

- **Changed:** the `Microsoft.EntityFrameworkCore.Abstractions` dependency is now `[10.0.0, 11.0.0)`.
  This package subclasses `MigrationsModelDiffer` and calls internals EF marks as changeable without
  notice in any release, so an open-ended floor let NuGet resolve a future major where the differ can
  break — in your `dotnet ef` run, not anywhere visible from here. Nothing changes if you are on EF
  Core 10: NuGet resolves the lowest version in a range, so restore still picks 10.0.0.
- **New:** the public API is fully documented. The shipped `.xml` had 64 gaps, so IntelliSense came up
  empty on parts of the fluent API, the annotation keys, `CompositeIndexDefinition` and
  `IndexPartDefinition`.
- **Tests:** a consumer smoke test packs the packages, installs them into a throwaway project outside
  the repository, and runs a real `dotnet ef migrations add`, asserting on the scaffolded content —
  the delivery chain (restore, `.targets` injection, design-time discovery, differ selection) was
  previously only ever verified in pieces.

## 5.0.2

- **Fixed:** the design-time migration differ is now selected deterministically when a provider
  satellite is installed. This package's design-time attribute rides along transitively next to the
  satellite's, and EF Core resolves last-registration-wins, so NuGet's restore order decided which
  differ ran — and this one winning silently drops every provider-specific feature.
- **Fixed:** duplicate index names are rejected instead of producing a migration that fails at apply
  time. Reusing an explicit name throws at the declaration; collisions between default names —
  including a property-level and an entity-level index over the same column — throw during
  `dotnet ef migrations add`.
- **Fixed:** selectors that read a captured variable or static member instead of the lambda
  parameter (`x => captured.Name`) throw at the declaration, naming the offending selector.
  Previously they produced an unmatchable property path that failed much later with an opaque
  resolution error.
- **Fixed:** `DbOrder.Asc` now marks a column ascending, and combining it with `DbOrder.Desc` (or
  `NullsFirst` with `NullsLast`) throws rather than silently picking one. Repeating a marker is fine.
- **Fixed:** provider validation runs through a scoped extension point instead of sweeping the
  finished operation list, so satellites can no longer reject index operations this package did not
  create.
- **Fixed:** array-valued provider annotations (operator classes, `INCLUDE` lists) compare by
  content rather than by reference in `CompositeIndexDefinition`.

## 5.0.1

- **Tests:** the differ is now exercised against *real* model snapshots — generated as C#, compiled
  in-memory, and rebuilt exactly as `dotnet ef migrations add` does — guarding the whole feature set
  against snapshot round-trip churn.

## 5.0.0

- **Fixed:** custom `DROP INDEX` operations are ordered *before* the base migration operations.
  Moving an index between a native `HasIndex` and a complex-index declaration previously scaffolded
  a migration that created the new index before dropping the same-named old one.
- **Fixed:** integral provider-annotation values (e.g. fill factor) survive snapshot round-trips as
  `int` instead of degrading to `double`, which made generators drop them.
- **Changed:** property annotations reach index operations through a provider **whitelist** instead
  of a blacklist. Column facets such as `Relational:ColumnName` no longer leak into scaffolded
  migrations, closing a class of phantom drop/create churn.
- **Changed:** an indexed property that resolves to no column throws at `migrations add` instead of
  silently dropping the index.
- **Changed:** two indexes over the same columns may coexist when their filters differ (both must be
  named); re-declaring with the same filter updates in place.
- **Changed:** a name-only index change emits `RenameIndexOperation` on providers that can rename
  standalone; renaming a table no longer drops and recreates the complex indexes it carries.
- **New:** entity-level `HasComplexIndex(x => x.Complex.Prop, …)` for single-column indexes.
- **New:** per-column `ASC`/`DESC` via `DbOrder.Asc`/`DbOrder.Desc`.
